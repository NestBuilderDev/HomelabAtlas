using Microsoft.EntityFrameworkCore;
using HomelabAtlas.Data;
using HomelabAtlas.Integrations;

namespace HomelabAtlas.Services;

/// <param name="Status">Language-neutral status code, see <see cref="SyncStatus"/>.</param>
public sealed record SyncResult(bool Success, string Status, string? Message);

/// <summary>
/// Fetches a snapshot from the matching provider (UniFi, FRITZ!Box, ...) and reconciles it with the database.
/// Manual entries are never modified or deleted.
/// </summary>
public sealed class SyncService(
    IDbContextFactory<HomelabAtlasDbContext> dbFactory,
    IEnumerable<IGatewayProvider> providers,
    ILogger<SyncService> log)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<GatewayKind, IGatewayProvider> _providers = providers.ToDictionary(p => p.Kind);

    public bool IsRunning => _lock.CurrentCount == 0;

    public async Task SyncAllAsync(CancellationToken ct)
    {
        List<int> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            ids = await db.Gateways.Where(g => g.Enabled && g.Kind != GatewayKind.Manual).Select(g => g.Id).ToListAsync(ct);

        foreach (var id in ids)
            await SyncGatewayAsync(id, ct);
    }

    /// <summary>Connection test; returns a short description of the gateway.</summary>
    public async Task<string> TestAsync(int gatewayId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var gateway = await db.Gateways.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gatewayId, ct)
                      ?? throw new InvalidOperationException("Gateway not found.");
        return await GetProvider(gateway.Kind).TestAsync(gateway, ct);
    }

    public async Task<SyncResult> SyncGatewayAsync(int gatewayId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var gateway = await db.Gateways.Include(g => g.Sites).FirstOrDefaultAsync(g => g.Id == gatewayId, ct);
            if (gateway is null) return new SyncResult(false, SyncStatus.Error, "Gateway not found.");
            if (gateway.IsManual) return new SyncResult(true, SyncStatus.Ok, null);

            var now = DateTime.UtcNow;
            log.LogInformation("Sync started: {Gateway} ({Kind}, {Host})", gateway.Name, gateway.Kind, gateway.Host);

            try
            {
                var snapshot = await GetProvider(gateway.Kind).FetchAsync(gateway, ct);
                var warnings = snapshot.Warnings;
                gateway.ApplicationVersion = snapshot.Version ?? gateway.ApplicationVersion;

                var seenSites = new HashSet<string>();
                foreach (var s in snapshot.Sites)
                {
                    seenSites.Add(s.ExternalId);
                    var site = gateway.Sites.FirstOrDefault(x => x.ExternalId == s.ExternalId);
                    if (site is null)
                    {
                        site = new Site { ExternalId = s.ExternalId };
                        gateway.Sites.Add(site);
                    }
                    site.Name = s.Name;
                    site.InternalReference = s.InternalReference;
                    site.LastSeenUtc = now;
                    await db.SaveChangesAsync(ct); // generate site id

                    await ReconcileAsync(db, site.Id, s.Networks, now, ct);
                    await ReconcileAsync(db, site.Id, s.Devices, now, ct);
                    await ReconcileAsync(db, site.Id, s.Clients, now, ct);
                    await ReconcileAsync(db, site.Id, s.FirewallZones, now, ct);
                    await ReconcileAsync(db, site.Id, s.FirewallPolicies, now, ct);
                }

                // Remove sites that no longer exist – unless manual entries are attached (those must never be lost)
                foreach (var gone in gateway.Sites.Where(x => !seenSites.Contains(x.ExternalId)).ToList())
                {
                    if (await HasManualEntriesAsync(db, gone.Id, ct))
                        warnings.Add($"Site '{gone.Name}' no longer exists on the gateway but contains manual entries and was kept.");
                    else
                        db.Sites.Remove(gone);
                }

                gateway.LastSyncStatus = warnings.Count == 0 ? SyncStatus.Ok : SyncStatus.Warning;
                gateway.LastSyncError = warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Sync failed: {Gateway}", gateway.Name);
                gateway.LastSyncStatus = SyncStatus.Error;
                gateway.LastSyncError = ex.Message;
            }

            gateway.LastSyncUtc = now;
            await db.SaveChangesAsync(ct);
            log.LogInformation("Sync finished: {Gateway} – {Status}", gateway.Name, gateway.LastSyncStatus);
            return new SyncResult(gateway.LastSyncStatus != SyncStatus.Error, gateway.LastSyncStatus!, gateway.LastSyncError);
        }
        finally
        {
            _lock.Release();
        }
    }

    private IGatewayProvider GetProvider(GatewayKind kind) =>
        _providers.TryGetValue(kind, out var p) ? p : throw new InvalidOperationException($"No provider for gateway type '{kind}'.");

    private static async Task<bool> HasManualEntriesAsync(HomelabAtlasDbContext db, int siteId, CancellationToken ct) =>
        await db.Networks.AnyAsync(x => x.SiteId == siteId && x.IsManual, ct)
        || await db.Devices.AnyAsync(x => x.SiteId == siteId && x.IsManual, ct)
        || await db.Clients.AnyAsync(x => x.SiteId == siteId && x.IsManual, ct)
        || await db.FirewallZones.AnyAsync(x => x.SiteId == siteId && x.IsManual, ct)
        || await db.FirewallPolicies.AnyAsync(x => x.SiteId == siteId && x.IsManual, ct);

    /// <summary>Insert/update by external ID; synced objects that no longer exist are deleted.
    /// <c>null</c> = section could not be read → existing entries are kept. Manual entries are never touched.</summary>
    private static async Task ReconcileAsync<T>(HomelabAtlasDbContext db, int siteId, List<SyncItem<T>>? items,
        DateTime now, CancellationToken ct) where T : SyncedEntity, new()
    {
        if (items is null) return;

        var set = db.Set<T>();
        var existing = await set.Where(x => x.SiteId == siteId && !x.IsManual).ToDictionaryAsync(x => x.ExternalId, ct);
        var seen = new HashSet<string>();

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.ExternalId) || !seen.Add(item.ExternalId)) continue;

            if (!existing.TryGetValue(item.ExternalId, out var entity))
            {
                entity = new T { ExternalId = item.ExternalId, SiteId = siteId, FirstSeenUtc = now };
                set.Add(entity);
            }

            entity.RawJson = item.RawJson;
            entity.LastSeenUtc = now;
            item.Apply(entity);
        }

        set.RemoveRange(existing.Values.Where(x => !seen.Contains(x.ExternalId)));
        await db.SaveChangesAsync(ct);
    }
}
