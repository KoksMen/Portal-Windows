using System.Runtime.InteropServices;

namespace Portal.Common.Services;

internal static class LsaSecretStore
{
    private const int POLICY_GET_PRIVATE_INFORMATION = 0x00000004;
    private const int POLICY_CREATE_SECRET = 0x00000020;

    public static bool TryWriteSecret(string secretName, string secretValue)
    {
        if (string.IsNullOrWhiteSpace(secretName))
        {
            return false;
        }

        IntPtr policyHandle = IntPtr.Zero;
        IntPtr secretNameBuffer = IntPtr.Zero;
        IntPtr secretValueBuffer = IntPtr.Zero;

        try
        {
            var objectAttributes = new LsaObjectAttributes
            {
                Length = Marshal.SizeOf<LsaObjectAttributes>()
            };

            var openStatus = LsaOpenPolicy(
                IntPtr.Zero,
                ref objectAttributes,
                POLICY_CREATE_SECRET,
                out policyHandle);

            if (openStatus != 0)
            {
                LogLsaError("LsaOpenPolicy(write)", openStatus);
                return false;
            }

            var secretNameLsa = InitLsaString(secretName, out secretNameBuffer);
            var secretValueLsa = InitLsaString(secretValue, out secretValueBuffer);

            var storeStatus = LsaStorePrivateData(policyHandle, ref secretNameLsa, ref secretValueLsa);
            if (storeStatus != 0)
            {
                LogLsaError("LsaStorePrivateData", storeStatus);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError("[LsaSecretStore] Failed to write secret.", ex);
            return false;
        }
        finally
        {
            if (policyHandle != IntPtr.Zero)
            {
                LsaClose(policyHandle);
            }

            if (secretNameBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(secretNameBuffer);
            }

            if (secretValueBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(secretValueBuffer);
            }
        }
    }

    public static bool TryReadSecret(string secretName, out string? secretValue)
    {
        secretValue = null;
        if (string.IsNullOrWhiteSpace(secretName))
        {
            return false;
        }

        IntPtr policyHandle = IntPtr.Zero;
        IntPtr secretNameBuffer = IntPtr.Zero;
        IntPtr privateDataPtr = IntPtr.Zero;

        try
        {
            var objectAttributes = new LsaObjectAttributes
            {
                Length = Marshal.SizeOf<LsaObjectAttributes>()
            };

            var openStatus = LsaOpenPolicy(
                IntPtr.Zero,
                ref objectAttributes,
                POLICY_GET_PRIVATE_INFORMATION,
                out policyHandle);

            if (openStatus != 0)
            {
                LogLsaError("LsaOpenPolicy(read)", openStatus);
                return false;
            }

            var secretNameLsa = InitLsaString(secretName, out secretNameBuffer);
            var readStatus = LsaRetrievePrivateData(policyHandle, ref secretNameLsa, out privateDataPtr);
            if (readStatus != 0)
            {
                const uint STATUS_OBJECT_NAME_NOT_FOUND = 0xC0000034;
                if (readStatus != STATUS_OBJECT_NAME_NOT_FOUND)
                {
                    LogLsaError("LsaRetrievePrivateData", readStatus);
                }

                return false;
            }

            if (privateDataPtr == IntPtr.Zero)
            {
                return false;
            }

            var privateData = Marshal.PtrToStructure<LsaUnicodeString>(privateDataPtr);
            if (privateData.Buffer == IntPtr.Zero || privateData.Length == 0)
            {
                secretValue = string.Empty;
                return true;
            }

            secretValue = Marshal.PtrToStringUni(privateData.Buffer, privateData.Length / 2);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError("[LsaSecretStore] Failed to read secret.", ex);
            return false;
        }
        finally
        {
            if (privateDataPtr != IntPtr.Zero)
            {
                LsaFreeMemory(privateDataPtr);
            }

            if (policyHandle != IntPtr.Zero)
            {
                LsaClose(policyHandle);
            }

            if (secretNameBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(secretNameBuffer);
            }
        }
    }

    private static LsaUnicodeString InitLsaString(string value, out IntPtr allocatedBuffer)
    {
        allocatedBuffer = IntPtr.Zero;
        if (string.IsNullOrEmpty(value))
        {
            return default;
        }

        allocatedBuffer = Marshal.StringToHGlobalUni(value);
        var byteLength = checked((ushort)(value.Length * sizeof(char)));
        return new LsaUnicodeString
        {
            Buffer = allocatedBuffer,
            Length = byteLength,
            MaximumLength = checked((ushort)(byteLength + sizeof(char)))
        };
    }

    private static void LogLsaError(string operation, uint ntStatus)
    {
        var win32 = LsaNtStatusToWinError(ntStatus);
        Logger.LogWarning($"[LsaSecretStore] {operation} failed. NTSTATUS=0x{ntStatus:X8}, Win32={win32}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public int Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint LsaOpenPolicy(
        IntPtr systemName,
        ref LsaObjectAttributes objectAttributes,
        int desiredAccess,
        out IntPtr policyHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint LsaStorePrivateData(
        IntPtr policyHandle,
        ref LsaUnicodeString keyName,
        ref LsaUnicodeString privateData);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint LsaRetrievePrivateData(
        IntPtr policyHandle,
        ref LsaUnicodeString keyName,
        out IntPtr privateData);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr policyHandle);

    [DllImport("advapi32.dll")]
    private static extern uint LsaFreeMemory(IntPtr buffer);

    [DllImport("advapi32.dll")]
    private static extern uint LsaNtStatusToWinError(uint status);
}
