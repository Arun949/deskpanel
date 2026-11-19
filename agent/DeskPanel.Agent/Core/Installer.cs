using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;

namespace DeskPanel.Agent.Core;

public enum HostOs { MacOS, Windows, Other }

/// <summary>
/// Where an installed agent lives and how it starts at login. Per user, no administrator rights:
/// macOS: <c>~/Library/Application Support/DeskPanel/bin/deskpanel-agent</c> plus a LaunchAgent;
/// Windows: <c>%LOCALAPPDATA%\Programs\DeskPanel\deskpanel-agent.exe</c> plus a value under HKCU\…\Run.
/// </summary>
public sealed record InstallLayout(HostOs Os, string Executable, string? LaunchAgentPlist, string LogFile)
{
    public const string LaunchdLabel = "app.deskpanel.agent";
    public const string RunValueName = "DeskPanel";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static HostOs CurrentOs => OperatingSystem.IsMacOS() ? HostOs.MacOS : OperatingSystem.IsWindows() ? HostOs.Windows : HostOs.Other;

    public static InstallLayout Current(AgentPaths paths) => For(CurrentOs,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        paths.Home);

    public static InstallLayout For(HostOs os, string userHome, string localAppData, string dataHome) => os switch
    {
        HostOs.MacOS => new(os, Path.Combine(dataHome, "bin", "deskpanel-agent"),
            Path.Combine(userHome, "Library", "LaunchAgents", LaunchdLabel + ".plist"), Path.Combine(dataHome, "agent.log")),
        HostOs.Windows => new(os, Path.Combine(localAppData, "Programs", "DeskPanel", "deskpanel-agent.exe"), null,
            Path.Combine(dataHome, "agent.log")),
        _ => new(os, Path.Combine(dataHome, "bin", "deskpanel-agent"), null, Path.Combine(dataHome, "agent.log")),
    };

    /// <summary>Arguments the login item starts the agent with.</summary>
    public static IReadOnlyList<string> StartArguments(int port) =>
        port == AgentOptions.DefaultPort ? ["--background"] : ["--background", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    /// <summary>
    /// The LaunchAgent: starts at login in the user's GUI session (dialogs need it), restarts after a crash but not
    /// after a clean exit. Output goes to the agent's own rotated log, not to launchd.
    /// </summary>
    public static string LaunchdPlist(string executable, int port)
    {
        var args = string.Concat(new[] { executable }.Concat(StartArguments(port))
            .Select(a => $"\n        <string>{SecurityElement.Escape(a)}</string>"));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{LaunchdLabel}</string>
                <key>ProgramArguments</key>
                <array>{args}
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <dict>
                    <key>SuccessfulExit</key>
                    <false/>
                </dict>
                <key>ThrottleInterval</key>
                <integer>10</integer>
                <key>ProcessType</key>
                <string>Interactive</string>
                <key>LimitLoadToSessionType</key>
                <string>Aqua</string>
            </dict>
            </plist>

            """;
    }

    /// <summary>The command line stored under HKCU\…\Run (path quoted: it usually contains spaces).</summary>
    public static string RunCommand(string executable, int port) =>
        $"\"{executable}\" " + string.Join(' ', StartArguments(port));
}

/// <summary>
/// <c>install</c>, <c>uninstall</c>, <c>status</c>: copy this executable to its per-user place, register it to start
/// at login, start it, and undo all of that. Files in the data folder are removed by name only, never a whole folder
/// the agent didn't create (DESKPANEL_HOME may point anywhere).
/// </summary>
/// <param name="layout">tests pass a <see cref="HostOs.Other"/> layout, so they never touch launchd or the registry</param>
public sealed partial class Installer(AgentPaths paths, AgentOptions options, TextWriter output, InstallLayout? layout = null)
{
    /// <summary>Everything the agent writes in its data folder.</summary>
    public static readonly string[] DataFiles =
        ["devices.json", "layout.json", "actions.json", "permissions.json", "admin-link", "audit.jsonl", "audit.jsonl.1", "agent.log", "agent.log.1", "wifi-key.pem", "wifi.json"];

    private readonly InstallLayout _layout = layout ?? InstallLayout.Current(paths);
    private bool Mac => _layout.Os == HostOs.MacOS && OperatingSystem.IsMacOS();
    private bool Windows => _layout.Os == HostOs.Windows && OperatingSystem.IsWindows();

    public int Install()
    {
        if (_layout.Os == HostOs.Other)
        {
            output.WriteLine("Installing as a login item is supported on macOS and Windows. On this system, run the agent directly.");
            return 2;
        }
        var source = Environment.ProcessPath;
        if (source is null || Path.GetFileNameWithoutExtension(source) is "dotnet")
        {
            output.WriteLine("Run install from the downloaded agent executable, not through `dotnet`.");
            return 2;
        }

        StopInstalled();
        if (!SamePath(source, _layout.Executable))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_layout.Executable)!);
            var tmp = _layout.Executable + ".new";
            File.Copy(source, tmp, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Move(tmp, _layout.Executable, overwrite: true);
        }
        output.WriteLine($"Installed {_layout.Executable}");

        if (Mac)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_layout.LaunchAgentPlist)!);
            File.WriteAllText(_layout.LaunchAgentPlist!, InstallLayout.LaunchdPlist(_layout.Executable, options.Port));
            var r = Launchctl("bootstrap", $"gui/{getuid()}", _layout.LaunchAgentPlist!);
            if (r.ExitCode != 0)
            {
                output.WriteLine($"Could not start it with launchctl: {r.Error.Trim()}");
                return 1;
            }
            output.WriteLine($"Starts at login: {_layout.LaunchAgentPlist}");
        }
        else if (Windows && OperatingSystem.IsWindows())
        {
            using (var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(InstallLayout.RunKey))
                run.SetValue(InstallLayout.RunValueName, InstallLayout.RunCommand(_layout.Executable, options.Port));
            output.WriteLine($@"Starts at login: HKCU\{InstallLayout.RunKey}\{InstallLayout.RunValueName}");
            var psi = new ProcessStartInfo(_layout.Executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.Home };
            foreach (var a in InstallLayout.StartArguments(options.Port)) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
        }

        var version = WaitForAgent(TimeSpan.FromSeconds(20));
        if (version is null)
        {
            output.WriteLine($"Installed, but the agent did not answer on port {options.Port}. See {_layout.LogFile}");
            return 1;
        }
        output.WriteLine($"""
            DeskPanel agent {version} is running on http://127.0.0.1:{options.Port} and will start at every login.

            Next:
              1. Install the DeskPanel app on your phone, turn on USB debugging, and plug it in.
              2. Answer "Allow" on this computer when it asks about the phone.
              3. Run `deskpanel-agent settings` (the installed copy: {_layout.Executable})
                 to choose what the phone may control.
            """);
        return 0;
    }

    public int Uninstall(bool keepData)
    {
        if (Mac && _layout.LaunchAgentPlist is { } plist)
        {
            Launchctl("bootout", $"gui/{getuid()}/{InstallLayout.LaunchdLabel}");
            if (File.Exists(plist)) { File.Delete(plist); output.WriteLine($"Removed {plist}"); }
        }
        else if (Windows && OperatingSystem.IsWindows())
        {
            using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(InstallLayout.RunKey, writable: true))
                if (run?.GetValue(InstallLayout.RunValueName) is not null)
                {
                    run.DeleteValue(InstallLayout.RunValueName);
                    output.WriteLine("Removed the login item");
                }
        }
        StopInstalled();

        if (File.Exists(_layout.Executable))
        {
            if (Windows && SamePath(Environment.ProcessPath, _layout.Executable))
                DeleteAfterExit(_layout.Executable); // Windows can't delete a running program
            else
                File.Delete(_layout.Executable);
            output.WriteLine($"Removed {_layout.Executable}");
        }
        TryRemoveEmptyDirectory(Path.GetDirectoryName(_layout.Executable)!);

        if (keepData)
            output.WriteLine($"Kept your settings and phone keys in {paths.Home}");
        else
        {
            foreach (var name in DataFiles.Concat(DataFiles.Select(f => f + ".tmp")))
            {
                var file = Path.Combine(paths.Home, name);
                if (File.Exists(file)) File.Delete(file);
            }
            TryRemoveEmptyDirectory(paths.Home);
            output.WriteLine($"Removed settings and phone keys from {paths.Home}");
        }
        output.WriteLine(Mac
            ? "Also: uninstall the app from your phone, and remove deskpanel-agent from System Settings → Privacy & Security → Accessibility if you added it."
            : "Also: uninstall the app from your phone.");
        return 0;
    }

    public int Status()
    {
        var installed = File.Exists(_layout.Executable);
        var atLogin = Mac ? File.Exists(_layout.LaunchAgentPlist)
            : Windows && OperatingSystem.IsWindows() && WindowsRunValue() is not null;
        var version = WaitForAgent(TimeSpan.Zero);
        output.WriteLine($"Installed:      {(installed ? _layout.Executable : "no")}");
        output.WriteLine($"Starts at login: {(atLogin ? "yes" : "no")}");
        output.WriteLine($"Running:        {(version is null ? $"no (nothing answers on port {options.Port})" : $"yes, version {version} on port {options.Port}")}");
        output.WriteLine($"Data folder:    {paths.Home}");
        output.WriteLine($"Log:            {_layout.LogFile}");
        return version is null ? 3 : 0;
    }

    /// <summary>Asks the agent for <c>/api/v1/hello</c> until it answers as DeskPanel or the time is up. Returns its version.</summary>
    private string? WaitForAgent(TimeSpan patience)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var until = DateTime.UtcNow + patience;
        do
        {
            try
            {
                var hello = http.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:{options.Port}/api/v1/hello").GetAwaiter().GetResult();
                if (hello.TryGetProperty("app", out var app) && app.GetString() == "DeskPanel")
                    return hello.GetProperty("version").GetString();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { }
            if (DateTime.UtcNow >= until) return null;
            Thread.Sleep(500);
        } while (true);
    }

    /// <summary>Stops a running installed copy (an upgrade replaces it; an uninstall removes it).</summary>
    private void StopInstalled()
    {
        if (Mac)
        {
            Launchctl("bootout", $"gui/{getuid()}/{InstallLayout.LaunchdLabel}");
            return;
        }
        if (!Windows) return;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_layout.Executable)))
        {
            using (p)
            {
                try
                {
                    if (p.Id == Environment.ProcessId || !SamePath(p.MainModule?.FileName, _layout.Executable)) continue;
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    private ToolResult Launchctl(params string[] args) =>
        new ToolRunner(paths).RunAsync("/bin/launchctl", args, TimeSpan.FromSeconds(15), CancellationToken.None).GetAwaiter().GetResult();

    [SupportedOSPlatform("windows")]
    private static string? WindowsRunValue()
    {
        using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(InstallLayout.RunKey);
        return run?.GetValue(InstallLayout.RunValueName) as string;
    }

    /// <summary>A short-lived cmd waits for this process to end, then deletes the file and its folder if empty.</summary>
    private static void DeleteAfterExit(string file)
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        // a raw command line: cmd.exe does its own parsing, so ArgumentList's escaping would get in the way
        var line = $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{file}\" & rmdir \"{Path.GetDirectoryName(file)}\"";
        Process.Start(new ProcessStartInfo(cmd, line) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
    }

    private static void TryRemoveEmptyDirectory(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [LibraryImport("libc")]
    private static partial uint getuid();
}
