using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Portal.Common.Helpers;
using Portal.Common.Models;
using Portal.Common.Services;
using System.Text.Json.Nodes;

namespace Portal.Common;

public class PortalWinConfig
{
    private const string DevicesSecretName = "L$Portal-Windows.Devices";

    public static string ConfigDir => PortalStoragePaths.RootDirectory;

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly string[] HiddenPersistedKeys =
    {
        "port",
        "devices",
        "update repository",
        "update access token",
        "experimentalFeaturesEnabled",
        "strictSelectedTileWebSocketConnections",
        "disableKestrelClientCertificateValidation",
        "hostRequestCorrelationEnabled"
    };

    [JsonPropertyName("port")]
    public int Port { get; set; } = 29170;

    [JsonPropertyName("unlockMode")]
    public UnlockMode UnlockMode { get; set; } = UnlockMode.ClientInitiated;

    [JsonPropertyName("hostRequestTrigger")]
    public HostRequestTrigger HostRequestTrigger { get; set; } = HostRequestTrigger.OnClick;

    [JsonPropertyName("enforceUniqueAccountPerTransport")]
    public bool EnforceUniqueAccountPerTransport { get; set; } = true;

    [JsonPropertyName("enforceUniqueAccountAcrossTransports")]
    public bool EnforceUniqueAccountAcrossTransports { get; set; } = false;

    /// <summary>
    /// Timeout in minutes for waiting for client approval in Host-Initiated mode.
    /// 0 = infinite (wait until user cancels manually).
    /// Default: 2 minutes. Not exposed in UI — edit config.json directly.
    /// </summary>
    [JsonPropertyName("hostRequestTimeoutMinutes")]
    public int HostRequestTimeoutMinutes { get; set; } = 2;

    /// <summary>
    /// Hidden compatibility flag for Host-Initiated request correlation.
    /// true (default): use requestId to correlate unlock_request/unlock_response across transports.
    /// false: completely disable requestId correlation and use legacy transport behavior.
    /// Not exposed in UI — edit config.json directly.
    /// </summary>
    [JsonPropertyName("hostRequestCorrelationEnabled")]
    public bool HostRequestCorrelationEnabled { get; set; } = true;

    /// <summary>
    /// Hidden strict-mode flag for selected-tile WebSocket control.
    /// false (default): keep unmatched WebSocket connections for stability and restrict only request routing.
    /// true: reject and disconnect WebSocket clients that do not belong to the currently selected tile policy.
    /// Not exposed in UI — edit config.json directly.
    /// </summary>
    [JsonPropertyName("strictSelectedTileWebSocketConnections")]
    public bool StrictSelectedTileWebSocketConnections { get; set; } = false;

    /// <summary>
    /// Hidden safety-override for troubleshooting only.
    /// false (default): validate client cert on both Kestrel TLS callback and middleware.
    /// true: disable Kestrel-level cert validation and keep only middleware validation (legacy behavior).
    /// Not exposed in UI — edit config.json directly.
    /// </summary>
    [JsonPropertyName("disableKestrelClientCertificateValidation")]
    public bool DisableKestrelClientCertificateValidation { get; set; } = false;

    /// <summary>
    /// Hidden feature-flag for experimental Host functionality.
    /// false (default): hide experimental UI sections such as FAQ and Updates.
    /// true: expose experimental UI sections and flows.
    /// Not exposed in UI — edit config.json directly.
    /// </summary>
    [JsonPropertyName("experimentalFeaturesEnabled")]
    public bool ExperimentalFeaturesEnabled { get; set; } = false;

    /// <summary>
    /// Network compatibility mode for VPN-heavy environments.
    /// true (default): Host ignores known virtual/VPN adapters when choosing announce/connectivity IPs.
    /// false: Host allows all active adapters.
    /// </summary>
    [JsonPropertyName("vpnCompatibilityModeEnabled")]
    public bool VpnCompatibilityModeEnabled { get; set; } = true;

    [JsonPropertyName("lastUpdateCheckUtc")]
    public DateTime? LastUpdateCheckUtc { get; set; }

    [JsonPropertyName("lastDiscoveredUpdateVersion")]
    public string? LastDiscoveredUpdateVersion { get; set; }

    [JsonPropertyName("lastInstalledUpdateUtc")]
    public DateTime? LastInstalledUpdateUtc { get; set; }

    [JsonPropertyName("autoUpdateChecksEnabled")]
    public bool AutoUpdateChecksEnabled { get; set; } = true;

    [JsonPropertyName("update repository")]
    public string UpdateRepository { get; set; } = string.Empty;

    [JsonPropertyName("update access token")]
    public string UpdateAccessToken { get; set; } = string.Empty;

    // Backward compatibility for previous config keys.
    [JsonPropertyName("updateRepository")]
    public string? UpdateRepositoryLegacy
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(UpdateRepository))
            {
                UpdateRepository = value.Trim();
            }
        }
    }

    [JsonPropertyName("updateAccessToken")]
    public string? UpdateAccessTokenLegacy
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(UpdateAccessToken))
            {
                UpdateAccessToken = value.Trim();
            }
        }
    }

    [JsonPropertyName("hostId")]
    public string HostId { get; set; } = string.Empty;

    [JsonPropertyName("devices")]
    public List<DeviceModel> Devices { get; set; } = new();



    // --- Load / Save ---

    public static PortalWinConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<PortalWinConfig>(json) ?? new PortalWinConfig();
                var jsonObject = JsonNode.Parse(json) as JsonObject;
                var shouldResave = false;
                var legacyDevices = cfg.Devices ?? new List<DeviceModel>();

                if (cfg.UpdateRepository == null)
                {
                    cfg.UpdateRepository = string.Empty;
                    shouldResave = true;
                }

                if (cfg.UpdateAccessToken == null)
                {
                    cfg.UpdateAccessToken = string.Empty;
                    shouldResave = true;
                }

                if (cfg.Port != 29170)
                {
                    cfg.Port = 29170;
                    shouldResave = true;
                }

                var hasHiddenPersistedKeys = false;
                if (jsonObject != null)
                {
                    foreach (var key in HiddenPersistedKeys)
                    {
                        if (!jsonObject.ContainsKey(key))
                        {
                            continue;
                        }

                        hasHiddenPersistedKeys = true;
                        break;
                    }
                }

                if (hasHiddenPersistedKeys
                    || !string.IsNullOrWhiteSpace(cfg.UpdateRepository)
                    || !string.IsNullOrWhiteSpace(cfg.UpdateAccessToken)
                    || cfg.ExperimentalFeaturesEnabled
                    || cfg.StrictSelectedTileWebSocketConnections
                    || cfg.DisableKestrelClientCertificateValidation
                    || !cfg.HostRequestCorrelationEnabled)
                {
                    cfg.UpdateRepository = string.Empty;
                    cfg.UpdateAccessToken = string.Empty;
                    cfg.ExperimentalFeaturesEnabled = false;
                    cfg.StrictSelectedTileWebSocketConnections = false;
                    cfg.DisableKestrelClientCertificateValidation = false;
                    cfg.HostRequestCorrelationEnabled = true;
                    shouldResave = true;
                }

                if (string.IsNullOrEmpty(cfg.HostId))
                {
                    cfg.HostId = Guid.NewGuid().ToString();
                    Logger.Log($"Generated new HostId: {cfg.HostId}");
                    shouldResave = true;
                }

                if (TryLoadDevicesFromLsa(out var devicesFromLsa))
                {
                    cfg.Devices = devicesFromLsa;
                    if (legacyDevices.Count > 0)
                    {
                        shouldResave = true;
                    }
                }
                else
                {
                    cfg.Devices = legacyDevices;
                    if (legacyDevices.Count > 0 && TrySaveDevicesToLsa(legacyDevices))
                    {
                        shouldResave = true;
                    }
                }

                if (shouldResave)
                {
                    cfg.Save();
                }
                return cfg;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to load config", ex);
        }
        var newCfg = new PortalWinConfig();
        newCfg.HostId = Guid.NewGuid().ToString();
        newCfg.Save();
        Logger.Log($"Generated new HostId: {newCfg.HostId}");
        return newCfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var persistedToLsa = TrySaveDevicesToLsa(Devices);
            var json = BuildConfigJson(includeDevices: !persistedToLsa);
            File.WriteAllText(ConfigPath, json);

            try
            {
                var fileInfo = new FileInfo(ConfigPath);
                var fileSecurity = fileInfo.GetAccessControl();
                fileSecurity.SetAccessRuleProtection(true, false);

                var systemSid = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
                var adminSid = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
                var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User;

                fileSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(systemSid, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
                fileSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(adminSid, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
                if (currentUser != null)
                {
                    fileSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(currentUser, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
                }

                fileInfo.SetAccessControl(fileSecurity);
            }
            catch (Exception secEx)
            {
                Logger.Log($"Warning: Failed to lock down config.json ACLs: {secEx.Message}");
            }

            Logger.Log("Configuration saved.");
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to save config", ex);
            throw;
        }
    }

    // --- Helpers ---

    public DeviceModel? FindDeviceByCertHash(string certHash)
    {
        return Devices.FirstOrDefault(d =>
            d.IsEnabled &&
            string.Equals(d.CertHash, certHash, StringComparison.OrdinalIgnoreCase));
    }

    public DeviceModel? FindDeviceByClientId(string clientId)
    {
        return Devices.FirstOrDefault(d =>
            string.Equals(d.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
    }

    public BluetoothDeviceModel? FindDeviceByBluetoothAddress(string macAddress)
    {
        var normalized = NormalizeBluetoothAddress(macAddress);
        return Devices.OfType<BluetoothDeviceModel>().FirstOrDefault(d =>
            d.IsEnabled &&
            string.Equals(NormalizeBluetoothAddress(d.BluetoothAddress), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Find all devices (Network + Bluetooth) that have an account matching the given username.
    /// Used by Host-Initiated mode to send unlock requests across all transports.
    /// </summary>
    public List<DeviceModel> FindAllDevicesForUser(string username, string? qualifiedName = null)
    {
        var targetCanonical = IdentityHelper.ToCanonical(qualifiedName) ?? IdentityHelper.ToCanonical(username);
        var targetShort = IdentityHelper.GetShortUsername(qualifiedName) ?? IdentityHelper.GetShortUsername(username);

        var result = new List<DeviceModel>();
        foreach (var device in Devices)
        {
            if (!device.IsEnabled)
            {
                continue;
            }

            foreach (var account in device.Accounts)
            {
                var accountCanonical = IdentityHelper.ToCanonical(account.Username, account.Domain);
                if (IdentityHelper.EqualsIgnoreCase(targetCanonical, accountCanonical))
                {
                    result.Add(device);
                    break; // don't add same device twice
                }

                var accountShort = IdentityHelper.GetShortUsername(account.Username);
                if (IdentityHelper.EqualsIgnoreCase(targetShort, accountShort))
                {
                    result.Add(device);
                    break; // don't add same device twice
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Checks whether an account is already assigned to any paired device.
    /// </summary>
    public bool HasPairedAccount(string username, string? domain = null, string? excludeClientId = null)
    {
        var targetCanonical = IdentityHelper.ToCanonical(username, domain) ?? IdentityHelper.ToCanonical(username);
        var targetShort = IdentityHelper.GetShortUsername(username);

        foreach (var device in Devices)
        {
            if (!string.IsNullOrWhiteSpace(excludeClientId) &&
                string.Equals(device.ClientId, excludeClientId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var account in device.Accounts)
            {
                var accountCanonical = IdentityHelper.ToCanonical(account.Username, account.Domain);
                if (IdentityHelper.EqualsIgnoreCase(targetCanonical, accountCanonical))
                {
                    return true;
                }

                var accountShort = IdentityHelper.GetShortUsername(account.Username);
                if (IdentityHelper.EqualsIgnoreCase(targetShort, accountShort))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether an account is already assigned to any paired device for the specified transport.
    /// </summary>
    public bool HasPairedAccountForTransport(string username, string? domain, TransportType transport, string? excludeClientId = null)
    {
        var targetCanonical = IdentityHelper.ToCanonical(username, domain) ?? IdentityHelper.ToCanonical(username);
        var targetShort = IdentityHelper.GetShortUsername(username);

        foreach (var device in Devices)
        {
            if (!string.IsNullOrWhiteSpace(excludeClientId) &&
                string.Equals(device.ClientId, excludeClientId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TransportMatches(device.TransportType, transport))
            {
                continue;
            }

            foreach (var account in device.Accounts)
            {
                var accountCanonical = IdentityHelper.ToCanonical(account.Username, account.Domain);
                if (IdentityHelper.EqualsIgnoreCase(targetCanonical, accountCanonical))
                {
                    return true;
                }

                var accountShort = IdentityHelper.GetShortUsername(account.Username);
                if (IdentityHelper.EqualsIgnoreCase(targetShort, accountShort))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether an account is already assigned to any paired device on a different transport.
    /// </summary>
    public bool HasPairedAccountOnOtherTransport(string username, string? domain, TransportType currentTransport, string? excludeClientId = null)
    {
        var otherTransport = currentTransport == TransportType.Network ? TransportType.Bluetooth : TransportType.Network;
        return HasPairedAccountForTransport(username, domain, otherTransport, excludeClientId);
    }

    private static bool TransportMatches(TransportType deviceTransport, TransportType requestedTransport)
    {
        if (deviceTransport == TransportType.Both || requestedTransport == TransportType.Both)
        {
            return true;
        }

        return deviceTransport == requestedTransport;
    }

    /// <summary>
    /// Normalizes a Bluetooth MAC address to uppercase hex without separators.
    /// Handles formats: "AA:BB:CC:DD:EE:FF", "AA-BB-CC-DD-EE-FF", "AABBCCDDEEFF",
    /// and parenthesized "(AA:BB:CC:DD:EE:FF)".
    /// </summary>
    public static string NormalizeBluetoothAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return string.Empty;
        // Remove parentheses, colons, dashes, spaces
        return address
            .Replace("(", "").Replace(")", "")
            .Replace(":", "").Replace("-", "")
            .Replace(" ", "")
            .ToUpperInvariant();
    }

    private static bool TryLoadDevicesFromLsa(out List<DeviceModel> devices)
    {
        devices = new List<DeviceModel>();
        if (!LsaSecretStore.TryReadSecret(DevicesSecretName, out var json) || string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            devices = JsonSerializer.Deserialize<List<DeviceModel>>(json, JsonOptions) ?? new List<DeviceModel>();
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to deserialize devices from LSA secret", ex);
            return false;
        }
    }

    private static bool TrySaveDevicesToLsa(List<DeviceModel>? devices)
    {
        try
        {
            var safeDevices = devices ?? new List<DeviceModel>();
            var json = JsonSerializer.Serialize(safeDevices, JsonOptions);
            return LsaSecretStore.TryWriteSecret(DevicesSecretName, json);
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to serialize devices for LSA secret", ex);
            return false;
        }
    }

    private string BuildConfigJson(bool includeDevices)
    {
        var node = JsonSerializer.SerializeToNode(this, JsonOptions) as JsonObject ?? new JsonObject();

        foreach (var key in HiddenPersistedKeys)
        {
            node.Remove(key);
        }

        return node.ToJsonString(JsonOptions);
    }

    public void SetUpdateAccessToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            UpdateAccessToken = string.Empty;
            return;
        }

        // Token is intentionally stored as plain text by user request.
        UpdateAccessToken = token.Trim();
    }

    public string? GetUpdateAccessToken()
    {
        if (string.IsNullOrWhiteSpace(UpdateAccessToken))
        {
            return null;
        }

        return UpdateAccessToken.Trim();
    }
}


public enum UnlockMode
{
    ClientInitiated = 0,
    HostInitiated = 1,
    Both = 2
}

public enum TransportType
{
    Network = 0,
    Bluetooth = 1,
    Both = 2
}

/// <summary>
/// Controls when the CredentialProvider automatically sends host-initiated unlock requests.
/// Only applicable when UnlockMode is HostInitiated or Both.
/// </summary>
public enum HostRequestTrigger
{
    /// <summary>Unlock request is sent only when the user clicks the button on the lock screen.</summary>
    OnClick = 0,

    /// <summary>Unlock request is sent on button click AND automatically at PC startup (Logon scenario).</summary>
    OnClickAndStartup = 1,

    /// <summary>Unlock request is sent on button click AND automatically on any LogonUI appearance (Logon + WorkstationUnlock).</summary>
    OnClickAndAnyLockScreen = 2
}
