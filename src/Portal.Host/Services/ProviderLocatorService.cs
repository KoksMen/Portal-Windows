using System;
using System.Collections.Generic;
using System.IO;
using Portal.Common;

namespace Portal.Host.Services;

public class ProviderLocatorService
{
    public string? FindProviderDll(string? userSpecifiedPath)
    {
        var currentAppVolumeRoot = GetCurrentApplicationVolumeRoot();

        // 1. Check if user manually specified a path
        if (!string.IsNullOrEmpty(userSpecifiedPath) && File.Exists(userSpecifiedPath))
        {
            var fullUserSpecifiedPath = Path.GetFullPath(userSpecifiedPath);
            if (IsOnVolume(fullUserSpecifiedPath, currentAppVolumeRoot))
            {
                Logger.Log($"[FindProviderDll] Found via user-specified path: {fullUserSpecifiedPath}");
                return fullUserSpecifiedPath;
            }

            Logger.LogWarning($"[FindProviderDll] Ignoring provider path from a different volume. CurrentVolume={currentAppVolumeRoot}, Path={fullUserSpecifiedPath}");
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

    private static string GetCurrentApplicationVolumeRoot()
    {
        var baseDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        return Path.GetPathRoot(baseDir) ?? string.Empty;
    }

    private static bool IsOnVolume(string path, string expectedVolumeRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(expectedVolumeRoot))
        {
            return false;
        }

        var pathVolumeRoot = Path.GetPathRoot(path);
        return string.Equals(pathVolumeRoot, expectedVolumeRoot, StringComparison.OrdinalIgnoreCase);
    }
}
