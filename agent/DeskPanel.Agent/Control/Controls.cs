using System.Globalization;
using DeskPanel.Agent.Core;

namespace DeskPanel.Agent.Control;

public interface IMediaControl
{
    /// <param name="op">play-pause, next or prev</param>
    /// <param name="app">the player the phone sees (from state), or null for whichever is running</param>
    Task<ControlResult> SendAsync(string op, string? app, CancellationToken ct);
}

public interface IVolumeControl
{
    Task<ControlResult> SetAsync(int? level, bool? muted, CancellationToken ct);
}

public interface IAppLauncher
{
    Task<ControlResult> LaunchAsync(string appPath, CancellationToken ct);
}

public interface IKeySender
{
    Task<ControlResult> SendAsync(KeySpec keys, CancellationToken ct);
}

/// <summary>Platforms without controls yet: refuse plainly.</summary>
public sealed class UnsupportedControls : IMediaControl, IVolumeControl, IAppLauncher, IKeySender
{
    private static readonly Task<ControlResult> No = Task.FromResult(new ControlResult(false, "unsupported-on-this-computer"));
    public Task<ControlResult> SendAsync(string op, string? app, CancellationToken ct) => No;
    public Task<ControlResult> SetAsync(int? level, bool? muted, CancellationToken ct) => No;
    public Task<ControlResult> LaunchAsync(string appPath, CancellationToken ct) => No;
    public Task<ControlResult> SendAsync(KeySpec keys, CancellationToken ct) => No;
}

/// <summary>
/// macOS controls. Every AppleScript comes from a fixed table or is built only from validated numbers and key codes;
/// app paths go to /usr/bin/open as a single argument (no shell, no arguments for the app).
/// </summary>
public sealed class MacControls(IToolRunner tools) : IMediaControl, IVolumeControl, IAppLauncher, IKeySender
{
    private const string Osascript = "/usr/bin/osascript";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    private static readonly Dictionary<(string App, string Op), string> MediaScripts = new()
    {
        [("Spotify", "play-pause")] = "tell application \"Spotify\" to playpause",
        [("Spotify", "next")] = "tell application \"Spotify\" to next track",
        [("Spotify", "prev")] = "tell application \"Spotify\" to previous track",
        [("Music", "play-pause")] = "tell application \"Music\" to playpause",
        [("Music", "next")] = "tell application \"Music\" to next track",
        [("Music", "prev")] = "tell application \"Music\" to previous track",
    };

    public async Task<ControlResult> SendAsync(string op, string? app, CancellationToken ct)
    {
        // only a player that is already running is controlled; the agent never launches one
        var target = app is "Spotify" or "Music" && IsRunning(app) ? app
            : IsRunning("Spotify") ? "Spotify" : IsRunning("Music") ? "Music" : null;
        if (target is null) return new(false, "no-player-running");
        if (!MediaScripts.TryGetValue((target, op), out var script)) return new(false, "unknown-op");
        return Ok(await tools.RunAsync(Osascript, ["-e", script], Timeout, ct));
    }

    public async Task<ControlResult> SetAsync(int? level, bool? muted, CancellationToken ct)
    {
        var parts = new List<string>();
        if (level is { } l) parts.Add($"set volume output volume {Math.Clamp(l, 0, 100).ToString(CultureInfo.InvariantCulture)}");
        if (muted is { } m) parts.Add($"set volume output muted {(m ? "true" : "false")}");
        if (parts.Count == 0) return new(false, "nothing-to-set");
        return Ok(await tools.RunAsync(Osascript, ["-e", string.Join('\n', parts)], Timeout, ct));
    }

    public async Task<ControlResult> LaunchAsync(string appPath, CancellationToken ct)
    {
        if (!ActionStore.IsAppBundle(appPath)) return new(false, "app-missing");
        return Ok(await tools.RunAsync("/usr/bin/open", ["-a", appPath], Timeout, ct));
    }

    public async Task<ControlResult> SendAsync(KeySpec keys, CancellationToken ct)
    {
        var r = await tools.RunAsync(Osascript, ["-e", $"tell application \"System Events\" to {keys.AppleScript}"], Timeout, ct);
        if (r.ExitCode != 0 && NeedsAccessibility(r.Error)) return new(false, "needs-accessibility-permission");
        return Ok(r);
    }

    /// <summary>
    /// macOS refuses synthetic keys until the agent has the Accessibility permission. The wording differs by version:
    /// "(1002) … not allowed to send keystrokes" (recent), "-1719 … assistive access" (older), -25211.
    /// </summary>
    internal static bool NeedsAccessibility(string stderr) =>
        stderr.Contains("not allowed to send keystrokes", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("(1002)", StringComparison.Ordinal)
        || stderr.Contains("1719", StringComparison.Ordinal)
        || stderr.Contains("25211", StringComparison.Ordinal)
        || stderr.Contains("assistive", StringComparison.OrdinalIgnoreCase);

    private static ControlResult Ok(ToolResult r) => r.ExitCode == 0 ? new(true) : new(false, "failed");

    private static bool IsRunning(string name)
    {
        var ps = System.Diagnostics.Process.GetProcessesByName(name);
        try { return ps.Length > 0; }
        finally { foreach (var p in ps) p.Dispose(); }
    }
}
