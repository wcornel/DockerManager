using System.IO;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerManager.App.Models;

namespace DockerManager.App.Services;

public partial class DockerService
{
    private async Task<bool> PullImageAsync(DockerClient client, ServiceDefinition service, IProgress<string>? progress, ProfileRegistryConfig? registryConfig = null, CancellationToken ct = default)
    {
        var image = service.Image;
        var (imageName, tag) = ParseImageNameAndTag(image);
        bool hadNewerLayers = false;

        var progressHandler = new Progress<JSONMessage>(msg =>
        {
            if (!string.IsNullOrWhiteSpace(msg.Status))
            {
                var text = $"{msg.Status} {msg.ProgressMessage ?? string.Empty}".Trim();
                progress?.Report(text);

                if (msg.Status.Contains("Downloaded newer image", StringComparison.OrdinalIgnoreCase) ||
                    msg.Status.Contains("Pull complete", StringComparison.OrdinalIgnoreCase) ||
                    msg.Status.Contains("Extracting", StringComparison.OrdinalIgnoreCase) ||
                    msg.Status.Contains("Downloading", StringComparison.OrdinalIgnoreCase))
                {
                    hadNewerLayers = true;
                }
            }
        });

        var cred = _credentialService.GetCredentialForImage(imageName);

        // Fallback to profile-level registry configuration if not found in credential store
        if ((cred == null || string.IsNullOrWhiteSpace(cred.Password)) && registryConfig != null && !string.IsNullOrWhiteSpace(registryConfig.Password))
        {
            var server = string.IsNullOrWhiteSpace(registryConfig.Server) ? "ghcr.io" : registryConfig.Server.Trim();
            if (imageName.StartsWith(server, StringComparison.OrdinalIgnoreCase) ||
                (server.Contains("ghcr.io", StringComparison.OrdinalIgnoreCase) && imageName.Contains("ghcr.io", StringComparison.OrdinalIgnoreCase)))
            {
                cred = new RegistryCredential
                {
                    ServerAddress = server,
                    Username = !string.IsNullOrWhiteSpace(registryConfig.Username) 
                        ? registryConfig.Username 
                        : (!string.IsNullOrWhiteSpace(registryConfig.Namespace) ? registryConfig.Namespace : _settingsService.Settings.GitHubRepoOwner),
                    Password = registryConfig.Password,
                    DisplayName = server
                };
            }
        }

        // If credentials exist or service explicitly requires auth
        if (cred != null && !string.IsNullOrWhiteSpace(cred.Password))
        {
            var user = !string.IsNullOrWhiteSpace(cred.Username) ? cred.Username : _settingsService.Settings.GitHubRepoOwner;
            progress?.Report($"Authenticeren bij {cred.ServerAddress} als '{user}'...");
            var authConfig = new AuthConfig
            {
                ServerAddress = cred.ServerAddress,
                Username = user,
                Password = cred.Password
            };

            try
            {
                await client.Images.CreateImageAsync(new ImagesCreateParameters
                {
                    FromImage = imageName,
                    Tag = tag
                }, authConfig, progressHandler, ct);
                return hadNewerLayers;
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized || ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                progress?.Report($"❌ Toegang geweigerd tot {cred.ServerAddress} (401/403).");
                throw new InvalidOperationException($"Registry '{cred.ServerAddress}' weigert toegang tot '{image}'. Controleer de gebruikersnaam en token/wachtwoord in Instellingen ➔ Container Registries.");
            }
        }

        // Anonymous pull fallback
        try
        {
            await client.Images.CreateImageAsync(new ImagesCreateParameters
            {
                FromImage = imageName,
                Tag = tag
            }, new AuthConfig(), progressHandler, ct);
            return hadNewerLayers;
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized || ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException($"Image '{image}' vereist authenticatie (401 Unauthorized). Configureer inloggegevens voor deze registry via Instellingen ➔ Container Registries.");
        }
    }

    private static (string ImageName, string Tag) ParseImageNameAndTag(string fullImage)
    {
        var lastSlash = fullImage.LastIndexOf('/');
        var lastColon = fullImage.LastIndexOf(':');
        if (lastColon > lastSlash && lastColon != -1)
        {
            return (fullImage[..lastColon], fullImage[(lastColon + 1)..]);
        }
        return (fullImage, "latest");
    }


    public async Task<bool> CheckImageUpdateAvailableAsync(ServiceDefinition service, ProfileRegistryConfig? registryConfig = null, CancellationToken ct = default)
    {
        try
        {
            var client = GetClient();
            var containerName = service.GetEffectiveContainerName();

            // 1. Check if the running container is using an older image ID than what is locally present
            var containerInspect = await client.Containers.InspectContainerAsync(containerName, ct);
            var runningImageId = containerInspect?.Image;

            var localImageInspect = await client.Images.InspectImageAsync(service.Image, ct);
            var localImageId = localImageInspect?.ID;

            if (!string.IsNullOrWhiteSpace(runningImageId) && !string.IsNullOrWhiteSpace(localImageId) &&
                !runningImageId.Equals(localImageId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 2. Query distribution inspect / manifest if supported
            try
            {
                var (imageName, tag) = ParseImageNameAndTag(service.Image);
                var cred = _credentialService.GetCredentialForImage(imageName);
                var authConfig = cred != null && !string.IsNullOrWhiteSpace(cred.Password)
                    ? new AuthConfig { ServerAddress = cred.ServerAddress, Username = cred.Username, Password = cred.Password }
                    : null;

                // Inspect image repo digest vs container image repo digest
                if (localImageInspect?.RepoDigests != null && localImageInspect.RepoDigests.Count > 0)
                {
                    // If repo digests are present, checking if a pull dry-run reports newer layers
                    // Note: We can check if a newer digest exists without interrupting the container
                }
            }
            catch { }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(bool Success, string Message)> TestRegistryConnectionAsync(RegistryCredential cred, CancellationToken ct = default)
    {
        try
        {
            var client = GetClient();
            var authConfig = new AuthConfig
            {
                ServerAddress = cred.ServerAddress,
                Username = cred.Username,
                Password = cred.Password
            };
            await client.System.AuthenticateAsync(authConfig, ct);
            return (true, "Login geslaagd (OK)");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<ImageInspectResult> InspectAndPullImageConfigAsync(string image, bool requiresAuth = false, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return new ImageInspectResult { Success = false, ErrorMessage = "Geen Docker image opgegeven." };
        }

        var result = new ImageInspectResult();
        var client = GetClient();

        try
        {
            progress?.Report($"Image '{image}' ophalen / downloaden...");
            var service = new ServiceDefinition { Image = image.Trim(), RequiresAuth = requiresAuth };
            await PullImageAsync(client, service, progress, null, ct);

            progress?.Report($"Image inspecteren...");
            var inspect = await client.Images.InspectImageAsync(image.Trim(), ct);

            // Extract exposed ports
            if (inspect.Config?.ExposedPorts != null)
            {
                foreach (var ep in inspect.Config.ExposedPorts.Keys)
                {
                    var portStr = ep.Split('/')[0];
                    if (int.TryParse(portStr, out var p) && p > 0 && !result.ExposedPorts.Contains(p))
                    {
                        result.ExposedPorts.Add(p);
                    }
                }
            }

            // Extract environment variables
            if (inspect.Config?.Env != null)
            {
                foreach (var envStr in inspect.Config.Env)
                {
                    var idx = envStr.IndexOf('=');
                    if (idx > 0)
                    {
                        var key = envStr[..idx];
                        var val = envStr[(idx + 1)..];
                        if (!key.Equals("PATH", StringComparison.OrdinalIgnoreCase) &&
                            !key.Equals("HOSTNAME", StringComparison.OrdinalIgnoreCase) &&
                            !key.Equals("DOTNET_RUNNING_IN_CONTAINER", StringComparison.OrdinalIgnoreCase))
                        {
                            result.EnvironmentVariables[key] = val;
                        }
                    }
                }
            }

            // Extract working dir and command
            result.WorkingDir = inspect.Config?.WorkingDir;
            if (inspect.Config?.Cmd != null && inspect.Config.Cmd.Count > 0)
            {
                result.Command = Helpers.CommandLineHelper.FormatCommandLine(inspect.Config.Cmd);
            }

            // Suggested names
            var cleanName = image.Trim();
            if (cleanName.Contains('/')) cleanName = cleanName[(cleanName.LastIndexOf('/') + 1)..];
            if (cleanName.Contains(':')) cleanName = cleanName[..cleanName.IndexOf(':')];
            result.SuggestedContainerName = cleanName.ToLowerInvariant().Replace(" ", "-");
            result.SuggestedDisplayName = char.ToUpperInvariant(cleanName[0]) + cleanName[1..];

            result.Success = true;
            progress?.Report($"✅ Image '{image}' succesvol gedownload en geconfigureerd!");
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            progress?.Report($"❌ Fout: {ex.Message}");
            return result;
        }
    }
}
