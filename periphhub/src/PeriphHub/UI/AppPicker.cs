using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace PeriphHub.UI;

/// <summary>Small dialog: pick a running app or type an .exe name for a per-application profile.</summary>
public static class AppPicker
{
    public static string? Pick(Window owner)
    {
        var running = Process.GetProcesses()
            .Where(p => { try { return p.MainWindowHandle != IntPtr.Zero && p.Id != Environment.ProcessId; } catch { return false; } })
            .Select(p => p.ProcessName.ToLowerInvariant() + ".exe")
            .Distinct().OrderBy(n => n).ToList();

        var box = new ComboBox { IsEditable = true, ItemsSource = running, Margin = new Thickness(0, 8, 0, 16), MinWidth = 320 };
        var ok = new Button { Content = "Add", IsDefault = true, Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(18, 6, 18, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Pick an open app or type its program name (e.g. chrome.exe, photoshop.exe):", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var dlg = new Window
        {
            Title = "Add application profile", Owner = owner, Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        ok.Click += (_, _) => dlg.DialogResult = true;
        box.Loaded += (_, _) => box.Focus();
        return dlg.ShowDialog() == true ? box.Text?.Trim() : null;
    }
}
