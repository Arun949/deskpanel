using System.Net.Http.Json;
using System.Text.Json;
using DeskPanel.Agent.Api;
using DeskPanel.Agent.State;
using DeskPanel.Agent.State.Mac;
using DeskPanel.Agent.State.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPanel.Agent.Tests;

/// <summary>The "On Air" signal: camera / mic in use, yes or no (docs/design/WIDGETS.md #1).</summary>
public sealed class PresenceTests(TestHost host) : IClassFixture<TestHost>
{
    [Theory]
    [InlineData(133000000000000000L, 0L, true)]                    // started, not stopped: in use now
    [InlineData(133000000000000000L, 133000000000000001L, false)]  // started and stopped
    [InlineData(0L, 0L, false)]                                    // never used
    public void WindowsReadsItsOwnUsageRecord(long start, long stop, bool active) =>
        Assert.Equal(active, WindowsPresence.IsActive(start, stop));

    [Fact]
    public void WindowsIgnoresValuesOfTheWrongType() => Assert.False(WindowsPresence.IsActive("1", 0L));

    [Theory]
    [InlineData("corespeechd", true)]   // "Hey Siri": always listening, not a call
    [InlineData("avconferenced", false)] // FaceTime calls
    [InlineData("zoom.us", false)]
    [InlineData(null, false)]
    public void TheWakeWordListenerDoesNotCountAsACall(string? name, bool ignored) =>
        Assert.Equal(ignored, MacPresence.IsAlwaysListening(name));

    [Fact]
    public void FourCharacterCodesMatchApples() => Assert.Equal(0x676F6E65u, MacPresence.Code("gone"));

    /// <summary>On a Mac (CI runners included) the real CoreAudio / CoreMediaIO calls answer yes or no, never throw.</summary>
    [Fact]
    public void OnAMacTheRealCallsAnswer()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var p = new MacPresence().Read();
        Assert.NotNull(p);
        Assert.NotNull(p.Camera);
        Assert.NotNull(p.Mic);
    }

    [Fact]
    public void OnAirMeansEither()
    {
        Assert.True(new PresenceSnapshot(true, false).OnAir);
        Assert.True(new PresenceSnapshot(null, true).OnAir);
        Assert.False(new PresenceSnapshot(false, null).OnAir);
    }

    [Fact]
    public void AChangeIsANewStateVersion()
    {
        var hub = new StateHub(new UnsupportedSources(), new UnsupportedSources(), new UnsupportedSources(), NullLogger<StateHub>.Instance);
        hub.Publish(null, null, null, new PresenceSnapshot(false, false));
        var v = hub.Current.Version;
        hub.Publish(null, null, null, new PresenceSnapshot(false, false));
        Assert.Equal(v, hub.Current.Version);           // same value: no new version, nothing sent
        hub.Publish(null, null, null, new PresenceSnapshot(true, false));
        Assert.Equal(v + 1, hub.Current.Version);
    }

    [Fact]
    public async Task ThePhoneGetsItInTheState()
    {
        var (client, _, _) = host.Enrolled("PRESENCE");
        host.Hub.Publish(host.Sources.Metrics, host.Sources.Media, host.Sources.Volume, new PresenceSnapshot(true, false));
        var state = await client.GetFromJsonAsync<StateMessage>("/api/v1/state");
        Assert.True(state!.Presence!.Camera);
        Assert.False(state.Presence.Mic);
    }

    [Fact]
    public async Task OnlyYesOrNoIsSentNeverAppNames()
    {
        var (client, _, _) = host.Enrolled("PRESENCE-SHAPE");
        host.Hub.Publish(host.Sources.Metrics, host.Sources.Media, host.Sources.Volume, new PresenceSnapshot(false, true));
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/v1/state"));
        var names = doc.RootElement.GetProperty("presence").EnumerateObject().Select(p => p.Name).Order().ToList();
        Assert.Equal(["camera", "mic"], names);
    }
}
