namespace EzOdata.Core.Time;

/// <summary>Injectable clock so business logic never reads wall-clock time directly (spec 13 §8).</summary>
public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : ISystemClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
