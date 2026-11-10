namespace PeriphHub.Drivers.Logitech;

public sealed record ControlInfo(ushort Cid, ushort Tid, byte Flags1, byte Flags2, byte Position, byte Group)
{
    public bool Divertable => (Flags1 & 0x20) != 0;
    public bool Virtual => (Flags1 & 0x80) != 0;
    public bool RawXY => (Flags2 & 0x01) != 0;
    public string ButtonId => $"cid:{Cid:X4}";
    public string Name => ControlNames.Name(Cid);
}

public static class ControlNames
{
    public static string Name(ushort cid) => cid switch
    {
        0x0050 => "Left click",
        0x0051 => "Right click",
        0x0052 => "Middle click (wheel press)",
        0x0053 => "Back button",
        0x0054 => "Left / back button",
        0x0056 => "Forward button",
        0x0057 => "Right / forward button",
        0x005B => "Wheel tilt left",
        0x005D => "Wheel tilt right",
        0x00C3 => "Gesture button (thumb)",
        0x00C4 => "Wheel mode shift button",
        0x00C7 => "Thumb wheel press",
        0x00D7 => "Virtual gesture button",
        0x00FD => "DPI switch",
        0x00ED => "DPI button",
        0x01A0 => "Haptic sense / action button",
        0x00E3 => "Brightness down",
        0x00E4 => "Brightness up",
        0x00E0 => "Mic mute",
        0x00E1 => "Media play/pause",
        0x00E2 => "Mute",
        0x00E5 => "Easy-Switch 1",
        0x00E6 => "Easy-Switch 2",
        0x00E7 => "Easy-Switch 3",
        0x00E8 => "Backlight down",
        0x00E9 => "Backlight up",
        0x0103 => "Emoji key",
        0x0108 => "Screen capture key",
        0x010A => "Dictation key",
        0x0110 => "Smart action key",
        _ => $"Control 0x{cid:X4}",
    };
}
