namespace Comments.Application.Dtos;

/// <summary>
/// Response of <c>GET /api/info</c>: the provider switchboard as it was resolved at startup, plus
/// live reachability. Answers "what is this deployment running on, and what could it run on
/// instead" without exec-ing into the container to read configuration.
/// </summary>
public sealed class InfoDto
{
    public string Version { get; set; } = "2.2.0";

    public bool Strict { get; set; } = true;

    public bool FailFastOnUnavailable { get; set; }

    public List<ProviderInfoDto> Providers { get; set; } = new();
}

public sealed class ProviderInfoDto
{
    /// <summary>Port name: <c>database</c> | <c>cache</c> | <c>messaging</c> | <c>search</c> | <c>storage</c>.</summary>
    public string Port { get; set; } = string.Empty;

    /// <summary>The active provider for this port, e.g. <c>s3</c>.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>"ok" | "error" | "disabled" — the same vocabulary as <c>/api/health</c>.</summary>
    public string Status { get; set; } = "n/a";

    public bool Implemented { get; set; } = true;

    /// <summary>True when the adapter lives inside the API process (no external service to probe).</summary>
    public bool SelfContained { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>Other values accepted for this port — the switch is a config change, not a deploy.</summary>
    public List<string> Alternatives { get; set; } = new();
}
