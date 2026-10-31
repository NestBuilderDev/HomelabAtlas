using System.Globalization;
using System.Text.Json;

namespace HomelabAtlas.Localization;

public sealed record LanguageInfo(string Code, string Name);

/// <summary>
/// Loads all language files (*.json, file name = culture code, e.g. "en.json", "de.json", "fr-CA.json").
/// Files from later directories override/extend earlier ones, so custom translations can be dropped
/// into the data volume (<c>/data/locales</c>) without rebuilding the image.
/// Keys can be flat ("Nav.Overview") or nested ({ "Nav": { "Overview": ... } }).
/// </summary>
public sealed class TextCatalog
{
    public const string PrimaryCulture = "en";

    private readonly Dictionary<string, Dictionary<string, string>> _texts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _loadErrors = new();

    public string DefaultCulture { get; }
    public IReadOnlyList<LanguageInfo> Languages { get; }
    public IReadOnlyList<string> LoadErrors => _loadErrors;

    public TextCatalog(IEnumerable<string> directories, string? defaultCulture)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        foreach (var dir in directories.Where(Directory.Exists))
        {
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var code = Path.GetFileNameWithoutExtension(file);
                if (!IsValidCulture(code))
                {
                    _loadErrors.Add($"Ignored language file '{file}': '{code}' is not a valid culture code.");
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file), options);
                    if (!_texts.TryGetValue(code, out var dict))
                        _texts[code] = dict = new Dictionary<string, string>(StringComparer.Ordinal);
                    Flatten(doc.RootElement, "", dict);
                }
                catch (Exception ex)
                {
                    _loadErrors.Add($"Could not load language file '{file}': {ex.Message}");
                }
            }
        }

        DefaultCulture =
            !string.IsNullOrWhiteSpace(defaultCulture) && _texts.ContainsKey(defaultCulture) ? defaultCulture
            : _texts.ContainsKey(PrimaryCulture) ? PrimaryCulture
            : _texts.Keys.FirstOrDefault() ?? PrimaryCulture;

        Languages = _texts
            .Select(kv => new LanguageInfo(kv.Key, kv.Value.TryGetValue("_language", out var n) ? n : NativeName(kv.Key)))
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public string[] SupportedCultures => Languages.Select(l => l.Code).ToArray();

    public bool IsSupported(string? code) => !string.IsNullOrWhiteSpace(code) && _texts.ContainsKey(code);

    /// <summary>Best available language for a culture (de-AT → de), or null.</summary>
    public string? Resolve(CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
            if (_texts.ContainsKey(c.Name)) return c.Name;
        return null;
    }

    /// <summary>Lookup order: requested language → configured default → English.</summary>
    public string? Find(CultureInfo culture, string key)
    {
        var code = Resolve(culture);
        if (code is not null && _texts[code].TryGetValue(key, out var v)) return v;
        if (_texts.TryGetValue(DefaultCulture, out var def) && def.TryGetValue(key, out v)) return v;
        if (_texts.TryGetValue(PrimaryCulture, out var en) && en.TryGetValue(key, out v)) return v;
        return null;
    }

    private static void Flatten(JsonElement el, string prefix, Dictionary<string, string> target)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
                Flatten(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}.{p.Name}", target);
        }
        else if (prefix.Length > 0)
        {
            target[prefix] = el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString();
        }
    }

    private static bool IsValidCulture(string code)
    {
        try
        {
            return !string.IsNullOrEmpty(CultureInfo.GetCultureInfo(code).Name);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static string NativeName(string code)
    {
        try { return CultureInfo.GetCultureInfo(code).NativeName; }
        catch (CultureNotFoundException) { return code; }
    }
}
