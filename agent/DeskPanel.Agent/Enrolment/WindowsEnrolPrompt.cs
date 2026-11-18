using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskPanel.Agent.Admin;
using DeskPanel.Agent.Control;
using DeskPanel.Agent.Core;

namespace DeskPanel.Agent.Enrolment;

/// <summary>
/// The Windows enrolment question: a topmost Yes/No box with "No" as the default, answered "No" after 2 minutes.
/// The phone's model name is untrusted but only ever shown as text.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsEnrolPrompt : IEnrolPrompt
{
    private const uint MbYesNo = 0x4, MbIconWarning = 0x30, MbDefButton2 = 0x100, MbSetForeground = 0x10000, MbTopmost = 0x40000;
    private const int IdYes = 6;

    public Task<bool> AskAsync(string model, string serial, CancellationToken ct) => Task.Run(() =>
    {
        var text = $"A phone wants to show this computer's dashboard:\n\n{model}  ({serial})\n\nAllow only if you just plugged in your own phone.";
        var answer = MessageBoxTimeoutW(IntPtr.Zero, text, "DeskPanel",
            MbYesNo | MbIconWarning | MbDefButton2 | MbSetForeground | MbTopmost, 0, 120_000);
        return answer == IdYes;
    }, ct);

    // Exported by user32 since Windows XP; returns 32000 when the timeout closes the box.
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxTimeoutW(IntPtr hwnd, string text, string caption, uint type, ushort language, uint milliseconds);
}

/// <summary>
/// Picks the program for a "launch" action in a native Open dialog (PowerShell + Windows Forms on an STA thread).
/// Shortcuts are resolved to their .exe, so a .lnk's hidden arguments never come along.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAppPicker(IToolRunner tools) : IAppPicker
{
    private const string Script =
        "Add-Type -AssemblyName System.Windows.Forms;" +
        "$d = New-Object System.Windows.Forms.OpenFileDialog;" +
        "$d.Title = 'Choose a program your phone may open';" +
        "$d.Filter = 'Programs (*.exe;*.lnk)|*.exe;*.lnk';" +
        "$d.DereferenceLinks = $true;" +
        "$d.InitialDirectory = [Environment]::GetFolderPath('CommonPrograms');" +
        "if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { [Console]::Out.Write($d.FileName) }";

    public async Task<string?> PickAsync(CancellationToken ct)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var r = await tools.RunAsync(powershell, ["-NoProfile", "-STA", "-Command", Script], TimeSpan.FromMinutes(2), ct);
        var path = r.Output.Trim();
        return r.ExitCode == 0 && ActionStore.IsLaunchTarget(path) ? path : null;
    }
}
