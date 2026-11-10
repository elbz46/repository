using System.Runtime.InteropServices;
using System.Windows.Input;

namespace PeriphHub.Core;

/// <summary>Synthesises keyboard / mouse input with SendInput.</summary>
public static class Input
{
    public const uint InjectedTag = 0x50480001; // dwExtraInfo marker so our own hook ignores our own input

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public int mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
    private const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4,
        MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_MIDDLEDOWN = 0x20,
        MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_XDOWN = 0x80, MOUSEEVENTF_XUP = 0x100,
        MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_HWHEEL = 0x1000;

    private static readonly HashSet<ushort> Extended = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, // PgUp PgDn End Home arrows Ins Del
        0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5,                   // LWin RWin Apps NumDiv NumLock RCtrl RAlt
        0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3,                   // media
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xB4, 0xB5, 0xB6, 0xB7,
    };

    private static void Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (Extended.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = flags, dwExtraInfo = (IntPtr)InjectedTag } },
        };
    }

    private static INPUT Mouse(uint flags, int data = 0, int dx = 0, int dy = 0) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data, dx = dx, dy = dy, dwExtraInfo = (IntPtr)InjectedTag } },
    };

    public static void KeyDown(ushort vk) => Send(Key(vk, false));
    public static void KeyUp(ushort vk) => Send(Key(vk, true));

    public static void ComboDown(IReadOnlyList<ushort> vks) => Send(vks.Select(v => Key(v, false)).ToArray());
    public static void ComboUp(IReadOnlyList<ushort> vks) => Send(vks.Reverse().Select(v => Key(v, true)).ToArray());
    public static void Tap(IReadOnlyList<ushort> vks) { ComboDown(vks); ComboUp(vks); }

    public static void TypeText(string text)
    {
        var list = new List<INPUT>();
        foreach (var ch in text)
        {
            if (ch == '\n') { list.Add(Key(0x0D, false)); list.Add(Key(0x0D, true)); continue; }
            if (ch == '\r') continue;
            list.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE, dwExtraInfo = (IntPtr)InjectedTag } } });
            list.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, dwExtraInfo = (IntPtr)InjectedTag } } });
        }
        Send(list.ToArray());
    }

    public enum Button { Left, Right, Middle, Back, Forward }

    public static void MouseButton(Button b, bool down)
    {
        switch (b)
        {
            case Button.Left: Send(Mouse(down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP)); break;
            case Button.Right: Send(Mouse(down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP)); break;
            case Button.Middle: Send(Mouse(down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP)); break;
            case Button.Back: Send(Mouse(down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 1)); break;
            case Button.Forward: Send(Mouse(down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 2)); break;
        }
    }

    public static void Wheel(int delta) => Send(Mouse(MOUSEEVENTF_WHEEL, delta));
    public static void HWheel(int delta) => Send(Mouse(MOUSEEVENTF_HWHEEL, delta));
    public static void Move(int dx, int dy) => Send(Mouse(MOUSEEVENTF_MOVE, 0, dx, dy));

    private static readonly Dictionary<string, ushort> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0xA2, ["control"] = 0xA2, ["lctrl"] = 0xA2, ["rctrl"] = 0xA3,
        ["shift"] = 0xA0, ["lshift"] = 0xA0, ["rshift"] = 0xA1,
        ["alt"] = 0xA4, ["lalt"] = 0xA4, ["ralt"] = 0xA5, ["altgr"] = 0xA5,
        ["win"] = 0x5B, ["windows"] = 0x5B, ["meta"] = 0x5B, ["super"] = 0x5B,
        ["esc"] = 0x1B, ["escape"] = 0x1B, ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09,
        ["space"] = 0x20, ["backspace"] = 0x08, ["bksp"] = 0x08, ["del"] = 0x2E, ["delete"] = 0x2E,
        ["ins"] = 0x2D, ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23, ["pgup"] = 0x21,
        ["pageup"] = 0x21, ["pgdn"] = 0x22, ["pagedown"] = 0x22, ["left"] = 0x25, ["up"] = 0x26,
        ["right"] = 0x27, ["down"] = 0x28, ["prtsc"] = 0x2C, ["printscreen"] = 0x2C, ["menu"] = 0x5D,
        ["capslock"] = 0x14, ["numlock"] = 0x90, ["scrolllock"] = 0x91, ["pause"] = 0x13,
        ["plus"] = 0xBB, ["="] = 0xBB, ["minus"] = 0xBD, ["-"] = 0xBD, [","] = 0xBC, ["."] = 0xBE,
        ["/"] = 0xBF, [";"] = 0xBA, ["'"] = 0xDE, ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC, ["`"] = 0xC0,
        ["playpause"] = 0xB3, ["nexttrack"] = 0xB0, ["prevtrack"] = 0xB1, ["stop"] = 0xB2,
        ["volumeup"] = 0xAF, ["volup"] = 0xAF, ["volumedown"] = 0xAE, ["voldown"] = 0xAE, ["mute"] = 0xAD,
        ["browserback"] = 0xA6, ["browserforward"] = 0xA7, ["browserrefresh"] = 0xA8,
        ["calculator"] = 0xB7, ["mail"] = 0xB4,
    };

    /// <summary>Parses "Ctrl+Shift+T", "Win+Tab", "F13", "Ctrl+Win+Left" into virtual-key codes.</summary>
    public static IReadOnlyList<ushort> ParseCombo(string combo)
    {
        var result = new List<ushort>();
        foreach (var raw in combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (Aliases.TryGetValue(raw, out var vk)) { result.Add(vk); continue; }
            if (raw.Length == 1 && char.IsLetterOrDigit(raw[0])) { result.Add(char.ToUpperInvariant(raw[0])); continue; }
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                ushort.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, null, out var hexVk)) { result.Add(hexVk); continue; }
            if (Enum.TryParse<System.Windows.Input.Key>(raw, true, out var key))
            {
                var v = KeyInterop.VirtualKeyFromKey(key);
                if (v != 0) { result.Add((ushort)v); continue; }
            }
            throw new FormatException($"Unknown key '{raw}'");
        }
        return result;
    }

    public static string VkName(int vk)
    {
        foreach (var kv in Aliases) if (kv.Value == vk && kv.Key.Length > 1 && char.IsLetter(kv.Key[0])) return Capitalize(kv.Key);
        if (vk is >= 0x30 and <= 0x5A) return ((char)vk).ToString();
        var key = KeyInterop.KeyFromVirtualKey(vk);
        return key == System.Windows.Input.Key.None ? $"0x{vk:X2}" : key.ToString();
    }

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
