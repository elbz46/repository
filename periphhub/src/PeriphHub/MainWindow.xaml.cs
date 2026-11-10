using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PeriphHub.Core;

namespace PeriphHub;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public System.Collections.ObjectModel.ObservableCollection<ManagedDevice> Devices => App.Hub.Devices;

    private ManagedDevice? _selected;
    public ManagedDevice? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            PageScroller?.ScrollToTop();
            if (EmptyText != null)
            {
                EmptyText.Visibility = value == null ? Visibility.Visible : Visibility.Collapsed;
                PageBody.Visibility = value == null ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        // Only devices that are connected right now are listed; they appear / disappear by themselves.
        var view = (System.Windows.Data.ListCollectionView)System.Windows.Data.CollectionViewSource.GetDefaultView(Devices);
        view.Filter = o => AppSettingsPage.ShowDisconnected || o is ManagedDevice { IsOnline: true };
        view.IsLiveFiltering = true;
        view.LiveFilteringProperties.Add(nameof(ManagedDevice.IsOnline));
        AppSettingsPage.ShowDisconnectedChanged += () => Dispatcher.Invoke(view.Refresh);
        ((System.Collections.Specialized.INotifyCollectionChanged)view).CollectionChanged += (_, _) => EnsureVisibleSelection(view);
    }

    private void EnsureVisibleSelection(System.Windows.Data.ListCollectionView view)
    {
        if (Selected == App.AppPage) return;
        if (Selected != null && view.Contains(Selected)) return;
        DeviceList.SelectedIndex = view.Count > 0 ? 0 : -1;
        if (view.Count == 0) Selected = null;
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
        Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (SettingsStore.Instance.GetApp("closeToTray", true))
        {
            e.Cancel = true;
            Hide();
            return;
        }
        e.Cancel = true;
        ((App)Application.Current).Quit();
    }

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is ManagedDevice d) Selected = d;
    }

    private void OnShowSettings(object sender, RoutedEventArgs e)
    {
        DeviceList.SelectedItem = null;
        Selected = App.AppPage;
    }

    private void OnRescan(object sender, RoutedEventArgs e) => App.Hub.QueueRescan(50);

    private async void OnCommand(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CommandItem item, Tag: CommandSetting setting } b)
        {
            b.IsEnabled = false;
            try { await setting.RunAsync(item); } finally { b.IsEnabled = true; }
        }
    }

    private void OnSwatch(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string hex, Tag: ColorSetting setting }) setting.Value = hex;
    }

    private void OnPickColor(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ColorSetting setting }) return;
        var (r, g, b) = ColorSetting.Parse(setting.Value);
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(r, g, b) };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            setting.Value = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
    }

    private void OnAddApp(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ButtonMapSetting map }) return;
        var exe = UI.AppPicker.Pick(this);
        if (!string.IsNullOrWhiteSpace(exe)) map.AddProfile(exe);
    }

    private void OnRemoveApp(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ButtonMapSetting map }) map.RemoveCurrentProfile();
    }

    /// <summary>For "Keystroke…" actions the box records the shortcut you press instead of typing text.</summary>
    private void OnArgKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: MappableButton { Selected.Id: "key" } mb } tb) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab && Keyboard.Modifiers == ModifierKeys.None) return; // allow normal focus change
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;
        if (key == Key.Back && Keyboard.Modifiers == ModifierKeys.None) { tb.Text = ""; mb.Arg = ""; return; }
        var parts = new List<string>();
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) parts.Add("Ctrl");
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) parts.Add("Win");
        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) parts.Add("Alt");
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) parts.Add("Shift");
        var vk = KeyInterop.VirtualKeyFromKey(key);
        parts.Add(Input.VkName(vk));
        tb.Text = string.Join("+", parts);
        mb.Arg = tb.Text;
    }
}
