namespace NewUOAM.Positioning;

public enum PositionProviderStatus
{
    Stopped,
    Starting,
    Running,
    Error,
}

/// <summary>
/// A pluggable source of "where is my character right now" data. Multiple implementations can
/// exist side by side (Orion script over UDP, network packet proxy, external process-memory
/// read) and the app picks whichever one is running.
/// </summary>
public interface IPositionProvider : IDisposable
{
    string Name { get; }
    string Description { get; }
    PositionProviderStatus Status { get; }

    event EventHandler<PositionUpdate>? PositionChanged;
    event EventHandler<string>? StatusMessage;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}
