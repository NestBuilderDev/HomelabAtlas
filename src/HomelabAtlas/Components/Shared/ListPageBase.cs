using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using HomelabAtlas.Data;
using HomelabAtlas.Services;
using HomelabAtlas.Unifi;

namespace HomelabAtlas.Components.Shared;

/// <summary>
/// Gemeinsame Logik der Listen-Seiten: Laden, Standort-Filter, Volltextsuche, Spaltendefinitionen
/// sowie Anlegen/Bearbeiten/Löschen manueller (nicht-UniFi) Einträge.
/// </summary>
public abstract class ListPageBase<T> : ComponentBase where T : SyncedEntity, new()
{
    [Inject] protected IDbContextFactory<HomelabAtlasDbContext> DbFactory { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;

    protected List<T> Items { get; set; } = new();
    protected List<UnifiController> ControllerList { get; set; } = new();
    protected List<UnifiSite> SiteList { get; set; } = new();
    protected List<GridColumn<T>> Columns { get; private set; } = new();
    protected int ControllerFilter { get; set; }
    protected string Search { get; set; } = "";

    /// <summary>Aktuell im Dialog bearbeiteter Eintrag (Kopie), null = Dialog geschlossen.</summary>
    protected T? Editing { get; set; }
    protected string? EditError { get; set; }
    protected string EditTitle => Editing?.Id == 0 ? $"{EntityLabel} hinzufügen" : $"{EntityLabel} bearbeiten";

    protected abstract string EntityLabel { get; }
    protected abstract List<GridColumn<T>> BuildColumns();
    protected abstract bool Matches(T item, string term);
    /// <summary>Kopiert die editierbaren Felder (ohne Id/SiteId/Notes).</summary>
    protected abstract void CopyEditable(T from, T to);
    protected virtual string? Validate(T item) => null;
    protected virtual Task OnLoadedAsync(HomelabAtlasDbContext db) => Task.CompletedTask;

    protected override void OnInitialized() => Columns = BuildColumns();

    protected override async Task OnInitializedAsync() => await LoadAsync();

    protected async Task LoadAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        ControllerList = await db.Controllers.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
        SiteList = await db.Sites.AsNoTracking().Include(s => s.Controller)
            .OrderBy(s => s.Controller!.Name).ThenBy(s => s.Name).ToListAsync();
        Items = await db.Set<T>().AsNoTracking()
            .Include(x => x.Site!).ThenInclude(s => s.Controller)
            .ToListAsync();
        await OnLoadedAsync(db);
    }

    protected IEnumerable<T> Filtered => Items
        .Where(x => ControllerFilter == 0 || x.Site?.ControllerId == ControllerFilter)
        .Where(x => string.IsNullOrWhiteSpace(Search) || Matches(x, Search.Trim())
                    || Has(x.Notes, Search.Trim()) || Has(x.Site?.Name, Search.Trim())
                    || Has(x.Site?.Controller?.Name, Search.Trim()));

    protected static bool Has(string? value, string term) =>
        value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

    protected static string SiteLabel(SyncedEntity e) => $"{e.Site?.Controller?.Name} / {e.Site?.Name}";

    protected static string? RowCss(T item) => item.IsManual ? "manual-row" : null;

    protected static bool IsEditable(T item) => item.IsManual;

    // ---------- Gemeinsame Spalten ----------

    protected GridColumn<T> SiteColumn() => new()
    {
        Title = "Standort / Site", Value = SiteLabel, SelectFilter = true, CssClass = "muted"
    };

    protected GridColumn<T> SourceColumn() => new()
    {
        Title = "Quelle",
        Value = x => x.IsManual ? "Manuell" : "UniFi",
        SelectFilter = true,
        Template = x => b =>
        {
            b.OpenElement(0, "span");
            b.AddAttribute(1, "class", x.IsManual ? "badge manual" : "badge neutral");
            b.AddContent(2, x.IsManual ? "Manuell" : "UniFi");
            b.CloseElement();
        }
    };

    protected GridColumn<T> NotesColumn() => new()
    {
        Title = "Notiz",
        Value = x => string.IsNullOrEmpty(x.Notes) ? null : "📝",
        SelectFilter = true
    };

    protected static GridColumn<T> IpColumn(string title, Func<T, string?> value) => new()
    {
        Title = title, Value = value, SortKey = x => JsonExt.IpSortKey(value(x)), CssClass = "mono"
    };

    // ---------- Manuelle Einträge ----------

    /// <summary>Standard-Site für neue Einträge: gefilterter Standort, sonst erster manueller, sonst erste Site.</summary>
    private int DefaultSiteId() =>
        SiteList.FirstOrDefault(s => ControllerFilter != 0 && s.ControllerId == ControllerFilter)?.Id
        ?? SiteList.FirstOrDefault(s => s.Controller?.IsManual == true)?.Id
        ?? SiteList.FirstOrDefault()?.Id
        ?? 0;

    protected virtual void InitNew(T item) { }

    protected void NewItem()
    {
        var item = new T { IsManual = true, SiteId = DefaultSiteId() };
        InitNew(item);
        Editing = item;
        EditError = SiteList.Count == 0
            ? "Es gibt noch keinen Standort. Bitte zuerst unter „Standorte / Controller“ einen manuellen Standort anlegen oder einen UCG synchronisieren."
            : null;
    }

    protected void EditItem(T item)
    {
        var copy = new T { Id = item.Id, SiteId = item.SiteId, Notes = item.Notes, IsManual = true };
        CopyEditable(item, copy);
        Editing = copy;
        EditError = null;
    }

    protected void CancelEdit()
    {
        Editing = null;
        EditError = null;
    }

    protected async Task SaveAsync()
    {
        if (Editing is null) return;
        if (Editing.SiteId == 0) { EditError = "Bitte einen Standort / eine Site wählen."; return; }
        var error = Validate(Editing);
        if (error is not null) { EditError = error; return; }

        await using var db = await DbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        T? entity;

        if (Editing.Id == 0)
        {
            entity = new T
            {
                ExternalId = "manual-" + Guid.NewGuid().ToString("N"),
                IsManual = true,
                FirstSeenUtc = now,
                RawJson = "{}"
            };
            db.Set<T>().Add(entity);
        }
        else
        {
            entity = await db.Set<T>().FindAsync(Editing.Id);
            if (entity is null || !entity.IsManual)
            {
                EditError = "Eintrag nicht gefunden oder nicht bearbeitbar (UniFi-Einträge werden durch den Sync verwaltet).";
                return;
            }
        }

        entity.SiteId = Editing.SiteId;
        entity.LastSeenUtc = now;
        entity.Notes = Fmt.Clean(Editing.Notes);
        CopyEditable(Editing, entity);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            EditError = $"Speichern fehlgeschlagen: {ex.InnerException?.Message ?? ex.Message}";
            return;
        }

        Editing = null;
        EditError = null;
        await LoadAsync();
    }

    protected async Task DeleteAsync(T item)
    {
        if (!item.IsManual) return;
        if (!await JS.InvokeAsync<bool>("confirm", $"{EntityLabel} „{DisplayName(item)}“ wirklich löschen?"))
            return;

        await using var db = await DbFactory.CreateDbContextAsync();
        var entity = await db.Set<T>().FindAsync(item.Id);
        if (entity is not null && entity.IsManual)
        {
            db.Set<T>().Remove(entity);
            await db.SaveChangesAsync();
        }
        await LoadAsync();
    }

    protected virtual string DisplayName(T item) => item.ExternalId;
}
