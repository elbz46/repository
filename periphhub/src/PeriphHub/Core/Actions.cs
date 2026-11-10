using System.Diagnostics;

namespace PeriphHub.Core;

/// <summary>What a button / key does. Serialised into settings.json.</summary>
public sealed class ActionSpec
{
    public string Id { get; set; } = "default";
    /// <summary>Keystroke ("Ctrl+C"), text to type, or program path depending on <see cref="Id"/>.</summary>
    public string? Arg { get; set; }

    public static ActionSpec Default => new() { Id = "default" };
    public bool IsDefault => Id == "default";
    public ActionSpec Clone() => new() { Id = Id, Arg = Arg };
    public override string ToString() => Arg == null ? Id : $"{Id}({Arg})";
}

public sealed record ActionDef(string Id, string Label, string Category, string? Combo = null, bool NeedsArg = false, string? ArgHint = null);

/// <summary>Implemented by devices that offer their own actions (DPI cycle, gestures, wheel mode...).</summary>
public interface IDeviceActionHandler
{
    void DeviceAction(string id, bool pressed);
}

public static class ActionCatalog
{
    public static readonly ActionDef Default = new("default", "Default (what the button normally does)", "General");
    public static readonly ActionDef Disabled = new("disabled", "Do nothing (disable)", "General");

    public static IReadOnlyList<ActionDef> Common { get; } = new ActionDef[]
    {
        Default,
        Disabled,
        new("key", "Keystroke…", "General", NeedsArg: true, ArgHint: "e.g. Ctrl+Shift+T, Win+Tab, F13"),
        new("text", "Type text…", "General", NeedsArg: true, ArgHint: "Text to type (\\n for Enter)"),
        new("launch", "Open program / file / website…", "General", NeedsArg: true, ArgHint: @"C:\path\app.exe or https://…"),

        new("mouse:left", "Left click", "Mouse"),
        new("mouse:right", "Right click", "Mouse"),
        new("mouse:middle", "Middle click", "Mouse"),
        new("mouse:back", "Back", "Mouse"),
        new("mouse:forward", "Forward", "Mouse"),
        new("scroll:left", "Scroll left", "Mouse"),
        new("scroll:right", "Scroll right", "Mouse"),

        new("media:playpause", "Play / Pause", "Media", "PlayPause"),
        new("media:next", "Next track", "Media", "NextTrack"),
        new("media:prev", "Previous track", "Media", "PrevTrack"),
        new("media:stop", "Stop", "Media", "Stop"),
        new("media:volup", "Volume up", "Media", "VolumeUp"),
        new("media:voldown", "Volume down", "Media", "VolumeDown"),
        new("media:mute", "Mute", "Media", "Mute"),

        new("win:taskview", "Task View", "Windows", "Win+Tab"),
        new("win:desktop", "Show desktop", "Windows", "Win+D"),
        new("win:deskleft", "Previous virtual desktop", "Windows", "Ctrl+Win+Left"),
        new("win:deskright", "Next virtual desktop", "Windows", "Ctrl+Win+Right"),
        new("win:snip", "Screenshot (Snipping Tool)", "Windows", "Win+Shift+S"),
        new("win:explorer", "File Explorer", "Windows", "Win+E"),
        new("win:search", "Search", "Windows", "Win+S"),
        new("win:action", "Quick settings", "Windows", "Win+A"),
        new("win:notifications", "Notifications", "Windows", "Win+N"),
        new("win:emoji", "Emoji panel", "Windows", "Win+."),
        new("win:lock", "Lock PC", "Windows", "Win+L"),
        new("win:dictate", "Voice typing", "Windows", "Win+H"),
        new("win:alttab", "Switch app (Alt+Tab)", "Windows", "Alt+Tab"),
        new("win:close", "Close window", "Windows", "Alt+F4"),
        new("win:minimize", "Minimize window", "Windows", "Win+Down"),
        new("win:maximize", "Maximize window", "Windows", "Win+Up"),

        new("edit:copy", "Copy", "Editing", "Ctrl+C"),
        new("edit:cut", "Cut", "Editing", "Ctrl+X"),
        new("edit:paste", "Paste", "Editing", "Ctrl+V"),
        new("edit:undo", "Undo", "Editing", "Ctrl+Z"),
        new("edit:redo", "Redo", "Editing", "Ctrl+Y"),
        new("edit:selectall", "Select all", "Editing", "Ctrl+A"),
        new("edit:save", "Save", "Editing", "Ctrl+S"),
        new("edit:find", "Find", "Editing", "Ctrl+F"),

        new("browser:back", "Browser back", "Browser", "Alt+Left"),
        new("browser:forward", "Browser forward", "Browser", "Alt+Right"),
        new("browser:refresh", "Refresh", "Browser", "F5"),
        new("browser:newtab", "New tab", "Browser", "Ctrl+T"),
        new("browser:closetab", "Close tab", "Browser", "Ctrl+W"),
        new("browser:reopentab", "Reopen closed tab", "Browser", "Ctrl+Shift+T"),
        new("browser:nexttab", "Next tab", "Browser", "Ctrl+Tab"),
        new("browser:prevtab", "Previous tab", "Browser", "Ctrl+Shift+Tab"),
        new("zoom:in", "Zoom in", "Browser", "Ctrl+Plus"),
        new("zoom:out", "Zoom out", "Browser", "Ctrl+Minus"),
        new("zoom:reset", "Reset zoom", "Browser", "Ctrl+0"),
    };

    public static ActionDef? Find(string id, IEnumerable<ActionDef>? extra = null) =>
        (extra ?? Enumerable.Empty<ActionDef>()).Concat(Common).FirstOrDefault(a => a.Id == id);
}

/// <summary>Executes actions with correct press / release semantics (keys and clicks are held while the button is held).</summary>
public static class ActionRunner
{
    public static void Run(ActionSpec spec, bool pressed, IDeviceActionHandler? device = null)
    {
        try { RunCore(spec, pressed, device); }
        catch (Exception ex) { Log.Error($"Action {spec} failed", ex); }
    }

    private static void RunCore(ActionSpec spec, bool pressed, IDeviceActionHandler? device)
    {
        var id = spec.Id;
        if (id is "default" or "disabled") return;

        if (id.StartsWith("device:")) { device?.DeviceAction(id, pressed); return; }

        switch (id)
        {
            case "key":
                if (string.IsNullOrWhiteSpace(spec.Arg)) return;
                var vks = Input.ParseCombo(spec.Arg);
                if (pressed) Input.ComboDown(vks); else Input.ComboUp(vks);
                return;
            case "text":
                if (pressed && spec.Arg != null) Input.TypeText(spec.Arg.Replace("\\n", "\n"));
                return;
            case "launch":
                if (pressed && !string.IsNullOrWhiteSpace(spec.Arg))
                    Process.Start(new ProcessStartInfo(spec.Arg.Trim('"')) { UseShellExecute = true });
                return;
            case "mouse:left": Input.MouseButton(Input.Button.Left, pressed); return;
            case "mouse:right": Input.MouseButton(Input.Button.Right, pressed); return;
            case "mouse:middle": Input.MouseButton(Input.Button.Middle, pressed); return;
            case "mouse:back": Input.MouseButton(Input.Button.Back, pressed); return;
            case "mouse:forward": Input.MouseButton(Input.Button.Forward, pressed); return;
            case "scroll:left": if (pressed) Input.HWheel(-120); return;
            case "scroll:right": if (pressed) Input.HWheel(120); return;
        }

        var def = ActionCatalog.Find(id);
        if (def?.Combo != null)
        {
            var vks = Input.ParseCombo(def.Combo);
            if (pressed) Input.ComboDown(vks); else Input.ComboUp(vks);
            return;
        }
        Log.Warn($"Unknown action '{id}'");
    }
}
