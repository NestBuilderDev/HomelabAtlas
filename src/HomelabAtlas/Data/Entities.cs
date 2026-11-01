namespace HomelabAtlas.Data;

/// <summary>Type of a gateway / data source.</summary>
public enum GatewayKind
{
    /// <summary>UniFi Cloud Gateway (e.g. UCG Fiber) via UniFi Network Integration API.</summary>
    Unifi,
    /// <summary>AVM / FRITZ! FRITZ!Box via TR-064.</summary>
    FritzBox,
    /// <summary>Manual location without any API connection.</summary>
    Manual
}

/// <summary>A gateway that is synchronized automatically (UniFi, FRITZ!Box) – or a manual location.</summary>
public class Gateway
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public GatewayKind Kind { get; set; } = GatewayKind.Unifi;
    /// <summary>IP/hostname or full URL. Empty for manual locations.</summary>
    public string Host { get; set; } = "";
    public string? Location { get; set; }
    /// <summary>User name (FRITZ!Box). Not used for UniFi.</summary>
    public string? Username { get; set; }
    /// <summary>API key (UniFi) or password (FRITZ!Box), encrypted with ASP.NET Core Data Protection.</summary>
    public string SecretProtected { get; set; } = "";
    public bool IgnoreTlsErrors { get; set; } = true;
    /// <summary>Included in the automatic sync.</summary>
    public bool Enabled { get; set; } = true;

    public DateTime? LastSyncUtc { get; set; }
    /// <summary>Language-neutral status code, see <see cref="SyncStatus"/>.</summary>
    public string? LastSyncStatus { get; set; }
    public string? LastSyncError { get; set; }
    /// <summary>Software version reported by the gateway (UniFi Network / FRITZ!OS).</summary>
    public string? ApplicationVersion { get; set; }

    public List<Site> Sites { get; set; } = new();

    public bool IsManual => Kind == GatewayKind.Manual;
}

public static class SyncStatus
{
    public const string Ok = "OK";
    public const string Warning = "Warning";
    public const string Error = "Error";
}

public class DatabaseParameter
{
    public int Id { get; set; }
    public string Parameter { get; set; } = "";
    public string Value { get; set; } = "";
}

public class Site
{
    public int Id { get; set; }
    public int GatewayId { get; set; }
    public Gateway? Gateway { get; set; }
    public string ExternalId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? InternalReference { get; set; }
    public DateTime LastSeenUtc { get; set; }

    public List<NetworkEntry> Networks { get; set; } = new();
    public List<DeviceEntry> Devices { get; set; } = new();
    public List<ClientEntry> Clients { get; set; } = new();
    public List<FirewallZoneEntry> FirewallZones { get; set; } = new();
    public List<FirewallPolicyEntry> FirewallPolicies { get; set; } = new();
}

/// <summary>Common base of all documented objects. Notes survive a sync;
/// manual entries (IsManual) are never modified or deleted by the sync.</summary>
public abstract class SyncedEntity
{
    public int Id { get; set; }
    public int SiteId { get; set; }
    public Site? Site { get; set; }
    public string ExternalId { get; set; } = "";
    public string RawJson { get; set; } = "{}";
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public string? Notes { get; set; }
    public bool IsManual { get; set; }
}

public class NetworkEntry : SyncedEntity
{
    public string Name { get; set; } = "";
    public int? VlanId { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; }
    public string? Management { get; set; }
    public string? Subnet { get; set; }
    public string? GatewayIp { get; set; }
    public string? DhcpMode { get; set; }
    public string? DhcpRange { get; set; }
}

public class DeviceEntry : SyncedEntity
{
    public string Name { get; set; } = "";
    public string? Model { get; set; }
    public string? MacAddress { get; set; }
    public string? IpAddress { get; set; }
    public string? State { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? Features { get; set; }
    public string? Manufacturer { get; set; }
    public string? DeviceType { get; set; }
    /// <summary>Room / rack / mounting position.</summary>
    public string? Location { get; set; }
}

public class ClientEntry : SyncedEntity
{
    public string Name { get; set; } = "";
    public string? Type { get; set; }
    public string? MacAddress { get; set; }
    public string? IpAddress { get; set; }
    public string? UplinkDeviceExternalId { get; set; }
    public string? LinkSpeed { get; set; }
    /// <summary>Currently connected (null = unknown).</summary>
    public bool? Online { get; set; }
}

public class FirewallZoneEntry : SyncedEntity
{
    public string Name { get; set; } = "";
    /// <summary>Comma-separated external network IDs.</summary>
    public string? NetworkExternalIds { get; set; }
    public string? Origin { get; set; }
}

public class FirewallPolicyEntry : SyncedEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public int? Index { get; set; }
    public string? Action { get; set; }
    public string? SourceZoneExternalId { get; set; }
    public string? DestinationZoneExternalId { get; set; }
    public string? SourceFilter { get; set; }
    public string? DestinationFilter { get; set; }
    public string? IpScope { get; set; }
    public string? ConnectionStates { get; set; }
    public bool LoggingEnabled { get; set; }
    public string? Origin { get; set; }
}
