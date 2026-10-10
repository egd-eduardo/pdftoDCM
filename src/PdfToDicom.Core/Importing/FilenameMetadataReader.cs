using System.Globalization;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Models;

namespace PdfToDicom.Core.Importing;

/// <summary>
/// What reading a file name produced.
/// </summary>
/// <param name="IsMatch">True when one of the configured patterns matched.</param>
/// <param name="Demographics">
/// The patient information read from the name. Null when nothing matched.
/// </param>
/// <param name="Pattern">The pattern that matched, for the log and for the operator.</param>
/// <param name="Fields">The raw captured values, before any interpretation.</param>
/// <param name="Notes">
/// Anything worth telling the operator: a date that could not be parsed, a field the
/// pattern did not carry. Never a reason to refuse the file.
/// </param>
public sealed record FilenameReadResult(
    bool IsMatch,
    PatientDemographics? Demographics,
    string Pattern,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<string> Notes)
{
    /// <summary>Nothing matched.</summary>
    public static FilenameReadResult NoMatch(IReadOnlyList<string> notes) =>
        new(false, null, string.Empty, new Dictionary<string, string>(), notes);
}

/// <summary>
/// Reads patient information out of a file name, using the patterns a site configured.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that a folder of documents produced by another system can be imported
/// without anybody retyping the identifiers that are already in the file names. The
/// patterns are configurable and tried in order, because the point is to accept whatever
/// the sending software happens to produce rather than to dictate a format to it.
/// </para>
/// <para>
/// <b>It reads; it does not decide.</b> What comes back is handed to the same validation
/// and the same confirmation as anything typed by hand. A file name is not a trustworthy
/// source of patient identity: it can be renamed by anyone, and a document filed against
/// the wrong patient is the error this application exists to make hard.
/// </para>
/// </remarks>
public sealed class FilenameMetadataReader
{
    private readonly FilenameImportConfiguration _configuration;
    private readonly IReadOnlyList<FilenamePattern> _patterns;
    private readonly IReadOnlyList<string> _patternErrors;

    /// <summary>
    /// Compiles the configured patterns, keeping any that are malformed as reportable
    /// errors rather than throwing.
    /// </summary>
    /// <remarks>
    /// A bad pattern must not stop the application starting. The others still work, and the
    /// error is surfaced where somebody can act on it.
    /// </remarks>
    public FilenameMetadataReader(FilenameImportConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

        var compiled = new List<FilenamePattern>();
        var errors = new List<string>();

        foreach (string template in _configuration.Patterns)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                continue;
            }

            try
            {
                compiled.Add(FilenamePattern.Compile(template));
            }
            catch (FormatException ex)
            {
                errors.Add(ex.Message);
            }
            catch (ArgumentException ex)
            {
                errors.Add(ex.Message);
            }
        }

        _patterns = compiled;
        _patternErrors = errors;
    }

    /// <summary>Patterns that would not compile, each with the reason.</summary>
    public IReadOnlyList<string> PatternErrors => _patternErrors;

    /// <summary>How many patterns are usable.</summary>
    public int UsablePatternCount => _patterns.Count;

    /// <summary>
    /// Reads a file name, trying each configured pattern in order.
    /// </summary>
    /// <param name="path">The file name or full path.</param>
    /// <param name="studyDateTime">
    /// When supplied, carried through as the study date and time. The caller passes the
    /// file's creation time: the study happened when the document was produced, not when
    /// this application got round to converting it.
    /// </param>
    public FilenameReadResult Read(string? path, DateTime? studyDateTime = null)
    {
        var notes = new List<string>(_patternErrors);

        if (_patterns.Count == 0)
        {
            if (_patternErrors.Count == 0)
            {
                notes.Add("No file name patterns are configured.");
            }

            return FilenameReadResult.NoMatch(notes);
        }

        // Every pattern is tried, and the one that reads the MOST fields wins; the
        // configured order only breaks a tie.
        //
        // First match wins was the obvious rule and it is a trap. A site that lists
        // "{PatientId}_{Name}" before "{PatientId}_{Name}_{AccessionNumber}_{DateOfBirth}"
        // gets the loose one, which matches the longer name perfectly well by swallowing
        // the accession number and date of birth into the name. Nothing fails; a patient
        // called "Ana Silva_ACC001_19700521" is simply filed. Choosing the fullest reading
        // means the order of the list cannot cost anybody their accession number.
        (FilenamePattern Pattern, IReadOnlyDictionary<string, string> Fields)? best = null;

        foreach (FilenamePattern pattern in _patterns)
        {
            IReadOnlyDictionary<string, string>? fields = pattern.Match(path);

            if (fields is null)
            {
                continue;
            }

            if (best is null || fields.Count > best.Value.Fields.Count)
            {
                best = (pattern, fields);
            }
        }

        if (best is not null)
        {
            PatientDemographics demographics = Build(best.Value.Fields, studyDateTime, notes);

            return new FilenameReadResult(
                true, demographics, best.Value.Pattern.Template, best.Value.Fields, notes);
        }

        notes.Add(
            $"'{FilenamePattern.StemOf(path)}' does not match any of the " +
            $"{_patterns.Count} configured pattern(s).");

        return FilenameReadResult.NoMatch(notes);
    }

    // ------------------------------------------------------------------ Interpretation

    private PatientDemographics Build(
        IReadOnlyDictionary<string, string> fields,
        DateTime? studyDateTime,
        List<string> notes)
    {
        var demographics = new PatientDemographics
        {
            PatientId = Value(fields, FilenameField.PatientId),
            AccessionNumber = Value(fields, FilenameField.AccessionNumber),
            StudyDescription = Value(fields, FilenameField.StudyDescription),

            // Taken from the configuration, "O" by default, because the file name does not
            // carry it and guessing a patient's sex from their name would be exactly the
            // kind of invention this application must not do. The operator can still set
            // it on the form before sending.
            Sex = PatientSexExtensions.FromDicomValue(_configuration.DefaultSex),

            StudyDateTime = studyDateTime
        };

        ApplyName(fields, demographics, notes);
        ApplyDateOfBirth(fields, demographics, notes);
        ApplyPatientIdPadding(demographics, notes);

        return demographics;
    }

    /// <summary>
    /// Restores the leading zeros a sending system dropped from a patient number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This changes the one value that decides whose record a document joins, so it does as
    /// little as it possibly can. A value that is not entirely digits is left alone, because
    /// zeros in front of <c>AB12</c> are nonsense rather than a restored identifier. A value
    /// already at or beyond the configured width is left alone too, and in particular is
    /// never truncated: the right hand end of an identifier is the part that tells two
    /// people apart.
    /// </para>
    /// <para>
    /// Every change is reported, so the operator and the log both see that the identifier
    /// written to the object is not the one the file name carried.
    /// </para>
    /// </remarks>
    private void ApplyPatientIdPadding(PatientDemographics demographics, List<string> notes)
    {
        int width = _configuration.PadPatientIdTo;

        if (width <= 0)
        {
            return;
        }

        string id = demographics.PatientId;

        if (id.Length == 0 || id.Length >= width)
        {
            return;
        }

        if (!id.All(char.IsAsciiDigit))
        {
            notes.Add(
                $"'{id}' was left as it is: it is not a plain number, so padding it to " +
                $"{width} digits would change the identifier rather than restore it.");
            return;
        }

        string padded = id.PadLeft(width, '0');
        demographics.PatientId = padded;

        notes.Add($"the patient number '{id}' was padded to {width} digits as '{padded}'.");
    }

    /// <summary>
    /// Fills the name, from separate components when the pattern carries them and from the
    /// single field when it does not.
    /// </summary>
    /// <remarks>
    /// Three forms are accepted in the single field, because all three turn up:
    /// "SILVA^ANA" is already a Person Name and is taken as one; "SILVA, ANA" puts the
    /// family name first, which is how a hospital system lists people; and "ANA SILVA" is
    /// how a person writes their own name, so the last word is the family name.
    /// </remarks>
    private void ApplyName(
        IReadOnlyDictionary<string, string> fields,
        PatientDemographics demographics,
        List<string> notes)
    {
        string family = Value(fields, FilenameField.FamilyName);
        string given = Value(fields, FilenameField.GivenName);

        if (family.Length > 0 || given.Length > 0)
        {
            demographics.FamilyName = family;
            demographics.GivenName = given;
            return;
        }

        string whole = Value(fields, FilenameField.Name);

        if (whole.Length == 0)
        {
            notes.Add("The pattern carries no name, so it has to be entered.");
            return;
        }

        if (whole.Contains('^', StringComparison.Ordinal))
        {
            string[] components = whole.Split('^');
            demographics.FamilyName = components[0].Trim();
            demographics.GivenName = components.Length > 1 ? components[1].Trim() : string.Empty;
            return;
        }

        if (whole.Contains(',', StringComparison.Ordinal))
        {
            string[] halves = whole.Split(',', 2);
            demographics.FamilyName = halves[0].Trim();
            demographics.GivenName = halves[1].Trim();
            return;
        }

        string[] words = whole.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (words.Length == 1)
        {
            if (_configuration.SplitRunTogetherNames)
            {
                string[] parts = SplitAtCapitals(words[0]);

                if (parts.Length > 1)
                {
                    demographics.FamilyName = parts[^1];
                    demographics.GivenName = string.Join(' ', parts[..^1]);

                    notes.Add(
                        $"'{words[0]}' was split at its capitals into " +
                        $"'{demographics.GivenName}' and '{demographics.FamilyName}'. " +
                        "Check that is right for the software sending these files.");

                    return;
                }
            }

            // The whole thing as the family name: incomplete, and not wrong. Splitting it
            // would be a guess, and a mangled patient name is worse than a partial one.
            demographics.FamilyName = words[0];
            return;
        }

        demographics.FamilyName = words[^1];
        demographics.GivenName = string.Join(' ', words[..^1]);
    }

    /// <summary>
    /// Breaks a run-together name at the capitals that start each word.
    /// </summary>
    /// <remarks>
    /// A capital preceded by a lower case letter starts a new word, which gets
    /// "JohnDoe" right. A run of capitals is kept together, so "JOHNDOE" and an initial
    /// like "JDoe" are not shredded into single letters. It is still a guess, which is why
    /// the caller reports what it did and the setting is off by default.
    /// </remarks>
    private static string[] SplitAtCapitals(string value)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];

            bool startsAWord =
                index > 0
                && char.IsUpper(character)
                && char.IsLower(value[index - 1]);

            if (startsAWord && current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words.ToArray();
    }

    private void ApplyDateOfBirth(
        IReadOnlyDictionary<string, string> fields,
        PatientDemographics demographics,
        List<string> notes)
    {
        string text = Value(fields, FilenameField.DateOfBirth);

        if (text.Length == 0)
        {
            return;
        }

        string[] formats = _configuration.EffectiveDateFormats;

        if (DateTime.TryParseExact(
                text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            demographics.DateOfBirth = parsed;
            return;
        }

        // Reported and left blank rather than refused. A date of birth is optional here,
        // and losing the whole import because one field is in an unexpected format would
        // be a poor trade.
        notes.Add(
            $"'{text}' was read as the date of birth and is not one of the configured " +
            $"formats ({string.Join(", ", formats)}), so it has been left blank.");
    }

    private static string Value(IReadOnlyDictionary<string, string> fields, string field) =>
        fields.TryGetValue(field, out string? value) ? value.Trim() : string.Empty;
}
