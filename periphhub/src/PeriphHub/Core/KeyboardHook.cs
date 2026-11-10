using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PeriphHub.Core;

/// <summary>Low-level keyboard hook used for remapping keys / macros on any keyboard.</summary>
public sealed class KeyboardHook : IDisposable
{
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const uint LLKHF_INJECTED = 0x10;

    private readonly HookProc _proc;
    private IntPtr _hook;
    private readonly BlockingCollection<Action> _queue = new();

    /// <summary>Return true to swallow the key. Called on the UI thread; must be fast.</summary>
    public Func<int, bool, bool>? Handler { get; set; }

    /// <summary>One-shot capture of the next key press (for "Add key").</summary>
    public Action<int>? Capture { get; set; }

    public KeyboardHook()
    {
        _proc = Callback;
        var worker = new Thread(() => { foreach (var a in _queue.GetConsumingEnumerable()) { try { a(); } catch (Exception ex) { Log.Error("Key action", ex); } } })
        { IsBackground = true, Name = "key-actions" };
        worker.Start();
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        using var cur = Process.GetCurrentProcess();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(cur.MainModule?.ModuleName), 0);
        if (_hook == IntPtr.Zero) Log.Warn($"Keyboard hook failed ({Marshal.GetLastWin32Error()})");
    }

    public void Enqueue(Action a) => _queue.Add(a);

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (k.flags & LLKHF_INJECTED) != 0 || k.dwExtraInfo == (IntPtr)Input.InjectedTag;
            int msg = (int)wParam;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;
            if (!injected && (down || up))
            {
                if (Capture is { } cap && down)
                {
                    Capture = null;
                    cap((int)k.vkCode);
                    return (IntPtr)1;
                }
                if (Handler?.Invoke((int)k.vkCode, down) == true) return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _queue.CompleteAdding();
    }
}
