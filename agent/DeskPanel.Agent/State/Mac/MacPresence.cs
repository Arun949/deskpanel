using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskPanel.Agent.State.Mac;

/// <summary>
/// Is any app using the camera or the microphone? Asked of macOS directly (no helper process, no permission prompt):
///  - camera: CoreMediaIO, "is this device running somewhere" for every video device;
///  - mic: CoreAudio's per-process "is running input" (macOS 14.4+), else "running somewhere" on input devices.
///    macOS's own always-on listeners (the "Hey Siri" wake-word service) keep the mic open all the time; like the
///    system's orange mic dot, they don't count (found in the real-phone test: the light stayed on after a call).
/// Only yes/no comes back; DeskPanel never opens the camera or the mic itself.
/// </summary>
public sealed partial class MacPresence : IPresenceSource
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreMediaIO = "/System/Library/Frameworks/CoreMediaIO.framework/CoreMediaIO";
    private const uint SystemObject = 1;
    private static readonly uint Global = Code("glob"), Input = Code("inpt");

    [SupportedOSPlatform("macos")]
    public PresenceSnapshot? Read() => new(CameraInUse(), MicInUse());

    private static bool? CameraInUse()
    {
        var devices = CmioIds(SystemObject, new Address(Code("dev#"), Global, 0));
        if (devices is null) return null;
        foreach (var device in devices)
            if (CmioUInt(device, new Address(Code("gone"), Global, 0)) is > 0) return true;
        return false;
    }

    private static bool? MicInUse()
    {
        // precise: does any process capture audio right now?
        var processes = AudioIds(SystemObject, new Address(Code("prs#"), Global, 0));
        if (processes is { Length: > 0 })
        {
            foreach (var process in processes)
            {
                if (AudioUInt(process, new Address(Code("piri"), Global, 0)) is not > 0) continue;
                var pid = AudioUInt(process, new Address(Code("ppid"), Global, 0));
                if (pid is { } p && IsAlwaysListening(ProcessName((int)p))) continue;
                return true;
            }
            return false;
        }
        // older macOS: input devices that are running (a headset playing music can make this say yes)
        var devices = AudioIds(SystemObject, new Address(Code("dev#"), Global, 0));
        if (devices is null) return null;
        foreach (var device in devices)
        {
            if (!HasInput(device)) continue;
            if (AudioUInt(device, new Address(Code("gone"), Global, 0)) is > 0) return true;
        }
        return false;
    }

    private static bool HasInput(uint device)
    {
        var address = new Address(Code("stm#"), Input, 0);
        return AudioObjectGetPropertyDataSize(device, ref address, 0, 0, out var size) == 0 && size > 0;
    }

    private static uint[]? AudioIds(uint obj, Address address)
    {
        if (AudioObjectGetPropertyDataSize(obj, ref address, 0, 0, out var size) != 0) return null;
        var ids = new uint[size / sizeof(uint)];
        if (ids.Length == 0) return ids;
        var bytes = size;
        return AudioObjectGetPropertyData(obj, ref address, 0, 0, ref bytes, ids) == 0 ? ids[..(int)(bytes / sizeof(uint))] : null;
    }

    private static uint? AudioUInt(uint obj, Address address)
    {
        var value = new uint[1];
        uint size = sizeof(uint);
        return AudioObjectGetPropertyData(obj, ref address, 0, 0, ref size, value) == 0 ? value[0] : null;
    }

    private static uint[]? CmioIds(uint obj, Address address)
    {
        if (CMIOObjectGetPropertyDataSize(obj, ref address, 0, 0, out var size) != 0) return null;
        var ids = new uint[size / sizeof(uint)];
        if (ids.Length == 0) return ids;
        return CMIOObjectGetPropertyData(obj, ref address, 0, 0, size, out var used, ids) == 0 ? ids[..(int)(used / sizeof(uint))] : null;
    }

    private static uint? CmioUInt(uint obj, Address address)
    {
        var value = new uint[1];
        return CMIOObjectGetPropertyData(obj, ref address, 0, 0, sizeof(uint), out _, value) == 0 ? value[0] : null;
    }

    /// <summary>macOS services that listen for a wake word all the time; they don't mean "you're on a call".</summary>
    internal static bool IsAlwaysListening(string? processName) => processName is "corespeechd";

    /// <summary>The executable's file name for a process id, or null.</summary>
    private static unsafe string? ProcessName(int pid)
    {
        var buffer = stackalloc byte[4096];
        var n = proc_pidpath(pid, buffer, 4096);
        return n > 0 ? Path.GetFileName(System.Text.Encoding.UTF8.GetString(buffer, n)) : null;
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    private static unsafe partial int proc_pidpath(int pid, byte* buffer, uint size);

    /// <summary>A four-character code such as 'gone' as Apple's APIs expect it.</summary>
    internal static uint Code(string fourCc) => (uint)(fourCc[0] << 24 | fourCc[1] << 16 | fourCc[2] << 8 | fourCc[3]);

    [StructLayout(LayoutKind.Sequential)]
    private struct Address(uint selector, uint scope, uint element)
    {
        public uint Selector = selector, Scope = scope, Element = element;
    }

    [LibraryImport(CoreAudio)]
    private static partial int AudioObjectGetPropertyDataSize(uint obj, ref Address address, uint qualifierSize, nint qualifier, out uint size);

    [LibraryImport(CoreAudio)]
    private static partial int AudioObjectGetPropertyData(uint obj, ref Address address, uint qualifierSize, nint qualifier, ref uint size, [Out] uint[] data);

    [LibraryImport(CoreMediaIO)]
    private static partial int CMIOObjectGetPropertyDataSize(uint obj, ref Address address, uint qualifierSize, nint qualifier, out uint size);

    [LibraryImport(CoreMediaIO)]
    private static partial int CMIOObjectGetPropertyData(uint obj, ref Address address, uint qualifierSize, nint qualifier, uint dataSize, out uint used, [Out] uint[] data);
}
