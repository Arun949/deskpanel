using System.Net;
using DeskPanel.Agent.Admin;
using DeskPanel.Agent.Api;
using DeskPanel.Agent.Control;
using DeskPanel.Agent.Core;
using DeskPanel.Agent.Enrolment;
using DeskPanel.Agent.Layout;
using DeskPanel.Agent.Security;
using DeskPanel.Agent.State;
using DeskPanel.Agent.State.Mac;

var options = AgentOptions.From(args, Environment.GetEnvironmentVariable("DESKPANEL_PORT"));

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 64 * 1024;
    k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
    k.Limits.MaxRequestLineSize = 8 * 1024;
    // Loopback only. Wi-Fi comes later as a separate, TLS-only listener with its own allow-list.
    k.Listen(IPAddress.Loopback, options.Port);
});

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

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

// controls: every request goes through ControlGate (permission, layout, rate limit, idempotency, audit)
s.AddSingleton<ActionStore>();
s.AddSingleton<AuditLog>();
s.AddSingleton<ControlGate>();
if (OperatingSystem.IsMacOS())
{
    s.AddSingleton<MacControls>();
    s.AddSingleton<IMediaControl>(sp => sp.GetRequiredService<MacControls>());
    s.AddSingleton<IVolumeControl>(sp => sp.GetRequiredService<MacControls>());
    s.AddSingleton<IAppLauncher>(sp => sp.GetRequiredService<MacControls>());
    s.AddSingleton<IKeySender>(sp => sp.GetRequiredService<MacControls>());
    s.AddSingleton<IAppPicker, MacAppPicker>();
}
else
{
    var none = new UnsupportedControls();
    s.AddSingleton<IMediaControl>(none);
    s.AddSingleton<IVolumeControl>(none);
    s.AddSingleton<IAppLauncher>(none);
    s.AddSingleton<IKeySender>(none);
    s.AddSingleton<IAppPicker, NoAppPicker>();
}

// USB enrolment (adb is the root of trust)
s.AddSingleton(sp => new Adb(sp.GetRequiredService<IToolRunner>()));
if (OperatingSystem.IsMacOS()) s.AddSingleton<IEnrolPrompt, MacEnrolPrompt>();
else s.AddSingleton<IEnrolPrompt, DenyEnrolPrompt>();
s.AddHostedService<AdbEnroller>();

// live state
if (OperatingSystem.IsMacOS())
{
    s.AddSingleton<ISystemMetrics, MacSystemMetrics>();
    s.AddSingleton<IMediaSource, MacMediaSource>();
    s.AddSingleton<IVolumeSource, MacVolumeSource>();
}
else
{
    var unsupported = new UnsupportedSources();
    s.AddSingleton<ISystemMetrics>(unsupported);
    s.AddSingleton<IMediaSource>(unsupported);
    s.AddSingleton<IVolumeSource>(unsupported);
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
    app.Logger.LogInformation("Settings: open the one-time link in {AdminLink} (tools/admin.sh does it)", adminLink);
});

app.Run();

/// <summary>Visible to the in-process API tests (WebApplicationFactory).</summary>
public partial class Program;
