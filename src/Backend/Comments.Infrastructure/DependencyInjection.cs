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
/// Infrastructure composition (docs/ARCHITECTURE-v2.md §4, §5). Every external dependency is
/// selected from configuration and degrades gracefully: a missing Redis/RabbitMQ/Elasticsearch
/// host never crashes startup.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCommentsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
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

        // ------------------------------------------------------------------ options
        services.Configure<AttachmentStorageOptions>(o =>
        {
            o.Provider = configuration["Storage:Provider"] ?? "filesystem";
            o.Root = configuration["Storage:Root"] ?? "storage";
        });
        services.Configure<RabbitMqOptions>(o =>
        {
            o.ConnectionString = configuration["RabbitMq:ConnectionString"] ?? o.ConnectionString;
        });
        services.Configure<ElasticOptions>(o =>
        {
            o.Enabled = !string.Equals(configuration["Search:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
            o.Url = configuration["Elastic:Url"] ?? configuration["Search:Url"] ?? o.Url;
            o.IndexName = configuration["Search:IndexName"] ?? o.IndexName;
        });

        // ------------------------------------------------------------------ storage
        var storageProvider = (configuration["Storage:Provider"] ?? "filesystem").Trim().ToLowerInvariant();
        switch (storageProvider)
        {
            case "s3":
                services.AddSingleton<IFileStorage, S3FileStorage>();
                break;
            case "azureblob":
                services.AddSingleton<IFileStorage, AzureBlobFileStorage>();
                break;
            default:
                services.AddSingleton<IFileStorage, FileSystemStorage>();
                break;
        }

        services.AddScoped<IAttachmentService, AttachmentService>();

        // ------------------------------------------------------------------ cache / captcha
        var cacheProvider = (configuration["Cache:Provider"] ?? "redis").Trim().ToLowerInvariant();
        var redisConnectionString = configuration["Redis:ConnectionString"] ?? "redis:6379";
        var redis = cacheProvider == "redis" ? TryConnectRedis(redisConnectionString) : null;

        services.AddSingleton<MemoryCacheService>();
        if (cacheProvider == "memory")
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
        if (cacheProvider != "memory" && redis is not null)
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
        var messagingProvider = (configuration["Messaging:Provider"] ?? "rabbitmq").Trim().ToLowerInvariant();
        var rabbitConnectionString = configuration["RabbitMq:ConnectionString"] ?? "amqp://guest:guest@rabbitmq:5672/";
        var rabbitConnection = messagingProvider == "rabbitmq" ? TryConnectRabbit(rabbitConnectionString) : null;

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
        var searchEnabled = !string.Equals(configuration["Search:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
        if (searchEnabled)
        {
            var elasticUrl = configuration["Elastic:Url"] ?? configuration["Search:Url"] ?? "http://elasticsearch:9200";
            services.AddSingleton<ICommentSearchIndex>(sp => new ElasticsearchSearchIndex(
                new HttpClient { BaseAddress = new Uri(elasticUrl.TrimEnd('/') + "/") },
                sp.GetRequiredService<IOptions<ElasticOptions>>(),
                sp.GetRequiredService<ILogger<ElasticsearchSearchIndex>>()));
        }
        else
        {
            services.AddSingleton<ICommentSearchIndex, NoopSearchIndex>();
        }

        // Adapter-side effects: cache generation bump + async ES indexing (docs/API-v2.md §7.3, §9).
        services.AddHostedService<CommentCreatedProjection>();

        return services;
    }

    private static void RegisterInMemoryMessaging(IServiceCollection services)
    {
        services.AddSingleton<InMemoryEventConsumer>();
        services.AddSingleton<IEventConsumer>(sp => sp.GetRequiredService<InMemoryEventConsumer>());
        services.AddSingleton<IWorkEventConsumer>(sp => sp.GetRequiredService<InMemoryEventConsumer>());
        services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<InMemoryEventConsumer>());
        services.AddHostedService(sp => sp.GetRequiredService<InMemoryEventConsumer>());
    }

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
