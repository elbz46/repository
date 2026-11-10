using System.Threading;
using System.Windows;
using PeriphHub.Core;
using PeriphHub.Drivers.Asus;
using PeriphHub.Drivers.Generic;
using PeriphHub.Drivers.Logitech;
using PeriphHub.UI;

namespace PeriphHub;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private TrayIcon? _tray;
    public static DeviceHub Hub { get; } = new();
    public static KeyRemapDevice? Keys { get; private set; }
    public static AppSettingsPage? AppPage { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, @"Local\PeriphHub.SingleInstance", out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PeriphHub.Show");
        if (!first)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) => { Log.Error("Unhandled UI exception", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Unhandled exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

        Log.Info($"PeriphHub {typeof(App).Assembly.GetName().Version} starting on {Environment.OSVersion}");

        var window = new MainWindow();
        MainWindow = window;
        _tray = new TrayIcon(window);
        Toast.Requested += t => _tray.ShowToast(t);

        Keys = new KeyRemapDevice();
        AppPage = new AppSettingsPage();
        Hub.AddProvider(new AsusProvider());
        Hub.AddProvider(new LogitechProvider());
        Hub.AddProvider(new GenericProvider());
        Hub.Add(Keys);
        _ = Keys.ApplySavedAsync();
        Keys.Start();
        if (e.Args.Contains("--demo"))
        {
            Hub.Add(new DemoDevice("MX Master 2S (demo)", DeviceKind.Mouse));
            Hub.Add(new DemoDevice("G321 LIGHTSPEED (demo)", DeviceKind.Headset));
        }
        Hub.Start();

        if (!e.Args.Contains("--minimized")) window.Show();

        var listener = new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(window.ShowAndActivate);
        }) { IsBackground = true };
        listener.Start();
    }

    public void Quit()
    {
        SettingsStore.Instance.SaveNow();
        _tray?.Dispose();
        Hub.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SettingsStore.Instance.SaveNow();
        base.OnExit(e);
    }
}
