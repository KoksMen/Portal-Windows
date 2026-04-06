using System.Security;

namespace Portal.Host.Models;

public sealed class RestoreBackupPayload : IDisposable
{
    public string FilePath { get; }
    public SecureString Password { get; }

    public RestoreBackupPayload(string filePath, SecureString password)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        Password = password ?? throw new ArgumentNullException(nameof(password));
    }

    public void Dispose()
    {
        Password.Dispose();
    }
}
