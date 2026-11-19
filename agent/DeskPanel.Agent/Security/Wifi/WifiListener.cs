using System.Net;
using System.Security.Authentication;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace DeskPanel.Agent.Security.Wifi;

/// <summary>Who may open a Wi-Fi connection at all, before TLS. Replaced only by tests (to allow loopback).</summary>
public sealed record LanPolicy(Func<IPAddress?, bool> AllowsRemote, int MaxConnections)
{
    public static LanPolicy Default { get; } = new(LanAddresses.IsPrivate, 16);
}

/// <summary>
/// Drives Kestrel's reloadable "Lan" endpoint: present (so Kestrel binds it) only while at least one phone may use Wi-Fi.
/// The loopback listener is configured in code and never affected.
/// </summary>
public sealed class LanEndpointSource : ConfigurationProvider, IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public void Set(int? port)
    {
        Data = port is { } p
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Endpoints:Lan:Url"] = $"https://*:{p}" }
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        OnReload(); // Kestrel re-reads its endpoints: binds or unbinds the Wi-Fi port without a restart
    }
}

/// <summary>
/// The Wi-Fi listener (docs/design/WIFI.md §4): HTTPS only, TLS 1.2+1.3, client certificate required and accepted only if
/// its public key belongs to an active device with Wi-Fi on. Private addresses only, at most 16 connections.
/// Opens when the first phone gets Wi-Fi and closes when the last one loses it (or is revoked).
/// </summary>
public sealed partial class WifiListener(DeviceStore devices, WifiSettings settings, LanEndpointSource endpoint, ILogger<WifiListener> log)
    : IHostedService
{
    /// <summary>Set on every connection accepted by the Wi-Fi listener; requests check it with <see cref="IsLan"/>.</summary>
    public const string LanItem = "deskpanel.lan";

    private readonly Lock _lock = new();
    private bool? _open;

    public bool IsOpen { get { lock (_lock) return _open == true; } }

    public int Port => settings.Port;

    public Task StartAsync(CancellationToken ct)
    {
        devices.Changed += Refresh;
        Refresh();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        devices.Changed -= Refresh;
        return Task.CompletedTask;
    }

    private void Refresh()
    {
        var open = devices.AnyOnWifi();
        lock (_lock)
        {
            if (_open == open) return;
            _open = open;
        }
        endpoint.Set(open ? settings.Port : null);
        if (open) LogOpened(log, settings.Port, string.Join(", ", LanAddresses.Current()));
        else LogClosed(log);
    }

    /// <summary>True when this request arrived through the Wi-Fi listener.</summary>
    public static bool IsLan(HttpContext context) =>
        context.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(LanItem) == true;

    /// <summary>Adds the reloadable "Lan" endpoint to Kestrel. Its settings come from services at bind time.</summary>
    public static void Configure(KestrelServerOptions kestrel, IConfiguration lanConfig) =>
        kestrel.Configure(lanConfig, reloadOnChange: true).Endpoint("Lan", e =>
        {
            var services = kestrel.ApplicationServices;
            var identity = services.GetRequiredService<WifiIdentity>();
            var store = services.GetRequiredService<DeviceStore>();
            var policy = services.GetRequiredService<LanPolicy>();

            // runs before TLS (Kestrel adds its HTTPS middleware after this callback)
            var slots = new SemaphoreSlim(policy.MaxConnections);
            e.ListenOptions.Use(next => async connection =>
            {
                if (!policy.AllowsRemote((connection.RemoteEndPoint as IPEndPoint)?.Address) || !slots.Wait(0))
                {
                    connection.Abort();
                    return;
                }
                try
                {
                    connection.Items[LanItem] = true;
                    await next(connection);
                }
                finally { slots.Release(); }
            });

            e.HttpsOptions.ServerCertificate = identity.Certificate;
            e.HttpsOptions.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13; // macOS only does 1.2 (W0 finding 1)
            e.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            e.HttpsOptions.CheckCertificateRevocation = false; // phones' certificates are self-signed by their Keystore
            e.HttpsOptions.HandshakeTimeout = TimeSpan.FromSeconds(5);
            // the key is the identity: chains and dates are ignored on purpose (W0 finding 3)
            e.HttpsOptions.ClientCertificateValidation = (certificate, _, _) =>
                store.ActiveOnWifi(WifiIdentity.SpkiPin(certificate)) is not null;
        });

    [LoggerMessage(Level = LogLevel.Information, Message = "Wi-Fi: listening on port {Port} (this computer: {Addresses})")]
    private static partial void LogOpened(ILogger logger, int port, string addresses);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wi-Fi: listener closed (no phone is allowed on Wi-Fi)")]
    private static partial void LogClosed(ILogger logger);
}
