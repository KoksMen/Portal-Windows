using System;
using System.Runtime.InteropServices;
using System.Security;
using Portal.Common;

namespace Portal.CredentialProvider.Services;

/// <summary>
/// Manages pending unlock state shared between Provider and Tile instances.
/// Extracted from PortalWinProvider/PortalWinReverseProvider to eliminate duplication.
/// </summary>
public class PendingUnlockState : IDisposable
{
    public string? Username { get; private set; }
    public string? Domain { get; private set; }
    public bool HasPendingUnlock { get; private set; }
    public bool ShouldAutoLogon { get; private set; }

    private SecureString? _securePassword;

    /// <summary>
    /// Set credentials from an approved unlock request.
    /// </summary>
    public void SetPending(string username, SecureString? securePassword, string domain)
    {
        Clear();

        Username = username;
        Domain = domain;
        HasPendingUnlock = true;
        ShouldAutoLogon = true;

        if (securePassword != null && securePassword.Length > 0)
        {
            _securePassword = securePassword.Copy();
            _securePassword.MakeReadOnly();
        }

        Logger.Log($"[PendingUnlockState] Pending unlock set for user: '{username}'");
    }

    public void ConsumeAutoLogon()
    {
        ShouldAutoLogon = false;
    }

    /// <summary>
    /// Retrieves the plaintext password. The caller is responsible for the lifetime of this string.
    /// </summary>
    public string? GetPassword()
    {
        if (_securePassword == null) return null;
        
        IntPtr unmanagedString = IntPtr.Zero;
        try
        {
            unmanagedString = Marshal.SecureStringToGlobalAllocUnicode(_securePassword);
            return Marshal.PtrToStringUni(unmanagedString);
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(unmanagedString);
        }
    }

    /// <summary>
    /// Clear pending state after credentials have been submitted.
    /// </summary>
    public void Clear()
    {
        HasPendingUnlock = false;
        ShouldAutoLogon = false;
        Username = null;
        Domain = null;
        
        _securePassword?.Dispose();
        _securePassword = null;
    }

    public void Dispose()
    {
        Clear();
    }
}
