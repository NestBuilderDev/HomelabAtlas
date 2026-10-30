using Microsoft.AspNetCore.Components;

namespace HomelabAtlas.Components.Shared;

/// <summary>Column definition for <see cref="DataGrid{TItem}"/>.</summary>
public sealed class GridColumn<T>
{
    public required string Title { get; init; }

    /// <summary>Text value – used for display, filtering and (without SortKey) sorting.</summary>
    public required Func<T, string?> Value { get; init; }

    /// <summary>Optional custom cell rendering.</summary>
    public RenderFragment<T>? Template { get; init; }

    /// <summary>Optional sort key (e.g. numeric VLAN ID, IP addresses).</summary>
    public Func<T, object?>? SortKey { get; init; }

    /// <summary>true = drop-down with all occurring values instead of a text box.</summary>
    public bool SelectFilter { get; init; }

    public bool Filterable { get; init; } = true;
    public string? CssClass { get; init; }
}
