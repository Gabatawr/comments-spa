namespace Comments.Application.Abstractions;

/// <summary>Abstracts the clock so use-cases are deterministic in tests.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>Default UTC clock. Pure — safe to live in the Application layer.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
