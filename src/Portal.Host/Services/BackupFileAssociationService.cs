using Microsoft.Win32;
using Portal.Common;

namespace Portal.Host.Services;

public sealed class BackupFileAssociationService
{
    private const string ProgId = "Portal.Host.BackupFile";

    public void EnsureAssociation(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        try
        {
            using var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{EncryptedBackupService.BackupFileExtension}");
            extKey?.SetValue(string.Empty, ProgId);

            using var progIdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}");
            progIdKey?.SetValue(string.Empty, "Portal Encrypted Backup");

            using var iconKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\DefaultIcon");
            iconKey?.SetValue(string.Empty, $"\"{executablePath}\",0");

            using var commandKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            commandKey?.SetValue(string.Empty, $"\"{executablePath}\" \"%1\"");
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[BackupAssoc] Failed to register file association: {ex.Message}");
        }
    }
}
