using System.Xml.Linq;
using DeskPanel.Agent.Core;

namespace DeskPanel.Agent.Tests;

public sealed class CliTests
{
    [Fact]
    public void NoArgumentsRunsTheAgent()
    {
        var r = CliRequest.Parse([]);
        Assert.Null(r.Command);
        Assert.False(r.Background);
        Assert.Equal(AgentOptions.DefaultPort, r.Options.Port);
    }

    [Fact]
    public void LoginItemArguments()
    {
        var r = CliRequest.Parse(["--background", "--port", "9000"]);
        Assert.True(r.Background);
        Assert.Equal(9000, r.Options.Port);
        Assert.Empty(r.HostArgs);
    }

    [Fact]
    public void OnlyTheTestHostsSettingsPassToTheWebHost()
    {
        Assert.Equal(["--environment=Development"], CliRequest.Parse(["--environment=Development"]).HostArgs);
        Assert.Throws<ArgumentException>(() => CliRequest.Parse(["install", "--environment=Development"]));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("status")]
    [InlineData("settings")]
    [InlineData("--version")]
    public void Commands(string command) => Assert.Equal(command, CliRequest.Parse([command]).Command);

    [Fact]
    public void HelpAliases()
    {
        Assert.Equal("help", CliRequest.Parse(["--help"]).Command);
        Assert.Equal("help", CliRequest.Parse(["-h"]).Command);
    }

    [Fact]
    public void UninstallCanKeepData()
    {
        Assert.False(CliRequest.Parse(["uninstall"]).KeepData);
        Assert.True(CliRequest.Parse(["uninstall", "--keep-data"]).KeepData);
    }

    [Fact]
    public void CommandsKeepThePort() => Assert.Equal(9100, CliRequest.Parse(["status", "--port", "9100"]).Options.Port);

    [Theory]
    [InlineData("--prot", "9000")]              // a typo must not start the agent with defaults
    [InlineData("instal")]
    [InlineData("--port", "9000", "install")]   // the command comes first
    [InlineData("install", "--background")]
    [InlineData("--keep-data")]
    [InlineData("status", "--keep-data")]
    [InlineData("--urls", "http://0.0.0.0:80")] // ASP.NET's own switches are not accepted either
    [InlineData("--urls=http://0.0.0.0:80")]
    [InlineData("--port=9000")]
    public void UnknownOrMisplacedArgumentsAreRejected(params string[] args) =>
        Assert.Throws<ArgumentException>(() => CliRequest.Parse(args));
}

public sealed class InstallLayoutTests
{
    [Fact]
    public void MacPaths()
    {
        var l = InstallLayout.For(HostOs.MacOS, "/Users/a", "/Users/a/.local/share", "/Users/a/Library/Application Support/DeskPanel");
        // Path.Combine, so the test also passes where the separator is a backslash
        Assert.Equal(Path.Combine("/Users/a/Library/Application Support/DeskPanel", "bin", "deskpanel-agent"), l.Executable);
        Assert.Equal(Path.Combine("/Users/a", "Library", "LaunchAgents", "app.deskpanel.agent.plist"), l.LaunchAgentPlist);
        Assert.Equal(Path.Combine("/Users/a/Library/Application Support/DeskPanel", "agent.log"), l.LogFile);
    }

    [Fact]
    public void WindowsPathsNeedNoAdministrator()
    {
        var l = InstallLayout.For(HostOs.Windows, "C:/Users/a", "C:/Users/a/AppData/Local", "C:/Users/a/AppData/Roaming/DeskPanel");
        Assert.Equal(Path.Combine("C:/Users/a/AppData/Local", "Programs", "DeskPanel", "deskpanel-agent.exe"), l.Executable);
        Assert.Null(l.LaunchAgentPlist);
    }

    [Fact]
    public void PlistIsValidAndEscapesThePath()
    {
        var exe = "/Users/a & b/Library/Application Support/DeskPanel/bin/deskpanel-agent";
        var xml = XDocument.Parse(InstallLayout.LaunchdPlist(exe, 9000), LoadOptions.None);
        var dict = xml.Root!.Element("dict")!.Elements().ToList();
        string Key(string k) => dict[dict.FindIndex(e => e.Name == "key" && e.Value == k) + 1].ToString(SaveOptions.DisableFormatting);
        Assert.Contains("app.deskpanel.agent", Key("Label"));
        var args = dict[dict.FindIndex(e => e.Value == "ProgramArguments") + 1].Elements("string").Select(e => e.Value).ToList();
        Assert.Equal([exe, "--background", "--port", "9000"], args);
        Assert.Equal("<true />", Key("RunAtLoad"));
        Assert.Contains("<key>SuccessfulExit</key><false />", Key("KeepAlive")); // restart after a crash, not after a clean exit
        Assert.Contains("Aqua", Key("LimitLoadToSessionType"));                // GUI session: the enrolment dialog needs it
    }

    [Fact]
    public void DefaultPortIsNotRepeated() => Assert.Equal(["--background"], InstallLayout.StartArguments(AgentOptions.DefaultPort));

    [Fact]
    public void RunCommandQuotesThePath() =>
        Assert.Equal("\"C:\\Users\\a b\\deskpanel-agent.exe\" --background",
            InstallLayout.RunCommand("C:\\Users\\a b\\deskpanel-agent.exe", AgentOptions.DefaultPort));
}

public sealed class UninstallTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("deskpanel-uninstall-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private (Installer, StringWriter, InstallLayout) Make()
    {
        var layout = InstallLayout.For(HostOs.Other, _home, _home, _home); // Other: never touches launchd or the registry
        var output = new StringWriter();
        return (new Installer(new AgentPaths(_home), new AgentOptions(AgentOptions.DefaultPort), output, layout), output, layout);
    }

    private void Seed(InstallLayout layout)
    {
        foreach (var f in Installer.DataFiles) File.WriteAllText(Path.Combine(_home, f), "x");
        Directory.CreateDirectory(Path.GetDirectoryName(layout.Executable)!);
        File.WriteAllText(layout.Executable, "binary");
    }

    [Fact]
    public void RemovesTheProgramAndOnlyTheAgentsOwnFiles()
    {
        var (installer, _, layout) = Make();
        Seed(layout);
        File.WriteAllText(Path.Combine(_home, "mine.txt"), "someone else's file"); // DESKPANEL_HOME may point anywhere
        Assert.Equal(0, installer.Uninstall(keepData: false));
        Assert.False(File.Exists(layout.Executable));
        Assert.All(Installer.DataFiles, f => Assert.False(File.Exists(Path.Combine(_home, f)), f));
        Assert.True(File.Exists(Path.Combine(_home, "mine.txt")));
    }

    [Fact]
    public void KeepDataKeepsKeysAndSettings()
    {
        var (installer, output, layout) = Make();
        Seed(layout);
        installer.Uninstall(keepData: true);
        Assert.False(File.Exists(layout.Executable));
        Assert.True(File.Exists(Path.Combine(_home, "devices.json")));
        Assert.Contains("Kept", output.ToString());
    }

    [Fact]
    public void InstallSaysItIsUnsupportedWhereThereIsNoLoginItem()
    {
        var (installer, output, _) = Make();
        Assert.Equal(2, installer.Install());
        Assert.Contains("macOS and Windows", output.ToString());
    }
}
