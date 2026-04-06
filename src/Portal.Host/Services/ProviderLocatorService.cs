using System;
using System.Collections.Generic;
using System.IO;
using Portal.Common;

namespace Portal.Host.Services;

public class ProviderLocatorService
{
    public string? FindProviderDll(string? userSpecifiedPath)
    {
        // 1. Check if user manually specified a path
        if (!string.IsNullOrEmpty(userSpecifiedPath) && File.Exists(userSpecifiedPath))
        {
            Logger.Log($"[FindProviderDll] Found via user-specified path: {userSpecifiedPath}");
            return Path.GetFullPath(userSpecifiedPath);
        }

        // 2. Search in CredentialProvider folder near the executable
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var providerDir = Path.Combine(baseDir, "CredentialProvider");
        var providerDll = Path.Combine(providerDir, "Portal.CredentialProvider.comhost.dll");

        Logger.Log($"[FindProviderDll] Checking provider path: {providerDll}");

        try
        {
            var fullPath = Path.GetFullPath(providerDll);
            if (File.Exists(fullPath))
            {
                Logger.Log($"[FindProviderDll] FOUND: {fullPath}");
                return fullPath;
            }
        }
        catch { }

        Logger.LogWarning($"[FindProviderDll] Provider DLL not found at: {providerDll}");
        return null;
    }
}
