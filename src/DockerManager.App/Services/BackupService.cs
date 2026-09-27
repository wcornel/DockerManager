using System.IO;
using System.IO.Compression;
using System.Text.Json;
using DockerManager.App.Models;

namespace DockerManager.App.Services;

public interface IBackupService
{
    Task<BackupResult> CreateBackupAsync(
        ProfileModel profile,
        string destinationZipPath,
        IProgress<(string message, double percentage)>? progress = null,
        CancellationToken ct = default);

    Task<RestoreResult> RestoreBackupAsync(
        string sourceZipPath,
        ProfileModel? targetProfile = null,
        bool overwriteExisting = true,
        IProgress<(string message, double percentage)>? progress = null,
        CancellationToken ct = default);
}

public class BackupResult
{
    public bool Success { get; set; }
    public string ZipPath { get; set; } = string.Empty;
    public int FilesCount { get; set; }
    public long TotalBytes { get; set; }
    public List<string> Warnings { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

public class RestoreResult
{
    public bool Success { get; set; }
    public int RestoredFilesCount { get; set; }
    public long RestoredBytes { get; set; }
    public ProfileModel? RestoredProfile { get; set; }
    public List<string> Warnings { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

public class BackupService : IBackupService
{
    private readonly ISettingsService _settingsService;

    public BackupService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task<BackupResult> CreateBackupAsync(
        ProfileModel profile,
        string destinationZipPath,
        IProgress<(string message, double percentage)>? progress = null,
        CancellationToken ct = default)
    {
        var result = new BackupResult
        {
            ZipPath = destinationZipPath
        };

        try
        {
            var targetDir = Path.GetDirectoryName(destinationZipPath);
            if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            var filesToBackup = new List<(string sourcePath, string entryPath)>();
            var baseDirs = GetBaseDirectories();
            var server = _settingsService.Settings.GetActiveServer();
            var isRemote = server.HostType.Equals("Tcp", StringComparison.OrdinalIgnoreCase);

            if (isRemote)
            {
                result.Warnings.Add($"Let op: '{server.Name}' is een externe VPS/server ({server.TcpUrl}). De fysieke bestanden staan op de externe host ({server.GetEffectiveVolumesRootPath()}) en kunnen niet rechtstreeks via het lokale bestandssysteem worden ingepakt. Deze back-up bevat de volledige profiel- en containerconfiguratie.");
            }

            // Collect all files from volumes
            foreach (var service in profile.Services)
            {
                var containerName = string.IsNullOrWhiteSpace(service.ContainerName) ? service.Id : service.ContainerName;

                foreach (var vol in service.Volumes)
                {
                    if (string.IsNullOrWhiteSpace(vol.HostPath)) continue;

                    var resolvedPath = ResolveHostPath(vol.HostPath, profile, server, baseDirs);
                    var volFolderSafe = SanitizeEntryName(Path.GetFileName(vol.HostPath.TrimEnd('/', '\\')));
                    if (string.IsNullOrWhiteSpace(volFolderSafe)) volFolderSafe = "vol";

                    if (File.Exists(resolvedPath))
                    {
                        var fileName = Path.GetFileName(resolvedPath);
                        filesToBackup.Add((resolvedPath, $"{containerName}/{volFolderSafe}/{fileName}"));
                    }
                    else if (Directory.Exists(resolvedPath))
                    {
                        var dirFiles = Directory.GetFiles(resolvedPath, "*", SearchOption.AllDirectories);
                        foreach (var f in dirFiles)
                        {
                            var rel = Path.GetRelativePath(resolvedPath, f).Replace('\\', '/');
                            filesToBackup.Add((f, $"{containerName}/{volFolderSafe}/{rel}"));
                        }
                    }
                    else if (!isRemote)
                    {
                        result.Warnings.Add($"Pad voor volume '{vol.HostPath}' ({containerName}) niet gevonden op host.");
                    }
                }
            }

            // Create ZIP
            using (var zipStream = new FileStream(destinationZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var total = filesToBackup.Count;
                var current = 0;

                foreach (var (src, entryName) in filesToBackup)
                {
                    ct.ThrowIfCancellationRequested();
                    current++;
                    var pct = total > 0 ? (double)current / total * 100.0 : 100.0;
                    progress?.Report(($"Inpakken ({current}/{total}): {Path.GetFileName(src)}", pct));

                    try
                    {
                        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                        using (var srcStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var entryStream = entry.Open())
                        {
                            await srcStream.CopyToAsync(entryStream, ct);
                            result.TotalBytes += srcStream.Length;
                        }
                        result.FilesCount++;
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Kon bestand '{src}' niet toevoegen: {ex.Message}");
                    }
                }

                // Add profile.json
                try
                {
                    var profileEntry = archive.CreateEntry("profile.json", CompressionLevel.Optimal);
                    using (var entryStream = profileEntry.Open())
                    using (var writer = new StreamWriter(entryStream))
                    {
                        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
                        await writer.WriteAsync(json);
                    }
                }
                catch { }

                // Add manifest.txt
                try
                {
                    var manifestEntry = archive.CreateEntry("backup_manifest.txt", CompressionLevel.Optimal);
                    using (var entryStream = manifestEntry.Open())
                    using (var writer = new StreamWriter(entryStream))
                    {
                        await writer.WriteLineAsync($"DockerManager Back-up Manifest");
                        await writer.WriteLineAsync($"Datum: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        await writer.WriteLineAsync($"Profiel: {profile.Name}");
                        await writer.WriteLineAsync($"Aantal bestanden: {result.FilesCount}");
                        await writer.WriteLineAsync($"Totale omvang: {result.TotalBytes / 1024.0 / 1024.0:F2} MB");
                        if (result.Warnings.Count > 0)
                        {
                            await writer.WriteLineAsync();
                            await writer.WriteLineAsync("Waarschuwingen:");
                            foreach (var w in result.Warnings)
                            {
                                await writer.WriteLineAsync($"- {w}");
                            }
                        }
                    }
                }
                catch { }
            }

            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    public async Task<RestoreResult> RestoreBackupAsync(
        string sourceZipPath,
        ProfileModel? targetProfile = null,
        bool overwriteExisting = true,
        IProgress<(string message, double percentage)>? progress = null,
        CancellationToken ct = default)
    {
        var result = new RestoreResult();

        try
        {
            if (!File.Exists(sourceZipPath))
            {
                result.Success = false;
                result.ErrorMessage = $"Back-up bestand '{sourceZipPath}' niet gevonden.";
                return result;
            }

            var baseDirs = GetBaseDirectories();
            var server = _settingsService.Settings.GetActiveServer();

            using (var zipStream = new FileStream(sourceZipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                // Read profile.json if exists
                var profileEntry = archive.GetEntry("profile.json");
                ProfileModel? backupProfile = null;
                if (profileEntry != null)
                {
                    try
                    {
                        using var reader = new StreamReader(profileEntry.Open());
                        var json = await reader.ReadToEndAsync(ct);
                        backupProfile = JsonSerializer.Deserialize<ProfileModel>(json);
                        result.RestoredProfile = backupProfile;
                    }
                    catch { }
                }

                var activeProfile = targetProfile ?? backupProfile;
                if (activeProfile == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "Geen profielconfiguratie gevonden in het archief of geselecteerd.";
                    return result;
                }

                var fileEntries = archive.Entries
                    .Where(e => !string.IsNullOrWhiteSpace(e.Name) && e.FullName != "profile.json" && e.FullName != "backup_manifest.txt")
                    .ToList();

                var total = fileEntries.Count;
                var current = 0;

                foreach (var entry in fileEntries)
                {
                    ct.ThrowIfCancellationRequested();
                    current++;
                    var pct = total > 0 ? (double)current / total * 100.0 : 100.0;
                    progress?.Report(($"Herstellen ({current}/{total}): {entry.Name}", pct));

                    var parts = entry.FullName.Split('/');
                    if (parts.Length < 3) continue;

                    var containerName = parts[0];
                    var volFolder = parts[1];
                    var relativePath = string.Join('/', parts.Skip(2));

                    // Find matching service
                    var service = activeProfile.Services.FirstOrDefault(s =>
                        (string.IsNullOrWhiteSpace(s.ContainerName) ? s.Id : s.ContainerName) == containerName ||
                        s.Id == containerName);

                    string targetFilePath;
                    if (service != null)
                    {
                        var vol = service.Volumes.FirstOrDefault(v =>
                            SanitizeEntryName(Path.GetFileName(v.HostPath.TrimEnd('/', '\\'))) == volFolder ||
                            v.HostPath.Contains(volFolder));

                        if (vol != null)
                        {
                            var resolvedHostPath = ResolveHostPath(vol.HostPath, activeProfile, server, baseDirs);
                            if (File.Exists(resolvedHostPath) || (parts.Length == 3 && Path.HasExtension(resolvedHostPath) && !Directory.Exists(resolvedHostPath)))
                            {
                                targetFilePath = resolvedHostPath;
                            }
                            else
                            {
                                targetFilePath = Path.Combine(resolvedHostPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                            }
                        }
                        else
                        {
                            var primaryDir = server.GetEffectiveVolumesRootPath();
                            var sub = activeProfile.GetEffectiveVolumesSubfolder();
                            targetFilePath = Path.Combine(primaryDir, sub, containerName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                        }
                    }
                    else
                    {
                        var primaryDir = server.GetEffectiveVolumesRootPath();
                        var sub = activeProfile.GetEffectiveVolumesSubfolder();
                        targetFilePath = Path.Combine(primaryDir, sub, containerName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    }

                    var parentDir = Path.GetDirectoryName(targetFilePath);
                    if (!string.IsNullOrWhiteSpace(parentDir) && !Directory.Exists(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    if (File.Exists(targetFilePath) && !overwriteExisting)
                    {
                        result.Warnings.Add($"Overgeslagen (bestaat al): {targetFilePath}");
                        continue;
                    }

                    try
                    {
                        using (var entryStream = entry.Open())
                        using (var outStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                        {
                            await entryStream.CopyToAsync(outStream, ct);
                            result.RestoredBytes += entry.Length;
                            result.RestoredFilesCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Kon '{targetFilePath}' niet herstellen: {ex.Message}");
                    }
                }
            }

            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    private List<string> GetBaseDirectories()
    {
        return new[]
        {
            _settingsService.Settings.LocalProfilesFolder,
            AppDomain.CurrentDomain.BaseDirectory,
            Environment.CurrentDirectory
        }.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)).ToList();
    }

    private static string ResolveHostPath(string hostPath, ProfileModel profile, DockerServerEnvironment server, List<string> baseDirs)
    {
        if (Path.IsPathRooted(hostPath) && (File.Exists(hostPath) || Directory.Exists(hostPath)))
        {
            return hostPath;
        }

        var clean = hostPath.TrimStart('.', '/', '\\');
        if (clean.StartsWith("volumes\\", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("volumes/", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[8..];
        }

        var serverRoot = server.GetEffectiveVolumesRootPath();
        var profileSubfolder = profile.GetEffectiveVolumesSubfolder();

        // 1. Check serverRoot / profileSubfolder / clean
        var candidateWithProfile = Path.Combine(serverRoot, profileSubfolder, clean);
        if (File.Exists(candidateWithProfile) || Directory.Exists(candidateWithProfile))
        {
            return candidateWithProfile;
        }

        // 2. Check serverRoot / clean
        var candidateDirect = Path.Combine(serverRoot, clean);
        if (File.Exists(candidateDirect) || Directory.Exists(candidateDirect))
        {
            return candidateDirect;
        }

        // 3. Check legacy baseDirs
        foreach (var dir in baseDirs)
        {
            var combinedWithProfile = Path.Combine(dir, "volumes", profileSubfolder, clean);
            if (File.Exists(combinedWithProfile) || Directory.Exists(combinedWithProfile))
            {
                return combinedWithProfile;
            }

            var combined = Path.Combine(dir, clean);
            if (File.Exists(combined) || Directory.Exists(combined))
            {
                return combined;
            }
        }

        return candidateWithProfile;
    }

    private static string SanitizeEntryName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
