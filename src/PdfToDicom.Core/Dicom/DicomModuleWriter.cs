using System.Globalization;
using FellowOakDicom;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.Core.Dicom;

/// <summary>
/// Writes the DICOM modules that every object this software produces has in common,
/// whatever its SOP Class.
/// </summary>
/// <remarks>
/// <para>
/// An Encapsulated PDF object and a Secondary Capture image differ only in how the
/// payload is carried. Patient, General Study, General Series, General Equipment, SC
/// Equipment and SOP Common are identical in both, and so is the File Meta Information.
/// Writing them twice would mean a correction applied to one and forgotten in the other,
/// and the attributes concerned are the ones that decide which patient a document is
/// filed against.
/// </para>
/// <para>
/// The class holds no state beyond the configuration and the identifier source, so a
/// single instance can build any number of objects.
/// </para>
/// </remarks>
internal sealed class DicomModuleWriter
{
    private readonly AppConfiguration _configuration;
    private readonly IUidGenerator _uidGenerator;

    internal DicomModuleWriter(AppConfiguration configuration, IUidGenerator uidGenerator)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _uidGenerator = uidGenerator ?? throw new ArgumentNullException(nameof(uidGenerator));
    }

    /// <summary>Product version reported in Software Versions (0018,1020).</summary>
    internal static string ProductVersion =>
        typeof(DicomModuleWriter).Assembly.GetName().Version?.ToString(3) ?? "3.0.0";

    /// <summary>
    /// Writes Specific Character Set (0008,0005). The attribute is Type 1C and is
    /// required whenever any value uses an extended repertoire; it is always written
    /// here so the receiving system never has to guess the encoding.
    /// </summary>
    /// <remarks>
    /// Must be called before any text value is added, because the toolkit encodes each
    /// string as it is written using the repertoire declared at that moment.
    /// </remarks>
    internal void AddSpecificCharacterSet(DicomDataset dataset)
    {
        dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, _configuration.SpecificCharacterSetValue);
    }

    /// <summary>Writes the Patient module, PS3.3 C.7.1.1.</summary>
    internal static void AddPatientModule(DicomDataset dataset, PatientDemographics demographics)
    {
        string patientName = PersonNameFormatter.FromPatient(demographics);

        dataset.AddOrUpdate(DicomTag.PatientName, patientName);
        dataset.AddOrUpdate(
            DicomTag.PatientID,
            DicomValueRules.Sanitise(demographics.PatientId, DicomValueRules.MaxLongStringLength));

        // Patient's Birth Date is Type 2, so it is written even when unknown. The
        // validator prevents that in practice, but the encoder must not depend on it.
        dataset.AddOrUpdate(
            DicomTag.PatientBirthDate,
            demographics.DateOfBirth?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty);

        dataset.AddOrUpdate(DicomTag.PatientSex, demographics.Sex.ToDicomValue());
    }

    /// <summary>Writes the General Study module, PS3.3 C.7.2.1.</summary>
    internal void AddGeneralStudyModule(
        DicomDataset dataset,
        PatientDemographics demographics,
        string studyUid,
        DateTime now)
    {
        dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);

        // The study happened when the document was produced, which for an imported file is
        // its creation time rather than the moment of conversion. Without this a folder
        // worked through in a batch stamps every study with the same afternoon and sorts
        // uselessly in the archive. Content Date and Time, written elsewhere, still record
        // when this object was built.
        DateTime study = demographics.StudyDateTime ?? now;

        dataset.AddOrUpdate(DicomTag.StudyDate, study.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        dataset.AddOrUpdate(DicomTag.StudyTime, study.ToString("HHmmss", CultureInfo.InvariantCulture));

        dataset.AddOrUpdate(
            DicomTag.AccessionNumber,
            DicomValueRules.Sanitise(demographics.AccessionNumber, DicomValueRules.MaxShortStringLength));

        // Referring Physician's Name is Type 2: present, possibly empty.
        dataset.AddOrUpdate(
            DicomTag.ReferringPhysicianName,
            PersonNameFormatter.FromFreeText(demographics.ReferringPhysicianName));

        // Study ID is Type 2. When the operator does not supply one, a value derived from
        // the accession number keeps the study identifiable to the department.
        string studyId = !string.IsNullOrWhiteSpace(demographics.StudyId)
            ? demographics.StudyId
            : demographics.AccessionNumber;

        dataset.AddOrUpdate(
            DicomTag.StudyID,
            DicomValueRules.Sanitise(studyId, DicomValueRules.MaxShortStringLength));

        string studyDescription = FirstNonEmpty(
            demographics.StudyDescription,
            _configuration.DefaultStudyDescription);

        if (!string.IsNullOrEmpty(studyDescription))
        {
            dataset.AddOrUpdate(
                DicomTag.StudyDescription,
                DicomValueRules.Sanitise(studyDescription, DicomValueRules.MaxLongStringLength));
        }
    }

    /// <summary>
    /// Writes the series level attributes, which are the same three in the Encapsulated
    /// Document Series module (PS3.3 C.24.1) and the General Series module (C.7.3.1).
    /// </summary>
    /// <param name="modality">
    /// Value for Modality (0008,0060), which is Type 1 in both modules. It differs by
    /// object: a document series may be OT or DOC, while an image series is OT or XC.
    /// </param>
    /// <param name="seriesNumber">
    /// Overrides the configured series number. Used when one conversion produces more than
    /// one series, so that a viewer orders them predictably rather than arbitrarily.
    /// </param>
    internal void AddSeriesModule(
        DicomDataset dataset, string seriesUid, string modality, int? seriesNumber = null)
    {
        dataset.AddOrUpdate(
            DicomTag.Modality,
            DicomValueRules.Sanitise(modality, DicomValueRules.MaxShortStringLength));

        dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesUid);
        dataset.AddOrUpdate(
            DicomTag.SeriesNumber,
            (seriesNumber ?? _configuration.SeriesNumber).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Writes the General Equipment module, PS3.3 C.7.5.1.</summary>
    internal void AddGeneralEquipmentModule(DicomDataset dataset)
    {
        // Manufacturer is Type 2 and must always be present.
        dataset.AddOrUpdate(
            DicomTag.Manufacturer,
            DicomValueRules.Sanitise(_configuration.Manufacturer, DicomValueRules.MaxLongStringLength));

        AddIfNotEmpty(dataset, DicomTag.InstitutionName, _configuration.InstitutionName, DicomValueRules.MaxLongStringLength);
        AddIfNotEmpty(dataset, DicomTag.InstitutionalDepartmentName, _configuration.DepartmentName, DicomValueRules.MaxLongStringLength);
        AddIfNotEmpty(dataset, DicomTag.StationName, _configuration.StationName, DicomValueRules.MaxShortStringLength);

        dataset.AddOrUpdate(DicomTag.ManufacturerModelName, "PDF to DICOM Converter");
        dataset.AddOrUpdate(DicomTag.SoftwareVersions, ProductVersion);
    }

    /// <summary>
    /// Writes the SC Equipment module, PS3.3 C.8.6.1. Conversion Type (0008,0064) is
    /// Type 1 and describes how the object came into being.
    /// </summary>
    internal void AddScEquipmentModule(DicomDataset dataset)
    {
        dataset.AddOrUpdate(
            DicomTag.ConversionType,
            DicomValueRules.Sanitise(_configuration.ConversionType, DicomValueRules.MaxShortStringLength));
    }

    /// <summary>Writes the SOP Common module, PS3.3 C.12.1.</summary>
    internal void AddSopCommonModule(DicomDataset dataset, string sopClassUid, string sopUid, DateTime now)
    {
        dataset.AddOrUpdate(DicomTag.SOPClassUID, sopClassUid);
        dataset.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);

        dataset.AddOrUpdate(DicomTag.InstanceCreationDate, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        dataset.AddOrUpdate(DicomTag.InstanceCreationTime, now.ToString("HHmmss", CultureInfo.InvariantCulture));
        dataset.AddOrUpdate(DicomTag.InstanceCreatorUID, _uidGenerator.ImplementationClassUid);

        // Recording the offset from UTC removes any ambiguity about the local timestamps
        // above, which matters when an archive serves sites in different time zones.
        dataset.AddOrUpdate(DicomTag.TimezoneOffsetFromUTC, FormatUtcOffset(now));
    }

    /// <summary>
    /// Wraps the data set in a DICOM file and populates the File Meta Information group,
    /// which is what makes the result a valid Part 10 file rather than a bare data set.
    /// </summary>
    internal DicomFile CreateFileWithMetaInformation(DicomDataset dataset, string sopClassUid, string sopUid)
    {
        var file = new DicomFile(dataset);

        file.FileMetaInfo.AddOrUpdate(
            DicomTag.FileMetaInformationVersion,
            new byte[] { 0x00, 0x01 });

        file.FileMetaInfo.AddOrUpdate(DicomTag.MediaStorageSOPClassUID, sopClassUid);
        file.FileMetaInfo.AddOrUpdate(DicomTag.MediaStorageSOPInstanceUID, sopUid);
        file.FileMetaInfo.AddOrUpdate(DicomTag.TransferSyntaxUID, DicomConstants.ExplicitVrLittleEndianUid);
        file.FileMetaInfo.AddOrUpdate(DicomTag.ImplementationClassUID, _uidGenerator.ImplementationClassUid);
        file.FileMetaInfo.AddOrUpdate(DicomTag.ImplementationVersionName, _uidGenerator.ImplementationVersionName);

        // Source Application Entity Title records which system produced the object, which
        // is the first thing an archive administrator looks at when tracing an instance.
        file.FileMetaInfo.AddOrUpdate(
            DicomTag.SourceApplicationEntityTitle,
            DicomValueRules.Sanitise(_configuration.CallingAeTitle, DicomValueRules.MaxAeTitleLength));

        return file;
    }

    // ------------------------------------------------------------------ Helpers

    /// <summary>
    /// Formats the local offset from UTC as the signed four digit value required by the
    /// Timezone Offset From UTC attribute, for example "+0100".
    /// </summary>
    internal static string FormatUtcOffset(DateTime localTime)
    {
        TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(localTime);
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        TimeSpan absolute = offset.Duration();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sign}{absolute.Hours:00}{absolute.Minutes:00}");
    }

    /// <summary>Returns the first supplied value that is not null, empty or whitespace.</summary>
    internal static string FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim() ?? string.Empty;

    /// <summary>Adds a value only when it carries content, leaving Type 3 attributes absent otherwise.</summary>
    internal static void AddIfNotEmpty(DicomDataset dataset, DicomTag tag, string? value, int maxLength)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            dataset.AddOrUpdate(tag, DicomValueRules.Sanitise(value, maxLength));
        }
    }

    /// <summary>
    /// Builds a file name that a support engineer can recognise at a glance while
    /// remaining safe on a Windows file system and unique within the output folder.
    /// </summary>
    /// <param name="pageSuffix">
    /// Distinguishes the pages of a multi page source. Empty for a single object, which
    /// keeps the names produced for a PDF exactly as they were.
    /// </param>
    internal static string BuildFileName(PatientDemographics demographics, string sopUid, string pageSuffix = "")
    {
        string accession = SanitiseForFileName(demographics.AccessionNumber);
        string patientId = SanitiseForFileName(demographics.PatientId);

        // The final segment of the SOP Instance UID is enough to make the name unique
        // while keeping the path comfortably within the Windows length limit.
        string uidSuffix = sopUid.Split('.').LastOrDefault() ?? sopUid;
        if (uidSuffix.Length > 16)
        {
            uidSuffix = uidSuffix[^16..];
        }

        return $"{patientId}_{accession}_{uidSuffix}{pageSuffix}.dcm";
    }

    /// <summary>Replaces every character that is not safe in a file name with an underscore.</summary>
    internal static string SanitiseForFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (char c in value.Trim())
        {
            builder.Append(invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c);
        }

        string result = builder.ToString();
        return result.Length > 32 ? result[..32] : result;
    }
}
