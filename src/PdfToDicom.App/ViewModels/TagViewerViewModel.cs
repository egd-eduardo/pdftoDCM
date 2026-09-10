using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using FellowOakDicom;
using PdfToDicom.App.Infrastructure;
using PdfToDicom.Core.Dicom;
using PdfToDicom.Core.Services;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.App.ViewModels;

/// <summary>
/// View model for the DICOM tag viewer.
/// </summary>
/// <remarks>
/// The viewer exists so that an advanced user, typically a PACS administrator during
/// commissioning, can confirm exactly what the application will send before it is sent,
/// and can copy the whole listing into a support ticket.
/// </remarks>
public sealed class TagViewerViewModel : ObservableObject
{
    private readonly ApplicationServices _services;
    private readonly DicomFile _file;
    private readonly ICollectionView _view;

    private string _searchText = string.Empty;
    private string _validationSummary = string.Empty;

    /// <summary>Creates the view model over an already generated object.</summary>
    public TagViewerViewModel(ApplicationServices services, DicomFile file)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _file = file ?? throw new ArgumentNullException(nameof(file));

        Rows = new ObservableCollection<DicomTagRow>(_services.TagInspector.Inspect(file));

        _view = CollectionViewSource.GetDefaultView(Rows);
        _view.Filter = MatchesSearch;

        CopyAllCommand = new RelayCommand(CopyAll);

        RunValidation();
    }

    /// <summary>Every attribute of the object, file meta information first.</summary>
    public ObservableCollection<DicomTagRow> Rows { get; }

    /// <summary>Copies the whole listing to the clipboard.</summary>
    public RelayCommand CopyAllCommand { get; }

    /// <summary>Free text filter applied to the tag, name and value columns.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _view.Refresh();
            }
        }
    }

    /// <summary>Result of re-running the IOD validation over the displayed object.</summary>
    public string ValidationSummary
    {
        get => _validationSummary;
        private set => SetProperty(ref _validationSummary, value);
    }

    /// <summary>True when the displayed object passed validation.</summary>
    public bool IsValid { get; private set; }

    /// <summary>Number of attributes currently shown by the filter.</summary>
    public int VisibleCount => _view.Cast<object>().Count();

    private bool MatchesSearch(object item)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        if (item is not DicomTagRow row)
        {
            return false;
        }

        return row.TagDisplay.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || row.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || row.Value.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || row.Vr.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void RunValidation()
    {
        ValidationResult validation = _services.DicomValidator.Validate(_file);
        IsValid = validation.IsValid;

        ValidationSummary = validation.IsValid
            ? "The object conforms to the Encapsulated PDF definition."
            : "The object does NOT conform:" + Environment.NewLine + validation.ToDisplayString();

        OnPropertyChanged(nameof(IsValid));
    }

    private void CopyAll()
    {
        try
        {
            Clipboard.SetText(_services.TagInspector.ToText(Rows));
        }
        catch (Exception ex)
        {
            // The clipboard can be locked by another application; this must not crash the
            // viewer, so the failure is logged and the user simply sees nothing copied.
            _services.Logger.Warning($"The tag listing could not be copied to the clipboard: {ex.Message}");
        }
    }
}
