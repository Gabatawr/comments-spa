namespace Comments.Application.Abstractions.Persistence;

/// <summary>
/// Bulk generation of test data for k6 / «1M rows» (docs/API-v2.md §3.4).
/// Implemented in Infrastructure with batched inserts / binary COPY.
/// </summary>
public interface ICommentSeeder
{
    Task<SeedOutcome> SeedAsync(SeedOptions options, CancellationToken cancellationToken = default);
}
