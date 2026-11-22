using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerManager.App.Models;

namespace DockerManager.App.Services;

public partial class DockerService
{
    private static readonly Regex WwwAuthParamRegex = new(@"(\w+)\s*=\s*(?:""([^""]*)""|([^,\s]+))", RegexOptions.Compiled);

    private async Task<bool> PullImageAsync(DockerClient client, ServiceDefinition service, IProgress<string>? progress, ProfileRegistryConfig? registryConfig = null, CancellationToken ct = default)
    {
        var image = service.Image;
        var (imageName, tag) = ParseImageNameAndTag(image);
        bool hadNewerLayers = false;
        string? streamError = null;

        var progressHandler = new SyncProgress<JSONMessage>(msg =>
        {
            var errMsg = !string.IsNullOrWhiteSpace(msg.ErrorMessage) ? msg.ErrorMessage : msg.Error?.Message;
            if (!string.IsNullOrWhiteSpace(errMsg))
            {
                streamError ??= errMsg;
                progress?.Report($"❌ {errMsg}");
            }

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

        var cred = ResolveRegistryCredential(imageName, registryConfig);
        var createParams = new ImagesCreateParameters
        {
            FromImage = imageName,
            Tag = string.IsNullOrEmpty(tag) ? null : tag
        };

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
                await client.Images.CreateImageAsync(createParams, authConfig, progressHandler, ct);
                if (!string.IsNullOrWhiteSpace(streamError))
                {
                    throw new InvalidOperationException($"Fout bij downloaden van '{image}': {streamError}");
                }
                return hadNewerLayers;
            }
            catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
            {
                progress?.Report($"❌ Toegang geweigerd tot {cred.ServerAddress} (401/403).");
                throw new InvalidOperationException($"Registry '{cred.ServerAddress}' weigert toegang tot '{image}'. Controleer de gebruikersnaam en token/wachtwoord in Instellingen ➔ Container Registries.");
            }
        }

        // Anonymous pull fallback
        try
        {
            await client.Images.CreateImageAsync(createParams, new AuthConfig(), progressHandler, ct);
            if (!string.IsNullOrWhiteSpace(streamError))
            {
                throw new InvalidOperationException($"Fout bij downloaden van '{image}': {streamError}");
            }
            return hadNewerLayers;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException($"Image '{image}' vereist authenticatie (401 Unauthorized). Configureer inloggegevens voor deze registry via Instellingen ➔ Container Registries.");
        }
    }

    private RegistryCredential? ResolveRegistryCredential(string imageName, ProfileRegistryConfig? registryConfig)
    {
        var cred = _credentialService.GetCredentialForImage(imageName);

        if ((cred == null || string.IsNullOrWhiteSpace(cred.Password)) && registryConfig != null && !string.IsNullOrWhiteSpace(registryConfig.Password))
        {
            var rawServer = string.IsNullOrWhiteSpace(registryConfig.Server) ? "ghcr.io" : registryConfig.Server.Trim();
            var normalizedConfigServer = CredentialService.NormalizeServerAddress(rawServer);
            var targetRegistryServer = CredentialService.ExtractServerAddress(imageName);
            if (targetRegistryServer.Equals(normalizedConfigServer, StringComparison.OrdinalIgnoreCase))
            {
                cred = new RegistryCredential
                {
                    ServerAddress = normalizedConfigServer,
                    Username = !string.IsNullOrWhiteSpace(registryConfig.Username)
                        ? registryConfig.Username
                        : (!string.IsNullOrWhiteSpace(registryConfig.Namespace) ? registryConfig.Namespace : _settingsService.Settings.GitHubRepoOwner),
                    Password = registryConfig.Password,
                    DisplayName = normalizedConfigServer
                };
            }
        }

        return cred;
    }

    internal static (string ImageName, string Tag) ParseImageNameAndTag(string fullImage)
    {
        if (string.IsNullOrWhiteSpace(fullImage))
        {
            return (string.Empty, "latest");
        }

        var trimmed = fullImage.Trim();
        if (trimmed.Contains('@'))
        {
            return (trimmed, string.Empty);
        }

        var lastSlash = trimmed.LastIndexOf('/');
        var lastColon = trimmed.LastIndexOf(':');
        if (lastColon > lastSlash && lastColon != -1)
        {
            return (trimmed[..lastColon], trimmed[(lastColon + 1)..]);
        }
        return (trimmed, "latest");
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

            // Digest-pinned images are immutable
            if (string.IsNullOrWhiteSpace(service.Image) || service.Image.Contains('@'))
            {
                return false;
            }

            // 2. Compare local repo digests against remote registry manifest digest
            var localDigests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (localImageInspect?.RepoDigests != null)
            {
                foreach (var repoDigest in localImageInspect.RepoDigests)
                {
                    var atIdx = repoDigest.IndexOf('@');
                    if (atIdx >= 0 && atIdx + 1 < repoDigest.Length)
                    {
                        localDigests.Add(repoDigest[(atIdx + 1)..].Trim());
                    }
                }
            }

            if (localDigests.Count > 0)
            {
                var remoteDigest = await FetchRemoteImageDigestAsync(service.Image, registryConfig, ct);
                if (!string.IsNullOrWhiteSpace(remoteDigest) && !localDigests.Contains(remoteDigest))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    internal async Task<string?> FetchRemoteImageDigestAsync(string fullImage, ProfileRegistryConfig? registryConfig = null, CancellationToken ct = default)
    {
        try
        {
            var (imageName, tag) = ParseImageNameAndTag(fullImage);
            if (string.IsNullOrWhiteSpace(imageName) || string.IsNullOrEmpty(tag))
            {
                return null;
            }

            string host;
            string repo;
            var slashIdx = imageName.IndexOf('/');
            if (slashIdx < 0)
            {
                host = "docker.io";
                repo = $"library/{imageName}";
            }
            else
            {
                var firstSeg = imageName[..slashIdx];
                if (firstSeg.Contains('.') || firstSeg.Contains(':') || firstSeg.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    host = firstSeg;
                    repo = imageName[(slashIdx + 1)..];
                }
                else
                {
                    host = "docker.io";
                    repo = imageName;
                }
            }

            if (string.IsNullOrWhiteSpace(repo))
            {
                return null;
            }

            var apiHost = host.Equals("docker.io", StringComparison.OrdinalIgnoreCase) ||
                          host.Equals("index.docker.io", StringComparison.OrdinalIgnoreCase) ||
                          host.Equals("registry-1.docker.io", StringComparison.OrdinalIgnoreCase)
                ? "registry-1.docker.io"
                : host;

            var escapedRepo = string.Join('/', repo.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
            var manifestUrl = $"https://{apiHost}/v2/{escapedRepo}/manifests/{Uri.EscapeDataString(tag)}";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            async Task<HttpResponseMessage> SendHeadAsync(AuthenticationHeaderValue? auth)
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, manifestUrl);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.index.v1+json"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.docker.distribution.manifest.list.v2+json"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.manifest.v1+json"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.docker.distribution.manifest.v2+json"));
                if (auth != null)
                {
                    req.Headers.Authorization = auth;
                }
                return await _registryHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            }

            using var initialResp = await SendHeadAsync(null);
            if (initialResp.IsSuccessStatusCode)
            {
                return ExtractContentDigest(initialResp);
            }

            if (initialResp.StatusCode == HttpStatusCode.Unauthorized)
            {
                var cred = ResolveRegistryCredential(imageName, registryConfig);
                var bearerHeader = initialResp.Headers.WwwAuthenticate
                    .FirstOrDefault(h => string.Equals(h.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase));

                if (bearerHeader != null)
                {
                    var (realm, serviceName, scope) = ParseWwwAuthenticateBearer(bearerHeader.ToString());
                    if (Uri.TryCreate(realm, UriKind.Absolute, out var realmUri) &&
                        realmUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        var queryParts = new List<string>();
                        if (!string.IsNullOrWhiteSpace(serviceName))
                        {
                            queryParts.Add($"service={Uri.EscapeDataString(serviceName)}");
                        }
                        var effectiveScope = !string.IsNullOrWhiteSpace(scope) ? scope : $"repository:{repo}:pull";
                        queryParts.Add($"scope={Uri.EscapeDataString(effectiveScope)}");

                        var separator = realm!.Contains('?') ? "&" : "?";
                        var tokenUrl = $"{realm}{separator}{string.Join("&", queryParts)}";

                        using var tokenReq = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
                        if (cred != null && !string.IsNullOrWhiteSpace(cred.Password))
                        {
                            var user = !string.IsNullOrWhiteSpace(cred.Username) ? cred.Username : _settingsService.Settings.GitHubRepoOwner;
                            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{cred.Password}"));
                            tokenReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                        }

                        using var tokenResp = await _registryHttpClient.SendAsync(tokenReq, cts.Token);
                        if (!tokenResp.IsSuccessStatusCode)
                        {
                            return null;
                        }

                        var tokenJson = await tokenResp.Content.ReadAsStringAsync(cts.Token);
                        using var doc = JsonDocument.Parse(tokenJson);
                        string? token = null;
                        if (doc.RootElement.TryGetProperty("token", out var tokProp))
                        {
                            token = tokProp.GetString();
                        }
                        else if (doc.RootElement.TryGetProperty("access_token", out var accProp))
                        {
                            token = accProp.GetString();
                        }

                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            using var authResp = await SendHeadAsync(new AuthenticationHeaderValue("Bearer", token));
                            if (authResp.IsSuccessStatusCode)
                            {
                                return ExtractContentDigest(authResp);
                            }
                        }
                    }
                }
                else if (cred != null && !string.IsNullOrWhiteSpace(cred.Password))
                {
                    var user = !string.IsNullOrWhiteSpace(cred.Username) ? cred.Username : _settingsService.Settings.GitHubRepoOwner;
                    var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{cred.Password}"));
                    using var basicResp = await SendHeadAsync(new AuthenticationHeaderValue("Basic", basic));
                    if (basicResp.IsSuccessStatusCode)
                    {
                        return ExtractContentDigest(basicResp);
                    }
                }
            }
        }
        catch
        {
            // Ignore transient network errors during background update check
        }

        return null;
    }

    internal static (string? Realm, string? Service, string? Scope) ParseWwwAuthenticateBearer(string header)
    {
        string? realm = null;
        string? service = null;
        string? scope = null;

        if (string.IsNullOrWhiteSpace(header))
        {
            return (null, null, null);
        }

        foreach (Match m in WwwAuthParamRegex.Matches(header))
        {
            var key = m.Groups[1].Value;
            var val = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (key.Equals("realm", StringComparison.OrdinalIgnoreCase)) realm = val;
            else if (key.Equals("service", StringComparison.OrdinalIgnoreCase)) service = val;
            else if (key.Equals("scope", StringComparison.OrdinalIgnoreCase)) scope = val;
        }

        return (realm, service, scope);
    }

    private static string? ExtractContentDigest(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Docker-Content-Digest", out var values))
        {
            return values.FirstOrDefault()?.Trim();
        }
        return null;
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
