using HidSharp;
using PeriphHub.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace PeriphHub.Drivers.Generic;

/// <summary>
/// Picks up everything that has no dedicated driver: Bluetooth devices (battery via the standard
/// Battery Service / Windows battery property) and known-vendor USB HID devices such as MSI keyboards.
/// </summary>
public sealed class GenericProvider : IDeviceProvider
{
    private const string BtBatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private static readonly Dictionary<int, string> Vendors = new()
    {
        [0x0DB0] = "MSI", [0x1462] = "MSI", [0x1532] = "Razer", [0x1B1C] = "Corsair", [0x1038] = "SteelSeries",
        [0x0951] = "HyperX", [0x03F0] = "HyperX", [0x3434] = "Keychron", [0x258A] = "Generic (Sinowealth)",
    };

    public string Name => "Generic";

    public async Task ScanAsync(DeviceHub hub)
    {
        await ScanUsbAsync(hub);
        try { await ScanBluetoothAsync(hub); }
        catch (Exception ex) { Log.Warn($"Bluetooth scan failed: {ex.Message}"); }
    }

    private static Task ScanUsbAsync(DeviceHub hub)
    {
        var byProduct = DeviceList.Local.GetHidDevices()
            .Where(d => Vendors.ContainsKey(d.VendorID))
            .GroupBy(d => (d.VendorID, d.ProductID));
        foreach (var g in byProduct)
        {
            var first = g.First();
            string name;
            try { name = first.GetProductName(); } catch { name = $"{Vendors[first.VendorID]} device"; }
            if (string.IsNullOrWhiteSpace(name)) name = $"{Vendors[first.VendorID]} device {first.ProductID:X4}";
            var vendor = Vendors[first.VendorID];
            var kind = GuessKind(name, g);
            var key = $"hid:{first.VendorID:X4}:{first.ProductID:X4}";
            var path = first.DevicePath.ToLowerInvariant();
            var bt = path.Contains("00001124") || path.Contains("00001812");
            var dev = FindByName(hub, vendor, name) ?? hub.Find<GenericDevice>(key);
            if (dev == null)
            {
                dev = new GenericDevice(key, name, vendor, kind);
                hub.Add(dev);
                _ = dev.ApplySavedAsync();
            }
            dev.IsOnline = true;
            dev.SetConnections(new[] { bt ? "Bluetooth" : "USB" },
                g.Select(d => $"VID {d.VendorID:X4}  PID {d.ProductID:X4}  {SafeUsage(d)}"));
        }
        return Task.CompletedTask;
    }

    private static GenericDevice? FindByName(DeviceHub hub, string vendor, string name)
    {
        lock (hub.Devices)
            return hub.Devices.OfType<GenericDevice>().FirstOrDefault(d => d.Vendor == vendor &&
                string.Equals(Normalize(d.Name), Normalize(name), StringComparison.OrdinalIgnoreCase));
    }

    private static bool HandledElsewhere(DeviceHub hub, string name)
    {
        var n = Normalize(name).ToLowerInvariant();
        if (n.Length < 3) return false;
        List<ManagedDevice> others;
        lock (hub.Devices) others = hub.Devices.Where(d => d is not GenericDevice && d.Vendor != "All keyboards").ToList();
        var match = others.Any(d =>
        {
            var m = Normalize(d.Name).ToLowerInvariant();
            return m.Length >= 3 && (m.Contains(n) || n.Contains(m));
        });
        if (match)
        {
            List<GenericDevice> dupes;
            lock (hub.Devices) dupes = hub.Devices.OfType<GenericDevice>().Where(d => Normalize(d.Name).ToLowerInvariant() == n).ToList();
            foreach (var d in dupes) hub.Remove(d);
        }
        return match;
    }

    private static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    private static string SafeUsage(HidDevice d)
    {
        try
        {
            var u = d.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues()).FirstOrDefault();
            return $"usage {u >> 16:X4}:{u & 0xFFFF:X4}  in {d.GetMaxInputReportLength()} out {d.GetMaxOutputReportLength()} feat {d.GetMaxFeatureReportLength()}";
        }
        catch { return ""; }
    }

    private static DeviceKind GuessKind(string name, IEnumerable<HidDevice>? hids = null)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("keyboard") || n.Contains("kb") || n.Contains("gk") || n.Contains("strike") || n.Contains("vigor") || n.Contains("forge")) return DeviceKind.Keyboard;
        if (n.Contains("mouse") || n.Contains("clutch") || n.Contains(" gm") || n.Contains("mx ")) return DeviceKind.Mouse;
        if (n.Contains("headset") || n.Contains("headphone") || n.Contains("buds") || n.Contains("immerse") || n.Contains("audio")) return DeviceKind.Headset;
        if (hids != null)
        {
            try
            {
                var usages = hids.SelectMany(h => h.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues())).ToHashSet();
                if (usages.Contains(0x00010006)) return DeviceKind.Keyboard;
                if (usages.Contains(0x00010002)) return DeviceKind.Mouse;
            }
            catch { }
        }
        return DeviceKind.Other;
    }

    private static async Task ScanBluetoothAsync(DeviceHub hub)
    {
        // Paired Bluetooth (classic + LE) devices.
        var selectors = new[]
        {
            BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
        };
        foreach (var sel in selectors)
        {
            var found = await DeviceInformation.FindAllAsync(sel, new[] { "System.Devices.Aep.IsConnected", "System.Devices.Aep.DeviceAddress" }, DeviceInformationKind.AssociationEndpoint);
            foreach (var di in found)
            {
                var name = di.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                // Devices that a dedicated driver already handles (e.g. Logitech over HID++) are not duplicated.
                if (HandledElsewhere(hub, name)) continue;
                var kind = GuessKind(name);
                var vendor = name.Contains("MSI", StringComparison.OrdinalIgnoreCase) ? "MSI" : "Bluetooth";
                if (kind == DeviceKind.Other && vendor != "MSI") continue; // phones, speakers, watches…
                var addr = di.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var a) ? a?.ToString() : di.Id;
                var connected = di.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is true;
                var key = $"bt:{addr}";
                var dev = FindByName(hub, vendor, name) ?? hub.Find<GenericDevice>(key);
                if (dev == null)
                {
                    dev = new GenericDevice(key, name, vendor, kind);
                    hub.Add(dev);
                    _ = dev.ApplySavedAsync();
                }
                var isLe = sel == selectors[1];
                var id = di.Id;
                dev.SetConnections(new[] { "Bluetooth" }, new[] { $"Bluetooth {(isLe ? "LE" : "classic")} {addr}" });
                dev.BatteryReader = () => ReadBatteryAsync(id, isLe, name);
                await dev.PollAsync();
                if (!connected) dev.IsOnline = false;
            }
        }
    }

    private static async Task<(int?, bool)> ReadBatteryAsync(string id, bool isLe, string name)
    {
        if (isLe)
        {
            using var le = await BluetoothLEDevice.FromIdAsync(id);
            if (le == null) return (null, false);
            var online = le.ConnectionStatus == BluetoothConnectionStatus.Connected;
            if (!online) return (null, false);
            var svc = await le.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
            foreach (var s in svc.Services)
            {
                using (s)
                {
                    var chars = await s.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel);
                    foreach (var ch in chars.Characteristics)
                    {
                        var r = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
                        if (r.Status == GattCommunicationStatus.Success && r.Value.Length > 0)
                        {
                            var reader = Windows.Storage.Streams.DataReader.FromBuffer(r.Value);
                            return (reader.ReadByte(), true);
                        }
                    }
                }
            }
            return (null, true);
        }
        // Classic Bluetooth (HFP headsets etc.): Windows exposes the battery as a device property.
        var nodes = await DeviceInformation.FindAllAsync($"System.ItemNameDisplay:=\"{name.Replace("\"", "")}\"",
            new[] { BtBatteryKey, "System.Devices.Connected" }, DeviceInformationKind.Device);
        foreach (var n in nodes)
            if (n.Properties.TryGetValue(BtBatteryKey, out var v) && v is byte b)
                return (b, true);
        using var classic = await BluetoothDevice.FromIdAsync(id);
        return (null, classic?.ConnectionStatus == BluetoothConnectionStatus.Connected);
    }

    public void Dispose() { }
}
