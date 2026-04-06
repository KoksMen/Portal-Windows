using System.Security.Cryptography.X509Certificates;

namespace Portal.Common.Abstractions;

/// <summary>
/// Abstraction for host certificate management (generate, load, save, remove).
/// </summary>
public interface ICertificateService
{
    X509Certificate2 GenerateSelfSignedCertificate();
    void SaveCertificate(X509Certificate2 cert, string? path = null);
    X509Certificate2? LoadCertificate(string? path = null);
    void RemoveCertificate(string? path = null);
    bool CertificateExists(string? path = null);
    void EnsureCertPermissions(string? dir = null);
    string GetCertHash(X509Certificate2 cert);
    string GetCertHash(X509Certificate cert);
}
