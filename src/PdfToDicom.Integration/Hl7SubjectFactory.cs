using System.Globalization;
using PdfToDicom.Core.Models;
using PdfToDicom.Interop.Messaging;

namespace PdfToDicom.Integration;

/// <summary>
/// Turns what a conversion knows into the named values a profile maps from.
/// </summary>
/// <remarks>
/// <para>
/// Every name here is part of the configuration surface: an administrator writes these
/// into a profile's mappings, so removing or renaming one breaks a site's configuration
/// exactly as removing a setting would. They are listed in the integration documentation
/// for that reason.
/// </para>
/// <para>
/// Nothing is invented. An absent date of birth produces an empty string, never today's
/// date, and an unspecified sex produces an empty code rather than a guess. That is the
/// same rule the DICOM side follows for Type 2 attributes, and for the same reason: a
/// plausible value nobody supplied is worse than an obvious gap.
/// </para>
/// </remarks>
public static class Hl7SubjectFactory
{
    /// <summary>Builds the subject for one converted document.</summary>
    /// <param name="demographics">What the operator entered or the worklist supplied.</param>
    /// <param name="result">What the conversion produced, when it produced anything.</param>
    /// <param name="now">The current moment, injectable so a test is not at the mercy of a clock.</param>
    public static MessageSubject From(
        PatientDemographics demographics,
        ConversionResult? result = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(demographics);

        DateTimeOffset moment = now ?? DateTimeOffset.Now;

        var subject = new MessageSubject()
            .With("now", moment.ToString("o", CultureInfo.InvariantCulture))
            .With("today", moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))

            .With("patient.id", demographics.PatientId)
            .With("patient.familyName", demographics.FamilyName)
            .With("patient.givenName", demographics.GivenName)
            .With("patient.middleName", demographics.MiddleName)
            .With("patient.prefix", demographics.NamePrefix)
            .With("patient.suffix", demographics.NameSuffix)
            .With("patient.dateOfBirth", Iso(demographics.DateOfBirth))

            // Both forms, because a profile may map either. The name suits a code table
            // written in words; the code is what most interfaces want and is the same
            // letter the DICOM object carries, so the two systems cannot disagree.
            .With("patient.sex", demographics.Sex.ToString())
            .With("patient.sexCode", demographics.Sex.ToDicomValue())

            .With("study.accessionNumber", demographics.AccessionNumber)
            .With("study.description", demographics.StudyDescription)
            .With("study.id", demographics.StudyId)
            .With("study.dateTime", Iso(demographics.StudyDateTime))
            .With("series.description", demographics.SeriesDescription)
            .With("document.title", demographics.DocumentTitle)
            .With("referring.physician", demographics.ReferringPhysicianName);

        if (result is null)
        {
            return subject;
        }

        return subject
            .With("study.instanceUid", result.StudyInstanceUid ?? string.Empty)
            .With("series.instanceUid", result.SeriesInstanceUid ?? string.Empty)
            .With("document.sopInstanceUid", result.SopInstanceUid ?? string.Empty)
            .With("document.instanceCount",
                result.InstanceCount.ToString(CultureInfo.InvariantCulture))
            .With("document.path", result.GeneratedFilePath ?? string.Empty);
    }

    /// <summary>
    /// Writes a date in ISO 8601, or nothing at all.
    /// </summary>
    /// <remarks>
    /// Deliberately not written in an HL7 form here. How a date is formatted is the
    /// profile's business, expressed with a transform, because one destination wants eight
    /// digits and the next wants fourteen. Formatting it at the source would make that a
    /// code change.
    /// </remarks>
    private static string Iso(DateTime? value) =>
        value.HasValue
            ? value.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
            : string.Empty;
}
