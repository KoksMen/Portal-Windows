using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using Portal.Common;
using Portal.Common.Models;

namespace Portal.Host.Services;

public sealed class EncryptedBackupService
{
    public const string BackupFileExtension = ".portalbackup";
    public const string BackupFileFilter = "Portal Backup (*.portalbackup)|*.portalbackup|All files (*.*)|*.*";

    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int KeySize = 32;
    private const int TagSize = 16;
    private const int Pbkdf2Iterations = 210000;

    private static readonly JsonSerializerOptions DeviceJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task CreateDeviceBackupAsync(
        string filePath,
        PortalWinConfig configSnapshot,
        IReadOnlyCollection<DeviceModel> devices,
        byte[] serverCertificatePfx,
        SecureString password,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(configSnapshot);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(serverCertificatePfx);
        ArgumentNullException.ThrowIfNull(password);

        if (password.Length == 0)
        {
            throw new InvalidOperationException("Backup password must not be empty.");
        }

        if (serverCertificatePfx.Length == 0)
        {
            throw new InvalidOperationException("Server certificate data is empty.");
        }

        ct.ThrowIfCancellationRequested();

        byte[]? passwordBytes = null;
        byte[]? key = null;
        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        byte[]? tag = null;
        byte[]? salt = null;
        byte[]? nonce = null;

        try
        {
            var plaintextPayload = new BackupPlaintext
            {
                ConfigJson = JsonSerializer.Serialize(configSnapshot, DeviceJsonOptions),
                Devices = devices.ToList(),
                ServerCertificatePfx = Convert.ToBase64String(serverCertificatePfx),
                CreatedUtc = DateTime.UtcNow
            };
            plaintext = JsonSerializer.SerializeToUtf8Bytes(plaintextPayload, DeviceJsonOptions);
            salt = RandomNumberGenerator.GetBytes(SaltSize);
            nonce = RandomNumberGenerator.GetBytes(NonceSize);
            tag = new byte[TagSize];
            ciphertext = new byte[plaintext.Length];

            passwordBytes = ToUtf8Bytes(password);
            key = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);

            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            var envelope = new EncryptedBackupEnvelope
            {
                Format = "Portal.EncryptedDeviceBackup",
                Version = 1,
                CreatedUtc = DateTime.UtcNow,
                Kdf = "PBKDF2-HMAC-SHA256",
                Iterations = Pbkdf2Iterations,
                Cipher = "AES-256-GCM",
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                Ciphertext = Convert.ToBase64String(ciphertext)
            };

            var json = JsonSerializer.Serialize(envelope, EnvelopeJsonOptions);
            await File.WriteAllTextAsync(filePath, json, Encoding.UTF8, ct);
        }
        finally
        {
            Zero(passwordBytes);
            Zero(key);
            Zero(plaintext);
            Zero(ciphertext);
            Zero(tag);
            Zero(salt);
            Zero(nonce);
        }
    }

    public async Task<DecryptedBackupData> RestoreDeviceBackupAsync(
        string filePath,
        SecureString password,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length == 0)
        {
            throw new InvalidOperationException("Backup password must not be empty.");
        }

        ct.ThrowIfCancellationRequested();

        byte[]? passwordBytes = null;
        byte[]? key = null;
        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        byte[]? tag = null;
        byte[]? salt = null;
        byte[]? nonce = null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct);
            var envelope = JsonSerializer.Deserialize<EncryptedBackupEnvelope>(json, DeviceJsonOptions)
                ?? throw new InvalidOperationException("Backup file is empty or malformed.");

            ValidateEnvelope(envelope);

            salt = Convert.FromBase64String(envelope.Salt);
            nonce = Convert.FromBase64String(envelope.Nonce);
            tag = Convert.FromBase64String(envelope.Tag);
            ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            plaintext = new byte[ciphertext.Length];

            passwordBytes = ToUtf8Bytes(password);
            key = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, envelope.Iterations, HashAlgorithmName.SHA256, KeySize);

            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            var payload = JsonSerializer.Deserialize<BackupPlaintext>(plaintext, DeviceJsonOptions)
                ?? throw new InvalidOperationException("Unable to parse decrypted backup payload.");

            var restoredConfig = string.IsNullOrWhiteSpace(payload.ConfigJson)
                ? new PortalWinConfig()
                : (JsonSerializer.Deserialize<PortalWinConfig>(payload.ConfigJson, DeviceJsonOptions) ?? new PortalWinConfig());

            var restoredDevices = payload.Devices ?? new List<DeviceModel>();
            var certBytes = string.IsNullOrWhiteSpace(payload.ServerCertificatePfx)
                ? Array.Empty<byte>()
                : Convert.FromBase64String(payload.ServerCertificatePfx);

            if (certBytes.Length == 0)
            {
                throw new InvalidOperationException("Backup does not contain server certificate.");
            }

            return new DecryptedBackupData(restoredConfig, restoredDevices, certBytes);
        }
        finally
        {
            Zero(passwordBytes);
            Zero(key);
            Zero(plaintext);
            Zero(ciphertext);
            Zero(tag);
            Zero(salt);
            Zero(nonce);
        }
    }

    private static void ValidateEnvelope(EncryptedBackupEnvelope envelope)
    {
        if (!string.Equals(envelope.Format, "Portal.EncryptedDeviceBackup", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported backup format: {envelope.Format}");
        }

        if (envelope.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported backup version: {envelope.Version}");
        }

        if (!string.Equals(envelope.Cipher, "AES-256-GCM", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported cipher: {envelope.Cipher}");
        }

        if (!string.Equals(envelope.Kdf, "PBKDF2-HMAC-SHA256", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported KDF: {envelope.Kdf}");
        }
    }

    private static byte[] ToUtf8Bytes(SecureString secure)
    {
        if (secure.Length == 0)
        {
            return Array.Empty<byte>();
        }

        IntPtr ptr = IntPtr.Zero;
        char[]? chars = null;
        byte[]? bytes = null;
        try
        {
            ptr = Marshal.SecureStringToGlobalAllocUnicode(secure);
            chars = new char[secure.Length];
            Marshal.Copy(ptr, chars, 0, chars.Length);
            bytes = Encoding.UTF8.GetBytes(chars);
            return bytes;
        }
        finally
        {
            if (chars != null)
            {
                Array.Clear(chars, 0, chars.Length);
            }

            if (ptr != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(ptr);
            }
        }
    }

    private static void Zero(byte[]? bytes)
    {
        if (bytes != null && bytes.Length > 0)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed class EncryptedBackupEnvelope
    {
        [JsonPropertyName("format")]
        public string Format { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("createdUtc")]
        public DateTime CreatedUtc { get; set; }

        [JsonPropertyName("kdf")]
        public string Kdf { get; set; } = string.Empty;

        [JsonPropertyName("iterations")]
        public int Iterations { get; set; }

        [JsonPropertyName("cipher")]
        public string Cipher { get; set; } = string.Empty;

        [JsonPropertyName("salt")]
        public string Salt { get; set; } = string.Empty;

        [JsonPropertyName("nonce")]
        public string Nonce { get; set; } = string.Empty;

        [JsonPropertyName("tag")]
        public string Tag { get; set; } = string.Empty;

        [JsonPropertyName("ciphertext")]
        public string Ciphertext { get; set; } = string.Empty;
    }

    private sealed class BackupPlaintext
    {
        [JsonPropertyName("configJson")]
        public string ConfigJson { get; set; } = string.Empty;

        [JsonPropertyName("devices")]
        public List<DeviceModel> Devices { get; set; } = new();

        [JsonPropertyName("serverCertificatePfx")]
        public string ServerCertificatePfx { get; set; } = string.Empty;

        [JsonPropertyName("createdUtc")]
        public DateTime CreatedUtc { get; set; }
    }
}

public sealed class DecryptedBackupData : IDisposable
{
    public PortalWinConfig Config { get; }
    public List<DeviceModel> Devices { get; }
    public byte[] ServerCertificatePfx { get; }

    public DecryptedBackupData(PortalWinConfig config, List<DeviceModel> devices, byte[] serverCertificatePfx)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Devices = devices ?? throw new ArgumentNullException(nameof(devices));
        ServerCertificatePfx = serverCertificatePfx ?? throw new ArgumentNullException(nameof(serverCertificatePfx));
    }

    public void Dispose()
    {
        if (ServerCertificatePfx.Length > 0)
        {
            CryptographicOperations.ZeroMemory(ServerCertificatePfx);
        }
    }
}
