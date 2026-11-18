using System.Diagnostics;
using System.Text;

namespace DeskPanel.Agent.Core;

public sealed record ToolResult(int ExitCode, string Output);

/// <summary>Runs a helper program: no shell, arguments as a list, a timeout that kills the whole tree.</summary>
public interface IToolRunner
{
    Task<ToolResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);
}

public sealed class ToolRunner(AgentPaths paths) : IToolRunner
{
    public async Task<ToolResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = paths.Home, // never the caller's folder: a planted helper there must not be picked up
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return new ToolResult(-1, $"{Path.GetFileName(file)} timed out");
        }
        var output = await stdout;
        await stderr; // drained so the process never blocks on a full pipe
        return new ToolResult(p.ExitCode, output);
    }
}
