using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeskPanel.Agent.Api;
using DeskPanel.Agent.Layout;

namespace DeskPanel.Agent.Tests;

public sealed class StateAndLayoutTests(TestHost host) : IClassFixture<TestHost>
{
    [Fact]
    public async Task TheStreamSendsAFullStateFirstWithARunScopedId()
    {
        var (client, _, _) = host.Enrolled("STREAM");
        host.Hub.Publish(host.Sources.Metrics, host.Sources.Media, host.Sources.Volume);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var res = await client.GetAsync("/api/v1/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);

        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cts.Token));
        var id = await reader.ReadLineAsync(cts.Token);
        var evt = await reader.ReadLineAsync(cts.Token);
        var data = await reader.ReadLineAsync(cts.Token);

        Assert.Matches("^id: [0-9a-f]{16}-[0-9]+$", id);
        Assert.Equal("event: state", evt);
        var msg = JsonSerializer.Deserialize<StateMessage>(data!["data: ".Length..], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(msg!.Full);
        Assert.Equal(12.5, msg.Metrics!.Cpu);
        Assert.Equal("Song", msg.Media!.Title);
    }

    [Fact]
    public async Task TheStreamRequiresADeviceKey() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client().GetAsync("/api/v1/stream")).StatusCode);

    [Fact]
    public async Task TheDefaultLayoutIsServedToADevice()
    {
        var (client, _, _) = host.Enrolled("LAYOUT");
        var layout = await client.GetFromJsonAsync<LayoutDoc>("/api/v1/layout");
        Assert.Equal(LayoutStore.Default.Widgets.Select(w => w.Id), layout!.Widgets.Select(w => w.Id));
        Assert.True(File.Exists(Path.Combine(host.Home, "layout.json")));
    }

    [Fact]
    public async Task TheLayoutRequiresADeviceKey() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client().GetAsync("/api/v1/layout")).StatusCode);

    /// <summary>Secret canary: a device key must never appear in anything the agent serves or stores.</summary>
    [Fact]
    public async Task TheDeviceKeyNeverLeaves()
    {
        var (client, device, key) = host.Enrolled("CANARY");
        host.Hub.Publish(host.Sources.Metrics, host.Sources.Media, host.Sources.Volume);
        var bodies = new[]
        {
            await client.GetStringAsync("/api/v1/state"),
            await client.GetStringAsync("/api/v1/layout"),
            await host.Client().GetStringAsync($"/api/v1/hello?d={device.Id}&n=abcdefghijklmnop0123"),
            await File.ReadAllTextAsync(Path.Combine(host.Home, "devices.json")),
        };
        foreach (var body in bodies) Assert.DoesNotContain(key, body, StringComparison.Ordinal);
    }

    [Fact]
    public void DevicesFileIsOwnerOnly()
    {
        host.Enrolled("PERMS");
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(host.Home, "devices.json")));
    }
}

public sealed class LayoutValidationTests
{
    private static WidgetConfig W(string id, string type, int x = 0, int y = 0, int w = 2, int h = 2, Dictionary<string, JsonElement>? o = null) =>
        new(id, type, x, y, w, h, o);

    [Fact]
    public void UnknownTypesAndDuplicateOrBadIdsAreDropped()
    {
        var doc = LayoutStore.Validate(new LayoutDoc(1, [W("a", "clock"), W("a", "stat"), W("b", "shell-command"), W("Bad Id", "clock")]));
        Assert.Equal(["a"], doc.Widgets.Select(w => w.Id));
    }

    [Fact]
    public void WidgetsAreClampedOntoTheGrid()
    {
        // a clock is at least 2×1 (catalogue), so it is pulled in from the edge to fit
        var w = LayoutStore.Validate(new LayoutDoc(1, [W("a", "clock", x: 11, y: 9, w: 6, h: 0)])).Widgets.Single();
        Assert.Equal((10, 5, 2, 1), (w.X, w.Y, w.W, w.H));
    }

    [Fact]
    public void OptionsAreLimitedToShortScalars()
    {
        var o = new Dictionary<string, JsonElement>
        {
            ["metric"] = JsonSerializer.SerializeToElement("cpu"),
            ["seconds"] = JsonSerializer.SerializeToElement(true),
            ["nested"] = JsonSerializer.SerializeToElement(new { cmd = "rm -rf ~" }),
            ["long"] = JsonSerializer.SerializeToElement(new string('x', 201)),
        };
        // only options the catalogue defines for 'stat', with valid values, survive a hand edit
        var opts = LayoutStore.Validate(new LayoutDoc(1, [W("a", "stat", o: o)])).Widgets.Single().Options!;
        Assert.Equal(["metric"], opts.Keys.Order());
    }

    [Fact]
    public void ANullDocumentFallsBackToTheDefault() => Assert.Same(LayoutStore.Default, LayoutStore.Validate(null));
}
