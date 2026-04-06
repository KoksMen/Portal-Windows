using System.Text.Json.Serialization;

namespace Portal.Common.Models;

public class NetworkDeviceModel : DeviceModel
{
    [JsonPropertyName("ipAddress")]
    public string IpAddress { get; set; } = string.Empty;

    [JsonPropertyName("clientPort")]
    public int ClientPort { get; set; } = 29171;

    public NetworkDeviceModel()
    {
        TransportType = TransportType.Network;
    }
}
