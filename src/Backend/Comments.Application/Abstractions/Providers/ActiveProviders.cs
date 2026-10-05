namespace Comments.Application.Abstractions.Providers;

/// <summary>
/// The provider set this process was configured with, resolved once at startup
/// (docs/ARCHITECTURE-v2.md §3).
///
/// Deliberately a dependency-free value object: the composition root produces it, and the web
/// layer may read it to report configuration without depending on an Infrastructure type. Because
/// the same instance drives both the adapters that are wired and what <c>/api/health</c> and
/// <c>/api/info</c> report, those two can never disagree.
/// </summary>
public sealed class ActiveProviders
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public ActiveProviders(
        IReadOnlyDictionary<string, string> values,
        bool strict,
        bool failFastOnUnavailable,
        IReadOnlyList<string> warnings)
    {
        _values = values;
        Strict = strict;
        FailFastOnUnavailable = failFastOnUnavailable;
        Warnings = warnings;
    }

    /// <summary>Unknown or unimplemented provider names abort startup instead of silently defaulting.</summary>
    public bool Strict { get; }

    /// <summary>When true, a selected provider that is unreachable aborts startup instead of degrading.</summary>
    public bool FailFastOnUnavailable { get; }

    /// <summary>Non-fatal notes collected while resolving; surfaced by the startup banner.</summary>
    public IReadOnlyList<string> Warnings { get; }

    public string Database => this[ProviderCatalog.Database];
    public string Cache => this[ProviderCatalog.Cache];
    public string Messaging => this[ProviderCatalog.Messaging];
    public string Search => this[ProviderCatalog.Search];
    public string Storage => this[ProviderCatalog.Storage];

    public string this[string port] => _values[port];

    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>
    /// True when the configured provider for <paramref name="port"/> runs inside this process.
    /// Health reports those as <c>disabled</c> rather than <c>ok</c>: there is no external service
    /// behind them whose reachability would be worth reporting.
    /// </summary>
    public bool IsSelfContained(string port) =>
        ProviderCatalog.Find(port, this[port])?.SelfContained ?? false;

    /// <summary>"database=postgres cache=redis messaging=rabbitmq search=elastic storage=filesystem".</summary>
    public string Describe() => string.Join(' ', ProviderCatalog.Ports.Select(p => $"{p}={_values[p]}"));
}
