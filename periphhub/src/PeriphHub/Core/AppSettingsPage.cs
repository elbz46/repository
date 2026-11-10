using System.Diagnostics;
using Microsoft.Win32;

namespace PeriphHub.Core;

/// <summary>Application settings, rendered with the same UI as a device.</summary>
public sealed class AppSettingsPage : ManagedDevice
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public AppSettingsPage() : base("app", "PeriphHub settings", "PeriphHub", DeviceKind.Other)
    {
        Transport = $"Version {typeof(AppSettingsPage).Assembly.GetName().Version?.ToString(3)}";
        Add("General", new ToggleSetting("autostart", "Start with Windows (in the tray)", IsAutostart(), v =>
        {
            SetAutostart(v);
            return Task.CompletedTask;
        }) { Persist = false, Description = "Needed so your device settings are re-applied automatically after every boot." });
        Add("General", new ToggleSetting("closetotray", "Closing the window keeps PeriphHub running in the tray",
            SettingsStore.Instance.GetApp("closeToTray", true), v => { SettingsStore.Instance.SetApp("closeToTray", v); return Task.CompletedTask; })
        { Persist = false });
        Add("Backup", new CommandSetting("backup", "Settings backup",
            new CommandItem("Export settings…", ExportAsync),
            new CommandItem("Import settings…", ImportAsync),
            new CommandItem("Open data folder", () => { Process.Start(new ProcessStartInfo(SettingsStore.DataDir) { UseShellExecute = true }); return Task.CompletedTask; }))
        { Description = "Export to move your setup to another PC, or share it with a friend." });
        Add("About", new InfoSetting("about", "About",
            "PeriphHub controls Logitech (HID++), ASUS laptop (Armoury-style) and generic Bluetooth/USB peripherals from one place.\n" +
            "Devices are recognised automatically; settings follow each device between USB and Bluetooth."));
    }

    public override string Glyph => "\uE713";

    public static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("PeriphHub") != null;
    }

    public static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("PeriphHub", $"\"{Environment.ProcessPath}\" --minimized");
        else k.DeleteValue("PeriphHub", false);
    }

    private static Task ExportAsync()
    {
        var dlg = new SaveFileDialog { FileName = "PeriphHub-settings.json", Filter = "PeriphHub settings|*.json" };
        if (dlg.ShowDialog() == true) SettingsStore.Instance.Export(dlg.FileName);
        return Task.CompletedTask;
    }

    private static Task ImportAsync()
    {
        var dlg = new OpenFileDialog { Filter = "PeriphHub settings|*.json" };
        if (dlg.ShowDialog() != true) return Task.CompletedTask;
        SettingsStore.Instance.Import(dlg.FileName);
        System.Windows.MessageBox.Show("Settings imported. PeriphHub will restart to apply them.", "PeriphHub");
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
        return Task.CompletedTask;
    }
}
