using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskPanel.Agent.Layout;

/// <param name="Type">bool, number, select, text, idlist (comma-separated action ids)</param>
public sealed record OptionDef(string Key, string Label, string Type, JsonElement Default,
    double? Min = null, double? Max = null, IReadOnlyList<string>? Choices = null, int? MaxLength = null, int? MaxItems = null);

public sealed record WidgetDef(string Type, string Name, string Icon, string Desc, int W, int H, int MinW, int MinH,
    IReadOnlyList<OptionDef> Options);

public sealed record LayoutError(string Path, string Message);

/// <summary>
/// The widget catalogue (widgets.json, embedded): sizes and an option schema per type. One source for the agent's
/// validation, the editor's palette and forms, and the panel's "every widget renders" test (PhoneBox kept option values
/// unchecked on the server, Module 11).
/// </summary>
public static partial class Catalog
{
    public const int Cols = 12, Rows = 6, MaxWidgets = 30;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly string RawJson = ReadResource();
    public static readonly IReadOnlyList<WidgetDef> Widgets = JsonSerializer.Deserialize<List<WidgetDef>>(RawJson, Json)!;
    private static readonly Dictionary<string, WidgetDef> ByType = Widgets.ToDictionary(w => w.Type, StringComparer.Ordinal);

    public static WidgetDef? Get(string type) => ByType.GetValueOrDefault(type);

    /// <summary>
    /// Strict check for a layout saved from the editor: every problem is reported with a path such as
    /// <c>widgets[2].options.focusMin</c>; nothing is silently fixed.
    /// </summary>
    public static IReadOnlyList<LayoutError> Check(LayoutDoc? doc)
    {
        var errors = new List<LayoutError>();
        if (doc?.Widgets is null) return [new("widgets", "missing")];
        if (doc.Widgets.Count > MaxWidgets) errors.Add(new("widgets", $"at most {MaxWidgets} widgets"));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var cells = new string?[Cols, Rows];
        for (var i = 0; i < doc.Widgets.Count; i++)
        {
            var w = doc.Widgets[i];
            var at = $"widgets[{i}]";
            if (w is null) { errors.Add(new(at, "empty")); continue; }
            if (!IdRe().IsMatch(w.Id ?? "")) errors.Add(new($"{at}.id", "1–40 characters: a–z, 0–9, '-'"));
            else if (!ids.Add(w.Id!)) errors.Add(new($"{at}.id", $"duplicate id '{w.Id}'"));
            var def = Get(w.Type ?? "");
            if (def is null) { errors.Add(new($"{at}.type", $"unknown widget type '{w.Type}'")); continue; }
            if (w.W < def.MinW || w.H < def.MinH) errors.Add(new($"{at}.size", $"at least {def.MinW}×{def.MinH}"));
            if (w.X < 0 || w.Y < 0 || w.X + w.W > Cols || w.Y + w.H > Rows) { errors.Add(new($"{at}.position", "outside the 12×6 grid")); continue; }
            for (var x = w.X; x < w.X + w.W; x++)
                for (var y = w.Y; y < w.Y + w.H; y++)
                {
                    if (cells[x, y] is { } other) { errors.Add(new($"{at}.position", $"overlaps '{other}'")); goto placed; }
                    cells[x, y] = w.Id;
                }
            placed:
            CheckOptions(def, w.Options, $"{at}.options", errors);
        }
        return errors;
    }

    private static void CheckOptions(WidgetDef def, Dictionary<string, JsonElement>? options, string at, List<LayoutError> errors)
    {
        if (options is null) return;
        foreach (var (key, value) in options)
        {
            var path = $"{at}.{key}";
            var o = def.Options.FirstOrDefault(d => d.Key == key);
            if (o is null) { errors.Add(new(path, $"'{def.Type}' has no option '{key}'")); continue; }
            var error = o.Type switch
            {
                "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "must be true or false",
                "number" => value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var n)
                    ? "must be a number"
                    : n < (o.Min ?? double.MinValue) || n > (o.Max ?? double.MaxValue)
                        ? $"must be between {o.Min?.ToString(CultureInfo.InvariantCulture)} and {o.Max?.ToString(CultureInfo.InvariantCulture)}"
                        : null,
                "select" => value.ValueKind == JsonValueKind.String && o.Choices!.Contains(value.GetString()!)
                    ? null : $"must be one of {string.Join(", ", o.Choices!)}",
                "text" => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= (o.MaxLength ?? 200)
                    ? null : $"must be text of at most {o.MaxLength ?? 200} characters",
                "idlist" => IdList(value, o.MaxItems ?? 12),
                _ => "unsupported option type",
            };
            if (error is not null) errors.Add(new(path, error));
        }
    }

    private static string? IdList(JsonElement value, int maxItems)
    {
        if (value.ValueKind != JsonValueKind.String) return "must be text";
        var items = value.GetString()!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (items.Length > maxItems) return $"at most {maxItems} ids";
        return items.FirstOrDefault(i => !ActionIdRe().IsMatch(i)) is { } bad ? $"'{bad}' is not an action id" : null;
    }

    private static string ReadResource()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("widgets.json")
            ?? throw new InvalidOperationException("widgets.json is not embedded");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$")] private static partial Regex IdRe();
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")] private static partial Regex ActionIdRe();
}
