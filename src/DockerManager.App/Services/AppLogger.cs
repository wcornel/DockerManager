using System.Diagnostics;
using System.IO;

namespace DockerManager.App.Services;

/// <summary>
/// Lightweight, thread-safe file and debug logger writing to %AppData%\DockerManager\logs\app.log.
/// </summary>
public static class AppLogger
{
    private static readonly object SyncLock = new();
    private const long MaxLogFileBytes = 2 * 1024 * 1024; // 2 MB

    private static string LogFilePath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "DockerManager", "logs", "app.log");
        }
    }

    public static void LogInfo(string context, string message) => Write("INFO", context, message, null);

    public static void LogWarning(string context, string message, Exception? ex = null) => Write("WARN", context, message, ex);

    public static void LogError(string context, Exception ex, string? message = null) =>
        Write("ERROR", context, message ?? ex.Message, ex);

    private static void Write(string level, string context, string message, Exception? ex)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{context}] {message}";
        if (ex != null)
        {
            line += $"{Environment.NewLine}{ex}";
        }

        Debug.WriteLine(line);

        try
        {
            lock (SyncLock)
            {
                var path = LogFilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(path) && new FileInfo(path).Length > MaxLogFileBytes)
                {
                    var archivePath = path + ".1";
                    File.Move(path, archivePath, overwrite: true);
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never crash the application
        }
    }
}
