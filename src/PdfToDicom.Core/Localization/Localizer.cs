using System.Reflection;
using System.Text.Json;
using PdfToDicom.Core.Abstractions;

namespace PdfToDicom.Core.Localization;

/// <summary>
/// Loads user interface strings from JSON resources embedded in the assembly.
/// </summary>
/// <remarks>
/// Embedded JSON is used rather than satellite assemblies so that the published single
/// file executable carries every language with no additional files to deploy. English is
/// always loaded as the fallback layer, which means a missing translation degrades to
/// readable English rather than to a blank label.
/// </remarks>
public sealed class Localizer : ILocalizer
{
    private const string FallbackLanguage = "en-US";
    private const string ResourcePrefix = "PdfToDicom.Core.Localization.Strings.";

    private readonly Dictionary<string, Dictionary<string, string>> _languages =
        new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, string> _active = new(StringComparer.Ordinal);
    private Dictionary<string, string> _fallback = new(StringComparer.Ordinal);

    /// <summary>Creates the localizer and activates the supplied language.</summary>
    /// <param name="cultureName">Culture to activate, for example "pt-PT".</param>
    public Localizer(string cultureName = FallbackLanguage)
    {
        LoadEmbeddedLanguages();
        _fallback = _languages.TryGetValue(FallbackLanguage, out var english)
            ? english
            : new Dictionary<string, string>(StringComparer.Ordinal);

        SetLanguage(cultureName);
    }

    /// <inheritdoc />
    public string CurrentLanguage { get; private set; } = FallbackLanguage;

    /// <inheritdoc />
    public IReadOnlyList<LanguageOption> AvailableLanguages { get; private set; } =
        Array.Empty<LanguageOption>();

    /// <inheritdoc />
    public event EventHandler? LanguageChanged;

    /// <inheritdoc />
    public string this[string key] => Get(key);

    /// <inheritdoc />
    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        if (_active.TryGetValue(key, out string? value))
        {
            return value;
        }

        if (_fallback.TryGetValue(key, out string? englishValue))
        {
            return englishValue;
        }

        // Returning the key makes an omission obvious during testing instead of
        // producing an empty control that looks like a layout defect.
        return key;
    }

    /// <inheritdoc />
    public string Format(string key, params object[] arguments)
    {
        string template = Get(key);

        try
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, template, arguments);
        }
        catch (FormatException)
        {
            // A malformed placeholder in a translation must not crash the interface.
            return template;
        }
    }

    /// <inheritdoc />
    public void SetLanguage(string cultureName)
    {
        string resolved = ResolveLanguage(cultureName);

        if (string.Equals(resolved, CurrentLanguage, StringComparison.OrdinalIgnoreCase) &&
            _active.Count > 0)
        {
            return;
        }

        _active = _languages.TryGetValue(resolved, out var strings)
            ? strings
            : _fallback;

        CurrentLanguage = resolved;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Chooses the best available language: an exact match, then a match on the language
    /// part alone (so "pt-BR" falls back to "pt-PT" if that is what is shipped), then English.
    /// </summary>
    private string ResolveLanguage(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return FallbackLanguage;
        }

        if (_languages.ContainsKey(cultureName))
        {
            return cultureName;
        }

        string languagePart = cultureName.Split('-')[0];
        string? relative = _languages.Keys.FirstOrDefault(
            k => k.StartsWith(languagePart + "-", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(k, languagePart, StringComparison.OrdinalIgnoreCase));

        return relative ?? FallbackLanguage;
    }

    /// <summary>Reads every embedded language resource into memory.</summary>
    private void LoadEmbeddedLanguages()
    {
        Assembly assembly = typeof(Localizer).Assembly;
        var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip };
        var discovered = new List<LanguageOption>();

        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string cultureName = resourceName[ResourcePrefix.Length..^".json".Length];

            try
            {
                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                {
                    continue;
                }

                var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, options);
                if (strings is null || strings.Count == 0)
                {
                    continue;
                }

                _languages[cultureName] = new Dictionary<string, string>(strings, StringComparer.Ordinal);

                strings.TryGetValue("Language.DisplayName", out string? displayName);
                discovered.Add(new LanguageOption(cultureName, displayName ?? cultureName));
            }
            catch (JsonException)
            {
                // A malformed translation file is skipped so the application still starts.
            }
        }

        AvailableLanguages = discovered
            .OrderBy(l => l.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }
}
