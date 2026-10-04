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

    public string IdsSafe() => $"{Name} ({ClientId})";
}
