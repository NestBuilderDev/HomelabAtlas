# HomelabAtlas

Network documentation for your homelab – automatically from UniFi and FRITZ!Box, plus manual entries for everything else.
ASP.NET Core (.NET 10) + Blazor Server, SQLite, runs as a Linux Docker container. Multilingual (English, German, extensible).

## Data sources

### 1. UniFi (automatic)
Any number of UniFi Cloud Gateways (e.g. UCG Fiber) via the official local Network Integration API
(`https://<UCG>/proxy/network/integration/`, header `X-API-KEY`):

| Area | Endpoint |
|---|---|
| Sites | `GET /v1/sites` |
| Networks / VLANs | `GET /v1/sites/{id}/networks` + detail |
| Devices | `GET /v1/sites/{id}/devices` |
| Clients | `GET /v1/sites/{id}/clients` |
| Firewall zones | `GET /v1/sites/{id}/firewall/zones` |
| Firewall policies | `GET /v1/sites/{id}/firewall/policies` |

Create an API key on each UCG: UniFi Network → *Settings → Integrations*.

### 2. FRITZ!Box (automatic, TR-064)
Any number of FRITZ!Boxes via TR-064 (SOAP, digest authentication, `http://<box>:49000` or `https://<box>:49443`):

| Area | TR-064 service / action | Result in HomelabAtlas |
|---|---|---|
| Model, FRITZ!OS | `DeviceInfo:GetInfo` | Device "router" + version |
| LAN | `LANHostConfigManagement:GetInfo` | Network "LAN" (subnet, gateway, DHCP range) |
| WAN | `WANIPConnection` / `WANPPPConnection:GetExternalIPAddress` | Network "WAN / Internet" (external IP) |
| Host list | `Hosts:X_AVM-DE_GetHostListPath` | Clients incl. online state, wired/Wi-Fi/guest/VPN |
| Mesh | `Hosts:X_AVM-DE_GetMeshListPath` | Devices: repeaters / mesh nodes |
| Port forwardings | `WAN*Connection:GetGenericPortMappingEntry` | Firewall rules "Internet → LAN" (action `FORWARD`) |

The FRITZ!Box has no rule-based firewall like UniFi, so IPv4 port forwardings are documented as firewall rules.

**Prerequisites on the FRITZ!Box**
1. *Home Network → Network → Network Settings*: enable **"Allow access for applications"**
   (depending on FRITZ!OS also "Transmit status information over UPnP").
2. *System → FRITZ!Box Users*: create a dedicated user (e.g. `homelabatlas`) with the right **"FRITZ!Box settings"**.
3. In HomelabAtlas: *Locations / gateways → FRITZ!Box*, host `fritz.box` or the IP, user name + password → *Test* → *Sync*.

TR-064 is only reachable from the local network – the container must be able to reach the box on port 49000/49443.

### 3. Manual
Your own networks, devices (switches, servers, NAS, printers, PLCs …), clients, firewall zones and rules:
- assign them to a synced site (e.g. a third-party switch behind the UCG or FRITZ!Box) **or**
- to a **manual location** without API (*Locations / gateways → Manual location*).
- Manual entries are marked purple, can be edited/deleted (✎ / 🗑) and are **never** modified or deleted by the sync.

## Tables
- **Column filters** below each header: text box (contains) or drop-down (exact value, incl. "(empty)")
  - `=value` → exact match · `!value` → does not contain
- **Sorting** by clicking a header (ascending / descending / off); IP addresses and VLANs sort numerically
- Location filter and full-text search on top; click a row → notes & raw data

## Getting started

You need Docker Engine (or Docker Desktop) running. The app listens on port `8080` and stores its database,
data protection keys and optional custom locales in `/data`. Use a persistent volume to keep this data across
container updates.

### With Docker Compose

From the repository root, build and start the app:

```bash
docker compose up -d --build
```

Open <http://localhost:8080> (or `http://<docker-host>:8080` when Docker runs on another host). The Compose
configuration creates and uses the named `homelabatlas-data` volume. Stop the app with `docker compose down`;
the volume and its data are retained. Run `docker compose up -d` to start it again.

### With Docker

Build the image from the repository root, then create and start the container with a named data volume:

```bash
docker build -t homelabatlas:latest .
docker volume create homelabatlas-data
docker run -d --name homelabatlas --restart unless-stopped \
  -p 8080:8080 \
  -v homelabatlas-data:/data \
  -e TZ=Europe/Berlin \
  homelabatlas:latest
```

Open <http://localhost:8080>. Stop and restart the container with `docker stop homelabatlas` and
`docker start homelabatlas`. The named volume keeps application data when the container is removed or replaced.

## Configuration
| Variable | Default | Description |
|---|---|---|
| `Sync__IntervalMinutes` | 60 | Automatic sync interval, `0` = off |
| `Localization__DefaultCulture` | `en` | Fallback language if the browser language is not available |
| `DataDir` | `/data` | SQLite DB, data protection keys, custom locales (volume!) |
| `ASPNETCORE_HTTP_PORTS` | 8080 | Internal port |
| `Gateways__N__Kind` | `Unifi` | Optional pre-configuration: `Unifi` or `FritzBox` |
| `Gateways__N__Name` / `__Host` / `__Location` | | Name, host, location |
| `Gateways__N__ApiKey` | | UniFi API key |
| `Gateways__N__Username` / `__Password` | | FRITZ!Box credentials |

**Backup:** back up the `/data` volume (`homelabatlas.db` + `keys/`). Without `keys/`, stored API keys/passwords cannot be decrypted.

## Languages
English is the primary language; German is included.

**How the language is chosen** (first match wins):
1. Query string `?culture=de` (also works for the export: `/export/markdown?culture=de`)
2. Language selector in the sidebar (stored in a cookie for one year)
3. Browser language (`Accept-Language`)
4. `Localization__DefaultCulture`

**Adding a language** – no rebuild required: copy `src/HomelabAtlas/Locales/en.json` to `<culture>.json`
(e.g. `fr.json`), translate the values (keep keys and `{0}` placeholders), set `"_language"` to the native name,
and either add it to `Locales/` or place it in the data volume under `/data/locales/` and restart.
Files in `/data/locales/` can also override single texts of the built-in languages.

## Adding another gateway type
Implement `IGatewayProvider` (`Integrations/`) – return a `GatewaySnapshot` with sites, networks, devices, clients,
zones and rules – and register it in `Program.cs`. Sync, reconciliation (incl. protection of manual entries and notes),
UI and export work automatically; only a new `GatewayKind` value and the form fields need to be added.

## Database
The schema is created on first start via `EnsureCreated`. For later model changes, switch to EF Core migrations
(or delete the volume for a fresh install).

## Notes
- No built-in authentication – run it internally only or behind a reverse proxy with authentication (it contains firewall rules!).
- Missing endpoints/services show up as a warning; everything else is still synced.
- Sync warnings/errors are logged in English regardless of the UI language.
