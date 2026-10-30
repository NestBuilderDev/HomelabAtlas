using System.Globalization;
using HomelabAtlas.Data;

namespace HomelabAtlas.Localization;

/// <summary>
/// Translation service. Uses the current UI culture (set per request/circuit by the
/// request localization middleware). Missing keys fall back to English and finally to the key itself.
/// Usage in Razor: <c>@L["Nav.Overview"]</c> or with placeholders <c>@(L["Grid.Count", shown, total])</c>.
/// </summary>
public sealed class Texts(TextCatalog catalog)
{
    public string this[string key] => catalog.Find(CultureInfo.CurrentUICulture, key) ?? key;

    public string this[string key, params object?[] args]
    {
        get
        {
            var format = this[key];
            try
            {
                return string.Format(CultureInfo.CurrentCulture, format, args);
            }
            catch (FormatException)
            {
                return format;
            }
        }
    }

    public string? TryGet(string key) => catalog.Find(CultureInfo.CurrentUICulture, key);

    public string YesNo(bool value) => this[value ? "Common.Yes" : "Common.No"];

    /// <summary>Translates a status code (OK, Warning, Error, Manual, ...); unknown codes (e.g. ALLOW) are shown as-is.</summary>
    public string Status(string? code) => string.IsNullOrEmpty(code) ? "" : TryGet("Status." + code) ?? code;

    /// <summary>Display name of a gateway type.</summary>
    public string Kind(GatewayKind kind) => kind switch
    {
        GatewayKind.Unifi => "UniFi",
        GatewayKind.FritzBox => "FRITZ!Box",
        _ => this["Common.Manual"]
    };

    /// <summary>Data source of an entry: "Manual" or the gateway type it was synced from.</summary>
    public string Source(bool isManual, GatewayKind kind) => isManual ? this["Common.Manual"] : Kind(kind);

    public string Source(SyncedEntity e) => Source(e.IsManual, e.Site?.Gateway?.Kind ?? GatewayKind.Manual);

    public string CurrentLanguage => catalog.Resolve(CultureInfo.CurrentUICulture) ?? catalog.DefaultCulture;
}
