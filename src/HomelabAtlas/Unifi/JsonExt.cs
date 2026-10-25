using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace HomelabAtlas.Unifi;

/// <summary>Tolerante JSON-Helfer – fehlende Felder liefern null statt Exceptions,
/// damit Schemaänderungen der UniFi-API den Sync nicht brechen.</summary>
public static class JsonExt
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static JsonElement? Get(this JsonElement e, params string[] path)
    {
        var cur = e;
        foreach (var p in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out var next))
                return null;
            cur = next;
        }
        return cur.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : cur;
    }

    public static string? Str(this JsonElement e, params string[] path)
    {
        var v = e.Get(path);
        if (v is null) return null;
        return v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString() : v.Value.ToString();
    }

    public static int? Int(this JsonElement e, params string[] path)
    {
        var v = e.Get(path);
        if (v is null) return null;
        if (v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetInt32(out var i)) return i;
        if (v.Value.ValueKind == JsonValueKind.String && int.TryParse(v.Value.GetString(), out var s)) return s;
        return null;
    }

    public static bool? Bool(this JsonElement e, params string[] path)
    {
        var v = e.Get(path);
        if (v is null) return null;
        if (v.Value.ValueKind == JsonValueKind.True) return true;
        if (v.Value.ValueKind == JsonValueKind.False) return false;
        if (v.Value.ValueKind == JsonValueKind.String && bool.TryParse(v.Value.GetString(), out var b)) return b;
        return null;
    }

    /// <summary>Array von Strings (oder Objekten mit "name"/"id") als kommagetrennter Text.</summary>
    public static string? Join(this JsonElement e, params string[] path)
    {
        var v = e.Get(path);
        if (v is null || v.Value.ValueKind != JsonValueKind.Array) return null;
        var parts = v.Value.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.Object
                ? x.Str("name") ?? x.Str("id") ?? x.GetRawText()
                : x.ToString())
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var result = string.Join(", ", parts);
        return result.Length == 0 ? null : result;
    }

    /// <summary>Flacht ein beliebiges JSON-Objekt zu "a.b=c; d=[x, y]" ab (für Firewall-Filter etc.).</summary>
    public static string Summarize(JsonElement? element, int maxLength = 250)
    {
        if (element is not { } root || root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return "";

        var parts = new List<string>();

        void Walk(JsonElement el, string prefix)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in el.EnumerateObject())
                        Walk(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}.{p.Name}");
                    break;
                case JsonValueKind.Array:
                    var items = el.EnumerateArray().ToList();
                    if (items.All(i => i.ValueKind is JsonValueKind.String or JsonValueKind.Number))
                        parts.Add($"{prefix}=[{string.Join(", ", items.Select(i => i.ToString()))}]");
                    else
                        for (var i = 0; i < items.Count; i++) Walk(items[i], $"{prefix}[{i}]");
                    break;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    break;
                default:
                    parts.Add(prefix.Length == 0 ? el.ToString() : $"{prefix}={el}");
                    break;
            }
        }

        Walk(root, "");
        var s = string.Join("; ", parts);
        return s.Length > maxLength ? s[..maxLength] + "…" : s;
    }

    public static string Pretty(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, Indented);
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    /// <summary>192.168.10.1 + 24 → 192.168.10.0/24</summary>
    public static string? ToCidr(string? hostIp, int? prefix)
    {
        if (string.IsNullOrWhiteSpace(hostIp) || prefix is null) return null;
        if (!IPAddress.TryParse(hostIp, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
            || prefix < 0 || prefix > 32)
            return $"{hostIp}/{prefix}";

        var b = ip.GetAddressBytes();
        var value = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        var mask = prefix.Value == 0 ? 0u : uint.MaxValue << (32 - prefix.Value);
        var n = value & mask;
        return $"{(n >> 24) & 255}.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}/{prefix}";
    }

    /// <summary>Sortierschlüssel für IPv4-Adressen/Subnetze (10.0.0.2 vor 10.0.0.10).</summary>
    public static long? IpSortKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var ipPart = value.Split('/', ' ', '–')[0].Trim();
        if (!IPAddress.TryParse(ipPart, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;
        var b = ip.GetAddressBytes();
        return ((long)b[0] << 24) | ((long)b[1] << 16) | ((long)b[2] << 8) | b[3];
    }
}
