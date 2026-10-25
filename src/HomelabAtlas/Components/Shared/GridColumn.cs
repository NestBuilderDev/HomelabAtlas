using Microsoft.AspNetCore.Components;

namespace HomelabAtlas.Components.Shared;

/// <summary>Spaltendefinition für <see cref="DataGrid{TItem}"/>.</summary>
public sealed class GridColumn<T>
{
    public required string Title { get; init; }

    /// <summary>Textwert – wird für Anzeige, Filter und (ohne SortKey) Sortierung verwendet.</summary>
    public required Func<T, string?> Value { get; init; }

    /// <summary>Optionale eigene Darstellung der Zelle.</summary>
    public RenderFragment<T>? Template { get; init; }

    /// <summary>Optionaler Sortierschlüssel (z. B. VLAN-ID numerisch, IP-Adressen).</summary>
    public Func<T, object?>? SortKey { get; init; }

    /// <summary>true = Dropdown mit allen vorkommenden Werten statt Textfeld.</summary>
    public bool SelectFilter { get; init; }

    public bool Filterable { get; init; } = true;
    public string? CssClass { get; init; }
}
