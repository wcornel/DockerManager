# Handleiding: Externe Docker Host Beheren via Tailscale & DockerManager

Deze handleiding legt uit hoe je vanuit **DockerManager** (op je Windows machine) een **externe Docker host** (zoals een Linux VPS, Ubuntu server, Raspberry Pi of thuisserver) op afstand beheert over een veilige **Tailscale** VPN-verbinding.

---

## 🗺️ Architectuur

```text
[ Windows PC ]                                       [ Externe Linux Server ]
┌───────────────────────────┐                        ┌──────────────────────────────┐
│ DockerManager.App.exe     │                        │ Docker Daemon (dockerd)      │
│ (Beheer GUI)              │                        │                              │
│             │             │                        │ Luistert op:                 │
│             ▼             │                        │ 100.x.y.z:2375 (Tailscale)   │
│   Tailscale Client        │ === Versleutelde ===>  │ Tailscale Client             │
│   (100.a.b.c)             │     VPN Tunnel         │ (100.x.y.z)                  │
└───────────────────────────┘                        │                              │
                                                     │ Containers (Nginx, Apps, ...)│
                                                     └──────────────────────────────┘
```

---

## 🛠️ Stap 1: Docker Daemon op de Externe Linux Server Configureren

Standaard luistert Docker op Linux alleen op de lokale socket (`/var/run/docker.sock`). Om Docker over Tailscale bereikbaar te maken, maken we een **systemd override** aan.

> ⚠️ **Waarom geen `/etc/docker/daemon.json`?**  
> Het toevoegen van `"hosts": ["tcp://..."]` in `daemon.json` veroorzaakt op moderne Linux-distributies (systemd) vaak een foutmelding omdat de startup-flags van systemd ermee conflicteren. Een systemd drop-in override is de officiële en stabiele manier.

### 1.1 Tailscale IP-adres van de server opvragen
Voer op de externe Linux-server uit:
```bash
tailscale ip -4
```
*Noteer het adres (bijvoorbeeld `100.80.90.100`).*

### 1.2 Override bestand aanmaken
Voer uit op de server:
```bash
sudo systemctl edit docker.service
```

Plaats de volgende regels in de geopende editor (vervang `100.x.y.z` door het Tailscale IP uit stap 1.1):
```ini
[Service]
ExecStart=
ExecStart=/usr/bin/dockerd -H fd:// -H tcp://100.x.y.z:2375
```

> 🔒 **Veiligheid:** Door hier specifiek je **Tailscale IP** (`100.x.y.z`) in te vullen en géén `0.0.0.0`, is poort 2375 **alleen** bereikbaar binnen jouw beveiligde Tailscale netwerk en nergens op het openbare internet.

### 1.3 Docker herstarten
```bash
sudo systemctl daemon-reload
sudo systemctl restart docker
```

Controleer of Docker actief is en luistert:
```bash
sudo ss -tulpn | grep 2375
```

---

## 💻 Stap 2: DockerManager Verbinden

1. Start **DockerManager** op je Windows-machine.
2. Ga naar **Instellingen** (⚙️).
3. Selecteer bij **Docker Host Type**: **`TCP Socket`**.
4. Vul bij de URL het Tailscale IP en poort 2375 in:
   ```text
   tcp://100.x.y.z:2375
   ```
5. Klik op **Opslaan**.
6. DockerManager maakt nu direct verbinding met de externe machine! Je ziet alle actieve containers van de server verschijnen.

---

## 🌐 Stap 3: Cloudflare Tunnel & Subdomeinen op de Externe Host

Als je apps op de externe server via een **Cloudflare Tunnel** wereldwijd bereikbaar wilt maken onder je eigen domein (`*.jouwdomein.nl`), werkt dat naadloos samen met DockerManager:

### Hoe werkt dit onder water?

```text
[ DockerManager op Windows ]
   │
   ├── 1. [HTTPS API Call] ──────────▶ [ Cloudflare Cloud ]
   │      - Maakt DNS CNAME aan           - Koppelt sub.jouwdomein.nl
   │      - Voegt Ingress regel toe       - Stuurt verkeer naar Tunnel ID
   │
   └── 2. [Tailscale TCP 2375] ──────▶ [ Externe Linux Server ]
          - Start containers               - Container 'cloudflared' start op server
                                           - Container 'nginx' / apps starten op server
                                           - 'cloudflared' verbindt met Cloudflare
```

1. **Beheer vanuit DockerManager:**  
   Wanneer je in DockerManager een nieuw subdomein registreert, praat DockerManager rechtstreeks via HTTPS met de Cloudflare API om het DNS CNAME-record en de tunnel-ingressregel te configureren.
2. **Tunnel start op de server:**  
   Omdat DockerManager met de externe server verbonden is, start de container `cloudflared` (met jouw Tunnel Token) fysiek **op de externe Linux server**.
3. **Rechtstreekse tunnelverbinding:**  
   De `cloudflared` container op de server legt de uitgaande verbinding naar Cloudflare. Inkomende bezoekers komen direct op de externe server uit bij poort 8080 (NGINX) of je container.
4. **Onafhankelijk:**  
   Jouw lokale Windows-machine hoeft **niet aan te blijven staan** voor de bezoekers; de externe server draait 24/7 zelfstandig door!
