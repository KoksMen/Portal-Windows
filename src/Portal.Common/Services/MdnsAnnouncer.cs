using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using Makaretu.Dns;
using Portal.Common.Abstractions;

namespace Portal.Common;

/// <summary>
/// Announces the PortalWin host service via mDNS (Bonjour/Zeroconf).
/// Other devices on the local network can discover this host automatically.
/// </summary>
public class MdnsAnnouncer : IMdnsAnnouncer
{
    private const string ServiceType = "_portalwin._tcp";
    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;
    private ServiceProfile? _profile;
    private bool _isRunning;
    private string? _currentMode;

    public bool UseIpv4 { get; set; } = true;
    public bool UseIpv6 { get; set; } = true;

    /// <summary>
    /// Start announcing the service on the network.
    /// </summary>
    /// <param name="config">Host configuration containing port and device info.</param>
    /// <param name="mode">Service mode: "pair" for Host pairing, "locked" for CredentialProvider lock screen.</param>
    /// <param name="ipAddress">Specific IP address to advertise. If null, automatically detects via the Dns library.</param>
    public void Start(PortalWinConfig config, string mode = "pair", string? ipAddress = null)
    {
        if (_isRunning && _currentMode == mode) return; // Avoid redundant restarts
        if (_isRunning) Stop();
        _currentMode = mode;

        try
        {
            var port = config.Port;
            var hostName = Dns.GetHostName();
            var instanceName = $"PortalWin-{config.HostId}";

            _profile = new ServiceProfile(instanceName, ServiceType, (ushort)port);
            _profile.AddProperty("mode", mode);
            _profile.AddProperty("version", "1");
            _profile.AddProperty("hostname", hostName);
            _profile.AddProperty("port", port.ToString());

            // NEW: Add ALL physical IP addresses to the profile to improve discovery
            // Filter out virtual and vEthernet adapters to avoid "wrong IP" issues
            var allIps = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => IsAdapterEligible(ni, config.VpnCompatibilityModeEnabled))
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Select(ua => ua.Address)
                .ToList();

            foreach (var ip in allIps.Where(i => i.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
            {
                _profile.Resources.Add(new ARecord { Name = _profile.HostName, Address = ip });
            }
            foreach (var ip in allIps.Where(i => i.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6))
            {
                _profile.Resources.Add(new AAAARecord { Name = _profile.HostName, Address = ip });
            }

            if (!string.IsNullOrEmpty(ipAddress))
            {
                _profile.AddProperty("ip", ipAddress);
                _profile.AddProperty("preferred_ip", ipAddress);
            }

            Logger.Log($"[MdnsAnnouncer] {_profile.ToString()} {_profile.InstanceName.ToString()} {_profile.Domain.ToString()} {_profile.QualifiedServiceName.ToString()} {_profile.HostName.ToString()}");

            // Advertise all paired client IDs so mobile apps can match
            var clientIds = string.Join(",", config.Devices.Select(d => d.ClientId));
            if (!string.IsNullOrEmpty(clientIds))
                _profile.AddProperty("clients", clientIds);

            // If a specific IP is provided, log it but keep others for reliability
            if (!string.IsNullOrEmpty(ipAddress))
            {
                Logger.Log($"[MdnsAnnouncer] Preferred IP provided: {ipAddress}");
            }
            else
            {
                foreach (var r in _profile.Resources.OfType<AddressRecord>())
                {
                    Logger.Log($"[MdnsAnnouncer] Auto-detected address record: {r.Name} -> {r.Address}");
                }
            }

            // Инициализируем сервис, передавая ему функцию фильтрации интерфейсов
            _mdns = new MulticastService(interfaces =>
            {
                // Prefer active physical, non-loopback interfaces
                var preferred = interfaces.Where(ni =>
                    IsAdapterEligible(ni, config.VpnCompatibilityModeEnabled)).ToList();

                if (!string.IsNullOrEmpty(ipAddress) && IPAddress.TryParse(ipAddress, out var targetIp))
                {
                    var targetNi = preferred.FirstOrDefault(ni =>
                        ni.GetIPProperties().UnicastAddresses.Any(ua => ua.Address.Equals(targetIp)));

                    if (targetNi != null)
                    {
                        Logger.Log($"[MdnsAnnouncer] Selected exact interface {targetNi.Name} for IP {targetIp}");
                        return new[] { targetNi };
                    }
                }

                if (preferred.Count == 0)
                {
                    Logger.Log("[MdnsAnnouncer] Внимание: не найдено физических активных сетевых адаптеров, используем все доступные.");
                    return interfaces;
                }

                foreach (var ni in preferred)
                {
                    Logger.Log($"[MdnsAnnouncer] Выбран адаптер для mDNS: {ni.Name} ({ni.Description})");
                }

                return preferred;
            });

            _mdns.UseIpv4 = UseIpv4;
            _mdns.UseIpv6 = UseIpv6;

            Logger.Log($"[MdnsAnnouncer] {_mdns}");

            // Log incoming queries for diagnostics
            _mdns.QueryReceived += (s, e) =>
            {
                foreach (var q in e.Message.Questions)
                {
                    Logger.Log($"[MdnsAnnouncer] Query received: {q.Name} ({q.Type})");
                }
            };

            // Log answers we send
            _mdns.AnswerReceived += (s, e) =>
            {
                foreach (var a in e.Message.Answers)
                {
                    Logger.Log($"[MdnsAnnouncer] Answer sent: {a.Name} ({a.Type})");
                }
            };

            // Pass the managed MulticastService to ServiceDiscovery
            _sd = new ServiceDiscovery(_mdns);
            _sd.Advertise(_profile);

            // Start listening and responding to mDNS queries
            _mdns.Start();

            _isRunning = true;
            Logger.Log($"[MdnsAnnouncer] Advertising '{instanceName}' as {ServiceType} on port {port} (mode={mode})");
        }
        catch (Exception ex)
        {
            Logger.LogError("[MdnsAnnouncer] Failed to start mDNS announcement", ex);
        }
    }

    /// <summary>
    /// Stop announcing the service.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return;

        try
        {
            if (_profile != null)
            {
                _sd?.Unadvertise(_profile);
            }
            _sd?.Dispose();
            _mdns?.Stop();
            _mdns?.Dispose();
            _sd = null;
            _mdns = null;
            _profile = null;
            _isRunning = false;
            _currentMode = null;
            Logger.Log("[MdnsAnnouncer] mDNS announcement stopped.");
        }
        catch (Exception ex)
        {
            Logger.LogError("[MdnsAnnouncer] Error stopping mDNS", ex);
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private static bool IsAdapterEligible(NetworkInterface ni, bool vpnCompatibilityModeEnabled)
    {
        if (ni.OperationalStatus != OperationalStatus.Up ||
            ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        {
            return false;
        }

        if (!vpnCompatibilityModeEnabled)
        {
            return true;
        }

        if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        return !IsVirtualOrVpnInterface(ni);
    }

    private static bool IsVirtualOrVpnInterface(NetworkInterface ni)
    {
        return ContainsAny(ni.Name, ni.Description, ni.Id,
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

}
