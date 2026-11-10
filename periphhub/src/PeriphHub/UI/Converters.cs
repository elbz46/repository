using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PeriphHub.UI;

public sealed class NotEmptyToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class ProfileName : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value as string == "*" ? "All applications" : value ?? "";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class HexToBrush : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value as string ?? "#000000")); }
        catch { return Brushes.Transparent; }
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class OnlineToOpacity : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is true ? 1.0 : 0.55;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class OnOff : IValueConverter
{
    public static OnOff Instance { get; } = new();
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is true ? "On" : "Off";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class MonoForDiag : IValueConverter
{
    public static MonoForDiag Instance { get; } = new();
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        new FontFamily(value is string s && s.StartsWith("diag") ? "Cascadia Mono, Consolas" : "Segoe UI Variable Text, Segoe UI");
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
