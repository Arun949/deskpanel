using DeskPanel.Agent.Control;
using DeskPanel.Agent.Layout;
using DeskPanel.Agent.Security;
using DeskPanel.Agent.State;

namespace DeskPanel.Agent.Api;

/// <summary>
/// Device routes that change something. All go through <see cref="ControlGate"/>. Parameters are in the query string
/// (the Android proxy forwards no bodies), and none of them is a command, path, URL or text.
/// </summary>
public static class ControlEndpoints
{
    private static readonly HashSet<string> MediaOps = ["play-pause", "next", "prev"];

    public static void MapControls(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1").WithTrust(TrustClass.Device).OverLan(); // the phone's controls work over Wi-Fi too

        v1.MapPost("/media/{op}", async (string op, HttpContext ctx, ControlGate gate, IMediaControl media, StateHub hub, LayoutStore layout) =>
            !MediaOps.Contains(op)
                ? Results.NotFound()
                : await gate.RunAsync(ctx, $"media.{op}", Permission.MediaControl, layout.Has("media-controls"),
                    ct => media.SendAsync(op, hub.Current.Media?.App, ct)));

        v1.MapPost("/volume", async (int? level, bool? mute, HttpContext ctx, ControlGate gate, IVolumeControl volume, LayoutStore layout) =>
            level is null && mute is null || level is < 0 or > 100
                ? Results.BadRequest(new { error = "give level=0..100 and/or mute=true|false" })
                : await gate.RunAsync(ctx, level is null ? (mute == true ? "volume.mute" : "volume.unmute") : "volume.set",
                    Permission.VolumeControl, layout.Has("volume"), ct => volume.SetAsync(level, mute, ct)));

        v1.MapPost("/actions/{id}", (string id, HttpContext ctx, ControlGate gate, ActionStore actions, LayoutStore layout,
            IAppLauncher launcher, IKeySender keys) =>
        {
            var action = actions.Get(id);
            if (action is null) return Task.FromResult(gate.NotFound(ctx, $"action.{(id.Length > 40 ? id[..40] : id)}"));
            return gate.RunAsync(ctx, $"action.{action.Id}", action.Permission, layout.ReferencedActions().Contains(action.Id),
                ct => action.Kind == "launch"
                    ? launcher.LaunchAsync(action.Target, ct)
                    : keys.SendAsync(KeySpec.Parse(action.Target)!, ct));
        });
    }
}
