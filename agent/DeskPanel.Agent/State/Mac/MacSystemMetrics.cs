using System.Runtime.InteropServices;

namespace DeskPanel.Agent.State.Mac;

/// <summary>
/// CPU and memory through Mach system calls, so nothing is launched once a second (PhoneBox started vm_stat every
/// second, Module 6). Memory "used" follows Activity Monitor: app memory + wired + compressed.
/// </summary>
public sealed partial class MacSystemMetrics : ISystemMetrics
{
    private const int HostCpuLoadInfo = 3;       // HOST_CPU_LOAD_INFO
    private const int HostVmInfo64 = 4;          // HOST_VM_INFO64
    private const int VmInfo64Count = 38;        // sizeof(vm_statistics64_data_t) / sizeof(integer_t)
    private const double Gb = 1024d * 1024 * 1024;

    // Looked up once (each call returns a port right) and lazily, so the pure math below also loads on Linux/Windows.
    private static uint? _host;
    private static uint Host => _host ??= mach_host_self();
    private readonly long _pageSize = SysctlLong("hw.pagesize") ?? 16384;
    private readonly long? _memTotal = SysctlLong("hw.memsize");
    private readonly NetworkCounter _net = new();
    private uint[]? _prevTicks;

    public MetricsSnapshot Sample()
    {
        var ticks = CpuTicks();
        double? cpu = ticks is not null && _prevTicks is not null ? CpuPercent(_prevTicks, ticks) : null;
        _prevTicks = ticks;
        var used = VmInfo() is { } vm ? MemoryUsedBytes(vm, _pageSize) : (long?)null;
        var (down, up) = _net.Sample();
        return new MetricsSnapshot(
            cpu,
            used is { } u ? Math.Round(u / Gb, 1) : null,
            _memTotal is { } t ? Math.Round(t / Gb, 1) : null,
            down, up);
    }

    // ---------------------------------------------------------------- pure math (unit-tested on every OS)

    /// <summary>% busy between two HOST_CPU_LOAD_INFO readings {user, system, idle, nice}. Tick counters wrap at 2^32.</summary>
    public static double? CpuPercent(uint[] before, uint[] after)
    {
        if (before.Length < 4 || after.Length < 4) return null;
        ulong total = 0, idle = 0;
        for (var i = 0; i < 4; i++)
        {
            var d = unchecked(after[i] - before[i]);
            total += d;
            if (i == 2) idle = d;
        }
        if (total == 0) return null;
        return Math.Round(Math.Clamp(100.0 * (total - idle) / total, 0, 100), 1);
    }

    /// <summary>
    /// Used bytes from vm_statistics64 viewed as 38 ints: (internal − purgeable + wired + compressor) pages × page size.
    /// Offsets: wire_count 3, purgeable_count 22, compressor_page_count 32, internal_page_count 35.
    /// </summary>
    public static long? MemoryUsedBytes(int[] vm, long pageSize)
    {
        if (vm.Length < VmInfo64Count) return null;
        long wired = (uint)vm[3], purgeable = (uint)vm[22], compressed = (uint)vm[32], internalPages = (uint)vm[35];
        var pages = Math.Max(0, internalPages - purgeable) + wired + compressed;
        return pages * pageSize;
    }

    // ---------------------------------------------------------------- Mach / sysctl

    private static uint[]? CpuTicks()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var info = new uint[4];
        var count = 4;
        return host_statistics(Host, HostCpuLoadInfo, info, ref count) == 0 ? info : null;
    }

    private static int[]? VmInfo()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var info = new int[VmInfo64Count];
        var count = VmInfo64Count;
        return host_statistics64(Host, HostVmInfo64, info, ref count) == 0 ? info : null;
    }

    private static long? SysctlLong(string name)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        long value = 0;
        nint size = sizeof(long);
        return sysctlbyname(name, ref value, ref size, IntPtr.Zero, 0) == 0 ? value : null;
    }

    private const string LibSystem = "/usr/lib/libSystem.dylib";
    [LibraryImport(LibSystem)] private static partial uint mach_host_self();
    [LibraryImport(LibSystem)] private static partial int host_statistics(uint host, int flavor, [In, Out] uint[] info, ref int count);
    [LibraryImport(LibSystem)] private static partial int host_statistics64(uint host, int flavor, [In, Out] int[] info, ref int count);
    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sysctlbyname(string name, ref long oldp, ref nint oldlenp, IntPtr newp, nint newlen);
}
