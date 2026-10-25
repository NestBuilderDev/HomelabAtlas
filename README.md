# HomelabAtlas – Netzwerkdokumentation fürs Homelab (UniFi + manuell)

ASP.NET Core (.NET 10) + Blazor Server, SQLite, läuft als Linux-Docker-Container.

## Datenquellen
**1. UniFi (automatisch)** – beliebig viele UniFi Cloud Gateways (z. B. UCG Fiber) über die
offizielle lokale Network Integration API (`https://<UCG>/proxy/network/integration/`, Header `X-API-KEY`):

| Bereich | Endpoint |
|---|---|
| Sites | `GET /v1/sites` |
| Netzwerke / VLANs | `GET /v1/sites/{id}/networks` + Detail |
| Geräte | `GET /v1/sites/{id}/devices` |
| Clients | `GET /v1/sites/{id}/clients` |
| Firewall-Zonen | `GET /v1/sites/{id}/firewall/zones` |
| Firewall-Regeln | `GET /v1/sites/{id}/firewall/policies` |

**2. Manuell** – eigene Netze, Geräte (Switches, Server, Drucker, SPS …), Clients, Firewall-Zonen und -Regeln
für Nicht-UniFi-Komponenten:
- können einer **UniFi-Site** zugeordnet werden (z. B. Cisco-Switch am UCG-Standort) **oder**
- einem **manuellen Standort** ohne UniFi (unter *Standorte / Controller → Manueller Standort*).
- Manuelle Einträge sind lila markiert, editier-/löschbar (✎ / 🗑) und werden vom Sync **nie** verändert oder gelöscht.
  Eine UniFi-Site, die auf dem Controller verschwindet, bleibt erhalten, solange manuelle Einträge daran hängen.

## Tabellen
- **Spaltenfilter** unter jeder Überschrift: Textfeld (enthält) oder Dropdown (exakter Wert, inkl. „(leer)“)
  - `=wert` → exakt · `!wert` → enthält nicht
- **Sortierung** per Klick auf Spaltenkopf (auf-/absteigend/aus), IP-Adressen und VLANs werden numerisch sortiert
- Zusätzlich: Standort-Auswahl und Volltextsuche oben, Zeile anklicken → Notizen & Rohdaten

## Start
```bash
docker compose up -d --build
# → http://<docker-host>:8080
```
API-Key auf jedem UCG: UniFi Network → *Settings → Integrations*.

## Konfiguration
| Variable | Standard | Bedeutung |
|---|---|---|
| `Sync__IntervalMinutes` | 60 | Auto-Sync-Intervall, `0` = aus |
| `DataDir` | `/data` | SQLite-DB + Data-Protection-Keys (Volume!) |
| `ASPNETCORE_HTTP_PORTS` | 8080 | interner Port |

**Backup:** Volume `/data` sichern (`homelabatlas.db` + `keys/`).

## Datenbank
Das Schema wird beim ersten Start per `EnsureCreated` angelegt. Bei späteren Modelländerungen auf EF-Core-Migrations umstellen
(oder für eine Neuinstallation das Volume löschen).

## Hinweise
- Keine eigene Anmeldung – nur intern bzw. hinter Reverse-Proxy mit Authentifizierung betreiben.
- Fehlende Endpoints (ältere UniFi-Network-Version) erscheinen als Warnung, der Rest wird synchronisiert.
