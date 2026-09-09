using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PdfToDicom.App.Infrastructure;

/// <summary>
/// Base class for view models, providing change notification.
/// </summary>
/// <remarks>
/// Implemented directly rather than taken from a toolkit package. The requirement is a
/// few dozen lines, and every dependency avoided is one less assembly to embed in the
/// self contained executable and one less licence to account for in a hospital.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/> for the named property.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Assigns a value to a backing field and raises change notification when the value
    /// actually differs.
    /// </summary>
    /// <returns>True when the value changed.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
