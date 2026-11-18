using DeskPanel.Agent.Core;

namespace DeskPanel.Agent.Enrolment;

/// <summary>Asks the person at the PC whether a newly plugged-in phone may be enrolled.</summary>
public interface IEnrolPrompt
{
    Task<bool> AskAsync(string model, string serial, CancellationToken ct);
}

/// <summary>
/// macOS dialog via osascript. The phone's model name is untrusted, so it goes in as an argv item, never spliced into
/// the AppleScript source. "Don't Allow" is the default and the answer when nobody responds within 2 minutes.
/// </summary>
public sealed class MacEnrolPrompt(IToolRunner tools) : IEnrolPrompt
{
    private const string Script = """
        on run argv
          set msg to "A phone wants to show this Mac's dashboard:" & return & return & (item 1 of argv) & "  (" & (item 2 of argv) & ")" & return & return & "Allow only if you just plugged in your own phone."
          display dialog msg with title "DeskPanel" buttons {"Don't Allow", "Allow"} default button "Don't Allow" cancel button "Don't Allow" with icon caution giving up after 120
        end run
        """;

    public async Task<bool> AskAsync(string model, string serial, CancellationToken ct)
    {
        var r = await tools.RunAsync("/usr/bin/osascript", ["-e", Script, model, serial], TimeSpan.FromSeconds(130), ct);
        return IsAllowed(r);
    }

    internal static bool IsAllowed(ToolResult r) =>
        r.ExitCode == 0 && r.Output.Contains("button returned:Allow", StringComparison.Ordinal)
        && !r.Output.Contains("gave up:true", StringComparison.Ordinal);
}

/// <summary>Other platforms until their dialog exists: never enrol silently.</summary>
public sealed class DenyEnrolPrompt : IEnrolPrompt
{
    public Task<bool> AskAsync(string model, string serial, CancellationToken ct) => Task.FromResult(false);
}
