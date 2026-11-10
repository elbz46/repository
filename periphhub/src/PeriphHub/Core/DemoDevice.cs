namespace PeriphHub.Core;

/// <summary>Simulated device used with --demo to preview / test the UI without hardware.</summary>
public sealed class DemoDevice : ManagedDevice
{
    public DemoDevice(string name, DeviceKind kind) : base($"demo:{name}", name, "Demo", kind)
    {
        Transport = "Simulated";
        BatteryPercent = 76;
        Notice = "Demo device – nothing is sent to hardware.";
        Task Nop<T>(T _) => Task.CompletedTask;
        Add("Pointer", new RangeSetting("dpi", "Pointer speed (DPI)", 200, 4000, 50, 1000, Nop) { Unit = "DPI", Description = "Supported range 200–4000 DPI." });
        Add("Scrolling", new ChoiceSetting("wheel", "Scroll wheel mode", new[] { new ChoiceOption("smart", "SmartShift"), new ChoiceOption("free", "Free-spin") }, "smart", Nop));
        Add("Scrolling", new ToggleSetting("hires", "Smooth (high-resolution) scrolling", true, Nop));
        var map = Add("Buttons", new ButtonMapSetting("buttons", "Button assignments", () => Task.CompletedTask) { Description = "Per-application profiles supported." });
        var gestures = new[] { new ActionDef("device:gestures", "Gestures (hold and move)", "Device") };
        map.DefineButton("cid:00C3", "Gesture button (thumb)", gestures);
        map.DefineButton("cid:0053", "Back button");
        map.DefineButton("cid:0056", "Forward button");
        map.SetDefault("cid:00C3", new ActionSpec { Id = "device:gestures" });
        map.SetDefault("cid:0053", new ActionSpec { Id = "key", Arg = "Ctrl+Shift+T" });
        Add("Lighting", new ColorSetting("color", "Lighting colour", "#00B4FF", Nop));
        var eq = Add("Audio", new EqualizerSetting("eq", "Equalizer", new[] { "80", "240", "750", "2.2k", "6.6k" }, -12, 12, Nop));
        eq.Preset("Bass boost");
        Add("Audio", new CommandSetting("presets", "Equalizer presets", EqualizerSetting.PresetNames.Select(n => new CommandItem(n, () => { eq.Preset(n); return Task.CompletedTask; })).ToArray()));
        Add("Device", new InfoSetting("diag.features", "HID++ features", "[01] 0x0001  Feature set\n[02] 0x0003  Device information"));
        Add("Pointer", new TextSetting("dpi.cycle", "DPI levels for “Cycle DPI” button", "800, 1600, 3200", Nop) { Hint = "Comma separated" });
    }
}
