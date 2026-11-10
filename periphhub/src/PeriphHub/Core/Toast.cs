namespace PeriphHub.Core;

/// <summary>Short on-screen notifications (shown by the tray icon).</summary>
public static class Toast
{
    public static event Action<string>? Requested;
    public static void Show(string text) => Requested?.Invoke(text);
}
