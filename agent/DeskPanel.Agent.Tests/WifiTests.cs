using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DeskPanel.Agent.Api;
using DeskPanel.Agent.Core;
using DeskPanel.Agent.Security;
using DeskPanel.Agent.Security.Wifi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DeskPanel.Agent.Tests;

/// <summary>
/// The Wi-Fi listener over real TLS (docs/design/WIFI.md §5): a phone's Keystore key is played by a P-256 key made here.
/// One host for the class; each test enrols its own phones.
/// </summary>
public sealed class WifiListenerTests(KestrelTestHost host) : IClassFixture<KestrelTestHost>
{
    private sealed record Phone(EnrolledDevice Device, string Key, X509Certificate2 Certificate);

    private WifiIdentity Identity => host.Services.GetRequiredService<WifiIdentity>();

    /// <summary>A phone with Wi-Fi on: enrolled, with a client key whose hash is registered.</summary>
    private Phone WifiPhone(string serial)
    {
        var (device, key) = host.Devices.Enrol(serial, "Pixel Test");
        var certificate = ClientCertificate();
        Assert.True(host.Devices.SetWifi(device.Id, WifiIdentity.SpkiPin(certificate)));
        return new Phone(device, key, certificate);
    }

    private static X509Certificate2 ClientCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var c = new CertificateRequest("CN=phone", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pkcs12, "t"), "t"); // usable by SslStream on macOS
    }

    /// <summary>A client like the phone: optional client key, and it accepts only the agent's pinned key.</summary>
    private HttpClient LanClient(X509Certificate2? clientCertificate, string? deviceAuth = null, string? pin = null)
    {
        pin ??= Identity.Pin;
        var handler = new SocketsHttpHandler
        {
            SslOptions =
            {
                ClientCertificates = clientCertificate is null ? null : new X509CertificateCollection { clientCertificate },
                LocalCertificateSelectionCallback = clientCertificate is null ? null : (_, _, _, _, _) => clientCertificate,
                RemoteCertificateValidationCallback = (_, c, _, _) => c is X509Certificate2 x && WifiIdentity.SpkiPin(x) == pin,
            },
            PooledConnectionLifetime = TimeSpan.Zero, // a fresh TLS connection per request
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{host.Lan}/"), Timeout = TimeSpan.FromSeconds(10) };
        if (deviceAuth is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", deviceAuth);
        return client;
    }

    private static string Auth(Phone p) => $"Device {p.Device.Id}.{p.Key}";

    private async Task<bool> Listening()
    {
        using var tcp = new TcpClient();
        try { await tcp.ConnectAsync(IPAddress.Loopback, host.Lan); return true; }
        catch (SocketException) { return false; }
    }

    private async Task WaitForListener(bool open)
    {
        for (var i = 0; i < 50 && await Listening() != open; i++) await Task.Delay(100);
        Assert.Equal(open, await Listening());
    }

    [Fact]
    public async Task APhoneWithItsKeyAndTheRightPinGetsItsState()
    {
        var phone = WifiPhone("WIFI-OK");
        await WaitForListener(open: true);
        using var client = LanClient(phone.Certificate, Auth(phone));
        var state = await client.GetFromJsonAsync<StateMessage>("/api/v1/state");
        Assert.NotNull(state);
        var wifi = await client.GetFromJsonAsync<WifiInfo>("/api/v1/wifi");
        Assert.True(wifi!.Enabled);
        Assert.Equal(host.Lan, wifi.Port);
        Assert.Equal(Identity.Pin, wifi.Pin);
    }

    [Fact]
    public async Task WithoutAClientKeyTheHandshakeFails()
    {
        var phone = WifiPhone("WIFI-NOKEY");
        await WaitForListener(open: true);
        using var client = LanClient(clientCertificate: null, Auth(phone));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("/api/v1/hello"));
    }

    [Fact]
    public async Task AnUnregisteredClientKeyFailsTheHandshake()
    {
        var phone = WifiPhone("WIFI-UNKNOWN");
        await WaitForListener(open: true);
        using var client = LanClient(ClientCertificate(), Auth(phone)); // right device key, wrong Keystore key
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("/api/v1/state"));
    }

    [Fact]
    public async Task APhoneCanNotUseAnotherPhonesDeviceKey()
    {
        var a = WifiPhone("WIFI-A");
        var b = WifiPhone("WIFI-B");
        await WaitForListener(open: true);
        using var client = LanClient(a.Certificate, Auth(b));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/state")).StatusCode);
    }

    [Fact]
    public async Task TheClientRefusesAnAgentWithAnotherKey()
    {
        var phone = WifiPhone("WIFI-PIN");
        await WaitForListener(open: true);
        using var client = LanClient(phone.Certificate, Auth(phone), pin: new string('A', 43));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("/api/v1/hello"));
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/login?t=x")]        // Public, but not marked for Wi-Fi
    [InlineData("/admin/api/devices")]
    public async Task SettingsRoutesDoNotExistOverWifi(string path)
    {
        var phone = WifiPhone("WIFI-ADMIN" + path.Length);
        await WaitForListener(open: true);
        using var client = LanClient(phone.Certificate, Auth(phone));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task ABrowserHeaderIsRefusedOverWifi()
    {
        var phone = WifiPhone("WIFI-ORIGIN");
        await WaitForListener(open: true);
        using var client = LanClient(phone.Certificate, Auth(phone));
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/state")).StatusCode);
    }

    [Fact]
    public async Task ControlsWorkOverWifiThroughTheSameGate()
    {
        var phone = WifiPhone("WIFI-CONTROL");
        await WaitForListener(open: true);
        using var client = LanClient(phone.Certificate, Auth(phone));
        // no permission granted yet: the control gate answers, as it would over USB
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/media/next", null)).StatusCode);
    }

    /// <summary>
    /// Found in the real-phone test: with another phone keeping the Wi-Fi port open, a phone whose Wi-Fi is turned off
    /// must lose its already-open stream too, not just new requests.
    /// </summary>
    [Fact]
    public async Task TurningWifiOffEndsThatPhonesOpenStreamEvenIfThePortStaysOpen()
    {
        var phone = WifiPhone("WIFI-STREAM-A");
        WifiPhone("WIFI-STREAM-B"); // keeps the listener open
        await WaitForListener(open: true);
        using var client = LanClient(phone.Certificate, Auth(phone));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var res = await client.GetAsync("/api/v1/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cts.Token));
        Assert.StartsWith("id: ", await reader.ReadLineAsync(cts.Token));

        var offAt = System.Diagnostics.Stopwatch.StartNew();
        host.Devices.SetWifi(phone.Device.Id, clientSpki: null);
        while (await reader.ReadLineAsync(cts.Token) is not null) { }
        Assert.True(offAt.Elapsed < Endpoints.FullStateEvery, $"stream ended after {offAt.Elapsed}");
        Assert.True(await Listening()); // the port stayed open for the other phone
    }

    [Fact]
    public async Task TurningWifiOffForAPhoneCutsItOffAtOnce()
    {
        var phone = WifiPhone("WIFI-OFF");
        await WaitForListener(open: true);
        using var client = LanClient(phone.Certificate, Auth(phone));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/state")).StatusCode);
        host.Devices.SetWifi(phone.Device.Id, clientSpki: null);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("/api/v1/state")); // its key is no longer accepted
    }
}

/// <summary>The listener only exists while a phone may use it (its own host, so no other test's phones keep it open).</summary>
public sealed class WifiListenerLifecycleTests : IDisposable
{
    private readonly KestrelTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task<bool> Listening()
    {
        using var tcp = new TcpClient();
        try { await tcp.ConnectAsync(IPAddress.Loopback, _host.Lan); return true; }
        catch (SocketException) { return false; }
    }

    private async Task Until(bool open)
    {
        for (var i = 0; i < 50 && await Listening() != open; i++) await Task.Delay(100);
        Assert.Equal(open, await Listening());
    }

    [Fact]
    public async Task OpensForTheFirstWifiPhoneAndClosesWhenTheLastIsRevoked()
    {
        Assert.False(await Listening());                                    // nobody on Wi-Fi: no port on the network
        Assert.False(File.Exists(Path.Combine(_host.Home, WifiIdentity.FileName))); // and no key made

        var (device, _) = _host.Devices.Enrol("LIFECYCLE", "Pixel Test");
        _host.Devices.SetWifi(device.Id, new string('x', 43));
        await Until(open: true);
        Assert.True(File.Exists(Path.Combine(_host.Home, WifiIdentity.FileName)));

        _host.Devices.Revoke(device.Id);
        await Until(open: false);
    }

    [Fact]
    public async Task LoopbackKeepsWorkingWhileWifiOpensAndCloses()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.Loopback}/") };
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/hello")).StatusCode);
        var (device, _) = _host.Devices.Enrol("LOOPBACK", "Pixel Test");
        _host.Devices.SetWifi(device.Id, new string('y', 43));
        await Until(open: true);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/hello")).StatusCode);
        _host.Devices.SetWifi(device.Id, null);
        await Until(open: false);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/hello")).StatusCode);
    }
}

public sealed class WifiRouteTests(TestHost factory) : IClassFixture<TestHost>
{
    private IReadOnlyList<RouteEndpoint> Routes()
    {
        using var _ = factory.CreateClient();
        return factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
    }

    [Fact]
    public void NoAdminRouteIsEverAllowedOverWifi()
    {
        var bad = Routes().Where(e => e.Metadata.GetMetadata<TrustMetadata>()?.Class == TrustClass.Admin
                                      && e.Metadata.GetMetadata<LanAllowedMetadata>() is not null)
            .Select(e => e.RoutePattern.RawText).ToList();
        Assert.True(bad.Count == 0, "Admin routes marked OverLan: " + string.Join(", ", bad));
    }

    /// <summary>The exact list of Wi-Fi routes. A change here is a security decision: review it in the pull request.</summary>
    [Fact]
    public void TheWifiRoutesAreExactlyThePhonesRoutes()
    {
        var lan = Routes().Where(e => e.Metadata.GetMetadata<LanAllowedMetadata>() is not null)
            .Select(e => e.RoutePattern.RawText!.TrimStart('/')).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            ["api/v1/actions/{id}", "api/v1/hello", "api/v1/layout", "api/v1/media/{op}", "api/v1/state", "api/v1/stream", "api/v1/volume", "api/v1/wifi"],
            lan);
    }

    [Fact]
    public async Task APhoneWithoutWifiLearnsNothingAndNoKeyIsMade()
    {
        var (client, _, _) = factory.Enrolled("NO-WIFI");
        var info = await client.GetFromJsonAsync<WifiInfo>("/api/v1/wifi");
        Assert.False(info!.Enabled);
        Assert.Null(info.Pin);
        Assert.False(File.Exists(Path.Combine(factory.Home, WifiIdentity.FileName)));
    }
}

public sealed class WifiPartsTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("deskpanel-wifi-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    /// <summary>
    /// Shared vector: the pin of this fixed P-256 key was computed with openssl
    /// (<c>openssl pkey -pubout -outform DER | sha256 | base64url</c>). The phone's Kotlin code must produce the same.
    /// </summary>
    private const string VectorKey = """
        -----BEGIN PRIVATE KEY-----
        MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgDY3TTx5aJYDICJ77
        pw1Pdws3W+bkbkvQ5mpR6vMbDRGhRANCAAQDT3VQkQTVl94cqeSLSCWRNLE80SIM
        Xso6iz5AOrgcu4kyIO7BpepgFoie5TB3RJgob57KxWccIYOENzRf4yFU
        -----END PRIVATE KEY-----
        """;
    public const string VectorPin = "NE6VEyqZI4eYeSx2Ggl8Mtp8B8QsLaLVULsd-Nz5W0o";

    [Fact]
    public void PinMatchesTheSharedVector()
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(VectorKey);
        Assert.Equal(VectorPin, WifiIdentity.SpkiPin(key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void TheKeyIsMadeOnceKeptOwnerOnlyAndReused()
    {
        var first = new WifiIdentity(new AgentPaths(_home));
        var file = Path.Combine(_home, WifiIdentity.FileName);
        Assert.True(File.Exists(file));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.Equal(first.Pin, new WifiIdentity(new AgentPaths(_home)).Pin);  // same key after a restart
        Assert.Equal(first.Pin, WifiIdentity.SpkiPin(first.Certificate));       // the certificate carries that key
        Assert.True(first.Certificate.HasPrivateKey);
        Assert.Equal("CN=DeskPanel", first.Certificate.Subject);                // no computer name
    }

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.0.1", false)]   // carrier-grade NAT / Tailscale: not in W1
    [InlineData("127.0.0.1", false)]    // loopback has its own listener
    [InlineData("2001:4860::8888", false)]
    [InlineData("::1", false)]
    public void OnlyPrivateAddressesMayConnect(string address, bool allowed) =>
        Assert.Equal(allowed, LanAddresses.IsPrivate(IPAddress.Parse(address)));

    [Theory]
    [InlineData("""{"port": 9000}""", 9000)]
    [InlineData("""{"port": 80}""", WifiSettings.DefaultPort)]       // privileged: ignored
    [InlineData("""{"port": 8765}""", WifiSettings.DefaultPort)]     // the loopback port: ignored
    [InlineData("""{"port": "9000"}""", WifiSettings.DefaultPort)]
    [InlineData("not json", WifiSettings.DefaultPort)]
    [InlineData("[9000]", WifiSettings.DefaultPort)]
    public void SettingsAreReadStrictly(string json, int port)
    {
        File.WriteAllText(Path.Combine(_home, WifiSettings.FileName), json);
        Assert.Equal(port, WifiSettings.Load(new AgentPaths(_home), new AgentOptions(AgentOptions.DefaultPort)).Port);
    }

    [Fact]
    public void WifiFollowsTheDeviceLifecycle()
    {
        var store = new DeviceStore(new AgentPaths(_home));
        var changes = 0;
        store.Changed += () => changes++;
        var (device, _) = store.Enrol("S1", "Pixel");
        Assert.False(store.AnyOnWifi());
        Assert.True(store.SetWifi(device.Id, "pin1"));
        Assert.Equal(device.Id, store.ActiveOnWifi("pin1")?.Id);
        Assert.Null(store.ActiveOnWifi("pin2"));
        Assert.True(store.AnyOnWifi());
        Assert.Equal(device.Id, new DeviceStore(new AgentPaths(_home)).ActiveOnWifi("pin1")?.Id); // saved

        var (again, _) = store.Enrol("S1", "Pixel"); // re-enrolment revokes the old record, and its Wi-Fi with it
        Assert.Null(store.ActiveOnWifi("pin1"));
        Assert.False(store.SetWifi(device.Id, "pin1")); // a revoked device can't get Wi-Fi back
        Assert.True(store.SetWifi(again.Id, "pin3"));
        store.Revoke(again.Id);
        Assert.False(store.AnyOnWifi());
        Assert.Equal(5, changes); // enrol, Wi-Fi on, re-enrol, Wi-Fi on, revoke (the refused SetWifi raises nothing)
    }
}
