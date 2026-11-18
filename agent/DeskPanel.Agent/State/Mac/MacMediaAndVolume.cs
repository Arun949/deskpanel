using System.Diagnostics;
using System.Globalization;
using DeskPanel.Agent.Core;

namespace DeskPanel.Agent.State.Mac;

/// <summary>
/// Now playing from Spotify or Music through AppleScript. Only an app that is already running is asked, so the agent never
/// launches one, and macOS never shows "Where is Spotify?". The first query triggers macOS's Automation permission prompt.
/// (nowplaying-cli / MediaRemote stopped working for unentitled apps in macOS 15.4, so it is not used.)
/// </summary>
public sealed class MacMediaSource(IToolRunner tools) : IMediaSource
{
    // Fixed scripts: nothing from outside is ever spliced into AppleScript source.
    private static readonly (string App, string Process, string Script)[] Players =
    [
        ("Spotify", "Spotify", """
            tell application "Spotify"
              set st to player state as string
              if st is "stopped" then return "stopped"
              set t to current track
              return st & tab & (name of t) & tab & (artist of t) & tab & (album of t) & tab & ((player position * 1000) as integer) & tab & (duration of t)
            end tell
            """),
        ("Music", "Music", """
            tell application "Music"
              set st to player state as string
              if st is "stopped" then return "stopped"
              set t to current track
              return st & tab & (name of t) & tab & (artist of t) & tab & (album of t) & tab & ((player position * 1000) as integer) & tab & (((duration of t) * 1000) as integer)
            end tell
            """),
    ];

    public async Task<MediaSnapshot?> ReadAsync(CancellationToken ct)
    {
        MediaSnapshot? paused = null;
        foreach (var (app, process, script) in Players)
        {
            if (!IsRunning(process)) continue;
            var r = await tools.RunAsync("/usr/bin/osascript", ["-e", script], TimeSpan.FromSeconds(3), ct);
            if (r.ExitCode != 0) continue; // not allowed (Automation permission) or no track
            var snap = Parse(app, r.Output);
            if (snap?.Playing == true) return snap; // a playing player wins over a paused one
            paused ??= snap;
        }
        return paused;
    }

    private static bool IsRunning(string name)
    {
        var ps = Process.GetProcessesByName(name);
        try { return ps.Length > 0; }
        finally { foreach (var p in ps) p.Dispose(); }
    }

    /// <summary>Parses "state\ttitle\tartist\talbum\tpositionMs\tdurationMs". Text is untrusted (track metadata): length-capped.</summary>
    public static MediaSnapshot? Parse(string app, string output)
    {
        var f = output.TrimEnd('\r', '\n').Split('\t');
        if (f.Length < 6 || f[0] is not ("playing" or "paused")) return null;
        return new MediaSnapshot(app, f[0], Text(f[1]), Text(f[2]), Text(f[3]), Ms(f[4]), Ms(f[5]));
    }

    private static string? Text(string s)
    {
        s = s.Trim();
        return s.Length == 0 || s == "missing value" ? null : (s.Length > 200 ? s[..200] : s);
    }

    private static long? Ms(string s) =>
        long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;
}

/// <summary>Output volume and mute through AppleScript ("missing value" when the device has no software volume).</summary>
public sealed class MacVolumeSource(IToolRunner tools) : IVolumeSource
{
    private const string Script = "set v to get volume settings\nreturn ((output volume of v) as string) & \",\" & ((output muted of v) as string)";

    public async Task<VolumeSnapshot?> ReadAsync(CancellationToken ct)
    {
        var r = await tools.RunAsync("/usr/bin/osascript", ["-e", Script], TimeSpan.FromSeconds(3), ct);
        return r.ExitCode == 0 ? Parse(r.Output) : null;
    }

    public static VolumeSnapshot Parse(string output)
    {
        var parts = output.Trim().Split(',');
        int? level = parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) && l is >= 0 and <= 100 ? l : null;
        bool? muted = parts.Length > 1 ? parts[1] switch { "true" => true, "false" => false, _ => null } : null;
        return new VolumeSnapshot(level, muted);
    }
}
