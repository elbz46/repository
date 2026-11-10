using System.Management;
using PeriphHub.Core;

namespace PeriphHub.Drivers.Asus;

public sealed class AsusLaptopDevice : ManagedDevice
{
    private readonly AtkAcpi? _acpi;
    private readonly AuraHid? _aura;
    private readonly bool _tuf;
    private readonly InfoSetting? _fans;
    private ColorSetting? _color;
    private ChoiceSetting? _mode;
    private ChoiceSetting? _speed;

    public string Model { get; }

    public AsusLaptopDevice(string model, AtkAcpi? acpi, AuraHid? aura)
        : base($"asus:laptop:{model}", model, "ASUS", DeviceKind.Laptop)
    {
        Model = model; _acpi = acpi; _aura = aura;
        _tuf = model.Contains("TUF", StringComparison.OrdinalIgnoreCase);
        Transport = "Built-in";

        Add("Laptop", new InfoSetting("info.model", "Model detected", model)
        { Description = "This is your exact model number — it was read from the BIOS, so you don't have to look for the sticker." });
        Add("Laptop", new InfoSetting("info.bios", "BIOS", Wmi("Win32_BIOS", "SMBIOSBIOSVersion") ?? "?"));

        BuildLighting();
        if (_acpi != null) BuildPerformance();
        if (_acpi != null && (_acpi.IsSupported(AtkAcpi.CpuFan) || _acpi.IsSupported(AtkAcpi.CpuTemp)))
            _fans = Add("Performance", new InfoSetting("perf.fans", "Fans & temperatures"));

        if (_acpi == null)
            Notice = "The ASUS System Control Interface driver was not found. Install “ASUS System Control Interface” " +
                     "(from MyASUS or the support page for your model) to unlock keyboard lighting, fan modes and battery care.";
    }

    public static string? Wmi(string cls, string prop)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}");
            foreach (var o in s.Get()) return o[prop]?.ToString()?.Trim();
        }
        catch { }
        return null;
    }

    // ---------------------------------------------------------------- keyboard lighting
    private void BuildLighting()
    {
        bool acpiRgb = _acpi != null && _tuf;
        if (!acpiRgb && _aura == null)
        {
            if (_acpi != null && _acpi.IsSupported(AtkAcpi.KeyboardBacklight))
                AddBrightness();
            return;
        }

        var modes = new List<ChoiceOption>
        {
            new("0", "Static"), new("1", "Breathing"), new("2", "Colour cycle"),
        };
        modes.Add(acpiRgb ? new ChoiceOption("10", "Strobing") : new ChoiceOption("3", "Rainbow"));

        _mode = Add("Keyboard lighting", new ChoiceSetting("kb.mode", "Effect", modes, "0", _ => ApplyRgbAsync()));
        _color = Add("Keyboard lighting", new ColorSetting("kb.color", "Colour", "#FF0000", _ => ApplyRgbAsync())
        { Description = "Colour cycle / rainbow ignore the colour." });
        _speed = Add("Keyboard lighting", new ChoiceSetting("kb.speed", "Effect speed", new[]
        {
            new ChoiceOption("225", "Slow"), new ChoiceOption("235", "Medium"), new ChoiceOption("245", "Fast"),
        }, "235", _ => ApplyRgbAsync()));
        AddBrightness();

        if (acpiRgb)
        {
            var awake = Add("Keyboard lighting", new ToggleSetting("kb.awake", "Lights on while the laptop is in use", true, _ => ApplyPowerAsync()));
            Add("Keyboard lighting", new ToggleSetting("kb.boot", "Lights on during boot", true, _ => ApplyPowerAsync()));
            Add("Keyboard lighting", new ToggleSetting("kb.sleep", "Lights on while asleep", false, _ => ApplyPowerAsync()));
            Add("Keyboard lighting", new ToggleSetting("kb.shutdown", "Lights on after shutdown", false, _ => ApplyPowerAsync()));
            _ = awake;
        }
    }

    private void AddBrightness()
    {
        Add("Keyboard lighting", new ChoiceSetting("kb.brightness", "Backlight brightness", new[]
        {
            new ChoiceOption("0", "Off"), new ChoiceOption("1", "Low"), new ChoiceOption("2", "Medium"), new ChoiceOption("3", "High"),
        }, "3", v => Task.Run(() =>
        {
            int level = int.Parse(v);
            if (_acpi != null) _acpi.Set(AtkAcpi.TufKeyboardBrightness, (uint)(0x80 | level));
            else _aura?.SetBrightness(level);
        })));
    }

    private Task ApplyRgbAsync() => Task.Run(() =>
    {
        if (_mode == null || _color == null || _speed == null) return;
        var (r, g, b) = ColorSetting.Parse(_color.Value);
        var mode = byte.Parse(_mode.Value);
        var speed = byte.Parse(_speed.Value);
        if (_acpi != null && _tuf)
        {
            var p = new byte[] { 0xB4, mode, r, g, b, speed };
            if (_acpi.Set(AtkAcpi.TufKeyboard, p) != 1) _acpi.Set(AtkAcpi.TufKeyboard2, p);
        }
        else if (_aura != null)
        {
            _aura.Init();
            _aura.SetMode(mode, r, g, b, speed);
        }
    });

    private Task ApplyPowerAsync() => Task.Run(() =>
    {
        if (_acpi == null) return;
        bool Get(string id) => AllSettings.OfType<ToggleSetting>().FirstOrDefault(s => s.Id == id)?.Value ?? false;
        uint state = 0xBD;
        if (Get("kb.boot")) state |= 1 << 13;
        if (Get("kb.awake")) state |= 1 << 11;
        if (Get("kb.sleep")) state |= 1 << 9;
        if (Get("kb.shutdown")) state |= 1 << 15;
        state |= 1 << 16;
        _acpi.Set(AtkAcpi.TufKeyboardState, state);
    });

    // ---------------------------------------------------------------- performance & battery
    private void BuildPerformance()
    {
        var acpi = _acpi!;
        uint perfId = acpi.IsSupported(AtkAcpi.PerformanceMode) || !acpi.IsSupported(AtkAcpi.VivoPerformanceMode)
            ? AtkAcpi.PerformanceMode : AtkAcpi.VivoPerformanceMode;
        Add("Performance", new ChoiceSetting("perf.mode", "Operating mode (fans & power)", new[]
        {
            new ChoiceOption("2", "Silent"), new ChoiceOption("0", "Performance (balanced)"), new ChoiceOption("1", "Turbo"),
        }, "0", v => Task.Run(() => acpi.Set(perfId, uint.Parse(v))))
        { Description = "Same as Fn+F5 / Armoury Crate operating modes." });

        if (acpi.IsSupported(AtkAcpi.BatteryLimit))
        {
            Add("Battery", new RangeSetting("battery.limit", "Stop charging at", 40, 100, 5, 100,
                v => Task.Run(() => acpi.Set(AtkAcpi.BatteryLimit, (uint)v)))
            { Unit = "%", Description = "Battery care: 60–80% greatly extends battery life if you are mostly plugged in." });
        }
        if (acpi.IsSupported(AtkAcpi.PanelOverdrive))
        {
            Add("Display", new ToggleSetting("display.od", "Panel overdrive (less ghosting)", acpi.Get(AtkAcpi.PanelOverdrive) == 1,
                v => Task.Run(() => acpi.Set(AtkAcpi.PanelOverdrive, v ? 1u : 0u))));
        }
        if (acpi.IsSupported(AtkAcpi.GpuEco))
        {
            Add("Graphics", new ToggleSetting("gpu.eco", "Eco mode (turn the NVIDIA/AMD GPU off)", acpi.Get(AtkAcpi.GpuEco) == 1,
                v => Task.Run(() => acpi.Set(AtkAcpi.GpuEco, v ? 1u : 0u)))
            { Persist = false, Description = "Saves battery. Close games / apps using the dedicated GPU first." });
        }
    }

    public override Task PollAsync() => Task.Run(() =>
    {
        if (_fans == null || _acpi == null) return;
        try
        {
            var parts = new List<string>();
            int cf = _acpi.Get(AtkAcpi.CpuFan), gf = _acpi.Get(AtkAcpi.GpuFan);
            int ct = _acpi.Get(AtkAcpi.CpuTemp), gt = _acpi.Get(AtkAcpi.GpuTemp);
            if (cf >= 0) parts.Add($"CPU fan {cf * 100} rpm");
            if (gf >= 0) parts.Add($"GPU fan {gf * 100} rpm");
            if (ct > 0 && ct < 120) parts.Add($"CPU {ct} °C");
            if (gt > 0 && gt < 120) parts.Add($"GPU {gt} °C");
            _fans.Text = parts.Count == 0 ? "Not reported by this model" : string.Join("   ·   ", parts);
        }
        catch (Exception ex) { _fans.Text = ex.Message; }
    });

    public override void Dispose() => _acpi?.Dispose();
}
