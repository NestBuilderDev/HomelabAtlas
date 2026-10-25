namespace HomelabAtlas.Data;

/// <summary>Ein UniFi-Gateway (z. B. UCG Fiber) – oder ein manueller Standort ohne UniFi.</summary>
public class UnifiController
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>IP/Hostname oder vollständige URL (https://ucg.local). Leer bei manuellen Standorten.</summary>
    public string Host { get; set; } = "";
    public string? Location { get; set; }
    /// <summary>API-Key, verschlüsselt mit ASP.NET Data Protection.</summary>
    public string ApiKeyProtected { get; set; } = "";
    public bool IgnoreTlsErrors { get; set; } = true;
    public bool Enabled { get; set; } = true;
    /// <summary>Manueller Standort ohne UniFi-Anbindung (kein Sync, nur eigene Einträge).</summary>
    public bool IsManual { get; set; }

    public DateTime? LastSyncUtc { get; set; }
    public string? LastSyncStatus { get; set; }
    public string? LastSyncError { get; set; }
    public string? ApplicationVersion { get; set; }

    public List<UnifiSite> Sites { get; set; } = new();
}

public class UnifiSite
{
    public int Id { get; set; }
    public int ControllerId { get; set; }
    public UnifiController? Controller { get; set; }
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

/// <summary>Gemeinsame Basis aller Objekte. Notes bleiben beim Sync erhalten,
/// manuelle Einträge (IsManual) werden vom Sync nie verändert oder gelöscht.</summary>
public abstract class SyncedEntity
{
    public int Id { get; set; }
    public int SiteId { get; set; }
    public UnifiSite? Site { get; set; }
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
    /// <summary>Raum / Rack / Einbauort.</summary>
    public string? Location { get; set; }
}

public class ClientEntry : SyncedEntity
{
    public string Name { get; set; } = "";
    public string? Type { get; set; }
    public string? MacAddress { get; set; }
    public string? IpAddress { get; set; }
    public string? UplinkDeviceExternalId { get; set; }
    public string? ConnectedAt { get; set; }
}

public class FirewallZoneEntry : SyncedEntity
{
    public string Name { get; set; } = "";
    /// <summary>Kommagetrennte externe Netzwerk-IDs.</summary>
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
