using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SchoolObservatory.Desktop.ViewModels;

/// <summary>Small base for incremental MVVM migration, without changing current WPF pages.</summary>
public abstract class ObservableViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetValue<T>(ref T field, T value,
        [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        return true;
    }
}
