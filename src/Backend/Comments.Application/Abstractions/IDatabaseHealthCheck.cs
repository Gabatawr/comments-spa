namespace Comments.Application.Abstractions;

/// <summary>Database reachability probe used by <c>GET /api/health</c> (docs/API-v2.md §2.1).</summary>
public interface IDatabaseHealthCheck
{
    Task<bool> CanConnectAsync(CancellationToken cancellationToken = default);
}
