using System.Formats.Tar;
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
    private readonly IDockerService? _dockerService;

    public BackupService(ISettingsService settingsService, IDockerService? dockerService = null)
    {
        _settingsService = settingsService;
        _dockerService = dockerService;
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
            var dockerArchiveVolumes = new List<(string containerName, string containerPath, string volFolderSafe)>();
            var baseDirs = GetBaseDirectories();
            var server = _settingsService.Settings.GetActiveServer();
            var isRemote = server.HostType.Equals("Tcp", StringComparison.OrdinalIgnoreCase);

            if (isRemote && _dockerService == null)
            {
                result.Warnings.Add($"Let op: '{server.Name}' is een externe VPS/server ({server.TcpUrl}) zonder beschikbare Docker API-koppeling. De fysieke bestanden op de externe host kunnen niet worden ingepakt; deze back-up bevat alleen de profielconfiguratie.");
            }

            // Collect all files from volumes (local bind-mounts or remote/named volumes via Docker API)
            foreach (var service in profile.Services)
            {
                var containerName = service.GetEffectiveContainerName();

                foreach (var vol in service.Volumes)
                {
                    if (string.IsNullOrWhiteSpace(vol.HostPath) && string.IsNullOrWhiteSpace(vol.ContainerPath)) continue;

                    var volFolderNameRaw = Path.GetFileName((!string.IsNullOrWhiteSpace(vol.HostPath) ? vol.HostPath : vol.ContainerPath).TrimEnd('/', '\\'));
                    var volFolderSafe = SanitizeEntryName(volFolderNameRaw);
                    if (string.IsNullOrWhiteSpace(volFolderSafe)) volFolderSafe = "vol";

                    if (isRemote || vol.IsNamedVolume)
                    {
                        if (string.IsNullOrWhiteSpace(vol.ContainerPath)) continue;

                        if (_dockerService == null)
                        {
                            result.Warnings.Add($"Volume '{vol.ContainerPath}' ({containerName}) is overgeslagen omdat er geen Docker API-service beschikbaar is.");
                            continue;
                        }

                        dockerArchiveVolumes.Add((containerName, vol.ContainerPath, volFolderSafe));
                        continue;
                    }

                    var resolvedPath = ResolveHostPath(vol.HostPath, profile, server, baseDirs);

                    if (File.Exists(resolvedPath))
                    {
                        if (IsReparsePoint(resolvedPath))
                        {
                            result.Warnings.Add($"Symbolische link overgeslagen: {resolvedPath}");
                            continue;
                        }
                        var fileName = Path.GetFileName(resolvedPath);
                        filesToBackup.Add((resolvedPath, $"{containerName}/{volFolderSafe}/{fileName}"));
                    }
                    else if (Directory.Exists(resolvedPath))
                    {
                        if (IsReparsePoint(resolvedPath))
                        {
                            result.Warnings.Add($"Symbolische link of junction overgeslagen: {resolvedPath}");
                            continue;
                        }

                        foreach (var f in EnumerateSafeLocalFiles(resolvedPath, result.Warnings))
                        {
                            var rel = Path.GetRelativePath(resolvedPath, f).Replace('\\', '/');
                            filesToBackup.Add((f, $"{containerName}/{volFolderSafe}/{rel}"));
                        }
                    }
                    else
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
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Kon bestand '{src}' niet toevoegen: {ex.Message}");
                    }
                }

                // Pack remote / named Docker volumes via Docker Archive TAR stream
                for (int i = 0; i < dockerArchiveVolumes.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (containerName, containerPath, volFolderSafe) = dockerArchiveVolumes[i];
                    progress?.Report(($"Docker volume inpakken ({i + 1}/{dockerArchiveVolumes.Count}): {containerName}:{containerPath}", 100.0));

                    try
                    {
                        using var tarStream = await _dockerService!.GetContainerArchiveStreamAsync(containerName, containerPath, ct);
                        if (tarStream == null)
                        {
                            result.Warnings.Add($"Kon volume '{containerPath}' van container '{containerName}' niet uitlezen via Docker API (container of pad bestaat mogelijk niet).");
                            continue;
                        }

                        var topFolderName = Path.GetFileName(containerPath.TrimEnd('/', '\\'));
                        using var tarReader = new TarReader(tarStream, leaveOpen: false);
                        TarEntry? tarEntry;
                        while ((tarEntry = await tarReader.GetNextEntryAsync(copyData: false, ct)) != null)
                        {
                            ct.ThrowIfCancellationRequested();

                            if (tarEntry.EntryType != TarEntryType.RegularFile && tarEntry.EntryType != TarEntryType.V7RegularFile)
                            {
                                continue;
                            }

                            var rawName = (tarEntry.Name ?? string.Empty).Replace('\\', '/').Trim();
                            while (rawName.StartsWith("./", StringComparison.Ordinal)) rawName = rawName[2..];
                            rawName = rawName.TrimStart('/');

                            if (!string.IsNullOrEmpty(topFolderName) &&
                                rawName.StartsWith(topFolderName + "/", StringComparison.Ordinal))
                            {
                                rawName = rawName[(topFolderName.Length + 1)..];
                            }

                            if (!IsSafeArchiveRelativePath(rawName))
                            {
                                continue;
                            }

                            var zipEntryPath = $"{containerName}/{volFolderSafe}/{rawName}";
                            var zipEntry = archive.CreateEntry(zipEntryPath, CompressionLevel.Optimal);
                            long entryBytes = 0;
                            if (tarEntry.DataStream != null)
                            {
                                using var entryOut = zipEntry.Open();
                                entryBytes = tarEntry.Length;
                                await tarEntry.DataStream.CopyToAsync(entryOut, ct);
                            }
                            else
                            {
                                using var entryOut = zipEntry.Open();
                            }

                            result.TotalBytes += entryBytes;
                            result.FilesCount++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Fout bij Docker volume back-up '{containerName}:{containerPath}': {ex.Message}");
                    }
                }

                // Add profile.json (scrubbed of raw cloudflared tunnel tokens)
                try
                {
                    var profileEntry = archive.CreateEntry("profile.json", CompressionLevel.Optimal);
                    using (var entryStream = profileEntry.Open())
                    using (var writer = new StreamWriter(entryStream))
                    {
                        var json = GitHubProfileService.SerializeProfileForLocalSave(profile);
                        await writer.WriteAsync(json);
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Kon profile.json niet toevoegen aan back-up: {ex.Message}");
                }

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
        catch (OperationCanceledException)
        {
            throw;
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

                const int MaxEntries = 50_000;
                const long MaxSingleEntryBytes = 512L * 1024 * 1024;
                const long MaxTotalRestoreBytes = 2L * 1024 * 1024 * 1024;

                var fileEntries = archive.Entries
                    .Where(e => !string.IsNullOrWhiteSpace(e.Name) && e.FullName != "profile.json" && e.FullName != "backup_manifest.txt")
                    .ToList();

                if (fileEntries.Count > MaxEntries)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Archief bevat te veel bestanden ({fileEntries.Count}; maximum is {MaxEntries}).";
                    return result;
                }

                var isRemote = server.HostType.Equals("Tcp", StringComparison.OrdinalIgnoreCase);
                var invalidFileChars = Path.GetInvalidFileNameChars();
                var total = fileEntries.Count;
                var current = 0;
                long totalRestoredBytes = 0;

                var dockerRestoreBuckets = new Dictionary<(string ContainerName, string ExtractDir), (FileStream TarStream, TarWriter Writer, int FileCount, long ByteCount)>();

                try
                {
                    foreach (var entry in fileEntries)
                    {
                        ct.ThrowIfCancellationRequested();
                        current++;
                        var pct = total > 0 ? (double)current / total * 100.0 : 100.0;
                        progress?.Report(($"Herstellen ({current}/{total}): {entry.Name}", pct));

                        if (entry.FullName.Contains('\\') || entry.FullName.StartsWith('/'))
                        {
                            result.Warnings.Add($"Onveilig archiefpad overgeslagen: {entry.FullName}");
                            continue;
                        }

                        var parts = entry.FullName.Split('/');
                        if (parts.Length < 3) continue;

                        if (parts.Any(p => string.IsNullOrEmpty(p) || p == "." || p == ".."))
                        {
                            result.Warnings.Add($"Onveilig archiefpad overgeslagen: {entry.FullName}");
                            continue;
                        }

                        var containerName = parts[0];
                        var volFolder = parts[1];
                        var relativePath = string.Join('/', parts.Skip(2));

                        if (containerName.Contains(':') || containerName.IndexOfAny(invalidFileChars) >= 0 ||
                            volFolder.Contains(':') || volFolder.IndexOfAny(invalidFileChars) >= 0 ||
                            relativePath.StartsWith('/') || relativePath.StartsWith('\\') ||
                            relativePath.Contains(':') || Path.IsPathRooted(relativePath))
                        {
                            result.Warnings.Add($"Onveilig archiefpad overgeslagen: {entry.FullName}");
                            continue;
                        }

                        // Find matching service & volume
                        var service = activeProfile.Services.FirstOrDefault(s =>
                            s.GetEffectiveContainerName().Equals(containerName, StringComparison.OrdinalIgnoreCase) ||
                            s.Id.Equals(containerName, StringComparison.OrdinalIgnoreCase));

                        VolumeMapping? vol = null;
                        if (service != null)
                        {
                            vol = service.Volumes.FirstOrDefault(v =>
                                SanitizeEntryName(Path.GetFileName((!string.IsNullOrWhiteSpace(v.HostPath) ? v.HostPath : v.ContainerPath).TrimEnd('/', '\\'))) == volFolder ||
                                v.HostPath.Contains(volFolder));
                        }

                        if (service != null && vol != null && (isRemote || vol.IsNamedVolume))
                        {
                            if (_dockerService == null)
                            {
                                result.Warnings.Add($"Volume '{entry.FullName}' kan niet worden hersteld zonder Docker API-service.");
                                continue;
                            }

                            var containerTargetPath = string.IsNullOrWhiteSpace(vol.ContainerPath) ? "/" : vol.ContainerPath.Replace('\\', '/').Trim();
                            var lastSeg = Path.GetFileName(containerTargetPath.TrimEnd('/'));
                            string extractDir = containerTargetPath;
                            string tarEntryName = relativePath;

                            if (parts.Length == 3 &&
                                !string.IsNullOrWhiteSpace(lastSeg) &&
                                Path.HasExtension(lastSeg) &&
                                relativePath.Equals(lastSeg, StringComparison.OrdinalIgnoreCase))
                            {
                                var slash = containerTargetPath.LastIndexOf('/');
                                extractDir = slash > 0 ? containerTargetPath[..slash] : "/";
                                tarEntryName = lastSeg;
                            }

                            using var tempEntryData = new FileStream(
                                Path.GetTempFileName(),
                                FileMode.Create,
                                FileAccess.ReadWrite,
                                FileShare.None,
                                81920,
                                FileOptions.DeleteOnClose);

                            var (copyOk, copiedBytes, limitErr) = await CopyBoundedAsync(
                                entry,
                                tempEntryData,
                                MaxSingleEntryBytes,
                                MaxTotalRestoreBytes,
                                totalRestoredBytes,
                                ct);
                            totalRestoredBytes += copiedBytes;

                            if (!copyOk)
                            {
                                result.Success = false;
                                result.ErrorMessage = limitErr;
                                return result;
                            }

                            var key = (service.GetEffectiveContainerName(), extractDir);
                            if (!dockerRestoreBuckets.TryGetValue(key, out var bucket))
                            {
                                var tarFs = new FileStream(
                                    Path.GetTempFileName(),
                                    FileMode.Create,
                                    FileAccess.ReadWrite,
                                    FileShare.None,
                                    81920,
                                    FileOptions.DeleteOnClose);
                                var writer = new TarWriter(tarFs, TarEntryFormat.Pax, leaveOpen: true);
                                bucket = (tarFs, writer, 0, 0L);
                            }

                            tempEntryData.Position = 0;
                            var paxEntry = new PaxTarEntry(TarEntryType.RegularFile, tarEntryName)
                            {
                                DataStream = tempEntryData
                            };
                            await bucket.Writer.WriteEntryAsync(paxEntry, ct);
                            dockerRestoreBuckets[key] = (bucket.TarStream, bucket.Writer, bucket.FileCount + 1, bucket.ByteCount + copiedBytes);
                            continue;
                        }

                        bool allowRooted = targetProfile != null;
                        string targetFilePath;
                        string allowedRoot;
                        bool isSingleFileTarget = false;

                        if (service != null)
                        {
                            if (vol != null)
                            {
                                var resolvedHostPath = ResolveHostPath(vol.HostPath, activeProfile, server, baseDirs, allowRooted);
                                if (File.Exists(resolvedHostPath) || (parts.Length == 3 && Path.HasExtension(resolvedHostPath) && !Directory.Exists(resolvedHostPath)))
                                {
                                    targetFilePath = resolvedHostPath;
                                    allowedRoot = resolvedHostPath;
                                    isSingleFileTarget = true;
                                }
                                else
                                {
                                    allowedRoot = resolvedHostPath;
                                    targetFilePath = Path.Combine(resolvedHostPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                                }
                            }
                            else
                            {
                                var primaryDir = server.GetEffectiveVolumesRootPath();
                                var sub = activeProfile.GetEffectiveVolumesSubfolder();
                                allowedRoot = Path.Combine(primaryDir, sub, containerName);
                                targetFilePath = Path.Combine(allowedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                            }
                        }
                        else
                        {
                            var primaryDir = server.GetEffectiveVolumesRootPath();
                            var sub = activeProfile.GetEffectiveVolumesSubfolder();
                            allowedRoot = Path.Combine(primaryDir, sub, containerName);
                            targetFilePath = Path.Combine(allowedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                        }

                        var fullTarget = Path.GetFullPath(targetFilePath);
                        var fullAllowedRoot = Path.GetFullPath(allowedRoot);
                        var isAllowed = isSingleFileTarget
                            ? fullTarget.Equals(fullAllowedRoot, StringComparison.OrdinalIgnoreCase)
                            : fullTarget.StartsWith(EnsureTrailingSeparator(fullAllowedRoot), StringComparison.OrdinalIgnoreCase);

                        if (!isAllowed)
                        {
                            result.Warnings.Add($"Onveilig archiefpad overgeslagen: {entry.FullName}");
                            continue;
                        }

                        if (File.Exists(fullTarget) && !overwriteExisting)
                        {
                            result.Warnings.Add($"Overgeslagen (bestaat al): {fullTarget}");
                            continue;
                        }

                        var parentDir = Path.GetDirectoryName(fullTarget);
                        if (!string.IsNullOrWhiteSpace(parentDir) && !Directory.Exists(parentDir))
                        {
                            Directory.CreateDirectory(parentDir);
                        }

                        long entryCopiedBytes = 0;
                        try
                        {
                            bool limitOk;
                            string? limitError;
                            using (var outStream = new FileStream(fullTarget, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                            {
                                (limitOk, entryCopiedBytes, limitError) = await CopyBoundedAsync(
                                    entry,
                                    outStream,
                                    MaxSingleEntryBytes,
                                    MaxTotalRestoreBytes,
                                    totalRestoredBytes,
                                    ct);
                                totalRestoredBytes += entryCopiedBytes;
                            }

                            if (!limitOk)
                            {
                                try { if (File.Exists(fullTarget)) File.Delete(fullTarget); } catch { }
                                result.Success = false;
                                result.ErrorMessage = limitError;
                                return result;
                            }

                            result.RestoredBytes += entryCopiedBytes;
                            result.RestoredFilesCount++;
                        }
                        catch (OperationCanceledException)
                        {
                            try { if (File.Exists(fullTarget)) File.Delete(fullTarget); } catch { }
                            throw;
                        }
                        catch (Exception ex)
                        {
                            result.Warnings.Add($"Kon '{fullTarget}' niet herstellen: {ex.Message}");
                        }
                    }

                    // Upload accumulated TAR archives into remote / named Docker volumes
                    foreach (var ((cName, extractDir), bucket) in dockerRestoreBuckets)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            await bucket.Writer.DisposeAsync();
                            bucket.TarStream.Position = 0;

                            var ok = await _dockerService!.ExtractArchiveToContainerAsync(cName, extractDir, bucket.TarStream, ct);
                            if (ok)
                            {
                                result.RestoredFilesCount += bucket.FileCount;
                                result.RestoredBytes += bucket.ByteCount;
                            }
                            else
                            {
                                result.Warnings.Add($"Kon volume niet herstellen in container '{cName}' op pad '{extractDir}'. Controleer of de container bestaat.");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            result.Warnings.Add($"Fout bij terugzetten van Docker volume '{cName}:{extractDir}': {ex.Message}");
                        }
                    }
                }
                finally
                {
                    foreach (var bucket in dockerRestoreBuckets.Values)
                    {
                        try { await bucket.Writer.DisposeAsync(); } catch { }
                        try { bucket.TarStream.Dispose(); } catch { }
                    }
                }
            }

            result.Success = true;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
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

    private static string ResolveHostPath(string hostPath, ProfileModel profile, DockerServerEnvironment server, List<string> baseDirs, bool allowRootedHostPath = true)
    {
        if (allowRootedHostPath && Path.IsPathRooted(hostPath) && (File.Exists(hostPath) || Directory.Exists(hostPath)))
        {
            return hostPath;
        }

        var clean = DockerService.StripRelativePrefix(hostPath).Replace('/', Path.DirectorySeparatorChar);
        if (clean.StartsWith("volumes\\", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("volumes/", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[8..];
        }

        if (Path.IsPathRooted(clean) || clean.Contains(':'))
        {
            clean = SanitizeEntryName(Path.GetFileName(clean.TrimEnd('/', '\\')));
            if (string.IsNullOrWhiteSpace(clean)) clean = "vol";
        }
        else
        {
            var safeSegments = clean.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(s => s != "." && s != "..");
            clean = string.Join(Path.DirectorySeparatorChar, safeSegments);
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

    private static async Task<(bool Ok, long CopiedBytes, string? Error)> CopyBoundedAsync(
        ZipArchiveEntry entry,
        Stream outStream,
        long maxSingleEntryBytes,
        long maxTotalRestoreBytes,
        long totalRestoredSoFar,
        CancellationToken ct)
    {
        long entryCopied = 0;
        using var entryStream = entry.Open();
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await entryStream.ReadAsync(buffer, ct)) > 0)
        {
            entryCopied += bytesRead;
            if (entryCopied > maxSingleEntryBytes)
            {
                return (false, entryCopied, $"Archiefonderdeel '{entry.FullName}' overschrijdt de maximale bestandsgrootte ({maxSingleEntryBytes / 1024 / 1024} MB).");
            }

            if (totalRestoredSoFar + entryCopied > maxTotalRestoreBytes)
            {
                return (false, entryCopied, $"Totale herstelomvang overschrijdt het maximum ({maxTotalRestoreBytes / 1024 / 1024} MB).");
            }

            await outStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
        }

        return (true, entryCopied, null);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateSafeLocalFiles(string rootDir, List<string> warnings)
    {
        var stack = new Stack<string>();
        stack.Push(rootDir);

        while (stack.Count > 0)
        {
            var currentDir = stack.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(currentDir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                warnings.Add($"Map '{currentDir}' kon niet volledig worden gelezen: {ex.Message}");
                continue;
            }

            foreach (var f in files)
            {
                if (IsReparsePoint(f))
                {
                    warnings.Add($"Symbolische link overgeslagen: {f}");
                    continue;
                }
                yield return f;
            }

            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(currentDir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                warnings.Add($"Submappen van '{currentDir}' konden niet worden gelezen: {ex.Message}");
                continue;
            }

            foreach (var d in dirs)
            {
                if (IsReparsePoint(d))
                {
                    warnings.Add($"Symbolische link of junction overgeslagen: {d}");
                    continue;
                }
                stack.Push(d);
            }
        }
    }

    private static bool IsSafeArchiveRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':'))
            return false;

        var invalid = Path.GetInvalidFileNameChars();
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(seg => seg != "." && seg != ".." && seg.IndexOfAny(invalid) < 0);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static string SanitizeEntryName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
