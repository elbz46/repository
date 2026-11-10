using System.IO;
using HidSharp;
using PeriphHub.Core;

namespace PeriphHub.Drivers.Logitech;

public sealed class HidppException(string message, byte code = 0) : Exception(message)
{
    public byte Code { get; } = code;
}

/// <summary>
/// One physical Logitech HID interface (a receiver, a Bluetooth device or a wired/dongle device).
/// Windows splits it into a "short" (7-byte, report 0x10) and a "long" (20-byte, report 0x11) collection;
/// we read both and send HID++ 2.0 requests as long reports.
/// </summary>
public sealed class HidppChannel : IDisposable
{
    public const byte ShortId = 0x10, LongId = 0x11;
    private const byte SoftwareId = 0x0B;

    private readonly HidStream _long;
    private readonly HidStream? _short;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _pendingGate = new();
    private (Func<byte[], bool> match, TaskCompletionSource<byte[]> tcs)? _pending;
    private volatile bool _disposed;

    public string Key { get; }
    public int ProductId { get; }
    public string DevicePath { get; }
    public bool IsBluetooth { get; }
    public string TransportName { get; }

    public event Action<byte[]>? Notification;
    public event Action<HidppChannel>? Closed;

    public HidppChannel(string key, HidDevice longDev, HidDevice? shortDev)
    {
        Key = key;
        ProductId = longDev.ProductID;
        DevicePath = longDev.DevicePath;
        var p = longDev.DevicePath.ToLowerInvariant();
        IsBluetooth = p.Contains("00001124-0000-1000-8000-00805f9b34fb") || p.Contains("00001812-0000-1000-8000-00805f9b34fb") || p.Contains("bth");
        TransportName = IsBluetooth ? "Bluetooth" : ReceiverName(ProductId);
        _long = longDev.Open();
        _long.ReadTimeout = Timeout.Infinite;
        _long.WriteTimeout = 1000;
        StartReader(_long, longDev.GetMaxInputReportLength());
        if (shortDev != null)
        {
            try
            {
                _short = shortDev.Open();
                _short.ReadTimeout = Timeout.Infinite;
                _short.WriteTimeout = 1000;
                StartReader(_short, shortDev.GetMaxInputReportLength());
            }
            catch (Exception ex) { Log.Warn($"Short HID++ collection unavailable: {ex.Message}"); }
        }
    }

    public static string ReceiverName(int pid) => pid switch
    {
        0xC52B or 0xC532 => "USB · Unifying receiver",
        0xC548 => "USB · Bolt receiver",
        0xC534 or 0xC52F or 0xC537 => "USB · Nano receiver",
        0xC539 or 0xC53A or 0xC53D or 0xC53F or 0xC541 or 0xC545 or 0xC547 or 0xC54D => "USB · LIGHTSPEED receiver",
        _ when pid >= 0x0A00 && pid <= 0x0BFF => "USB · LIGHTSPEED headset dongle",
        _ when pid >= 0xC500 && pid <= 0xC5FF => "USB receiver",
        _ => "USB",
    };

    public bool HasShort => _short != null;

    private void StartReader(HidStream stream, int len)
    {
        var t = new Thread(() =>
        {
            var buf = new byte[Math.Max(len, 20)];
            while (!_disposed)
            {
                int n;
                try { n = stream.Read(buf, 0, buf.Length); }
                catch (TimeoutException) { continue; }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    if (!_disposed) { Log.Info($"HID++ channel {Key} closed ({ex.Message})"); Dispose(); }
                    return;
                }
                if (n < 4) continue;
                var report = new byte[20];
                Array.Copy(buf, report, Math.Min(n, 20));
                Dispatch(report);
            }
        }) { IsBackground = true, Name = "hidpp-read" };
        t.Start();
    }

    private void Dispatch(byte[] r)
    {
        lock (_pendingGate)
        {
            if (_pending is { } p && p.match(r))
            {
                _pending = null;
                p.tcs.TrySetResult(r);
                return;
            }
        }
        // Notifications are handled off the reader thread: handlers may themselves send requests.
        var handler = Notification;
        if (handler != null) ThreadPool.QueueUserWorkItem(_ => { try { handler(r); } catch (Exception ex) { Log.Error("HID++ notification handler", ex); } });
    }

    /// <summary>HID++ 2.0 feature call. Returns the 16 parameter bytes of the response.</summary>
    public async Task<byte[]> CallAsync(byte deviceIndex, byte featureIndex, byte function, byte[] args, int timeoutMs = 1500)
    {
        var fnSw = (byte)((function << 4) | SoftwareId);
        var report = new byte[20];
        report[0] = LongId; report[1] = deviceIndex; report[2] = featureIndex; report[3] = fnSw;
        Array.Copy(args, 0, report, 4, Math.Min(args.Length, 16));

        bool Match(byte[] r) =>
            r[1] == deviceIndex &&
            ((r[2] == featureIndex && r[3] == fnSw) ||
             ((r[2] == 0xFF || r[2] == 0x8F) && r[3] == featureIndex && r[4] == fnSw));

        var resp = await Exchange(report, _long, Match, timeoutMs);
        if (resp[2] == 0xFF) throw new HidppException($"HID++ error 0x{resp[5]:X2} ({ErrorName(resp[5])})", resp[5]);
        if (resp[2] == 0x8F) throw new HidppException($"Device not reachable (0x{resp[5]:X2})", resp[5]);
        return resp[4..20];
    }

    /// <summary>HID++ 1.0 register access on a receiver (short report, 0x80 = set, 0x81 = get).</summary>
    public async Task<byte[]> RegisterAsync(byte deviceIndex, byte subId, byte address, byte[] args, int timeoutMs = 1000)
    {
        var stream = _short ?? _long;
        var len = _short != null ? 7 : 20;
        var report = new byte[len];
        report[0] = _short != null ? ShortId : LongId;
        report[1] = deviceIndex; report[2] = subId; report[3] = address;
        Array.Copy(args, 0, report, 4, Math.Min(args.Length, len - 4));
        bool Match(byte[] r) => r[1] == deviceIndex &&
                                ((r[2] == subId && r[3] == address) || (r[2] == 0x8F && r[3] == subId && r[4] == address));
        var resp = await Exchange(report, stream, Match, timeoutMs);
        if (resp[2] == 0x8F) throw new HidppException($"Register error 0x{resp[5]:X2}", resp[5]);
        return resp[4..20];
    }

    private async Task<byte[]> Exchange(byte[] report, HidStream stream, Func<byte[], bool> match, int timeoutMs)
    {
        if (_disposed) throw new HidppException("Device disconnected");
        await _lock.WaitAsync();
        try
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingGate) _pending = (match, tcs);
            try { stream.Write(report); }
            catch (Exception ex)
            {
                lock (_pendingGate) _pending = null;
                throw new HidppException($"Write failed: {ex.Message}");
            }
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            lock (_pendingGate) if (_pending?.tcs == tcs) _pending = null;
            if (done != tcs.Task) throw new HidppException("Timed out waiting for the device", 0xFE);
            return tcs.Task.Result;
        }
        finally { _lock.Release(); }
    }

    public static string ErrorName(byte code) => code switch
    {
        1 => "unknown", 2 => "invalid argument", 3 => "out of range", 4 => "hardware error",
        5 => "logitech internal", 6 => "invalid feature index", 7 => "invalid function", 8 => "busy",
        9 => "unsupported", _ => "error",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _long.Dispose(); } catch { }
        try { _short?.Dispose(); } catch { }
        lock (_pendingGate) { _pending?.tcs.TrySetException(new HidppException("Device disconnected")); _pending = null; }
        Closed?.Invoke(this);
    }

    public bool IsDisposed => _disposed;
}
