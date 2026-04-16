using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Portal.Common;
using Portal.Common.Models;

namespace Portal.Host.Services;

public enum AppUpdateAvailabilityStatus
{
    NoSourceConfigured,
    NoUpdate,
    UpdateAvailable
}

public enum AppUpdateStage
{
    Idle,
    Checking,
    Downloading,
    PreparingFiles,
    LaunchingInstaller,
    Completed,
    Failed
}

public sealed class AppUpdateCheckResult
{
    public AppUpdateAvailabilityStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public AppUpdateManifest? Manifest { get; init; }
}

public sealed class AppUpdateProgressSnapshot
{
    public AppUpdateStage Stage { get; init; }
    public string StageText { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
    public double? BytesPerSecond { get; init; }
}

public enum AppUpdateSilentCheckStatus
{
    Completed,
    Failed
}

public sealed class AppUpdateSilentCheckResult
{
    public AppUpdateSilentCheckStatus Status { get; init; }
    public AppUpdateCheckResult? CheckResult { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class UpdateService
{
    private const string GitHubRepoOwner = "KoksMen";
    private const string GitHubRepoName = "Portal-Windows";
    private const string PreferredReleaseAssetName = "";
    private const string DefaultProviderDllRelativePath = "CredentialProvider\\Portal.CredentialProvider.comhost.dll";
    private const int StagePauseMs = 260;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;

    public UpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Portal-Windows", GetSafeVersionText()));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public string UpdatesRootDirectory => Path.Combine(PortalWinConfig.ConfigDir, "updates");
    public string LastUpdateResultPath => Path.Combine(UpdatesRootDirectory, "last-update-result.json");
    public static string BuiltInRepository => $"{GitHubRepoOwner}/{GitHubRepoName}";
    public static string BuiltInSourceLabel => $"GitHub Releases - {BuiltInRepository}";

    public Version GetCurrentVersion()
    {
        return typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
    }

    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync(PortalWinConfig? config = null, CancellationToken cancellationToken = default)
    {
        var repository = NormalizeRepository(BuiltInRepository);
        var sourceLabel = $"GitHub Releases - {repository}";
        var accessToken = config?.GetUpdateAccessToken();
        var manifest = await LoadGitHubReleaseManifestAsync(repository, PreferredReleaseAssetName, accessToken, cancellationToken);
        return BuildCheckResult(manifest, sourceLabel);
    }

    public async Task<AppUpdateSilentCheckResult> TryRunScheduledCheckAsync(
        PortalWinConfig config,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;

        try
        {
            var checkResult = await CheckForUpdatesAsync(config, cancellationToken);
            config.LastUpdateCheckUtc = nowUtc;
            config.LastDiscoveredUpdateVersion = checkResult.Status == AppUpdateAvailabilityStatus.UpdateAvailable
                ? checkResult.Manifest?.Version?.Trim()
                : null;
            config.Save();

            return new AppUpdateSilentCheckResult
            {
                Status = AppUpdateSilentCheckStatus.Completed,
                CheckResult = checkResult
            };
        }
        catch (Exception ex)
        {
            Logger.LogError("[UpdateService] Scheduled update check failed.", ex);

            return new AppUpdateSilentCheckResult
            {
                Status = AppUpdateSilentCheckStatus.Failed,
                ErrorMessage = ex.Message
            };
        }
    }

    public AppUpdateResult? TryReadLastUpdateResult()
    {
        try
        {
            if (!File.Exists(LastUpdateResultPath))
            {
                return null;
            }

            var json = File.ReadAllText(LastUpdateResultPath);
            return JsonSerializer.Deserialize<AppUpdateResult>(json);
        }
        catch (Exception ex)
        {
            Logger.LogError("[UpdateService] Failed to read last update result.", ex);
            return null;
        }
    }

    public void ClearLastUpdateResult()
    {
        try
        {
            if (File.Exists(LastUpdateResultPath))
            {
                File.Delete(LastUpdateResultPath);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[UpdateService] Failed to clear last update result.", ex);
        }
    }

    public async Task PrepareAndLaunchUpdateAsync(
        AppUpdateManifest manifest,
        PortalWinConfig? config = null,
        IProgress<AppUpdateProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(UpdatesRootDirectory);

        var accessToken = config?.GetUpdateAccessToken();
        var packageUriValue = !string.IsNullOrWhiteSpace(accessToken) && !string.IsNullOrWhiteSpace(manifest.PackageApiUri)
            ? manifest.PackageApiUri!
            : manifest.PackageUri;
        var packageUri = ResolvePackageUri(packageUriValue);
        var packageFileName = string.IsNullOrWhiteSpace(manifest.PackageFileName)
            ? Path.GetFileName(packageUri.IsFile ? packageUri.LocalPath : packageUri.AbsolutePath)
            : manifest.PackageFileName.Trim();

        var sessionDirectory = Path.Combine(UpdatesRootDirectory, Guid.NewGuid().ToString("N"));
        var packageDirectory = Path.Combine(sessionDirectory, "package");
        var stagingDirectory = Path.Combine(sessionDirectory, "staging");
        var backupDirectory = Path.Combine(sessionDirectory, "backup");
        var packagePath = Path.Combine(packageDirectory, packageFileName);

        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(stagingDirectory);
        Directory.CreateDirectory(backupDirectory);

        await ReportStageAsync(
            progress,
            AppUpdateStage.Checking,
            "[1/7] Initializing update",
            "Preparing update workspace...",
            packageFileName,
            StagePauseMs,
            cancellationToken);

        await ReportStageAsync(
            progress,
            AppUpdateStage.Downloading,
            "[2/7] Downloading package",
            $"Starting package download from {BuiltInSourceLabel}...",
            packageFileName,
            0,
            cancellationToken);

        await DownloadPackageAsync(packageUri, packagePath, packageFileName, accessToken, progress, cancellationToken);
        await ReportStageAsync(
            progress,
            AppUpdateStage.PreparingFiles,
            "[3/7] Validating package",
            "Checking archive size and checksum...",
            packageFileName,
            StagePauseMs,
            cancellationToken);
        await ValidatePackageAsync(packagePath, manifest, cancellationToken);

        await ReportStageAsync(
            progress,
            AppUpdateStage.PreparingFiles,
            "[4/7] Extracting package",
            "Extracting package into staging area...",
            packageFileName,
            StagePauseMs,
            cancellationToken);

        ZipFile.ExtractToDirectory(packagePath, stagingDirectory, overwriteFiles: true);
        await ReportStageAsync(
            progress,
            AppUpdateStage.PreparingFiles,
            "[5/7] Analyzing payload",
            "Detecting post-install actions...",
            packageFileName,
            StagePauseMs,
            cancellationToken);

        var providerDllRelativePath = string.IsNullOrWhiteSpace(manifest.ProviderDllRelativePath)
            ? DefaultProviderDllRelativePath
            : manifest.ProviderDllRelativePath.Trim();
        var extractedRoot = ResolveExtractedRoot(stagingDirectory);
        var requiresProviderReinstall = DetectProviderPayload(extractedRoot, providerDllRelativePath);
        manifest.RequiresProviderReinstall = requiresProviderReinstall;
        manifest.ProviderDllRelativePath = providerDllRelativePath;

        await ReportStageAsync(
            progress,
            AppUpdateStage.PreparingFiles,
            "[6/7] Creating backup",
            "Creating backup of current Host files...",
            packageFileName,
            StagePauseMs,
            cancellationToken);

        CopyDirectoryContents(AppDomain.CurrentDomain.BaseDirectory, backupDirectory);
        await ReportStageAsync(
            progress,
            AppUpdateStage.PreparingFiles,
            "[6/7] Creating backup",
            "Backup complete. Writing update session...",
            packageFileName,
            StagePauseMs,
            cancellationToken);

        var resultFilePath = LastUpdateResultPath;
        var session = new AppUpdateSession
        {
            TargetVersion = manifest.Version.Trim(),
            HostProcessId = Environment.ProcessId,
            HostExecutablePath = Environment.ProcessPath ?? throw new InvalidOperationException("Unable to resolve Host executable path."),
            ApplicationDirectory = AppDomain.CurrentDomain.BaseDirectory,
            StagingDirectory = stagingDirectory,
            BackupDirectory = backupDirectory,
            RequiresProviderReinstall = requiresProviderReinstall,
            ProviderDllRelativePath = providerDllRelativePath,
            ResultFilePath = resultFilePath
        };

        var sessionPath = Path.Combine(sessionDirectory, "update-session.json");
        await File.WriteAllTextAsync(sessionPath, JsonSerializer.Serialize(session, JsonOptions), cancellationToken);

        await ReportStageAsync(
            progress,
            AppUpdateStage.LaunchingInstaller,
            "[7/7] Switching to installer",
            requiresProviderReinstall
                ? "Host prepared package and backup. Handing off to Updater (Provider reinstall enabled)..."
                : "Host prepared package and backup. Handing off to Updater...",
            packageFileName,
            StagePauseMs,
            cancellationToken);

        var updaterExecutable = CopyUpdaterToTemp();
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterExecutable,
            Arguments = $"\"{sessionPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(updaterExecutable) ?? AppDomain.CurrentDomain.BaseDirectory
        };

        Process.Start(startInfo);
    }

    private static async Task ReportStageAsync(
        IProgress<AppUpdateProgressSnapshot>? progress,
        AppUpdateStage stage,
        string stageText,
        string statusText,
        string fileName,
        int pauseMs,
        CancellationToken cancellationToken)
    {
        progress?.Report(new AppUpdateProgressSnapshot
        {
            Stage = stage,
            StageText = stageText,
            StatusText = statusText,
            FileName = fileName
        });

        if (pauseMs > 0)
        {
            await Task.Delay(pauseMs, cancellationToken);
        }
    }

    private AppUpdateCheckResult BuildCheckResult(AppUpdateManifest manifest, string sourceLabel)
    {
        var availableVersion = ParseVersion(manifest.Version);
        var currentVersion = GetCurrentVersion();

        if (availableVersion == null)
        {
            return new AppUpdateCheckResult
            {
                Status = AppUpdateAvailabilityStatus.NoSourceConfigured,
                Message = $"Update source returned an invalid version: '{manifest.Version}'."
            };
        }

        if (availableVersion <= currentVersion)
        {
            return new AppUpdateCheckResult
            {
                Status = AppUpdateAvailabilityStatus.NoUpdate,
                Message = $"Portal is up to date. {sourceLabel}. Current version: {currentVersion}."
            };
        }

        return new AppUpdateCheckResult
        {
            Status = AppUpdateAvailabilityStatus.UpdateAvailable,
            Message = $"Update {availableVersion} is available. {sourceLabel}.",
            Manifest = manifest
        };
    }

    private async Task<AppUpdateManifest> LoadGitHubReleaseManifestAsync(
        string repository,
        string preferredAssetName,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException("Built-in GitHub repository must use owner/repo format.");
        }

        var apiUrl = $"https://api.github.com/repos/{parts[0]}/{parts[1]}/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var release = JsonSerializer.Deserialize<GitHubReleaseDto>(json)
            ?? throw new InvalidOperationException("GitHub API returned an empty release payload.");

        if (string.IsNullOrWhiteSpace(release.TagName))
        {
            throw new InvalidOperationException("GitHub release does not contain tag_name.");
        }

        var asset = SelectReleaseAsset(release, preferredAssetName)
            ?? throw new InvalidOperationException("No suitable .zip asset was found in the latest GitHub release.");

        return new AppUpdateManifest
        {
            Version = release.TagName.Trim(),
            PackageUri = asset.BrowserDownloadUrl.Trim(),
            PackageApiUri = string.IsNullOrWhiteSpace(asset.ApiUrl) ? null : asset.ApiUrl.Trim(),
            PackageFileName = asset.Name.Trim(),
            PackageSizeBytes = asset.Size > 0 ? asset.Size : null,
            Sha256 = NormalizeDigest(asset.Digest),
            ReleaseNotes = release.Body,
            ProviderDllRelativePath = DefaultProviderDllRelativePath,
            SourceRepository = repository,
            ReleasePageUrl = string.IsNullOrWhiteSpace(release.HtmlUrl) ? null : release.HtmlUrl.Trim(),
            PublishedAtUtc = release.PublishedAtUtc
        };
    }

    private static GitHubReleaseAssetDto? SelectReleaseAsset(GitHubReleaseDto release, string preferredAssetName)
    {
        if (release.Assets == null || release.Assets.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredAssetName))
        {
            var preferred = release.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Name, preferredAssetName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (preferred != null)
            {
                return preferred;
            }
        }

        var zipAssets = release.Assets.Where(asset =>
                !string.IsNullOrWhiteSpace(asset.Name) &&
                asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (zipAssets.Count == 0)
        {
            return release.Assets.FirstOrDefault();
        }

        var platformToken = GetPlatformToken();
        var architectureToken = GetArchitectureToken();

        return zipAssets
            .OrderByDescending(asset => GetAssetScore(asset.Name, platformToken, architectureToken))
            .ThenBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string GetPlatformToken()
    {
        if (OperatingSystem.IsWindows())
        {
            return "win";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "mac";
        }

        return string.Empty;
    }

    private static string GetArchitectureToken()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => string.Empty
        };
    }

    private static int GetAssetScore(string assetName, string platformToken, string architectureToken)
    {
        var normalized = assetName.Trim().ToLowerInvariant();
        var score = 0;

        if (normalized.Contains("portalwin"))
        {
            score += 4;
        }

        if (!string.IsNullOrWhiteSpace(platformToken) &&
            (normalized.Contains(platformToken) || normalized.Contains("windows") && platformToken == "win"))
        {
            score += 3;
        }

        if (!string.IsNullOrWhiteSpace(architectureToken))
        {
            if (normalized.Contains(architectureToken))
            {
                score += 3;
            }
            else if (architectureToken == "x64" && normalized.Contains("amd64"))
            {
                score += 3;
            }
        }

        return score;
    }

    private async Task DownloadPackageAsync(
        Uri packageUri,
        string destinationPath,
        string packageFileName,
        string? accessToken,
        IProgress<AppUpdateProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        if (packageUri.IsFile)
        {
            var sourceInfo = new FileInfo(packageUri.LocalPath);
            var totalBytes = sourceInfo.Exists ? sourceInfo.Length : (long?)null;
            await using var sourceStream = File.OpenRead(packageUri.LocalPath);
            await using var destinationStream = File.Create(destinationPath);
            await CopyWithProgressAsync(sourceStream, destinationStream, totalBytes, packageFileName, progress, cancellationToken);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, packageUri);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        }

        if (string.Equals(packageUri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytesRemote = response.Content.Headers.ContentLength;
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destinationStreamRemote = File.Create(destinationPath);
        await CopyWithProgressAsync(contentStream, destinationStreamRemote, totalBytesRemote, packageFileName, progress, cancellationToken);
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        long? totalBytes,
        string fileName,
        IProgress<AppUpdateProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 64];
        long totalRead = 0;
        var startedAt = Stopwatch.StartNew();

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;

            var elapsedSeconds = Math.Max(startedAt.Elapsed.TotalSeconds, 0.1);
            progress?.Report(new AppUpdateProgressSnapshot
            {
                Stage = AppUpdateStage.Downloading,
                StageText = "Downloading update",
                StatusText = totalBytes.HasValue
                    ? $"Downloading {fileName} from GitHub Releases..."
                    : $"Downloading {fileName} (size unknown)...",
                FileName = fileName,
                BytesReceived = totalRead,
                TotalBytes = totalBytes,
                BytesPerSecond = totalRead / elapsedSeconds
            });
        }
    }

    private static async Task ValidatePackageAsync(string packagePath, AppUpdateManifest manifest, CancellationToken cancellationToken)
    {
        if (manifest.PackageSizeBytes.HasValue)
        {
            var actualSize = new FileInfo(packagePath).Length;
            if (actualSize != manifest.PackageSizeBytes.Value)
            {
                throw new InvalidOperationException($"Package size mismatch. Expected {manifest.PackageSizeBytes.Value}, actual {actualSize}.");
            }
        }

        if (string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            return;
        }

        await using var stream = File.OpenRead(packagePath);
        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        var actualHash = Convert.ToHexString(hashBytes);
        if (!string.Equals(actualHash, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Package hash validation failed.");
        }
    }

    private static Uri ResolvePackageUri(string packageUriValue)
    {
        if (Uri.TryCreate(packageUriValue, UriKind.Absolute, out var absolutePackageUri))
        {
            return absolutePackageUri;
        }

        throw new InvalidOperationException("GitHub release asset URL must be absolute.");
    }

    private static string? NormalizeDigest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digest = value.Trim();
        const string prefix = "sha256:";
        return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..]
            : digest;
    }

    private static string ResolveExtractedRoot(string stagingDirectory)
    {
        var files = Directory.GetFiles(stagingDirectory);
        var directories = Directory.GetDirectories(stagingDirectory);
        if (files.Length == 0 && directories.Length == 1)
        {
            return directories[0];
        }

        return stagingDirectory;
    }

    private static bool DetectProviderPayload(string extractedRoot, string providerDllRelativePath)
    {
        var expectedProviderPath = Path.Combine(extractedRoot, providerDllRelativePath);
        if (File.Exists(expectedProviderPath))
        {
            return true;
        }

        return Directory.GetFiles(extractedRoot, "Portal.CredentialProvider*.dll", SearchOption.AllDirectories).Any()
               || Directory.GetFiles(extractedRoot, "Portal.CredentialProvider*.comhost.dll", SearchOption.AllDirectories).Any();
    }

    private string CopyUpdaterToTemp()
    {
        var updaterSourceDirectory = FindUpdaterDirectory();
        var tempUpdaterDirectory = Path.Combine(UpdatesRootDirectory, "updater-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempUpdaterDirectory);

        foreach (var file in Directory.GetFiles(updaterSourceDirectory))
        {
            var destinationPath = Path.Combine(tempUpdaterDirectory, Path.GetFileName(file));
            File.Copy(file, destinationPath, overwrite: true);
        }

        var updaterPath = Path.Combine(tempUpdaterDirectory, "Portal.Updater.exe");
        if (!File.Exists(updaterPath))
        {
            throw new FileNotFoundException("Portal.Updater.exe was not found after copying updater runtime.", updaterPath);
        }

        return updaterPath;
    }

    private static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private string FindUpdaterDirectory()
    {
        var candidateDirectories = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Updater"),
            AppDomain.CurrentDomain.BaseDirectory
        };

        foreach (var candidateDirectory in candidateDirectories)
        {
            var updaterPath = Path.Combine(candidateDirectory, "Portal.Updater.exe");
            if (File.Exists(updaterPath))
            {
                return candidateDirectory;
            }
        }

        throw new FileNotFoundException("Portal.Updater.exe was not found. Make sure the updater project is built and copied next to Host.");
    }

    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        return Version.TryParse(normalized, out var parsed) ? parsed : null;
    }

    private static string GetSafeVersionText()
    {
        var version = typeof(UpdateService).Assembly.GetName().Version;
        return version?.ToString(3) ?? "1.1.4";
    }

    private static string NormalizeRepository(string repository)
    {
        var normalized = repository.Trim();
        normalized = normalized.Trim('"');

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Host, "www.github.com", StringComparison.OrdinalIgnoreCase)))
        {
            normalized = uri.AbsolutePath.Trim('/');
        }

        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("Repository must be in owner/repo format or a GitHub URL.");
        }

        return $"{parts[0]}/{parts[1]}";
    }
}

internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("published_at")]
    public DateTime? PublishedAtUtc { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAssetDto> Assets { get; set; } = new();
}

internal sealed class GitHubReleaseAssetDto
{
    [JsonPropertyName("url")]
    public string ApiUrl { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}
