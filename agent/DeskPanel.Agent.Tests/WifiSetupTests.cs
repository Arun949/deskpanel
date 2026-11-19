using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using DeskPanel.Agent.Control;
using DeskPanel.Agent.Core;
using DeskPanel.Agent.Enrolment;
using DeskPanel.Agent.Security;
using DeskPanel.Agent.Security.Wifi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPanel.Agent.Tests;

/// <summary>Turning Wi-Fi on and off for a phone over adb (docs/design/WIFI.md §3.1), with a scripted adb.</summary>
public sealed class WifiSetupTests : IDisposable
{
    /// <summary>The shared vector's public key (base64url SPKI); its pin is <see cref="WifiPartsTests.VectorPin"/>.</summary>
    public const string VectorSpki = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEA091UJEE1ZfeHKnki0glkTSxPNEiDF7KOos-QDq4HLuJMiDuwaXqYBaInuUwd0SYKG-eysVnHCGDhDc0X-MhVA";

    private readonly string _home = Directory.CreateTempSubdirectory("deskpanel-wifisetup-").FullName;
    private readonly FakeTools _tools = new();
    private readonly DeviceStore _store;
    private readonly EnrolledDevice _device;
    private string _appId;
    private string _phoneSpki = VectorSpki;
    private bool _phoneAcceptsWifi = true;
    private bool _pluggedIn = true;

    public WifiSetupTests()
    {
        _store = new DeviceStore(new AgentPaths(_home));
        (_device, _) = _store.Enrol("PHONE1", "Pixel 4a");
        _appId = _device.Id;
        _tools.Respond = args =>
        {
            var a = string.Join(' ', args);
            if (a == "devices") return new(0, _pluggedIn ? "List of devices attached\nPHONE1\tdevice\n" : "List of devices attached\n");
            if (a.Contains("--method status", StringComparison.Ordinal)) return new(0, $"Result: Bundle[{{enrolled=true, id={_appId}}}]");
            if (a.Contains("--method wifiKey", StringComparison.Ordinal)) return new(0, $"Result: Bundle[{{spki={_phoneSpki}}}]");
            if (a.Contains("--method wifiOn", StringComparison.Ordinal)) return new(0, $"Result: Bundle[{{ok={(_phoneAcceptsWifi ? "true" : "false")}}}]");
            if (a.Contains("--method wifiOff", StringComparison.Ordinal)) return new(0, "Result: Bundle[{ok=true}]");
            return new(0, "");
        };
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);

    /// <summary>A WifiSetup over a scripted adb, with a fixed address list (tests must not depend on the machine's network).</summary>
    public static WifiSetup Setup(Adb adb, DeviceStore store, string home, IReadOnlyList<string>? hosts = null)
    {
        var services = new ServiceCollection().AddSingleton(new AgentPaths(home)).AddSingleton<WifiIdentity>().BuildServiceProvider();
        return new WifiSetup(adb, store, new WifiSettings(8766), services, new AuditLog(new AgentPaths(home)),
            NullLogger<WifiSetup>.Instance, () => hosts ?? ["192.168.1.20"]);
    }

    private WifiSetup Setup(IReadOnlyList<string>? hosts = null) => Setup(new Adb(_tools, "/fake/adb"), _store, _home, hosts);

    private string AgentPin => new WifiIdentity(new AgentPaths(_home)).Pin;

    [Fact]
    public async Task TurningWifiOnSwapsTheKeysOverAdb()
    {
        Assert.Equal(WifiSetupResult.Ok, await Setup().EnableAsync(_device.Id, default));
        Assert.Equal(_device.Id, _store.ActiveOnWifi(WifiPartsTests.VectorPin)?.Id); // the phone's Keystore key is registered
        var on = Assert.Single(_tools.Calls, c => c.Contains("--method wifiOn", StringComparison.Ordinal));
        Assert.Contains($"--extra pin:s:{AgentPin}", on, StringComparison.Ordinal); // and the phone learns the agent's key
        Assert.Contains("--extra port:i:8766", on, StringComparison.Ordinal);
        Assert.Contains("--extra hosts:s:192.168.1.20", on, StringComparison.Ordinal);
        Assert.Contains(new AuditLog(new AgentPaths(_home)).Recent(10), e => e.Action == "wifi.on" && e.DeviceId == _device.Id);
    }

    [Fact]
    public async Task OnNeedsThePhonePluggedIn()
    {
        _pluggedIn = false;
        Assert.Equal(WifiSetupResult.NotPluggedIn, await Setup().EnableAsync(_device.Id, default));
        Assert.False(_store.AnyOnWifi());
    }

    [Fact]
    public async Task OnNeedsTheAppThatHoldsThisDevicesKey()
    {
        _appId = "0123456789abcdef"; // reinstalled app, another enrolment
        Assert.Equal(WifiSetupResult.AppNotReady, await Setup().EnableAsync(_device.Id, default));
        Assert.DoesNotContain(_tools.Calls, c => c.Contains("--method wifiKey", StringComparison.Ordinal));
        Assert.False(_store.AnyOnWifi());
    }

    [Theory]
    [InlineData("not-a-key")]
    [InlineData("")]
    [InlineData("MCowBQYDK2VwAyEAGb9ECWmEzf6FQbrBZ9w7lshQhqowtrbLDFw4rXAxZuE")] // an Ed25519 key, not P-256
    public async Task APhoneKeyThatIsNotP256IsRefused(string spki)
    {
        _phoneSpki = spki;
        Assert.Equal(WifiSetupResult.PhoneKeyRefused, await Setup().EnableAsync(_device.Id, default));
        Assert.False(_store.AnyOnWifi());
    }

    [Fact]
    public async Task IfThePhoneRefusesTheDetailsWifiStaysOff()
    {
        _phoneAcceptsWifi = false;
        Assert.Equal(WifiSetupResult.PhoneRefused, await Setup().EnableAsync(_device.Id, default));
        Assert.False(_store.AnyOnWifi());
    }

    [Fact]
    public async Task WithoutAPrivateAddressThereIsNothingToGiveThePhone()
    {
        Assert.Equal(WifiSetupResult.NoAddress, await Setup(hosts: []).EnableAsync(_device.Id, default));
        Assert.False(_store.AnyOnWifi());
    }

    [Fact]
    public async Task OffWorksAtOnceAndTellsAPluggedInPhone()
    {
        await Setup().EnableAsync(_device.Id, default);
        Assert.Equal(WifiSetupResult.Ok, await Setup().DisableAsync(_device.Id, default));
        Assert.False(_store.AnyOnWifi());
        Assert.Contains(_tools.Calls, c => c.Contains("--method wifiOff", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OffWorksEvenWhenThePhoneIsAway()
    {
        await Setup().EnableAsync(_device.Id, default);
        _pluggedIn = false;
        _tools.Calls.Clear();
        Assert.Equal(WifiSetupResult.Ok, await Setup().DisableAsync(_device.Id, default));
        Assert.False(_store.AnyOnWifi());
        Assert.DoesNotContain(_tools.Calls, c => c.Contains("wifiOff", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PluggingInRefreshesOrClearsTheDetails()
    {
        await Setup().RefreshAsync(_device, default); // Wi-Fi off: tell the phone to forget any old details
        Assert.Contains(_tools.Calls, c => c.Contains("--method wifiOff", StringComparison.Ordinal));

        await Setup().EnableAsync(_device.Id, default);
        _tools.Calls.Clear();
        await Setup(hosts: ["10.0.0.7"]).RefreshAsync(_store.Active(_device.Id)!, default); // the computer's address changed
        Assert.Contains(_tools.Calls, c => c.Contains("--extra hosts:s:10.0.0.7", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePhoneKeyIsCheckedBeforeItsPinIsTrusted()
    {
        Assert.Equal(WifiPartsTests.VectorPin, Adb.PinFromSpki(VectorSpki));
        Assert.Null(Adb.PinFromSpki(VectorSpki + "AA"));            // trailing bytes
        Assert.Null(Adb.PinFromSpki(VectorSpki.Replace('A', '+'))); // not base64url
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Null(Adb.PinFromSpki(DeviceStore.Base64Url(p384.ExportSubjectPublicKeyInfo())));
        using var rsa = RSA.Create(2048);
        Assert.Null(Adb.PinFromSpki(DeviceStore.Base64Url(rsa.ExportSubjectPublicKeyInfo())));
    }

    [Theory]
    [InlineData("192.168.1.20;reboot")]    // shell metacharacters never reach the phone's shell
    [InlineData("$(id)")]
    [InlineData("fe80::1")]                // IPv4 only in W1
    public async Task OnlyPlainIpv4AddressesGoOnTheCommandLine(string bad)
    {
        var adb = new Adb(_tools, "/fake/adb");
        await Assert.ThrowsAsync<ArgumentException>(() => adb.SetWifiAsync("PHONE1", 8766, AgentPin, [bad], default));
        Assert.True(await adb.SetWifiAsync("PHONE1", 8766, AgentPin, [bad, "10.1.2.3"], default));
        Assert.EndsWith("--extra hosts:s:10.1.2.3", _tools.Calls[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABadPinOrPortNeverReachesThePhone()
    {
        var adb = new Adb(_tools, "/fake/adb");
        await Assert.ThrowsAsync<ArgumentException>(() => adb.SetWifiAsync("PHONE1", 8766, "x;reboot", ["10.1.2.3"], default));
        await Assert.ThrowsAsync<ArgumentException>(() => adb.SetWifiAsync("PHONE1", 80, AgentPin, ["10.1.2.3"], default));
        Assert.Empty(_tools.Calls);
    }
}

public sealed class WifiSettingsApiTests(TestHost host) : IClassFixture<TestHost>
{
    [Fact]
    public async Task TheSettingsPageCanAskButNotForceWifiOn()
    {
        var (device, _) = host.Devices.Enrol("API-WIFI", "Pixel Test");
        using var admin = host.AdminClient();
        var res = await admin.PostAsJsonAsync($"/admin/api/devices/{device.Id}/wifi", new { on = true });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var result = (await res.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["result"];
        Assert.Contains(result, new[] { "NoAdb", "NotPluggedIn" }); // no such phone on this machine's USB
        Assert.False(host.Devices.AnyOnWifi());

        var devices = await admin.GetFromJsonAsync<List<Admin.DeviceView>>("/admin/api/devices");
        Assert.False(devices!.Single(d => d.Id == device.Id).Wifi);
        var status = await admin.GetFromJsonAsync<Dictionary<string, object>>("/admin/api/wifi");
        Assert.Equal("False", status!["open"].ToString());
    }

    [Fact]
    public async Task UnknownDevicesAndPhonesCanNotUseIt()
    {
        using var admin = host.AdminClient();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/admin/api/devices/0123456789abcdef/wifi", new { on = false })).StatusCode);
        var (phone, _, _) = host.Enrolled("API-WIFI-PHONE");
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.PostAsJsonAsync("/admin/api/devices/0123456789abcdef/wifi", new { on = true })).StatusCode);
    }
}
