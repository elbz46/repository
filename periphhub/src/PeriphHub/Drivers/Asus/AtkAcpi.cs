using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeriphHub.Drivers.Asus;

/// <summary>
/// Talks to the ASUS System Control Interface driver (\\.\ATKACPI), which is what Armoury Crate uses for
/// keyboard lighting, performance modes and battery care on ASUS laptops.
/// </summary>
public sealed class AtkAcpi : IDisposable
{
    private const uint ControlCode = 0x0022240C;
    private const uint DEVS = 0x53564544, DSTS = 0x53545344, INIT = 0x54494E49;

    // Device IDs
    public const uint TufKeyboard = 0x00100056, TufKeyboard2 = 0x0010005A, TufKeyboardState = 0x00100057,
        TufKeyboardBrightness = 0x00050021, PerformanceMode = 0x00120075, VivoPerformanceMode = 0x00110019,
        BatteryLimit = 0x00120057, PanelOverdrive = 0x00050019, GpuEco = 0x00090020, GpuMux = 0x00090016,
        CpuFan = 0x00110013, GpuFan = 0x00110014, CpuTemp = 0x00120094, GpuTemp = 0x00120097,
        KeyboardBacklight = 0x00050021;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, uint inLen, byte[] outBuf, uint outLen, out uint returned, IntPtr overlapped);

    private readonly SafeFileHandle _handle;
    private readonly object _gate = new();

    private AtkAcpi(SafeFileHandle h) { _handle = h; }

    public static AtkAcpi? TryOpen()
    {
        var h = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var acpi = new AtkAcpi(h);
        try { acpi.Call(INIT, new byte[4]); } catch { }
        return acpi;
    }

    private byte[] Call(uint method, byte[] args)
    {
        var buf = new byte[8 + args.Length];
        BitConverter.GetBytes(method).CopyTo(buf, 0);
        BitConverter.GetBytes((uint)args.Length).CopyTo(buf, 4);
        Array.Copy(args, 0, buf, 8, args.Length);
        var outBuf = new byte[16];
        lock (_gate)
        {
            if (!DeviceIoControl(_handle, ControlCode, buf, (uint)buf.Length, outBuf, (uint)outBuf.Length, out _, IntPtr.Zero))
                throw new InvalidOperationException($"ASUS ACPI call failed (Win32 error {Marshal.GetLastWin32Error()})");
        }
        return outBuf;
    }

    public int Set(uint deviceId, uint value)
    {
        var args = new byte[8];
        BitConverter.GetBytes(deviceId).CopyTo(args, 0);
        BitConverter.GetBytes(value).CopyTo(args, 4);
        return BitConverter.ToInt32(Call(DEVS, args), 0);
    }

    public int Set(uint deviceId, byte[] parameters)
    {
        var args = new byte[4 + parameters.Length];
        BitConverter.GetBytes(deviceId).CopyTo(args, 0);
        parameters.CopyTo(args, 4);
        return BitConverter.ToInt32(Call(DEVS, args), 0);
    }

    /// <summary>Returns the value, or a negative number if the laptop does not support this ID.</summary>
    public int Get(uint deviceId)
    {
        var args = new byte[8];
        BitConverter.GetBytes(deviceId).CopyTo(args, 0);
        return BitConverter.ToInt32(Call(DSTS, args), 0) - 65536;
    }

    public bool IsSupported(uint deviceId)
    {
        try { return Get(deviceId) >= 0; } catch { return false; }
    }

    public void Dispose() => _handle.Dispose();
}
