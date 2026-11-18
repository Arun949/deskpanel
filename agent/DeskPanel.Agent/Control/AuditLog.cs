using System.Text.Json;
using DeskPanel.Agent.Core;

namespace DeskPanel.Agent.Control;

/// <param name="Result">ok, denied, not-found, rate-limited, replayed, failed</param>
public sealed record AuditEntry(long Ts, string DeviceId, string Action, string Result, long Ms, string? Detail = null);

/// <summary>
/// Append-only record of every control attempt (PhoneBox logged only "shortcut: label", Module 7). It holds action ids and
/// outcomes, never targets, key material or typed text. audit.jsonl is owner-only and rolls over at 1 MB (one .1 kept).
/// </summary>
public sealed class AuditLog(AgentPaths paths)
{
    internal const long MaxBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _lock = new();
    private string FilePath => Path.Combine(paths.Home, "audit.jsonl");

    public void Write(AuditEntry entry)
    {
        lock (_lock)
        {
            var path = FilePath;
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
            var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var stream = new FileStream(path, options);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(JsonSerializer.Serialize(entry, Json));
        }
    }

    /// <summary>The newest entries first.</summary>
    public IReadOnlyList<AuditEntry> Recent(int limit)
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath)) return [];
            var entries = new List<AuditEntry>();
            foreach (var line in File.ReadLines(FilePath).Reverse().Take(Math.Clamp(limit, 1, 1000)))
            {
                try { if (JsonSerializer.Deserialize<AuditEntry>(line, Json) is { } e) entries.Add(e); }
                catch (JsonException) { }
            }
            return entries;
        }
    }
}
