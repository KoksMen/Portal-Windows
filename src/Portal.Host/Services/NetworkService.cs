using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using Portal.Common;

namespace Portal.Host.Services;

public record NetworkInterfaceAddress(string InterfaceName, string IpAddress);

public class NetworkService
{
    public async Task<List<NetworkInterfaceAddress>> GetLocalInterfaceAddressesAsync(bool vpnCompatibilityModeEnabled = true)
    {
        var result = new List<NetworkInterfaceAddress>();

        try
        {
            var interfaces = GetEligibleInterfaces(vpnCompatibilityModeEnabled)
                .OrderBy(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                .ThenBy(ni => ni.Name)
                .ToList();

            foreach (var ni in interfaces)
            {
                var addresses = ni.GetIPProperties().UnicastAddresses
                    .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork
                                 && !IPAddress.IsLoopback(ua.Address))
                    .Select(ua => ua.Address.ToString());

                foreach (var ip in addresses)
                {
                    result.Add(new NetworkInterfaceAddress(ni.Name, ip));
                }
            }

            if (result.Count == 0)
            {
                string hostName = Dns.GetHostName();
                var host = await Dns.GetHostEntryAsync(hostName);
                foreach (var ip in host.AddressList.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork))
                {
                    result.Add(new NetworkInterfaceAddress("DNS", ip.ToString()));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("GetLocalInterfaceAddressesAsync", ex);
        }

        return result;
    }

    public async Task<List<string>> GetLocalIPsAsync(bool vpnCompatibilityModeEnabled = true)
    {
        var endpoints = await GetLocalInterfaceAddressesAsync(vpnCompatibilityModeEnabled);
        return endpoints.Select(x => x.IpAddress).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string? GetMacAddressForIp(string? ipAddress, bool vpnCompatibilityModeEnabled = true)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(ipAddress) && IPAddress.TryParse(ipAddress, out var parsedIp))
            {
                foreach (var ni in GetEligibleInterfaces(vpnCompatibilityModeEnabled))
                {
                    var hasMatchingIp = ni.GetIPProperties().UnicastAddresses.Any(ua =>
                        ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        Equals(ua.Address, parsedIp));

                    if (!hasMatchingIp)
                    {
                        continue;
                    }

                    var formatted = FormatMacAddress(ni.GetPhysicalAddress());
                    if (!string.IsNullOrWhiteSpace(formatted))
                    {
                        Logger.Log($"[NetworkService] Resolved MAC '{formatted}' for IP '{parsedIp}' using adapter '{ni.Name}'.");
                        return formatted;
                    }
                }
            }

            Logger.LogWarning($"[NetworkService] Could not resolve MAC for IP '{ipAddress ?? "<null>"}'. Falling back to preferred adapter.");
            return GetPreferredMacAddress(vpnCompatibilityModeEnabled);
        }
        catch (Exception ex)
        {
            Logger.LogError("GetMacAddressForIp", ex);
            return null;
        }
    }

    public string? GetPreferredMacAddress(bool vpnCompatibilityModeEnabled = true)
    {
        try
        {
            foreach (var ni in GetEligibleInterfaces(vpnCompatibilityModeEnabled)
                         .OrderBy(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
                         .ThenBy(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                         .ThenBy(ni => ni.Name))
            {
                var formatted = FormatMacAddress(ni.GetPhysicalAddress());
                if (!string.IsNullOrWhiteSpace(formatted))
                {
                    Logger.Log($"[NetworkService] Selected preferred MAC '{formatted}' from adapter '{ni.Name}'.");
                    return formatted;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("GetPreferredMacAddress", ex);
        }

        return null;
    }

    private static IEnumerable<NetworkInterface> GetEligibleInterfaces(bool vpnCompatibilityModeEnabled)
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                         && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                         && (!vpnCompatibilityModeEnabled || IsAdapterVpnCompatible(ni)));
    }

    private static bool IsAdapterVpnCompatible(NetworkInterface ni)
    {
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        return !ContainsAny(ni.Name, ni.Description, ni.Id,
            "Virtual",
            "vEthernet",
            "VMware",
            "VirtualBox",
            "Hyper-V",
            "Tailscale",
            "ZeroTier",
            "GlobalProtect",
            "Fortinet",
            "AnyConnect",
            "OpenVPN",
            "WireGuard",
            "Nord",
            "ProtonVPN",
            "Radmin",
            "NDIS",
            "Wintun",
            "TAP",
            "PPP");
    }

    private static bool ContainsAny(string? name, string? description, string? id, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if ((!string.IsNullOrWhiteSpace(name) && name.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(description) && description.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(id) && id.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FormatMacAddress(PhysicalAddress? address)
    {
        if (address == null)
        {
            return null;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length == 0)
        {
            return null;
        }

        return string.Join("-", bytes.Select(b => b.ToString("X2")));
    }
}
