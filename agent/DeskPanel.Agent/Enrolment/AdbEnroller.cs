using DeskPanel.Agent.Core;
using DeskPanel.Agent.Security;

namespace DeskPanel.Agent.Enrolment;

/// <summary>
/// Every 3 s: for each phone on USB with the app installed, make sure it is enrolled (asking the user on the PC first),
/// that its key is in the app, and that adb reverse is in place. adb is the root of trust: the phone owner allowed USB
/// debugging, and the PC user allowed enrolment.
/// </summary>
public sealed partial class AdbEnroller(
    Adb adb,
    DeviceStore store,
    IEnrolPrompt prompt,
    AgentOptions options,
    WifiSetup wifi,
    ILogger<AdbEnroller> log) : BackgroundService
{
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, DateTimeOffset> _declined = [];
    private readonly HashSet<string> _opened = [];
    private bool _warnedNoAdb;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RoundAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                LogRoundFailed(log, ex.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    internal async Task RoundAsync(CancellationToken ct)
    {
        if (adb.Path is null)
        {
            if (!_warnedNoAdb) LogNoAdb(log);
            _warnedNoAdb = true;
            return;
        }
        var serials = await adb.DevicesAsync(ct);
        _opened.IntersectWith(serials); // a phone that was unplugged gets its panel opened again next time
        foreach (var serial in serials)
        {
            if (!await adb.AppInstalledAsync(serial, ct)) continue;

            var device = store.ActiveForSerial(serial);
            var (status, appId) = await adb.EnrolledIdAsync(serial, ct);
            if (status == Adb.KeyStatus.Unknown) { LogNoAnswer(log, serial); continue; } // ask again next round
            if (device is null || appId != device.Id)
            {
                // new phone, or the app lost its key (reinstalled, data cleared): enrol again, with consent
                if (_declined.TryGetValue(serial, out var at) && DateTimeOffset.UtcNow - at < AskAgainAfter) continue;
                var model = await adb.ModelAsync(serial, ct);
                LogAsking(log, model, serial);
                if (!await prompt.AskAsync(model, serial, ct))
                {
                    _declined[serial] = DateTimeOffset.UtcNow;
                    LogDeclined(log, serial);
                    continue;
                }
                var (enrolled, key) = store.Enrol(serial, model);
                if (!await adb.DeliverKeyAsync(serial, enrolled.Id, key, ct))
                {
                    store.Revoke(enrolled.Id); // never keep a key the phone doesn't have
                    LogDeliveryFailed(log, serial);
                    continue;
                }
                _declined.Remove(serial);
                LogEnrolled(log, model, enrolled.Id);
            }

            await adb.EnsureReverseAsync(serial, options.Port, ct);
            if (_opened.Add(serial))
            {
                // just plugged in: bring its Wi-Fi details up to date (or tell it Wi-Fi was turned off meanwhile)
                if (store.ActiveForSerial(serial) is { } current) await wifi.RefreshAsync(current, ct);
                await adb.OpenPanelAsync(serial, ct);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The app on {Serial} didn't answer the key check; trying again")]
    private static partial void LogNoAnswer(ILogger logger, string serial);
    [LoggerMessage(Level = LogLevel.Warning, Message = "adb not found: install Android platform tools to connect a phone over USB")]
    private static partial void LogNoAdb(ILogger logger);
    [LoggerMessage(Level = LogLevel.Warning, Message = "USB round failed: {Message}")]
    private static partial void LogRoundFailed(ILogger logger, string message);
    [LoggerMessage(Level = LogLevel.Information, Message = "Asking on this computer whether to enrol {Model} ({Serial})")]
    private static partial void LogAsking(ILogger logger, string model, string serial);
    [LoggerMessage(Level = LogLevel.Information, Message = "Enrolment of {Serial} was not allowed; asking again in 10 minutes")]
    private static partial void LogDeclined(ILogger logger, string serial);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not hand the key to the app on {Serial}; enrolment undone")]
    private static partial void LogDeliveryFailed(ILogger logger, string serial);
    [LoggerMessage(Level = LogLevel.Information, Message = "Enrolled {Model} as device {DeviceId}")]
    private static partial void LogEnrolled(ILogger logger, string model, string deviceId);
}
