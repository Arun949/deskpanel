using System.Runtime.InteropServices;
using DeskPanel.Agent.Control;
using DeskPanel.Agent.Control.Windows;
using DeskPanel.Agent.State.Windows;

namespace DeskPanel.Agent.Tests;

/// <summary>The pure parts of the Windows adapters; they run on every OS (the system calls are covered by the CI smoke test).</summary>
public sealed class WindowsAdapterTests
{
    [Fact]
    public void CpuPercentUsesKernelTimeThatIncludesIdle() =>
        Assert.Equal(25.0, WindowsSystemMetrics.CpuPercent((Idle: 1000, Total: 2000), (Idle: 1750, Total: 3000))); // 250 busy of 1000

    [Fact]
    public void CpuPercentIsUnknownWithoutElapsedTime() =>
        Assert.Null(WindowsSystemMetrics.CpuPercent((5, 5), (5, 5)));

    [Theory]
    [InlineData("a", 0x41)]
    [InlineData("z", 0x5A)]
    [InlineData("0", 0x30)]
    [InlineData("f1", 0x70)]
    [InlineData("f12", 0x7B)]
    [InlineData("space", 0x20)]
    [InlineData("escape", 0x1B)]
    [InlineData("left", 0x25)]
    [InlineData("/", 0xBF)]
    public void EveryTableKeyHasAVirtualKey(string key, int vk) => Assert.Equal((ushort)vk, WindowsControls.VirtualKey(key));

    [Fact]
    public void AllKeysInTheSharedTableMapOnWindows()
    {
        // the same names KeySpec accepts must work on Windows, or an action saved on one OS fails on another
        string[] keys = ["a", "m", "9", "f6", "return", "enter", "tab", "space", "delete", "backspace", "escape", "esc",
            "left", "right", "up", "down", "home", "end", "pageup", "pagedown", "-", "=", "[", "]", ";", "'", ",", ".", "/", "`"];
        foreach (var k in keys)
        {
            Assert.NotNull(KeySpec.Parse(k));
            Assert.NotNull(WindowsControls.VirtualKey(k));
        }
    }

    [Fact]
    public void ACombinationPressesModifiersFirstAndReleasesThemLast()
    {
        var inputs = WindowsControls.Inputs(KeySpec.Parse("ctrl+shift+left")!)!;
        var seq = inputs.Select(i => (i.U.Ki.Vk, Up: (i.U.Ki.Flags & 2) != 0)).ToArray();
        Assert.Equal([(0xA2, false), (0xA0, false), (0x25, false), (0x25, true), (0xA0, true), (0xA2, true)],
            seq.Select(x => ((int)x.Vk, x.Up)));
        Assert.All(inputs.Where(i => i.U.Ki.Vk == 0x25), i => Assert.Equal(1u, i.U.Ki.Flags & 1)); // arrows are extended keys
    }

    [Fact]
    public void TheInputStructHasTheSizeSendInputExpects() =>
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<WindowsControls.Input>());
}
