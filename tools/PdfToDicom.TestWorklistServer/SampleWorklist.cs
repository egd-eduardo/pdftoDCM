using System.Globalization;
using FellowOakDicom;

namespace PdfToDicom.TestWorklistServer;

/// <summary>
/// The invented scheduled procedure steps this server answers with.
/// </summary>
/// <remarks>
/// <para>
/// Chosen to exercise the things that actually break. Two of these names are longer than
/// the sixty four characters a DICOM Person Name allows, which is the everyday case in
/// Portugal and the one that forces the shortening rule to be exercised rather than
/// assumed. Several carry accented characters, so a character set fault shows up here
/// rather than in a hospital.
/// </para>
/// <para>
/// None of it describes a real person. The identifiers are obviously invented and the
/// procedures are ordinary.
/// </para>
/// </remarks>
internal static class SampleWorklist
{
    internal sealed record Item(
        string PatientId,
        string FamilyName,
        string GivenName,
        string AccessionNumber,
        string BirthDate,
        string Sex,
        string Modality,
        string ProcedureDescription,
        int MinutesFromStartOfDay);

    internal static IReadOnlyList<Item> Items { get; } = new[]
    {
        // Ordinary, comfortably within the limit.
        new Item("P0000001", "SILVA", "ANA", "ACC20260001", "19850214", "F",
                 "OT", "Relatório de Consulta", 9 * 60),

        // Accented throughout, still within the limit.
        new Item("P0000002", "GONÇALVES", "JOÃO MIGUEL", "ACC20260002", "19700903", "M",
                 "OT", "Relatório de Radiologia", 9 * 60 + 30),

        // 59 characters once joined, so it fits with little to spare. Included because a
        // name this long passing through untouched is as worth proving as one that cannot.
        new Item("P0000003", "APARÍCIO DA SILVA PEREIRA", "MARIA DO CARMO FERNANDA CONCEIÇÃO",
                 "ACC20260003", "19621130", "F", "OT", "Relatório de Ecografia", 10 * 60),

        // 73 characters once joined, so over the limit. The surname is 43, which leaves
        // twenty for the given name: enough for "JOSÉ ANTÓNIO" and not for what follows.
        new Item("P0000004", "ALBUQUERQUE MONTENEGRO DOS SANTOS RODRIGUES", "JOSÉ ANTÓNIO FRANCISCO XAVIER",
                 "ACC20260004", "19551007", "M", "OT", "Relatório de Tomografia Computorizada", 10 * 60 + 45),

        // No given name at all, which a worklist is entitled to send.
        new Item("P0000005", "FERREIRA", "", "ACC20260005", "19900621", "O",
                 "OT", "Relatório de Análises", 11 * 60),

        // A different modality, so filtering by modality can be seen to work.
        new Item("P0000006", "MARTINS", "BEATRIZ", "ACC20260006", "20010418", "F",
                 "CR", "Radiografia de Tórax", 11 * 60 + 30),

        // A surname of 69 characters on its own: the pathological case, where there is no
        // room for a given name at all and the surname itself has to be cut.
        new Item("P0000007", "VASCONCELOS CAVALCANTI DE ALMEIDA BARBOSA MAGALHÃES NASCIMENTO JUNIOR",
                 "PEDRO", "ACC20260007", "19781225", "M", "OT", "Relatório de Consulta Externa", 12 * 60)
    };

    /// <summary>
    /// Builds the response data set for one item, scheduled on the supplied day.
    /// </summary>
    internal static DicomDataset ToDataset(Item item, DateTime day)
    {
        var step = new DicomDataset();
        step.AddOrUpdate(DicomTag.Modality, item.Modality);
        step.AddOrUpdate(DicomTag.ScheduledStationAETitle, "TESTWL");
        step.AddOrUpdate(
            DicomTag.ScheduledProcedureStepStartDate,
            day.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        step.AddOrUpdate(
            DicomTag.ScheduledProcedureStepStartTime,
            day.Date.AddMinutes(item.MinutesFromStartOfDay).ToString("HHmmss", CultureInfo.InvariantCulture));
        step.AddOrUpdate(DicomTag.ScheduledProcedureStepDescription, item.ProcedureDescription);
        step.AddOrUpdate(DicomTag.ScheduledProcedureStepID, item.AccessionNumber);
        step.AddOrUpdate(DicomTag.ScheduledPerformingPhysicianName, "RIBEIRO^ANA");

        // Validation is switched off for this data set on purpose. Several of the sample
        // names are longer than the sixty four characters DICOM allows, which is exactly
        // the case the client has to cope with, and a validating data set would refuse to
        // hold them. A real worklist server will happily send such a name; refusing to
        // reproduce one here would make the case untestable.
        var dataset = new DicomDataset().NotValidated();

        // Written first, so every text value after it is encoded as UTF-8. Without this
        // the accented names would be mangled on the wire.
        dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, "ISO_IR 192");

        dataset.AddOrUpdate(DicomTag.PatientID, item.PatientId);
        dataset.AddOrUpdate(ComposeName(item));
        dataset.AddOrUpdate(DicomTag.PatientBirthDate, item.BirthDate);
        dataset.AddOrUpdate(DicomTag.PatientSex, item.Sex);
        dataset.AddOrUpdate(DicomTag.AccessionNumber, item.AccessionNumber);
        dataset.AddOrUpdate(DicomTag.ReferringPhysicianName, "COSTA^MANUEL");
        dataset.AddOrUpdate(DicomTag.RequestedProcedureDescription, item.ProcedureDescription);
        dataset.AddOrUpdate(DicomTag.RequestedProcedureID, item.AccessionNumber);

        // The Study Instance UID the order already created. Taking it is what lets a
        // converted document land in the study the examination belongs to.
        dataset.AddOrUpdate(DicomTag.StudyInstanceUID, StudyUidFor(item));

        dataset.AddOrUpdate(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, step));

        return dataset;
    }

    /// <summary>
    /// Composes Patient's Name without validation, so names longer than DICOM allows can
    /// be served deliberately.
    /// </summary>
    /// <remarks>
    /// A real worklist server will happily send one, which is precisely why the client has
    /// to cope. Building the value through a validating path here would make it impossible
    /// to reproduce the case under test.
    /// </remarks>
    private static DicomPersonName ComposeName(Item item) =>
        new(DicomTag.PatientName, item.FamilyName, item.GivenName);

    /// <summary>A stable, obviously invented Study Instance UID for each item.</summary>
    private static string StudyUidFor(Item item) =>
        $"1.2.826.0.1.3680043.9.6008.99.{item.PatientId.TrimStart('P').TrimStart('0')}";
}
