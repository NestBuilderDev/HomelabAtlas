using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using HomelabAtlas.Data;

namespace HomelabAtlas.Services;

public sealed class SyncOptions
{
    /// <summary>Intervall für den automatischen Sync in Minuten. 0 = deaktiviert.</summary>
    public int IntervalMinutes { get; set; } = 60;
}

/// <summary>Verschlüsselt API-Keys in der Datenbank (Schlüssel liegen im Data-Volume).</summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("HomelabAtlas.UnifiApiKeys.v1");

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
            log.LogInformation("Automatischer Sync deaktiviert (Sync:IntervalMinutes = 0).");
            return;
        }

        log.LogInformation("Automatischer Sync alle {Minutes} Minuten.", minutes);
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
                log.LogError(ex, "Automatischer Sync fehlgeschlagen.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Optional: Controller aus appsettings / Umgebungsvariablen (Unifi__Controllers__0__Host ...) anlegen.</summary>
public static class ConfigSeeder
{
    public sealed class ControllerSeed
    {
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string? Location { get; set; }
        public bool IgnoreTlsErrors { get; set; } = true;
    }

    public static async Task SeedAsync(HomelabAtlasDbContext db, IConfiguration config, SecretProtector protector)
    {
        var seeds = config.GetSection("Unifi:Controllers").Get<List<ControllerSeed>>() ?? new();
        foreach (var s in seeds.Where(s => !string.IsNullOrWhiteSpace(s.Host)))
        {
            var existing = await db.Controllers.FirstOrDefaultAsync(c => c.Host == s.Host);
            if (existing is null)
            {
                existing = new UnifiController { Host = s.Host.Trim() };
                db.Controllers.Add(existing);
            }

            existing.Name = string.IsNullOrWhiteSpace(s.Name) ? s.Host : s.Name;
            existing.Location = s.Location ?? existing.Location;
            existing.IgnoreTlsErrors = s.IgnoreTlsErrors;
            if (!string.IsNullOrWhiteSpace(s.ApiKey))
                existing.ApiKeyProtected = protector.Protect(s.ApiKey.Trim());
        }
        await db.SaveChangesAsync();
    }
}

public static class Fmt
{
    public static string Time(DateTime? utc) =>
        utc is null ? "–" : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    public static string YesNo(bool b) => b ? "Ja" : "Nein";

    /// <summary>Leere Eingaben → null, sonst getrimmt.</summary>
    public static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
