using System.Net;
using System.Net.Security;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace HomelabAtlas.Integrations.FritzBox;

public sealed class Tr064Exception(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Minimal TR-064 client (SOAP over HTTP, digest authentication) for the FRITZ!Box.
/// Default ports: 49000 (http) / 49443 (https). Service control URLs are read from /tr64desc.xml.
/// </summary>
public sealed class Tr064Client : IDisposable
{
    private readonly HttpClient _http;
    private Dictionary<string, string>? _services; // serviceType -> controlURL

    public Uri BaseUri { get; }

    public Tr064Client(string host, string? username, string password, bool ignoreTlsErrors, TimeSpan? timeout = null)
    {
        BaseUri = BuildBaseUri(host);

        var handler = new SocketsHttpHandler
        {
            // Digest authentication is handled by the handler after the 401 challenge
            Credentials = new NetworkCredential(username ?? "", password),
            PreAuthenticate = true,
            AllowAutoRedirect = false
        };
        if (ignoreTlsErrors)
            handler.SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true };

        _http = new HttpClient(handler) { BaseAddress = BaseUri, Timeout = timeout ?? TimeSpan.FromSeconds(30) };
    }

    /// <summary>"fritz.box" → http://fritz.box:49000/, "https://192.168.178.1" → https://192.168.178.1:49443/</summary>
    public static Uri BuildBaseUri(string host)
    {
        var h = string.IsNullOrWhiteSpace(host) ? "fritz.box" : host.Trim();
        if (!h.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !h.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            h = "http://" + h;

        var uri = new Uri(h);
        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? (uri.Scheme == Uri.UriSchemeHttps ? 49443 : 49000) : uri.Port, "/");
        return builder.Uri;
    }

    /// <summary>Reads the device description and returns all services (serviceType → controlURL).</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetServicesAsync(CancellationToken ct)
    {
        if (_services is not null) return _services;

        string xml;
        try
        {
            xml = await _http.GetStringAsync("tr64desc.xml", ct);
        }
        catch (HttpRequestException ex)
        {
            throw new Tr064Exception($"TR-064 not reachable at {BaseUri} ({ex.Message}). Is \"Allow access for applications\" enabled on the FRITZ!Box?", ex);
        }

        var doc = XDocument.Parse(xml);
        _services = doc.Descendants()
            .Where(e => e.Name.LocalName == "service")
            .Select(e => new
            {
                Type = e.Elements().FirstOrDefault(x => x.Name.LocalName == "serviceType")?.Value.Trim(),
                Url = e.Elements().FirstOrDefault(x => x.Name.LocalName == "controlURL")?.Value.Trim()
            })
            .Where(x => !string.IsNullOrEmpty(x.Type) && !string.IsNullOrEmpty(x.Url))
            .GroupBy(x => x.Type!)
            .ToDictionary(g => g.Key, g => g.First().Url!);
        return _services;
    }

    public async Task<bool> HasServiceAsync(string serviceType, CancellationToken ct) =>
        (await GetServicesAsync(ct)).ContainsKey(serviceType);

    /// <summary>Calls a SOAP action and returns all output arguments (name → value).</summary>
    public async Task<Dictionary<string, string>> CallAsync(string serviceType, string action,
        CancellationToken ct, IReadOnlyDictionary<string, string>? args = null)
    {
        var services = await GetServicesAsync(ct);
        if (!services.TryGetValue(serviceType, out var controlUrl))
            throw new Tr064Exception($"service '{serviceType}' not available on this FRITZ!Box");

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
        sb.Append("<s:Body>");
        sb.Append($"<u:{action} xmlns:u=\"{serviceType}\">");
        if (args is not null)
            foreach (var (name, value) in args)
                sb.Append($"<{name}>{SecurityElement.Escape(value)}</{name}>");
        sb.Append($"</u:{action}>");
        sb.Append("</s:Body></s:Envelope>");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, controlUrl)) // explicit: "/x" would be a file URI on Linux
        {
            Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{serviceType}#{action}\"");

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new Tr064Exception("authentication failed – check user name, password and the user's rights (\"FRITZ!Box settings\")");

        XDocument doc;
        try
        {
            doc = XDocument.Parse(body);
        }
        catch (Exception ex)
        {
            throw new Tr064Exception($"{action}: invalid response (HTTP {(int)response.StatusCode})", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var code = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value;
            var desc = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value;
            throw new Tr064Exception($"{action}: {desc ?? response.ReasonPhrase} (UPnP error {code ?? ((int)response.StatusCode).ToString()})");
        }

        var result = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == action + "Response")
                     ?? throw new Tr064Exception($"{action}: response element missing");

        return result.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
    }

    /// <summary>Loads a path returned by the box (e.g. "/devicehostlist.lua?sid=...").</summary>
    public async Task<string> GetTextAsync(string pathAndQuery, CancellationToken ct)
    {
        var uri = Uri.TryCreate(pathAndQuery, UriKind.Absolute, out var abs) && abs.Scheme.StartsWith("http")
            ? abs
            : new Uri(BaseUri, pathAndQuery);
        return await _http.GetStringAsync(uri, ct);
    }

    public void Dispose() => _http.Dispose();
}
