using System.Collections.ObjectModel;

namespace PeriphHub.Core;

public sealed class MappableButton : Observable
{
    private readonly ButtonMapSetting _owner;
    internal bool Loading;

    public MappableButton(ButtonMapSetting owner, string id, string name, IReadOnlyList<ActionDef> choices)
    {
        _owner = owner; Id = id; Name = name; Choices = choices;
        _selected = choices[0];
    }

    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<ActionDef> Choices { get; }

    private ActionDef _selected;
    public ActionDef Selected
    {
        get => _selected;
        set
        {
            if (value == null || !Set(ref _selected, value)) return;
            Raise(nameof(NeedsArg)); Raise(nameof(ArgHint));
            if (!Loading) _owner.ButtonEdited(this);
        }
    }

    private string _arg = "";
    public string Arg
    {
        get => _arg;
        set { if (Set(ref _arg, value ?? "") && !Loading) _owner.ButtonEdited(this); }
    }

    public bool NeedsArg => _selected.NeedsArg;
    public string? ArgHint => _selected.ArgHint;

    internal ActionSpec ToSpec() => new() { Id = _selected.Id, Arg = _selected.NeedsArg ? _arg : null };
}

/// <summary>
/// Button assignments with optional per-application overrides ("*" = all applications).
/// </summary>
public sealed class ButtonMapSetting : DeviceSetting
{
    public const string AllApps = "*";
    public const string Inherit = "inherit";
    private static readonly ActionDef InheritDef = new(Inherit, "Same as “All applications”", "General");

    private readonly List<(string id, string name, IReadOnlyList<ActionDef> extra)> _defs = new();
    private Dictionary<string, Dictionary<string, ActionSpec>> _map = new() { [AllApps] = new() };
    private readonly Func<Task> _onChanged;

    public ButtonMapSetting(string id, string label, Func<Task> onChanged) : base(id, label)
    {
        _onChanged = onChanged;
        Profiles.Add(AllApps);
    }

    public ObservableCollection<string> Profiles { get; } = new();
    public ObservableCollection<MappableButton> Buttons { get; } = new();

    private string _profile = AllApps;
    public string CurrentProfile
    {
        get => _profile;
        set { if (value != null && Set(ref _profile, value)) { RebuildButtons(); Raise(nameof(CanRemoveProfile)); } }
    }
    public bool CanRemoveProfile => _profile != AllApps;

    public void DefineButton(string id, string name, IReadOnlyList<ActionDef>? extraActions = null)
    {
        _defs.Add((id, name, extraActions ?? Array.Empty<ActionDef>()));
        RebuildButtons();
    }

    public void SetDefault(string buttonId, ActionSpec spec)
    {
        if (!_map[AllApps].ContainsKey(buttonId)) _map[AllApps][buttonId] = spec;
        RebuildButtons();
    }

    private void RebuildButtons()
    {
        void Do()
        {
            Buttons.Clear();
            var profile = _map.GetValueOrDefault(_profile) ?? new();
            foreach (var (id, name, extra) in _defs)
            {
                var choices = new List<ActionDef>();
                if (_profile != AllApps) choices.Add(InheritDef);
                choices.AddRange(extra);
                choices.AddRange(ActionCatalog.Common);
                var b = new MappableButton(this, id, name, choices) { Loading = true };
                var spec = profile.GetValueOrDefault(id) ?? (_profile == AllApps ? ActionSpec.Default : new ActionSpec { Id = Inherit });
                b.Selected = choices.FirstOrDefault(c => c.Id == spec.Id) ?? choices[0];
                b.Arg = spec.Arg ?? "";
                b.Loading = false;
                Buttons.Add(b);
            }
        }
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) Do(); else d.Invoke(Do);
    }

    internal async void ButtonEdited(MappableButton b)
    {
        if (!_map.TryGetValue(_profile, out var profile)) _map[_profile] = profile = new();
        var spec = b.ToSpec();
        if (spec.Id == Inherit) profile.Remove(b.Id); else profile[b.Id] = spec;
        await SaveAndNotify();
    }

    public void AddProfile(string exe)
    {
        exe = exe.Trim().ToLowerInvariant();
        if (exe.Length == 0) return;
        if (!exe.EndsWith(".exe")) exe += ".exe";
        if (!_map.ContainsKey(exe)) _map[exe] = new();
        if (!Profiles.Contains(exe)) Profiles.Add(exe);
        CurrentProfile = exe;
        _ = SaveAndNotify();
    }

    public void RemoveCurrentProfile()
    {
        if (_profile == AllApps) return;
        _map.Remove(_profile);
        Profiles.Remove(_profile);
        CurrentProfile = AllApps;
        _ = SaveAndNotify();
    }

    private async Task SaveAndNotify()
    {
        if (Owner != null) SettingsStore.Instance.SetDevice(Owner.Key, Id, _map);
        try { await _onChanged(); Error = null; }
        catch (Exception ex) { Error = ex.Message; Log.Error("Updating button assignments failed", ex); }
    }

    /// <summary>The action for a button right now, taking the foreground application into account.</summary>
    public ActionSpec Resolve(string buttonId)
    {
        var app = ForegroundApp.ProcessName;
        if (app.Length > 0 && _map.TryGetValue(app, out var p) && p.TryGetValue(buttonId, out var s)) return s;
        return _map[AllApps].GetValueOrDefault(buttonId) ?? ActionSpec.Default;
    }

    /// <summary>True if any profile assigns something non-default (so the device must hand the button to us).</summary>
    public bool IsCustomisedAnywhere(string buttonId) =>
        _map.Values.Any(p => p.TryGetValue(buttonId, out var s) && !s.IsDefault);

    public bool UsesAction(string buttonId, string actionId) =>
        _map.Values.Any(p => p.TryGetValue(buttonId, out var s) && s.Id == actionId);

    public override async Task ApplySavedAsync()
    {
        if (Owner != null && SettingsStore.Instance.HasDevice(Owner.Key, Id))
        {
            var saved = SettingsStore.Instance.GetDevice<Dictionary<string, Dictionary<string, ActionSpec>>>(Owner.Key, Id);
            if (saved != null)
            {
                if (!saved.ContainsKey(AllApps)) saved[AllApps] = new();
                // keep built-in defaults for buttons the user never touched
                foreach (var kv in _map[AllApps]) saved[AllApps].TryAdd(kv.Key, kv.Value);
                _map = saved;
                void Sync()
                {
                    foreach (var p in _map.Keys) if (!Profiles.Contains(p)) Profiles.Add(p);
                }
                var d = System.Windows.Application.Current?.Dispatcher;
                if (d == null || d.CheckAccess()) Sync(); else d.Invoke(Sync);
                RebuildButtons();
            }
        }
        try { await _onChanged(); Error = null; }
        catch (Exception ex) { Error = ex.Message; Log.Error("Restoring button assignments failed", ex); }
    }
}
