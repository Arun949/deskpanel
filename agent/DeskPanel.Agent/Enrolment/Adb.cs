using System.Text.RegularExpressions;
using DeskPanel.Agent.Core;

namespace DeskPanel.Agent.Enrolment;

/// <summary>The few adb commands enrolment needs. Every value placed on an adb command line is validated first.</summary>
public sealed partial class Adb(IToolRunner tools, string? adbPath = null)
{
    public const string Package = "app.deskpanel";
    public const string EnrolAuthority = "app.deskpanel.enrol";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public string? Path { get; } = adbPath ?? Find();

    public static string? Find()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => System.IO.Path.Combine(d, OperatingSystem.IsWindows() ? "adb.exe" : "adb"))
            .Concat(OperatingSystem.IsWindows() ? WindowsCandidates() : [
                "/opt/homebrew/bin/adb",
                "/usr/local/bin/adb",
                System.IO.Path.Combine(home, "Library", "Android", "sdk", "platform-tools", "adb"),
            ]);
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Android Studio's SDK and the WinGet package (Google.PlatformTools) on Windows.</summary>
    private static IEnumerable<string> WindowsCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return System.IO.Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe");
        var winget = System.IO.Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(winget)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(winget, "Google.PlatformTools*"))
            yield return System.IO.Path.Combine(dir, "platform-tools", "adb.exe");
    }

    public async Task<IReadOnlyList<string>> DevicesAsync(CancellationToken ct) =>
        ParseDevices((await RunAsync(["devices"], ct)).Output);

    public async Task<string> ModelAsync(string serial, CancellationToken ct) =>
        CleanModel((await RunAsync(["-s", Serial(serial), "shell", "getprop", "ro.product.model"], ct)).Output);

    public async Task<bool> AppInstalledAsync(string serial, CancellationToken ct) =>
        (await RunAsync(["-s", Serial(serial), "shell", "pm", "path", Package], ct)).Output.Contains("package:", StringComparison.Ordinal);

    /// <summary>What the app's enrolment provider says about its key.</summary>
    public enum KeyStatus { Unknown, NotEnrolled, Enrolled }

    /// <summary>
    /// Asks the app which device id it holds a key for. <see cref="KeyStatus.Unknown"/> when the app didn't answer
    /// (starting up, busy, provider unreachable): that must never be taken as "no key", or a hiccup would revoke a working
    /// enrolment and prompt again.
    /// </summary>
    public async Task<(KeyStatus Status, string? Id)> EnrolledIdAsync(string serial, CancellationToken ct)
    {
        var r = await RunAsync(["-s", Serial(serial), "shell", "content", "call", "--uri", $"content://{EnrolAuthority}", "--method", "status"], ct);
        return ParseStatus(r);
    }

    public static (KeyStatus Status, string? Id) ParseStatus(ToolResult r)
    {
        if (r.ExitCode != 0) return (KeyStatus.Unknown, null);
        var bundle = ParseBundle(r.Output);
        return bundle.GetValueOrDefault("enrolled") switch
        {
            "false" => (KeyStatus.NotEnrolled, null),
            "true" when bundle.GetValueOrDefault("id") is { } id && DeviceIdRe().IsMatch(id) => (KeyStatus.Enrolled, id),
            _ => (KeyStatus.Unknown, null),
        };
    }

    /// <summary>
    /// Hands the key to the app. The app's provider accepts it only from the adb shell user (uid 2000), never from
    /// another app, so physical USB access is what enrolment rests on.
    /// </summary>
    public async Task<bool> DeliverKeyAsync(string serial, string deviceId, string key, CancellationToken ct)
    {
        if (!DeviceIdRe().IsMatch(deviceId) || !KeyRe().IsMatch(key)) throw new ArgumentException("bad device id or key");
        var r = await RunAsync(["-s", Serial(serial), "shell", "content", "call", "--uri", $"content://{EnrolAuthority}",
            "--method", "enrol", "--arg", deviceId, "--extra", $"key:s:{key}"], ct);
        return ParseBundle(r.Output).GetValueOrDefault("ok") == "true";
    }

    /// <summary>
    /// Asks the app for the public key of its Wi-Fi client key (made in the Android Keystore, never exportable) and
    /// returns its pin, or null if the app didn't give a valid P-256 key. Same uid-2000 lock as enrolment.
    /// </summary>
    public async Task<string?> WifiClientPinAsync(string serial, CancellationToken ct)
    {
        var r = await RunAsync(["-s", Serial(serial), "shell", "content", "call", "--uri", $"content://{EnrolAuthority}", "--method", "wifiKey"], ct);
        return r.ExitCode == 0 ? PinFromSpki(ParseBundle(r.Output).GetValueOrDefault("spki")) : null;
    }

    /// <summary>Gives the app what it needs to reach this agent over Wi-Fi, or (null) tells it to forget Wi-Fi.</summary>
    public async Task<bool> SetWifiAsync(string serial, int? port, string? agentPin, IReadOnlyList<string> hosts, CancellationToken ct)
    {
        IReadOnlyList<string> args;
        if (port is null) args = ["-s", Serial(serial), "shell", "content", "call", "--uri", $"content://{EnrolAuthority}", "--method", "wifiOff"];
        else
        {
            // every value is checked here, because adb shell passes the command line through the phone's shell
            if (port is < 1024 or > 65535 || agentPin is null || !KeyRe().IsMatch(agentPin)) throw new ArgumentException("bad Wi-Fi details");
            var clean = hosts.Where(h => System.Net.IPAddress.TryParse(h, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Take(4).ToList();
            if (clean.Count == 0) throw new ArgumentException("no address to give the phone");
            args = ["-s", Serial(serial), "shell", "content", "call", "--uri", $"content://{EnrolAuthority}", "--method", "wifiOn",
                "--extra", $"port:i:{port}", "--extra", $"pin:s:{agentPin}", "--extra", $"hosts:s:{string.Join(',', clean)}"];
        }
        return ParseBundle((await RunAsync(args, ct)).Output).GetValueOrDefault("ok") == "true";
    }

    /// <summary>
    /// The pin of a SubjectPublicKeyInfo sent by the app as base64url, only if it really is an EC P-256 public key
    /// (anything else, including garbage, gives null).
    /// </summary>
    public static string? PinFromSpki(string? base64Url)
    {
        if (base64Url is null || base64Url.Length is < 80 or > 200 || !Base64UrlRe().IsMatch(base64Url)) return null;
        try
        {
            var der = Convert.FromBase64String(base64Url.Replace('-', '+').Replace('_', '/').PadRight((base64Url.Length + 3) / 4 * 4, '='));
            using var key = System.Security.Cryptography.ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(der, out var read);
            if (read != der.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") return null;
            return Security.Wifi.WifiIdentity.SpkiPin(der);
        }
        catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException) { return null; }
    }

    public async Task EnsureReverseAsync(string serial, int port, CancellationToken ct)
    {
        var rule = $"tcp:{port}";
        var list = (await RunAsync(["-s", Serial(serial), "reverse", "--list"], ct)).Output;
        if (list.Split('\n').Any(l => l.TrimEnd().EndsWith($"tcp:{AgentOptions.DefaultPort} {rule}", StringComparison.Ordinal))) return;
        await RunAsync(["-s", Serial(serial), "reverse", $"tcp:{AgentOptions.DefaultPort}", rule], ct);
    }

    public Task OpenPanelAsync(string serial, CancellationToken ct) =>
        RunAsync(["-s", Serial(serial), "shell", "am", "start", "-n", $"{Package}/.MainActivity"], ct);

    private async Task<ToolResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (Path is null) throw new InvalidOperationException("adb not found");
        return await tools.RunAsync(Path, args, Timeout, ct);
    }

    // ---------------------------------------------------------------- parsing (pure, unit-tested)

    /// <summary>Serials of attached devices in the "device" state ("unauthorized" ones haven't allowed USB debugging).</summary>
    public static IReadOnlyList<string> ParseDevices(string output) => output
        .Split('\n')
        .Skip(1)
        .Select(l => l.Trim().Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries))
        .Where(p => p.Length >= 2 && p[1] == "device" && SerialRe().IsMatch(p[0]))
        .Select(p => p[0])
        .ToList();

    /// <summary>Parses <c>Result: Bundle[{ok=true, id=abc}]</c>, the output of <c>adb shell content call</c>.</summary>
    public static Dictionary<string, string> ParseBundle(string output)
    {
        var m = BundleRe().Match(output);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!m.Success) return map;
        foreach (var part in m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=', StringComparison.Ordinal);
            if (i > 0) map[part[..i]] = part[(i + 1)..];
        }
        return map;
    }

    /// <summary>The phone's model name, as untrusted text: printable ASCII only, at most 40 characters.</summary>
    public static string CleanModel(string raw)
    {
        var s = new string(raw.Trim().Where(c => c is >= ' ' and <= '~').Take(40).ToArray());
        return s.Length > 0 ? s : "Android phone";
    }

    private static string Serial(string serial) =>
        SerialRe().IsMatch(serial) ? serial : throw new ArgumentException("bad serial");

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,64}$")] private static partial Regex SerialRe();
    [GeneratedRegex("^[0-9a-f]{16}$")] private static partial Regex DeviceIdRe();
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")] private static partial Regex KeyRe();
    [GeneratedRegex(@"Bundle\[\{(.*)\}\]")] private static partial Regex BundleRe();
    [GeneratedRegex("^[A-Za-z0-9_-]+$")] private static partial Regex Base64UrlRe();
}
