using System.Text.Json;
using HomelabAtlas.Data;

namespace HomelabAtlas.Integrations.Unifi;

/// <summary>Reads sites, networks/VLANs, devices, clients and firewall (zones + policies) from a UniFi gateway.</summary>
public sealed class UnifiProvider(UnifiClientFactory clientFactory) : IGatewayProvider
{
    public GatewayKind Kind => GatewayKind.Unifi;

    public async Task<string> TestAsync(Gateway gateway, CancellationToken ct)
    {
        var api = clientFactory.Create(gateway);
        var info = await api.GetInfoAsync(ct);
        var sites = await api.GetSitesAsync(ct);
        return $"UniFi Network {info.Str("applicationVersion") ?? "?"}, {sites.Count} site(s)";
    }

    public async Task<GatewaySnapshot> FetchAsync(Gateway gateway, CancellationToken ct)
    {
        var api = clientFactory.Create(gateway);
        var snap = new GatewaySnapshot();
        var w = snap.Warnings;

        var info = await ProviderHelper.TryAsync("Info", w, () => api.GetInfoAsync(ct));
        if (info.ValueKind == JsonValueKind.Object)
            snap.Version = info.Str("applicationVersion");

        // If the site list cannot be loaded, the whole sync fails (API key / host wrong)
        foreach (var s in await api.GetSitesAsync(ct))
        {
            var siteId = s.Str("id");
            if (string.IsNullOrEmpty(siteId)) continue;

            var site = new SiteSnapshot
            {
                ExternalId = siteId,
                Name = s.Str("name") ?? siteId,
                InternalReference = s.Str("internalReference")
            };
            var label = $"[{site.Name}]";

            site.Networks = await ProviderHelper.TryAsync($"{label} Networks", w, async () =>
            {
                var list = await api.GetNetworksAsync(siteId, ct);
                var detailed = new List<JsonElement>(list.Count);
                foreach (var n in list)
                {
                    // The detail endpoint returns the IPv4/DHCP configuration
                    var nid = n.Str("id");
                    try { detailed.Add(nid is null ? n : await api.GetNetworkDetailAsync(siteId, nid, ct)); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { detailed.Add(n); }
                }
                return ToItems<NetworkEntry>(detailed, MapNetwork);
            });

            site.Devices = await ProviderHelper.TryAsync($"{label} Devices", w, async () =>
                ToItems<DeviceEntry>(await api.GetDevicesAsync(siteId, ct), MapDevice));

            site.Clients = await ProviderHelper.TryAsync($"{label} Clients", w, async () =>
                ToItems<ClientEntry>(await api.GetClientsAsync(siteId, ct), MapClient));

            site.FirewallZones = await ProviderHelper.TryAsync($"{label} Firewall zones", w, async () =>
                ToItems<FirewallZoneEntry>(await api.GetFirewallZonesAsync(siteId, ct), MapZone));

            site.FirewallPolicies = await ProviderHelper.TryAsync($"{label} Firewall policies", w, async () =>
                ToItems<FirewallPolicyEntry>(await api.GetFirewallPoliciesAsync(siteId, ct), MapPolicy));

            snap.Sites.Add(site);
        }

        return snap;
    }

    private static List<SyncItem<T>> ToItems<T>(IEnumerable<JsonElement> source, Action<T, JsonElement> map)
        where T : SyncedEntity =>
        source
            .Where(j => !string.IsNullOrEmpty(j.Str("id")))
            .Select(j => new SyncItem<T>(j.Str("id")!, j.GetRawText(), e => map(e, j)))
            .ToList();

    // ---------- Mapping (tolerant of missing fields) ----------

    private static void MapNetwork(NetworkEntry n, JsonElement j)
    {
        n.Name = j.Str("name") ?? n.ExternalId;
        n.VlanId = j.Int("vlanId");
        n.Enabled = j.Bool("enabled") ?? true;
        n.IsDefault = j.Bool("default") ?? false;
        n.Management = j.Str("management");

        var hostIp = j.Str("ipv4Configuration", "hostIpAddress");
        var prefix = j.Int("ipv4Configuration", "prefixLength");
        n.GatewayIp = hostIp;
        n.Subnet = JsonExt.ToCidr(hostIp, prefix) ?? j.Str("ipv4Configuration", "subnet") ?? j.Str("subnet");

        n.DhcpMode = j.Str("ipv4Configuration", "dhcpConfiguration", "mode");
        var start = j.Str("ipv4Configuration", "dhcpConfiguration", "ipAddressRange", "start");
        var stop = j.Str("ipv4Configuration", "dhcpConfiguration", "ipAddressRange", "stop")
                   ?? j.Str("ipv4Configuration", "dhcpConfiguration", "ipAddressRange", "end");
        n.DhcpRange = start is null && stop is null ? null : $"{start} – {stop}";
    }

    private static void MapDevice(DeviceEntry d, JsonElement j)
    {
        d.Name = j.Str("name") ?? j.Str("macAddress") ?? d.ExternalId;
        d.Model = j.Str("model");
        d.MacAddress = j.Str("macAddress");
        d.IpAddress = j.Str("ipAddress");
        d.State = j.Str("state");
        d.FirmwareVersion = j.Str("firmwareVersion");
        d.Features = j.Join("features");
        d.Manufacturer = "Ubiquiti";
        d.DeviceType ??= "UniFi";
    }

    private static void MapClient(ClientEntry c, JsonElement j)
    {
        c.Name = j.Str("name") ?? j.Str("macAddress") ?? c.ExternalId;
        c.Type = j.Str("type");
        c.MacAddress = j.Str("macAddress");
        c.IpAddress = j.Str("ipAddress");
        c.UplinkDeviceExternalId = j.Str("uplinkDeviceId");
        c.ConnectedAt = j.Str("connectedAt");
        c.Online = true; // the UniFi client list only contains connected clients
    }

    private static void MapZone(FirewallZoneEntry z, JsonElement j)
    {
        z.Name = j.Str("name") ?? z.ExternalId;
        z.NetworkExternalIds = j.Join("networkIds");
        z.Origin = j.Str("metadata", "origin");
    }

    private static void MapPolicy(FirewallPolicyEntry p, JsonElement j)
    {
        p.Name = j.Str("name") ?? p.ExternalId;
        p.Description = j.Str("description");
        p.Enabled = j.Bool("enabled") ?? true;
        p.Index = j.Int("index");
        p.Action = j.Str("action", "type");
        p.SourceZoneExternalId = j.Str("source", "zoneId");
        p.DestinationZoneExternalId = j.Str("destination", "zoneId");
        p.SourceFilter = JsonExt.Summarize(j.Get("source", "trafficFilter"));
        p.DestinationFilter = JsonExt.Summarize(j.Get("destination", "trafficFilter"));
        p.IpScope = JsonExt.Summarize(j.Get("ipProtocolScope"));
        p.ConnectionStates = j.Join("connectionStateFilter");
        p.LoggingEnabled = j.Bool("loggingEnabled") ?? false;
        p.Origin = j.Str("metadata", "origin");
    }
}
