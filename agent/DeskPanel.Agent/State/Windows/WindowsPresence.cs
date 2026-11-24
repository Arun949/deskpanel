using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DeskPanel.Agent.State.Windows;

/// <summary>
/// Is any app using the camera or the microphone? Windows records this itself (the privacy indicator in the taskbar):
/// under CapabilityAccessManager\ConsentStore, an app whose LastUsedTimeStop is 0 is using the device right now.
/// Only yes/no comes back; no app names leave this class.
/// </summary>
public sealed class WindowsPresence : IPresenceSource
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    [SupportedOSPlatform("windows")]
    public PresenceSnapshot? Read() => new(InUse("webcam"), InUse("microphone"));

    [SupportedOSPlatform("windows")]
    private static bool? InUse(string capability)
    {
        bool? any = null;
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(Store + capability);
            if (key is null) continue;
            any ??= false;
            if (Active(key) || (key.OpenSubKey("NonPackaged") is { } nonPackaged && Active(nonPackaged, dispose: true))) return true;
        }
        return any;
    }

    /// <summary>True if any app below <paramref name="key"/> started using the device and hasn't stopped.</summary>
    [SupportedOSPlatform("windows")]
    private static bool Active(RegistryKey key, bool dispose = false)
    {
        try
        {
            foreach (var name in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(name);
                if (app is null) continue;
                if (IsActive(app.GetValue("LastUsedTimeStart"), app.GetValue("LastUsedTimeStop"))) return true;
            }
            return false;
        }
        finally { if (dispose) key.Dispose(); }
    }

    /// <summary>Started at some point and not stopped (Windows writes 0 for "still running"). Pure, so tested on every OS.</summary>
    public static bool IsActive(object? start, object? stop) => start is long s && s > 0 && stop is long e && e == 0;
}
