using System.Text.RegularExpressions;
using HidSharp;
using PeriphHub.Core;

namespace PeriphHub.Drivers.Logitech;

/// <summary>
/// Finds every Logitech HID++ interface (Unifying / Bolt / LIGHTSPEED receivers, headset dongles,
/// Bluetooth and wired devices), probes what is behind it and maps it to a <see cref="LogitechDevice"/>.
/// The same physical device seen over a different transport is re-attached to the same entry.
/// </summary>
public sealed partial class LogitechProvider : IDeviceProvider
{
    public const int VendorId = 0x046D;
    private readonly Dictionary<string, HidppChannel> _channels = new();
    private readonly Dictionary<(string channel, byte index), LogitechDevice> _routes = new();
    private readonly object _gate = new();
    private DeviceHub? _hub;

    public string Name => "Logitech";

    [GeneratedRegex(@"&col[0-9a-f]{2}", RegexOptions.IgnoreCase)]
    private static partial Regex ColRegex();

    // Windows gives each top-level collection its own instance suffix ("...&0&0000#", "...&0&0001#").
    [GeneratedRegex(@"(#[^#]+)&[0-9a-f]{4}(#\{[^}]+\})$", RegexOptions.IgnoreCase)]
    private static partial Regex InstanceRegex();

    /// <summary>Same key for all collections of one physical HID interface.</summary>
    public static string InterfaceKey(string devicePath) =>
        InstanceRegex().Replace(ColRegex().Replace(devicePath.ToLowerInvariant(), ""), "$1$2");

    public async Task ScanAsync(DeviceHub hub)
    {
        _hub = hub;
        var all = DeviceList.Local.GetHidDevices(VendorId).ToList();
        var groups = all.GroupBy(d => InterfaceKey(d.DevicePath));
        foreach (var g in groups)
        {
            lock (_gate)
                if (_channels.TryGetValue(g.Key, out var existing) && !existing.IsDisposed) continue;

            HidDevice? longDev = null, shortDev = null;
            foreach (var d in g)
            {
                try
                {
                    if (!IsVendorCollection(d)) continue;
                    var outLen = d.GetMaxOutputReportLength();
                    if (outLen == 20 && longDev == null) longDev = d;
                    else if (outLen == 7 && shortDev == null) shortDev = d;
                }
                catch { /* some collections cannot be queried; skip */ }
            }
            if (longDev == null) continue;

            HidppChannel ch;
            try { ch = new HidppChannel(g.Key, longDev, shortDev); }
            catch (Exception ex) { Log.Warn($"Cannot open Logitech interface {longDev.ProductID:X4}: {ex.Message}"); continue; }

            lock (_gate) _channels[g.Key] = ch;
            ch.Notification += r => OnNotification(ch, r);
            ch.Closed += OnClosed;
            Log.Info($"Logitech interface {ch.ProductId:X4} opened ({ch.TransportName})");
            await ProbeChannelAsync(ch);
        }
    }

    private static bool IsVendorCollection(HidDevice d)
    {
        var desc = d.GetReportDescriptor();
        foreach (var item in desc.DeviceItems)
            foreach (var u in item.Usages.GetAllValues())
            {
                var page = u >> 16;
                if (page is 0xFF00 or 0xFF43) return true;
            }
        return false;
    }

    private async Task ProbeChannelAsync(HidppChannel ch)
    {
        // Direct device (Bluetooth, cable, headset dongle) answers on index 0xFF.
        var direct = new HidppDevice(ch, 0xFF);
        if (await direct.PingAsync())
        {
            await AttachEndpointAsync(direct);
            return;
        }
        // Otherwise it is a receiver: enable connection notifications and look at the pairing slots.
        if (ch.HasShort)
        {
            try { await ch.RegisterAsync(0xFF, 0x80, 0x00, new byte[] { 0x00, 0x09, 0x00 }); }
            catch (Exception ex) { Log.Warn($"Receiver notifications not enabled: {ex.Message}"); }
        }
        for (byte i = 1; i <= 6; i++)
        {
            var dev = new HidppDevice(ch, i);
            if (await dev.PingAsync(600)) await AttachEndpointAsync(dev);
        }
    }

    private async Task AttachEndpointAsync(HidppDevice dev)
    {
        try
        {
            await dev.DiscoverAsync();
            var (key, name, kind) = await LogitechDevice.IdentifyAsync(dev);
            var hub = _hub!;
            var existing = hub.Find<LogitechDevice>(key);
            LogitechDevice device;
            if (existing != null)
            {
                device = existing;
                Log.Info($"{name} is back (via {dev.Channel.TransportName}) – restoring its settings");
            }
            else
            {
                device = new LogitechDevice(key, name, kind, dev);
                hub.Add(device);
            }
            lock (_gate)
            {
                foreach (var stale in _routes.Where(kv => kv.Value == device).Select(kv => kv.Key).ToList()) _routes.Remove(stale);
                _routes[(dev.Channel.Key, dev.Index)] = device;
            }
            await device.AttachAsync(dev);
        }
        catch (Exception ex) { Log.Error($"Logitech device at index {dev.Index} could not be set up", ex); }
    }

    private void OnNotification(HidppChannel ch, byte[] r)
    {
        LogitechDevice? device;
        lock (_gate) _routes.TryGetValue((ch.Key, r[1]), out device);

        // HID++ 1.0 receiver: device (dis)connection
        if (r[0] == HidppChannel.ShortId && r[2] == 0x41 && r[1] is >= 1 and <= 6)
        {
            bool online = (r[4] & 0x40) == 0;
            if (device != null)
            {
                if (online) _ = device.AttachAsync(device.Endpoint.Channel == ch && device.Endpoint.Index == r[1] ? device.Endpoint : new HidppDevice(ch, r[1]));
                else device.MarkOffline();
            }
            else if (online)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(300);
                    var dev = new HidppDevice(ch, r[1]);
                    if (await dev.PingAsync()) await AttachEndpointAsync(dev);
                });
            }
            return;
        }

        // HID++ 2.0 event (software id 0)
        if ((r[3] & 0x0F) == 0 && device != null && r[2] < 0x80) device.OnNotification(r);
    }

    private void OnClosed(HidppChannel ch)
    {
        List<LogitechDevice> affected;
        lock (_gate)
        {
            _channels.Remove(ch.Key);
            affected = _routes.Where(kv => kv.Key.channel == ch.Key).Select(kv => kv.Value).ToList();
            foreach (var k in _routes.Keys.Where(k => k.channel == ch.Key).ToList()) _routes.Remove(k);
        }
        foreach (var d in affected) if (d.Endpoint.Channel == ch) d.MarkOffline();
        _hub?.QueueRescan();
    }

    public void Dispose()
    {
        List<HidppChannel> chans;
        lock (_gate) chans = _channels.Values.ToList();
        foreach (var c in chans) c.Dispose();
    }
}
