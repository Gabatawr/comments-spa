using Comments.Application.Abstractions;
using Comments.Application.Abstractions.Caching;
using Comments.Application.Abstractions.Messaging;
using Comments.Application.Abstractions.Persistence;
using Comments.Application.Abstractions.Search;
using Comments.Application.Abstractions.Storage;
using Comments.Application.Services;
using Comments.Infrastructure.Caching;
using Comments.Infrastructure.Captcha;
using Comments.Infrastructure.Messaging;
using Comments.Infrastructure.Persistence;
using Comments.Application.Abstractions.Providers;
using Comments.Infrastructure.Providers;
using Comments.Infrastructure.Search;
using Comments.Infrastructure.Services;
using Comments.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using StackExchange.Redis;

namespace Comments.Infrastructure;

/// <summary>
/// Infrastructure composition (docs/ARCHITECTURE-v2.md §3, §4).
///
/// Every external dependency is an adapter behind a port, chosen from configuration through
/// <see cref="ActiveProviders"/>. By default a missing Redis/RabbitMQ/Elasticsearch host never
/// crashes startup (it degrades and health reports <c>error</c>);
/// <c>Providers:FailFastOnUnavailable=true</c> flips that to a startup failure, which is what a
/// managed-cloud deployment usually wants — silent degradation in production is worse than a
/// container that refuses to start.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCommentsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ------------------------------------------------------------------ switchboard
        // Resolved once and shared with the startup banner and GET /api/info, so the adapters that
        // are wired and the providers that get reported can never disagree.
        var providers = ProviderResolver.Resolve(configuration);
        services.AddSingleton(providers);
        services.AddHostedService<ProviderBanner>();

        var connectionString = configuration.GetConnectionString("Default")
                               ?? configuration["ConnectionStrings:Default"]
                               ?? "Host=postgres;Database=comments;Username=comments;Password=comments";

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure()));

        services.AddScoped<ICommentRepository, CommentRepository>();
        services.AddScoped<IAttachmentRepository, AttachmentRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IDatabaseHealthCheck, DatabaseHealthCheck>();
        services.AddScoped<ICommentSeeder, CommentSeeder>();

        services.AddMemoryCache();

        // ------------------------------------------------------------------ storage
        // NOTE: this switch and ProviderCatalog are the two places that know provider names; the
        // catalog is the source of truth for what is selectable, this switch for what a name wires
        // up. A name added to the catalog without a case here fails loudly at startup (`default`).
        services.Configure<AttachmentStorageOptions>(o =>
        {
            o.Provider = providers.Storage;
            o.Root = configuration["Storage:Root"] ?? "storage";
        });
        services.Configure<S3StorageOptions>(o =>
        {
            o.ServiceUrl = Str(configuration, "Storage:S3:ServiceUrl");
            o.Bucket = Str(configuration, "Storage:S3:Bucket");
            o.Prefix = Str(configuration, "Storage:S3:Prefix");
            o.Region = Str(configuration, "Storage:S3:Region") ?? o.Region;
            o.UseHttp = Flag(configuration["Storage:S3:UseHttp"], fallback: false);
            o.AccessKey = Str(configuration, "Storage:S3:AccessKey");
            o.SecretKey = Str(configuration, "Storage:S3:SecretKey");
            o.TimeoutSeconds = Number(configuration["Storage:S3:TimeoutSeconds"], o.TimeoutSeconds);

            // MinIO and most self-hosted gateways need path-style addressing; AWS does not.
            // An explicitly configured value always wins, including an explicit "false".
            var pathStyle = configuration["Storage:S3:ForcePathStyle"];
            o.ForcePathStyle = string.IsNullOrWhiteSpace(pathStyle)
                ? !string.IsNullOrWhiteSpace(o.ServiceUrl)
                : Flag(pathStyle, fallback: true);
        });
        services.Configure<AzureBlobStorageOptions>(o =>
        {
            o.ConnectionString = Str(configuration, "Storage:AzureBlob:ConnectionString");
            o.AccountUrl = Str(configuration, "Storage:AzureBlob:AccountUrl");
            o.SasToken = Str(configuration, "Storage:AzureBlob:SasToken");
            o.Container = Str(configuration, "Storage:AzureBlob:Container");
            o.Prefix = Str(configuration, "Storage:AzureBlob:Prefix");
            o.CreateContainerIfMissing = Flag(configuration["Storage:AzureBlob:CreateContainerIfMissing"], fallback: true);
            o.TimeoutSeconds = Number(configuration["Storage:AzureBlob:TimeoutSeconds"], o.TimeoutSeconds);
        });

        switch (providers.Storage)
        {
            case "filesystem":
                services.AddSingleton<IFileStorage, FileSystemStorage>();
                break;
            case "s3":
                services.AddSingleton<IFileStorage, S3FileStorage>();
                break;
            case "azureblob":
                services.AddSingleton<IFileStorage, AzureBlobFileStorage>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Providers:Storage='{providers.Storage}' has no adapter registered in DependencyInjection."
                    + " Add a case here and an entry in ProviderCatalog.");
        }

        services.AddScoped<IAttachmentService, AttachmentService>();

        // ------------------------------------------------------------------ cache / captcha
        var redisConnectionString = configuration["Redis:ConnectionString"] ?? "redis:6379";
        var redis = providers.Cache == "redis" ? TryConnectRedis(redisConnectionString) : null;

        if (providers.Cache == "redis" && redis is null && providers.FailFastOnUnavailable)
        {
            throw new InvalidOperationException(
                $"Providers:Cache=redis is not reachable at '{redisConnectionString}'"
                + " and Providers:FailFastOnUnavailable=true.");
        }

        services.AddSingleton<MemoryCacheService>();
        if (providers.Cache == "memory")
        {
            services.AddSingleton<ICacheService>(sp => sp.GetRequiredService<MemoryCacheService>());
        }
        else if (redis is not null)
        {
            services.AddSingleton<ICacheService>(_ => new RedisBackedCacheService(redis));
        }
        else
        {
            // Redis requested but unreachable: keep working on memory, report cache=error.
            services.AddSingleton<ICacheService>(sp =>
                new FallbackCacheService(new UnavailableCacheService(), sp.GetRequiredService<MemoryCacheService>()));
        }

        services.AddSingleton<ICacheTelemetry>(sp => (ICacheTelemetry)sp.GetRequiredService<ICacheService>());

        services.AddSingleton<MemoryCaptchaStore>();
        if (providers.Cache != "memory" && redis is not null)
        {
            services.AddSingleton<ICaptchaStore>(sp => new FallbackCaptchaStore(
                new RedisBackedCaptchaStore(redis),
                sp.GetRequiredService<MemoryCaptchaStore>()));
        }
        else
        {
            services.AddSingleton<ICaptchaStore>(sp => sp.GetRequiredService<MemoryCaptchaStore>());
        }

        services.AddSingleton<ICaptchaService, CaptchaService>();

        // ------------------------------------------------------------------ messaging
        var rabbitConnectionString = configuration["RabbitMq:ConnectionString"] ?? "amqp://guest:guest@rabbitmq:5672/";
        var rabbitConnection = providers.Messaging == "rabbitmq" ? TryConnectRabbit(rabbitConnectionString) : null;

        if (providers.Messaging == "rabbitmq" && rabbitConnection is null && providers.FailFastOnUnavailable)
        {
            throw new InvalidOperationException(
                "Providers:Messaging=rabbitmq is not reachable and Providers:FailFastOnUnavailable=true.");
        }

        services.Configure<RabbitMqOptions>(o =>
        {
            o.ConnectionString = configuration["RabbitMq:ConnectionString"] ?? o.ConnectionString;
        });

        if (rabbitConnection is not null)
        {
            services.AddSingleton(rabbitConnection);
            services.AddSingleton<RabbitMqEventBus>();
            services.AddSingleton<IEventConsumer>(sp => sp.GetRequiredService<RabbitMqEventBus>());
            services.AddSingleton<IWorkEventConsumer>(sp => sp.GetRequiredService<RabbitMqEventBus>());
            services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<RabbitMqEventBus>());
            services.AddHostedService(sp => sp.GetRequiredService<RabbitMqEventBus>());
        }
        else
        {
            RegisterInMemoryMessaging(services);
        }

        services.AddSingleton<IEventBus, InMemoryEventBus>();

        // ------------------------------------------------------------------ search
        var elasticOptions = BuildElasticOptions(configuration, providers);
        services.AddSingleton<IOptions<ElasticOptions>>(Options.Create(elasticOptions));

        if (providers.Search == "elastic")
        {
            services.AddSingleton<ICommentSearchIndex>(sp => new ElasticsearchSearchIndex(
                ElasticHttpClientFactory.Create(elasticOptions),
                sp.GetRequiredService<IOptions<ElasticOptions>>(),
                sp.GetRequiredService<ILogger<ElasticsearchSearchIndex>>()));
        }
        else
        {
            services.AddSingleton<ICommentSearchIndex, NoopSearchIndex>();
        }

        // Adapter-side effects of CommentCreated, one work queue each (docs/API-v2.md §7.1, §7.3, §9):
        // the queue a projection subscribes to decides which copies of an event it sees, so cache
        // invalidation and search indexing are delivered exactly once per event, not once per queue.
        services.AddHostedService<CacheInvalidationProjection>();
        services.AddHostedService<SearchIndexProjection>();

        return services;
    }

    /// <summary>
    /// Reads the search settings. Whether search is on at all is decided by the provider selection
    /// (<c>Providers:Search</c>), not by a flag here: <c>none</c> wires <see cref="NoopSearchIndex"/>
    /// and the rest of these settings then never apply.
    /// </summary>
    private static ElasticOptions BuildElasticOptions(IConfiguration configuration, ActiveProviders providers) => new()
    {
        Url = Str(configuration, "Search:Url") ?? Str(configuration, "Elastic:Url") ?? "http://elasticsearch:9200",
        IndexName = Str(configuration, "Search:IndexName") ?? "comments",
        Username = Str(configuration, "Search:Username") ?? Str(configuration, "Elastic:Username"),
        Password = Str(configuration, "Search:Password") ?? Str(configuration, "Elastic:Password"),
        ApiKey = Str(configuration, "Search:ApiKey") ?? Str(configuration, "Elastic:ApiKey"),
        AllowInvalidCertificate = Flag(
            configuration["Search:AllowInvalidCertificate"] ?? configuration["Elastic:AllowInvalidCertificate"],
            fallback: false),
        TimeoutSeconds = Number(
            configuration["Search:TimeoutSeconds"] ?? configuration["Elastic:TimeoutSeconds"],
            fallback: 10),
    };

    /// <summary>
    /// Trimmed configuration value, or null when the key is absent <em>or blank</em>. Compose
    /// forwards unset variables as empty strings, so "" must mean "not configured", not "set to
    /// empty" — otherwise a blank Region or ForcePathStyle would silently override the default.
    /// </summary>
    private static string? Str(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static void RegisterInMemoryMessaging(IServiceCollection services)
    {
        services.AddSingleton<InMemoryEventConsumer>();
        services.AddSingleton<IEventConsumer>(sp => sp.GetRequiredService<InMemoryEventConsumer>());
        services.AddSingleton<IWorkEventConsumer>(sp => sp.GetRequiredService<InMemoryEventConsumer>());
        services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<InMemoryEventConsumer>());
        services.AddHostedService(sp => sp.GetRequiredService<InMemoryEventConsumer>());
    }

    /// <summary>Lenient boolean parse: anything but an explicit no is "yes" (matches ASP.NET config).</summary>
    private static bool Flag(string? raw, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var value = raw.Trim();
        return !(value.Equals("false", StringComparison.OrdinalIgnoreCase)
                 || value.Equals("0", StringComparison.Ordinal)
                 || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                 || value.Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    private static int Number(string? raw, int fallback) =>
        int.TryParse(raw, out var value) ? value : fallback;

    private static IConnectionMultiplexer? TryConnectRedis(string connectionString)
    {
        try
        {
            var options = ConfigurationOptions.Parse(connectionString);
            options.AbortOnConnectFail = false;
            options.ConnectTimeout = 3000;
            options.AsyncTimeout = 3000;
            options.ConnectRetry = 1;

            var multiplexer = ConnectionMultiplexer.Connect(options);
            if (multiplexer.IsConnected)
            {
                return multiplexer;
            }

            multiplexer.Dispose();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static IConnection? TryConnectRabbit(string connectionString)
    {
        try
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(connectionString),
                AutomaticRecoveryEnabled = true,
                ClientProvidedName = "comments-api",
            };

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return factory.CreateConnectionAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }
}
