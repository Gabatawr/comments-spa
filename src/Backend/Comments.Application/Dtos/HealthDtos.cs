namespace Comments.Application.Dtos;

/// <summary>Health response (docs/API-v2.md §2.1) with the Middle+ dependency fields.</summary>
public sealed class HealthDto
{
    public string Status { get; set; } = "ok";
    public string Database { get; set; } = "ok";
    public string Cache { get; set; } = "ok";
    public QueueHealthDto Queue { get; set; } = new();
    public WebSocketHealthDto Websocket { get; set; } = new();

    /// <summary>"ok" | "error" | "disabled".</summary>
    public string Redis { get; set; } = "ok";

    /// <summary>"ok" | "error" | "disabled".</summary>
    public string Broker { get; set; } = "ok";

    /// <summary>"ok" | "error" | "disabled".</summary>
    public string Search { get; set; } = "ok";

    /// <summary>"ok" | "error" | "disabled".</summary>
    public string Storage { get; set; } = "ok";

    /// <summary>
    /// Active provider per port as resolved at startup, e.g.
    /// <c>{"database":"postgres","storage":"filesystem"}</c>. Reports which adapter answers, while
    /// the fields above report whether it is reachable (docs/ARCHITECTURE-v2.md §3).
    /// </summary>
    public Dictionary<string, string> Providers { get; set; } = new(StringComparer.Ordinal);

    public string Version { get; set; } = "2.2.0";
}

public sealed class QueueHealthDto
{
    public int Pending { get; set; }
    public int Processed { get; set; }
}

public sealed class WebSocketHealthDto
{
    public int Clients { get; set; }
}
