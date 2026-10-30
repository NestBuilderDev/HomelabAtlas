using System.Text;
using Microsoft.EntityFrameworkCore;
using HomelabAtlas.Data;
using HomelabAtlas.Localization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace HomelabAtlas.Services;

/// <summary>Builds a Markdown network documentation in the current UI language
/// (can be pasted into Confluence, a wiki, Word, ...).</summary>
public sealed class DocumentationExporter(IDbContextFactory<HomelabAtlasDbContext> dbFactory, Texts L)
{
    public async Task<string> BuildMarkdownAsync(int? gatewayId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var gateways = await db.Gateways.AsNoTracking()
            .Where(g => gatewayId == null || g.Id == gatewayId)
            .Include(g => g.Sites).ThenInclude(s => s.Networks)
            .Include(g => g.Sites).ThenInclude(s => s.Devices)
            .Include(g => g.Sites).ThenInclude(s => s.Clients)
            .Include(g => g.Sites).ThenInclude(s => s.FirewallZones)
            .Include(g => g.Sites).ThenInclude(s => s.FirewallPolicies)
            .AsSplitQuery()
            .OrderBy(g => g.Name)
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine($"# HomelabAtlas – {L["Export.Title"]}");
        sb.AppendLine();
        sb.AppendLine(L["Export.Created", Fmt.Time(DateTime.UtcNow)]);
        sb.AppendLine();

        foreach (var g in gateways)
        {
            string Src(SyncedEntity e) => L.Source(e.IsManual, g.Kind);

            sb.AppendLine($"## {g.Name}");
            sb.AppendLine();
            Item(sb, L["Col.Type"], g.IsManual ? L["Export.TypeManual"] : L.Kind(g.Kind));
            if (!g.IsManual) Item(sb, L["Col.Host"], g.Host);
            if (!string.IsNullOrWhiteSpace(g.Location)) Item(sb, L["Col.Location"], g.Location);
            if (!string.IsNullOrWhiteSpace(g.ApplicationVersion)) Item(sb, L["Col.Version"], g.ApplicationVersion);
            if (!g.IsManual) Item(sb, L["Col.LastSync"], $"{Fmt.Time(g.LastSyncUtc)} ({L.Status(g.LastSyncStatus ?? "Never")})");
            sb.AppendLine();

            foreach (var s in g.Sites.OrderBy(s => s.Name))
            {
                var netNames = s.Networks.ToDictionary(n => n.ExternalId, n => n.Name);
                var zoneNames = s.FirewallZones.ToDictionary(z => z.ExternalId, z => z.Name);
                var devNames = s.Devices.ToDictionary(d => d.ExternalId, d => d.Name);

                sb.AppendLine($"### {L["Export.Site", s.Name]}");
                sb.AppendLine();

                sb.AppendLine($"#### {L["Nav.Networks"]}");
                Table(sb,
                    [L["Col.Name"], L["Col.Vlan"], L["Col.Subnet"], L["Col.Gateway"], L["Col.Dhcp"], L["Col.DhcpRange"],
                     L["Col.Type"], L["Col.Enabled"], L["Col.Source"], L["Col.Notes"]],
                    s.Networks.OrderBy(n => n.VlanId ?? 0).Select(n => new[]
                    {
                        n.Name, n.VlanId?.ToString(), n.Subnet, n.GatewayIp, n.DhcpMode, n.DhcpRange,
                        n.Management, L.YesNo(n.Enabled), Src(n), n.Notes
                    }));

                sb.AppendLine($"#### {L["Nav.Devices"]}");
                Table(sb,
                    [L["Col.Name"], L["Col.Type"], L["Col.Manufacturer"], L["Col.Model"], L["Col.Ip"], L["Col.Mac"],
                     L["Col.State"], L["Col.Firmware"], L["Col.MountLocation"], L["Col.Source"], L["Col.Notes"]],
                    s.Devices.OrderBy(d => d.Name).Select(d => new[]
                    {
                        d.Name, d.DeviceType, d.Manufacturer, d.Model, d.IpAddress, d.MacAddress,
                        d.State, d.FirmwareVersion, d.Location, Src(d), d.Notes
                    }));

                sb.AppendLine($"#### {L["Firewall.Zones"]}");
                Table(sb, [L["Col.Zone"], L["Col.Networks"], L["Col.Origin"], L["Col.Source"]],
                    s.FirewallZones.OrderBy(z => z.Name).Select(z => new[]
                    {
                        z.Name, ResolveList(z.NetworkExternalIds, netNames), z.Origin, Src(z)
                    }));

                sb.AppendLine($"#### {L["Firewall.Rules"]}");
                Table(sb,
                    ["#", L["Col.Name"], L["Col.Action"], L["Col.SourceZone"], L["Col.DestZone"], L["Col.SourceFilter"],
                     L["Col.DestFilter"], L["Col.IpProtocol"], L["Col.Enabled"], L["Col.Origin"], L["Col.Source"], L["Col.Notes"]],
                    s.FirewallPolicies.OrderBy(p => p.Index ?? int.MaxValue).Select(p => new[]
                    {
                        p.Index?.ToString(), p.Name, p.Action,
                        Resolve(p.SourceZoneExternalId, zoneNames), Resolve(p.DestinationZoneExternalId, zoneNames),
                        p.SourceFilter, p.DestinationFilter, p.IpScope, L.YesNo(p.Enabled), p.Origin, Src(p), p.Notes
                    }));

                sb.AppendLine($"#### {L["Nav.Clients"]}");
                Table(sb, [L["Col.Name"], L["Col.Type"], L["Col.Ip"], L["Col.Mac"], L["Col.Online"], L["Col.Uplink"], L["Col.Source"], L["Col.Notes"]],
                    s.Clients.OrderBy(x => x.Name).Select(x => new[]
                    {
                        x.Name, x.Type, x.IpAddress, x.MacAddress, x.Online is null ? null : L.YesNo(x.Online.Value),
                        Resolve(x.UplinkDeviceExternalId, devNames), Src(x), x.Notes
                    }));
            }
        }

        return sb.ToString();
    }

    public async Task<byte[]> BuildPdfAsync(int? gatewayId, CancellationToken ct)
    {
        var markdown = await BuildMarkdownAsync(gatewayId, ct);
        using var stream = new MemoryStream();

        Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(28);
            page.DefaultTextStyle(style => style.FontSize(8));
            page.Header().AlignRight().Text("HomelabAtlas").FontSize(9).FontColor(Colors.Grey.Darken1);
            page.Content().Column(column => AddMarkdown(column, markdown));
            page.Footer().AlignCenter().Text(text =>
            {
                text.Span("HomelabAtlas · ");
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
        })).GeneratePdf(stream);

        return stream.ToArray();
    }

    private static void AddMarkdown(ColumnDescriptor column, string markdown)
    {
        var lines = markdown.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            var headingLevel = line.TakeWhile(c => c == '#').Count();
            if (headingLevel > 0 && line.Length > headingLevel && line[headingLevel] == ' ')
            {
                var size = headingLevel switch { 1 => 20, 2 => 15, 3 => 12, _ => 10 };
                column.Item().PaddingTop(headingLevel == 1 ? 10 : 6)
                    .Text(line[(headingLevel + 1)..]).FontSize(size).Bold();
                continue;
            }

            if (line.StartsWith('|'))
            {
                var headers = ParseTableRow(line);
                if (i + 1 < lines.Length && lines[i + 1].StartsWith('|')) i++;
                var rows = new List<string[]>();
                while (i + 1 < lines.Length && lines[i + 1].StartsWith('|'))
                    rows.Add(ParseTableRow(lines[++i]));

                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        foreach (var _ in headers) columns.RelativeColumn();
                    });
                    table.Header(header =>
                    {
                        foreach (var cell in headers)
                            header.Cell().Element(PdfCell).Background(Colors.Grey.Lighten3).Text(cell).SemiBold();
                    });
                    foreach (var row in rows)
                    {
                        for (var cellIndex = 0; cellIndex < headers.Length; cellIndex++)
                        {
                            var cell = cellIndex < row.Length ? row[cellIndex] : "";
                            table.Cell().Element(PdfCell).Text(cell);
                        }
                    }
                });
                continue;
            }

            if (line.StartsWith("- **", StringComparison.Ordinal) && line.IndexOf("**:", StringComparison.Ordinal) is var labelEnd && labelEnd >= 0)
            {
                column.Item().Text(text =>
                {
                    text.Span(line[4..labelEnd]).SemiBold();
                    text.Span(line[(labelEnd + 3)..].TrimStart());
                });
                continue;
            }

            if (line.Length > 1 && line[0] == '_' && line[^1] == '_')
                column.Item().Text(line[1..^1]).Italic().FontColor(Colors.Grey.Darken1);
            else
                column.Item().Text(line);
        }
    }

    private static string[] ParseTableRow(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        for (var i = 1; i < line.Length; i++)
        {
            if (line[i] == '|' && (i == 0 || line[i - 1] != '\\'))
            {
                cells.Add(cell.ToString().Replace("<br>", "\n", StringComparison.Ordinal).Trim());
                cell.Clear();
            }
            else if (line[i] == '\\' && i + 1 < line.Length && line[i + 1] == '|')
            {
                cell.Append('|');
                i++;
            }
            else if (line[i] != '|' || i != line.Length - 1)
            {
                cell.Append(line[i]);
            }
        }
        return cells.ToArray();
    }

    private static IContainer PdfCell(IContainer container) =>
        container.Border(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3);

    private static void Item(StringBuilder sb, string label, string? value) => sb.AppendLine($"- **{label}:** {value}");

    private static string? Resolve(string? id, Dictionary<string, string> names) =>
        id is null ? null : names.TryGetValue(id, out var n) ? n : id;

    private static string? ResolveList(string? ids, Dictionary<string, string> names) =>
        ids is null ? null : string.Join(", ", ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(i => Resolve(i, names)));

    private void Table(StringBuilder sb, string[] headers, IEnumerable<string?[]> rows)
    {
        var list = rows.ToList();
        sb.AppendLine();
        if (list.Count == 0)
        {
            sb.AppendLine($"_{L["Grid.NoEntries"]}_");
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
