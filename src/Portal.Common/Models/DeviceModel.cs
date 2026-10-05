using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Portal.Common.Models;

[JsonDerivedType(typeof(NetworkDeviceModel), typeDiscriminator: "network")]
[JsonDerivedType(typeof(BluetoothDeviceModel), typeDiscriminator: "bluetooth")]
public abstract class DeviceModel : INotifyPropertyChanged
{
    private bool _hasSecretIntegrityIssue;
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
    public string TransportIconData => TransportType == TransportType.Bluetooth
        ? "M14.88 16.29L13 18.17V14.41l1.88 1.88zM13 5.83l1.88 1.88L13 9.59V5.83zM17.71 7.71L12 2h-1v7.59L6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 11 14.41V22h1l5.71-5.71-4.3-4.29 4.3-4zm-2.83 8.58l-1.88-1.88V13.5l1.88 1.88.91-.91-1.9-1.9 1.9-1.9-.91-.91-1.88 1.88V9.59l1.88-1.88L13 6.83V4.41l3.29 3.3-3.29 3.29v2l3.29 3.29-3.29 3.29v-2.41l1.88-1.88z"
        : "M12 4C7.58 4 3.75 5.79.88 8.66l1.41 1.41C4.69 7.74 8.13 6.2 12 6.2c3.87 0 7.31 1.54 9.71 3.88l1.41-1.41C20.25 5.79 16.42 4 12 4zm0 4.5c-3.1 0-5.83 1.25-7.78 3.26l1.41 1.41C7.14 11.75 9.42 10.7 12 10.7s4.86 1.05 6.37 2.47l1.41-1.41C17.83 9.75 15.1 8.5 12 8.5zm0 4.5c-1.66 0-3.13.67-4.24 1.76l4.24 4.24 4.24-4.24C15.13 13.67 13.66 13 12 13z";

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
    public string TestToolTip => Helpers.Localization.T("Test Connection / Проверить связь с телефоном");

    [JsonIgnore]
    public string InfoToolTip => Helpers.Localization.T("Certificate information");

    [JsonIgnore]
    public string EditToolTip => Helpers.Localization.T("Edit Account");

    [JsonIgnore]
    public bool HasSecretIntegrityIssue
    {
        get => _hasSecretIntegrityIssue;
        set
        {
            if (_hasSecretIntegrityIssue != value)
            {
                _hasSecretIntegrityIssue = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSecretIntegrityIssue)));
            }
        }
    }

    [JsonIgnore]
    public string SecretIntegrityBadgeText => Helpers.Localization.T("Secret Error");

    [JsonIgnore]
    public string SecretIntegrityWarning => Helpers.Localization.T("Stored credentials cannot be decrypted. Re-enter password via Edit.");

    public event PropertyChangedEventHandler? PropertyChanged;

    public string IdsSafe() => $"{Name} ({ClientId})";
}
