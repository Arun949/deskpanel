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
            .Concat([
                "/opt/homebrew/bin/adb",
                "/usr/local/bin/adb",
                System.IO.Path.Combine(home, "Library", "Android", "sdk", "platform-tools", "adb"),
            ]);
        return candidates.FirstOrDefault(File.Exists);
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
}
