using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PeriphHub.Core;

/// <summary>Resolves the executable name of the foreground window, for per-application button profiles.</summary>
public static class ForegroundApp
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private static string _cached = "";
    private static long _cachedAt;

    public static string ProcessName
    {
        get
        {
            var now = Environment.TickCount64;
            if (now - _cachedAt < 250) return _cached;
            _cachedAt = now;
            try
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
                using var p = Process.GetProcessById((int)pid);
                _cached = p.ProcessName.ToLowerInvariant() + ".exe";
            }
            catch { _cached = ""; }
            return _cached;
        }
    }
}
