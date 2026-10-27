namespace EstateHub.Api.Streaming;

/// <summary>Bound from the "Streaming" configuration section.</summary>
public sealed class StreamingOptions
{
    public const string SectionName = "Streaming";

    /// <summary>Idle time before a keepalive heartbeat is sent on an open stream.</summary>
    public int HeartbeatSeconds { get; set; } = 30;

    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(Math.Max(1, HeartbeatSeconds));

    /// <summary>Open server streams allowed per user at once (SECURITY.md layer 6).</summary>
    public int MaxStreamsPerUser { get; set; } = 5;
}
