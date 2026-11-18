using System.Text.Json;
using System.Text.RegularExpressions;
using DeskPanel.Agent.Core;

namespace DeskPanel.Agent.Layout;

public sealed record WidgetConfig(string Id, string Type, int X, int Y, int W, int H, Dictionary<string, JsonElement>? Options);

public sealed record LayoutDoc(int SchemaVersion, IReadOnlyList<WidgetConfig> Widgets);

/// <summary>
/// layout.json: the one page the phone shows. It is phone-visible by design, so it must never hold commands, paths, URLs
/// or secrets; it is validated on every load. M3 replaces the hand-edited file with the editor and a JSON schema.
/// </summary>
public sealed partial class LayoutStore(AgentPaths paths)
{
    public const int Cols = 12, Rows = 6, MaxWidgets = 30;
    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal) { "clock", "pomodoro", "stat", "now-playing" };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _lock = new();
    private (DateTime Stamp, LayoutDoc Doc)? _cache;

    public LayoutDoc Get()
    {
        lock (_lock)
        {
            if (!File.Exists(paths.Layout)) PrivateFile.WriteAtomic(paths.Layout, JsonSerializer.Serialize(Default, Json));
            var stamp = File.GetLastWriteTimeUtc(paths.Layout);
            if (_cache is { } c && c.Stamp == stamp) return c.Doc;
            LayoutDoc doc;
            try { doc = Validate(JsonSerializer.Deserialize<LayoutDoc>(File.ReadAllText(paths.Layout), Json)); }
            catch (JsonException) { doc = Default; } // a broken hand edit shows the default page, not an error
            _cache = (stamp, doc);
            return doc;
        }
    }

    public static readonly LayoutDoc Default = new(1,
    [
        new("clock", "clock", 0, 0, 4, 3, null),
        new("now-playing", "now-playing", 4, 0, 5, 3, null),
        new("pomodoro", "pomodoro", 9, 0, 3, 3, null),
        new("cpu", "stat", 0, 3, 4, 3, Opt("metric", "cpu")),
        new("ram", "stat", 4, 3, 4, 3, Opt("metric", "ram")),
        new("net", "stat", 8, 3, 4, 3, Opt("metric", "net")),
    ]);

    /// <summary>Known types only, unique slug ids, inside the grid, options limited to short strings, numbers and booleans.</summary>
    public static LayoutDoc Validate(LayoutDoc? doc)
    {
        if (doc?.Widgets is null) return Default;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var widgets = new List<WidgetConfig>();
        foreach (var w in doc.Widgets.Take(MaxWidgets))
        {
            if (w is null || !Types.Contains(w.Type) || !IdRe().IsMatch(w.Id ?? "") || !ids.Add(w.Id!)) continue;
            var x = Math.Clamp(w.X, 0, Cols - 1);
            var y = Math.Clamp(w.Y, 0, Rows - 1);
            widgets.Add(w with { X = x, Y = y, W = Math.Clamp(w.W, 1, Cols - x), H = Math.Clamp(w.H, 1, Rows - y), Options = CleanOptions(w.Options) });
        }
        return new LayoutDoc(1, widgets);
    }

    private static Dictionary<string, JsonElement>? CleanOptions(Dictionary<string, JsonElement>? options)
    {
        if (options is null) return null;
        var clean = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in options.Take(20))
        {
            if (!IdRe().IsMatch(key)) continue;
            var ok = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()!.Length <= 200,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => true,
                _ => false, // no objects or arrays in M1
            };
            if (ok) clean[key] = value.Clone();
        }
        return clean;
    }

    private static Dictionary<string, JsonElement> Opt(string key, string value) =>
        new(StringComparer.Ordinal) { [key] = JsonSerializer.SerializeToElement(value) };

    [GeneratedRegex("^[a-z][a-zA-Z0-9-]{0,39}$")]
    private static partial Regex IdRe();
}
