using System.Drawing;
using System.Windows.Forms;
using PeriphHub.Core;

namespace PeriphHub.UI;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly MainWindow _window;

    public TrayIcon(MainWindow window)
    {
        _window = window;
        _icon = new NotifyIcon
        {
            Text = "PeriphHub",
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open PeriphHub", null, (_, _) => _window.ShowAndActivate());
        menu.Items.Add("Rescan devices", null, (_, _) => App.Hub.QueueRescan(100));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ((App)System.Windows.Application.Current).Quit());
        menu.Opening += (_, _) => UpdateTooltip();
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _window.ShowAndActivate(); };
        _icon.MouseMove += (_, _) => UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        var lines = App.Hub.Devices.Where(d => d.BatteryPercent != null)
            .Select(d => $"{d.Name}: {d.BatteryPercent}%{(d.Charging ? " ⚡" : "")}");
        var text = string.Join("\n", new[] { "PeriphHub" }.Concat(lines));
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    public void ShowToast(string text)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _icon.ShowBalloonTip(1500, "PeriphHub", text, ToolTipIcon.None));
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
