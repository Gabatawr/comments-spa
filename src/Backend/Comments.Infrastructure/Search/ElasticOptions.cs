namespace Comments.Infrastructure.Search;

/// <summary>
/// Elasticsearch / OpenSearch settings (docs/API-v2.md §8.3, §10). The auth fields exist because a
/// managed cluster (Elastic Cloud, OpenSearch Service, Aiven, self-hosted behind nginx) always
/// requires credentials — a bare <see cref="HttpClient"/> only ever worked against a local,
/// unauthenticated container.
/// </summary>
public sealed class ElasticOptions
{
    public const string SectionName = "Search";

    public bool Enabled { get; set; } = true;

    public string Url { get; set; } = "http://elasticsearch:9200";

    public string IndexName { get; set; } = "comments";

    /// <summary>Basic auth (managed Elastic Cloud, OpenSearch Service). Empty = no auth header.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>
    /// API-key auth. For Elasticsearch this is the base64 <c>id:api_key</c> value; for OpenSearch
    /// the plain API key. Takes precedence over <see cref="Username"/>.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Accept a private/self-signed CA certificate (managed clusters behind a private CA).</summary>
    public bool AllowInvalidCertificate { get; set; }

    /// <summary>Per-request timeout — bound so an unreachable cluster cannot stall /api/health.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}
