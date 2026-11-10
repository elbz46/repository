using System.Collections.ObjectModel;
using HidSharp;

namespace PeriphHub.Core;

public interface IDeviceProvider : IDisposable
{
    string Name { get; }
    Task ScanAsync(DeviceHub hub);
}

/// <summary>Owns all providers, reacts to plug / unplug / Bluetooth (re)connects and keeps the device list.</summary>
public sealed class DeviceHub : IDisposable
{
    private readonly List<IDeviceProvider> _providers = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private Timer? _debounce;
    private Timer? _poll;
    private bool _rescanQueued;

    public ObservableCollection<ManagedDevice> Devices { get; } = new();

    public void AddProvider(IDeviceProvider p) => _providers.Add(p);

    public void Start()
    {
        DeviceList.Local.Changed += (_, _) => QueueRescan();
        QueueRescan(100);
        _poll = new Timer(async _ => await PollAll(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));
    }

    public void QueueRescan(int delayMs = 1200)
    {
        lock (_providers)
        {
            _debounce?.Dispose();
            _debounce = new Timer(async _ => await ScanAll(), null, delayMs, Timeout.Infinite);
        }
    }

    private async Task ScanAll()
    {
        if (!await _scanLock.WaitAsync(0)) { _rescanQueued = true; return; }
        try
        {
            do
            {
                _rescanQueued = false;
                foreach (var p in _providers)
                {
                    try { await p.ScanAsync(this); }
                    catch (Exception ex) { Log.Error($"{p.Name} scan failed", ex); }
                }
            } while (_rescanQueued);
        }
        finally { _scanLock.Release(); }
    }

    private async Task PollAll()
    {
        foreach (var d in Devices.ToList())
        {
            try { await d.PollAsync(); } catch (Exception ex) { Log.Warn($"{d.Name}: poll failed: {ex.Message}"); }
        }
    }

    public T? Find<T>(string key) where T : ManagedDevice
    {
        lock (Devices) return Devices.OfType<T>().FirstOrDefault(d => d.Key == key);
    }

    public void Add(ManagedDevice device)
    {
        void Do()
        {
            lock (Devices)
            {
                if (Devices.Any(d => d.Key == device.Key)) return;
                // keep a stable, friendly order: laptop, mice, keyboards, headsets, others
                int Rank(ManagedDevice d) => d.Kind switch
                {
                    DeviceKind.Laptop => 0, DeviceKind.Mouse => 1, DeviceKind.Keyboard => 2,
                    DeviceKind.Headset => 3, _ => 4,
                };
                int i = 0;
                while (i < Devices.Count && Rank(Devices[i]) <= Rank(device) && Devices[i].Kind != DeviceKind.Other) i++;
                if (device.Kind == DeviceKind.Other) i = Devices.Count;
                Devices.Insert(i, device);
                SettingsStore.Instance.RememberDeviceName(device.Key, device.Name);
            }
        }
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp == null || disp.CheckAccess()) Do(); else disp.Invoke(Do);
        Log.Info($"Device added: {device.Name} [{device.Key}]");
    }

    public void Remove(ManagedDevice device)
    {
        void Do() { lock (Devices) Devices.Remove(device); }
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp == null || disp.CheckAccess()) Do(); else disp.Invoke(Do);
        device.Dispose();
    }

    public void Dispose()
    {
        _poll?.Dispose();
        _debounce?.Dispose();
        foreach (var p in _providers) p.Dispose();
        foreach (var d in Devices.ToList()) d.Dispose();
    }
}
