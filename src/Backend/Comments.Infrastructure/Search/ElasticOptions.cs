namespace Comments.Infrastructure.Search;

/// <summary>Elasticsearch settings (docs/API-v2.md §8.3, §10).</summary>
public sealed class ElasticOptions
{
    public const string SectionName = "Search";

    public bool Enabled { get; set; } = true;

    public string Url { get; set; } = "http://elasticsearch:9200";

    public string IndexName { get; set; } = "comments";
}
