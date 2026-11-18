using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskPanel.Agent.Core;

namespace DeskPanel.Agent.Layout;

public sealed record WidgetConfig(string Id, string Type, int X, int Y, int W, int H, Dictionary<string, JsonElement>? Options);

public sealed record LayoutDoc(int SchemaVersion, IReadOnlyList<WidgetConfig> Widgets);

public enum SaveOutcome { Saved, Invalid, Conflict }

/// <summary>
/// layout.json: the one page the phone shows. It is phone-visible by design, so it must never hold commands, paths, URLs
/// or secrets. Two ways in: the editor (strict: <see cref="Catalog.Check"/>, every error reported, version-checked) and a
/// hand edit of the file (forgiving: <see cref="Validate"/> drops or clamps what doesn't fit, so the phone still shows a page).
/// </summary>
public sealed partial class LayoutStore(AgentPaths paths)
{
    public const int Cols = Catalog.Cols, Rows = Catalog.Rows, MaxWidgets = Catalog.MaxWidgets;
    public static readonly IReadOnlySet<string> Types = Catalog.Widgets.Select(w => w.Type).ToHashSet(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _lock = new();
    private (DateTime Stamp, long Length, LayoutDoc Doc, string Version)? _cache;

    public LayoutDoc Get() => Current().Doc;

    /// <summary>
    /// A version for optimistic concurrency: a hash of the file's bytes (not its write time, whose resolution and JSON
    /// round-trip caused false conflicts in PhoneBox, Module 2). It also tells the phone when to reload its layout.
    /// </summary>
    public string Version => Current().Version;

    private (LayoutDoc Doc, string Version) Current()
    {
        lock (_lock)
        {
            if (!File.Exists(paths.Layout)) PrivateFile.WriteAtomic(paths.Layout, JsonSerializer.Serialize(Default, Json));
            var info = new FileInfo(paths.Layout);
            if (_cache is { } c && c.Stamp == info.LastWriteTimeUtc && c.Length == info.Length) return (c.Doc, c.Version);
            var bytes = File.ReadAllBytes(paths.Layout);
            LayoutDoc doc;
            try { doc = Validate(JsonSerializer.Deserialize<LayoutDoc>(bytes, Json)); }
            catch (JsonException) { doc = Default; } // a broken hand edit shows the default page, not an error
            var version = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
            _cache = (info.LastWriteTimeUtc, info.Length, doc, version);
            return (doc, version);
        }
    }

    /// <summary>Saves an edited layout if it is valid and nobody changed the file since <paramref name="expectedVersion"/>.</summary>
    public (SaveOutcome Outcome, IReadOnlyList<LayoutError> Errors, string Version) Save(LayoutDoc doc, string? expectedVersion)
    {
        var errors = Catalog.Check(doc);
        lock (_lock)
        {
            var current = Current().Version;
            if (errors.Count > 0) return (SaveOutcome.Invalid, errors, current);
            if (expectedVersion != current) return (SaveOutcome.Conflict, [], current);
            PrivateFile.WriteAtomic(paths.Layout, JsonSerializer.Serialize(doc with { SchemaVersion = 1 }, Json));
            _cache = null;
            return (SaveOutcome.Saved, [], Current().Version);
        }
    }

    /// <summary>Whether the current layout shows a widget of this type (controls are only callable when one is shown).</summary>
    public bool Has(string type) => Get().Widgets.Any(w => w.Type == type);

    /// <summary>Action ids listed by shortcuts widgets ("actions": "vscode,screenshot").</summary>
    public IReadOnlySet<string> ReferencedActions() => ActionIds(Get());

    public static IReadOnlySet<string> ActionIds(LayoutDoc doc) => doc.Widgets
        .Where(w => w.Type == "shortcuts" && w.Options is not null && w.Options.TryGetValue("actions", out var v) && v.ValueKind == JsonValueKind.String)
        .SelectMany(w => w.Options!["actions"].GetString()!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        .ToHashSet(StringComparer.Ordinal);

    public static readonly LayoutDoc Default = new(1,
    [
        new("clock", "clock", 0, 0, 4, 2, null),
        new("now-playing", "now-playing", 4, 0, 5, 2, null),
        new("pomodoro", "pomodoro", 9, 0, 3, 3, null),
        new("volume", "volume", 0, 2, 4, 1, null),
        new("media-controls", "media-controls", 4, 2, 5, 1, null),
        new("cpu", "stat", 0, 3, 3, 3, Opt("metric", "cpu")),
        new("ram", "stat", 3, 3, 3, 3, Opt("metric", "ram")),
        new("net", "stat", 6, 3, 3, 3, Opt("metric", "net")),
        new("shortcuts", "shortcuts", 9, 3, 3, 3, Opt("actions", "")),
    ]);

    /// <summary>
    /// Forgiving read of a hand-edited file: unknown types and bad or duplicate ids are dropped, sizes clamped to the
    /// catalogue's minimum and the grid, and any option the catalogue would refuse is dropped.
    /// </summary>
    public static LayoutDoc Validate(LayoutDoc? doc)
    {
        if (doc?.Widgets is null) return Default;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var widgets = new List<WidgetConfig>();
        foreach (var w in doc.Widgets.Take(MaxWidgets))
        {
            if (w is null || Catalog.Get(w.Type ?? "") is not { } def || !IdRe().IsMatch(w.Id ?? "") || !ids.Add(w.Id!)) continue;
            var x = Math.Clamp(w.X, 0, Cols - def.MinW);
            var y = Math.Clamp(w.Y, 0, Rows - def.MinH);
            widgets.Add(w with
            {
                X = x, Y = y,
                W = Math.Clamp(w.W, def.MinW, Cols - x), H = Math.Clamp(w.H, def.MinH, Rows - y),
                Options = w.Options?.Where(kv => Catalog.Check(new LayoutDoc(1, [new(w.Id!, w.Type!, 0, 0, Cols, Rows,
                    new() { [kv.Key] = kv.Value })])).Count == 0).ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
            });
        }
        return new LayoutDoc(1, widgets);
    }

    private static Dictionary<string, JsonElement> Opt(string key, string value) =>
        new(StringComparer.Ordinal) { [key] = JsonSerializer.SerializeToElement(value) };

    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$")]
    private static partial Regex IdRe();
}
