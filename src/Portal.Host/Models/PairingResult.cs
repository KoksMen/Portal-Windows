using Portal.Common;

namespace Portal.Host.Models;

public class PairingResult
{
    public Portal.Common.Models.DeviceModel? Device { get; set; }
    public bool Success { get; set; }
}
