using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PeriphHub.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
    {
        var handler = PropertyChanged;
        if (handler == null) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            handler(this, new PropertyChangedEventArgs(name));
        else
            dispatcher.BeginInvoke(() => handler(this, new PropertyChangedEventArgs(name)));
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}
