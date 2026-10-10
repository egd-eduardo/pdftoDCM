using System.Globalization;
using System.Reflection;
using FellowOakDicom;

namespace PdfToDicom.TestWorklistServer;

/// <summary>
/// The invented scheduled procedure steps this server answers with.
/// </summary>
/// <remarks>
/// <para>
/// What is served is two things joined: a small set of <see cref="Awkward"/> entries that
/// are always present, and a handful drawn at random each run from a pool of five hundred
/// ordinary Portuguese names.
/// </para>
/// <para>
/// <b>Why the awkward ones are not in the pool.</b> They are the cases that actually
/// break something: two names longer than the sixty four characters a DICOM Person Name
/// allows, a patient with no given name at all, and a different modality. The pool is
/// realistic but uniformly well behaved, with the longest name composing to thirty one
/// characters, so a random draw from it would exercise the shortening rule roughly never.
/// Keeping the awkward entries out of the draw is what stops this server quietly becoming
/// a test of nothing.
/// </para>
/// <para>
/// <b>Everything about a pool entry is derived from its position</b>, so a given name
/// always carries the same Patient ID, accession number, birth date and scheduled time.
/// Only which ten are drawn varies. That means a run can be reproduced from a seed, and
/// that converting for the same patient twice files into the same study, while the list in
/// front of the operator still looks different each time.
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

    /// <summary>
    /// Stamped onto every identifier and description this server sends, so that anything
    /// generated from it is unmistakably a test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not cosmetic. Somebody commissioning a worklist is one wrong destination
    /// away from putting these studies into a live archive, and a study that cannot be
    /// told apart from a real one is then a clinical problem rather than an inconvenience.
    /// Every field a client can take from this server and write into an object carries the
    /// mark: the Patient ID, the accession number, the study and procedure descriptions
    /// and both physician names. A study list, a patient lookup and the study description
    /// therefore all show it without anybody having to know what to look for.
    /// </para>
    /// <para>
    /// <b>The patient names are deliberately left alone.</b> They are the one field whose
    /// realism is the thing under test: the shortening rule turns on their exact length,
    /// and prefixing them would both break that and make the accented characters harder to
    /// read. The identifiers carry the mark instead, and they are what an archive indexes
    /// and what staff check a patient against.
    /// </para>
    /// <para>
    /// Study Instance UIDs are issued under <c>...9.6008.99.</c>, a branch used for nothing
    /// else, so even an object whose text has been stripped can be traced back here.
    /// </para>
    /// </remarks>
    internal const string TestMarker = "TESTE";

    /// <summary>How many pool entries are drawn when nothing else is asked for.</summary>
    internal const int DefaultRandomCount = 10;

    private static IReadOnlyList<Item>? _items;

    /// <summary>
    /// The entries this server is answering with for the life of the process.
    /// </summary>
    /// <remarks>
    /// Chosen once, by <see cref="Choose"/>, rather than per association. A list that
    /// changed between the operator pressing Search and pressing it again would be a
    /// confusing thing to hand somebody who is trying to establish whether their worklist
    /// works.
    /// </remarks>
    internal static IReadOnlyList<Item> Items =>
        _items ?? throw new InvalidOperationException(
            $"{nameof(Choose)} must be called before the worklist is served.");

    /// <summary>
    /// Fixes the set to serve: every awkward entry, plus <paramref name="randomCount"/>
    /// drawn from the pool.
    /// </summary>
    /// <param name="randomCount">
    /// How many to draw. Zero serves only the awkward entries, which is what every version
    /// before this one did; more than the pool holds draws the whole pool.
    /// </param>
    /// <param name="seed">
    /// Fixes the draw, so a run can be repeated. Null draws differently each time, which
    /// is the point of the feature; a seed is for when somebody reports what they saw.
    /// </param>
    internal static IReadOnlyList<Item> Choose(int randomCount = DefaultRandomCount, int? seed = null)
    {
        int wanted = Math.Clamp(randomCount, 0, Pool.Count);

        Random random = seed is null ? Random.Shared : new Random(seed.Value);

        // Shuffled by index rather than by reordering the pool, so the pool itself stays
        // in the order the file lists it and each entry keeps the position its identifiers
        // are derived from.
        int[] indices = Enumerable.Range(0, Pool.Count).ToArray();
        random.Shuffle(indices);

        _items = Awkward
            .Concat(indices.Take(wanted).Select(index => Pool[index]))
            .ToList();

        return _items;
    }

    /// <summary>
    /// The entries that are always served, because each one breaks something.
    /// </summary>
    internal static IReadOnlyList<Item> Awkward { get; } = new[]
    {
        // Ordinary, comfortably within the limit.
        new Item("TESTE0000001", "SILVA", "ANA", "TESTEACC000001", "19850214", "F",
                 "OT", "Relatório de Consulta", 9 * 60),

        // Accented throughout, still within the limit.
        new Item("TESTE0000002", "GONÇALVES", "JOÃO MIGUEL", "TESTEACC000002", "19700903", "M",
                 "OT", "Relatório de Radiologia", 9 * 60 + 30),

        // 59 characters once joined, so it fits with little to spare. Included because a
        // name this long passing through untouched is as worth proving as one that cannot.
        new Item("TESTE0000003", "APARÍCIO DA SILVA PEREIRA", "MARIA DO CARMO FERNANDA CONCEIÇÃO",
                 "TESTEACC000003", "19621130", "F", "OT", "Relatório de Ecografia", 10 * 60),

        // 73 characters once joined, so over the limit. The surname is 43, which leaves
        // twenty for the given name: enough for "JOSÉ ANTÓNIO" and not for what follows.
        new Item("TESTE0000004", "ALBUQUERQUE MONTENEGRO DOS SANTOS RODRIGUES", "JOSÉ ANTÓNIO FRANCISCO XAVIER",
                 "TESTEACC000004", "19551007", "M", "OT", "Relatório de Tomografia Computorizada", 10 * 60 + 45),

        // No given name at all, which a worklist is entitled to send.
        new Item("TESTE0000005", "FERREIRA", "", "TESTEACC000005", "19900621", "O",
                 "OT", "Relatório de Análises", 11 * 60),

        // A different modality, so filtering by modality can be seen to work.
        new Item("TESTE0000006", "MARTINS", "BEATRIZ", "TESTEACC000006", "20010418", "F",
                 "CR", "Radiografia de Tórax", 11 * 60 + 30),

        // A surname of 69 characters on its own: the pathological case, where there is no
        // room for a given name at all and the surname itself has to be cut.
        new Item("TESTE0000007", "VASCONCELOS CAVALCANTI DE ALMEIDA BARBOSA MAGALHÃES NASCIMENTO JUNIOR",
                 "PEDRO", "TESTEACC000007", "19781225", "M", "OT", "Relatório de Consulta Externa", 12 * 60)
    };

    /// <summary>
    /// Five hundred ordinary Portuguese names, read from the embedded name list.
    /// </summary>
    /// <remarks>
    /// The file holds each name already split as <c>Family^Given|Sex</c>, so the decision
    /// about where a surname ends is reviewable data rather than a heuristic applied at run
    /// time. Portuguese names do not split reliably by rule: the last token is the family
    /// name often enough to look right, and no rule gets "Maria do Carmo Farinha" and
    /// "Abel Cordeiro Brandão" both right without being told.
    /// </remarks>
    /// <remarks>
    /// Built on first use rather than in a field initialiser. Static initialisers run in
    /// declaration order, and building the pool reads <see cref="PoolProcedures"/>, which
    /// is declared below it; as a field initialiser this threw a NullReferenceException
    /// from inside a type initialiser, which is a poor way to learn about declaration
    /// order. Deferring removes the dependence rather than relying on it.
    /// </remarks>
    internal static IReadOnlyList<Item> Pool => _pool ??= LoadPool();

    private static IReadOnlyList<Item>? _pool;

    private static IReadOnlyList<Item> LoadPool()
    {
        const string resource = "PdfToDicom.TestWorklistServer.names-pt-PT.txt";

        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);

        if (stream is null)
        {
            // A missing resource is a packaging fault, not a condition to work around. The
            // server would still start and serve the awkward entries, which is exactly the
            // silent degradation worth refusing.
            string[] found = Assembly.GetExecutingAssembly().GetManifestResourceNames();

            throw new InvalidOperationException(
                $"The embedded name list '{resource}' is missing from this build. " +
                $"This assembly carries {found.Length} resource(s): {string.Join(", ", found)}. " +
                "A resource file whose name contains a dotted culture, such as 'names.pt-PT.txt', " +
                "is compiled into a satellite assembly for that culture instead, which is why the " +
                "file is named with a hyphen and its LogicalName is pinned in the project file.");
        }

        using var reader = new StreamReader(stream);

        var items = new List<Item>();
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            string trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            string[] halves = trimmed.Split('|');
            string[] components = halves[0].Split('^');

            if (halves.Length != 2 || components.Length != 2)
            {
                throw new InvalidOperationException(
                    $"'{trimmed}' is not a valid name list entry. Each line is Family^Given|Sex.");
            }

            items.Add(PoolItem(items.Count, components[0], components[1], halves[1]));
        }

        return items;
    }

    /// <summary>
    /// Turns one line of the name list into a scheduled procedure step.
    /// </summary>
    /// <remarks>
    /// Every field is derived from the position, so the entry is the same on every run and
    /// only its presence in the draw varies. The identifiers start at a million so they
    /// cannot be confused with, or collide with, the awkward entries numbered from one.
    /// Both carry <see cref="TestMarker"/>, so a study generated from either is
    /// identifiable at a glance.
    /// </remarks>
    private static Item PoolItem(int index, string familyName, string givenName, string sex)
    {
        string number = (1_000_000 + index).ToString(CultureInfo.InvariantCulture);

        // Spread across a working day in twelve minute steps, wrapping after fifty slots,
        // so a draw of ten lands at scattered times rather than in a block. The client
        // sorts by scheduled time, which this gives it something to do.
        int minutes = (8 * 60) + ((index % 50) * 12);

        // Between 1940 and 2004, on a day that always exists in any month.
        int year = 1940 + (index % 65);
        int month = 1 + (index % 12);
        int day = 1 + (index % 28);

        return new Item(
            PatientId: $"{TestMarker}{number}",
            FamilyName: familyName.ToUpperInvariant(),
            GivenName: givenName.ToUpperInvariant(),
            AccessionNumber: $"{TestMarker}ACC{number}",
            BirthDate: $"{year:D4}{month:D2}{day:D2}",
            Sex: sex,
            Modality: "OT",
            ProcedureDescription: PoolProcedures[index % PoolProcedures.Length],
            MinutesFromStartOfDay: minutes);
    }

    /// <summary>
    /// Procedure descriptions for the pool, accents included, so the character set is
    /// exercised by the procedure text as well as by the names.
    /// </summary>
    private static readonly string[] PoolProcedures =
    {
        "Relatório de Consulta",
        "Relatório de Radiologia",
        "Relatório de Ecografia",
        "Relatório de Análises Clínicas",
        "Relatório de Consulta Externa",
        "Relatório de Tomografia Computorizada",
        "Relatório de Ressonância Magnética",
        "Relatório de Endoscopia",
        "Relatório de Anatomia Patológica",
        "Relatório de Cardiologia",
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
        step.AddOrUpdate(DicomTag.ScheduledProcedureStepDescription, Described(item));
        step.AddOrUpdate(DicomTag.ScheduledProcedureStepID, item.AccessionNumber);
        step.AddOrUpdate(DicomTag.ScheduledPerformingPhysicianName, $"{TestMarker}^FICTÍCIO");

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
        dataset.AddOrUpdate(DicomTag.ReferringPhysicianName, $"{TestMarker}^FICTÍCIO");
        dataset.AddOrUpdate(DicomTag.RequestedProcedureDescription, Described(item));
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

    /// <summary>
    /// The procedure description, marked so it is obvious in a study list.
    /// </summary>
    /// <remarks>
    /// The mark leads rather than trails. A study description is routinely shown truncated,
    /// and a marker at the end is the part that gets cut off.
    /// </remarks>
    private static string Described(Item item) => $"[{TestMarker}] {item.ProcedureDescription}";

    /// <summary>A stable, obviously invented Study Instance UID for each item.</summary>
    /// <remarks>
    /// Built from the digits of the Patient ID rather than by trimming a known prefix,
    /// because the identifiers now carry a textual marker and a UID component may hold
    /// nothing but digits. Getting this wrong produces a malformed UID rather than an
    /// error, which a receiving archive would be entitled to reject in its own way.
    /// </remarks>
    private static string StudyUidFor(Item item)
    {
        string digits = new(item.PatientId.Where(char.IsDigit).ToArray());
        string suffix = digits.TrimStart('0');

        return $"1.2.826.0.1.3680043.9.6008.99.{(suffix.Length == 0 ? "0" : suffix)}";
    }
}
