using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PdfToDicom.App.ViewModels;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.App.Converters;

/// <summary>Maps true to <see cref="Visibility.Visible"/> and false to collapsed.</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>When true, the mapping is reversed.</summary>
    public bool Invert { get; set; }

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;

        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("This converter is one way.");
}

/// <summary>Maps a null or empty string to collapsed.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("This converter is one way.");
}

/// <summary>
/// Maps the workflow state to the colour of the status indicator, so the operator can
/// read the outcome at a glance from across the room.
/// </summary>
public sealed class WorkflowStateToBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value is WorkflowState state
            ? state switch
            {
                WorkflowState.Completed => "SuccessBrush",
                WorkflowState.Error => "ErrorBrush",

                // Amber rather than red. The document reached the archive; something else
                // did not, and the two must not look the same to somebody deciding whether
                // to send the document again.
                WorkflowState.NeedsAttention => "WarningBrush",
                WorkflowState.Ready => "SecondaryTextBrush",
                _ => "AccentBrush"
            }
            : "SecondaryTextBrush";

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("This converter is one way.");
}

/// <summary>Maps a validation severity to the brush used for its message.</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value is ValidationSeverity severity
            ? severity switch
            {
                ValidationSeverity.Error => "ErrorBrush",
                ValidationSeverity.Warning => "WarningBrush",
                _ => "SecondaryTextBrush"
            }
            : "SecondaryTextBrush";

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("This converter is one way.");
}

/// <summary>Maps a transmission outcome to a brush, used to colour the result rows.</summary>
public sealed class TransmissionOutcomeToBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value is TransmissionOutcome outcome && outcome == TransmissionOutcome.Succeeded
            ? "SuccessBrush"
            : "ErrorBrush";

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("This converter is one way.");
}

/// <summary>Maps a transmission outcome to a short textual marker.</summary>
public sealed class TransmissionOutcomeToTextConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is TransmissionOutcome outcome && outcome == TransmissionOutcome.Succeeded ? "OK" : "FAILED";

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("This converter is one way.");
}

/// <summary>
/// Carries a data context to somewhere that cannot reach one.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="System.Windows.Controls.DataGridColumn"/> is not part of the visual tree.
/// It has no inherited DataContext and no ancestor to walk up to, so the obvious
/// <c>RelativeSource AncestorType=Window</c> binding on a column's Visibility resolves to
/// nothing. WPF reports that only as a trace message, so the symptom is a column that
/// never hides and no error anywhere.
/// </para>
/// <para>
/// Declaring one of these as a resource gives the column something it CAN bind to: a
/// resource lookup works from a column, and this object's own DataContext is inherited
/// normally because it is a <see cref="Freezable"/>, which takes part in that inheritance
/// even though it is not a visual.
/// </para>
/// <para>
/// This is the established WPF answer to the problem rather than a trick of ours, and it
/// is the reason the source column on the worklist window can be hidden when only one
/// server was queried.
/// </para>
/// </remarks>
public sealed class BindingProxy : Freezable
{
    /// <summary>The object bindings are routed through, normally the view model.</summary>
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    /// <summary>The object bindings are routed through, normally the view model.</summary>
    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <inheritdoc />
    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
