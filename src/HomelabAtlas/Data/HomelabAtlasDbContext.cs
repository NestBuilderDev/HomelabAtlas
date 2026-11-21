using Microsoft.EntityFrameworkCore;

namespace HomelabAtlas.Data;

public class HomelabAtlasDbContext(DbContextOptions<HomelabAtlasDbContext> options) : DbContext(options)
{
    public DbSet<DatabaseParameter> DatabaseParameter => Set<DatabaseParameter>();
    public DbSet<Gateway> Gateways => Set<Gateway>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<NetworkEntry> Networks => Set<NetworkEntry>();
    public DbSet<DeviceEntry> Devices => Set<DeviceEntry>();
    public DbSet<ClientEntry> Clients => Set<ClientEntry>();
    public DbSet<FirewallZoneEntry> FirewallZones => Set<FirewallZoneEntry>();
    public DbSet<FirewallPolicyEntry> FirewallPolicies => Set<FirewallPolicyEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Gateway>().Property(g => g.Kind).HasConversion<string>().HasMaxLength(20);
        b.Entity<Gateway>().Ignore(g => g.IsManual);
        b.Entity<Gateway>()
            .HasMany(g => g.Sites).WithOne(s => s.Gateway!)
            .HasForeignKey(s => s.GatewayId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<Site>().HasIndex(s => new { s.GatewayId, s.ExternalId }).IsUnique();

        b.Entity<Site>().HasMany(s => s.Networks).WithOne(x => x.Site!).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Site>().HasMany(s => s.Devices).WithOne(x => x.Site!).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Site>().HasMany(s => s.Clients).WithOne(x => x.Site!).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Site>().HasMany(s => s.FirewallZones).WithOne(x => x.Site!).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Site>().HasMany(s => s.FirewallPolicies).WithOne(x => x.Site!).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<NetworkEntry>().HasIndex(x => new { x.SiteId, x.ExternalId }).IsUnique();
        b.Entity<DeviceEntry>().HasIndex(x => new { x.SiteId, x.ExternalId }).IsUnique();
        b.Entity<ClientEntry>().HasIndex(x => new { x.SiteId, x.ExternalId }).IsUnique();
        b.Entity<FirewallZoneEntry>().HasIndex(x => new { x.SiteId, x.ExternalId }).IsUnique();
        b.Entity<FirewallPolicyEntry>().HasIndex(x => new { x.SiteId, x.ExternalId }).IsUnique();
    }
}
