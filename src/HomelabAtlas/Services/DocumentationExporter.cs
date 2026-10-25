using System.Text;
using Microsoft.EntityFrameworkCore;
using HomelabAtlas.Data;

namespace HomelabAtlas.Services;

/// <summary>Erzeugt eine Markdown-Netzwerkdokumentation (lässt sich z. B. in Confluence/Wiki/Word übernehmen).</summary>
public sealed class DocumentationExporter(IDbContextFactory<HomelabAtlasDbContext> dbFactory)
{
    public async Task<string> BuildMarkdownAsync(int? controllerId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var controllers = await db.Controllers.AsNoTracking()
            .Where(c => controllerId == null || c.Id == controllerId)
            .Include(c => c.Sites).ThenInclude(s => s.Networks)
            .Include(c => c.Sites).ThenInclude(s => s.Devices)
            .Include(c => c.Sites).ThenInclude(s => s.Clients)
            .Include(c => c.Sites).ThenInclude(s => s.FirewallZones)
            .Include(c => c.Sites).ThenInclude(s => s.FirewallPolicies)
            .AsSplitQuery()
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("# HomelabAtlas – Netzwerkdokumentation");
        sb.AppendLine();
        sb.AppendLine($"Erstellt am {DateTime.Now:dd.MM.yyyy HH:mm} · Quellen: UniFi Network Integration API und manuelle Einträge");
        sb.AppendLine();

        foreach (var c in controllers)
        {
            sb.AppendLine($"## {c.Name}");
            sb.AppendLine();
            sb.AppendLine(c.IsManual ? "- **Typ:** Manueller Standort (ohne UniFi)" : $"- **Host:** {c.Host}");
            if (!string.IsNullOrWhiteSpace(c.Location)) sb.AppendLine($"- **Standort:** {c.Location}");
            if (!string.IsNullOrWhiteSpace(c.ApplicationVersion)) sb.AppendLine($"- **UniFi Network:** {c.ApplicationVersion}");
            if (!c.IsManual) sb.AppendLine($"- **Letzter Sync:** {Fmt.Time(c.LastSyncUtc)} ({c.LastSyncStatus ?? "–"})");
            sb.AppendLine();

            foreach (var s in c.Sites.OrderBy(s => s.Name))
            {
                var netNames = s.Networks.ToDictionary(n => n.ExternalId, n => n.Name);
                var zoneNames = s.FirewallZones.ToDictionary(z => z.ExternalId, z => z.Name);
                var devNames = s.Devices.ToDictionary(d => d.ExternalId, d => d.Name);

                sb.AppendLine($"### Site: {s.Name}");
                sb.AppendLine();

                sb.AppendLine("#### Netzwerke & VLANs");
                Table(sb, ["Name", "VLAN", "Subnetz", "Gateway", "DHCP", "Bereich", "Typ", "Aktiv", "Quelle", "Notizen"],
                    s.Networks.OrderBy(n => n.VlanId ?? 0).Select(n => new[]
                    {
                        n.Name, n.VlanId?.ToString(), n.Subnet, n.GatewayIp, n.DhcpMode, n.DhcpRange,
                        n.Management, Fmt.YesNo(n.Enabled), Src(n), n.Notes
                    }));

                sb.AppendLine("#### Geräte");
                Table(sb, ["Name", "Typ", "Hersteller", "Modell", "IP", "MAC", "Status", "Firmware", "Einbauort", "Quelle", "Notizen"],
                    s.Devices.OrderBy(d => d.Name).Select(d => new[]
                    {
                        d.Name, d.DeviceType, d.Manufacturer, d.Model, d.IpAddress, d.MacAddress,
                        d.State, d.FirmwareVersion, d.Location, Src(d), d.Notes
                    }));

                sb.AppendLine("#### Firewall-Zonen");
                Table(sb, ["Zone", "Netzwerke", "Herkunft", "Quelle"],
                    s.FirewallZones.OrderBy(z => z.Name).Select(z => new[]
                    {
                        z.Name, ResolveList(z.NetworkExternalIds, netNames), z.Origin, Src(z)
                    }));

                sb.AppendLine("#### Firewall-Regeln");
                Table(sb, ["#", "Name", "Aktion", "Quelle-Zone", "Ziel-Zone", "Quell-Filter", "Ziel-Filter", "IP/Protokoll", "Aktiv", "Herkunft", "Quelle", "Notizen"],
                    s.FirewallPolicies.OrderBy(p => p.Index ?? int.MaxValue).Select(p => new[]
                    {
                        p.Index?.ToString(), p.Name, p.Action,
                        Resolve(p.SourceZoneExternalId, zoneNames), Resolve(p.DestinationZoneExternalId, zoneNames),
                        p.SourceFilter, p.DestinationFilter, p.IpScope, Fmt.YesNo(p.Enabled), p.Origin, Src(p), p.Notes
                    }));

                sb.AppendLine("#### Clients");
                Table(sb, ["Name", "Typ", "IP", "MAC", "Uplink", "Quelle", "Notizen"],
                    s.Clients.OrderBy(x => x.Name).Select(x => new[]
                    {
                        x.Name, x.Type, x.IpAddress, x.MacAddress, Resolve(x.UplinkDeviceExternalId, devNames), Src(x), x.Notes
                    }));
            }
        }

        return sb.ToString();
    }

    private static string Src(SyncedEntity e) => e.IsManual ? "Manuell" : "UniFi";

    private static string? Resolve(string? id, Dictionary<string, string> names) =>
        id is null ? null : names.TryGetValue(id, out var n) ? n : id;

    private static string? ResolveList(string? ids, Dictionary<string, string> names) =>
        ids is null ? null : string.Join(", ", ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(i => Resolve(i, names)));

    private static void Table(StringBuilder sb, string[] headers, IEnumerable<string?[]> rows)
    {
        var list = rows.ToList();
        sb.AppendLine();
        if (list.Count == 0)
        {
            sb.AppendLine("_keine Einträge_");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| " + string.Join(" | ", headers) + " |");
        sb.AppendLine("|" + string.Join("|", headers.Select(_ => "---")) + "|");
        foreach (var r in list)
            sb.AppendLine("| " + string.Join(" | ", r.Select(Esc)) + " |");
        sb.AppendLine();
    }

    private static string Esc(string? v) =>
        string.IsNullOrEmpty(v) ? "" : v.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
}
