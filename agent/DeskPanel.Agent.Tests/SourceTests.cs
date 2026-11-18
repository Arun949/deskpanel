using DeskPanel.Agent.State;
using DeskPanel.Agent.State.Mac;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPanel.Agent.Tests;

public sealed class MacMathTests
{
    [Fact]
    public void CpuPercentIsBusyTicksOverAllTicks() =>
        Assert.Equal(25.0, MacSystemMetrics.CpuPercent([100, 100, 100, 0], [130, 120, 250, 0])); // busy 50 of 200

    [Fact]
    public void CpuPercentSurvivesCounterWrap() =>
        Assert.Equal(50.0, MacSystemMetrics.CpuPercent([uint.MaxValue - 9, 0, 0, 0], [10, 0, 20, 0])); // 20 busy, 20 idle

    [Fact]
    public void CpuPercentIsUnknownWithoutElapsedTicks() => Assert.Null(MacSystemMetrics.CpuPercent([1, 1, 1, 1], [1, 1, 1, 1]));

    [Fact]
    public void MemoryUsedIsAppPlusWiredPlusCompressed()
    {
        var vm = new int[38];
        vm[3] = 100;   // wired
        vm[22] = 50;   // purgeable
        vm[32] = 25;   // compressor
        vm[35] = 400;  // internal (anonymous)
        Assert.Equal((400 - 50 + 100 + 25) * 16384L, MacSystemMetrics.MemoryUsedBytes(vm, 16384));
        Assert.Null(MacSystemMetrics.MemoryUsedBytes(new int[10], 16384));
    }

    [Fact]
    public void NetworkRatesIgnoreNewAdaptersAndResets()
    {
        var before = new Dictionary<string, (long, long)> { ["en0"] = (1000, 500), ["en1"] = (9000, 9000) };
        var after = new Dictionary<string, (long, long)> { ["en0"] = (3000, 1500), ["en1"] = (10, 10), ["en9"] = (999999, 999999) };
        Assert.Equal((1000.0, 500.0), NetworkCounter.Rates(before, after, 2));
    }
}

public sealed class MacParsingTests
{
    [Fact]
    public void APlayingTrackIsParsed()
    {
        var m = MacMediaSource.Parse("Spotify", "playing\tSong\tArtist\tAlbum\t61000\t200000\n");
        Assert.Equal(new MediaSnapshot("Spotify", "playing", "Song", "Artist", "Album", 61000, 200000), m);
        Assert.True(m!.Playing);
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("")]
    [InlineData("playing\tonly-two")]
    public void NothingPlayingIsNull(string output) => Assert.Null(MacMediaSource.Parse("Music", output));

    [Fact]
    public void TrackTextIsCappedAndMissingValuesAreNull()
    {
        var m = MacMediaSource.Parse("Music", $"paused\t{new string('t', 300)}\tmissing value\t\t-5\tx");
        Assert.Equal(200, m!.Title!.Length);
        Assert.Null(m.Artist);
        Assert.Null(m.Album);
        Assert.Null(m.PositionMs);
        Assert.Null(m.DurationMs);
    }

    [Theory]
    [InlineData("35,false", 35, false)]
    [InlineData("0,true\n", 0, true)]
    [InlineData("missing value,missing value", null, null)]
    [InlineData("150,false", null, false)]
    public void VolumeIsParsed(string output, int? level, bool? muted) =>
        Assert.Equal(new VolumeSnapshot(level, muted), MacVolumeSource.Parse(output));
}

public sealed class StateHubTests
{
    private static StateHub Hub() => new(new UnsupportedSources(), new UnsupportedSources(), new UnsupportedSources(), NullLogger<StateHub>.Instance);

    [Fact]
    public void TheVersionMovesOnlyWhenSomethingChanged()
    {
        var hub = Hub();
        var m = new MetricsSnapshot(1, 2, 3, 4, 5);
        hub.Publish(m, null, null);
        var v = hub.Current.Version;
        hub.Publish(m with { }, null, null);   // equal value
        Assert.Equal(v, hub.Current.Version);
        hub.Publish(m with { Cpu = 9 }, null, null);
        Assert.Equal(v + 1, hub.Current.Version);
    }

    [Fact]
    public async Task WaitersWakeOnAChange()
    {
        var hub = Hub();
        var wait = hub.WaitForChangeAsync(hub.Current.Version, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.False(wait.IsCompleted);
        hub.Publish(new MetricsSnapshot(1, null, null, null, null), null, null);
        var state = await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, state.Metrics!.Cpu);
    }

    [Fact]
    public async Task ASourceThatThrowsKeepsTheLastReading()
    {
        var hub = new StateHub(new ThrowingMetrics(), new UnsupportedSources(), new UnsupportedSources(), NullLogger<StateHub>.Instance);
        var last = new MetricsSnapshot(7, null, null, null, null);
        hub.Publish(last, null, null);
        await hub.SampleOnceAsync(slow: false, CancellationToken.None);
        Assert.Equal(last, hub.Current.Metrics);
    }

    private sealed class ThrowingMetrics : ISystemMetrics
    {
        public MetricsSnapshot Sample() => throw new InvalidOperationException("sensor gone");
    }
}
