using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using DeskPanel.Agent.Core;

namespace DeskPanel.Agent.Security.Wifi;

/// <summary>Which addresses count as "the local network" (WIFI.md §4, connection filter).</summary>
public static class LanAddresses
{
    /// <summary>
    /// Private and link-local ranges only: 10/8, 172.16/12, 192.168/16, 169.254/16, fc00::/7, fe80::/10
    /// (an IPv4 address mapped into IPv6 is judged as IPv4). Everything else, including loopback, is not "LAN".
    /// </summary>
    public static bool IsPrivate(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
        }
        return false;
    }

    /// <summary>This computer's private IPv4 addresses on interfaces that are up (what phones try, in this order).</summary>
    public static IReadOnlyList<string> Current()
    {
        try
        {
            // interfaces with a gateway (the real network) first, so a phone tries Wi-Fi/Ethernet before a VPN tunnel
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPProperties())
                .OrderByDescending(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(p => p.UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.ToString())
                .Distinct()
                .Take(4)];
        }
        catch (NetworkInformationException) { return []; }
    }
}

/// <summary>
/// Wi-Fi settings in <c>wifi.json</c> (owner-only). Only the port today. Never a command-line switch, so a tampered
/// login item can't open the agent to the network (threat model T18).
/// </summary>
public sealed record WifiSettings(int Port)
{
    public const int DefaultPort = 8766;
    public const string FileName = "wifi.json";

    public static WifiSettings Load(AgentPaths paths, AgentOptions options)
    {
        var path = Path.Combine(paths.Home, FileName);
        var port = DefaultPort;
        try
        {
            using var doc = File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)) : null;
            if (doc is { RootElement.ValueKind: JsonValueKind.Object } && doc.RootElement.TryGetProperty("port", out var p)
                && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var value) && value is >= 1024 and <= 65535 && value != options.Port)
                port = value;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { } // damaged or unreadable: the default port, nothing wider
        return new WifiSettings(port);
    }
}
