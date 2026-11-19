using FellowOakDicom;
using WorklistExplorer.Core.Query;

namespace WorklistExplorer.Core.Dicom;

/// <summary>
/// Builds the Modality Worklist C-FIND identifier attribute by attribute.
/// </summary>
/// <remarks>
/// fo-dicom's DicomCFindRequest.CreateWorklistQuery is deliberately not used: it sends a
/// Scheduled Procedure Step Start Time of 000000-000000, which matches only steps at
/// midnight and returns nothing from a real department. Building by hand also means every
/// key that goes on the wire is one written here, so the dataset shown to the user is the
/// whole truth about the question asked.
/// </remarks>
public static class WorklistQueryBuilder
{
    public const string Utf8CharacterSet = "ISO_IR 192";

    public static DicomDataset Build(QueryTarget target, DateSelection dates, DateTime today)
    {
        // NotValidated: a DA range such as 20251201-20251231 or an open range is a valid
        // matching key but not a valid stored DA value, and fo-dicom's validation is written
        // for stored values. Validation is turned off rather than switching to typed range
        // objects, so that the string sent is exactly the string shown.
        var ds = new DicomDataset().NotValidated();

        // Declares that the requester accepts UTF-8 answers. Without it a server may
        // transliterate or refuse names such as GONÇALVES or João.
        ds.Add(DicomTag.SpecificCharacterSet, Utf8CharacterSet);

        // Every attribute wanted back is sent empty. Empty is universal matching: it
        // excludes nothing and asks the server to report the value. A key that is not sent
        // is not returned, which is the usual cause of rows without a Study Instance UID.
        ds.Add(DicomTag.PatientName, string.Empty);
        ds.Add(DicomTag.PatientID, string.Empty);
        ds.Add(DicomTag.PatientBirthDate, string.Empty);
        ds.Add(DicomTag.PatientSex, string.Empty);
        ds.Add(DicomTag.AccessionNumber, string.Empty);
        ds.Add(DicomTag.ReferringPhysicianName, string.Empty);
        ds.Add(DicomTag.StudyInstanceUID, string.Empty);
        ds.Add(DicomTag.RequestedProcedureDescription, string.Empty);
        ds.Add(DicomTag.RequestedProcedureID, string.Empty);

        // The scheduled attributes live in exactly one sequence item.
        var sps = new DicomDataset().NotValidated();
        // Values sent are exactly as typed. Nothing is trimmed, upper cased or
        // transliterated, because the server matches what it receives literally.
        sps.Add(DicomTag.Modality, target.Modality ?? string.Empty);
        sps.Add(DicomTag.ScheduledStationAETitle, target.ScheduledStationAeTitle ?? string.Empty);
        sps.Add(DicomTag.ScheduledProcedureStepStartDate, dates.ToDicomValue(today));
        // Always empty. A time key is the trap CreateWorklistQuery falls into.
        sps.Add(DicomTag.ScheduledProcedureStepStartTime, string.Empty);
        sps.Add(DicomTag.ScheduledProcedureStepDescription, string.Empty);
        sps.Add(DicomTag.ScheduledProcedureStepID, string.Empty);
        sps.Add(DicomTag.ScheduledPerformingPhysicianName, string.Empty);

        ds.Add(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, sps));
        return ds;
    }

    /// <summary>
    /// The question in words, used whenever nothing comes back, so that an empty answer is
    /// never reported without saying what was asked.
    /// </summary>
    public static string DescribeQuestion(QueryTarget target, DateSelection dates, DateTime today)
    {
        var modality = string.IsNullOrEmpty(target.Modality) ? "every modality" : $"modality '{target.Modality}'";
        var station = string.IsNullOrEmpty(target.ScheduledStationAeTitle) ? "every scheduled station" : $"scheduled station '{target.ScheduledStationAeTitle}'";
        return $"asked for {dates.Describe(today)}, {modality}, {station}, from {target.Endpoint}";
    }
}
