using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Portal.Common.Models;

public class DeviceAccount
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;

    [JsonPropertyName("userSid")]
    public string? UserSid { get; set; }

    [JsonPropertyName("encryptedPasswordBlob")]
    public string EncryptedPasswordBlob { get; set; } = string.Empty;

    /// <summary>
    /// Custom unlock mode override for this account. If null, inherits host global UnlockMode.
    /// </summary>
    [JsonPropertyName("customUnlockMode")]
    public UnlockMode? CustomUnlockMode { get; set; }

    /// <summary>
    /// Optional inactivity expiry timeout in days (e.g. 7, 14, 30, 60, 90).
    /// If null or &lt;= 0, inactivity expiry is disabled.
    /// </summary>
    [JsonPropertyName("inactivityExpiryDays")]
    public int? InactivityExpiryDays { get; set; }

    [JsonPropertyName("lastUnlockedAtUtc")]
    public DateTime? LastUnlockedAtUtc { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Indicates whether remote unlock requires entering a local 2FA PIN on the lock screen.
    /// </summary>
    [JsonPropertyName("requiresTwoFactorPin")]
    public bool RequiresTwoFactorPin { get; set; } = false;

    /// <summary>
    /// Base64-encoded 16-byte random salt for PBKDF2-SHA256 PIN hashing.
    /// </summary>
    [JsonPropertyName("twoFactorPinSalt")]
    public string? TwoFactorPinSalt { get; set; }

    /// <summary>
    /// Base64-encoded 32-byte PBKDF2-SHA256 hash (100,000 iterations) of the 2FA PIN.
    /// </summary>
    [JsonPropertyName("twoFactorPinHash")]
    public string? TwoFactorPinHash { get; set; }

    private const int PinSaltByteCount = 16;
    private const int PinHashByteCount = 32;
    private const int PinPbkdf2Iterations = 100_000;

    public void SetTwoFactorPin(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            RemoveTwoFactorPin();
            return;
        }

        var trimmed = pin.Trim();
        if (trimmed.Length < 4 || trimmed.Length > 30)
        {
            throw new ArgumentException("2FA PIN must be between 4 and 30 characters.", nameof(pin));
        }

        byte[] pinBytes = Encoding.UTF8.GetBytes(trimmed);
        byte[] salt = RandomNumberGenerator.GetBytes(PinSaltByteCount);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            pinBytes,
            salt,
            PinPbkdf2Iterations,
            HashAlgorithmName.SHA256,
            PinHashByteCount);

        try
        {
            TwoFactorPinSalt = Convert.ToBase64String(salt);
            TwoFactorPinHash = Convert.ToBase64String(hash);
            RequiresTwoFactorPin = true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinBytes);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public bool VerifyTwoFactorPin(string pin)
    {
        if (!RequiresTwoFactorPin || string.IsNullOrEmpty(TwoFactorPinHash) || string.IsNullOrEmpty(TwoFactorPinSalt))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(pin))
        {
            return false;
        }

        byte[]? pinBytes = null;
        try
        {
            byte[] salt = Convert.FromBase64String(TwoFactorPinSalt);
            byte[] expectedHash = Convert.FromBase64String(TwoFactorPinHash);
            pinBytes = Encoding.UTF8.GetBytes(pin.Trim());
            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
                pinBytes,
                salt,
                PinPbkdf2Iterations,
                HashAlgorithmName.SHA256,
                PinHashByteCount);

            try
            {
                return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actualHash);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to verify 2FA PIN for {Username}", ex);
            return false;
        }
        finally
        {
            if (pinBytes != null)
            {
                CryptographicOperations.ZeroMemory(pinBytes);
            }
        }
    }

    public void RemoveTwoFactorPin()
    {
        RequiresTwoFactorPin = false;
        TwoFactorPinSalt = null;
        TwoFactorPinHash = null;
    }

    [JsonIgnore]
    public bool IsExpired => InactivityExpiryDays is > 0 &&
        (DateTime.UtcNow - (LastUnlockedAtUtc ?? CreatedAtUtc)) > TimeSpan.FromDays(InactivityExpiryDays.Value);

    public UnlockMode ResolveEffectiveUnlockMode(UnlockMode globalMode) => CustomUnlockMode ?? globalMode;

    public void RecordSuccessfulUnlock()
    {
        LastUnlockedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Decrypts the DPAPI blob and returns the password. 
    /// The caller holds the string and is responsible for its lifetime.
    /// </summary>
    public string? GetDecryptedPassword()
    {
        var data = TryDecryptPasswordBytes();
        if (data == null || data.Length == 0)
            return null;

        try
        {
            return Encoding.UTF8.GetString(data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public SecureString? GetDecryptedSecurePassword()
    {
        var data = TryDecryptPasswordBytes();
        if (data == null || data.Length == 0)
            return null;

        char[]? chars = null;
        try
        {
            chars = Encoding.UTF8.GetChars(data);
            var secure = new SecureString();
            foreach (var c in chars)
            {
                secure.AppendChar(c);
            }

            secure.MakeReadOnly();
            return secure;
        }
        finally
        {
            if (chars != null)
            {
                Array.Clear(chars, 0, chars.Length);
            }

            CryptographicOperations.ZeroMemory(data);
        }
    }

    /// <summary>
    /// Instantly encrypts the plaintext password into the DPAPI blob and discards the plaintext.
    /// </summary>
    public void SetPassword(string plaintextPassword)
    {
        if (string.IsNullOrEmpty(plaintextPassword))
        {
            EncryptedPasswordBlob = string.Empty;
            return;
        }

        using var secure = new SecureString();
        foreach (var c in plaintextPassword)
        {
            secure.AppendChar(c);
        }

        secure.MakeReadOnly();
        SetPassword(secure);
    }

    public void SetPassword(SecureString? securePassword)
    {
        if (securePassword == null || securePassword.Length == 0)
        {
            EncryptedPasswordBlob = string.Empty;
            return;
        }

        IntPtr unmanagedString = IntPtr.Zero;
        char[]? chars = null;
        byte[]? data = null;

        try
        {
            unmanagedString = Marshal.SecureStringToGlobalAllocUnicode(securePassword);
            chars = new char[securePassword.Length];
            Marshal.Copy(unmanagedString, chars, 0, chars.Length);

            data = Encoding.UTF8.GetBytes(chars);
            var entropy = Encoding.UTF8.GetBytes("PortalWinSecureEntropy");
            var encrypted = ProtectedData.Protect(data, entropy, DataProtectionScope.LocalMachine);
            EncryptedPasswordBlob = Convert.ToBase64String(encrypted);
        }
        finally
        {
            if (chars != null)
            {
                Array.Clear(chars, 0, chars.Length);
            }

            if (data != null)
            {
                CryptographicOperations.ZeroMemory(data);
            }

            if (unmanagedString != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(unmanagedString);
            }
        }
    }

    private byte[]? TryDecryptPasswordBytes()
    {
        if (string.IsNullOrEmpty(EncryptedPasswordBlob))
            return null;

        try
        {
            var encrypted = Convert.FromBase64String(EncryptedPasswordBlob);
            try
            {
                var entropy = Encoding.UTF8.GetBytes("PortalWinSecureEntropy");
                return ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.LocalMachine);
            }
            catch (CryptographicException)
            {
                // Fallback for older blobs
                return ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to decrypt password for {Username}", ex);
            return null;
        }
    }
}
