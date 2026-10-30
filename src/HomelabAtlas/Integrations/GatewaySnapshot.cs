using HomelabAtlas.Data;

namespace HomelabAtlas.Integrations;

/// <summary>
/// An object read from a gateway: external ID, raw data (JSON) and a function
/// that writes the mapped values into the database entity.
/// </summary>
public sealed record SyncItem<T>(string ExternalId, string RawJson, Action<T> Apply) where T : SyncedEntity;

/// <summary>All data of one site. A list that is <c>null</c> means "could not be read" –
/// existing entries of that section are then kept unchanged instead of being deleted.</summary>
public sealed class SiteSnapshot
{
    public required string ExternalId { get; init; }
    public required string Name { get; init; }
    public string? InternalReference { get; init; }

    public List<SyncItem<NetworkEntry>>? Networks { get; set; }
    public List<SyncItem<DeviceEntry>>? Devices { get; set; }
    public List<SyncItem<ClientEntry>>? Clients { get; set; }
    public List<SyncItem<FirewallZoneEntry>>? FirewallZones { get; set; }
    public List<SyncItem<FirewallPolicyEntry>>? FirewallPolicies { get; set; }
}

public sealed class GatewaySnapshot
{
    public string? Version { get; set; }
    public List<SiteSnapshot> Sites { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>Reads the documentation data from one type of gateway (UniFi, FRITZ!Box, ...).</summary>
public interface IGatewayProvider
{
    GatewayKind Kind { get; }

    /// <summary>Reads everything. Throws if the gateway is not reachable at all;
    /// partial failures are reported as warnings in the snapshot.</summary>
    Task<GatewaySnapshot> FetchAsync(Gateway gateway, CancellationToken ct);

    /// <summary>Connection test; returns a short, language-neutral description (model, version, ...).</summary>
    Task<string> TestAsync(Gateway gateway, CancellationToken ct);
}

public static class ProviderHelper
{
    /// <summary>Runs one part of the sync; on failure a warning is added and <c>default</c> is returned.</summary>
    public static async Task<T?> TryAsync<T>(string what, List<string> warnings, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"{what}: {ex.Message}");
            return default;
        }
    }
}
