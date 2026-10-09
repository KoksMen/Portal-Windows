using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Portal.Common;
using Portal.Common.Helpers;
using Portal.Common.Models;

namespace Portal.Host.Services;

public class WindowsPasswordExpiryService : IDisposable
{
    private const uint UF_DONT_EXPIRE_PASSWORD = 0x10000;
    private const uint TIMEQ_FOREVER = 0xFFFFFFFF;
    private const int NERR_Success = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_1
    {
        public string usri1_name;
        public string usri1_password;
        public uint usri1_password_age;
        public uint usri1_priv;
        public string usri1_home_dir;
        public string usri1_comment;
        public uint usri1_flags;
        public string usri1_script_path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct USER_MODALS_INFO_0
    {
        public uint usrmod0_min_passwd_len;
        public uint usrmod0_max_passwd_age;
        public uint usrmod0_min_passwd_age;
        public uint usrmod0_force_logoff;
        public uint usrmod0_password_hist_len;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetUserGetInfo(string? servername, string username, int level, out IntPtr bufptr);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetUserModalsGet(string? servername, int level, out IntPtr bufptr);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    private readonly Timer? _timer;
    private readonly object _sync = new();
    private bool _disposed;

    public WindowsPasswordExpiryService()
    {
        // Periodic check every 6 hours, initial check after 10 seconds
        _timer = new Timer(_ => _ = RefreshAllAccountsAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromHours(6));
    }

    public async Task RefreshAllAccountsAsync()
    {
        await Task.Run(() =>
        {
            lock (_sync)
            {
                try
                {
                    var config = PortalWinConfig.Load();
                    var report = new PasswordStatusReport
                    {
                        CheckedAtUtc = DateTime.UtcNow
                    };

                    var processedUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var device in config.Devices)
                    {
                        foreach (var account in device.Accounts)
                        {
                            var userKey = $"{account.Domain}\\{account.Username}";
                            if (!processedUsers.Add(userKey)) continue;

                            var status = QueryAccountPasswordStatus(account.Username, account.Domain, account.UserSid);
                            if (status != null)
                            {
                                report.Accounts.Add(status);
                            }
                        }
                    }

                    PasswordStatusStore.SaveStatus(report);
                    Logger.Log($"[PasswordExpiry] Refreshed status for {report.Accounts.Count} account(s).");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[PasswordExpiry] Failed to refresh password status: {ex.Message}");
                }
            }
        });
    }

    public static UserPasswordExpiryStatus? QueryAccountPasswordStatus(string username, string? domain, string? userSid)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;

        var cleanUser = IdentityHelper.GetShortUsername(username) ?? username;
        if (string.IsNullOrWhiteSpace(cleanUser)) return null;
        string? server = null;

        IntPtr userBuf = IntPtr.Zero;
        IntPtr modalsBuf = IntPtr.Zero;

        try
        {
            int errUser = NetUserGetInfo(server, cleanUser, 1, out userBuf);
            if (errUser != NERR_Success || userBuf == IntPtr.Zero)
            {
                return null;
            }

            var userInfo = Marshal.PtrToStructure<USER_INFO_1>(userBuf);

            int errModals = NetUserModalsGet(server, 0, out modalsBuf);
            uint maxPasswdAge = 0;
            if (errModals == NERR_Success && modalsBuf != IntPtr.Zero)
            {
                var modals = Marshal.PtrToStructure<USER_MODALS_INFO_0>(modalsBuf);
                maxPasswdAge = modals.usrmod0_max_passwd_age;
            }

            var status = new UserPasswordExpiryStatus
            {
                Username = username,
                Domain = domain,
                UserSid = userSid,
                CheckedAtUtc = DateTime.UtcNow
            };

            // Check if user has 'Password never expires' flag
            if ((userInfo.usri1_flags & UF_DONT_EXPIRE_PASSWORD) != 0 || maxPasswdAge == 0 || maxPasswdAge == TIMEQ_FOREVER)
            {
                status.IsNeverExpires = true;
                status.IsExpired = false;
                status.DaysUntilExpiry = null;
                return status;
            }

            long remainingSeconds = (long)maxPasswdAge - userInfo.usri1_password_age;
            if (remainingSeconds <= 0)
            {
                status.IsExpired = true;
                status.DaysUntilExpiry = 0;
            }
            else
            {
                status.IsExpired = false;
                status.DaysUntilExpiry = (int)Math.Ceiling(remainingSeconds / 86400.0);
            }

            return status;
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[PasswordExpiry] Error querying account '{username}': {ex.Message}");
            return null;
        }
        finally
        {
            if (userBuf != IntPtr.Zero) NetApiBufferFree(userBuf);
            if (modalsBuf != IntPtr.Zero) NetApiBufferFree(modalsBuf);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
    }
}
