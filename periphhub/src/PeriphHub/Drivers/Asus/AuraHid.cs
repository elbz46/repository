using HidSharp;
using PeriphHub.Core;

namespace PeriphHub.Drivers.Asus;

/// <summary>ASUS Aura keyboards that sit on USB (VID 0B05, vendor page 0xFF31, feature report 0x5D).</summary>
public sealed class AuraHid
{
    private const byte Id = 0x5D;
    private readonly HidDevice _device;

    private AuraHid(HidDevice d) { _device = d; }

    public static AuraHid? Find()
    {
        foreach (var d in DeviceList.Local.GetHidDevices(0x0B05))
        {
            try
            {
                if (d.GetMaxFeatureReportLength() < 16) continue;
                var desc = d.GetReportDescriptor();
                if (desc.DeviceItems.Any(i => i.Usages.GetAllValues().Any(u => (u >> 16) == 0xFF31)) &&
                    desc.FeatureReports.Any(r => r.ReportID == Id))
                    return new AuraHid(d);
            }
            catch { }
        }
        return null;
    }

    public string Description => $"USB Aura controller {_device.ProductID:X4}";

    private void Write(params byte[][] messages)
    {
        using var s = _device.Open();
        var len = _device.GetMaxFeatureReportLength();
        foreach (var m in messages)
        {
            var buf = new byte[len];
            Array.Copy(m, buf, Math.Min(m.Length, len));
            s.SetFeature(buf);
        }
    }

    public void Init() => Write(new byte[] { Id, 0xB9 }, "]ASUS Tech.Inc."u8.ToArray(), new byte[] { Id, 0x05, 0x20, 0x31, 0, 0x1A });

    public void SetMode(byte mode, byte r, byte g, byte b, byte speed)
    {
        var msg = new byte[17];
        msg[0] = Id; msg[1] = 0xB3; msg[2] = 0; msg[3] = mode;
        msg[4] = r; msg[5] = g; msg[6] = b; msg[7] = speed; msg[8] = 0;
        msg[9] = (byte)(mode == 1 ? 1 : 0);
        Write(msg, new byte[] { Id, 0xB5, 0, 0, 0 }, new byte[] { Id, 0xB4 });
    }

    public void SetBrightness(int level) => Write(new byte[] { Id, 0xBA, 0xC5, 0xC4, (byte)Math.Clamp(level, 0, 3) });

    public static bool Present()
    {
        try { return Find() != null; } catch (Exception ex) { Log.Warn($"Aura HID probe: {ex.Message}"); return false; }
    }
}
