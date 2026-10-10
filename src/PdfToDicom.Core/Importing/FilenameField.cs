namespace PdfToDicom.Core.Importing;

/// <summary>
/// The fields a filename pattern can carry, and the spellings accepted for each.
/// </summary>
/// <remarks>
/// Aliases exist because the people writing a pattern will reach for whatever the sending
/// software calls the field. Refusing <c>{PatientID}</c> because the canonical spelling is
/// <c>{PatientId}</c> would be a pointless thing to make somebody debug.
/// </remarks>
public static class FilenameField
{
    /// <summary>Patient ID, the hospital number.</summary>
    public const string PatientId = "PatientId";

    /// <summary>The whole patient name in one field, split when it is used.</summary>
    public const string Name = "Name";

    /// <summary>Family name on its own, for software that separates them.</summary>
    public const string FamilyName = "FamilyName";

    /// <summary>Given name on its own.</summary>
    public const string GivenName = "GivenName";

    /// <summary>Accession Number.</summary>
    public const string AccessionNumber = "AccessionNumber";

    /// <summary>Patient's date of birth, parsed with the configured formats.</summary>
    public const string DateOfBirth = "DateOfBirth";

    /// <summary>Study Description.</summary>
    public const string StudyDescription = "StudyDescription";

    /// <summary>Every field, in the order they are usually written.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        PatientId, Name, FamilyName, GivenName, AccessionNumber, DateOfBirth, StudyDescription
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [PatientId] = PatientId,
        ["PatientID"] = PatientId,
        ["Patient"] = PatientId,
        ["Id"] = PatientId,
        ["MRN"] = PatientId,
        ["ProcessoClinico"] = PatientId,
        ["Processo"] = PatientId,

        [Name] = Name,
        ["PatientName"] = Name,
        ["FullName"] = Name,
        ["Nome"] = Name,

        [FamilyName] = FamilyName,
        ["Surname"] = FamilyName,
        ["LastName"] = FamilyName,
        ["Apelido"] = FamilyName,

        [GivenName] = GivenName,
        ["FirstName"] = GivenName,
        ["Forename"] = GivenName,
        ["NomeProprio"] = GivenName,

        [AccessionNumber] = AccessionNumber,
        ["Accession"] = AccessionNumber,
        ["AccessionNo"] = AccessionNumber,
        ["Acession"] = AccessionNumber,
        ["AccNo"] = AccessionNumber,
        ["Acesso"] = AccessionNumber,

        [DateOfBirth] = DateOfBirth,
        ["Dob"] = DateOfBirth,
        ["BirthDate"] = DateOfBirth,
        ["DataNascimento"] = DateOfBirth,

        [StudyDescription] = StudyDescription,
        ["Study"] = StudyDescription,
        ["Description"] = StudyDescription,
        ["Exame"] = StudyDescription,
    };

    /// <summary>
    /// The canonical field name for a spelling, or null when it is not a field.
    /// </summary>
    /// <remarks>
    /// "Acession", missing its second c, is accepted on purpose. It is the spelling that
    /// turns up in practice, and a pattern that silently did not match because of it would
    /// be an unpleasant thing to diagnose.
    /// </remarks>
    public static string? Resolve(string? token) =>
        token is not null && Aliases.TryGetValue(token.Trim(), out string? field) ? field : null;
}
