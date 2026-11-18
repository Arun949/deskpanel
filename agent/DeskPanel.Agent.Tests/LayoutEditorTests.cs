using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeskPanel.Agent.Admin;
using DeskPanel.Agent.Api;
using DeskPanel.Agent.Layout;

namespace DeskPanel.Agent.Tests;

public sealed class CatalogTests
{
    private static WidgetConfig W(string id, string type, int x, int y, int w, int h, object? options = null) =>
        new(id, type, x, y, w, h, options is null ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(options)));

    [Fact]
    public void TheCatalogueIsConsistent()
    {
        Assert.Equal(Catalog.Widgets.Count, Catalog.Widgets.Select(w => w.Type).Distinct().Count());
        foreach (var w in Catalog.Widgets)
        {
            Assert.InRange(w.MinW, 1, w.W);
            Assert.InRange(w.MinH, 1, w.H);
            Assert.True(w.W <= Catalog.Cols && w.H <= Catalog.Rows, w.Type);
            // every default must pass the widget's own schema
            var defaults = w.Options.ToDictionary(o => o.Key, o => o.Default);
            Assert.Empty(Catalog.Check(new LayoutDoc(1, [new WidgetConfig("x", w.Type, 0, 0, w.W, w.H, defaults)])));
        }
    }

    [Fact]
    public void TheDefaultLayoutPassesTheStrictCheck() => Assert.Empty(Catalog.Check(LayoutStore.Default));

    [Fact]
    public void ErrorsNameTheExactField()
    {
        var errors = Catalog.Check(new LayoutDoc(1,
        [
            W("timer", "pomodoro", 0, 0, 3, 3, new { focusMin = 500 }),
            W("cpu", "stat", 3, 0, 3, 3, new { metric = "gpu" }),
            W("clock", "clock", 6, 0, 4, 2, new { showSeconds = "yes", colour = "red" }),
            W("sc", "shortcuts", 6, 2, 3, 2, new { actions = "ok-id, Bad Id" }),
            W("tiny", "now-playing", 9, 0, 1, 1),
            W("cpu", "clock", 0, 3, 2, 1),
            W("ghost", "shell", 2, 3, 2, 1),
            W("over", "clock", 0, 0, 2, 1),
            W("edge", "clock", 11, 5, 2, 1),
        ])).Select(e => $"{e.Path}: {e.Message}").ToList();

        Assert.Contains(errors, e => e.StartsWith("widgets[0].options.focusMin: must be between 1 and 120", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("widgets[1].options.metric: must be one of", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("widgets[2].options.showSeconds: must be true or false", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("widgets[2].options.colour: 'clock' has no option", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("widgets[3].options.actions: 'Bad Id' is not an action id", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("widgets[4].size: at least 3×1", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("widgets[5].id: duplicate id 'cpu'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("widgets[6].type: unknown widget type 'shell'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("widgets[7].position: overlaps 'timer'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("widgets[8].position: outside the 12×6 grid", StringComparison.Ordinal));
    }
}

public sealed class LayoutEditorApiTests : IDisposable
{
    private readonly TestHost _host = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<LayoutEnvelope> Load(HttpClient admin) => (await admin.GetFromJsonAsync<LayoutEnvelope>("/admin/api/layout", Json))!;

    [Fact]
    public async Task AValidEditIsSavedAndTheVersionMoves()
    {
        var admin = _host.AdminClient();
        var env = await Load(admin);
        var edited = env.Layout with { Widgets = [.. env.Layout.Widgets.Where(w => w.Id != "pomodoro")] };
        var r = await admin.PutAsJsonAsync("/admin/api/layout", new LayoutEnvelope(env.Version, edited), Json);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var after = await Load(admin);
        Assert.NotEqual(env.Version, after.Version);
        Assert.DoesNotContain(after.Layout.Widgets, w => w.Id == "pomodoro");
    }

    [Fact]
    public async Task AnInvalidEditIsRefusedWithPathsAndNothingIsWritten()
    {
        var admin = _host.AdminClient();
        var env = await Load(admin);
        var bad = env.Layout with { Widgets = [.. env.Layout.Widgets, new WidgetConfig("x", "clock", 0, 0, 2, 1, null)] }; // overlaps
        var r = await admin.PutAsJsonAsync("/admin/api/layout", new LayoutEnvelope(env.Version, bad), Json);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetProperty("path").GetString()!.EndsWith(".position", StringComparison.Ordinal));
        Assert.Equal(env.Version, (await Load(admin)).Version);
    }

    [Fact]
    public async Task ASaveOverSomeoneElsesChangeIsAConflict()
    {
        var admin = _host.AdminClient();
        var first = await Load(admin);
        var tabA = first.Layout with { Widgets = [.. first.Layout.Widgets.Where(w => w.Id != "cpu")] };
        var tabB = first.Layout with { Widgets = [.. first.Layout.Widgets.Where(w => w.Id != "ram")] };
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/admin/api/layout", new LayoutEnvelope(first.Version, tabA), Json)).StatusCode);
        var r = await admin.PutAsJsonAsync("/admin/api/layout", new LayoutEnvelope(first.Version, tabB), Json);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains((await Load(admin)).Layout.Widgets, w => w.Id == "ram"); // tab A's save survived
    }

    [Fact]
    public async Task ThePhoneLearnsTheLayoutVersion()
    {
        var (phone, _, _) = _host.Enrolled("LAYOUT-V");
        var state = await phone.GetFromJsonAsync<StateMessage>("/api/v1/state", Json);
        Assert.Equal((await Load(_host.AdminClient())).Version, state!.LayoutVersion);
    }

    [Fact]
    public async Task OnlyTheAdminCanReadOrWriteTheLayoutThroughTheEditorApi()
    {
        var (phone, _, _) = _host.Enrolled("LAYOUT-NO");
        phone.DefaultRequestHeaders.Add(AdminEndpoints.Header, "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/admin/api/layout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.PutAsJsonAsync("/admin/api/layout", new { }, Json)).StatusCode);
    }

    public void Dispose() => _host.Dispose();
}
