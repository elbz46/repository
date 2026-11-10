namespace PeriphHub.Drivers.Logitech;

public static class Feature
{
    public const ushort Root = 0x0000, FeatureSet = 0x0001, DeviceInfo = 0x0003, DeviceName = 0x0005,
        Reset = 0x0020, BatteryStatus = 0x1000, BatteryVoltage = 0x1001, UnifiedBattery = 0x1004,
        ChangeHost = 0x1814, ReprogControlsV4 = 0x1B04, WirelessStatus = 0x1D4B, AdcMeasurement = 0x1F20,
        SmartShift = 0x2110, SmartShiftEnhanced = 0x2111, HiResWheel = 0x2121, Thumbwheel = 0x2150,
        AdjustableDpi = 0x2201, PointerSpeed = 0x2205, FnInversion = 0x40A0, FnInversion2 = 0x40A2,
        ColorLedEffects = 0x8070, RgbEffects = 0x8071, OnboardProfiles = 0x8100, ReportRate = 0x8060,
        Sidetone = 0x8300, Equalizer = 0x8310;

    public static string Name(ushort id) => id switch
    {
        0x0000 => "Root", 0x0001 => "Feature set", 0x0002 => "Feature info", 0x0003 => "Device information",
        0x0005 => "Device name & type", 0x0007 => "Device friendly name", 0x0020 => "Reset", 0x00C2 => "DFU control",
        0x00D0 => "DFU", 0x1000 => "Battery status", 0x1001 => "Battery voltage", 0x1004 => "Unified battery",
        0x1814 => "Change host (Easy-Switch)", 0x1815 => "Hosts info", 0x1B04 => "Reprogrammable controls v4",
        0x1D4B => "Wireless device status", 0x1F20 => "ADC measurement (battery)", 0x1E00 => "Enable hidden features",
        0x2110 => "SmartShift", 0x2111 => "SmartShift (enhanced)", 0x2121 => "Hi-res wheel", 0x2150 => "Thumb wheel",
        0x2201 => "Adjustable DPI", 0x2205 => "Pointer speed", 0x2250 => "Analysis mode", 0x40A0 => "Fn inversion",
        0x40A2 => "Fn inversion (default state)", 0x40A3 => "Fn inversion (multi-host)", 0x4521 => "Disable keys",
        0x4530 => "Dual platform", 0x8060 => "Report rate", 0x8070 => "Color LED effects", 0x8071 => "RGB effects",
        0x8100 => "Onboard profiles", 0x8110 => "Mouse button spy", 0x8300 => "Sidetone", 0x8310 => "Equalizer",
        _ => $"0x{id:X4}",
    };
}

/// <summary>A HID++ 2.0 device reachable at (channel, device index).</summary>
public sealed class HidppDevice
{
    private readonly Dictionary<ushort, (byte index, byte version)> _features = new();

    public HidppDevice(HidppChannel channel, byte index) { Channel = channel; Index = index; }

    public HidppChannel Channel { get; }
    public byte Index { get; }
    public int ProtocolMajor { get; private set; }
    public IReadOnlyDictionary<ushort, (byte index, byte version)> Features => _features;

    public bool Has(ushort feature) => _features.ContainsKey(feature);
    public byte FeatureIndex(ushort feature) => _features.TryGetValue(feature, out var f) ? f.index : (byte)0;
    public ushort FeatureAt(byte index) => _features.FirstOrDefault(kv => kv.Value.index == index).Key;

    public async Task<byte[]> CallAsync(ushort feature, byte function, params byte[] args)
    {
        if (!_features.TryGetValue(feature, out var f))
            throw new HidppException($"Feature {Feature.Name(feature)} not supported");
        return await Channel.CallAsync(Index, f.index, function, args);
    }

    /// <summary>Root.ping: returns false when nothing (or only a HID++ 1.0 device) answers at this index.</summary>
    public async Task<bool> PingAsync(int timeoutMs = 800)
    {
        try
        {
            var r = await Channel.CallAsync(Index, 0x00, 0x1, new byte[] { 0, 0, 0x5A }, timeoutMs);
            ProtocolMajor = r[0];
            return r[0] >= 2;
        }
        catch (HidppException ex) when (ex.Code == 0x01) // HID++ 1.0 "invalid sub-id" => HID++ 1.0 device
        {
            ProtocolMajor = 1;
            return false;
        }
        catch (HidppException) { return false; }
    }

    public async Task DiscoverAsync()
    {
        _features.Clear();
        _features[Feature.Root] = (0, 0);
        var fs = await Channel.CallAsync(Index, 0x00, 0x0, new byte[] { 0x00, 0x01 });
        if (fs[0] == 0) return;
        _features[Feature.FeatureSet] = (fs[0], fs[2]);
        var count = (await CallAsync(Feature.FeatureSet, 0x0))[0];
        for (byte i = 1; i <= count; i++)
        {
            var r = await CallAsync(Feature.FeatureSet, 0x1, i);
            var id = (ushort)((r[0] << 8) | r[1]);
            _features[id] = (i, r[3]);
        }
    }

    public async Task<string?> GetNameAsync()
    {
        if (!Has(Feature.DeviceName)) return null;
        var len = (await CallAsync(Feature.DeviceName, 0x0))[0];
        var chars = new List<byte>();
        while (chars.Count < len)
        {
            var chunk = await CallAsync(Feature.DeviceName, 0x1, (byte)chars.Count);
            var take = Math.Min(16, len - chars.Count);
            chars.AddRange(chunk.Take(take));
        }
        return System.Text.Encoding.UTF8.GetString(chars.ToArray()).TrimEnd('\0').Trim();
    }

    /// <summary>0 keyboard, 3 mouse, 4 trackpad, 5 trackball, 8 headset... (-1 unknown)</summary>
    public async Task<int> GetDeviceTypeAsync()
    {
        if (!Has(Feature.DeviceName)) return -1;
        try { return (await CallAsync(Feature.DeviceName, 0x2))[0]; } catch { return -1; }
    }

    public async Task<(string unitId, string modelId)?> GetIdentityAsync()
    {
        if (!Has(Feature.DeviceInfo)) return null;
        var r = await CallAsync(Feature.DeviceInfo, 0x0);
        var unit = Convert.ToHexString(r, 1, 4);
        var model = Convert.ToHexString(r, 7, 6);
        return (unit, model);
    }

    public async Task<string?> GetFirmwareAsync()
    {
        if (!Has(Feature.DeviceInfo)) return null;
        var count = (await CallAsync(Feature.DeviceInfo, 0x0))[0];
        var parts = new List<string>();
        for (byte e = 0; e < count; e++)
        {
            var r = await CallAsync(Feature.DeviceInfo, 0x1, e);
            var type = r[0] switch { 0 => "FW", 1 => "Bootloader", 2 => "HW", 3 => "Touchpad", 4 => "Optical sensor", 5 => "Soft device", 6 => "RF", _ => "Other" };
            if (r[0] == 2) { parts.Add($"HW {r[1]}"); continue; }
            var prefix = System.Text.Encoding.ASCII.GetString(r, 1, 3).Trim('\0');
            parts.Add($"{type} {prefix} {r[4]:X2}.{r[5]:X2}.B{r[6]:X2}{r[7]:X2}");
        }
        return string.Join("  ·  ", parts);
    }
}
