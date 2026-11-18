namespace DeskPanel.Agent.State;

/// <summary>The live state a phone sees. Immutable; a new instance (and Version) per change.</summary>
public sealed record AgentState(long Version, long Ts, MetricsSnapshot? Metrics, MediaSnapshot? Media, VolumeSnapshot? Volume);

/// <summary>
/// Samples the sources and keeps the latest <see cref="AgentState"/>. Routes only read it. Sampling runs only while a
/// phone has asked for state in the last 30 s (PhoneBox sampled every second forever, Module 6).
/// </summary>
public sealed partial class StateHub(ISystemMetrics metrics, IMediaSource media, IVolumeSource volume, ILogger<StateHub> log)
    : BackgroundService
{
    internal static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(30);
    private readonly Lock _lock = new();
    private AgentState _state = new(0, Now(), null, null, null);
    private TaskCompletionSource _changed = NewSignal();
    private long _demandedAt;
    private TaskCompletionSource _demand = NewSignal();

    public AgentState Current { get { lock (_lock) return _state; } }

    /// <summary>A phone wants state: keep (or start) sampling.</summary>
    public void Touch()
    {
        Interlocked.Exchange(ref _demandedAt, Environment.TickCount64);
        _demand.TrySetResult();
    }

    /// <summary>Waits until the state's version passes <paramref name="version"/>, or the timeout. Returns the current state.</summary>
    public async Task<AgentState> WaitForChangeAsync(long version, TimeSpan timeout, CancellationToken ct)
    {
        Task signal;
        lock (_lock)
        {
            if (_state.Version > version) return _state;
            signal = _changed.Task;
        }
        await Task.WhenAny(signal, Task.Delay(timeout, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return Current;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var tick = 0L;
        while (!ct.IsCancellationRequested)
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _demandedAt) > IdleAfter.TotalMilliseconds)
            {
                // nobody is watching: sleep until a phone asks again
                var demand = _demand = NewSignal();
                if (Environment.TickCount64 - Interlocked.Read(ref _demandedAt) > IdleAfter.TotalMilliseconds)
                {
                    try { await demand.Task.WaitAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
                continue;
            }
            await SampleOnceAsync(slow: tick++ % 2 == 0, ct).ConfigureAwait(false);
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One round: metrics every second; media and volume (helper processes) every other second.</summary>
    internal async Task SampleOnceAsync(bool slow, CancellationToken ct)
    {
        var current = Current;
        var m = Safe(metrics.Sample, current.Metrics);
        var md = current.Media;
        var v = current.Volume;
        if (slow)
        {
            md = await SafeAsync(media.ReadAsync, ct).ConfigureAwait(false); // null = nothing playing
            v = await SafeAsync(volume.ReadAsync, ct).ConfigureAwait(false) ?? current.Volume;
        }
        Publish(m, md, v);
    }

    internal void Publish(MetricsSnapshot? m, MediaSnapshot? md, VolumeSnapshot? v)
    {
        TaskCompletionSource? fire = null;
        lock (_lock)
        {
            if (Equals(m, _state.Metrics) && Equals(md, _state.Media) && Equals(v, _state.Volume)) return;
            _state = new AgentState(_state.Version + 1, Now(), m, md, v);
            fire = _changed;
            _changed = NewSignal();
        }
        fire.TrySetResult();
    }

    private T? Safe<T>(Func<T> read, T? fallback) where T : class
    {
        try { return read(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { LogSourceFailed(log, typeof(T).Name, ex.Message); return fallback; }
    }

    private async Task<T?> SafeAsync<T>(Func<CancellationToken, Task<T?>> read, CancellationToken ct) where T : class
    {
        try { return await read(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { LogSourceFailed(log, typeof(T).Name, ex.Message); return null; }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Source} reading failed: {Message}")]
    private static partial void LogSourceFailed(ILogger logger, string source, string message);
}
