namespace Comments.Application.Abstractions.Providers;

/// <summary>
/// One selectable provider for one port. <paramref name="Implemented"/> marks a documented seam
/// (the name is recognised, so it gets a precise error instead of "unknown value").
/// <paramref name="SelfContained"/> marks an adapter that lives inside the API process — the
/// layer that serves every request is what implements it, so reporting "ok" for it in health says
/// nothing about an external dependency and is reported as "disabled" instead.
/// </summary>
public sealed record ProviderDescriptor(
    string Port,
    string Value,
    string Description,
    bool Implemented = true,
    bool SelfContained = false);

/// <summary>
/// Single source of truth for "which providers exist for which port". Validation
/// (<c>ProviderSelection.Resolve</c>), the startup banner and <c>GET /api/info</c> all read this
/// list, so adding a provider is: one entry here + one adapter in Infrastructure — nothing else
/// needs to know the name (no <c>switch</c> in the API layer, no hard-coded name in health).
/// </summary>
public static class ProviderCatalog
{
    public const string Database = "database";
    public const string Cache = "cache";
    public const string Messaging = "messaging";
    public const string Search = "search";
    public const string Storage = "storage";

    /// <summary>Ports in report order (startup banner and <c>GET /api/info</c>).</summary>
    public static IReadOnlyList<string> Ports { get; } =
        new[] { Database, Cache, Messaging, Search, Storage };

    public static IReadOnlyList<ProviderDescriptor> All { get; } = new ProviderDescriptor[]
    {
        new(Database, "postgres",
            "PostgreSQL via Npgsql (managed: RDS/Aurora, Cloud SQL, Azure Database, Yandex Managed)"),
        new(Database, "mssql",
            "MS SQL Server via EF Core SqlServer — the assignment's preferred engine, needs its own migration set",
            Implemented: false),

        new(Cache, "redis",
            "Redis over TCP/TLS (managed: ElastiCache, Memorystore, Azure Cache, Upstash)"),
        new(Cache, "memory",
            "In-process memory cache — single instance only, CAPTCHA not shared between replicas",
            SelfContained: true),

        new(Messaging, "rabbitmq",
            "AMQP 0-9-1 broker (managed: CloudAMQP, Amazon MQ, Azure Service Bus AMQP)"),
        new(Messaging, "inmemory",
            "In-process bus — no broker, queued events die with the process",
            SelfContained: true),

        new(Search, "elastic",
            "Elasticsearch 8 / OpenSearch REST — managed clusters via basic auth, API key or TLS"),
        new(Search, "none",
            "Search switched off — GET /api/search answers 503 and indexing is a no-op",
            SelfContained: true),

        new(Storage, "filesystem",
            "Local or shared volume — the only provider with a sendfile fast path"),
        new(Storage, "s3",
            "S3-compatible object storage: AWS S3, MinIO, Yandex Object Storage, Cloudflare R2, DO Spaces"),
        new(Storage, "azureblob",
            "Azure Blob Storage: connection string, account URL + SAS, or DefaultAzureCredential (managed identity)"),
    };

    public static IEnumerable<ProviderDescriptor> For(string port) =>
        All.Where(p => string.Equals(p.Port, port, StringComparison.Ordinal));

    public static ProviderDescriptor? Find(string port, string? value) =>
        value is null
            ? null
            : All.FirstOrDefault(p =>
                string.Equals(p.Port, port, StringComparison.Ordinal)
                && string.Equals(p.Value, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Used when nothing is configured; must itself be an implemented provider.</summary>
    public static string DefaultFor(string port) => port switch
    {
        Database => "postgres",
        Cache => "redis",
        Messaging => "rabbitmq",
        Search => "elastic",
        Storage => "filesystem",
        _ => throw new ArgumentOutOfRangeException(nameof(port), port, "Unknown provider port."),
    };

    /// <summary>"database" → "Database", for configuration keys and error messages.</summary>
    public static string Title(string port) => char.ToUpperInvariant(port[0]) + port[1..];

    public static string ImplementedValues(string port) =>
        string.Join(", ", For(port).Where(p => p.Implemented).Select(p => p.Value));

    public static string AllValues(string port) =>
        string.Join(", ", For(port).Select(p => p.Value));
}
