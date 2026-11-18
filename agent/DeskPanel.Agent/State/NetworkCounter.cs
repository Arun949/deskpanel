using System.Diagnostics;
using System.Net.NetworkInformation;

namespace DeskPanel.Agent.State;

/// <summary>Bytes per second over the physical, connected adapters (no loopback, tunnels or AWDL).</summary>
public sealed class NetworkCounter
{
    private Dictionary<string, (long Rx, long Tx)>? _prev;
    private long _prevAt;

    public (double? Down, double? Up) Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var cur = Read();
        var prev = _prev;
        var prevAt = _prevAt;
        (_prev, _prevAt) = (cur, now);
        if (prev is null) return (null, null);
        return Rates(prev, cur, Stopwatch.GetElapsedTime(prevAt, now).TotalSeconds);
    }

    /// <summary>
    /// Only adapters present in both readings count (a new one carries lifetime totals), and a counter that went
    /// backwards (reset) adds nothing.
    /// </summary>
    public static (double? Down, double? Up) Rates(
        IReadOnlyDictionary<string, (long Rx, long Tx)> before,
        IReadOnlyDictionary<string, (long Rx, long Tx)> after,
        double seconds)
    {
        if (seconds <= 0) return (null, null);
        double rx = 0, tx = 0;
        foreach (var (id, now) in after)
        {
            if (!before.TryGetValue(id, out var was)) continue;
            if (now.Rx >= was.Rx) rx += now.Rx - was.Rx;
            if (now.Tx >= was.Tx) tx += now.Tx - was.Tx;
        }
        return (Math.Round(rx / seconds), Math.Round(tx / seconds));
    }

    private static Dictionary<string, (long, long)> Read()
    {
        var map = new Dictionary<string, (long, long)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)) continue;
            if (ni.Name.StartsWith("utun", StringComparison.Ordinal) || ni.Name.StartsWith("awdl", StringComparison.Ordinal)
                || ni.Name.StartsWith("llw", StringComparison.Ordinal) || ni.Name.StartsWith("bridge", StringComparison.Ordinal)) continue;
            try
            {
                var s = ni.GetIPStatistics();
                map[ni.Id] = (s.BytesReceived, s.BytesSent);
            }
            catch (NetworkInformationException) { }
            catch (PlatformNotSupportedException) { }
        }
        return map;
    }
}
