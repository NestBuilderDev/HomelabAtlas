using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using HomelabAtlas.Components;
using HomelabAtlas.Data;
using HomelabAtlas.Integrations;
using HomelabAtlas.Integrations.FritzBox;
using HomelabAtlas.Integrations.Unifi;
using HomelabAtlas.Localization;
using HomelabAtlas.Services;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Path.GetFullPath(builder.Configuration["DataDir"] ?? "./data");
Directory.CreateDirectory(dataDir);

// ---------- Localization ----------
// Built-in languages ship in /Locales, custom/override files can be placed in <DataDir>/locales.
var catalog = new TextCatalog(
    [Path.Combine(AppContext.BaseDirectory, "Locales"), Path.Combine(dataDir, "locales")],
    builder.Configuration["Localization:DefaultCulture"]);
builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton<Texts>();

// ---------- UI + data ----------
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var dbPath = Path.Combine(dataDir, "homelabatlas.db");
builder.Services.AddDbContextFactory<HomelabAtlasDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddDataProtection()
    .SetApplicationName("HomelabAtlas")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

// ---------- Integrations ----------
// UniFi: UCGs ship with a self-signed certificate -> selectable per gateway
builder.Services.AddHttpClient(UnifiClientFactory.SecureClient, c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient(UnifiClientFactory.InsecureClient, c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });
builder.Services.AddSingleton<UnifiClientFactory>();

// One provider per gateway type – add new integrations here
builder.Services.AddSingleton<IGatewayProvider, UnifiProvider>();
builder.Services.AddSingleton<IGatewayProvider, FritzBoxProvider>();

builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection("Sync"));
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<DocumentationExporter>();
builder.Services.AddHostedService<SyncBackgroundService>();

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var app = builder.Build();

foreach (var error in catalog.LoadErrors)
    app.Logger.LogWarning("{Error}", error);
app.Logger.LogInformation("Languages: {Languages} (default: {Default})",
    string.Join(", ", catalog.SupportedCultures), catalog.DefaultCulture);

// Create database + optionally import gateways from configuration
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<HomelabAtlasDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    var backupPath = await DatabaseMigration.DatabaseMigrationAsync(db, app.Logger);
    if (backupPath is not null)
        app.Logger.LogInformation("Database schema upgraded; backup created at {BackupPath}", backupPath);

    await ConfigSeeder.SeedAsync(db, app.Configuration, scope.ServiceProvider.GetRequiredService<SecretProtector>());
}

// Language resolution order: ?culture=xx → cookie (language selector) → browser Accept-Language → default
var cultures = catalog.SupportedCultures;
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture(catalog.DefaultCulture)
    .AddSupportedCultures(cultures)
    .AddSupportedUICultures(cultures));

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// Called by the language selector: stores the culture in a cookie and reloads the page
app.MapGet("/culture/set", (string culture, string? redirectUri, HttpContext ctx) =>
{
    if (catalog.IsSupported(culture))
    {
        ctx.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax });
    }

    var target = redirectUri ?? "/";
    if (!target.StartsWith('/') || target.StartsWith("//") || target.StartsWith("/\\"))
        target = "/";
    return Results.LocalRedirect(target);
});

app.MapGet("/export/markdown", async (int? gatewayId, DocumentationExporter exporter, CancellationToken ct) =>
{
    var md = await exporter.BuildMarkdownAsync(gatewayId, ct);
    return Results.File(Encoding.UTF8.GetBytes(md), "text/markdown; charset=utf-8",
        $"homelabatlas-{DateTime.Now:yyyyMMdd-HHmm}.md");
});

app.MapGet("/export/pdf", async (int? gatewayId, DocumentationExporter exporter, CancellationToken ct) =>
{
    var pdf = await exporter.BuildPdfAsync(gatewayId, ct);
    return Results.File(pdf, "application/pdf", $"homelabatlas-{DateTime.Now:yyyyMMdd-HHmm}.pdf");
});

app.MapGet("/health", () => Results.Ok("ok"));

app.Run();
