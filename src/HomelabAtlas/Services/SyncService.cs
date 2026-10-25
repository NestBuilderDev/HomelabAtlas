using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using HomelabAtlas.Data;
using HomelabAtlas.Unifi;

namespace HomelabAtlas.Services;

public sealed record SyncResult(bool Success, string Status, string? Message);

/// <summary>Lädt Sites, Netzwerke/VLANs, Geräte, Clients und Firewall (Zonen + Policies)
/// von allen konfigurierten UCGs und gleicht sie mit der lokalen Datenbank ab.
/// Manuelle Einträge werden dabei nie verändert oder gelöscht.</summary>
public sealed class SyncService(
    IDbContextFactory<HomelabAtlasDbContext> dbFactory,
    UnifiClientFactory clientFactory,
    ILogger<SyncService> log)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsRunning => _lock.CurrentCount == 0;

    public async Task SyncAllAsync(CancellationToken ct)
    {
        List<int> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            ids = await db.Controllers.Where(c => c.Enabled && !c.IsManual).Select(c => c.Id).ToListAsync(ct);

        foreach (var id in ids)
            await SyncControllerAsync(id, ct);
    }

    public async Task<SyncResult> SyncControllerAsync(int controllerId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ctrl = await db.Controllers.Include(c => c.Sites).FirstOrDefaultAsync(c => c.Id == controllerId, ct);
            if (ctrl is null) return new SyncResult(false, "Fehler", "Controller nicht gefunden");
            if (ctrl.IsManual) return new SyncResult(true, "OK", "Manueller Standort – kein Sync nötig.");

            var now = DateTime.UtcNow;
            var warnings = new List<string>();
            log.LogInformation("Sync gestartet: {Controller} ({Host})", ctrl.Name, ctrl.Host);

            try
            {
                var api = clientFactory.Create(ctrl);

                try { ctrl.ApplicationVersion = (await api.GetInfoAsync(ct)).Str("applicationVersion"); }
                catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add($"Info: {ex.Message}"); }

                var sites = await api.GetSitesAsync(ct);
                var seenSites = new HashSet<string>();

                foreach (var s in sites)
                {
                    var extId = s.Str("id");
                    if (string.IsNullOrEmpty(extId)) continue;
                    seenSites.Add(extId);

                    var site = ctrl.Sites.FirstOrDefault(x => x.ExternalId == extId);
                    if (site is null)
                    {
                        site = new UnifiSite { ExternalId = extId };
                        ctrl.Sites.Add(site);
                    }
                    site.Name = s.Str("name") ?? extId;
                    site.InternalReference = s.Str("internalReference");
                    site.LastSeenUtc = now;
                    await db.SaveChangesAsync(ct); // Site-Id erzeugen

                    var label = $"[{site.Name}]";

                    await TryAsync($"{label} Netzwerke", warnings, async () =>
                    {
                        var list = await api.GetNetworksAsync(extId, ct);
                        var detailed = new List<JsonElement>(list.Count);
                        foreach (var n in list)
                        {
                            // Detail-Endpoint liefert IPv4-/DHCP-Konfiguration
                            var nid = n.Str("id");
                            try { detailed.Add(nid is null ? n : await api.GetNetworkDetailAsync(extId, nid, ct)); }
                            catch (Exception ex) when (ex is not OperationCanceledException) { detailed.Add(n); }
                        }
                        await UpsertAsync<NetworkEntry>(db, site, detailed, MapNetwork, now, ct);
                    });

                    await TryAsync($"{label} Geräte", warnings, async () =>
                        await UpsertAsync<DeviceEntry>(db, site, await api.GetDevicesAsync(extId, ct), MapDevice, now, ct));

                    await TryAsync($"{label} Clients", warnings, async () =>
                        await UpsertAsync<ClientEntry>(db, site, await api.GetClientsAsync(extId, ct), MapClient, now, ct));

                    await TryAsync($"{label} Firewall-Zonen", warnings, async () =>
                        await UpsertAsync<FirewallZoneEntry>(db, site, await api.GetFirewallZonesAsync(extId, ct), MapZone, now, ct));

                    await TryAsync($"{label} Firewall-Regeln", warnings, async () =>
                        await UpsertAsync<FirewallPolicyEntry>(db, site, await api.GetFirewallPoliciesAsync(extId, ct), MapPolicy, now, ct));
                }

                // Sites, die es auf dem Controller nicht mehr gibt, entfernen –
                // außer es hängen manuelle Einträge daran (die dürfen nie verloren gehen)
                foreach (var gone in ctrl.Sites.Where(x => !seenSites.Contains(x.ExternalId)).ToList())
                {
                    if (await HasManualEntriesAsync(db, gone.Id, ct))
                        warnings.Add($"Site „{gone.Name}“ existiert auf dem Controller nicht mehr, enthält aber manuelle Einträge und wurde behalten.");
                    else
                        db.Sites.Remove(gone);
                }

                ctrl.LastSyncStatus = warnings.Count == 0 ? "OK" : "Warnung";
                ctrl.LastSyncError = warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Sync fehlgeschlagen: {Controller}", ctrl.Name);
                ctrl.LastSyncStatus = "Fehler";
                ctrl.LastSyncError = ex.Message;
            }

            ctrl.LastSyncUtc = now;
            await db.SaveChangesAsync(ct);
            log.LogInformation("Sync beendet: {Controller} – {Status}", ctrl.Name, ctrl.LastSyncStatus);
            return new SyncResult(ctrl.LastSyncStatus != "Fehler", ctrl.LastSyncStatus!, ctrl.LastSyncError);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<bool> HasManualEntriesAsync(HomelabAtlasDbContext db, int siteId, CancellationToken ct) =>
        await db.Networks.AnyAsync(x => x.SiteId == siteId && x.IsManual, ct)
        || await db.Devices.AnyAsync(x => x.SiteId == siteId && x.IsManual, ct)
        || await db.Clients.AnyAsync(x => x.SiteId == siteId && x.IsManual, ct)
        || await db.FirewallZones.AnyAsync(x => x.SiteId == siteId && x.IsManual, ct)
        || await db.FirewallPolicies.AnyAsync(x => x.SiteId == siteId && x.IsManual, ct);

    private static async Task TryAsync(string what, List<string> warnings, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (UnifiApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            warnings.Add($"{what}: Endpoint nicht verfügbar (UniFi-Network-Version prüfen).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"{what}: {ex.Message}");
        }
    }

    /// <summary>Insert/Update anhand der externen ID, nicht mehr vorhandene UniFi-Objekte werden gelöscht.
    /// Manuelle Einträge (IsManual) werden grundsätzlich nicht angefasst.</summary>
    private static async Task UpsertAsync<T>(HomelabAtlasDbContext db, UnifiSite site, IEnumerable<JsonElement> items,
        Action<T, JsonElement> map, DateTime now, CancellationToken ct) where T : SyncedEntity, new()
    {
        var set = db.Set<T>();
        var existing = await set.Where(x => x.SiteId == site.Id && !x.IsManual).ToDictionaryAsync(x => x.ExternalId, ct);
        var seen = new HashSet<string>();

        foreach (var item in items)
        {
            var id = item.Str("id");
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;

            if (!existing.TryGetValue(id, out var entity))
            {
                entity = new T { ExternalId = id, SiteId = site.Id, FirstSeenUtc = now };
                set.Add(entity);
            }

            entity.RawJson = item.GetRawText();
            entity.LastSeenUtc = now;
            map(entity, item);
        }

        set.RemoveRange(existing.Values.Where(x => !seen.Contains(x.ExternalId)));
        await db.SaveChangesAsync(ct);
    }

    // ---------- Mapping (tolerant gegenüber fehlenden Feldern) ----------

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
