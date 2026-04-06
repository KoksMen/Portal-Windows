using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Portal.Common;

/// <summary>
/// Manages self-signed X509 certificates for mTLS communication.
/// Certificates are stored as PFX files in the config folder.
/// </summary>
public static class CertificateService
{
    private const string CertSubject = "CN=Portal Host";
    private const string CertPassword = "portalwin-host";

    /// <summary>
    /// Default PFX file path: %ProgramData%\Portal-Windows\host_cert.pfx
    /// </summary>
    public static string DefaultCertPath => PortalStoragePaths.DefaultCertificatePath;

    /// <summary>
    /// Generate a new self-signed certificate for mTLS.
    /// </summary>
    public static X509Certificate2 GenerateSelfSignedCertificate()
    {
        Logger.Log("[CertificateService] Generating new self-signed certificate...");
        try
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(CertSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            // Add key usages
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    critical: false));

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection
                    {
                        new("1.3.6.1.5.5.7.3.1"), // Server Authentication
                        new("1.3.6.1.5.5.7.3.2")  // Client Authentication
                    },
                    critical: false));

            var cert = request.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(10));

            // Export and re-import to make private key usable
            var exported = cert.Export(X509ContentType.Pfx, CertPassword);
#if NET9_0_OR_GREATER
        var importedCert = X509CertificateLoader.LoadPkcs12(exported, CertPassword,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
#else
            var importedCert = new X509Certificate2(exported, CertPassword,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
#endif

            Logger.Log($"[CertificateService] Certificate generated successfully. Thumbprint: {importedCert.Thumbprint}");
            return importedCert;
        }
        catch (Exception ex)
        {
            Logger.LogError("[CertificateService] Failed to generate certificate.", ex);
            throw;
        }
    }

    /// <summary>
    /// Save certificate to PFX file.
    /// </summary>
    public static void SaveCertificate(X509Certificate2 cert, string? path = null)
    {
        path ??= DefaultCertPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Always ensure permissions, even if directory exists
        if (!string.IsNullOrEmpty(dir)) EnsureCertPermissions(dir);

        var pfxBytes = cert.Export(X509ContentType.Pfx, CertPassword);
        File.WriteAllBytes(path, pfxBytes);
        Logger.Log($"Certificate saved to: {path} | Thumbprint: {cert.Thumbprint}");
    }

    public static void EnsureCertPermissions(string? dir = null)
    {
        dir ??= Path.GetDirectoryName(DefaultCertPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        try
        {
            // Ensure System and Everyone can read this directory (Critical for Credential Provider)
            var di = new DirectoryInfo(dir);
            var security = di.GetAccessControl();

            // Allow Everyone Read & Execute
            var everyone = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(everyone, System.Security.AccessControl.FileSystemRights.ReadAndExecute, System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));

            di.SetAccessControl(security);
            Logger.Log($"Permissions updated for: {dir}");
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to update permissions", ex);
        }
    }

    /// <summary>
    /// Load certificate from PFX file.
    /// </summary>
    public static X509Certificate2? LoadCertificate(string? path = null)
    {
        path ??= DefaultCertPath;
        Logger.Log($"[CertificateService] Attempting to load certificate from: {path}");

        if (!File.Exists(path))
        {
            Logger.LogWarning($"[CertificateService] Certificate file not found at: {path}");
            return null;
        }

        try
        {
#if NET9_0_OR_GREATER
            var cert = X509CertificateLoader.LoadPkcs12FromFile(path, CertPassword,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
#else
            var cert = new X509Certificate2(path, CertPassword,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
#endif

            Logger.Log($"[CertificateService] Certificate loaded successfully. Thumbprint: {cert.Thumbprint}, Subject: {cert.Subject}");
            return cert;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[CertificateService] Failed to load certificate from {path}. Error: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// Remove certificate PFX file.
    /// </summary>
    public static void RemoveCertificate(string? path = null)
    {
        path ??= DefaultCertPath;
        if (File.Exists(path))
        {
            File.Delete(path);
            Logger.Log($"Certificate file removed: {path}");
        }
    }

    /// <summary>
    /// Check if certificate file exists.
    /// </summary>
    public static bool CertificateExists(string? path = null)
    {
        path ??= DefaultCertPath;
        return File.Exists(path);
    }

    /// <summary>
    /// Compute SHA256 hash of the certificate for finger-printing.
    /// </summary>
    public static string GetCertHash(X509Certificate2 cert)
    {
        var hash = SHA256.HashData(cert.RawData);
#if NET9_0_OR_GREATER
        return Convert.ToHexStringLower(hash);
#else
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
#endif
    }

    /// <summary>
    /// Compute SHA256 hash from a generic X509Certificate (used in mTLS callbacks).
    /// </summary>
    public static string GetCertHash(System.Security.Cryptography.X509Certificates.X509Certificate cert)
    {
        var hash = SHA256.HashData(cert.GetRawCertData());
#if NET9_0_OR_GREATER
        return Convert.ToHexStringLower(hash);
#else
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
#endif
    }
}
