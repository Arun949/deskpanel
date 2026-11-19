using System.Net;
using DeskPanel.Agent.Admin;
using DeskPanel.Agent.Api;
using DeskPanel.Agent.Control;
using DeskPanel.Agent.Core;
using DeskPanel.Agent.Enrolment;
using DeskPanel.Agent.Layout;
using DeskPanel.Agent.Security;
using DeskPanel.Agent.Security.Wifi;
using DeskPanel.Agent.State;
using DeskPanel.Agent.Control.Windows;
using DeskPanel.Agent.State.Mac;
using DeskPanel.Agent.State.Windows;

CliRequest request;
try { request = CliRequest.Parse(args, Environment.GetEnvironmentVariable("DESKPANEL_PORT")); }
catch (ArgumentException e)
{
    Console.Error.WriteLine($"deskpanel-agent: {e.Message}\n\n{CliRequest.Usage}");
    return 2;
}
if (CliRequest.Run(request, Console.Out) is { } exitCode) return exitCode;
var options = request.Options;
if (request.Background) BackgroundMode.Enter(new AgentPaths());

// no other command-line configuration (no --urls and the like): the arguments above are the agent's whole command line
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [.. request.HostArgs], ContentRootPath = AppContext.BaseDirectory });

// the Wi-Fi listener's endpoint appears and disappears through this (docs/design/WIFI.md §4)
var lanEndpoint = new LanEndpointSource();
var lanConfig = new ConfigurationBuilder().Add(lanEndpoint).Build();

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 64 * 1024;
    k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
    k.Limits.MaxRequestLineSize = 8 * 1024;
    // Loopback (USB via adb reverse, the settings page): plain HTTP, never on the network.
    k.Listen(IPAddress.Loopback, k.ApplicationServices.GetRequiredService<AgentOptions>().Port);
    // Wi-Fi: a separate HTTPS listener, only while a phone is allowed on it (WifiListener)
    WifiListener.Configure(k, lanConfig);
});

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
// Kestrel logs harmless "fail" entries on macOS whenever a TLS client disconnects (W0 finding 4)
builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Critical);

var s = builder.Services;
s.AddSingleton(options);
s.AddSingleton<AgentRun>();
s.AddSingleton(_ => new AgentPaths());
s.AddSingleton<IToolRunner, ToolRunner>();

// security
s.AddSingleton<DeviceStore>();
s.AddSingleton<IDeviceAuthenticator, DeviceKeyAuthenticator>();
s.AddSingleton(TimeProvider.System);
s.AddSingleton<AdminSessions>();
s.AddSingleton<IAdminAuthenticator>(sp => sp.GetRequiredService<AdminSessions>());   // settings-page session cookie
s.AddSingleton<PermissionStore>();

// Wi-Fi (off until a phone is allowed on it in the settings page)
s.AddSingleton(lanEndpoint);
s.AddSingleton(LanPolicy.Default);
s.AddSingleton(sp => WifiSettings.Load(sp.GetRequiredService<AgentPaths>(), sp.GetRequiredService<AgentOptions>()));
s.AddSingleton<WifiIdentity>(); // the key file is only made when first needed
s.AddSingleton<WifiListener>();
s.AddHostedService(sp => sp.GetRequiredService<WifiListener>());

// controls: every request goes through ControlGate (permission, layout, rate limit, idempotency, audit)
s.AddSingleton<ActionStore>();
s.AddSingleton<AuditLog>();
s.AddSingleton<ControlGate>();
s.AddSingleton(sp => new Adb(sp.GetRequiredService<IToolRunner>()));
s.AddHostedService<AdbEnroller>(); // USB enrolment (adb is the root of trust)

// Platform adapters. Each platform provides: metrics, media (read + control), volume (read + control), app launch,
// key presses, the enrolment question and the app picker. Anything missing is "unsupported", never "allowed".
if (OperatingSystem.IsMacOS())
{
    s.AddSingleton<ISystemMetrics, MacSystemMetrics>();
    s.AddSingleton<IMediaSource, MacMediaSource>();
    s.AddSingleton<IVolumeSource, MacVolumeSource>();
    s.AddSingleton<MacControls>();
    s.AddSingleton<IMediaControl>(sp => sp.GetRequiredService<MacControls>());
    s.AddSingleton<IVolumeControl>(sp => sp.GetRequiredService<MacControls>());
    s.AddSingleton<IAppLauncher>(sp => sp.GetRequiredService<MacControls>());
    s.AddSingleton<IKeySender>(sp => sp.GetRequiredService<MacControls>());
    s.AddSingleton<IEnrolPrompt, MacEnrolPrompt>();
    s.AddSingleton<IAppPicker, MacAppPicker>();
}
else if (OperatingSystem.IsWindows())
{
    s.AddSingleton<ISystemMetrics, WindowsSystemMetrics>();
    var audio = new WindowsAudio(); // created here, inside the Windows check (the platform analyser follows it)
    s.AddSingleton<IVolumeSource>(audio);
    s.AddSingleton<IVolumeControl>(audio);
#if WINDOWS
    var media = new WindowsMedia();
    s.AddSingleton<IMediaSource>(media);
    s.AddSingleton<IMediaControl>(media);
#else
    s.AddSingleton<IMediaSource>(new UnsupportedSources());   // built without the Windows target: no media session
    s.AddSingleton<IMediaControl>(new UnsupportedControls());
#endif
    var controls = new WindowsControls();
    s.AddSingleton<IAppLauncher>(controls);
    s.AddSingleton<IKeySender>(controls);
    s.AddSingleton<IEnrolPrompt, WindowsEnrolPrompt>();
    s.AddSingleton<IAppPicker, WindowsAppPicker>();
}
else
{
    var unsupported = new UnsupportedSources();
    var none = new UnsupportedControls();
    s.AddSingleton<ISystemMetrics>(unsupported);
    s.AddSingleton<IMediaSource>(unsupported);
    s.AddSingleton<IVolumeSource>(unsupported);
    s.AddSingleton<IMediaControl>(none);
    s.AddSingleton<IVolumeControl>(none);
    s.AddSingleton<IAppLauncher>(none);
    s.AddSingleton<IKeySender>(none);
    s.AddSingleton<IEnrolPrompt, DenyEnrolPrompt>();
    s.AddSingleton<IAppPicker, NoAppPicker>();
}
s.AddSingleton<StateHub>();
s.AddHostedService(sp => sp.GetRequiredService<StateHub>());
s.AddSingleton<LayoutStore>();

var app = builder.Build();

app.Use((context, next) =>
{
    var h = context.Response.Headers;
    h.CacheControl = "no-store";
    h.XContentTypeOptions = "nosniff";
    h.XFrameOptions = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path.StartsWithSegments("/admin"))
        h.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
    return next(context);
});
app.UseMiddleware<BrowserGuard>();   // keep browsers out (Host, Origin, Sec-Fetch-Site)
app.UseRouting();
app.UseMiddleware<TrustGate>();      // per-route trust class: Public / Device / Admin, fail closed
app.MapDeskPanel();
app.MapControls();
app.MapAdmin();

var run = app.Services.GetRequiredService<AgentRun>();
var home = app.Services.GetRequiredService<AgentPaths>().Home;
var adminLink = app.Services.GetRequiredService<AdminSessions>().LinkPath;
app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("DeskPanel agent {Version} (run {RunId}) listening on http://127.0.0.1:{Port}; data in {Home}",
        run.Version, run.RunId, options.Port, home);
    // the link itself is never logged: logs get shared, the owner-only file does not
    app.Logger.LogInformation("Settings: run `deskpanel-agent settings` (opens the one-time link in {AdminLink})", adminLink);
});

app.Run();
return 0;

/// <summary>Visible to the in-process API tests (WebApplicationFactory).</summary>
public partial class Program;
