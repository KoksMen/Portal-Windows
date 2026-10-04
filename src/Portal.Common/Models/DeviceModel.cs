using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Portal.Common.Models;

[JsonDerivedType(typeof(NetworkDeviceModel), typeDiscriminator: "network")]
[JsonDerivedType(typeof(BluetoothDeviceModel), typeDiscriminator: "bluetooth")]
public abstract class DeviceModel
{
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Unknown Device";

    [JsonPropertyName("certHash")]
    public string CertHash { get; set; } = string.Empty;

    [JsonPropertyName("accounts")]
    public List<DeviceAccount> Accounts { get; set; } = new();

    [JsonPropertyName("transportType")]
    public TransportType TransportType { get; set; } = TransportType.Network;

    [JsonPropertyName("pairedAt")]
    public DateTime PairedAt { get; set; } = DateTime.Now;

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    [JsonIgnore]
    public string StateText => IsEnabled ? Helpers.Localization.T("Enabled") : Helpers.Localization.T("Disabled");

    [JsonIgnore]
    public string StateToolTip => IsEnabled ? Helpers.Localization.T("Enabled (click to disable)") : Helpers.Localization.T("Disabled (click to enable)");

    [JsonIgnore]
    public string StateBadgeText => IsEnabled ? Helpers.Localization.T("Active") : Helpers.Localization.T("Disabled");

    [JsonIgnore]
    public string TransportBadgeText => TransportType == TransportType.Bluetooth
        ? Helpers.Localization.T("Bluetooth")
        : Helpers.Localization.T("Wi-Fi / LAN");

    [JsonIgnore]
    public string TransportBadgeIcon => TransportType == TransportType.Bluetooth ? "🔷" : "📶";

    [JsonIgnore]
    public string AccountLabel => Helpers.Localization.T("Account:");

    [JsonIgnore]
    public string AccountValue => Accounts.Count > 0 && !string.IsNullOrWhiteSpace(Accounts[0].Username)
        ? Accounts[0].Username
        : "—";

    [JsonIgnore]
    public string PairedLabel => Helpers.Localization.T("Paired:");

    [JsonIgnore]
    public string PairedValue => PairedAt.ToString("yyyy-MM-dd HH:mm");

    [JsonIgnore]
    public string TestButtonText => Helpers.Localization.T("Test Connection");

    [JsonIgnore]
    public string InfoToolTip => Helpers.Localization.T("Certificate information");

    [JsonIgnore]
    public string EditToolTip => Helpers.Localization.T("Edit Account");

    [JsonIgnore]
    public string DeleteToolTip => Helpers.Localization.T("Remove Device / Удалить");

    public string IdsSafe() => $"{Name} ({ClientId})";
}
