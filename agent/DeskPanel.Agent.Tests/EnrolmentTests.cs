using DeskPanel.Agent.Core;
using DeskPanel.Agent.Enrolment;
using DeskPanel.Agent.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPanel.Agent.Tests;

public sealed class AdbParsingTests
{
    [Theory]
    [InlineData("AGN2DK23HB019713\n", "AGN2DK23HB019713")]
    [InlineData("  R58M12ABCDE  ", "R58M12ABCDE")]
    [InlineData("", null)]
    [InlineData("unknown", null)]
    [InlineData("0123456789ABCDEF", null)]  // a placeholder some devices report
    [InlineData("abc;reboot", null)]
    [InlineData("x", null)]
    public void HardwareSerialsAreCheckedStrictly(string raw, string? expected) => Assert.Equal(expected, Adb.CleanHardwareSerial(raw));

    [Theory]
    [InlineData("10.0.0.5:41235", true)]
    [InlineData("adb-AGN2DK23HB019713-abc._adb-tls-connect._tcp", true)]
    [InlineData("AGN2DK23HB019713", false)]
    public void WirelessNamesAreRecognised(string name, bool wireless) => Assert.Equal(wireless, Adb.IsWirelessName(name));

    [Fact]
    public void OnlyAuthorizedDevicesWithSafeSerialsAreListed()
    {
        const string output = "List of devices attached\nABC123\tdevice\nDEF456\tunauthorized\nemulator-5554\tdevice\nbad;serial\tdevice\n\n";
        Assert.Equal(["ABC123", "emulator-5554"], Adb.ParseDevices(output));
    }

    [Fact]
    public void ContentCallBundlesAreParsed()
    {
        var map = Adb.ParseBundle("Result: Bundle[{ok=true, id=0123456789abcdef}]\n");
        Assert.Equal("true", map["ok"]);
        Assert.Equal("0123456789abcdef", map["id"]);
        Assert.Empty(Adb.ParseBundle("Error: no provider"));
    }

    [Theory]
    [InlineData("Pixel 7\n", "Pixel 7")]
    [InlineData("  \n", "Android phone")]
    [InlineData("Evil\" & do shell script \"x", "Evil\" & do shell script \"x")] // kept as text; it goes in as argv, never into script source
    public void ModelNamesAreCleanedAsUntrustedText(string raw, string expected) => Assert.Equal(expected, Adb.CleanModel(raw));

    [Fact]
    public void ModelNamesAreCappedAndStrippedOfControlCharacters() =>
        Assert.Equal("[31m" + new string('a', 36), Adb.CleanModel("\u001b[31m" + new string('a', 60))); // ESC dropped, 40 chars max

    [Theory]
    [InlineData(0, "button returned:Allow, gave up:false", true)]
    [InlineData(0, "button returned:Don't Allow, gave up:false", false)]
    [InlineData(0, "button returned:, gave up:true", false)]
    [InlineData(1, "execution error: User canceled. (-128)", false)]
    public void OnlyAnExplicitAllowEnrols(int exit, string output, bool allowed) =>
        Assert.Equal(allowed, MacEnrolPrompt.IsAllowed(new ToolResult(exit, output)));
}

/// <summary>The enrolment loop against a scripted adb.</summary>
public sealed class AdbEnrollerTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("deskpanel-enrol-").FullName;
    private readonly FakeTools _tools = new();
    private readonly DeviceStore _store;
    private string _transport = "PHONE1";      // adb's name for the connection (USB: the serial; wireless: ip:port)
    private string _hardwareSerial = "PHONE1"; // what the phone reports as ro.serialno
    private bool _deliverOk = true;
    private string? _appHasId;
    private bool _appSilent; // provider unreachable (app restarting)

    public AdbEnrollerTests()
    {
        _store = new DeviceStore(new AgentPaths(_home));
        _tools.Respond = args =>
        {
            var a = string.Join(' ', args);
            if (a == "devices") return new(0, $"List of devices attached\n{_transport}\tdevice\n");
            if (a.EndsWith("getprop ro.serialno", StringComparison.Ordinal)) return new(0, _hardwareSerial + "\n");
            if (a.EndsWith("pm path app.deskpanel", StringComparison.Ordinal)) return new(0, "package:/data/app/base.apk\n");
            if (a.EndsWith("getprop ro.product.model", StringComparison.Ordinal)) return new(0, "Pixel 4a\n");
            if (a.Contains("--method status", StringComparison.Ordinal) && _appSilent)
                return new(255, "", "Error while accessing provider:app.deskpanel.enrol");
            if (a.Contains("--method status", StringComparison.Ordinal))
                return new(0, _appHasId is null ? "Result: Bundle[{enrolled=false}]" : $"Result: Bundle[{{enrolled=true, id={_appHasId}}}]");
            if (a.Contains("--method enrol", StringComparison.Ordinal)) return new(0, $"Result: Bundle[{{ok={(_deliverOk ? "true" : "false")}}}]");
            if (a.EndsWith("reverse --list", StringComparison.Ordinal)) return new(0, "");
            return new(0, "");
        };
    }

    private AdbEnroller Enroller(bool allow) => Enroller(new FixedPrompt(allow));

    private AdbEnroller Enroller(FixedPrompt prompt)
    {
        var adb = new Adb(_tools, "/fake/adb");
        return new(adb, _store, prompt, new AgentOptions(8765), WifiSetupTests.Setup(adb, _store, _home), NullLogger<AdbEnroller>.Instance);
    }

    [Fact]
    public async Task ADeclinedPhoneIsNotEnrolledAndGetsNoTunnel()
    {
        await Enroller(allow: false).RoundAsync(CancellationToken.None);
        Assert.Empty(_store.All());
        Assert.DoesNotContain(_tools.Calls, c => c.Contains("--method enrol", StringComparison.Ordinal));
        Assert.DoesNotContain(_tools.Calls, c => c.Contains("reverse tcp:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAllowedPhoneGetsAKeyThenATunnel()
    {
        await Enroller(allow: true).RoundAsync(CancellationToken.None);
        var device = Assert.Single(_store.All());
        Assert.Equal("PHONE1", device.Serial);
        Assert.Equal("Pixel 4a", device.Model);
        var enrolCall = Assert.Single(_tools.Calls, c => c.Contains("--method enrol", StringComparison.Ordinal));
        Assert.Contains($"--arg {device.Id}", enrolCall, StringComparison.Ordinal);
        var tunnel = _tools.Calls.FindIndex(c => c.Contains("reverse tcp:8765 tcp:8765", StringComparison.Ordinal));
        Assert.True(tunnel > _tools.Calls.IndexOf(enrolCall), "the tunnel must come after the key");
    }

    [Fact]
    public async Task AFailedKeyDeliveryUndoesTheEnrolment()
    {
        _deliverOk = false;
        await Enroller(allow: true).RoundAsync(CancellationToken.None);
        Assert.All(_store.All(), d => Assert.True(d.Revoked));
        Assert.DoesNotContain(_tools.Calls, c => c.Contains("reverse tcp:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAlreadyEnrolledPhoneIsNotAskedAgain()
    {
        var (device, _) = _store.Enrol("PHONE1", "Pixel 4a");
        _appHasId = device.Id;
        var prompt = new FixedPrompt(allow: false);
        await Enroller(prompt).RoundAsync(CancellationToken.None);
        Assert.Equal(0, prompt.Asked);
        Assert.Contains(_tools.Calls, c => c.Contains("reverse tcp:8765 tcp:8765", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APhoneWhoseAppLostItsKeyIsAskedAgain()
    {
        _store.Enrol("PHONE1", "Pixel 4a");
        _appHasId = null; // app reinstalled
        var prompt = new FixedPrompt(allow: false);
        await Enroller(prompt).RoundAsync(CancellationToken.None);
        Assert.Equal(1, prompt.Asked);
    }

    /// <summary>T21: the same phone over wireless adb (adb calls it ip:port) is not a new phone.</summary>
    [Fact]
    public async Task AnEnrolledPhoneOverWirelessAdbIsNotAskedAgain()
    {
        var (device, _) = _store.Enrol("PHONE1", "Pixel 4a");
        _appHasId = device.Id;
        _transport = "10.0.0.5:41235";
        var prompt = new FixedPrompt(allow: true);
        await Enroller(prompt).RoundAsync(CancellationToken.None);
        Assert.Equal(0, prompt.Asked);
        Assert.Single(_store.All());
        Assert.Contains(_tools.Calls, c => c.StartsWith("-s 10.0.0.5:41235 reverse tcp:8765", StringComparison.Ordinal)); // adb commands use the connection
    }

    /// <summary>T21: a phone enrolled under its wireless name before the fix is moved to its hardware serial, not enrolled again.</summary>
    [Fact]
    public async Task ARecordFromWirelessAdbMovesToTheHardwareSerial()
    {
        var (device, _) = _store.Enrol("10.0.0.5:41235", "Pixel 4a");
        _appHasId = device.Id;
        var prompt = new FixedPrompt(allow: true);
        await Enroller(prompt).RoundAsync(CancellationToken.None); // now on USB, reporting PHONE1
        Assert.Equal(0, prompt.Asked);
        Assert.Equal("PHONE1", _store.Active(device.Id)!.Serial);
    }

    /// <summary>A hardware serial is never overwritten, even if another phone's app claims that record's id.</summary>
    [Fact]
    public async Task ARealSerialIsNeverMoved()
    {
        var (other, _) = _store.Enrol("OTHERPHONE", "Pixel 7");
        _appHasId = other.Id; // a different phone (PHONE1) claiming another phone's device id
        var prompt = new FixedPrompt(allow: false);
        await Enroller(prompt).RoundAsync(CancellationToken.None);
        Assert.Equal("OTHERPHONE", _store.Active(other.Id)!.Serial);
        Assert.Equal(1, prompt.Asked); // treated as a new phone: the user decides
    }

    [Fact]
    public async Task WithoutAHardwareSerialTheConnectionNameIsUsed()
    {
        _hardwareSerial = "";
        await Enroller(allow: true).RoundAsync(CancellationToken.None);
        Assert.Equal("PHONE1", Assert.Single(_store.All()).Serial);
    }

    /// <summary>Regression: a status check that fails must not revoke a working key or prompt (seen on a real phone).</summary>
    [Fact]
    public async Task AnAppThatDoesNotAnswerKeepsItsEnrolment()
    {
        var (device, _) = _store.Enrol("PHONE1", "Pixel 4a");
        _appSilent = true;
        var prompt = new FixedPrompt(allow: true);
        await Enroller(prompt).RoundAsync(CancellationToken.None);
        Assert.Equal(0, prompt.Asked);
        Assert.False(_store.Active(device.Id) is null);
        Assert.DoesNotContain(_tools.Calls, c => c.Contains("--method enrol", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, "Result: Bundle[{enrolled=false}]", "NotEnrolled")]
    [InlineData(0, "Result: Bundle[{id=0123456789abcdef, enrolled=true}]", "Enrolled")]
    [InlineData(255, "", "Unknown")]
    [InlineData(0, "Error while accessing provider", "Unknown")]
    [InlineData(0, "Result: Bundle[{enrolled=true}]", "Unknown")]   // claims a key but gives no valid id
    public void StatusAnswersAreReadStrictly(int exit, string output, string expected) =>
        Assert.Equal(expected, Adb.ParseStatus(new ToolResult(exit, output)).Status.ToString());

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private sealed class FixedPrompt(bool allow) : IEnrolPrompt
    {
        public int Asked { get; private set; }
        public Task<bool> AskAsync(string model, string serial, CancellationToken ct) { Asked++; return Task.FromResult(allow); }
    }
}
