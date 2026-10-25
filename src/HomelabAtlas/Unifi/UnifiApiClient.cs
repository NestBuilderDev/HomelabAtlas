using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HomelabAtlas.Data;
using HomelabAtlas.Services;

namespace HomelabAtlas.Unifi;

public sealed class UnifiApiException(HttpStatusCode statusCode, string path, string body)
    : Exception($"UniFi API {(int)statusCode} {statusCode} bei '{path}': {Truncate(body)}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;
}

/// <summary>
/// Client für die offizielle lokale UniFi Network Integration API
/// (https://&lt;gateway&gt;/proxy/network/integration/v1/..., Header X-API-KEY).
/// </summary>
public sealed class UnifiApiClient(HttpClient http)
{
    private const int PageSize = 200;

    public static Uri BuildBaseUri(string host)
    {
        var h = host.Trim();
        if (!h.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !h.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            h = "https://" + h;
        h = h.TrimEnd('/');
        if (!h.Contains("/integration", StringComparison.OrdinalIgnoreCase))
            h += "/proxy/network/integration";
        return new Uri(h + "/");
    }

    public Task<JsonElement> GetInfoAsync(CancellationToken ct) => GetAsync("v1/info", ct);

    public Task<List<JsonElement>> GetSitesAsync(CancellationToken ct) => GetPagedAsync("v1/sites", ct);

    public Task<List<JsonElement>> GetDevicesAsync(string siteId, CancellationToken ct) =>
        GetPagedAsync($"v1/sites/{siteId}/devices", ct);

    public Task<List<JsonElement>> GetClientsAsync(string siteId, CancellationToken ct) =>
        GetPagedAsync($"v1/sites/{siteId}/clients", ct);

    public Task<List<JsonElement>> GetNetworksAsync(string siteId, CancellationToken ct) =>
        GetPagedAsync($"v1/sites/{siteId}/networks", ct);

    public Task<JsonElement> GetNetworkDetailAsync(string siteId, string networkId, CancellationToken ct) =>
        GetAsync($"v1/sites/{siteId}/networks/{networkId}", ct);

    public Task<List<JsonElement>> GetFirewallZonesAsync(string siteId, CancellationToken ct) =>
        GetPagedAsync($"v1/sites/{siteId}/firewall/zones", ct);

    public Task<List<JsonElement>> GetFirewallPoliciesAsync(string siteId, CancellationToken ct) =>
        GetPagedAsync($"v1/sites/{siteId}/firewall/policies", ct);

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        using var resp = await http.GetAsync(path, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new UnifiApiException(resp.StatusCode, path, body);

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    /// <summary>Lädt alle Seiten (offset/limit) – Antwortformat: { offset, limit, count, totalCount, data: [...] }.</summary>
    private async Task<List<JsonElement>> GetPagedAsync(string path, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        var offset = 0;

        while (true)
        {
            var sep = path.Contains('?') ? '&' : '?';
            var page = await GetAsync($"{path}{sep}offset={offset}&limit={PageSize}", ct);

            if (page.ValueKind == JsonValueKind.Array)
            {
                result.AddRange(page.EnumerateArray());
                break;
            }

            if (!page.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;

            var count = 0;
            foreach (var item in data.EnumerateArray())
            {
                result.Add(item);
                count++;
            }

            var total = page.Int("totalCount") ?? result.Count;
            offset += count;
            if (count == 0 || offset >= total) break;
        }

        return result;
    }
}

public sealed class UnifiClientFactory(IHttpClientFactory httpFactory, SecretProtector protector)
{
    public const string SecureClient = "unifi";
    public const string InsecureClient = "unifi-insecure";

    public UnifiApiClient Create(UnifiController controller)
    {
        var http = httpFactory.CreateClient(controller.IgnoreTlsErrors ? InsecureClient : SecureClient);
        http.BaseAddress = UnifiApiClient.BuildBaseUri(controller.Host);
        http.DefaultRequestHeaders.Add("X-API-KEY", protector.Unprotect(controller.ApiKeyProtected));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return new UnifiApiClient(http);
    }
}
