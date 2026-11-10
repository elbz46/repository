using PeriphHub.Core;

namespace PeriphHub.Drivers.Logitech;

/// <summary>
/// A Logitech HID++ 2.0 device (mouse, keyboard or headset). The UI is built from the features the
/// device reports, so unknown/new models get every capability this app knows how to drive.
/// </summary>
public sealed class LogitechDevice : ManagedDevice, IDeviceActionHandler
{
    private HidppDevice _dev;
    private bool _built;
    private readonly SemaphoreSlim _attachLock = new(1, 1);

    // Buttons & gestures
    private ButtonMapSetting? _buttons;
    private readonly Dictionary<ushort, ControlInfo> _controls = new();
    private readonly Dictionary<ushort, byte> _divertState = new();
    private readonly HashSet<ushort> _pressed = new();
    private readonly Dictionary<ushort, ActionSpec> _activeActions = new();
    private ushort _gestureCid;
    private int _gx, _gy;
    private bool _gestureActive;

    // DPI
    private RangeSetting? _dpi;
    private List<int> _dpiValues = new();
    private TextSetting? _dpiCycle;
    private RangeSetting? _dpiShift;
    private int _dpiCycleIndex;
    private bool _dpiShifted;

    // Wheel
    private ChoiceSetting? _wheelMode;
    private RangeSetting? _smartShiftSensitivity;
    private bool _smartShiftEnhanced;
    private bool _wheelFree;

    // Thumb wheel
    private ChoiceSetting? _thumbAction;
    private RangeSetting? _thumbSensitivity;
    private ToggleSetting? _thumbInvert;
    private int _thumbAccum;

    private InfoSetting? _features;
    private InfoSetting? _hostInfo;

    public LogitechDevice(string key, string name, DeviceKind kind, HidppDevice dev)
        : base(key, name, "Logitech", kind)
    {
        _dev = dev;
        Transport = dev.Channel.TransportName;
    }

    public HidppDevice Endpoint => _dev;

    public static async Task<(string key, string name, DeviceKind kind)> IdentifyAsync(HidppDevice dev)
    {
        var name = await dev.GetNameAsync() ?? $"Logitech device {dev.Channel.ProductId:X4}";
        var type = await dev.GetDeviceTypeAsync();
        var kind = type switch
        {
            0 or 2 => DeviceKind.Keyboard,
            3 or 4 or 5 => DeviceKind.Mouse,
            8 or 15 => DeviceKind.Headset,
            _ => dev.Has(Feature.Sidetone) || dev.Has(Feature.Equalizer) ? DeviceKind.Headset :
                 dev.Has(Feature.AdjustableDpi) ? DeviceKind.Mouse : DeviceKind.Other,
        };
        string key;
        try
        {
            var id = await dev.GetIdentityAsync();
            key = id is { } v && v.unitId != "00000000" ? $"logitech:{v.unitId}" : $"logitech:name:{name}";
        }
        catch { key = $"logitech:name:{name}"; }
        return (key, name, kind);
    }

    /// <summary>Called when the device shows up on any transport (first time, after reconnect, or after switching USB/Bluetooth).</summary>
    public async Task AttachAsync(HidppDevice dev)
    {
        await _attachLock.WaitAsync();
        try
        {
            _dev = dev;
            Transport = dev.Channel.TransportName;
            IsOnline = true;
            _divertState.Clear();
            _pressed.Clear();
            if (!_built)
            {
                await BuildAsync();
                _built = true;
            }
            await ApplySavedAsync();
            await RefreshFromDeviceAsync();
            await PollAsync();
            Log.Info($"{Name} ready via {Transport}");
        }
        finally { _attachLock.Release(); }
    }

    public void MarkOffline() { IsOnline = false; _pressed.Clear(); }

    private async Task BuildAsync()
    {
        var d = _dev;
        Notice = "Close Logitech Options+ / G HUB while using PeriphHub — they fight over the same device settings.";

        // ---- Information
        var info = Add("Device", new InfoSetting("info.model", "Model", Name));
        try { if (await d.GetFirmwareAsync() is { } fw) Add("Device", new InfoSetting("info.fw", "Firmware", fw)); } catch { }
        try { if (await d.GetIdentityAsync() is { } id) Add("Device", new InfoSetting("info.unit", "Unit ID / model ID", $"{id.unitId} / {id.modelId}")); } catch { }

        // ---- Pointer / DPI
        if (d.Has(Feature.AdjustableDpi)) await BuildDpiAsync();
        if (d.Has(Feature.ReportRate)) await BuildReportRateAsync();
        if (d.Has(Feature.OnboardProfiles))
        {
            Add("Pointer", new ChoiceSetting("onboard.mode", "Profile mode",
                new[] { new ChoiceOption("2", "Software (settings from this app)"), new ChoiceOption("1", "Onboard memory") }, "2",
                async v => await _dev.CallAsync(Feature.OnboardProfiles, 0x1, byte.Parse(v)))
            { Description = "Software mode is required for DPI and button changes from this app to take effect on G-series mice." });
        }

        // ---- Scrolling
        if (d.Has(Feature.SmartShift) || d.Has(Feature.SmartShiftEnhanced)) BuildSmartShift();
        if (d.Has(Feature.HiResWheel)) BuildHiResWheel();
        if (d.Has(Feature.Thumbwheel)) BuildThumbwheel();

        // ---- Buttons
        if (d.Has(Feature.ReprogControlsV4)) await BuildButtonsAsync();

        // ---- Keyboard
        if (d.Has(Feature.FnInversion) || d.Has(Feature.FnInversion2))
        {
            var feat = d.Has(Feature.FnInversion) ? Feature.FnInversion : Feature.FnInversion2;
            Add("Keyboard", new ToggleSetting("kb.fnswap", "Use F1–F12 as standard function keys", false,
                async v => await _dev.CallAsync(feat, 0x1, (byte)(v ? 1 : 0)))
            { Description = "Off = media / special keys by default, hold Fn for F1–F12." });
        }

        // ---- Easy-Switch
        if (d.Has(Feature.ChangeHost)) await BuildChangeHostAsync();

        // ---- Audio (headsets)
        if (d.Has(Feature.Sidetone))
        {
            int cur = 0;
            try { cur = (await d.CallAsync(Feature.Sidetone, 0x0))[0]; } catch { }
            Add("Audio", new RangeSetting("audio.sidetone", "Mic monitoring (sidetone)", 0, 100, 1, cur,
                async v => await _dev.CallAsync(Feature.Sidetone, 0x1, (byte)v)) { Unit = "%",
                Description = "How much of your own voice you hear in the headset." });
        }
        if (d.Has(Feature.Equalizer)) await BuildEqualizerAsync();

        // ---- Lighting
        if (d.Has(Feature.ColorLedEffects)) await BuildLightingAsync();

        // ---- Diagnostics
        _features = Add("Diagnostics", new InfoSetting("diag.features", "HID++ features reported by the device"));
        _features.Text = string.Join("\n", d.Features.OrderBy(f => f.Value.index)
            .Select(f => $"[{f.Value.index:D2}] 0x{f.Key:X4}  {Feature.Name(f.Key)}  v{f.Value.version}"));
        _ = info;
    }

    // ------------------------------------------------------------------ DPI
    private async Task BuildDpiAsync()
    {
        var list = await _dev.CallAsync(Feature.AdjustableDpi, 0x1, 0);
        var values = new List<int>();
        int last = 0;
        for (int i = 1; i + 1 < list.Length; i += 2)
        {
            int v = (list[i] << 8) | list[i + 1];
            if (v == 0) break;
            if (v >= 0xE000)
            {
                int step = v - 0xE000;
                if (i + 3 >= list.Length) break;
                int max = (list[i + 2] << 8) | list[i + 3];
                for (int x = last + step; x <= max && step > 0; x += step) values.Add(x);
                last = max; i += 2;
                continue;
            }
            values.Add(v); last = v;
        }
        // The list can continue in a second call for devices with many values.
        values = values.Distinct().OrderBy(x => x).ToList();
        if (values.Count == 0) return;
        _dpiValues = values;

        var cur = await _dev.CallAsync(Feature.AdjustableDpi, 0x2, 0);
        int dpi = (cur[1] << 8) | cur[2];
        if (dpi == 0) dpi = (cur[3] << 8) | cur[4];

        _dpi = Add("Pointer", new RangeSetting("dpi", "Pointer speed (DPI)", values.First(), values.Last(),
            values.Count > 1 ? values[1] - values[0] : 50, dpi, SetDpiAsync)
        { Unit = "DPI", AllowedValues = values, Description = $"Supported range {values.First()}–{values.Last()} DPI." });

        var defaults = new[] { 400, 800, 1200, 1600, 2400, 3200 }.Where(v => v >= values.First() && v <= values.Last()).Select(v => SnapDpi(v)).Distinct();
        _dpiCycle = Add("Pointer", new TextSetting("dpi.cycle", "DPI levels for “Cycle DPI” button", string.Join(", ", defaults), _ => Task.CompletedTask)
        { Hint = "Comma separated, e.g. 800, 1600, 3200" });
        _dpiShift = Add("Pointer", new RangeSetting("dpi.shift", "DPI while “DPI shift / sniper” button is held", values.First(), values.Last(),
            values.Count > 1 ? values[1] - values[0] : 50, SnapDpi(400), _ => Task.CompletedTask) { Unit = "DPI", AllowedValues = values });
    }

    private int SnapDpi(int v) => _dpiValues.Count == 0 ? v : _dpiValues.MinBy(x => Math.Abs(x - v));

    private async Task SetDpiAsync(int dpi)
    {
        dpi = SnapDpi(dpi);
        await _dev.CallAsync(Feature.AdjustableDpi, 0x3, 0, (byte)(dpi >> 8), (byte)dpi);
    }

    private async Task BuildReportRateAsync()
    {
        var r = await _dev.CallAsync(Feature.ReportRate, 0x0);
        var opts = new List<ChoiceOption>();
        for (int bit = 0; bit < 8; bit++)
            if ((r[0] & (1 << bit)) != 0)
                opts.Add(new ChoiceOption((bit + 1).ToString(), $"{1000 / (bit + 1)} Hz ({bit + 1} ms)"));
        if (opts.Count < 2) return;
        var cur = (await _dev.CallAsync(Feature.ReportRate, 0x1))[0];
        Add("Pointer", new ChoiceSetting("reportrate", "Polling rate", opts, cur.ToString(),
            async v => await _dev.CallAsync(Feature.ReportRate, 0x2, byte.Parse(v))));
    }

    // ------------------------------------------------------------------ SmartShift / wheels
    private void BuildSmartShift()
    {
        _smartShiftEnhanced = !_dev.Has(Feature.SmartShift);
        _wheelMode = Add("Scrolling", new ChoiceSetting("wheel.mode", "Scroll wheel mode", new[]
        {
            new ChoiceOption("smart", "SmartShift (auto free-spin when you flick)"),
            new ChoiceOption("ratchet", "Always ratchet (click-to-click)"),
            new ChoiceOption("free", "Always free-spin"),
        }, "smart", _ => ApplyWheelModeAsync()));
        _smartShiftSensitivity = Add("Scrolling", new RangeSetting("wheel.smartshift", "SmartShift sensitivity", 1, 50, 1, 12,
            _ => ApplyWheelModeAsync()) { Description = "Lower = switches to free-spin with a gentler flick." });
    }

    private async Task ApplyWheelModeAsync()
    {
        if (_wheelMode == null) return;
        var feat = _smartShiftEnhanced ? Feature.SmartShiftEnhanced : Feature.SmartShift;
        byte setFn = (byte)(_smartShiftEnhanced ? 0x2 : 0x1);
        var threshold = (byte)Math.Clamp(_smartShiftSensitivity?.Value ?? 12, 1, 50);
        byte[] args = _wheelMode.Value switch
        {
            "free" => new byte[] { 1, 0, 0 },
            "ratchet" => new byte[] { 2, 0xFF, 0 },
            _ => new byte[] { 2, threshold, 0 },
        };
        _wheelFree = _wheelMode.Value == "free";
        if (_smartShiftSensitivity != null) _smartShiftSensitivity.IsEnabled = _wheelMode.Value == "smart";
        await _dev.CallAsync(feat, setFn, args);
    }

    private void BuildHiResWheel()
    {
        Add("Scrolling", new ToggleSetting("wheel.hires", "Smooth (high-resolution) scrolling", true, async v =>
        {
            var mode = (await _dev.CallAsync(Feature.HiResWheel, 0x1))[0];
            mode = (byte)(v ? (mode | 0x02) : (mode & ~0x02));
            await _dev.CallAsync(Feature.HiResWheel, 0x2, (byte)(mode & ~0x01));
        }));
        Add("Scrolling", new ToggleSetting("wheel.invert", "Natural scrolling (invert wheel direction)", false, async v =>
        {
            var mode = (await _dev.CallAsync(Feature.HiResWheel, 0x1))[0];
            mode = (byte)(v ? (mode | 0x04) : (mode & ~0x04));
            await _dev.CallAsync(Feature.HiResWheel, 0x2, (byte)(mode & ~0x01));
        }));
    }

    private void BuildThumbwheel()
    {
        _thumbAction = Add("Scrolling", new ChoiceSetting("thumb.action", "Thumb wheel does", new[]
        {
            new ChoiceOption("default", "Horizontal scroll (default)"),
            new ChoiceOption("hscroll", "Horizontal scroll (custom speed)"),
            new ChoiceOption("volume", "Volume up / down"),
            new ChoiceOption("zoom", "Zoom in / out"),
            new ChoiceOption("tabs", "Switch browser / editor tabs"),
            new ChoiceOption("desktops", "Switch virtual desktops"),
            new ChoiceOption("apps", "Switch apps (Alt+Tab)"),
            new ChoiceOption("none", "Disabled"),
        }, "default", _ => ApplyThumbwheelAsync()));
        _thumbSensitivity = Add("Scrolling", new RangeSetting("thumb.speed", "Thumb wheel speed", 1, 10, 1, 5, _ => Task.CompletedTask));
        _thumbInvert = Add("Scrolling", new ToggleSetting("thumb.invert", "Invert thumb wheel direction", false, _ => ApplyThumbwheelAsync()));
    }

    private async Task ApplyThumbwheelAsync()
    {
        if (_thumbAction == null) return;
        var divert = _thumbAction.Value != "default";
        var invert = _thumbInvert?.Value == true;
        await _dev.CallAsync(Feature.Thumbwheel, 0x2, (byte)(divert ? 1 : 0), (byte)(invert ? 1 : 0));
    }

    // ------------------------------------------------------------------ Buttons
    private async Task BuildButtonsAsync()
    {
        var count = (await _dev.CallAsync(Feature.ReprogControlsV4, 0x0))[0];
        for (byte i = 0; i < count; i++)
        {
            var r = await _dev.CallAsync(Feature.ReprogControlsV4, 0x1, i);
            var ci = new ControlInfo((ushort)((r[0] << 8) | r[1]), (ushort)((r[2] << 8) | r[3]), r[4], r[8], r[5], r[6]);
            _controls[ci.Cid] = ci;
        }

        _buttons = Add("Buttons", new ButtonMapSetting("buttons", "Button assignments", UpdateDiversionsAsync)
        { Description = "Choose “All applications” or add an app (e.g. chrome.exe) to give buttons different jobs per program." });

        var deviceActions = new List<ActionDef>();
        if (_dpi != null)
        {
            deviceActions.Add(new ActionDef("device:dpi-cycle", "Cycle DPI levels", "Device"));
            deviceActions.Add(new ActionDef("device:dpi-shift", "DPI shift / sniper (hold)", "Device"));
        }
        if (_wheelMode != null) deviceActions.Add(new ActionDef("device:wheel-toggle", "Toggle ratchet / free-spin", "Device"));
        if (_dev.Has(Feature.ChangeHost)) deviceActions.Add(new ActionDef("device:host-next", "Switch to next Easy-Switch computer", "Device"));

        bool anyGesture = false;
        foreach (var c in _controls.Values.Where(c => c.Divertable && c.Cid != 0x0050 && !c.Virtual).OrderBy(c => c.Position == 0 ? 99 : c.Position))
        {
            var extra = new List<ActionDef>(deviceActions);
            if (c.RawXY) { extra.Insert(0, new ActionDef("device:gestures", "Gestures (hold and move)", "Device")); anyGesture = true; }
            _buttons.DefineButton(c.ButtonId, c.Name, extra);
        }
        if (anyGesture)
        {
            foreach (var (id, label) in new[] { ("gesture:click", "Gesture: press & release"), ("gesture:up", "Gesture: hold + move up"),
                         ("gesture:down", "Gesture: hold + move down"), ("gesture:left", "Gesture: hold + move left"), ("gesture:right", "Gesture: hold + move right") })
                _buttons.DefineButton(id, label);
            var g = _controls.Values.FirstOrDefault(c => c.Cid == 0x00C3 && c.RawXY);
            if (g != null) _buttons.SetDefault(g.ButtonId, new ActionSpec { Id = "device:gestures" });
            _buttons.SetDefault("gesture:click", new ActionSpec { Id = "win:taskview" });
            _buttons.SetDefault("gesture:up", new ActionSpec { Id = "win:maximize" });
            _buttons.SetDefault("gesture:down", new ActionSpec { Id = "win:desktop" });
            _buttons.SetDefault("gesture:left", new ActionSpec { Id = "win:deskleft" });
            _buttons.SetDefault("gesture:right", new ActionSpec { Id = "win:deskright" });
        }
    }

    private async Task UpdateDiversionsAsync()
    {
        if (_buttons == null || !IsOnline) return;
        foreach (var c in _controls.Values.Where(c => c.Divertable))
        {
            bool divert = _buttons.IsCustomisedAnywhere(c.ButtonId);
            bool raw = c.RawXY && divert && _buttons.UsesAction(c.ButtonId, "device:gestures");
            byte flags = (byte)(divert ? 0x03 : 0x02);
            if (c.RawXY) flags |= (byte)(raw ? 0x30 : 0x20);
            if (_divertState.TryGetValue(c.Cid, out var prev) && prev == flags) continue;
            await _dev.CallAsync(Feature.ReprogControlsV4, 0x3, (byte)(c.Cid >> 8), (byte)c.Cid, flags, 0, 0);
            _divertState[c.Cid] = flags;
        }
    }

    // ------------------------------------------------------------------ Easy-Switch
    private async Task BuildChangeHostAsync()
    {
        var r = await _dev.CallAsync(Feature.ChangeHost, 0x0);
        int hosts = r[0];
        _hostInfo = Add("Easy-Switch", new InfoSetting("host.current", "Connected computer", $"Channel {r[1] + 1} of {hosts}"));
        var cmds = Enumerable.Range(0, hosts).Select(h => new CommandItem($"Switch to {h + 1}", async () =>
        {
            // The device leaves this computer straight away, so a missing answer is expected.
            try { await _dev.Channel.CallAsync(_dev.Index, _dev.FeatureIndex(Feature.ChangeHost), 0x1, new[] { (byte)h }, 300); }
            catch (HidppException) { }
        })).ToArray();
        Add("Easy-Switch", new CommandSetting("host.switch", "Move this device to another computer", cmds)
        { Description = "The device leaves this PC immediately (like pressing its Easy-Switch button)." });
    }

    // ------------------------------------------------------------------ Equalizer
    private async Task BuildEqualizerAsync()
    {
        var info = await _dev.CallAsync(Feature.Equalizer, 0x0);
        int bands = info[0];
        int range = info[1];
        int min = (sbyte)info[3], max = (sbyte)info[4];
        if (min == 0 && max == 0) { min = -range; max = range; }
        if (bands == 0 || bands > 16) return;
        var labels = new List<string>();
        for (int i = 0; i < bands; i += 7)
        {
            var f = await _dev.CallAsync(Feature.Equalizer, 0x1, (byte)i);
            for (int j = 0; j < 7 && i + j < bands; j++)
            {
                int hz = (f[1 + 2 * j] << 8) | f[2 + 2 * j];
                labels.Add(hz >= 1000 ? $"{hz / 1000.0:0.#}k" : hz.ToString());
            }
        }
        var gains = await _dev.CallAsync(Feature.Equalizer, 0x2);
        var initial = Enumerable.Range(0, bands).Select(i => (int)(sbyte)gains[1 + i]).ToArray();
        var eq = Add("Audio", new EqualizerSetting("audio.eq", "Equalizer", labels, min, max, async g =>
        {
            var args = new byte[1 + g.Length];
            args[0] = 0x02;
            for (int i = 0; i < g.Length; i++) args[1 + i] = (byte)(sbyte)Math.Clamp(g[i], min, max);
            await _dev.CallAsync(Feature.Equalizer, 0x3, args);
        }));
        eq.SetFromDevice(initial);
        Add("Audio", new CommandSetting("audio.eqpresets", "Equalizer presets",
            EqualizerSetting.PresetNames.Select(n => new CommandItem(n, () => { eq.Preset(n); return Task.CompletedTask; })).ToArray()));
    }

    // ------------------------------------------------------------------ Lighting (0x8070)
    private async Task BuildLightingAsync()
    {
        var info = await _dev.CallAsync(Feature.ColorLedEffects, 0x0);
        int zones = info[0];
        if (zones == 0) return;
        // Map effect ids -> per-zone effect index.
        var effectIndex = new Dictionary<(int zone, int effectId), byte>();
        for (byte z = 0; z < zones; z++)
        {
            var zi = await _dev.CallAsync(Feature.ColorLedEffects, 0x1, z);
            int effects = zi[3];
            for (byte e = 0; e < effects; e++)
            {
                var ei = await _dev.CallAsync(Feature.ColorLedEffects, 0x2, z, e);
                effectIndex[(z, (ei[2] << 8) | ei[3])] = e;
            }
        }
        ChoiceSetting? modeS = null;
        ColorSetting? colorS = null;

        async Task Apply()
        {
            var (r, g, b) = ColorSetting.Parse(colorS?.Value ?? "#00B4FF");
            int effectId = int.Parse(modeS?.Value ?? "1");
            for (byte z = 0; z < zones; z++)
            {
                if (!effectIndex.TryGetValue((z, effectId), out var idx)) continue;
                var p = new byte[13];
                p[0] = z; p[1] = idx;
                switch (effectId)
                {
                    case 1: p[2] = r; p[3] = g; p[4] = b; break;
                    case 10: p[2] = r; p[3] = g; p[4] = b; p[5] = 0x0B; p[6] = 0xB8; p[8] = 100; break; // 3 s period
                    case 3: p[7] = 0x27; p[8] = 0x10; p[9] = 100; break;                                    // 10 s cycle
                }
                p[12] = 1; // persist
                await _dev.CallAsync(Feature.ColorLedEffects, 0x3, p);
            }
        }

        modeS = Add("Lighting", new ChoiceSetting("light.mode", "Lighting effect", new[]
        {
            new ChoiceOption("1", "Static colour"), new ChoiceOption("10", "Breathing"),
            new ChoiceOption("3", "Colour cycle"), new ChoiceOption("0", "Off"),
        }, "1", _ => Apply()));
        colorS = Add("Lighting", new ColorSetting("light.color", "Lighting colour", "#00B4FF", _ => Apply()));
    }

    // ------------------------------------------------------------------ Refresh & battery
    private async Task RefreshFromDeviceAsync()
    {
        try
        {
            if (_dpi != null && !SettingsStore.Instance.HasDevice(Key, "dpi"))
            {
                var cur = await _dev.CallAsync(Feature.AdjustableDpi, 0x2, 0);
                _dpi.SetFromDevice((cur[1] << 8) | cur[2]);
            }
            if (_wheelMode != null && !SettingsStore.Instance.HasDevice(Key, "wheel.mode"))
            {
                var feat = _smartShiftEnhanced ? Feature.SmartShiftEnhanced : Feature.SmartShift;
                var r = await _dev.CallAsync(feat, (byte)(_smartShiftEnhanced ? 0x1 : 0x0));
                _wheelFree = r[0] == 1;
                _wheelMode.SetFromDevice(r[0] == 1 ? "free" : r[1] == 0xFF ? "ratchet" : "smart");
                if (r[1] is > 0 and <= 50) _smartShiftSensitivity?.SetFromDevice(r[1]);
            }
            if (_hostInfo != null)
            {
                var r = await _dev.CallAsync(Feature.ChangeHost, 0x0);
                _hostInfo.Text = $"Channel {r[1] + 1} of {r[0]}";
            }
        }
        catch (Exception ex) { Log.Warn($"{Name}: refresh failed: {ex.Message}"); }
    }

    public override async Task PollAsync()
    {
        if (!IsOnline) return;
        try
        {
            if (_dev.Has(Feature.UnifiedBattery))
            {
                var r = await _dev.CallAsync(Feature.UnifiedBattery, 0x1);
                BatteryPercent = r[0] > 0 ? r[0] : r[1] switch { >= 8 => 90, >= 4 => 50, >= 2 => 20, _ => 5 };
                Charging = r[2] is 1 or 2;
            }
            else if (_dev.Has(Feature.BatteryStatus))
            {
                var r = await _dev.CallAsync(Feature.BatteryStatus, 0x0);
                BatteryPercent = r[0];
                Charging = r[2] is 1 or 2 or 4;
            }
            else if (_dev.Has(Feature.BatteryVoltage))
            {
                var r = await _dev.CallAsync(Feature.BatteryVoltage, 0x0);
                BatteryPercent = VoltageToPercent((r[0] << 8) | r[1]);
                Charging = (r[2] & 0x80) != 0;
            }
            else if (_dev.Has(Feature.AdcMeasurement))
            {
                var r = await _dev.CallAsync(Feature.AdcMeasurement, 0x0);
                int mv = (r[0] << 8) | r[1];
                if ((r[2] & 0x01) == 0 || mv == 0) { BatteryPercent = null; return; }
                BatteryPercent = VoltageToPercent(mv);
                Charging = (r[2] & 0x02) != 0;
            }
        }
        catch (HidppException ex) when (ex.Code is 0xFE or 0x09 or 0x04)
        {
            MarkOffline();
        }
        catch (Exception ex) { Log.Warn($"{Name}: battery read failed: {ex.Message}"); }
    }

    private static readonly (int mv, int pct)[] Curve =
    {
        (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
        (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3500, 0),
    };

    public static int VoltageToPercent(int mv)
    {
        if (mv >= Curve[0].mv) return 100;
        for (int i = 1; i < Curve.Length; i++)
        {
            if (mv >= Curve[i].mv)
            {
                var (hiMv, hiP) = Curve[i - 1];
                var (loMv, loP) = Curve[i];
                return loP + (int)Math.Round((mv - loMv) * (hiP - loP) / (double)(hiMv - loMv));
            }
        }
        return 0;
    }

    // ------------------------------------------------------------------ Notifications
    public void OnNotification(byte[] r)
    {
        var feat = _dev.FeatureAt(r[2]);
        int fn = r[3] >> 4;
        var p = r[4..];
        switch (feat)
        {
            case Feature.ReprogControlsV4 when fn == 0: OnButtons(p); break;
            case Feature.ReprogControlsV4 when fn == 1:
                if (_gestureActive) { _gx += (short)((p[0] << 8) | p[1]); _gy += (short)((p[2] << 8) | p[3]); }
                break;
            case Feature.Thumbwheel when fn == 0: OnThumbwheel(p); break;
            case Feature.WirelessStatus when fn == 0 && p[0] == 1:
                Log.Info($"{Name} woke up / reconnected – restoring settings");
                _ = AttachAsync(_dev);
                break;
            case Feature.BatteryStatus or Feature.UnifiedBattery when fn == 0:
                _ = PollAsync();
                break;
        }
    }

    private void OnButtons(byte[] p)
    {
        if (_buttons == null) return;
        var now = new HashSet<ushort>();
        for (int i = 0; i + 1 < 8; i += 2)
        {
            var cid = (ushort)((p[i] << 8) | p[i + 1]);
            if (cid != 0) now.Add(cid);
        }
        foreach (var cid in now.Except(_pressed).ToList()) ButtonDown(cid);
        foreach (var cid in _pressed.Except(now).ToList()) ButtonUp(cid);
        _pressed.Clear();
        _pressed.UnionWith(now);
    }

    private void ButtonDown(ushort cid)
    {
        if (!_controls.TryGetValue(cid, out var c) || _buttons == null) return;
        var spec = _buttons.Resolve(c.ButtonId);
        if (spec.Id == "device:gestures")
        {
            _gestureActive = true; _gestureCid = cid; _gx = _gy = 0;
            return;
        }
        _activeActions[cid] = spec;
        ActionRunner.Run(spec, true, this);
    }

    private void ButtonUp(ushort cid)
    {
        if (_gestureActive && cid == _gestureCid && _buttons != null)
        {
            _gestureActive = false;
            const int threshold = 60;
            string dir = Math.Abs(_gx) < threshold && Math.Abs(_gy) < threshold ? "click"
                : Math.Abs(_gx) > Math.Abs(_gy) ? (_gx > 0 ? "right" : "left")
                : (_gy > 0 ? "down" : "up");
            var g = _buttons.Resolve($"gesture:{dir}");
            ActionRunner.Run(g, true, this);
            ActionRunner.Run(g, false, this);
            return;
        }
        if (_activeActions.Remove(cid, out var spec)) ActionRunner.Run(spec, false, this);
    }

    private void OnThumbwheel(byte[] p)
    {
        if (_thumbAction == null) return;
        int rot = (short)((p[0] << 8) | p[1]);
        if (rot == 0) return;
        var mode = _thumbAction.Value;
        int speed = _thumbSensitivity?.Value ?? 5;
        if (mode == "hscroll") { Input.HWheel(rot * speed * 4); return; }
        if (mode == "none") return;
        _thumbAccum += rot;
        int step = Math.Max(2, 26 - speed * 2);
        while (Math.Abs(_thumbAccum) >= step)
        {
            bool up = _thumbAccum > 0;
            _thumbAccum -= up ? step : -step;
            string combo = mode switch
            {
                "volume" => up ? "VolumeUp" : "VolumeDown",
                "zoom" => up ? "Ctrl+Plus" : "Ctrl+Minus",
                "tabs" => up ? "Ctrl+Tab" : "Ctrl+Shift+Tab",
                "desktops" => up ? "Ctrl+Win+Right" : "Ctrl+Win+Left",
                "apps" => up ? "Alt+Tab" : "Alt+Shift+Tab",
                _ => "",
            };
            if (combo.Length > 0) Input.Tap(Input.ParseCombo(combo));
        }
    }

    // ------------------------------------------------------------------ Device actions
    public void DeviceAction(string id, bool pressed)
    {
        switch (id)
        {
            case "device:dpi-cycle" when pressed && _dpi != null:
                var levels = ParseLevels();
                if (levels.Count == 0) return;
                _dpiCycleIndex = (levels.IndexOf(_dpi.Value) + 1) % levels.Count;
                _dpi.Value = levels[_dpiCycleIndex];
                Toast.Show($"{Name}: {_dpi.Value} DPI");
                break;
            case "device:dpi-shift" when _dpi != null && _dpiShift != null:
                if (pressed && !_dpiShifted) { _dpiShifted = true; _ = SetDpiAsync(_dpiShift.Value); }
                else if (!pressed && _dpiShifted) { _dpiShifted = false; _ = SetDpiAsync(_dpi.Value); }
                break;
            case "device:wheel-toggle" when pressed && _wheelMode != null:
                var feat = _smartShiftEnhanced ? Feature.SmartShiftEnhanced : Feature.SmartShift;
                _wheelFree = !_wheelFree;
                _ = _dev.CallAsync(feat, (byte)(_smartShiftEnhanced ? 0x2 : 0x1), (byte)(_wheelFree ? 1 : 2), 0, 0);
                break;
            case "device:host-next" when pressed:
                _ = Task.Run(async () =>
                {
                    var r = await _dev.CallAsync(Feature.ChangeHost, 0x0);
                    var next = (byte)((r[1] + 1) % Math.Max(1, (int)r[0]));
                    try { await _dev.CallAsync(Feature.ChangeHost, 0x1, next); } catch { /* device leaves immediately */ }
                });
                break;
        }
    }

    private List<int> ParseLevels()
    {
        var text = _dpiCycle?.Value ?? "";
        return text.Split(',', ';', ' ').Select(s => int.TryParse(s.Trim(), out var v) ? SnapDpi(v) : -1)
            .Where(v => v > 0).Distinct().ToList();
    }

    public override void Dispose() { }
}
