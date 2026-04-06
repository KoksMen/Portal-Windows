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

    public string IdsSafe() => $"{Name} ({ClientId})";
}
