using Comments.Application.Abstractions.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Comments.Infrastructure.Search;

/// <summary>Search bootstrap seam called from the composition root (docs/ARCHITECTURE-v2.md §4).</summary>
public static class SearchIndexExtensions
{
    /// <summary>Creates the Elasticsearch index if missing. Never throws (non-critical dependency).</summary>
    public static async Task EnsureSearchIndexAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var index = scope.ServiceProvider.GetRequiredService<ICommentSearchIndex>();
        await index.EnsureIndexAsync(cancellationToken);
    }
}
