# Handleiding: Lokale Stack Hosten met Cloudflare Tunnel, NGINX & DockerManager

Deze handleiding beschrijft hoe je de Oracle-stack (NGINX Proxy Manager, Versie API, KennisBase en Berger Scoresheet) lokaal op je eigen machine draait en veilig wereldwijd bereikbaar maakt onder je eigen subdomeinen (`*.jouwdomein.nl`), **zonder routerpoorten open te zetten**, **met automatische officiële Cloudflare SSL** en **zonder conflict met IIS of Zoho Mail**.

---

## 🗺️ Architectuur- & Datastroom Overzicht

```text
[ Bezoeker op Internet: https://scoresheet.jouwdomein.nl ]
                        ⬇️
    [ Cloudflare Edge (Gratis SSL & DDoS Bescherming voor *.jouwdomein.nl) ]
                        ⬇️ (Beveiligde uitgaande tunnel)
    [ cloudflared container in DockerManager ]
                        ⬇️
    [ NGINX Proxy Manager (Container poort 8080 / GUI poort 81) ]
        ├── ➔ scoresheet.jouwdomein.nl (Berger Scoresheet: poort 8009)
        ├── ➔ fastapi.jouwdomein.nl    (Versie API: poort 8002)
        └── ➔ kennisbase.jouwdomein.nl (KennisBase: poort 8007)
```

---

## 📋 Stappenplan: Van A tot Z

### Stap 1: Tunnel aanmaken in Cloudflare Zero Trust

1. Log in op het [Cloudflare Zero Trust Dashboard](https://one.dash.cloudflare.com/).
2. Ga in het linkermenu naar **Networks ➔ Tunnels**.
3. Klik op **Add a Tunnel**:
   * Kies **Cloudflared**.
   * Geef de tunnel een herkenbare naam (bijv. `mijn-tunnel`).
4. Cloudflare toont nu een installatie-overzicht met een **Tunnel Token** (een lange string die begint met `eyJh...`).
5. **Kopieer deze token**.

---

### Stap 2: Token invullen in DockerManager

In het bestand `profiles/oracle.json` staat de `cloudflared` container klaar:

1. Open `profiles/oracle.json` (of klik in DockerManager op het ✏️ bewerk-icoontje bij **Cloudflare Tunnel**).
2. Plak je token in bij `Environment`:
   ```json
   "Environment": {
     "TUNNEL_TOKEN": "eyJhBGciOiJIUzI1NiIsIn..."
   }
   ```
3. Klik in DockerManager op **`▶ Start Alles`** (of start `cloudflared` en `nginx`).

---

### Stap 3: Subdomeinen koppelen in Cloudflare (Hostname Routes)

In de nieuwste Cloudflare Zero Trust interface klik je op je Tunnel ➔ **Configure** (of **Routes**):

Kies voor **Hostname Routes** *(en NIET voor CIDR routes, want CIDR is voor privé-VPN's)*:

1. Klik op **Add a hostname route** (of **Add a published application**):
   * **Hostname / Path:** `indeling.jouwdomein.nl` *(of `scoresheet.jouwdomein.nl`, `fastapi.jouwdomein.nl`)*
   * **Service Type:** `HTTP`
   * **URL:** `localhost:8080` *(als cloudflared op Windows draait)* of `http://nginx:80` *(als cloudflared in Docker draait)*
2. Klik op **Save route**.

| Veld | Waarde (Windows Service) | Waarde (Docker Setup) | Toelichting |
|---|---|---|---|
| **Hostname** | `indeling.jouwdomein.nl` | `indeling.jouwdomein.nl` | Het subdomein van je app |
| **Service Type** | `HTTP` | `HTTP` | Protocol naar de reverse proxy |
| **URL** | `localhost:8080` | `nginx:80` | NGINX luisterpoort |

*Tip:* Je kunt ook met 1 wildcard werken (`*.jouwdomein.nl` ➔ `localhost:8080`), zodat NGINX automatisch alle routering regelt!

---

### Stap 4: CNAME Records instellen bij je Hoster (Zoho Mail blijft onaangeroerd)

In het DNS-beheer van je huidige hostingprovider (waar `jouwdomein.nl` en Zoho Mail staan) voeg je voor je subdomeinen een CNAME-record toe:

| Host / Naam | Type | Waarde / Doel |
|---|---|---|
| `scoresheet` *(of `*`)* | `CNAME` | `<jouw-tunnel-id>.cfargotunnel.com` |
| `fastapi` | `CNAME` | `<jouw-tunnel-id>.cfargotunnel.com` |
| `kennisbase` | `CNAME` | `<jouw-tunnel-id>.cfargotunnel.com` |

*(Laat alle bestaande MX-, TXT- en SPF-records voor Zoho Mail ongewijzigd staan).*

---

### Stap 5: NGINX Proxy Manager Inrichten (Web GUI)

1. Open in je browser: **`http://localhost:81`**
   * *Standaard inlog eerste keer:* `admin@example.com` / `changeme`
2. Ga naar **Hosts ➔ Proxy Hosts ➔ Add Proxy Host**:

#### Berger Scoresheet (`scoresheet.jouwdomein.nl`)
* **Domain Names:** `scoresheet.jouwdomein.nl` *(druk op Enter)*
* **Scheme:** `http`
* **Forward Hostname / IP:** `bergerscoresheet`
* **Forward Port:** `8009`
* **Vinkjes:** ✅ *Block Common Exploits*, ✅ *Websockets Support*
* **SSL:** `None` *(Cloudflare regelt dit al aan de voorkant)*

#### Versie API (`fastapi.jouwdomein.nl`)
* **Domain Names:** `fastapi.jouwdomein.nl` *(druk op Enter)*
* **Scheme:** `http`
* **Forward Hostname / IP:** `versieapi`
* **Forward Port:** `8002`
* **SSL:** `None`

#### KennisBase (`kennisbase.jouwdomein.nl`)
* **Domain Names:** `kennisbase.jouwdomein.nl` *(druk op Enter)*
* **Scheme:** `http`
* **Forward Hostname / IP:** `kennisbase`
* **Forward Port:** `8007`
* **SSL:** `None`

---

## 📂 Locatie van Bestanden & Data

Alle configuraties en databases staan veilig in de applicatiemap:
```text
[Map met DockerManager.App.exe]\volumes\oracle\
├── nginx\
│   └── data\          <-- Alle NGINX proxy instellingen
```
