using DeskPanel.Agent.Control;
using DeskPanel.Agent.Security;
using DeskPanel.Agent.Security.Wifi;

namespace DeskPanel.Agent.Enrolment;

/// <summary>Outcome of turning Wi-Fi on or off for a phone (shown in the settings page).</summary>
public enum WifiSetupResult { Ok, NotFound, NoAdb, NotPluggedIn, AppNotReady, PhoneKeyRefused, PhoneRefused, NoAddress }

/// <summary>
/// Turns Wi-Fi on or off for one phone (docs/design/WIFI.md §3.1). Turning it on needs the phone plugged in: the phone's
/// Keystore client key and the agent's pin are swapped over adb only, so nothing new crosses the network to set up trust.
/// </summary>
public sealed partial class WifiSetup(
    Adb adb,
    DeviceStore store,
    WifiSettings settings,
    IServiceProvider services,
    AuditLog audit,
    ILogger<WifiSetup> log,
    Func<IReadOnlyList<string>>? addresses = null)
{
    private IReadOnlyList<string> Addresses() => (addresses ?? LanAddresses.Current)();

    public async Task<WifiSetupResult> EnableAsync(string deviceId, CancellationToken ct)
    {
        var device = store.Active(deviceId);
        if (device is null) return WifiSetupResult.NotFound;
        if (adb.Path is null) return WifiSetupResult.NoAdb;
        // found by hardware serial, so USB and wireless adb both work (threat model T21)
        if (await adb.TransportForAsync(device.Serial, ct) is not { } transport) return WifiSetupResult.NotPluggedIn;
        // the app on the cable must be the one holding this device's key (not a reinstall waiting for enrolment)
        if (await adb.EnrolledIdAsync(transport, ct) is not (Adb.KeyStatus.Enrolled, var id) || id != device.Id)
            return WifiSetupResult.AppNotReady;

        var clientPin = await adb.WifiClientPinAsync(transport, ct);
        if (clientPin is null) return WifiSetupResult.PhoneKeyRefused;
        var hosts = Addresses();
        if (hosts.Count == 0) return WifiSetupResult.NoAddress;

        store.SetWifi(device.Id, clientPin); // opens the listener (and makes the agent's key) if this is the first phone
        var agentPin = services.GetRequiredService<WifiIdentity>().Pin;
        if (!await adb.SetWifiAsync(transport, settings.Port, agentPin, hosts, ct))
        {
            store.SetWifi(device.Id, null); // never leave Wi-Fi on for a phone that doesn't know how to use it
            return WifiSetupResult.PhoneRefused;
        }
        audit.Write(new AuditEntry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), device.Id, "wifi.on", "ok", 0));
        LogOn(log, device.Model, device.Id);
        return WifiSetupResult.Ok;
    }

    /// <summary>Turns Wi-Fi off at once (the agent forgets the phone's key); tells the phone too if it's plugged in.</summary>
    public async Task<WifiSetupResult> DisableAsync(string deviceId, CancellationToken ct)
    {
        var device = store.Active(deviceId);
        if (device is null) return WifiSetupResult.NotFound;
        store.SetWifi(device.Id, null);
        audit.Write(new AuditEntry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), device.Id, "wifi.off", "ok", 0));
        LogOff(log, device.Model, device.Id);
        if (adb.Path is not null && await adb.TransportForAsync(device.Serial, ct) is { } transport)
            await adb.SetWifiAsync(transport, null, null, [], ct); // best effort: the agent side is what counts
        return WifiSetupResult.Ok;
    }

    /// <summary>
    /// Called when an enrolled phone is plugged in: brings its Wi-Fi details up to date (the computer's address may
    /// have changed), or tells it to forget Wi-Fi if it was turned off while unplugged.
    /// </summary>
    public async Task RefreshAsync(EnrolledDevice device, string transport, CancellationToken ct)
    {
        if (device is { Wifi: true, ClientSpki: not null } && Addresses() is { Count: > 0 } hosts)
            await adb.SetWifiAsync(transport, settings.Port, services.GetRequiredService<WifiIdentity>().Pin, hosts, ct);
        else
            await adb.SetWifiAsync(transport, null, null, [], ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Wi-Fi turned on for {Model} ({DeviceId})")]
    private static partial void LogOn(ILogger logger, string model, string deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wi-Fi turned off for {Model} ({DeviceId})")]
    private static partial void LogOff(ILogger logger, string model, string deviceId);
}
