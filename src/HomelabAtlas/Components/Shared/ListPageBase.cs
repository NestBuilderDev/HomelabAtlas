using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using HomelabAtlas.Data;
using HomelabAtlas.Integrations;
using HomelabAtlas.Localization;
using HomelabAtlas.Services;

namespace HomelabAtlas.Components.Shared;

/// <summary>
/// Shared logic for list pages: loading, location filter, full-text search, column definitions
/// and create/edit/delete of manual entries.
/// </summary>
public abstract class ListPageBase<T> : ComponentBase where T : SyncedEntity, new()
{
    [Inject] protected IDbContextFactory<HomelabAtlasDbContext> DbFactory { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;
    [Inject] protected Texts L { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    protected List<T> Items { get; set; } = new();
    protected List<Gateway> GatewayList { get; set; } = new();
    protected List<Site> SiteList { get; set; } = new();
    protected List<GridColumn<T>> Columns { get; private set; } = new();
    protected int GatewayFilter { get; set; }
    protected string Search { get; set; } = "";

    /// <summary>Entry currently edited in the dialog (a copy); null = dialog closed.</summary>
    protected T? Editing { get; set; }
    protected string? EditError { get; set; }
    protected string EditTitle => Editing?.Id == 0 ? L["Dialog.AddTitle", EntityLabel] : L["Dialog.EditTitle", EntityLabel];

    protected abstract string EntityLabel { get; }
    protected abstract List<GridColumn<T>> BuildColumns();
    protected abstract bool Matches(T item, string term);
    /// <summary>Copies the editable fields (without Id/SiteId/Notes).</summary>
    protected abstract void CopyEditable(T from, T to);
    protected virtual string? Validate(T item) => null;
    protected virtual Task OnLoadedAsync(HomelabAtlasDbContext db) => Task.CompletedTask;
    protected virtual void InitNew(T item) { }
    protected virtual string DisplayName(T item) => item.ExternalId;

    protected override void OnInitialized() => Columns = BuildColumns();

    protected override void OnParametersSet()
    {
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(Nav.Uri).Query);
        GatewayFilter = query.TryGetValue("gatewayId", out var values)
            && int.TryParse(values.FirstOrDefault(), out var gatewayId)
            ? gatewayId
            : 0;
    }

    protected override async Task OnInitializedAsync() => await LoadAsync();

    protected async Task LoadAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        GatewayList = await db.Gateways.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
        SiteList = await db.Sites.AsNoTracking().Include(s => s.Gateway)
            .OrderBy(s => s.Gateway!.Name).ThenBy(s => s.Name).ToListAsync();
        Items = await db.Set<T>().AsNoTracking()
            .Include(x => x.Site!).ThenInclude(s => s.Gateway)
            .ToListAsync();
        await OnLoadedAsync(db);
    }

    protected IEnumerable<T> Filtered => Items
        .Where(x => GatewayFilter == 0 || x.Site?.GatewayId == GatewayFilter)
        .Where(x => string.IsNullOrWhiteSpace(Search) || Matches(x, Search.Trim())
                    || Has(x.Notes, Search.Trim()) || Has(x.Site?.Name, Search.Trim())
                    || Has(x.Site?.Gateway?.Name, Search.Trim()));

    protected static bool Has(string? value, string term) =>
        value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

    protected static string SiteLabel(SyncedEntity e) => $"{e.Site?.Gateway?.Name} / {e.Site?.Name}";

    protected static string? RowCss(T item) => item.IsManual ? "manual-row" : null;

    protected static bool IsEditable(T item) => item.IsManual;

    // ---------- Shared columns ----------

    protected GridColumn<T> SiteColumn() => new()
    {
        Title = L["Col.Site"], Value = SiteLabel, SelectFilter = true, CssClass = "muted"
    };

    protected GridColumn<T> SourceColumn() => new()
    {
        Title = L["Col.Source"],
        Value = x => L.Source(x),
        SelectFilter = true,
        Template = x => b =>
        {
            b.OpenElement(0, "span");
            b.AddAttribute(1, "class", x.IsManual ? "badge manual" : "badge neutral");
            b.AddContent(2, L.Source(x));
            b.CloseElement();
        }
    };

    protected GridColumn<T> NotesColumn() => new()
    {
        Title = L["Col.Note"],
        Value = x => string.IsNullOrEmpty(x.Notes) ? null : "📝",
        SelectFilter = true
    };

    protected static GridColumn<T> IpColumn(string title, Func<T, string?> value) => new()
    {
        Title = title, Value = value, SortKey = x => JsonExt.IpSortKey(value(x)), CssClass = "mono"
    };

    // ---------- Manual entries ----------

    /// <summary>Default site for new entries: filtered location, else first manual location, else first site.</summary>
    protected int DefaultSiteId() =>
        SiteList.FirstOrDefault(s => GatewayFilter != 0 && s.GatewayId == GatewayFilter)?.Id
        ?? SiteList.FirstOrDefault(s => s.Gateway?.Kind == GatewayKind.Manual)?.Id
        ?? SiteList.FirstOrDefault()?.Id
        ?? 0;

    protected void NewItem()
    {
        var item = new T { IsManual = true, SiteId = DefaultSiteId() };
        InitNew(item);
        Editing = item;
        EditError = SiteList.Count == 0 ? L["Error.NoSites"] : null;
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
        if (Editing.SiteId == 0) { EditError = L["Error.SelectSite"]; return; }
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
                EditError = L["Error.NotEditable"];
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
            EditError = L["Error.SaveFailed", ex.InnerException?.Message ?? ex.Message];
            return;
        }

        Editing = null;
        EditError = null;
        await LoadAsync();
    }

    protected async Task DeleteAsync(T item)
    {
        if (!item.IsManual) return;
        if (!await JS.InvokeAsync<bool>("confirm", L["Confirm.Delete", EntityLabel, DisplayName(item)]))
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
}
