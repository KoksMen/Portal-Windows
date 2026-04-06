using System.Security;

namespace Portal.Host.Models;

public sealed class BackupPasswordPayload : IDisposable
{
    public SecureString Password { get; }
    public SecureString ConfirmPassword { get; }

    public BackupPasswordPayload(SecureString password, SecureString confirmPassword)
    {
        Password = password ?? throw new ArgumentNullException(nameof(password));
        ConfirmPassword = confirmPassword ?? throw new ArgumentNullException(nameof(confirmPassword));
    }

    public void Dispose()
    {
        Password.Dispose();
        ConfirmPassword.Dispose();
    }
}
