# Handleiding: Lokale Stack Hosten met DockerManager, NGINX & Tailscale Funnel

Deze handleiding beschrijft stap voor stap hoe de Oracle-stack (NGINX Proxy Manager, KennisBase, Versie API en Berger Scoresheet) lokaal op een Windows-machine wordt gehost en veilig publiek bereikbaar wordt gemaakt via Tailscale Funnel en eigen subdomeinen (`*.jouwdomein.nl`), **zonder routerpoorten open te zetten** en **zonder conflict met IIS (poort 80/443)**.

---

## 🗺️ Architectuur- & Datastroom Overzicht

```text
[ Bezoeker op Internet: https://app1.jouwdomein.nl ]
                        ⬇️
             [ Tailscale Funnel Edge (Automatische HTTPS) ]
                        ⬇️ (Beveiligde uitgaande tunnel)
             [ Lokale PC (Tailscale Client) ] ──▶ (Sluist door naar poort 8080)
                        ⬇️
             [ NGINX Proxy Manager (Container poort 8080 / GUI poort 81) ]
                 ├── ➔ app1.jouwdomein.nl (KennisBase: poort 8007)
                 ├── ➔ app2.jouwdomein.nl (Versie API: poort 8002)
                 └── ➔ app3.jouwdomein.nl (Berger Scoresheet: poort 8009)
```

---

## 📋 Stap-voor-stap Implementatie

### Stap 1: Tailscale Funnel & HTTPS Eenmalig Activeren

1. **HTTPS Inschakelen in Tailscale:**
   * Ga naar het Tailscale dashboard: [login.tailscale.com/admin/dns](https://login.tailscale.com/admin/dns).
   * Scroll naar **HTTPS Certificates** en klik op **Enable HTTPS**.

2. **Funnel Starten in PowerShell (als Administrator):**
   * Open PowerShell op de Windows-machine en voer uit:
     ```powershell
     tailscale funnel --bg 8080
     ```
   * *Uitleg:* Dit activeert de achtergrond-tunnel die al het inkomende publieke internetverkeer direct doorstuurt naar jouw lokale poort `8080` (jouw NGINX).

3. **Publiek Tailscale-adres opvragen:**
   * Voer het status-commando uit:
     ```powershell
     tailscale funnel status
     ```
   * Noteer het getoonde publieke adres (bijvoorbeeld: `jouw-pc.tail1234.ts.net`).

---

### Stap 2: CNAME Records Toevoegen bij de DNS Hoster

Ga naar het DNS-beheer bij je huidige hostingprovider (waar `jouwdomein.nl` en de e-mail / Zoho Mail staan geregistreerd).

Voeg voor elk gewenst subdomein (of als wildcard `*`) een **CNAME record** toe:

| Veld | Waarde | Toelichting |
|---|---|---|
| **Host / Naam** | `app1` *(of `*` voor alle subdomeinen)* | Het subdomein (bijv. `app1.jouwdomein.nl`) |
| **Type** | `CNAME` | Alias verwijzing |
| **Waarde / Doel** | `jouw-pc.tail1234.ts.net` | Het Tailscale-adres uit Stap 1 |
| **TTL** | `3600` *(of Standaard)* | Tijd tot verversing |

> **Belangrijk:** Laat alle bestaande MX, TXT (SPF/DKIM) records voor Zoho Mail ongewijzigd staan.

---

### Stap 3: Containers Starten in DockerManager

1. Start **`DockerManager.App.exe`**.
2. Selecteer in de profiel-dropdown: **`Oracle`**.
3. Klik op de knop **`▶ Start Alles`**.
4. De containers starten op met de volgende lokale poorttoewijzingen:
   * **NGINX Proxy Manager:** Poort `8080` (HTTP doorgifte) en `81` (Beheer Web GUI).
   * **KennisBase:** Poort `8007`.
   * **Versie API:** Poort `8002`.
   * **Berger Scoresheet:** Poort `8009`.

---

### Stap 4: NGINX Proxy Manager Inrichten (Web GUI)

1. Open je browser en ga naar de beheeromgeving:
   👉 **`http://localhost:81`**
   * *Standaard inlog (eerste keer):*
     * **E-mail:** `admin@example.com`
     * **Wachtwoord:** `changeme`
   * Wijzig direct je e-mail en wachtwoord naar eigen keuze.

2. **Proxy Host toevoegen per applicatie:**
   * Ga in het menu naar **Hosts ➔ Proxy Hosts** en klik op **Add Proxy Host**.
   * Vul de gegevens in voor de betreffende app:

   #### Voorbeeld 1: KennisBase (`app1.jouwdomein.nl`)
   * **Domain Names:** `app1.jouwdomein.nl`
   * **Scheme:** `http`
   * **Forward Hostname / IP:** `host.docker.internal`
   * **Forward Port:** `8007`
   * Vink aan: `Block Common Exploits` en `Websockets Support`.
   * Klik op **Save**.

   #### Voorbeeld 2: Versie API (`app2.jouwdomein.nl`)
   * **Domain Names:** `app2.jouwdomein.nl`
   * **Scheme:** `http`
   * **Forward Hostname / IP:** `host.docker.internal`
   * **Forward Port:** `8002`
   * Klik op **Save**.

   #### Voorbeeld 3: Berger Scoresheet (`app3.jouwdomein.nl`)
   * **Domain Names:** `app3.jouwdomein.nl`
   * **Scheme:** `http`
   * **Forward Hostname / IP:** `host.docker.internal`
   * **Forward Port:** `8009`
   * Klik op **Save**.

---

### 📂 Waar staan de gegevens en configuraties?

Alle data en configuraties worden door DockerManager automatisch geplaatst in de map:
```text
[Map met DockerManager.App.exe]\volumes\oracle\
├── nginx\
│   ├── data\          <-- Alle NGINX Proxy instellingen & hosts
│   └── letsencrypt\   <-- SSL certificaten
└── kennisbase\        <-- SQLite database & media uploads
```

* **Back-up maken:** Klik in DockerManager op **`[ 💾 Back-up ]`** om met 1 klik een complete ZIP van alle configuraties te genereren.

---

### 🛠️ Veelvoorkomend Onderhoud & Nuttige Commando's

| Actie | Commando / Handeling |
|---|---|
| **Tailscale Funnel Status bekijken** | `tailscale funnel status` |
| **Tailscale Funnel Uitschakelen** | `tailscale funnel --bg 8080 off` |
| **Containers Updaten met nieuwste versies** | In DockerManager: Klik op `[ 🚀 Update Alles ]` |
| **Schijfruimte Opschonen** | In DockerManager: Klik op `[ 🧹 Opschonen ]` |
