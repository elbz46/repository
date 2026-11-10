using System.Collections.ObjectModel;
using System.IO;

namespace PeriphHub.Core;

/// <summary>Tiny file + in-memory logger. The in-memory part feeds the Diagnostics page.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static ObservableCollection<string> Recent { get; } = new();
    public static string FilePath => Path.Combine(SettingsStore.DataDir, "periphhub.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERR ", ex == null ? msg : $"{msg}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {msg}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.DataDir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* logging must never crash the app */ }
        }
        var app = System.Windows.Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            Recent.Add(line);
            while (Recent.Count > 500) Recent.RemoveAt(0);
        });
    }
}
