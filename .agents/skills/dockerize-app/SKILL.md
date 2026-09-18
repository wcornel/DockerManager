---
name: dockerize-app
description: Standardized workflow to dockerize .NET and Python applications for DockerManager and GitHub Container Registry (ghcr.io). Automatically generates multi-arch Dockerfiles, builds GitHub Actions CI/CD workflows, detects appsettings.json/os.getenv configuration, and formats DockerManager profile JSON with smart production checks (Timezone, Volumes, Healthchecks, Secrets).
---

# Dockerize App Skill (DockerManager & GHCR Standard)

Use this skill whenever the user asks to dockerize an application, create a Dockerfile, or set up container publishing for .NET or Python projects.

## 1. Detection Phase

Inspect the project workspace to detect the framework:

### A. .NET Application (.NET Core / 8 / 9 / 10)
1. Check `*.csproj` or `*.sln` to detect Target Framework (`net8.0`, `net9.0`, `net10.0`).
2. Identify the entrypoint assembly (e.g. `MijnApp.dll`).
3. Scan `appsettings.json` and `appsettings.Production.json` to extract configurable parameters:
   - Convert nested keys (e.g. `ConnectionStrings:DefaultConnection`) into environment variables with double underscore: `ConnectionStrings__DefaultConnection`.
   - Categorize parameters (Database, API Keys, URLs, General).
4. Default container port: `8080`.

### B. Python Application (FastAPI, Flask, Django, Worker)
1. Check for `requirements.txt`, `Pipfile` or `pyproject.toml`.
2. Detect start command:
   - Django: `python manage.py runserver 0.0.0.0:8000`
   - FastAPI / Uvicorn in requirements: `uvicorn main:app --host 0.0.0.0 --port 8000` (or `app:app`)
   - Flask / Standard: `python main.py` or `python app.py`
3. Scan `.env`, `.env.example`, and `*.py` files for `os.getenv('...')` and `os.environ['...']`.
4. Default container port: `8000`.

---

## 2. Smart Checks & Hardening Questionnaire (Best Practices)

Before generating the Dockerfile and Profile JSON, evaluate and apply these 5 quality checks:

### 1. 💾 Persistent Data & Volumes Check
- Ask or detect if the app writes local files (e.g., `uploads/`, `logs/`, `data/`, `sqlite.db`, exported PDFs).
- If yes, configure a persistent host volume in DockerManager:
  `./volumes/<app-name>/data:/app/data` (or `/app/uploads`).

### 2. ⏰ Timezone & Locale (Dutch Standard)
- Add `ENV TZ=Europe/Amsterdam` to ensure local timestamps, log events, and Dutch scheduling match reality (preventing UTC offset confusion).
- For .NET: set `ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false` so Dutch currency and `dd-MM-yyyy` parsing work seamlessly.

### 3. 💓 Health Checks (Self-Healing)
- Check if the app has a healthcheck endpoint (e.g., `/health`, `/healthz`, `/api/ping`).
- If present, add Docker `HEALTHCHECK` directive:
  ```dockerfile
  HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1
  ```

### 4. 🔒 Secret Sanitization (Security Gate)
- Ensure **NO real passwords, tokens, or connection strings with credentials** are baked into the Dockerfile image.
- Always replace real credentials in the Dockerfile with empty environment placeholders:
  `ENV ConnectionStrings__DefaultConnection=""`
  `ENV ApiKey=""`
- Secrets should be configured in DockerManager UI or via environment variables at runtime.

### 5. 🔗 Network & Inter-Container Communication
- Containers in the same DockerManager profile share a custom bridge network (`dockermanager_<profile>`).
- Direct container-to-container connections should use the destination **ContainerName** as DNS hostname (e.g. `http://oracle-db:1521` or `http://nginx:80`).
- Set `"RestartPolicy": "unless-stopped"` to gracefully handle database startup delays.

---

## 3. Dockerfile Generation Standard

### A. .NET Production Dockerfile Template
```dockerfile
# Multi-stage or runtime Dockerfile for .NET
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Install curl for healthchecks and tzdata for Amsterdam timezone
RUN apt-get update && apt-get install -y --no-install-recommends curl tzdata && rm -rf /var/lib/apt/lists/*

COPY . .

# Environment Configuration
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV TZ=Europe/Amsterdam

# Environment variable placeholders (from appsettings.json):
# ENV ConnectionStrings__DefaultConnection=""
# ENV ApiKey=""

EXPOSE 8080

# Healthcheck
# HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "<EntrypointDll>.dll"]
```

### B. Python Production Dockerfile Template
```dockerfile
FROM python:3.12-slim AS runtime
WORKDIR /app

# Install curl for healthchecks and tzdata
RUN apt-get update && apt-get install -y --no-install-recommends curl tzdata && rm -rf /var/lib/apt/lists/*

# Optimize layer caching: copy requirements first
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY . .

ENV PYTHONUNBUFFERED=1
ENV PORT=8000
ENV TZ=Europe/Amsterdam

# Environment variable placeholders (from os.getenv):
# ENV DATABASE_URL=""

EXPOSE 8000

# Healthcheck
# HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 CMD curl -f http://localhost:8000/health || exit 1

CMD ["python", "main.py"]
```

---

## 4. GitHub Actions Multi-Arch CI/CD Workflow

Generate `.github/workflows/docker-build.yml` for automated Multi-Arch builds (`linux/amd64` + `linux/arm64`):

```yaml
name: Build & Push Docker Container Images

on:
  push:
    branches: [ main, master ]
  workflow_dispatch:

jobs:
  build-and-push:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write

    steps:
      - name: Checkout repository
        uses: actions/checkout@v4

      - name: Set up QEMU
        uses: docker/setup-qemu-action@v3

      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v3

      - name: Log in to GitHub Container Registry (ghcr.io)
        uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Build and push multi-arch image
        run: |
          IMAGE_TAG="ghcr.io/${{ github.repository_owner }}/<app-name>:latest"
          IMAGE_TAG_LOWER=$(echo "$IMAGE_TAG" | tr '[:upper:]' '[:lower:]')
          
          docker buildx build \
            --platform linux/amd64,linux/arm64 \
            --tag "$IMAGE_TAG_LOWER" \
            --push \
            .
```

---

## 5. DockerManager Profile Integration

Generate the JSON service definition block to add to DockerManager (`profiles/*.json`):

```json
{
  "Id": "<app-name>",
  "DisplayName": "<App Display Name>",
  "ContainerName": "<app-name>",
  "Image": "ghcr.io/wcornel/<app-name>:latest",
  "Description": "<Framework> app '<app-name>'",
  "AutoStart": true,
  "RequiresAuth": true,
  "RestartPolicy": "unless-stopped",
  "Ports": [
    {
      "HostPort": 8080,
      "ContainerPort": 8080,
      "Protocol": "tcp"
    }
  ],
  "Volumes": [
    {
      "HostPath": "./volumes/<app-name>/data",
      "ContainerPath": "/app/data",
      "IsNamedVolume": false,
      "ReadOnly": false
    }
  ],
  "Environment": {
    "ConnectionStrings__DefaultConnection": "",
    "TZ": "Europe/Amsterdam"
  }
}
```
