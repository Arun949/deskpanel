using DeskPanel.Agent.Core;
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
    });

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
