using System.Text.Json.Serialization;

namespace Portal.Common.Models;

public class BluetoothDeviceModel : DeviceModel
{
    [JsonPropertyName("bluetoothAddress")]
    public string BluetoothAddress { get; set; } = string.Empty;

    public BluetoothDeviceModel()
    {
        TransportType = TransportType.Bluetooth;
    }
}
