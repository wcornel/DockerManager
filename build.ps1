<#
.SYNOPSIS
    Build script voor DockerManager (.NET 10 WPF & Velopack)
#>

param (
    [string]$Version = "",
    [string]$Configuration = "Release",
    [string]$OutputDir = "publish",
    [string]$ReleasesDir = "Releases",
    [switch]$SkipTests,
    [switch]$LocalOnly,
    [switch]$Publish,
    [string]$GitHubToken = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Version) -or $Version -eq "auto") {
    $highestVersion = [version]"1.0.0"
    $foundExisting = $false

    if (Test-Path "$ReleasesDir\releases.win.json") {
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
Write-Host "  DockerManager - Release & Velopack Build" -ForegroundColor Cyan
Write-Host "  Versie: $Version ($Configuration)" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""

$ProjectFile = "src/DockerManager.App/DockerManager.App.csproj"

# 1. Unit Tests
if (-not $SkipTests) {
    $testProjects = Get-ChildItem -Path "tests" -Filter "*Tests.csproj" -Recurse -ErrorAction SilentlyContinue
    if ($testProjects) {
        Write-Host "[1/5] Geautomatiseerde Unit Tests uitvoeren..." -ForegroundColor Yellow
        foreach ($testProj in $testProjects) {
            Write-Host "  Testen van $($testProj.Name)..." -ForegroundColor Gray
            dotnet test $testProj.FullName -c $Configuration --nologo
            if ($LASTEXITCODE -ne 0) {
                Write-Host "FOUT: Unit tests zijn mislukt in $($testProj.Name)! Publicatie afgebroken." -ForegroundColor Red
                exit 1
            }
        }
        Write-Host "Alle Unit Tests zijn geslaagd!" -ForegroundColor Green
    }
} else {
    Write-Host "[1/5] Unit Tests overgeslagen (-SkipTests)." -ForegroundColor Gray
}

# 2. Clean
Write-Host "[2/5] Oude publish map opschonen..." -ForegroundColor Yellow
if (Test-Path $OutputDir) {
    Remove-Item -Path $OutputDir -Recurse -Force
}

# 3. Publish
Write-Host "[3/5] Single-file executable compileren ($Configuration, v$Version)..." -ForegroundColor Yellow
dotnet publish $ProjectFile `
    -c $Configuration `
    -r win-x64 `
    -p:SelfContained=false `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $OutputDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build mislukt!" -ForegroundColor Red
    exit $LASTEXITCODE
}

# 4. Profiles
Write-Host "[4/5] Profielen kopiëren naar publish map..." -ForegroundColor Yellow
if (Test-Path "profiles") {
    $targetProfilesDir = Join-Path $OutputDir "profiles"
    if (-not (Test-Path $targetProfilesDir)) {
        New-Item -ItemType Directory -Path $targetProfilesDir -Force | Out-Null
    }
    Copy-Item -Path "profiles\*" -Destination $targetProfilesDir -Recurse -Force
}

# 5. Velopack
if (-not $LocalOnly) {
    Write-Host "[5/5] Velopack Setup & Update Pakket genereren (vpk pack)..." -ForegroundColor Yellow
    if (-not (Test-Path $ReleasesDir)) {
        New-Item -ItemType Directory -Path $ReleasesDir -Force | Out-Null
    }

    try {
        vpk pack -u DockerManager -v $Version -p $OutputDir -e "DockerManager.App.exe" -o $ReleasesDir --icon "src/DockerManager.App/app.ico"
        Write-Host "Velopack pakket v$Version succesvol aangemaakt in '$ReleasesDir'!" -ForegroundColor Green

        $setupSource = Join-Path $ReleasesDir "DockerManager-win-Setup.exe"
        if (Test-Path $setupSource) {
            $setupDest = Join-Path $OutputDir "DockerManager-Setup.exe"
            Copy-Item $setupSource $setupDest -Force
        }
    } catch {
        Write-Host "Waarschuwing: Velopack pack kon niet worden voltooid: $_" -ForegroundColor Yellow
    }

    if ($Publish) {
        Write-Host ""
        Write-Host "Bezig met uploaden naar GitHub Releases..." -ForegroundColor Cyan
        $tokenArg = if ($GitHubToken) { "--token $GitHubToken" } else { "" }
        try {
            Invoke-Expression "vpk upload github --repoUrl 'https://github.com/wcornel/DockerManager' --outputDir '$ReleasesDir' --tag 'v$Version' --publish $tokenArg"
            Write-Host "Release v$Version staat live op GitHub Releases!" -ForegroundColor Green
        } catch {
            Write-Host "Fout bij uploaden naar GitHub: $_" -ForegroundColor Red
        }
    }
} else {
    Write-Host "[5/5] Velopack overgeslagen (-LocalOnly). Alleen lokale executable gegenereerd." -ForegroundColor Cyan
}

$SetupPath = Join-Path $OutputDir "DockerManager-Setup.exe"
$ExePath = Join-Path $OutputDir "DockerManager.App.exe"

Write-Host ""
Write-Host "=========================================" -ForegroundColor Green
Write-Host "  Build Succesvol Voltooid!" -ForegroundColor Green
if (Test-Path $SetupPath) {
    $SetupSizeMb = [math]::Round((Get-Item $SetupPath).Length / 1MB, 2)
    Write-Host "  [INSTALLER] $SetupPath ($SetupSizeMb MB)  --> GEBRUIK DEZE" -ForegroundColor Green
}
if (Test-Path $ExePath) {
    $SizeMb = [math]::Round((Get-Item $ExePath).Length / 1MB, 2)
    Write-Host "  [PORTABLE]  $ExePath ($SizeMb MB)" -ForegroundColor Gray
}
Write-Host "=========================================" -ForegroundColor Green
