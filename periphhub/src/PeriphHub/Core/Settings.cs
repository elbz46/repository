using System.Collections.ObjectModel;

namespace PeriphHub.Core;

/// <summary>
/// Every device exposes its capabilities as a list of typed settings. The UI renders them generically,
/// so a newly discovered capability (e.g. a HID++ feature on a friend's mouse) shows up without new UI code.
/// </summary>
public abstract class DeviceSetting : Observable
{
    protected DeviceSetting(string id, string label) { Id = id; Label = label; }

    public string Id { get; }
    public string Label { get; }
    public string? Description { get; init; }
    public bool Persist { get; init; } = true;
    public ManagedDevice? Owner { get; internal set; }

    private string? _error;
    public string? Error { get => _error; protected set { Set(ref _error, value); Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);

    private bool _enabled = true;
    public bool IsEnabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>Re-sends the stored value to the hardware (used on connect / reconnect / transport switch).</summary>
    public virtual Task ApplySavedAsync() => Task.CompletedTask;
}

public abstract class ValueSetting<T> : DeviceSetting
{
    private readonly Func<T, Task> _apply;
    private T _value;
    private bool _suppress;
    private CancellationTokenSource? _debounce;

    protected ValueSetting(string id, string label, T initial, Func<T, Task> apply) : base(id, label)
    {
        _value = initial;
        _apply = apply;
    }

    /// <summary>Milliseconds to wait for the user to stop dragging before talking to the hardware.</summary>
    public int DebounceMs { get; init; }

    public T Value
    {
        get => _value;
        set
        {
            if (EqualityComparer<T>.Default.Equals(_value, value)) return;
            _value = value;
            Raise();
            OnValueChanged();
            if (!_suppress) Commit();
        }
    }

    protected virtual void OnValueChanged() { }

    /// <summary>Update the UI from a value read back from the hardware without writing it again.</summary>
    public void SetFromDevice(T value)
    {
        _suppress = true;
        try { Value = value; } finally { _suppress = false; }
    }

    private async void Commit()
    {
        var value = _value;
        if (Persist && Owner != null) SettingsStore.Instance.SetDevice(Owner.Key, Id, value);
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        try
        {
            if (DebounceMs > 0) await Task.Delay(DebounceMs, cts.Token);
            await _apply(value);
            Error = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error($"{Owner?.Name}: applying {Label} failed", ex);
        }
    }

    public override async Task ApplySavedAsync()
    {
        if (!Persist || Owner == null || !SettingsStore.Instance.HasDevice(Owner.Key, Id)) return;
        var saved = SettingsStore.Instance.GetDevice<T>(Owner.Key, Id);
        if (saved == null) return;
        SetFromDevice(saved);
        try { await _apply(saved); Error = null; }
        catch (Exception ex) { Error = ex.Message; Log.Error($"{Owner.Name}: restoring {Label} failed", ex); }
    }
}

public sealed class ToggleSetting(string id, string label, bool initial, Func<bool, Task> apply)
    : ValueSetting<bool>(id, label, initial, apply);

public sealed class RangeSetting : ValueSetting<int>
{
    public RangeSetting(string id, string label, int min, int max, int step, int initial, Func<int, Task> apply)
        : base(id, label, initial, apply)
    {
        Min = min; Max = max; Step = Math.Max(1, step);
        DebounceMs = 150;
    }
    public int Min { get; }
    public int Max { get; }
    public int Step { get; }
    public string Unit { get; init; } = "";
    /// <summary>When set, only these values are valid; the slider snaps to the nearest one.</summary>
    public IReadOnlyList<int>? AllowedValues { get; init; }

    public int Snap(int v)
    {
        if (AllowedValues is { Count: > 0 } list)
            return list.MinBy(x => Math.Abs(x - v));
        v = Math.Clamp(v, Min, Max);
        return Min + (int)Math.Round((v - Min) / (double)Step) * Step;
    }

    public double SliderValue
    {
        get => Value;
        set { var s = Snap((int)Math.Round(value)); if (s != Value) Value = s; else Raise(); }
    }
    protected override void OnValueChanged() => Raise(nameof(SliderValue));
}

public sealed record ChoiceOption(string Key, string Label);

public sealed class ChoiceSetting(string id, string label, IReadOnlyList<ChoiceOption> options, string initial, Func<string, Task> apply)
    : ValueSetting<string>(id, label, initial, apply)
{
    public IReadOnlyList<ChoiceOption> Options { get; } = options;
}

public sealed class ColorSetting : ValueSetting<string>
{
    public ColorSetting(string id, string label, string initialHex, Func<string, Task> apply)
        : base(id, label, initialHex, apply) { DebounceMs = 120; }

    public static IReadOnlyList<string> Swatches { get; } = new[]
    {
        "#FF0000", "#FF6A00", "#FFD800", "#4CFF00", "#00FF90", "#00FFFF",
        "#0094FF", "#0026FF", "#7F00FF", "#FF00DC", "#FF006E", "#FFFFFF",
    };

    public static (byte r, byte g, byte b) Parse(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return (255, 255, 255);
        return (Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }
}

public sealed class InfoSetting : DeviceSetting
{
    public InfoSetting(string id, string label, string text = "") : base(id, label) { _text = text; Persist = false; }
    private string _text;
    public string Text { get => _text; set => Set(ref _text, value); }
}

public sealed record CommandItem(string Label, Func<Task> Run);

public sealed class CommandSetting : DeviceSetting
{
    public CommandSetting(string id, string label, params CommandItem[] commands) : base(id, label)
    {
        Persist = false;
        Commands = commands;
    }
    public IReadOnlyList<CommandItem> Commands { get; }

    public async Task RunAsync(CommandItem item)
    {
        try { await item.Run(); Error = null; }
        catch (Exception ex) { Error = ex.Message; Log.Error($"{Owner?.Name}: {item.Label} failed", ex); }
    }
}

public sealed class EqBand : Observable
{
    private readonly EqualizerSetting _owner;
    private readonly int _index;
    public EqBand(EqualizerSetting owner, int index, string label) { _owner = owner; _index = index; Label = label; }
    public string Label { get; }
    public int Min => _owner.MinDb;
    public int Max => _owner.MaxDb;
    private int _gain;
    public int Gain
    {
        get => _gain;
        set { if (Set(ref _gain, value)) _owner.BandChanged(_index, value); }
    }
    internal void Load(int g) { _gain = g; Raise(nameof(Gain)); }
}

public sealed class EqualizerSetting : ValueSetting<int[]>
{
    private bool _loading;
    public EqualizerSetting(string id, string label, IReadOnlyList<string> bandLabels, int minDb, int maxDb, Func<int[], Task> apply)
        : base(id, label, new int[bandLabels.Count], apply)
    {
        MinDb = minDb; MaxDb = maxDb; DebounceMs = 200;
        Bands = new ObservableCollection<EqBand>(bandLabels.Select((l, i) => new EqBand(this, i, l)));
    }
    public int MinDb { get; }
    public int MaxDb { get; }
    public ObservableCollection<EqBand> Bands { get; }

    internal void BandChanged(int index, int gain)
    {
        if (_loading) return;
        var copy = Value.ToArray();
        if (index < copy.Length) copy[index] = gain;
        Value = copy;
    }

    protected override void OnValueChanged()
    {
        _loading = true;
        for (var i = 0; i < Bands.Count && i < Value.Length; i++) Bands[i].Load(Value[i]);
        _loading = false;
    }

    public void Preset(string name)
    {
        int n = Bands.Count;
        int[] g = new int[n];
        for (int i = 0; i < n; i++)
        {
            double t = n == 1 ? 0 : i / (double)(n - 1); // 0 = bass, 1 = treble
            g[i] = name switch
            {
                "Bass boost" => (int)Math.Round(MaxDb * 0.6 * Math.Max(0, 1 - t * 2)),
                "Treble boost" => (int)Math.Round(MaxDb * 0.5 * Math.Max(0, t * 2 - 1)),
                "Vocal" => (int)Math.Round(MaxDb * 0.4 * Math.Exp(-Math.Pow((t - 0.55) * 4, 2))),
                "FPS footsteps" => (int)Math.Round(-MaxDb * 0.3 * Math.Max(0, 1 - t * 3) + MaxDb * 0.5 * Math.Exp(-Math.Pow((t - 0.7) * 4, 2))),
                _ => 0,
            };
            g[i] = Math.Clamp(g[i], MinDb, MaxDb);
        }
        Value = g;
    }

    public static IReadOnlyList<string> PresetNames { get; } = new[] { "Flat", "Bass boost", "Treble boost", "Vocal", "FPS footsteps" };
}

public sealed class SettingGroup
{
    public SettingGroup(string title) { Title = title; }
    public string Title { get; }
    public ObservableCollection<DeviceSetting> Settings { get; } = new();
}

public sealed class TextSetting : ValueSetting<string>
{
    public TextSetting(string id, string label, string initial, Func<string, Task> apply)
        : base(id, label, initial, apply) { DebounceMs = 600; }
    public string? Hint { get; init; }
}
