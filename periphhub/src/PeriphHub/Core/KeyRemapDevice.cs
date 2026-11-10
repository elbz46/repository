namespace PeriphHub.Core;

/// <summary>
/// Remaps keys on every keyboard (laptop keyboard, MSI keyboard, …) to keystrokes, macros, media
/// controls or programs, with per-application profiles.
/// </summary>
public sealed class KeyRemapDevice : ManagedDevice
{
    private readonly KeyboardHook _hook = new();
    private readonly ButtonMapSetting _map;
    private readonly ToggleSetting _enabled;
    private readonly InfoSetting _status;
    private readonly HashSet<int> _held = new();
    private readonly Dictionary<int, ActionSpec> _active = new();
    private List<int> _keys;

    public KeyRemapDevice() : base("global:keys", "Key remapping & macros", "All keyboards", DeviceKind.Keyboard)
    {
        Transport = "Works with every keyboard";
        _keys = SettingsStore.Instance.GetDevice<List<int>>(Key, "keys.list") ?? new List<int>();

        _enabled = Add("Remapping", new ToggleSetting("enabled", "Enable key remapping", true, _ => Task.CompletedTask));
        _status = Add("Remapping", new InfoSetting("status", "Add a key", "Click “Add key”, then press the key you want to change."));
        Add("Remapping", new CommandSetting("add", "Keys",
            new CommandItem("Add key…", StartCaptureAsync),
            new CommandItem("Remove all keys", ClearAsync)));
        _map = Add("Remapping", new ButtonMapSetting("map", "Key assignments", () => Task.CompletedTask)
        { Description = "Tip: F13–F24, Scroll Lock, Pause, Insert, or the Copilot key make great macro keys." });
        foreach (var vk in _keys) _map.DefineButton(Id(vk), Input.VkName(vk));

        _hook.Handler = OnKey;
    }

    public void Start()
    {
        OnUi(_hook.Install);
    }

    private static string Id(int vk) => $"vk:{vk:X2}";

    private Task StartCaptureAsync()
    {
        _status.Text = "Press the key you want to change now…";
        _hook.Capture = vk => OnUi(() =>
        {
            if (!_keys.Contains(vk))
            {
                _keys.Add(vk);
                SettingsStore.Instance.SetDevice(Key, "keys.list", _keys);
                _map.DefineButton(Id(vk), Input.VkName(vk));
            }
            _status.Text = $"Added {Input.VkName(vk)} – choose what it should do below.";
        });
        return Task.CompletedTask;
    }

    private Task ClearAsync()
    {
        _keys = new List<int>();
        SettingsStore.Instance.SetDevice(Key, "keys.list", _keys);
        SettingsStore.Instance.SetDevice(Key, "map", new Dictionary<string, Dictionary<string, ActionSpec>>());
        _status.Text = "All remaps removed. Restart PeriphHub to clear the list.";
        return Task.CompletedTask;
    }

    private bool OnKey(int vk, bool down)
    {
        if (!_enabled.Value || _keys.Count == 0 || !_keys.Contains(vk)) return false;
        if (down)
        {
            var spec = _map.Resolve(Id(vk));
            if (spec.IsDefault) return false;
            bool repeat = !_held.Add(vk);
            if (repeat && spec.Id != "key") return true; // swallow auto-repeat for one-shot actions
            _active[vk] = spec;
            _hook.Enqueue(() => ActionRunner.Run(spec, true));
            return true;
        }
        _held.Remove(vk);
        if (_active.Remove(vk, out var s))
        {
            _hook.Enqueue(() => ActionRunner.Run(s, false));
            return true;
        }
        return false;
    }

    public override void Dispose() => _hook.Dispose();
}
