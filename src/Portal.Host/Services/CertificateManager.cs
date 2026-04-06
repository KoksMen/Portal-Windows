using System;
using System.Security.Cryptography.X509Certificates;
using Portal.Common;

namespace Portal.Host.Services;

public class CertificateManager
{
    public X509Certificate2 CreateOrLoadCertificate(PortalWinConfig config)
    {
        var existing = CertificateService.LoadCertificate();
        if (existing != null)
        {
            CertificateService.EnsureCertPermissions();
            return existing;
        }

        var cert = CertificateService.GenerateSelfSignedCertificate();
        CertificateService.SaveCertificate(cert);
        config.Save();
        Logger.Log($"New certificate generated: {cert.Thumbprint}");
        return cert;
    }

    public void RemoveCertificate(PortalWinConfig config)
    {
        CertificateService.RemoveCertificate();
        config.Save();
    }

    public bool CheckCertificate()
    {
        return CertificateService.CertificateExists();
    }
}
