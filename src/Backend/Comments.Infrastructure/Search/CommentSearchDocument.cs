namespace Comments.Infrastructure.Search;

/// <summary>
/// Elasticsearch document for the <c>comments</c> index (docs/API-v2.md §8.3). Field names are
/// inferred as camelCase by the client settings configured in DI.
/// </summary>
public sealed class CommentSearchDocument
{
    public long Id { get; set; }

    public long? ParentId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string TextPlain { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public string? ClientIp { get; set; }
}
