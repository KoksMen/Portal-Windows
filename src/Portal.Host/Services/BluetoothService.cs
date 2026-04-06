using System;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Portal.Common;

namespace Portal.Host.Services;

public class BluetoothService
{
    /// <summary>
    /// Get the local Bluetooth adapter address (MAC) for display in pairing UI.
    /// </summary>
    public async Task<string?> GetLocalBluetoothAddressAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter == null)
            {
                Logger.Log("[BluetoothService] No Bluetooth adapter found.");
                return null;
            }

            var address = adapter.BluetoothAddress;
            // Convert ulong to MAC string AA:BB:CC:DD:EE:FF
            var bytes = BitConverter.GetBytes(address);
            var mac = $"{bytes[5]:X2}:{bytes[4]:X2}:{bytes[3]:X2}:{bytes[2]:X2}:{bytes[1]:X2}:{bytes[0]:X2}";
            Logger.Log($"[BluetoothService] Local BT Address: {mac}");
            return mac;
        }
        catch (Exception ex)
        {
            Logger.LogError("[BluetoothService] Failed to get BT address", ex);
            return null;
        }
    }
}
