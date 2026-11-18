namespace DeskPanel.Agent.Core;

/// <summary>Where the agent keeps its files. <c>DESKPANEL_HOME</c> overrides it (tests, portable installs).</summary>
public sealed class AgentPaths
{
    public AgentPaths(string? home = null)
    {
        Home = home
            ?? Environment.GetEnvironmentVariable("DESKPANEL_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskPanel");
        Directory.CreateDirectory(Home);
        PrivateFile.RestrictDirectory(Home);
    }

    public string Home { get; }
    public string Devices => Path.Combine(Home, "devices.json");
    public string Layout => Path.Combine(Home, "layout.json");
}

/// <summary>Owner-only files and atomic writes (PhoneBox wrote its TLS key with default permissions, Module 4).</summary>
public static class PrivateFile
{
    private const UnixFileMode OwnerRw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerRwx = OwnerRw | UnixFileMode.UserExecute;

    /// <summary>Writes <paramref name="text"/> to a temp file readable only by the owner, then renames it into place.</summary>
    public static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerRw;
        using (var stream = new FileStream(tmp, options))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            writer.Write(text);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, OwnerRw); // an existing tmp keeps its old mode
        File.Move(tmp, path, overwrite: true);
    }

    public static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return; // %APPDATA% is already per-user
        try { File.SetUnixFileMode(path, OwnerRwx); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
