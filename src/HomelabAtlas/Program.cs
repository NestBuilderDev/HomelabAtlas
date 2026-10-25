using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using HomelabAtlas.Components;
using HomelabAtlas.Data;
using HomelabAtlas.Services;
using HomelabAtlas.Unifi;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Path.GetFullPath(builder.Configuration["DataDir"] ?? "./data");
Directory.CreateDirectory(dataDir);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var dbPath = Path.Combine(dataDir, "homelabatlas.db");

builder.Services.AddDbContextFactory<HomelabAtlasDbContext>(o =>
    o.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddDataProtection()
    .SetApplicationName("HomelabAtlas")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

// UCGs haben ab Werk ein selbstsigniertes Zertifikat -> pro Controller wählbar
builder.Services.AddHttpClient(UnifiClientFactory.SecureClient, c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient(UnifiClientFactory.InsecureClient, c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });

builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection("Sync"));
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<UnifiClientFactory>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<DocumentationExporter>();
builder.Services.AddHostedService<SyncBackgroundService>();

var app = builder.Build();

// Datenbank anlegen/aktualisieren + optionale Controller aus Konfiguration übernehmen
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<HomelabAtlasDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await ConfigSeeder.SeedAsync(db, app.Configuration, scope.ServiceProvider.GetRequiredService<SecretProtector>());
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapGet("/export/markdown", async (int? controllerId, DocumentationExporter exporter, CancellationToken ct) =>
{
    var md = await exporter.BuildMarkdownAsync(controllerId, ct);
    return Results.File(Encoding.UTF8.GetBytes(md), "text/markdown; charset=utf-8",
        $"homelabatlas-{DateTime.Now:yyyyMMdd-HHmm}.md");
});

app.MapGet("/health", () => Results.Ok("ok"));

app.Run();
