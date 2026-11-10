using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PeriphHub.Core;

/// <summary>
/// Persists every setting per *physical* device, keyed by a stable identity (e.g. the Logitech unit ID,
/// which is identical over Bluetooth and over the USB receiver). That is what lets a device be moved
/// between USB and Bluetooth without being set up again.
/// </summary>
public sealed class SettingsStore
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PeriphHub");

    private static string FilePath => Path.Combine(DataDir, "settings.json");
    private readonly object _gate = new();
    private JsonObject _root;
    private Timer? _saveTimer;

    public static SettingsStore Instance { get; } = new();

    private SettingsStore()
    {
        _root = Load();
    }

    private static JsonObject Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            Log.Error("Settings file unreadable, starting fresh (old file kept as .bak)", ex);
            try { File.Copy(FilePath, FilePath + ".bak", true); } catch { }
        }
        return new JsonObject();
    }

    private JsonObject Section(string name)
    {
        if (_root[name] is not JsonObject o) { o = new JsonObject(); _root[name] = o; }
        return o;
    }

    private JsonObject DeviceNode(string deviceKey)
    {
        var devices = Section("devices");
        if (devices[deviceKey] is not JsonObject d) { d = new JsonObject(); devices[deviceKey] = d; }
        return d;
    }

    public T? GetDevice<T>(string deviceKey, string settingId)
    {
        lock (_gate)
        {
            var node = DeviceNode(deviceKey)[settingId];
            if (node == null) return default;
            try { return node.Deserialize<T>(); } catch { return default; }
        }
    }

    public bool HasDevice(string deviceKey, string settingId)
    {
        lock (_gate) return DeviceNode(deviceKey)[settingId] != null;
    }

    public void SetDevice<T>(string deviceKey, string settingId, T value)
    {
        lock (_gate) DeviceNode(deviceKey)[settingId] = JsonSerializer.SerializeToNode(value);
        ScheduleSave();
    }

    public void RememberDeviceName(string deviceKey, string name) => SetDevice(deviceKey, "_name", name);

    public T? GetApp<T>(string id, T? fallback = default)
    {
        lock (_gate)
        {
            var node = Section("app")[id];
            if (node == null) return fallback;
            try { return node.Deserialize<T>(); } catch { return fallback; }
        }
    }

    public void SetApp<T>(string id, T value)
    {
        lock (_gate) Section("app")[id] = JsonSerializer.SerializeToNode(value);
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        lock (_gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = new Timer(_ => SaveNow(), null, 400, Timeout.Infinite);
        }
    }

    public void SaveNow()
    {
        string json;
        lock (_gate) json = _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { Log.Error("Could not save settings", ex); }
    }

    public void Export(string path) { SaveNow(); File.Copy(FilePath, path, true); }

    public void Import(string path)
    {
        var parsed = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                     ?? throw new InvalidDataException("Not a PeriphHub settings file");
        lock (_gate) _root = parsed;
        SaveNow();
    }
}
