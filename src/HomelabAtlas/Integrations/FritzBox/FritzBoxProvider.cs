using System.Text.Json;
using System.Xml.Linq;
using HomelabAtlas.Data;
using HomelabAtlas.Services;

namespace HomelabAtlas.Integrations.FritzBox;

/// <summary>
/// Reads a FRITZ!Box via TR-064:
/// <list type="bullet">
/// <item>DeviceInfo – model, FRITZ!OS version, serial number</item>
/// <item>LANHostConfigManagement – LAN subnet, gateway, DHCP range</item>
/// <item>WANIPConnection / WANPPPConnection – external IP and IPv4 port forwardings</item>
/// <item>Hosts – host list (all known clients incl. online state) and mesh topology (repeaters / mesh nodes)</item>
/// </list>
/// The FRITZ!Box has no rule-based firewall like UniFi; port forwardings are documented as
/// firewall rules from zone "Internet" to zone "LAN".
/// </summary>
public sealed class FritzBoxProvider(SecretProtector protector) : IGatewayProvider
{
    private const string DeviceInfo = "urn:dslforum-org:service:DeviceInfo:1";
    private const string LanHostConfig = "urn:dslforum-org:service:LANHostConfigManagement:1";
    private const string Hosts = "urn:dslforum-org:service:Hosts:1";
    private static readonly string[] WanServices =
        ["urn:dslforum-org:service:WANIPConnection:1", "urn:dslforum-org:service:WANPPPConnection:1"];

    private const string SiteId = "fritzbox";
    private const string BoxDeviceId = "fritzbox";
    private const string LanNetworkId = "lan";
    private const string WanNetworkId = "wan";
    private const string GuestNetworkId = "guest";
    private const string GuestNetworkSubnet = "192.168.179.0/24";
    private const string GuestNetworkGateway = "192.168.179.1";
    private const string InternetZoneId = "zone-internet";
    private const string LanZoneId = "zone-lan";

    public GatewayKind Kind => GatewayKind.FritzBox;

    private Tr064Client Create(Gateway g) =>
        new(g.Host, g.Username, protector.Unprotect(g.SecretProtected), g.IgnoreTlsErrors);

    public async Task<string> TestAsync(Gateway gateway, CancellationToken ct)
    {
        using var tr = Create(gateway);
        var info = await tr.CallAsync(DeviceInfo, "GetInfo", ct);
        return $"{Val(info, "NewModelName") ?? "FRITZ!Box"}, FRITZ!OS {Val(info, "NewSoftwareVersion") ?? "?"}";
    }

    public async Task<GatewaySnapshot> FetchAsync(Gateway gateway, CancellationToken ct)
    {
        using var tr = Create(gateway);
        var snap = new GatewaySnapshot();
        var w = snap.Warnings;

        // Fails completely if the box is unreachable or the credentials are wrong
        var info = await tr.CallAsync(DeviceInfo, "GetInfo", ct);
        snap.Version = Val(info, "NewSoftwareVersion") is { } sw ? $"FRITZ!OS {sw}" : null;

        var lan = await ProviderHelper.TryAsync("LAN", w, () => tr.CallAsync(LanHostConfig, "GetInfo", ct));
        var wan = await ProviderHelper.TryAsync("WAN", w, () => GetWanAsync(tr, ct));
        var hosts = await ProviderHelper.TryAsync("Hosts", w, () => GetHostListAsync(tr, ct));
        var mesh = await ProviderHelper.TryAsync("Mesh", w, () => GetMeshNodesAsync(tr, ct));
        var forwards = await ProviderHelper.TryAsync("Port forwardings", w, () => GetPortForwardingsAsync(tr, ct));

        var lanIp = Val(lan, "NewIPRouters")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var hostsByMac = (hosts ?? new List<Dictionary<string, string>>())
            .Where(h => Val(h, "MACAddress") is not null)
            .GroupBy(h => Val(h, "MACAddress")!.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        var site = new SiteSnapshot { ExternalId = SiteId, Name = "Default" };

        // ---------- Networks ----------
        if (lan is not null)
        {
            var networks = new List<SyncItem<NetworkEntry>>
            {
                new(LanNetworkId, ToJson(lan), n =>
                {
                    n.Name = "LAN";
                    n.Management = "LAN";
                    n.IsDefault = true;
                    n.Enabled = true;
                    n.VlanId = null;
                    n.GatewayIp = lanIp;
                    n.Subnet = JsonExt.ToCidr(lanIp, JsonExt.MaskToPrefix(Val(lan, "NewSubnetMask")));
                    n.DhcpMode = Val(lan, "NewDHCPServerEnable") == "1" ? "SERVER" : "NONE";
                    var min = Val(lan, "NewMinAddress");
                    var max = Val(lan, "NewMaxAddress");
                    n.DhcpRange = min is null && max is null ? null : $"{min} – {max}";
                })
            };

            if (wan is { } wanInfo)
            {
                networks.Add(new(WanNetworkId, ToJson(wanInfo.Raw), n =>
                {
                    n.Name = "WAN / Internet";
                    n.Management = "WAN";
                    n.Enabled = true;
                    n.GatewayIp = wanInfo.ExternalIp;
                    n.Subnet = null;
                    n.DhcpMode = null;
                    n.DhcpRange = null;
                }));
            }

            networks.Add(new(GuestNetworkId, JsonSerializer.Serialize(new
            {
                source = "FRITZ!Box standard guest network",
                subnet = GuestNetworkSubnet,
                gateway = GuestNetworkGateway
            }), n =>
            {
                n.Name = "Gastnetz";
                n.Management = "GUEST";
                n.Enabled = true;
                n.IsDefault = false;
                n.VlanId = null;
                n.GatewayIp = GuestNetworkGateway;
                n.Subnet = GuestNetworkSubnet;
                n.DhcpMode = "SERVER";
                n.DhcpRange = null;
            }));

            site.Networks = networks;
        }

        // ---------- Devices: the box itself + mesh repeaters ----------
        var meshNodes = mesh ?? new List<JsonElement>();
        JsonElement? master = meshNodes.Where(m => m.Str("mesh_role") == "master").Select(m => (JsonElement?)m).FirstOrDefault();
        var devices = new List<SyncItem<DeviceEntry>>
        {
            new(BoxDeviceId, ToJson(info), d =>
            {
                d.Name = gateway.Name;
                d.Model = Val(info, "NewModelName");
                d.Manufacturer = Val(info, "NewManufacturerName") ?? "AVM";
                d.DeviceType = "Router";
                d.FirmwareVersion = Val(info, "NewSoftwareVersion");
                d.IpAddress = lanIp;
                d.MacAddress = master?.Str("device_mac_address")?.ToLowerInvariant();
                d.State = "ONLINE";
                d.Features = "router, DHCP, Wi-Fi";
            })
        };

        foreach (var node in meshNodes.Where(m => m.Str("mesh_role") == "slave"))
        {
            var uid = node.Str("uid");
            if (uid is null) continue;
            var mac = node.Str("device_mac_address")?.ToLowerInvariant();
            var host = mac is not null && hostsByMac.TryGetValue(mac, out var h) ? h : null;

            devices.Add(new($"mesh-{uid}", node.GetRawText(), d =>
            {
                d.Name = node.Str("device_name") ?? mac ?? uid;
                d.Model = node.Str("device_model");
                d.Manufacturer = node.Str("device_manufacturer");
                var model = d.Model ?? "";
                d.DeviceType = model.Contains("Repeater", StringComparison.OrdinalIgnoreCase) ? "Repeater"
                    : model.Contains("Powerline", StringComparison.OrdinalIgnoreCase) ? "Powerline" : "Mesh node";
                d.FirmwareVersion = node.Str("device_firmware_version");
                d.MacAddress = mac;
                d.IpAddress = Val(host, "IPAddress");
                d.State = host is null ? null : Val(host, "Active") == "1" ? "ONLINE" : "OFFLINE";
                d.Features = "mesh";
            }));
        }
        site.Devices = devices;

        // ---------- Clients (host list, excluding mesh infrastructure represented as devices) ----------
        if (hosts is not null)
        {
            var deviceMacs = meshNodes
                .Where(m => m.Str("mesh_role") is "master" or "slave")
                .Select(m => m.Str("device_mac_address"))
                .Where(m => m is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            site.Clients = hosts
                .Where(h => Val(h, "MACAddress") is not { } mac || !deviceMacs.Contains(mac))
                .Select(h =>
                {
                    var mac = Val(h, "MACAddress")?.ToLowerInvariant();
                    var ip = Val(h, "IPAddress");
                    var id = mac ?? (ip is null ? null : $"ip-{ip}");
                    return id is null ? null : new SyncItem<ClientEntry>(id, ToJson(h), c => MapHost(c, h, mac, ip));
                })
                .Where(x => x is not null)
                .Select(x => x!)
                .ToList();
        }

        // ---------- Firewall: zones + port forwardings ----------
        if (forwards is not null)
        {
            site.FirewallZones =
            [
                new(InternetZoneId, "{}", z =>
                {
                    z.Name = "Internet";
                    z.NetworkExternalIds = WanNetworkId;
                    z.Origin = "SYSTEM";
                }),
                new(LanZoneId, "{}", z =>
                {
                    z.Name = "LAN";
                    z.NetworkExternalIds = LanNetworkId;
                    z.Origin = "SYSTEM";
                })
            ];

            site.FirewallPolicies = forwards.Select((f, i) =>
            {
                var proto = Val(f, "NewProtocol") ?? "?";
                var extPort = Val(f, "NewExternalPort") ?? "?";
                var remote = Val(f, "NewRemoteHost");
                var id = $"pf-{proto}-{extPort}-{remote ?? "any"}".ToLowerInvariant();

                return new SyncItem<FirewallPolicyEntry>(id, ToJson(f), p =>
                {
                    var client = Val(f, "NewInternalClient");
                    var intPort = Val(f, "NewInternalPort");
                    p.Name = Val(f, "NewPortMappingDescription") ?? $"{proto} {extPort}";
                    p.Description = $"{proto} {extPort} → {client}:{intPort}";
                    p.Index = i + 1;
                    p.Action = "FORWARD";
                    p.Enabled = Val(f, "NewEnabled") == "1";
                    p.SourceZoneExternalId = InternetZoneId;
                    p.DestinationZoneExternalId = LanZoneId;
                    p.SourceFilter = remote;
                    p.DestinationFilter = $"{client}:{intPort}";
                    p.IpScope = $"IPv4 {proto} {extPort}";
                    p.ConnectionStates = null;
                    p.LoggingEnabled = false;
                    p.Origin = "PORT_FORWARDING";
                });
            }).ToList();
        }

        snap.Sites.Add(site);
        return snap;
    }

    // ---------- TR-064 calls ----------

    private sealed record WanInfo(string? ExternalIp, Dictionary<string, string> Raw);

    private static async Task<WanInfo?> GetWanAsync(Tr064Client tr, CancellationToken ct)
    {
        foreach (var service in WanServices)
        {
            if (!await tr.HasServiceAsync(service, ct)) continue;
            try
            {
                var r = await tr.CallAsync(service, "GetExternalIPAddress", ct);
                var ip = Val(r, "NewExternalIPAddress");
                if (ip is not null && ip != "0.0.0.0")
                    return new WanInfo(ip, new Dictionary<string, string>(r) { ["Service"] = service });
            }
            catch (Tr064Exception)
            {
                // try the next WAN service
            }
        }
        return null;
    }

    /// <summary>Host list as XML (&lt;List&gt;&lt;Item&gt;...&lt;/Item&gt;&lt;/List&gt;) – one dictionary per host.</summary>
    private static async Task<List<Dictionary<string, string>>> GetHostListAsync(Tr064Client tr, CancellationToken ct)
    {
        var r = await tr.CallAsync(Hosts, "X_AVM-DE_GetHostListPath", ct);
        var path = Val(r, "NewX_AVM-DE_HostListPath") ?? throw new Tr064Exception("host list path missing");
        var doc = XDocument.Parse(await tr.GetTextAsync(path, ct));

        return doc.Descendants()
            .Where(e => e.Name.LocalName == "Item")
            .Select(item => item.Elements()
                .GroupBy(e => e.Name.LocalName)
                .ToDictionary(g => g.Key, g => g.First().Value))
            .ToList();
    }

    /// <summary>Mesh topology as JSON ({ "nodes": [...] }).</summary>
    private static async Task<List<JsonElement>> GetMeshNodesAsync(Tr064Client tr, CancellationToken ct)
    {
        var r = await tr.CallAsync(Hosts, "X_AVM-DE_GetMeshListPath", ct);
        var path = Val(r, "NewX_AVM-DE_MeshListPath") ?? throw new Tr064Exception("mesh list path missing");
        using var doc = JsonDocument.Parse(await tr.GetTextAsync(path, ct));

        return doc.RootElement.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
            ? nodes.EnumerateArray().Select(n => n.Clone()).ToList()
            : [];
    }

    /// <summary>IPv4 port forwardings of all WAN connection services.</summary>
    private static async Task<List<Dictionary<string, string>>> GetPortForwardingsAsync(Tr064Client tr, CancellationToken ct)
    {
        var result = new List<Dictionary<string, string>>();
        var anyService = false;

        foreach (var service in WanServices)
        {
            if (!await tr.HasServiceAsync(service, ct)) continue;

            int count;
            try
            {
                var r = await tr.CallAsync(service, "GetPortMappingNumberOfEntries", ct);
                count = int.TryParse(Val(r, "NewPortMappingNumberOfEntries"), out var c) ? c : 0;
                anyService = true;
            }
            catch (Tr064Exception)
            {
                continue; // service exists but is not the active WAN connection
            }

            for (var i = 0; i < count; i++)
            {
                var entry = await tr.CallAsync(service, "GetGenericPortMappingEntry", ct,
                    new Dictionary<string, string> { ["NewPortMappingIndex"] = i.ToString() });
                result.Add(entry);
            }
        }

        if (!anyService)
            throw new Tr064Exception("no WAN connection service with port forwardings available");
        return result;
    }

    // ---------- Helpers ----------

    private static void MapHost(ClientEntry c, Dictionary<string, string> h, string? mac, string? ip)
    {
        c.Name = Val(h, "X_AVM-DE_FriendlyName") ?? Val(h, "HostName") ?? mac ?? ip ?? c.ExternalId;
        c.MacAddress = mac;
        c.IpAddress = ip;
        c.Online = Val(h, "Active") switch { "1" => true, "0" => false, _ => null };

        var type = Val(h, "InterfaceType") switch
        {
            "Ethernet" => "WIRED",
            "802.11" => "WIRELESS",
            "HomePlug" => "POWERLINE",
            var other => other
        };
        if (Val(h, "X_AVM-DE_VPN") == "1") type = "VPN";
        if (Val(h, "X_AVM-DE_Guest") == "1") type = $"{type ?? "?"} (guest)";
        c.Type = type;

        c.UplinkDeviceExternalId = null;
        c.LinkSpeed = Val(h, "X_AVM-DE_Speed") is { } speed && speed != "0" ? $"{speed} Mbit/s" : null;
    }

    private static string? Val(IReadOnlyDictionary<string, string>? d, string key) =>
        d is not null && d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static string ToJson(IReadOnlyDictionary<string, string> d) => JsonSerializer.Serialize(d);
}
