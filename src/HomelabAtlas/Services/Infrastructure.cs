using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using HomelabAtlas.Data;

namespace HomelabAtlas.Services;

public sealed class SyncOptions
{
    /// <summary>Automatic sync interval in minutes. 0 = disabled.</summary>
    public int IntervalMinutes { get; set; } = 60;
}

/// <summary>Encrypts API keys / passwords in the database (key ring is stored in the data volume).</summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("HomelabAtlas.Secrets.v1");

    public string Protect(string plain) => _protector.Protect(plain);
    public string Unprotect(string cipher) => string.IsNullOrEmpty(cipher) ? "" : _protector.Unprotect(cipher);
}

public sealed class SyncBackgroundService(
    SyncService sync,
    IOptions<SyncOptions> options,
    ILogger<SyncBackgroundService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = options.Value.IntervalMinutes;
        if (minutes <= 0)
        {
            log.LogInformation("Automatic sync disabled (Sync:IntervalMinutes = 0).");
            return;
        }

        log.LogInformation("Automatic sync every {Minutes} minutes.", minutes);
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        do
        {
            try
            {
                await sync.SyncAllAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Automatic sync failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Optional: create gateways from appsettings / environment variables (Gateways__0__Kind, __Host, ...).</summary>
public static class ConfigSeeder
{
    public sealed class GatewaySeed
    {
        public GatewayKind Kind { get; set; } = GatewayKind.Unifi;
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        /// <summary>UniFi API key.</summary>
        public string? ApiKey { get; set; }
        /// <summary>FRITZ!Box user name.</summary>
        public string? Username { get; set; }
        /// <summary>FRITZ!Box password.</summary>
        public string? Password { get; set; }
        public string? Location { get; set; }
        public bool IgnoreTlsErrors { get; set; } = true;
    }

    public static async Task SeedAsync(HomelabAtlasDbContext db, IConfiguration config, SecretProtector protector)
    {
        var seeds = config.GetSection("Gateways").Get<List<GatewaySeed>>() ?? new();
        foreach (var s in seeds.Where(s => s.Kind != GatewayKind.Manual && !string.IsNullOrWhiteSpace(s.Host)))
        {
            var host = s.Host.Trim();
            var existing = await db.Gateways.FirstOrDefaultAsync(g => g.Host == host && g.Kind == s.Kind);
            if (existing is null)
            {
                existing = new Gateway { Host = host, Kind = s.Kind };
                db.Gateways.Add(existing);
            }

            existing.Name = string.IsNullOrWhiteSpace(s.Name) ? host : s.Name;
            existing.Location = s.Location ?? existing.Location;
            existing.IgnoreTlsErrors = s.IgnoreTlsErrors;
            existing.Username = s.Kind == GatewayKind.FritzBox ? Fmt.Clean(s.Username) : null;

            var secret = s.Kind == GatewayKind.FritzBox ? s.Password : s.ApiKey;
            if (!string.IsNullOrWhiteSpace(secret))
                existing.SecretProtected = protector.Protect(secret.Trim());
        }
        await db.SaveChangesAsync();
    }
}

public static class Fmt
{
    /// <summary>UTC timestamp as local time, formatted for the current UI culture.</summary>
    public static string Time(DateTime? utc) =>
        utc is null ? "–" : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Empty input → null, otherwise trimmed.</summary>
    public static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
