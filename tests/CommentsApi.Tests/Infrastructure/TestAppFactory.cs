using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace CommentsApi.Tests.Infrastructure;

/// <summary>
/// WebApplicationFactory with a per-instance PostgreSQL database
/// (<c>comments_test_&lt;guid&gt;</c>), temp storage root and the Development-only CAPTCHA peek
/// feature enabled (docs/ARCHITECTURE-v2.md §6). The database is created before the app starts,
/// migrations are applied by the app's own startup path, and it is dropped on dispose.
/// </summary>
public sealed class TestAppFactory : WebApplicationFactory<Program>
{
    /// <summary>PostgreSQL admin connection provided by the test harness (compose postgres).</summary>
    public const string DefaultServerConnection =
        "Host=localhost;Port=55432;Username=comments;Password=comments;Database=postgres";

    public static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("COMMENTS_TEST_POSTGRES") ?? DefaultServerConnection;

    /// <summary>When set to 1, the app is wired to the real Redis/Elasticsearch/RabbitMQ services.</summary>
    public static bool UseExternalServices =>
        string.Equals(Environment.GetEnvironmentVariable("COMMENTS_TEST_EXTERNAL_SERVICES"), "1", StringComparison.Ordinal);

    public static string RedisConnectionString =>
        Environment.GetEnvironmentVariable("COMMENTS_TEST_REDIS") ?? "localhost:56379";

    public static string ElasticUrl =>
        Environment.GetEnvironmentVariable("COMMENTS_TEST_ELASTIC") ?? "http://localhost:59200";

    public static string? RabbitConnectionString =>
        Environment.GetEnvironmentVariable("COMMENTS_TEST_RABBITMQ");

    public string RootDir { get; }

    public string StorageDir { get; }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public TestAppFactory()
    {
        RootDir = Path.Combine(Path.GetTempPath(), "comments-qa", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootDir);
        StorageDir = Path.Combine(RootDir, "storage");
        Directory.CreateDirectory(StorageDir);

        DatabaseName = "comments_test_" + Guid.NewGuid().ToString("N");
        ConnectionString = BuildDatabaseConnectionString(DatabaseName);
        CreateDatabase(DatabaseName);

        // IMPORTANT: Program.cs reads GetConnectionString("Default") during composition, before
        // WebApplicationFactory's ConfigureAppConfiguration callbacks run. Environment variables
        // are read by CreateBuilder itself, so they are the only reliable seam here. Test
        // collections run sequentially (see AssemblyInfo.cs), so mutating the process environment
        // is safe.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", ConnectionString);
        Environment.SetEnvironmentVariable("Storage__Root", StorageDir);
        Environment.SetEnvironmentVariable("Features__DevCaptchaPeek", "true");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        // Default: tests must not require Redis/RabbitMQ/Elasticsearch (docs/ARCHITECTURE-v2.md §5).
        // Opt-in COMMENTS_TEST_EXTERNAL_SERVICES=1 points the app at the compose data services,
        // which the Category=IntegrationBroker tests use.
        foreach (var (key, value) in BuildSettings())
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private Dictionary<string, string?> BuildSettings()
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Default"] = ConnectionString,
            ["ConnectionStrings__Default"] = ConnectionString,
            ["Features:DevCaptchaPeek"] = "true",
            ["Features__DevCaptchaPeek"] = "true",
            ["Storage:Root"] = StorageDir,
            ["Storage__Root"] = StorageDir,
        };

        if (UseExternalServices)
        {
            settings["Cache:Provider"] = "redis";
            settings["Cache__Provider"] = "redis";
            settings["Redis:ConnectionString"] = RedisConnectionString;
            settings["Redis__ConnectionString"] = RedisConnectionString;
            settings["Search:Enabled"] = "true";
            settings["Search__Enabled"] = "true";
            settings["Elastic:Url"] = ElasticUrl;
            settings["Elastic__Url"] = ElasticUrl;

            if (RabbitConnectionString is { } rabbit)
            {
                settings["Messaging:Provider"] = "rabbitmq";
                settings["Messaging__Provider"] = "rabbitmq";
                settings["RabbitMq:ConnectionString"] = rabbit;
                settings["RabbitMq__ConnectionString"] = rabbit;
            }
            else
            {
                settings["Messaging:Provider"] = "inmemory";
                settings["Messaging__Provider"] = "inmemory";
            }
        }
        else
        {
            settings["Cache:Provider"] = "memory";
            settings["Cache__Provider"] = "memory";
            settings["Messaging:Provider"] = "inmemory";
            settings["Messaging__Provider"] = "inmemory";
            settings["Search:Enabled"] = "false";
            settings["Search__Enabled"] = "false";
        }

        return settings;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(BuildSettings()));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        DropDatabase(DatabaseName);

        try
        {
            if (Directory.Exists(RootDir))
            {
                Directory.Delete(RootDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup of the temp dir; the OS temp folder is not part of the repo.
        }
    }

    private static string BuildDatabaseConnectionString(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(ServerConnectionString)
        {
            Database = databaseName,
        };

        return builder.ConnectionString;
    }

    private static void CreateDatabase(string databaseName)
    {
        var admin = new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = "postgres" };
        using var connection = new NpgsqlConnection(admin.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        // databaseName is server-generated (comments_test_ + GUID), never user input.
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        command.ExecuteNonQuery();
    }

    private static void DropDatabase(string databaseName)
    {
        try
        {
            NpgsqlConnection.ClearAllPools();

            var admin = new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = "postgres" };
            using var connection = new NpgsqlConnection(admin.ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            command.ExecuteNonQuery();
        }
        catch
        {
            // Best effort: leaving an idle test DB behind must not fail the run.
        }
    }
}
