namespace Portal.Host.Models;

public class StatusInfo
{
    public bool FirewallOk { get; set; }
    public bool CertificateOk { get; set; }
    public bool ProviderOk { get; set; }
    public int DeviceCount { get; set; }
}
