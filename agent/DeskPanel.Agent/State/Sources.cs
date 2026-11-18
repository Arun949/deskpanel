namespace DeskPanel.Agent.State;

// Every reading is nullable: "unknown" is never shown as 0 (PhoneBox's pattern, Module 6).

public sealed record MetricsSnapshot(double? Cpu, double? RamUsedGb, double? RamTotalGb, double? NetDownBps, double? NetUpBps);

public sealed record MediaSnapshot(string App, string State, string? Title, string? Artist, string? Album, long? PositionMs, long? DurationMs)
{
    public bool Playing => State == "playing";
}

public sealed record VolumeSnapshot(int? Level, bool? Muted);

/// <summary>CPU, memory and network. Called once a second; implementations keep the previous reading for rates.</summary>
public interface ISystemMetrics
{
    MetricsSnapshot Sample();
}

/// <summary>What is playing, or null when nothing is.</summary>
public interface IMediaSource
{
    Task<MediaSnapshot?> ReadAsync(CancellationToken ct);
}

public interface IVolumeSource
{
    Task<VolumeSnapshot?> ReadAsync(CancellationToken ct);
}

/// <summary>Platforms without an adapter yet: everything unknown, nothing fails.</summary>
public sealed class UnsupportedSources : ISystemMetrics, IMediaSource, IVolumeSource
{
    public MetricsSnapshot Sample() => new(null, null, null, null, null);
    public Task<MediaSnapshot?> ReadAsync(CancellationToken ct) => Task.FromResult<MediaSnapshot?>(null);
    Task<VolumeSnapshot?> IVolumeSource.ReadAsync(CancellationToken ct) => Task.FromResult<VolumeSnapshot?>(null);
}
