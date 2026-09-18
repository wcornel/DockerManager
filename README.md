# 🐳 DockerManager

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![CI](https://github.com/wcornel/DockerManager/actions/workflows/ci.yml/badge.svg)](https://github.com/wcornel/DockerManager/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-lightgrey.svg)]()
[![Velopack](https://img.shields.io/badge/Installer-Velopack-blueviolet.svg)](https://velopack.io/)

A modern, high-performance desktop hub built with **C# .NET 10 (WPF)** for managing Docker containers across local and remote environments using central GitHub or local profiles.

---

## ✨ Features

* **🌐 Bilingual Support (i18n):**
  * Instant runtime language switching between **English (EN)** and **Dutch (NL)** in Settings.
* **🐙 GitHub Central Profiles Sync:**
  * Synchronize and manage container profiles directly from public or private GitHub repositories.
  * Personal Access Tokens (PAT) and secrets are securely encrypted using **Windows DPAPI**.
* **🚀 Zero-Downtime Container Updates (Volume & Data Preservation):**
  * Pulls the newest image layer and recreates the container while keeping all volume mounts and configurations intact.
* **🔍 Port Conflict Detection & Windows Host Analyzer:**
  * Live detection of port collisions between containers and local Windows system services.
* **💾 Container Volume Backup & Restore:**
  * One-click ZIP backup and restore of all persistent container directories and configuration files.
* **📦 Integrated .NET & Python Dockerizer Wizard:**
  * Built-in wizard to containerize and publish .NET & Python apps directly to GitHub Container Registry (ghcr.io).
* **⚡ Smart Docker Startup Diagnostics:**
  * Automatically checks Windows service status and provides instant one-click launch if Docker is offline.
* **📜 Real-time Live Log Streaming:**
  * Embedded log viewer for inspecting stdout/stderr container output.

---

## 🚀 Getting Started

### Installation
Download the latest **`DockerManager-Setup.exe`** from the [Releases](https://github.com/wcornel/DockerManager/releases) page.
The installer automatically creates desktop and start menu shortcuts and handles silent delta updates.

### Configuration (`profiles/example.json`)
```json
{
  "name": "Example Stack",
  "description": "Demonstration profile featuring a web server and cache",
  "autoStopPreviousOnSwitch": false,
  "services": [
    {
      "id": "nginx-web",
      "displayName": "Nginx Web Server",
      "image": "nginx:alpine",
      "containerName": "demo-nginx",
      "ports": [
        { "hostPort": 8080, "containerPort": 80, "protocol": "tcp" }
      ],
      "volumes": [
        { "hostPath": "./volumes/nginx/html", "containerPath": "/usr/share/nginx/html", "isNamedVolume": false }
      ]
    }
  ]
}
```

---

## 🛠️ Building from Source

### Prerequisites
* Windows 10 / 11 (x64)
* [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
* [Velopack CLI](https://velopack.io/) (`dotnet tool install -g vpk`)

### Build & Package
Run the automated build script:
```powershell
./build.ps1
```

---

## 🤝 Contributing

Contributions, bug reports, and feature requests are very welcome!
1. Fork the repository
2. Create your feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

---

## 🙏 Acknowledgments

DockerManager is powered by these great open-source projects:
* [Docker.DotNet](https://github.com/dotnet/Docker.DotNet) — .NET client library for Docker Remote API
* [Velopack](https://velopack.io/) — Fast, zero-friction installer and auto-updater framework for desktop apps
* [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — Official modern .NET MVVM toolkit
* [YamlDotNet](https://github.com/aaubry/YamlDotNet) — .NET library for YAML serialization and parsing

---

## 📄 License

Distributed under the **MIT License**. Copyright (c) 2026 W. Cornelissen. See [`LICENSE`](LICENSE) for details.
