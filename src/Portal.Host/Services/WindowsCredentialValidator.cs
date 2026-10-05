using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using Portal.Common;

namespace Portal.Host.Services;

public record CredentialValidationResult(
    bool IsValid,
    bool IsHardFailure,
    string? ErrorMessage,
    int Win32ErrorCode = 0)
{
    public static CredentialValidationResult Success() =>
        new(IsValid: true, IsHardFailure: false, ErrorMessage: null, Win32ErrorCode: 0);

    public static CredentialValidationResult Failure(bool isHardFailure, string errorMessage, int win32ErrorCode = 0) =>
        new(IsValid: false, IsHardFailure: isHardFailure, ErrorMessage: errorMessage, Win32ErrorCode: win32ErrorCode);
}

/// <summary>
/// Validates Windows credentials in real-time before saving them to LSA/DPAPI storage.
/// Uses Win32 LogonUserW (LOGON32_LOGON_NETWORK) to safely verify username and password
/// without leaking managed password strings into the garbage collector.
/// </summary>
public class WindowsCredentialValidator
{
    private const int LOGON32_LOGON_NETWORK = 3;
    private const int LOGON32_LOGON_INTERACTIVE = 2;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    private const int ERROR_LOGON_FAILURE = 1326;
    private const int ERROR_ACCOUNT_RESTRICTION = 1327;
    private const int ERROR_PASSWORD_EXPIRED = 1330;
    private const int ERROR_ACCOUNT_DISABLED = 1331;
    private const int ERROR_ACCOUNT_LOCKED_OUT = 1909;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(
        string lpszUsername,
        string lpszDomain,
        IntPtr lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public virtual CredentialValidationResult Validate(string username, string? domain, SecureString? password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return CredentialValidationResult.Failure(
                isHardFailure: true,
                errorMessage: LocalizationService.T("Please select account."));
        }

        if (password == null || password.Length == 0)
        {
            return CredentialValidationResult.Failure(
                isHardFailure: true,
                errorMessage: LocalizationService.T("Please enter password."));
        }

        var effectiveUser = username.Trim();
        var effectiveDomain = (domain ?? string.Empty).Trim();

        if (effectiveUser.Contains('\\'))
        {
            var parts = effectiveUser.Split('\\', 2);
            effectiveDomain = parts[0];
            effectiveUser = parts[1];
        }

        if (string.IsNullOrWhiteSpace(effectiveDomain))
        {
            effectiveDomain = Environment.UserDomainName;
        }

        IntPtr unmanagedPassword = IntPtr.Zero;
        try
        {
            unmanagedPassword = Marshal.SecureStringToGlobalAllocUnicode(password);

            // 1. Attempt LOGON32_LOGON_NETWORK (fastest, standard for credential verification)
            IntPtr token = IntPtr.Zero;
            bool success = LogonUser(
                effectiveUser,
                effectiveDomain,
                unmanagedPassword,
                LOGON32_LOGON_NETWORK,
                LOGON32_PROVIDER_DEFAULT,
                out token);

            if (success)
            {
                CloseHandle(token);
                Logger.Log($"[CredentialValidator] Credential validation succeeded for user '{effectiveDomain}\\{effectiveUser}'.");
                return CredentialValidationResult.Success();
            }

            int error = Marshal.GetLastWin32Error();

            // 2. If network logon encountered a policy restriction or unexpected error (not wrong password),
            // try interactive logon fallback if privileged
            if (error != ERROR_LOGON_FAILURE && error != ERROR_ACCOUNT_LOCKED_OUT && error != ERROR_PASSWORD_EXPIRED && error != ERROR_ACCOUNT_DISABLED)
            {
                IntPtr interactiveToken = IntPtr.Zero;
                bool interactiveSuccess = LogonUser(
                    effectiveUser,
                    effectiveDomain,
                    unmanagedPassword,
                    LOGON32_LOGON_INTERACTIVE,
                    LOGON32_PROVIDER_DEFAULT,
                    out interactiveToken);

                if (interactiveSuccess)
                {
                    CloseHandle(interactiveToken);
                    Logger.Log($"[CredentialValidator] Interactive credential validation succeeded for user '{effectiveDomain}\\{effectiveUser}'.");
                    return CredentialValidationResult.Success();
                }

                int interactiveError = Marshal.GetLastWin32Error();
                if (interactiveError == ERROR_LOGON_FAILURE)
                {
                    error = ERROR_LOGON_FAILURE;
                }
            }

            Logger.LogWarning($"[CredentialValidator] Credential validation failed for user '{effectiveDomain}\\{effectiveUser}'. Win32 Error: {error}");

            return error switch
            {
                ERROR_LOGON_FAILURE => CredentialValidationResult.Failure(
                    isHardFailure: true,
                    errorMessage: LocalizationService.T("The Windows password you entered is incorrect. Please check your credentials and try again."),
                    win32ErrorCode: error),

                ERROR_ACCOUNT_LOCKED_OUT => CredentialValidationResult.Failure(
                    isHardFailure: true,
                    errorMessage: LocalizationService.T("The referenced account is currently locked out in Windows."),
                    win32ErrorCode: error),

                ERROR_PASSWORD_EXPIRED => CredentialValidationResult.Failure(
                    isHardFailure: true,
                    errorMessage: LocalizationService.T("The password for this account has expired."),
                    win32ErrorCode: error),

                ERROR_ACCOUNT_DISABLED => CredentialValidationResult.Failure(
                    isHardFailure: true,
                    errorMessage: LocalizationService.T("This account is currently disabled in Windows."),
                    win32ErrorCode: error),

                ERROR_ACCOUNT_RESTRICTION => CredentialValidationResult.Failure(
                    isHardFailure: true,
                    errorMessage: LocalizationService.T("Account restriction detected (blank passwords or logon time restrictions may apply)."),
                    win32ErrorCode: error),

                _ => CredentialValidationResult.Failure(
                    isHardFailure: false,
                    errorMessage: LocalizationService.TF(
                        "Windows credential validation could not be completed (Error {0}: {1}).",
                        error,
                        new Win32Exception(error).Message),
                    win32ErrorCode: error)
            };
        }
        catch (Exception ex)
        {
            Logger.LogError($"[CredentialValidator] Exception validating credentials for '{effectiveDomain}\\{effectiveUser}'", ex);
            return CredentialValidationResult.Failure(
                isHardFailure: false,
                errorMessage: LocalizationService.TF("Unexpected validation error: {0}", ex.Message));
        }
        finally
        {
            if (unmanagedPassword != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(unmanagedPassword);
            }
        }
    }
}
