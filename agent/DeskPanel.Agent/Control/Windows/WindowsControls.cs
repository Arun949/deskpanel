using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskPanel.Agent.Control.Windows;

/// <summary>
/// Launching a chosen program and pressing key combinations on Windows. Programs start without the shell and without
/// arguments; keys go through SendInput from a fixed virtual-key table (no text from the phone is ever typed).
/// Windows will not deliver input to an elevated (administrator) window from this unelevated agent: by design.
/// </summary>
public sealed partial class WindowsControls : IAppLauncher, IKeySender
{
    [SupportedOSPlatform("windows")]
    public Task<ControlResult> LaunchAsync(string appPath, CancellationToken ct)
    {
        if (!ActionStore.IsLaunchTarget(appPath)) return Task.FromResult(new ControlResult(false, "app-missing"));
        var psi = new ProcessStartInfo(appPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(appPath)!,
        };
        try
        {
            using var _ = Process.Start(psi);
            return Task.FromResult(new ControlResult(true));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(new ControlResult(false, "failed"));
        }
    }

    [SupportedOSPlatform("windows")]
    public Task<ControlResult> SendAsync(KeySpec keys, CancellationToken ct)
    {
        var inputs = Inputs(keys);
        if (inputs is null) return Task.FromResult(new ControlResult(false, "unsupported-key"));
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        // fewer than requested: the foreground window is elevated, or the secure desktop (lock screen, UAC) is up
        return Task.FromResult(sent == inputs.Length ? new ControlResult(true) : new ControlResult(false, "blocked-by-windows"));
    }

    /// <summary>Modifiers down, key down, key up, modifiers up (in reverse). Null when a key has no Windows code.</summary>
    internal static Input[]? Inputs(KeySpec keys)
    {
        if (VirtualKey(keys.Key) is not { } vk) return null;
        var mods = keys.Modifiers.Select(m => Modifiers[m]).ToList();
        var list = new List<Input>();
        list.AddRange(mods.Select(m => Key(m, up: false)));
        list.Add(Key(vk, up: false));
        list.Add(Key(vk, up: true));
        list.AddRange(Enumerable.Reverse(mods).Select(m => Key(m, up: true)));
        return [.. list];
    }

    /// <summary>"cmd" maps to the Windows key, so a combination written for a Mac means the analogous thing.</summary>
    private static readonly Dictionary<string, ushort> Modifiers = new()
    {
        ["cmd"] = 0x5B /* VK_LWIN */, ["ctrl"] = 0xA2 /* VK_LCONTROL */, ["alt"] = 0xA4 /* VK_LMENU */, ["shift"] = 0xA0 /* VK_LSHIFT */,
    };

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.Ordinal)
    {
        ["return"] = 0x0D, ["enter"] = 0x0D, ["tab"] = 0x09, ["space"] = 0x20, ["delete"] = 0x08, ["backspace"] = 0x08,
        ["escape"] = 0x1B, ["esc"] = 0x1B, ["pageup"] = 0x21, ["pagedown"] = 0x22, ["end"] = 0x23, ["home"] = 0x24,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["-"] = 0xBD, ["="] = 0xBB, ["["] = 0xDB, ["]"] = 0xDD, [";"] = 0xBA, ["'"] = 0xDE, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF, ["`"] = 0xC0,
    };

    /// <summary>The Windows virtual-key code for a key name from <see cref="KeySpec"/>'s table.</summary>
    internal static ushort? VirtualKey(string key)
    {
        if (Named.TryGetValue(key, out var vk)) return vk;
        if (key.Length == 1 && key[0] is >= 'a' and <= 'z') return (ushort)char.ToUpperInvariant(key[0]); // VK_A..VK_Z
        if (key.Length == 1 && key[0] is >= '0' and <= '9') return key[0];                               // VK_0..VK_9
        if (key.Length is 2 or 3 && key[0] == 'f' && int.TryParse(key[1..], out var n) && n is >= 1 and <= 12) return (ushort)(0x70 + n - 1);
        return null;
    }

    private static Input Key(ushort vk, bool up) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        U = new InputUnion
        {
            Ki = new KeybdInput
            {
                Vk = vk,
                // navigation keys sit on the extended part of the keyboard
                Flags = (up ? 0x0002u : 0u) | (vk is >= 0x21 and <= 0x28 ? 0x0001u : 0u),
            },
        },
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input { public uint Type; public InputUnion U; }

    // The union must be as large as MOUSEINPUT so that sizeof(INPUT) matches what SendInput expects (40 bytes on x64).
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion { [FieldOffset(0)] public MouseInput Mi; [FieldOffset(0)] public KeybdInput Ki; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput { public int Dx, Dy; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeybdInput { public ushort Vk, Scan; public uint Flags, Time; public IntPtr ExtraInfo; }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);
}
