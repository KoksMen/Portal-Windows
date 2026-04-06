using System.Text.Json.Serialization;

namespace Portal.Common.Models;

public class AppUpdateManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("packageUri")]
    public string PackageUri { get; set; } = string.Empty;

    [JsonPropertyName("packageApiUri")]
    public string? PackageApiUri { get; set; }

    [JsonPropertyName("packageFileName")]
    public string PackageFileName { get; set; } = string.Empty;

    [JsonPropertyName("packageSizeBytes")]
    public long? PackageSizeBytes { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("releaseNotes")]
    public string? ReleaseNotes { get; set; }

    [JsonPropertyName("requiresProviderReinstall")]
    public bool RequiresProviderReinstall { get; set; }

    [JsonPropertyName("providerDllRelativePath")]
    public string? ProviderDllRelativePath { get; set; }

    [JsonPropertyName("sourceRepository")]
    public string? SourceRepository { get; set; }

    [JsonPropertyName("releasePageUrl")]
    public string? ReleasePageUrl { get; set; }

    [JsonPropertyName("publishedAtUtc")]
    public DateTime? PublishedAtUtc { get; set; }
}

public class AppUpdateSession
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("targetVersion")]
    public string TargetVersion { get; set; } = string.Empty;

    [JsonPropertyName("hostProcessId")]
    public int HostProcessId { get; set; }

    [JsonPropertyName("hostExecutablePath")]
    public string HostExecutablePath { get; set; } = string.Empty;

    [JsonPropertyName("applicationDirectory")]
    public string ApplicationDirectory { get; set; } = string.Empty;

    [JsonPropertyName("stagingDirectory")]
    public string StagingDirectory { get; set; } = string.Empty;

    [JsonPropertyName("backupDirectory")]
    public string BackupDirectory { get; set; } = string.Empty;

    [JsonPropertyName("requiresProviderReinstall")]
    public bool RequiresProviderReinstall { get; set; }

    [JsonPropertyName("providerDllRelativePath")]
    public string? ProviderDllRelativePath { get; set; }

    [JsonPropertyName("resultFilePath")]
    public string ResultFilePath { get; set; } = string.Empty;
}

public class AppUpdateResult
{
    [JsonPropertyName("completedAtUtc")]
    public DateTime CompletedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("targetVersion")]
    public string TargetVersion { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    public string Details { get; set; } = string.Empty;

    [JsonPropertyName("requiresProviderReinstall")]
    public bool RequiresProviderReinstall { get; set; }

    [JsonPropertyName("providerReinstallSucceeded")]
    public bool ProviderReinstallSucceeded { get; set; }

    [JsonPropertyName("rollbackAttempted")]
    public bool RollbackAttempted { get; set; }

    [JsonPropertyName("rollbackSucceeded")]
    public bool RollbackSucceeded { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }
}
