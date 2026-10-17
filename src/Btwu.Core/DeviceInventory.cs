using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Btwu.Core;

public sealed record DeviceItem(string Name, string Id, string Kind, bool Connected, DeviceIdentity Identity)
{
    public string ToLine(int index)
    {
        var cls = Identity.KindText;
        var flag = Identity.Kind switch
        {
            HidKind.Keyboard => " *",   // usable as a trigger with the default gate
            HidKind.None => "  ",
            _ => " ~",                  // HID, but not a keyboard
        };
        return $"[{index,2}] {(Connected ? "connected   " : "disconnected")} {Kind,-7} {cls,-8}{flag} {Name}";
    }
}

public static class DeviceInventory
{
    /// <summary>
    /// Every paired Bluetooth device, BLE and classic (BR/EDR), deduplicated by device id.
    ///
    /// Both paths matter: a keyboard may be HID-over-GATT (instance id <c>BTHLE\DEV_*</c>) or classic
    /// HID (<c>BTHENUM\*</c>), and only <see cref="BluetoothLEDevice"/> vs <see cref="BluetoothDevice"/>
    /// can report the connection status of each. Classic-only APIs return nothing for BLE peripherals.
    /// </summary>
    public static async Task<List<DeviceItem>> ListAsync()
    {
        var result = new List<DeviceItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)))
        {
            if (!seen.Add(info.Id)) continue;
            var status = BluetoothConnectionStatus.Disconnected;
            try
            {
                var dev = await BluetoothLEDevice.FromIdAsync(info.Id);
                if (dev is not null)
                {
                    status = dev.ConnectionStatus;
                    dev.Dispose();
                }
            }
            catch (Exception ex) { Log.Warn($"probe {info.Name} failed: {ex.Message}"); }
            result.Add(new DeviceItem(info.Name, info.Id, "BLE", status == BluetoothConnectionStatus.Connected,
                                      HidGate.Probe(info.Id)));
        }

        foreach (var info in await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)))
        {
            if (!seen.Add(info.Id)) continue;
            var status = BluetoothConnectionStatus.Disconnected;
            try
            {
                var dev = await BluetoothDevice.FromIdAsync(info.Id);
                if (dev is not null)
                {
                    status = dev.ConnectionStatus;
                    dev.Dispose();
                }
            }
            catch (Exception ex) { Log.Warn($"probe {info.Name} failed: {ex.Message}"); }
            result.Add(new DeviceItem(info.Name, info.Id, "Classic", status == BluetoothConnectionStatus.Connected,
                                      HidGate.Probe(info.Id)));
        }

        return result.OrderByDescending(d => d.Connected).ThenBy(d => d.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Resolves a device selector given either a list index, a name fragment or an id fragment.</summary>
    public static async Task<DeviceItem?> ResolveAsync(string selector)
    {
        var all = await ListAsync();
        if (int.TryParse(selector, out var index) && index >= 0 && index < all.Count) return all[index];
        return all.FirstOrDefault(d => d.Name.Contains(selector, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(d => d.Id.Contains(selector, StringComparison.OrdinalIgnoreCase));
    }
}
