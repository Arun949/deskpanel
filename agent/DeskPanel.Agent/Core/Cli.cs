using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskPanel.Agent.Core;

/// <summary>What the command line asks for: one of the commands, or (no command) run the agent.</summary>
/// <param name="HostArgs">the host settings the in-process test host passes (environment, content root, app name)</param>
public sealed record CliRequest(string? Command, AgentOptions Options, bool Background, bool KeepData, IReadOnlyList<string> HostArgs)
{
    private static readonly string[] HostSettings = ["--environment=", "--contentRoot=", "--applicationName="];

    public const string Usage = """
        Usage: deskpanel-agent [command] [--port N]

        Without a command, the agent runs in this window (Ctrl+C stops it).

        Commands:
          install               copy this program to its per-user place, start it now and at every login
          uninstall             stop it, remove the login item and the program, and delete settings and phone keys
          uninstall --keep-data the same, but keep settings and phone keys (for a reinstall)
          status                show whether it's installed and running
          settings              open the settings page in your browser (the agent must be running)
          --version             print the version
        """;

    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal) { "install", "uninstall", "status", "settings", "help", "--help", "-h", "--version" };

    /// <summary>Parses the arguments. Anything unknown is an error: a typo must not start the agent with defaults.</summary>
    public static CliRequest Parse(IReadOnlyList<string> args, string? environmentPort = null)
    {
        string? command = null;
        bool background = false, keepData = false;
        var rest = new List<string>();
        var host = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (i == 0 && Commands.Contains(a)) command = a;
            else if (a == "--port") { rest.Add(a); if (i + 1 < args.Count) rest.Add(args[++i]); }
            else if (a == "--background" && command is null) background = true;
            else if (a == "--keep-data" && command == "uninstall") keepData = true;
            else if (command is null && HostSettings.Any(h => a.StartsWith(h, StringComparison.Ordinal))) host.Add(a);
            else throw new ArgumentException($"unknown argument '{a}'");
        }
        command = command switch { "--help" or "-h" => "help", _ => command };
        return new CliRequest(command, AgentOptions.From(rest, environmentPort), background, keepData, host);
    }

    /// <summary>Runs a command and returns its exit code, or null when the agent itself should run.</summary>
    public static int? Run(CliRequest request, TextWriter output)
    {
        switch (request.Command)
        {
            case null: return null;
            case "help": output.WriteLine(Usage); return 0;
            case "--version": output.WriteLine(new AgentRun().Version); return 0;
        }
        var paths = new AgentPaths();
        var installer = new Installer(paths, request.Options, output);
        return request.Command switch
        {
            "install" => installer.Install(),
            "uninstall" => installer.Uninstall(request.KeepData),
            "status" => installer.Status(),
            "settings" => OpenSettings(paths, request.Options, output),
            _ => throw new InvalidOperationException(request.Command),
        };
    }

    /// <summary>Opens the current one-time login link from the owner-only admin-link file.</summary>
    private static int OpenSettings(AgentPaths paths, AgentOptions options, TextWriter output)
    {
        var file = Path.Combine(paths.Home, "admin-link");
        var link = File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        // the file is owner-only, but never hand the browser anything other than this agent's own login page
        if (link is null || !link.StartsWith($"http://127.0.0.1:{options.Port}/admin/login?t=", StringComparison.Ordinal))
        {
            output.WriteLine($"No settings link for port {options.Port}. Is the agent running? (deskpanel-agent status)");
            return 1;
        }
        if (OperatingSystem.IsMacOS()) Process.Start("/usr/bin/open", [link])?.Dispose();
        else if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose();
        else { output.WriteLine($"Open this link in your browser (it works once): {file}"); return 0; }
        output.WriteLine("Opened the settings page in your browser.");
        return 0;
    }
}

/// <summary>
/// Background mode (started at login): no window on Windows, and output goes to agent.log in the data folder
/// (owner-only, rolled over at 2 MB with one old file kept) instead of a terminal nobody sees.
/// </summary>
public static partial class BackgroundMode
{
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Enter(AgentPaths paths)
    {
        if (OperatingSystem.IsWindows()) FreeConsole();
        var log = Path.Combine(paths.Home, "agent.log");
        if (File.Exists(log) && new FileInfo(log).Length > MaxBytes) File.Move(log, log + ".1", overwrite: true);
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var writer = new StreamWriter(new FileStream(log, options)) { AutoFlush = true };
        Console.SetOut(writer);
        Console.SetError(writer);
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();
}
