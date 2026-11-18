using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskPanel.Agent.State.Windows;

/// <summary>
/// CPU and memory from kernel32 (no process started), network from the adapter counters. Only the methods that call
/// Windows are marked Windows-only, so the pure math is tested on every OS.
/// </summary>
public sealed partial class WindowsSystemMetrics : ISystemMetrics
{
    private const double Gb = 1024d * 1024 * 1024;
    private readonly NetworkCounter _net = new();
    private (long Idle, long Total)? _prev;

    [SupportedOSPlatform("windows")]
    public MetricsSnapshot Sample()
    {
        double? cpu = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var now = (idle, kernel + user); // kernel time includes idle time
            if (_prev is { } p) cpu = CpuPercent(p, now);
            _prev = now;
        }
        double? used = null, total = null;
        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            used = Math.Round((mem.TotalPhys - mem.AvailPhys) / Gb, 1);
            total = Math.Round(mem.TotalPhys / Gb, 1);
        }
        var (down, up) = _net.Sample();
        return new MetricsSnapshot(cpu, used, total, down, up);
    }

    /// <summary>% busy between two (idle, kernel+user) readings in 100 ns units. Pure, so tested on every OS.</summary>
    public static double? CpuPercent((long Idle, long Total) before, (long Idle, long Total) after)
    {
        var total = after.Total - before.Total;
        var idle = after.Idle - before.Idle;
        if (total <= 0 || idle < 0) return null;
        return Math.Round(Math.Clamp(100.0 * (total - idle) / total, 0, 100), 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}
