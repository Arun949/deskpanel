#if WINDOWS
using System.Runtime.Versioning;
using DeskPanel.Agent.Control;
using Windows.Media.Control;

namespace DeskPanel.Agent.State.Windows;

/// <summary>
/// Now playing and play/pause/next/previous through the Windows system media session (the same source as the volume
/// flyout): Spotify, browsers, the Media Player app and anything else that registers with it.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsMedia : IMediaSource, IMediaControl
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    private async Task<GlobalSystemMediaTransportControlsSession?> SessionAsync()
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return _manager.GetCurrentSession();
    }

    public async Task<MediaSnapshot?> ReadAsync(CancellationToken ct)
    {
        var session = await SessionAsync();
        if (session is null) return null;
        var playback = session.GetPlaybackInfo();
        var state = playback.PlaybackStatus switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
            _ => null,
        };
        if (state is null) return null;
        var props = await session.TryGetMediaPropertiesAsync();
        var timeline = session.GetTimelineProperties();
        var duration = timeline.EndTime - timeline.StartTime;
        return new MediaSnapshot(
            AppName(session.SourceAppUserModelId), state,
            Text(props?.Title), Text(props?.Artist), Text(props?.AlbumTitle),
            (long)timeline.Position.TotalMilliseconds,
            duration > TimeSpan.Zero ? (long)duration.TotalMilliseconds : null);
    }

    public async Task<ControlResult> SendAsync(string op, string? app, CancellationToken ct)
    {
        var session = await SessionAsync();
        if (session is null) return new(false, "no-player-running");
        var ok = op switch
        {
            "play-pause" => await session.TryTogglePlayPauseAsync(),
            "next" => await session.TrySkipNextAsync(),
            "prev" => await session.TrySkipPreviousAsync(),
            _ => false,
        };
        return ok ? new(true) : new(false, "failed");
    }

    /// <summary>"Spotify.exe" → "Spotify"; "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" → "Microsoft.ZuneMusic".</summary>
    internal static string AppName(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "Media";
        var name = id.Split('!')[0].Split('_')[0];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length > 40 ? name[..40] : name;
    }

    private static string? Text(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Length > 200 ? s[..200] : s;
}
#endif
