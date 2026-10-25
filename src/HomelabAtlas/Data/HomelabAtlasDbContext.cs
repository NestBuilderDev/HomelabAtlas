using Microsoft.EntityFrameworkCore;

namespace HomelabAtlas.Data;

public class HomelabAtlasDbContext(DbContextOptions<HomelabAtlasDbContext> options) : DbContext(options)
{
    public DbSet<UnifiController> Controllers => Set<UnifiController>();
    public DbSet<UnifiSite> Sites => Set<UnifiSite>();
    public DbSet<NetworkEntry> Networks => Set<NetworkEntry>();
    public DbSet<DeviceEntry> Devices => Set<DeviceEntry>();
    public DbSet<ClientEntry> Clients => Set<ClientEntry>();
    public DbSet<FirewallZoneEntry> FirewallZones => Set<FirewallZoneEntry>();
    public DbSet<FirewallPolicyEntry> FirewallPolicies => Set<FirewallPolicyEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UnifiController>()
            .HasMany(c => c.Sites).WithOne(s => s.Controller!)
            .HasForeignKey(s => s.ControllerId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<UnifiSite>().HasIndex(s => new { s.ControllerId, s.ExternalId }).IsUnique();

        b.Entity<UnifiSite>().HasMany(s => s.Networks).WithOne(x => x.Site!).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UnifiSite>().HasMany(s => s.Devices).WithOne(x => x.Site!).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UnifiSite>().HasMany(s => s.Clients).WithOne(x => x.Site!).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UnifiSite>().HasMany(s => s.FirewallZones).WithOne(x => x.Site!).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UnifiSite>().HasMany(s => s.FirewallPolicies).WithOne(x => x.Site!).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<NetworkEntry>().HasIndex(x => new { x.SiteId, x.ExternalId }).IsUnique();
        b.Entity<DeviceEntry>().HasIndex(x => new { x.SiteId, x.ExternalId }).IsUnique();
        b.Entity<ClientEntry>().HasIndex(x => new { x.SiteId, x.ExternalId }).IsUnique();
        b.Entity<FirewallZoneEntry>().HasIndex(x => new { x.SiteId, x.ExternalId }).IsUnique();
        b.Entity<FirewallPolicyEntry>().HasIndex(x => new { x.SiteId, x.ExternalId }).IsUnique();
    }
}
