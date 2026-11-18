using DeskPanel.Agent.Admin;
using DeskPanel.Agent.Control;
using DeskPanel.Agent.Core;
using DeskPanel.Agent.Layout;
using DeskPanel.Agent.Enrolment;
using DeskPanel.Agent.Security;
using DeskPanel.Agent.State;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DeskPanel.Agent.Tests;

/// <summary>
/// The real app with a throw-away data folder, no adb loop (it could pop enrolment dialogs) and fixed state sources.
/// </summary>
public sealed class TestHost : WebApplicationFactory<Program>
{
    public string Home { get; } = Directory.CreateTempSubdirectory("deskpanel-test-").FullName;
    public FakeSources Sources { get; } = new();
    public FakeControls Controls { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(s =>
    {
        s.RemoveAll<AgentPaths>();
        s.AddSingleton(new AgentPaths(Home));
        foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AdbEnroller)).ToList())
            s.Remove(d);
        s.RemoveAll<IEnrolPrompt>();
        s.AddSingleton<IEnrolPrompt, DenyEnrolPrompt>();
        s.RemoveAll<ISystemMetrics>();
        s.RemoveAll<IMediaSource>();
        s.RemoveAll<IVolumeSource>();
        s.AddSingleton<ISystemMetrics>(Sources);
        s.AddSingleton<IMediaSource>(Sources);
        s.AddSingleton<IVolumeSource>(Sources);
        s.RemoveAll<IMediaControl>(); s.AddSingleton<IMediaControl>(Controls);
        s.RemoveAll<IVolumeControl>(); s.AddSingleton<IVolumeControl>(Controls);
        s.RemoveAll<IAppLauncher>(); s.AddSingleton<IAppLauncher>(Controls);
        s.RemoveAll<IKeySender>(); s.AddSingleton<IKeySender>(Controls);
        s.RemoveAll<IAppPicker>(); s.AddSingleton<IAppPicker, NoAppPicker>();
    });

    public PermissionStore Permissions => Services.GetRequiredService<PermissionStore>();
    public ActionStore Actions => Services.GetRequiredService<ActionStore>();
    public AuditLog Audit => Services.GetRequiredService<AuditLog>();
    public AdminSessions Admin => Services.GetRequiredService<AdminSessions>();

    /// <summary>Writes layout.json (the store reloads on the next request).</summary>
    public void SetLayout(params WidgetConfig[] widgets)
    {
        _ = Services; // the host must exist so AgentPaths is in use
        File.WriteAllText(Path.Combine(Home, "layout.json"),
            System.Text.Json.JsonSerializer.Serialize(new LayoutDoc(1, widgets), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        File.SetLastWriteTimeUtc(Path.Combine(Home, "layout.json"), DateTime.UtcNow.AddSeconds(Interlocked.Increment(ref _layoutTick)));
    }
    private static int _layoutTick;

    /// <summary>A client carrying a valid admin session cookie.</summary>
    public HttpClient AdminClient()
    {
        var token = File.ReadAllText(Admin.LinkPath).Trim().Split("?t=")[1];
        var session = Admin.Redeem(token) ?? throw new InvalidOperationException("no session");
        var client = Client();
        client.DefaultRequestHeaders.Add("Cookie", $"{AdminSessions.Cookie}={session}");
        client.DefaultRequestHeaders.Add(AdminEndpoints.Header, "1");
        return client;
    }

    public DeviceStore Devices => Services.GetRequiredService<DeviceStore>();
    public StateHub Hub => Services.GetRequiredService<StateHub>();

    public HttpClient Client(string baseAddress = "http://localhost/") =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(baseAddress), AllowAutoRedirect = false });

    /// <summary>A client that sends a freshly enrolled device's key.</summary>
    public (HttpClient Client, EnrolledDevice Device, string Key) Enrolled(string serial = "SERIAL1")
    {
        var (device, key) = Devices.Enrol(serial, "Pixel Test");
        var client = Client();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Device {device.Id}.{key}");
        return (client, device, key);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Home, recursive: true); } catch (IOException) { }
    }
}

public sealed class FakeSources : ISystemMetrics, IMediaSource, IVolumeSource
{
    public MetricsSnapshot Metrics { get; set; } = new(12.5, 8.1, 16, 1000, 200);
    public MediaSnapshot? Media { get; set; } = new("Spotify", "playing", "Song", "Artist", "Album", 1000, 200000);
    public VolumeSnapshot Volume { get; set; } = new(40, false);

    public MetricsSnapshot Sample() => Metrics;
    public Task<MediaSnapshot?> ReadAsync(CancellationToken ct) => Task.FromResult(Media);
    Task<VolumeSnapshot?> IVolumeSource.ReadAsync(CancellationToken ct) => Task.FromResult<VolumeSnapshot?>(Volume);
}

public sealed class FakeControls : IMediaControl, IVolumeControl, IAppLauncher, IKeySender
{
    public List<string> Calls { get; } = [];
    public Task<ControlResult> SendAsync(string op, string? app, CancellationToken ct) { lock (Calls) Calls.Add($"media:{op}"); return Ok; }
    public Task<ControlResult> SetAsync(int? level, bool? muted, CancellationToken ct) { lock (Calls) Calls.Add($"volume:{level}:{muted}"); return Ok; }
    public Task<ControlResult> LaunchAsync(string appPath, CancellationToken ct) { lock (Calls) Calls.Add($"launch:{appPath}"); return Ok; }
    public Task<ControlResult> SendAsync(KeySpec keys, CancellationToken ct) { lock (Calls) Calls.Add($"keys:{keys.Canonical}"); return Ok; }
    private static readonly Task<ControlResult> Ok = Task.FromResult(new ControlResult(true));
}

/// <summary>Scripted helper-program results, matched by the joined argument list.</summary>
public sealed class FakeTools : IToolRunner
{
    public List<string> Calls { get; } = [];
    public Func<IReadOnlyList<string>, ToolResult> Respond { get; set; } = _ => new ToolResult(0, "");

    public Task<ToolResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add(string.Join(' ', args));
        return Task.FromResult(Respond(args));
    }
}
