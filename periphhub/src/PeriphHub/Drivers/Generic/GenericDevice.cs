using PeriphHub.Core;

namespace PeriphHub.Drivers.Generic;

/// <summary>Any other mouse / keyboard / headset: battery (Bluetooth) and connection info.</summary>
public sealed class GenericDevice : ManagedDevice
{
    private readonly InfoSetting _conn;

    public GenericDevice(string key, string name, string vendor, DeviceKind kind) : base(key, name, vendor, kind)
    {
        _conn = Add("Device", new InfoSetting("info.conn", "Connections"));
        if (vendor == "MSI")
        {
            Notice = "MSI keyboard detected. Battery level and key remapping / macros (see “Key remapping & macros”) work now. " +
                     "MSI has not published its lighting protocol; per-key RGB for this model needs a protocol capture — " +
                     "see the Diagnostics section below and the README.";
        }
        Add("Diagnostics", new InfoSetting("diag.ids", "Hardware IDs (useful to add full support)"));
    }

    public Func<Task<(int? pct, bool online)>>? BatteryReader { get; set; }

    public void SetConnections(IEnumerable<string> transports, IEnumerable<string> ids)
    {
        var t = transports.Distinct().ToList();
        Transport = string.Join(" + ", t);
        _conn.Text = string.Join("\n", t);
        var diag = AllSettings.OfType<InfoSetting>().First(s => s.Id == "diag.ids");
        diag.Text = string.Join("\n", ids.Distinct());
    }

    public override async Task PollAsync()
    {
        if (BatteryReader == null) return;
        try
        {
            var (pct, online) = await BatteryReader();
            BatteryPercent = pct;
            IsOnline = online;
        }
        catch (Exception ex) { Log.Warn($"{Name}: battery read failed: {ex.Message}"); }
    }
}
