using System;
using System.IO;
using DockerManager.App.Services;

namespace DockerManager.Tests;

internal sealed class TestEnvironmentScope : IDisposable
{
    public string TempDir { get; }
    public string ProfilesDir { get; }
    public string SettingsPath { get; }
    public CredentialService Credentials { get; }
    public SettingsService Settings { get; }

    public TestEnvironmentScope()
    {
        TempDir = Path.Combine(Path.GetTempPath(), $"DockerManagerTests_{Guid.NewGuid():N}");
        ProfilesDir = Path.Combine(TempDir, "profiles");
        SettingsPath = Path.Combine(TempDir, "settings.json");

        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(ProfilesDir);

        Credentials = new CredentialService(TempDir);
        Settings = new SettingsService(Credentials, SettingsPath);
        Settings.Settings.LocalProfilesFolder = ProfilesDir;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(TempDir))
            {
                Directory.Delete(TempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors on temp files
        }
    }
}
