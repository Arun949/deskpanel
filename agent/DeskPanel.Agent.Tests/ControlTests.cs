using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeskPanel.Agent.Api;
using DeskPanel.Agent.Control;
using DeskPanel.Agent.Layout;
using DeskPanel.Agent.Security;

namespace DeskPanel.Agent.Tests;

/// <summary>M2 exit criteria: every control passes permission, layout, rate limit and idempotency, and is audited.</summary>
public sealed class ControlTests : IDisposable
{
    private readonly TestHost _host = new();
    private readonly string _app;

    public ControlTests()
    {
        _app = Directory.CreateDirectory(Path.Combine(_host.Home, "Apps", "Fake.app")).FullName;
        _ = _host.Services;
        _host.Actions.Replace([
            new ActionDef("open-fake", "Fake", "launch", _app),
            new ActionDef("shot", "Screenshot", "keys", "cmd+shift+4"),
            new ActionDef("hidden", "Hidden", "keys", "cmd+h"),
        ]);
        _host.SetLayout(
            new WidgetConfig("mc", "media-controls", 0, 0, 4, 1, null),
            new WidgetConfig("vol", "volume", 4, 0, 4, 1, null),
            new WidgetConfig("sc", "shortcuts", 0, 1, 4, 2, new() { ["actions"] = JsonSerializer.SerializeToElement("open-fake, shot") }));
    }

    private (HttpClient Client, EnrolledDevice Device) Phone(params string[] grants)
    {
        var (client, device, _) = _host.Enrolled($"S{Guid.NewGuid():N}"[..12]);
        _host.Permissions.Set(device.Id, grants);
        return (client, device);
    }

    private static Task<HttpResponseMessage> Post(HttpClient c, string path, string? idem = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path);
        if (idem is not null) req.Headers.Add("Idempotency-Key", idem);
        return c.SendAsync(req);
    }

    private static async Task<string?> Error(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("error", out var e) ? e.GetString() : null;

    [Fact]
    public async Task WithoutTheGrantAControlIsRefusedAndAudited()
    {
        var (phone, device) = Phone();
        var r = await Post(phone, "/api/v1/media/play-pause");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("not-allowed", await Error(r));
        Assert.Empty(_host.Controls.Calls);
        Assert.Contains(_host.Audit.Recent(10), e => e.DeviceId == device.Id && e.Action == "media.play-pause" && e.Result == "denied");
    }

    [Fact]
    public async Task WithTheGrantTheControlRunsAndIsAudited()
    {
        var (phone, device) = Phone(Permission.MediaControl, Permission.VolumeControl);
        Assert.Equal(HttpStatusCode.OK, (await Post(phone, "/api/v1/media/next")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(phone, "/api/v1/volume?level=30")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(phone, "/api/v1/volume?mute=true")).StatusCode);
        Assert.Equal(["media:next", "volume:30:", "volume::True"], _host.Controls.Calls);
        Assert.Equal(3, _host.Audit.Recent(10).Count(e => e.DeviceId == device.Id && e.Result == "ok"));
    }

    [Fact]
    public async Task AGrantForOneControlDoesNotCoverAnother()
    {
        var (phone, _) = Phone(Permission.VolumeControl);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(phone, "/api/v1/media/next")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(phone, "/api/v1/actions/open-fake")).StatusCode);
    }

    [Fact]
    public async Task ActionsRunOnlyByIdAndOnlyWhenTheLayoutShowsThem()
    {
        var (phone, _) = Phone(Permission.AppsLaunch, Permission.KeysSend);
        Assert.Equal(HttpStatusCode.OK, (await Post(phone, "/api/v1/actions/open-fake")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(phone, "/api/v1/actions/shot")).StatusCode);
        var hidden = await Post(phone, "/api/v1/actions/hidden"); // defined, permitted, but no widget lists it
        Assert.Equal(HttpStatusCode.Forbidden, hidden.StatusCode);
        Assert.Equal("not-on-layout", await Error(hidden));
        var unknown = await Post(phone, "/api/v1/actions/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal([$"launch:{_app}", "keys:cmd+shift+4"], _host.Controls.Calls);
        Assert.Contains(_host.Audit.Recent(10), e => e.Action == "action.does-not-exist" && e.Result == "not-found");
    }

    [Fact]
    public async Task AControlWhoseWidgetIsNotOnTheLayoutIsRefused()
    {
        _host.SetLayout(new WidgetConfig("clock", "clock", 0, 0, 4, 2, null));
        var (phone, _) = Phone(Permission.MediaControl, Permission.VolumeControl);
        var r = await Post(phone, "/api/v1/media/next");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("not-on-layout", await Error(r));
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(phone, "/api/v1/volume?level=10")).StatusCode);
        Assert.Empty(_host.Controls.Calls);
    }

    [Fact]
    public async Task ARetriedRequestWithTheSameIdempotencyKeyRunsOnce()
    {
        var (phone, _) = Phone(Permission.AppsLaunch);
        var first = await Post(phone, "/api/v1/actions/open-fake", "tap-0001-abcd");
        var again = await Post(phone, "/api/v1/actions/open-fake", "tap-0001-abcd");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True(again.Headers.Contains("Idempotent-Replayed"));
        Assert.Single(_host.Controls.Calls);
        Assert.Contains(_host.Audit.Recent(10), e => e.Result == "replayed");
    }

    [Fact]
    public async Task KeyPressesAreRateLimited()
    {
        var (phone, _) = Phone(Permission.KeysSend);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await Post(phone, "/api/v1/actions/shot")).StatusCode);
        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.OK));               // burst of 3
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        Assert.Contains(_host.Audit.Recent(10), e => e.Result == "rate-limited");
    }

    [Theory]
    [InlineData("/api/v1/media/shutdown")]
    [InlineData("/api/v1/volume")]
    [InlineData("/api/v1/volume?level=101")]
    public async Task BadControlRequestsAreRejected(string path)
    {
        var (phone, _) = Phone(Permission.MediaControl, Permission.VolumeControl);
        var r = await Post(phone, path);
        Assert.True(r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, r.StatusCode.ToString());
        Assert.Empty(_host.Controls.Calls);
    }

    [Fact]
    public async Task ControlsNeedADeviceKey()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(_host.Client(), "/api/v1/media/next")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(_host.AdminClient(), "/api/v1/actions/open-fake")).StatusCode);
    }

    [Fact]
    public async Task TheLayoutShowsActionLabelsButNeverTheirTargets()
    {
        var (phone, _) = Phone();
        var body = await phone.GetStringAsync("/api/v1/layout");
        var layout = JsonSerializer.Deserialize<LayoutResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(["open-fake", "shot"], layout.Actions.Select(a => a.Id).Order());
        Assert.DoesNotContain(_app, body, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd+shift+4", body, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", body, StringComparison.Ordinal);
    }

    public void Dispose() => _host.Dispose();
}

public sealed class AdminTests : IDisposable
{
    private readonly TestHost _host = new();

    [Fact]
    public async Task ADeviceKeyNeverOpensAdminRoutes()
    {
        var (phone, _, _) = _host.Enrolled("ADMIN-X");
        phone.DefaultRequestHeaders.Add("X-DeskPanel-Admin", "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/admin/api/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task AdminApiNeedsTheSessionAndTheHeader()
    {
        var admin = _host.AdminClient();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin/api/devices")).StatusCode);
        admin.DefaultRequestHeaders.Remove("X-DeskPanel-Admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/admin/api/devices")).StatusCode);
    }

    [Fact]
    public async Task TheLoginLinkWorksOnceAndSetsAStrictHttpOnlyCookie()
    {
        _ = _host.Services;
        var link = (await File.ReadAllTextAsync(_host.Admin.LinkPath)).Trim();
        var path = new Uri(link).PathAndQuery;
        var first = await _host.Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        var cookie = Assert.Single(first.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/admin", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client().GetAsync(path)).StatusCode); // used
        Assert.NotEqual(link, (await File.ReadAllTextAsync(_host.Admin.LinkPath)).Trim());          // replaced
    }

    [Fact]
    public async Task AWrongTokenBurnsTheCurrentLink()
    {
        _ = _host.Services;
        var link = (await File.ReadAllTextAsync(_host.Admin.LinkPath)).Trim();
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client().GetAsync("/admin/login?t=" + new string('x', 43))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client().GetAsync(new Uri(link).PathAndQuery)).StatusCode);
    }

    [Fact]
    public void TheAdminLinkFileIsOwnerOnly()
    {
        _ = _host.Services;
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_host.Admin.LinkPath));
    }

    [Fact]
    public async Task RevokingAPhoneEndsItsAccessAndItsGrants()
    {
        var (phone, device, _) = _host.Enrolled("REVOKE-ME");
        _host.Permissions.Set(device.Id, [Permission.MediaControl]);
        var admin = _host.AdminClient();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/admin/api/devices/{device.Id}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/v1/state")).StatusCode);
        Assert.Empty(_host.Permissions.For(device.Id));
    }

    [Fact]
    public async Task OnlyKnownPermissionsCanBeGranted()
    {
        var (_, device, _) = _host.Enrolled("GRANTS");
        var admin = _host.AdminClient();
        var r = await admin.PutAsJsonAsync($"/admin/api/devices/{device.Id}/permissions",
            new { permissions = new[] { Permission.MediaControl, "shell.command.execute" } });
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal([Permission.MediaControl], body.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task AdminPagesSendAStrictContentSecurityPolicy()
    {
        var r = await _host.AdminClient().GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("script-src 'self'", r.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    public void Dispose() => _host.Dispose();
}

public sealed class KeySpecAndActionTests
{
    [Theory]
    [InlineData("36:68: execution error: System Events got an error: osascript is not allowed to send keystrokes. (1002)", true)] // seen on this Mac
    [InlineData("execution error: System Events got an error: osascript is not allowed assistive access. (-1719)", true)]
    [InlineData("execution error: Can't get application \"Nope\". (-1728)", false)]
    public void AMissingAccessibilityPermissionIsRecognised(string stderr, bool expected) =>
        Assert.Equal(expected, MacControls.NeedsAccessibility(stderr));

    [Theory]
    [InlineData("cmd+shift+4", "cmd+shift+4", "key code 21 using {command down, shift down}")]
    [InlineData("Shift + Command + 4", "cmd+shift+4", "key code 21 using {command down, shift down}")]
    [InlineData("option+space", "alt+space", "key code 49 using {option down}")]
    [InlineData("f5", "f5", "key code 96")]
    public void CombinationsAreParsedFromTheTable(string spec, string canonical, string script)
    {
        var k = KeySpec.Parse(spec)!;
        Assert.Equal(canonical, k.Canonical);
        Assert.Equal(script, k.AppleScript);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cmd")]                       // no key
    [InlineData("cmd+a+b")]                   // two keys
    [InlineData("cmd+cmd+a")]
    [InlineData("rm -rf ~")]
    [InlineData("cmd+\" & do shell script \"x")]
    public void AnythingElseIsRejected(string spec) => Assert.Null(KeySpec.Parse(spec));

    [Fact]
    public void LaunchTargetsMustBeExistingAppBundles()
    {
        var dir = Directory.CreateTempSubdirectory("dp-apps-").FullName;
        var app = Directory.CreateDirectory(Path.Combine(dir, "Real.app")).FullName;
        try
        {
            Assert.Null(ActionStore.Validate(new ActionDef("ok", "OK", "launch", app)));
            Assert.NotNull(ActionStore.Validate(new ActionDef("rel", "Rel", "launch", "Real.app")));
            Assert.NotNull(ActionStore.Validate(new ActionDef("missing", "Missing", "launch", Path.Combine(dir, "Nope.app"))));
            Assert.NotNull(ActionStore.Validate(new ActionDef("bin", "Bin", "launch", "/bin/sh")));
            Assert.NotNull(ActionStore.Validate(new ActionDef("dots", "Dots", "launch", app + "/../Real.app")));
            Assert.NotNull(ActionStore.Validate(new ActionDef("cmd", "Cmd", "shell", "rm -rf ~")));
            Assert.NotNull(ActionStore.Validate(new ActionDef("Bad Id", "x", "keys", "cmd+a")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
