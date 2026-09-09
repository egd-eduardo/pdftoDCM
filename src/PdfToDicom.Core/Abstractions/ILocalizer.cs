namespace PdfToDicom.Core.Abstractions;

/// <summary>
/// Supplies user interface text in the configured language.
/// </summary>
public interface ILocalizer
{
    /// <summary>Culture name currently in use, for example "pt-PT".</summary>
    string CurrentLanguage { get; }

    /// <summary>Culture names for which translations are available.</summary>
    IReadOnlyList<LanguageOption> AvailableLanguages { get; }

    /// <summary>Raised when the language changes, so views can refresh their bindings.</summary>
    event EventHandler? LanguageChanged;

    /// <summary>
    /// Returns the translated string for the supplied key. When the key is missing in
    /// the active language the English text is returned, and when it is missing there
    /// too the key itself is returned so the omission is visible rather than blank.
    /// </summary>
    string this[string key] { get; }

    /// <summary>Returns the translated string for the supplied key.</summary>
    string Get(string key);

    /// <summary>Returns the translated string with positional arguments substituted.</summary>
    string Format(string key, params object[] arguments);

    /// <summary>Switches the active language.</summary>
    void SetLanguage(string cultureName);
}

/// <summary>An available user interface language.</summary>
/// <param name="CultureName">Culture name, for example "pt-PT".</param>
/// <param name="DisplayName">Name shown in the language selector, in that language.</param>
public sealed record LanguageOption(string CultureName, string DisplayName);
