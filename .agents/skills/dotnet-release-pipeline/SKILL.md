---
name: dotnet-release-pipeline
description: Automated pipeline to test, build, version, and package .NET applications. Automatically runs unit tests, determines SemVer/date versions, creates ultra-compact single-file binaries, and smartly chooses between Velopack (for public open-source repos) or standalone portable distribution (for private/internal repos).
---

# .NET Release Pipeline Skill

Use this skill whenever the user asks to create a build script, release an application, build a version, or package a .NET project.

## 1. Pipeline Principles & Quality Gate

Every release MUST follow this 5-step sequence:

### Step 1: Unit Tests (Quality Gate)
- Search for test projects matching `*Test*.csproj` or in `tests/`.
- Run: `dotnet test <test-project> -c Release --nologo`.
- **CRITICAL:** If any test fails (`$LASTEXITCODE -ne 0`), **immediately abort the pipeline** and report the failure. Never build or release broken code.

### Step 2: Smart Distribution Strategy (Public vs. Private)
Determine the packaging strategy based on the repository/project type:
- **Public / Open-Source Repository:**
  - Package with **Velopack** (`vpk pack`) to generate `Setup.exe` and delta updates for public GitHub Releases.
  - No user tokens required for public updates.
- **Private / Internal Corporate Tool (e.g. ProdistUpdater, internal tools):**
  - Package as **Standalone Portable Single-File Executable** (`publish/<AppName>.exe`) or internal API / ZIP distribution.
  - Avoids token-permission friction for external/internal users who don't have access to your private GitHub repo.

### Step 3: Versioning Standard
- Auto-detect previous release version from `Releases/releases.win.json` or Git tags.
- Auto-increment patch version: `1.0.0` -> `1.0.1` -> `1.0.2` (or date-based `Year.Month.Day`).
- Allow overriding via `-Version "<X.Y.Z>"`.

### Step 4: Compact Single-File Compilation
- Use standard .NET publish settings for Windows:
  ```powershell
  dotnet publish <ProjectFile> `
      -c Release `
      -r win-x64 `
      -p:SelfContained=false `
      -p:PublishSingleFile=true `
      -p:Version=$Version `
      -p:IncludeNativeLibrariesForSelfExtract=true `
      -o $OutputDir
  ```

### Step 5: Output & Publishing
- Copy necessary assets (e.g. `profiles/`, `config/`, icons).
- If Velopack is enabled: run `vpk pack` and copy `Setup.exe` directly into `publish/`.
- Output a clear summary with file sizes and instructions.

---

## 2. Standard `build.ps1` Template

When generating or updating `build.ps1` for a .NET project, use this standardized structure:

```powershell
<#
.SYNOPSIS
    Standard Release & Build Script for .NET Application
#>

param (
    [string]$Version = "",
    [string]$Configuration = "Release",
    [string]$OutputDir = "publish",
    [string]$ReleasesDir = "Releases",
    [switch]$SkipTests,
    [switch]$UseVelopack = $true,
    [switch]$Publish,
    [string]$GitHubToken = ""
)

$ErrorActionPreference = "Stop"

# Auto-versioning
if ([string]::IsNullOrWhiteSpace($Version) -or $Version -eq "auto") {
    $highestVersion = [version]"1.0.0"
    $foundExisting = $false

    if ($UseVelopack -and (Test-Path "$ReleasesDir\releases.win.json")) {
        try {
            $json = Get-Content "$ReleasesDir\releases.win.json" -Raw | ConvertFrom-Json
            if ($json.Assets) {
                foreach ($asset in $json.Assets) {
                    $vStr = "$($asset.Version)"
                    if ($vStr -match '^\d+(\.\d+)+') {
                        $parsed = [version]$matches[0]
                        if ($parsed -ge $highestVersion) {
                            $highestVersion = $parsed
                            $foundExisting = $true
                        }
                    }
                }
            }
        } catch { }
    }

    if ($foundExisting) {
        $buildPart = if ($highestVersion.Build -ge 0) { $highestVersion.Build + 1 } else { 1 }
        $Version = "$($highestVersion.Major).$($highestVersion.Minor).$buildPart"
    } else {
        $Version = "1.0.0"
    }
}

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "  Release Build: v$Version ($Configuration)" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan

# 1. Unit Tests
if (-not $SkipTests) {
    $testProjects = Get-ChildItem -Path "." -Filter "*Tests.csproj" -Recurse -ErrorAction SilentlyContinue
    if ($testProjects) {
        Write-Host "[1/4] Geautomatiseerde Unit Tests uitvoeren..." -ForegroundColor Yellow
        foreach ($testProj in $testProjects) {
            Write-Host "  Testen van $($testProj.Name)..." -ForegroundColor Gray
            dotnet test $testProj.FullName -c $Configuration --nologo
            if ($LASTEXITCODE -ne 0) {
                Write-Host "FOUT: Unit tests zijn mislukt in $($testProj.Name)! Build afgebroken." -ForegroundColor Red
                exit 1
            }
        }
        Write-Host "Alle Unit Tests zijn geslaagd!" -ForegroundColor Green
    }
}

# 2. Clean & Publish
Write-Host "[2/4] Compileren naar $OutputDir..." -ForegroundColor Yellow
if (Test-Path $OutputDir) { Remove-Item -Path $OutputDir -Recurse -Force }

dotnet publish $ProjectFile `
    -c $Configuration `
    -r win-x64 `
    -p:SelfContained=false `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $OutputDir

# 3. Optional Velopack Packaging (for Public Repos)
if ($UseVelopack) {
    Write-Host "[3/4] Velopack Setup genereren..." -ForegroundColor Yellow
    if (-not (Test-Path $ReleasesDir)) { New-Item -ItemType Directory -Path $ReleasesDir -Force | Out-Null }
    try {
        vpk pack -u <AppId> -v $Version -p $OutputDir -e "<MainExe>.exe" -o $ReleasesDir
        $setupSource = Join-Path $ReleasesDir "<AppId>-win-Setup.exe"
        if (Test-Path $setupSource) {
            Copy-Item $setupSource (Join-Path $OutputDir "<AppId>-Setup.exe") -Force
        }
    } catch {
        Write-Host "Velopack waarschuwing: $_" -ForegroundColor Yellow
    }
}

Write-Host "=========================================" -ForegroundColor Green
Write-Host "  Build v$Version Succesvol Voltooid!" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Green
```
