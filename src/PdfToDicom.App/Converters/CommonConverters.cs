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
