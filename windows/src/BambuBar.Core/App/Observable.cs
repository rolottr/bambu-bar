using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BambuBar.Core.App;

/// <summary>Property change notifications for WPF bindings (the Mac's @Observable).</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }

    /// <summary>Tells bindings that <paramref name="name"/> (or, with null, everything) may read differently now.</summary>
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
