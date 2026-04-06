using Portal.Common;
using System.Security;

namespace Portal.Host.Models;

public class PairingContext : IDisposable
{
    public string TargetUsername { get; set; } = string.Empty;
    public string TargetDomain { get; set; } = string.Empty;
    public string PairingCode { get; set; } = string.Empty;
    public string HostIpAddress { get; set; } = string.Empty;
    public TransportType SelectedTransport { get; set; } = TransportType.Network;

    private SecureString? _targetPassword;

    public SecureString? TargetPassword => _targetPassword;
    public bool HasTargetPassword => _targetPassword != null && _targetPassword.Length > 0;

    public void SetTargetPassword(SecureString? securePassword)
    {
        _targetPassword?.Dispose();
        _targetPassword = null;

        if (securePassword == null || securePassword.Length == 0)
        {
            return;
        }

        _targetPassword = securePassword.Copy();
        _targetPassword.MakeReadOnly();
    }

    public void ClearSensitiveData()
    {
        _targetPassword?.Dispose();
        _targetPassword = null;
    }

    public void Dispose()
    {
        ClearSensitiveData();
    }
}
