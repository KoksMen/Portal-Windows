using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Portal.Common.Helpers;
using Portal.Common.Services;

namespace Portal.Common;

/// <summary>
/// Manages self-signed X509 certificates for mTLS communication.
/// Certificates are stored as encrypted PFX files in the config folder.
/// The certificate password is cryptographically random (256-bit entropy)
/// and stored protected by DPAPI (LocalMachine) inside Windows LSA Secrets.
/// </summary>
public static class CertificateService
{
    private const string CertSubject = "CN=Portal Host";
    public const string LegacyCertPassword = "portalwin-host";
    private const string LsaCertSecretName = "L$PortalWin_HostCertPass";
    private static readonly byte[] DpapiEntropy = "PortalWinCertSecretEntropy"u8.ToArray();

    /// <summary>
    /// Default PFX file path: %ProgramData%\Portal-Windows\host_cert.pfx
    /// </summary>
    public static string DefaultCertPath => PortalStoragePaths.DefaultCertificatePath;

    /// <summary>
    /// Generates a new cryptographically secure random password (256-bit entropy, Base64).
    /// </summary>
    public static string GenerateRandomCertificatePassword()
    {
        var entropyBytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(entropyBytes);
    }

    private static string GetFallbackSecretPath(string? pfxPath = null)
    {
        var dir = Path.GetDirectoryName(pfxPath ?? DefaultCertPath);
        if (string.IsNullOrEmpty(dir))
        {
            dir = PortalStoragePaths.RootDirectory;
        }
        return Path.Combine(dir, ".cert_secret");
    }

    /// <summary>
    /// Stores the certificate password securely using DPAPI (LocalMachine) and Windows LSA Secrets.
    /// Falls back to DPAPI protected file if LSA is unavailable (e.g. non-elevated developer/test environment).
    /// </summary>
    public static bool StoreCertificatePassword(string plainPassword, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(plainPassword))
        {
            return false;
        }

        try
        {
            var rawBytes = Encoding.UTF8.GetBytes(plainPassword);
            var encrypted = ProtectedData.Protect(rawBytes, DpapiEntropy, DataProtectionScope.LocalMachine);
            var base64 = Convert.ToBase64String(encrypted);
            var storedInLsa = LsaSecretStore.TryWriteSecret(LsaCertSecretName, base64);
            if (storedInLsa)
            {
                Logger.Log("[CertificateService] Secure certificate password stored in LSA Secrets.");
                try
                {
                    var fallbackPath = GetFallbackSecretPath(path);
                    if (File.Exists(fallbackPath)) File.Delete(fallbackPath);
                }
                catch { }
                return true;
            }

            Logger.LogWarning("[CertificateService] LSA Secrets write unavailable. Storing in DPAPI fallback file.");
            var fallbackFile = GetFallbackSecretPath(path);
            var dir = Path.GetDirectoryName(fallbackFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            try
            {
                File.WriteAllText(fallbackFile, base64);
                SecurityHardeningHelper.HardenFilePermissions(fallbackFile);
            }
            catch (UnauthorizedAccessException)
            {
                var userFallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Portal-Windows", ".cert_secret");
                var userDir = Path.GetDirectoryName(userFallback);
                if (!string.IsNullOrEmpty(userDir) && !Directory.Exists(userDir))
                {
                    Directory.CreateDirectory(userDir);
                }
                File.WriteAllText(userFallback, base64);
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError("[CertificateService] Error storing certificate password in LSA/DPAPI.", ex);
            return false;
        }
    }

    /// <summary>
    /// Attempts to retrieve and decrypt the certificate password from LSA Secrets (or DPAPI fallback file).
    /// </summary>
    public static string? TryGetSecureCertificatePassword(string? path = null)
    {
        // 1. Try LSA Secrets
        try
        {
            if (LsaSecretStore.TryReadSecret(LsaCertSecretName, out var base64) && !string.IsNullOrWhiteSpace(base64))
            {
                var encrypted = Convert.FromBase64String(base64);
                var decrypted = ProtectedData.Unprotect(encrypted, DpapiEntropy, DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(decrypted);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[CertificateService] Failed reading from LSA Secrets ({ex.Message}), trying fallback...");
        }

        // 2. Try DPAPI fallback files (check whichever is newest between system and user fallback)
        try
        {
            var fallbackFile = GetFallbackSecretPath(path);
            var userFallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Portal-Windows", ".cert_secret");

            string? chosenFile = null;
            if (File.Exists(fallbackFile) && File.Exists(userFallback))
            {
                chosenFile = File.GetLastWriteTimeUtc(userFallback) > File.GetLastWriteTimeUtc(fallbackFile)
                    ? userFallback
                    : fallbackFile;
            }
            else if (File.Exists(fallbackFile))
            {
                chosenFile = fallbackFile;
            }
            else if (File.Exists(userFallback))
            {
                chosenFile = userFallback;
            }

            if (chosenFile != null)
            {
                var base64 = File.ReadAllText(chosenFile).Trim();
                if (!string.IsNullOrWhiteSpace(base64))
                {
                    var encrypted = Convert.FromBase64String(base64);
                    var decrypted = ProtectedData.Unprotect(encrypted, DpapiEntropy, DataProtectionScope.LocalMachine);
                    return Encoding.UTF8.GetString(decrypted);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("[CertificateService] Failed to read/unprotect certificate password from fallback file.", ex);
        }

        return null;
    }

    /// <summary>
    /// Resolves the current certificate password:
    /// 1. Tries secure password from LSA + DPAPI.
    /// 2. If not found or fails, tests if the existing certificate opens with legacy password.
    /// </summary>
    public static string? TryGetCertificatePassword(string? path = null)
    {
        var securePass = TryGetSecureCertificatePassword(path);
        if (!string.IsNullOrEmpty(securePass))
        {
            return securePass;
        }

        path ??= DefaultCertPath;
        if (File.Exists(path))
        {
            try
            {
#if NET9_0_OR_GREATER
                using var testCert = X509CertificateLoader.LoadPkcs12FromFile(path, LegacyCertPassword);
#else
                using var testCert = new X509Certificate2(path, LegacyCertPassword);
#endif
                if (testCert != null)
                {
                    return LegacyCertPassword;
                }
            }
            catch
            {
                // Not a legacy cert or wrong password
            }
        }

        return null;
    }

    /// <summary>
    /// Generate a new self-signed certificate for mTLS with a random password protected by LSA/DPAPI.
    /// </summary>
    public static X509Certificate2 GenerateSelfSignedCertificate(string? path = null)
    {
        path ??= DefaultCertPath;
        Logger.Log("[CertificateService] Generating new self-signed certificate...");
        try
        {
            var securePassword = GenerateRandomCertificatePassword();
            if (!StoreCertificatePassword(securePassword, path))
            {
                Logger.LogWarning("[CertificateService] Could not write secure password to LSA Secrets during generation.");
            }

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
            var exported = cert.Export(X509ContentType.Pfx, securePassword);
#if NET9_0_OR_GREATER
            var importedCert = X509CertificateLoader.LoadPkcs12(exported, securePassword,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
#else
            var importedCert = new X509Certificate2(exported, securePassword,
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
    /// Save certificate to PFX file using the secure password (or provided override).
    /// </summary>
    public static void SaveCertificate(X509Certificate2 cert, string? path = null, string? password = null)
    {
        path ??= DefaultCertPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (!string.IsNullOrEmpty(dir))
        {
            EnsureCertPermissions(dir);
        }

        password ??= TryGetCertificatePassword(path) ?? LegacyCertPassword;

        var pfxBytes = cert.Export(X509ContentType.Pfx, password);
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
    /// Tries the secure password from LSA/DPAPI first, falling back to legacy password if necessary.
    /// </summary>
    public static X509Certificate2? LoadCertificate(string? path = null, bool machineKeySet = false)
    {
        path ??= DefaultCertPath;
        Logger.Log($"[CertificateService] Attempting to load certificate from: {path} (MachineKeySet={machineKeySet})");

        if (!File.Exists(path))
        {
            Logger.LogWarning($"[CertificateService] Certificate file not found at: {path}");
            return null;
        }

        var storageFlags = X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable;
        if (machineKeySet)
        {
            storageFlags |= X509KeyStorageFlags.MachineKeySet;
        }

        // 1. Try secure password from LSA/DPAPI
        var securePassword = TryGetSecureCertificatePassword();
        if (!string.IsNullOrEmpty(securePassword))
        {
            try
            {
#if NET9_0_OR_GREATER
                var cert = X509CertificateLoader.LoadPkcs12FromFile(path, securePassword, storageFlags);
#else
                var cert = new X509Certificate2(path, securePassword, storageFlags);
#endif
                Logger.Log($"[CertificateService] Certificate loaded successfully with secure LSA password. Thumbprint: {cert.Thumbprint}, Subject: {cert.Subject}");
                return cert;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[CertificateService] Failed loading certificate with secure password ({ex.Message}), trying legacy password fallback...");
            }
        }

        // 2. Fallback to legacy password
        try
        {
#if NET9_0_OR_GREATER
            var cert = X509CertificateLoader.LoadPkcs12FromFile(path, LegacyCertPassword, storageFlags);
#else
            var cert = new X509Certificate2(path, LegacyCertPassword, storageFlags);
#endif
            Logger.Log($"[CertificateService] Certificate loaded with legacy password. Thumbprint: {cert.Thumbprint}, Subject: {cert.Subject}");
            return cert;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[CertificateService] Failed to load certificate from {path} with any password. Error: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// Remove certificate PFX file and its associated secret from LSA.
    /// </summary>
    public static void RemoveCertificate(string? path = null)
    {
        path ??= DefaultCertPath;
        if (File.Exists(path))
        {
            File.Delete(path);
            Logger.Log($"Certificate file removed: {path}");
        }

        LsaSecretStore.TryDeleteSecret(LsaCertSecretName);
        try
        {
            var fallbackFile = GetFallbackSecretPath(path);
            if (File.Exists(fallbackFile))
            {
                File.Delete(fallbackFile);
            }

            var userFallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Portal-Windows", ".cert_secret");
            if (File.Exists(userFallback))
            {
                File.Delete(userFallback);
            }
        }
        catch { }

        Logger.Log("[CertificateService] LSA certificate secret removed.");
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
