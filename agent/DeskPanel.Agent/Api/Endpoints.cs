using System.Text.Json;
using System.Text.RegularExpressions;
using DeskPanel.Agent.Core;
using DeskPanel.Agent.Layout;
using DeskPanel.Agent.Security;
using DeskPanel.Agent.State;

namespace DeskPanel.Agent.Api;

/// <summary>
/// Response body of <c>GET /api/v1/hello</c>. Public: identifies the agent. With <c>?d=&lt;device&gt;&amp;n=&lt;nonce&gt;</c>
/// it adds <see cref="Proof"/>, an HMAC only the real agent can compute, so the phone can tell it apart from an app
/// squatting on 127.0.0.1:8765 (Module 3).
/// </summary>
public sealed record HelloResponse(string App, int Api, string Version, string RunId, string? Proof = null);

/// <summary>Body of <c>GET /api/v1/state</c> and of each stream message. Sections are null when unchanged (stream) or unknown.</summary>
/// <param name="LayoutVersion">changes when layout.json changes, so the phone reloads its layout at once</param>
public sealed record StateMessage(string RunId, long Version, long Ts, bool Full,
    MetricsSnapshot? Metrics, MediaSnapshot? Media, VolumeSnapshot? Volume, string? LayoutVersion = null,
    PresenceSnapshot? Presence = null);

/// <summary>Body of <c>GET /api/v1/wifi</c>: where and how this phone reaches the agent over Wi-Fi.</summary>
public sealed record WifiInfo(bool Enabled, int? Port, string? Pin, IReadOnlyList<string> Addresses);

public sealed record LayoutResponse(int SchemaVersion, IReadOnlyList<WidgetConfig> Widgets, IReadOnlyList<Control.PublicAction> Actions);

public static partial class Endpoints
{
    public const int ApiVersion = 1;
    internal static readonly TimeSpan FullStateEvery = TimeSpan.FromSeconds(10);

    /// <summary>How often an open stream re-checks that its phone is still allowed (revoked → the stream ends).</summary>
    internal static readonly TimeSpan AccessCheckEvery = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Maps every route. Each one declares a <see cref="TrustClass"/>; see TrustGate.</summary>
    public static void MapDeskPanel(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1");

        v1.MapGet("/hello", Hello).WithTrust(TrustClass.Public).OverLan();

        v1.MapGet("/state", (StateHub hub, AgentRun run, LayoutStore layout) =>
        {
            hub.Touch();
            var s = hub.Current;
            return new StateMessage(run.RunId, s.Version, s.Ts, true, s.Metrics, s.Media, s.Volume, layout.Version, s.Presence);
        }).WithTrust(TrustClass.Device).OverLan();

        v1.MapGet("/stream", Stream).WithTrust(TrustClass.Device).OverLan();

        // the layout plus label/icon/kind of the actions its shortcuts widgets list; never an action's target
        v1.MapGet("/layout", (LayoutStore layout, Control.ActionStore actions) =>
        {
            var doc = layout.Get();
            var ids = LayoutStore.ActionIds(doc);
            return new LayoutResponse(doc.SchemaVersion, doc.Widgets,
                [.. actions.All().Where(a => ids.Contains(a.Id)).Select(a => new Control.PublicAction(a.Id, a.Label, a.Kind, a.Icon))]);
        }).WithTrust(TrustClass.Device).OverLan();

        // what this phone needs to reach the agent over Wi-Fi (asked over USB or Wi-Fi). The pin is public information;
        // a phone without Wi-Fi gets only "enabled: false", and the key file isn't even created for it.
        v1.MapGet("/wifi", (HttpContext ctx, DeviceStore devices, Security.Wifi.WifiSettings settings) =>
        {
            var device = devices.Active((string)ctx.Items[TrustGate.DeviceIdItem]!);
            if (device is not { Wifi: true }) return new WifiInfo(false, null, null, []);
            var identity = ctx.RequestServices.GetRequiredService<Security.Wifi.WifiIdentity>();
            return new WifiInfo(true, settings.Port, identity.Pin, Security.Wifi.LanAddresses.Current());
        }).WithTrust(TrustClass.Device).OverLan();
    }

    private static IResult Hello(string? d, string? n, AgentRun run, DeviceStore devices)
    {
        if (d is null && n is null) return Results.Ok(new HelloResponse("DeskPanel", ApiVersion, run.Version, run.RunId));
        if (d is null || n is null || !DeviceIdRe().IsMatch(d) || !NonceRe().IsMatch(n)) return Results.BadRequest();
        // an unknown device gets no proof; the phone then shows "not paired with this computer"
        var proof = devices.Active(d) is { } device ? DeviceStore.Proof(device, n, run.RunId) : null;
        return Results.Ok(new HelloResponse("DeskPanel", ApiVersion, run.Version, run.RunId, proof));
    }

    /// <summary>
    /// Server-sent events. Each message: <c>id: &lt;runId&gt;-&lt;version&gt;</c>, <c>event: state</c>, and only the sections
    /// that changed since the last message on this connection, plus a full state every 10 s (also a keep-alive).
    /// A new runId tells the phone the agent restarted.
    /// </summary>
    private static async Task Stream(HttpContext ctx, StateHub hub, AgentRun run, LayoutStore layout, DeviceStore devices)
    {
        var ct = ctx.RequestAborted;
        var deviceId = (string)ctx.Items[TrustGate.DeviceIdItem]!; // set by TrustGate: this is a Device route
        // over Wi-Fi, the phone must also stay allowed on Wi-Fi with the key that opened this connection
        var wifiKey = Security.Wifi.WifiListener.IsLan(ctx) && ctx.Connection.ClientCertificate is { } cert
            ? Security.Wifi.WifiIdentity.SpkiPin(cert) : null;
        bool StillAllowed() => devices.Active(deviceId) is not null
            && (wifiKey is null || devices.ActiveOnWifi(wifiKey)?.Id == deviceId);
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        string? lastMetrics = null, lastMedia = null, lastVolume = null, lastPresence = null;
        long? lastFull = null; // null = nothing sent yet, so the first message is always full
        var version = -1L;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                hub.Touch();
                // wake at least every AccessCheckEvery, so a revoked phone loses its open stream within seconds
                var s = await hub.WaitForChangeAsync(version, AccessCheckEvery, ct);
                if (!StillAllowed()) return; // revoked, or (over Wi-Fi) Wi-Fi turned off for this phone
                var full = lastFull is not { } at || Environment.TickCount64 - at >= FullStateEvery.TotalMilliseconds;
                if (!full && s.Version == version) continue; // woke only to check access: nothing new to send
                string Ser<T>(T v) => JsonSerializer.Serialize(v, Json);
                var (m, md, v, p) = (Ser(s.Metrics), Ser(s.Media), Ser(s.Volume), Ser(s.Presence));
                var msg = new StateMessage(run.RunId, s.Version, s.Ts, full,
                    full || m != lastMetrics ? s.Metrics : null,
                    full || md != lastMedia ? s.Media : null,
                    full || v != lastVolume ? s.Volume : null,
                    layout.Version,
                    full || p != lastPresence ? s.Presence : null);
                // a section that became null (nothing playing) must still reach the phone: send a full message then
                if (!full && ((md != lastMedia && s.Media is null) || (m != lastMetrics && s.Metrics is null)
                              || (v != lastVolume && s.Volume is null) || (p != lastPresence && s.Presence is null)))
                    msg = msg with { Full = true, Metrics = s.Metrics, Media = s.Media, Volume = s.Volume, Presence = s.Presence };
                if (msg.Full) lastFull = Environment.TickCount64;
                (lastMetrics, lastMedia, lastVolume, lastPresence, version) = (m, md, v, p, s.Version);
                await ctx.Response.WriteAsync($"id: {run.RunId}-{s.Version}\nevent: state\ndata: {Ser(msg)}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { } // the phone went away
    }

    [GeneratedRegex("^[0-9a-f]{16}$")] private static partial Regex DeviceIdRe();
    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")] private static partial Regex NonceRe();
}
