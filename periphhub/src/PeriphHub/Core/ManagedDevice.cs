using System.Collections.ObjectModel;

namespace PeriphHub.Core;

public enum DeviceKind { Mouse, Keyboard, Headset, Laptop, Receiver, Other }

/// <summary>A physical device as shown in the app, independent of how it is currently connected.</summary>
public abstract class ManagedDevice : Observable, IDisposable
{
    protected ManagedDevice(string key, string name, string vendor, DeviceKind kind)
    {
        Key = key; _name = name; Vendor = vendor; Kind = kind;
    }

    /// <summary>Stable identity used for saved settings (same over USB and Bluetooth).</summary>
    public string Key { get; }
    public string Vendor { get; }
    public DeviceKind Kind { get; protected set; }

    private string _name;
    public string Name { get => _name; protected set => Set(ref _name, value); }

    private string _transport = "";
    public string Transport { get => _transport; set => Set(ref _transport, value); }

    private bool _online = true;
    public bool IsOnline { get => _online; set { if (Set(ref _online, value)) Raise(nameof(StatusLine)); } }

    private int? _battery;
    public int? BatteryPercent { get => _battery; set { if (Set(ref _battery, value)) Raise(nameof(StatusLine)); } }

    private bool _charging;
    public bool Charging { get => _charging; set { if (Set(ref _charging, value)) Raise(nameof(StatusLine)); } }

    public string StatusLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Transport)) parts.Add(Transport);
            if (!IsOnline) parts.Add("asleep / out of range");
            else if (BatteryPercent is int b) parts.Add(Charging ? $"{b}% ⚡" : $"{b}%");
            else if (Charging) parts.Add("charging ⚡");
            return string.Join(" · ", parts);
        }
    }

    public virtual string Glyph => Kind switch
    {
        DeviceKind.Mouse => "",
        DeviceKind.Keyboard => "",
        DeviceKind.Headset => "",
        DeviceKind.Laptop => "",
        DeviceKind.Receiver => "",
        _ => "",
    };

    public ObservableCollection<SettingGroup> Groups { get; } = new();

    /// <summary>Banner shown at the top of the device page (limitations, tips).</summary>
    private string? _notice;
    public string? Notice { get => _notice; set => Set(ref _notice, value); }

    protected SettingGroup Group(string title)
    {
        var g = Groups.FirstOrDefault(x => x.Title == title);
        if (g == null) { g = new SettingGroup(title); OnUi(() => Groups.Add(g)); }
        return g;
    }

    protected T Add<T>(string group, T setting) where T : DeviceSetting
    {
        setting.Owner = this;
        var g = Group(group);
        OnUi(() => g.Settings.Add(setting));
        return setting;
    }

    protected static void OnUi(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a(); else d.Invoke(a);
    }

    public IEnumerable<DeviceSetting> AllSettings => Groups.SelectMany(g => g.Settings);

    /// <summary>Pushes every stored setting to the hardware. Called on first sight and on each reconnect.</summary>
    public virtual async Task ApplySavedAsync()
    {
        foreach (var s in AllSettings.ToList())
            await s.ApplySavedAsync();
    }

    /// <summary>Periodic refresh (battery etc.).</summary>
    public virtual Task PollAsync() => Task.CompletedTask;

    public virtual void Dispose() { }
}
