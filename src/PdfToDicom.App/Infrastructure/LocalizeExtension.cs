using System.Windows.Data;
using System.Windows.Markup;
using PdfToDicom.Core.Abstractions;

namespace PdfToDicom.App.Infrastructure;

/// <summary>
/// Adapts the localizer to WPF data binding so that a language change updates every
/// bound label without the views having to be reloaded.
/// </summary>
/// <remarks>
/// The class exposes an indexer, which WPF binds to with a path of the form
/// <c>[Key]</c>. Raising a change notification for the indexer invalidates every such
/// binding at once, which is what makes runtime language switching work.
/// </remarks>
public sealed class LocalizationSource : ObservableObject
{
    private static LocalizationSource? _instance;

    private ILocalizer? _localizer;

    private LocalizationSource()
    {
    }

    /// <summary>The single instance that XAML binds against.</summary>
    public static LocalizationSource Instance => _instance ??= new LocalizationSource();

    /// <summary>Returns the translated text for a key.</summary>
    public string this[string key] => _localizer?.Get(key) ?? key;

    /// <summary>
    /// Connects the source to the application's localizer. Called once during startup.
    /// </summary>
    public void Attach(ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        if (_localizer is not null)
        {
            _localizer.LanguageChanged -= OnLanguageChanged;
        }

        _localizer = localizer;
        _localizer.LanguageChanged += OnLanguageChanged;
        RefreshAll();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshAll();

    /// <summary>
    /// Invalidates every binding that goes through the indexer. Passing "Item[]" is the
    /// documented way to tell WPF that all indexed values have changed.
    /// </summary>
    private void RefreshAll() => OnPropertyChanged("Item[]");
}

/// <summary>
/// XAML markup extension that binds a property to a translated string.
/// </summary>
/// <example>
/// <code>&lt;TextBlock Text="{loc:Localize Step1.Header}" /&gt;</code>
/// </example>
public sealed class LocalizeExtension : MarkupExtension
{
    /// <summary>Creates the extension without a key, for XAML property element syntax.</summary>
    public LocalizeExtension()
    {
    }

    /// <summary>Creates the extension with the resource key.</summary>
    public LocalizeExtension(string key)
    {
        Key = key;
    }

    /// <summary>The resource key to look up.</summary>
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationSource.Instance,
            Mode = BindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }
}
