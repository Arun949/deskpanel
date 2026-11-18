using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskPanel.Agent.Control;

namespace DeskPanel.Agent.State.Windows;

/// <summary>
/// Master volume and mute of the default output device through Core Audio (IAudioEndpointVolume), in process: no helper
/// program, no extra library. The device is looked up on every call, so plugging in headphones is followed at once.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAudio : IVolumeSource, IVolumeControl
{
    private static Guid _context = Guid.Empty;

    public Task<VolumeSnapshot?> ReadAsync(CancellationToken ct) => Task.FromResult(Use(v =>
    {
        Check(v.GetMasterVolumeLevelScalar(out var level));
        Check(v.GetMute(out var muted));
        return new VolumeSnapshot((int)Math.Round(level * 100), muted);
    }));

    public Task<ControlResult> SetAsync(int? level, bool? muted, CancellationToken ct)
    {
        if (level is null && muted is null) return Task.FromResult(new ControlResult(false, "nothing-to-set"));
        var ok = Use(v =>
        {
            if (level is { } l) Check(v.SetMasterVolumeLevelScalar(Math.Clamp(l, 0, 100) / 100f, ref _context));
            if (muted is { } m) Check(v.SetMute(m, ref _context));
            return new VolumeSnapshot(level, muted);
        }) is not null;
        return Task.FromResult(ok ? new ControlResult(true) : new ControlResult(false, "no-audio-device"));
    }

    private static T? Use<T>(Func<IAudioEndpointVolume, T> action) where T : class
    {
        object? enumerator = null, device = null, endpoint = null;
        try
        {
            enumerator = new MMDeviceEnumeratorComObject();
            if (((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(ERender, EMultimedia, out var dev) != 0) return null;
            device = dev;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iid, ClsctxAll, IntPtr.Zero, out endpoint) != 0) return null;
            return action((IAudioEndpointVolume)endpoint);
        }
        catch (COMException) { return null; } // no output device (e.g. a server without audio)
        finally
        {
            foreach (var o in new[] { endpoint, device, enumerator })
                if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
    }

    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    private const int ERender = 0, EMultimedia = 1, ClsctxAll = 0x17;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    // Methods in vtable order; only the ones used are called.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
