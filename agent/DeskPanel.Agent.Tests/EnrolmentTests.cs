using DeskPanel.Agent.Core;
using DeskPanel.Agent.Enrolment;
using DeskPanel.Agent.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPanel.Agent.Tests;

public sealed class AdbParsingTests
{
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
    private bool _deliverOk = true;
    private string? _appHasId;
    private bool _appSilent; // provider unreachable (app restarting)

    public AdbEnrollerTests()
    {
        _store = new DeviceStore(new AgentPaths(_home));
        _tools.Respond = args =>
        {
            var a = string.Join(' ', args);
            if (a == "devices") return new(0, "List of devices attached\nPHONE1\tdevice\n");
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

    private AdbEnroller Enroller(bool allow) =>
        new(new Adb(_tools, "/fake/adb"), _store, new FixedPrompt(allow), new AgentOptions(8765), NullLogger<AdbEnroller>.Instance);

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
        await new AdbEnroller(new Adb(_tools, "/fake/adb"), _store, prompt, new AgentOptions(8765), NullLogger<AdbEnroller>.Instance)
            .RoundAsync(CancellationToken.None);
        Assert.Equal(0, prompt.Asked);
        Assert.Contains(_tools.Calls, c => c.Contains("reverse tcp:8765 tcp:8765", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APhoneWhoseAppLostItsKeyIsAskedAgain()
    {
        _store.Enrol("PHONE1", "Pixel 4a");
        _appHasId = null; // app reinstalled
        var prompt = new FixedPrompt(allow: false);
        await new AdbEnroller(new Adb(_tools, "/fake/adb"), _store, prompt, new AgentOptions(8765), NullLogger<AdbEnroller>.Instance)
            .RoundAsync(CancellationToken.None);
        Assert.Equal(1, prompt.Asked);
    }

    /// <summary>Regression: a status check that fails must not revoke a working key or prompt (seen on a real phone).</summary>
    [Fact]
    public async Task AnAppThatDoesNotAnswerKeepsItsEnrolment()
    {
        var (device, _) = _store.Enrol("PHONE1", "Pixel 4a");
        _appSilent = true;
        var prompt = new FixedPrompt(allow: true);
        await new AdbEnroller(new Adb(_tools, "/fake/adb"), _store, prompt, new AgentOptions(8765), NullLogger<AdbEnroller>.Instance)
            .RoundAsync(CancellationToken.None);
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
