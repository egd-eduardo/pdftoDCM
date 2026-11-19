using System.Globalization;
using FellowOakDicom;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Networking;
using PdfToDicom.Core.Dicom;
using PdfToDicom.Core.Security;
using PdfToDicom.Core.Validation;
using Xunit;

namespace PdfToDicom.Core.Tests;

/// <summary>
/// Covers fitting a worklist name into what DICOM allows.
/// </summary>
/// <remarks>
/// The rule the department chose: keep the surname whole and shorten the given name. These
/// tests exist because the rule decides what name a patient's document carries, and a
/// mistake in it is the kind that is only noticed once a record is wrong.
/// </remarks>
public sealed class PersonNameShortenerTests
{
    private const int Limit = DicomValueRules.MaxPersonNameGroupLength;

    [Fact]
    public void ANameThatFitsIsLeftExactlyAsItArrived()
    {
        ShortenedPersonName fitted = PersonNameShortener.Fit("GONÇALVES", "JOÃO MIGUEL");

        Assert.Equal("GONÇALVES", fitted.FamilyName);
        Assert.Equal("JOÃO MIGUEL", fitted.GivenName);
        Assert.False(fitted.WasShortened);
    }

    [Fact]
    public void TheSurnameSurvivesWholeAndTheGivenNameIsShortened()
    {
        // 73 characters. The surname is what an archive indexes and what a clinician
        // searches by, so it is the half that must not be touched.
        const string family = "ALBUQUERQUE MONTENEGRO DOS SANTOS RODRIGUES";
        const string given = "JOSÉ ANTÓNIO FRANCISCO XAVIER";

        ShortenedPersonName fitted = PersonNameShortener.Fit(family, given);

        Assert.Equal(family, fitted.FamilyName);
        Assert.True(fitted.WasShortened);
        Assert.StartsWith(fitted.GivenName, given, StringComparison.Ordinal);
        Assert.InRange(Composed(fitted).Length, 1, Limit);
    }

    [Fact]
    public void ShorteningStopsAtAWordBoundary()
    {
        ShortenedPersonName fitted = PersonNameShortener.Fit(
            "ALBUQUERQUE MONTENEGRO DOS SANTOS RODRIGUES",
            "JOSÉ ANTÓNIO FRANCISCO XAVIER");

        // "JOSÉ ANTÓNIO" reads as a name. "JOSÉ ANTÓNIO FRAN" reads as a defect, and a
        // clinician seeing it would reasonably doubt the rest of the record.
        Assert.Equal("JOSÉ ANTÓNIO", fitted.GivenName);
        Assert.DoesNotContain("  ", fitted.GivenName, StringComparison.Ordinal);
        Assert.Equal(fitted.GivenName.TrimEnd(), fitted.GivenName);
    }

    [Fact]
    public void ASurnameLongerThanTheWholeLimitIsCutAndTheGivenNameIsDropped()
    {
        // The pathological case. There is no room for a given name at all, and the surname
        // itself cannot be encoded as it stands.
        const string family = "VASCONCELOS CAVALCANTI DE ALMEIDA BARBOSA MAGALHÃES NASCIMENTO JUNIOR";

        ShortenedPersonName fitted = PersonNameShortener.Fit(family, "PEDRO");

        Assert.True(family.Length > Limit, "the fixture must exceed the limit for this test to mean anything");
        Assert.Empty(fitted.GivenName);
        Assert.True(fitted.WasShortened);
        Assert.InRange(fitted.FamilyName.Length, 1, Limit);
        Assert.StartsWith(fitted.FamilyName, family, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingGivenNameProducesNoTrailingSeparator()
    {
        ShortenedPersonName fitted = PersonNameShortener.Fit("FERREIRA", "");

        Assert.Equal("FERREIRA", fitted.FamilyName);
        Assert.Empty(fitted.GivenName);
        Assert.False(fitted.WasShortened);
        Assert.Equal(8, fitted.OriginalLength);
    }

    [Fact]
    public void ASingleWordTooLongToBreakIsCutRatherThanDiscarded()
    {
        // No space to break at. Returning nothing would lose the name entirely, which is
        // worse than returning its beginning.
        string family = new('A', 20);
        string given = new('B', 60);

        ShortenedPersonName fitted = PersonNameShortener.Fit(family, given);

        Assert.Equal(family, fitted.FamilyName);
        Assert.NotEmpty(fitted.GivenName);
        Assert.Equal(Limit, Composed(fitted).Length);
    }

    [Theory]
    [InlineData("SILVA", "ANA")]
    [InlineData("ALBUQUERQUE MONTENEGRO DOS SANTOS RODRIGUES", "JOSÉ ANTÓNIO FRANCISCO XAVIER")]
    [InlineData("VASCONCELOS CAVALCANTI DE ALMEIDA BARBOSA MAGALHÃES NASCIMENTO JUNIOR", "PEDRO")]
    [InlineData("", "")]
    public void WhateverArrivesTheResultCanAlwaysBeEncoded(string family, string given)
    {
        // The invariant that matters: no worklist entry, however long, may produce a name
        // the toolkit will refuse to build an object from.
        ShortenedPersonName fitted = PersonNameShortener.Fit(family, given);

        Assert.True(
            Composed(fitted).Length <= Limit,
            $"'{Composed(fitted)}' is {Composed(fitted).Length} characters, over the {Limit} limit.");
    }

    private static string Composed(ShortenedPersonName name) =>
        string.IsNullOrEmpty(name.GivenName) ? name.FamilyName : $"{name.FamilyName}^{name.GivenName}";
}

/// <summary>
/// Covers the round trip a referring physician's name makes from the worklist into the
/// generated object.
/// </summary>
/// <remarks>
/// This exists because of a defect that produced a plausible wrong answer rather than an
/// error. The worklist sends a Person Name as Family^Given; flattening it to
/// "COSTA MANUEL" reads correctly and then goes back through
/// <see cref="PersonNameFormatter.FromFreeText"/>, which takes the LAST word as the family
/// name, so the physician's surname and given name were swapped on the way into the
/// object. Nothing failed, nothing was logged, and the name was simply wrong.
///
/// The tests therefore assert the whole round trip rather than either half of it.
///
/// They also pin what the round trip CANNOT preserve. Folding a structured Person Name
/// into one free text field loses the boundary between the given and middle components,
/// so a given name of several words comes back redistributed. The family name, which is
/// what an archive indexes and what was being corrupted, survives exactly.
/// </remarks>
public sealed class ReferringPhysicianRoundTripTests
{
    [Theory]
    [InlineData("COSTA^MANUEL", "MANUEL COSTA", "COSTA^MANUEL")]
    [InlineData("GONÇALVES^MARIA^JOÃO", "MARIA JOÃO GONÇALVES", "GONÇALVES^MARIA^JOÃO")]
    [InlineData("TESTE^FICTÍCIO", "FICTÍCIO TESTE", "TESTE^FICTÍCIO")]
    [InlineData("SILVA", "SILVA", "SILVA")]
    public void AWorklistNameSurvivesTheTripThroughTheForm(
        string fromWorklist, string shownToTheOperator, string writtenToTheObject)
    {
        // What the worklist client turns the Person Name into for the form.
        string display = WorklistPersonNameForDisplay(fromWorklist);

        Assert.Equal(shownToTheOperator, display);

        // And what the builder makes of that text when the form is submitted unchanged.
        Assert.Equal(writtenToTheObject, PersonNameFormatter.FromFreeText(display));
    }

    [Fact]
    public void AMultiWordGivenNameComesBackRedistributedButNeverSwapped()
    {
        // The limit of what a single text field can carry, asserted rather than left to be
        // discovered. "LISTA DE TRABALHO" was one given component; it returns split across
        // the given and middle ones, because the field holds no record of where the given
        // name ended.
        const string fromWorklist = "TESTE^LISTA DE TRABALHO";

        string display = WorklistPersonNameForDisplay(fromWorklist);
        string written = PersonNameFormatter.FromFreeText(display);

        Assert.Equal("LISTA DE TRABALHO TESTE", display);
        Assert.Equal("TESTE^LISTA^DE TRABALHO", written);

        // The part that matters is intact: the family name is still the family name, which
        // is the whole point of the fix this test guards.
        Assert.Equal("TESTE", written.Split('^')[0]);
    }

    [Fact]
    public void FlatteningTheCaretsWouldHaveSwappedTheComponents()
    {
        // The defect, pinned so it cannot come back by someone "simplifying" the display
        // conversion into a Replace.
        const string fromWorklist = "COSTA^MANUEL";

        string naive = fromWorklist.Replace('^', ' ');

        Assert.Equal("MANUEL^COSTA", PersonNameFormatter.FromFreeText(naive));
        Assert.NotEqual(fromWorklist, PersonNameFormatter.FromFreeText(naive));
    }

    [Fact]
    public void AnEmptyNameStaysEmpty()
    {
        Assert.Equal(string.Empty, WorklistPersonNameForDisplay(string.Empty));
        Assert.Equal(string.Empty, PersonNameFormatter.FromFreeText(string.Empty));
    }

    /// <summary>
    /// Mirrors WorklistService.ReadPhysicianName, which is private because nothing outside
    /// the service has any business calling it.
    /// </summary>
    private static string WorklistPersonNameForDisplay(string raw)
    {
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        string[] components = raw.Split('=')[0].Split('^');

        string family = components.Length > 0 ? components[0].Trim() : string.Empty;
        string given = components.Length > 1 ? components[1].Trim() : string.Empty;
        string middle = components.Length > 2 ? components[2].Trim() : string.Empty;

        return string.Join(' ', new[] { given, middle, family }.Where(part => part.Length > 0));
    }
}

/// <summary>
/// Covers the password that guards the worklist settings.
/// </summary>
public sealed class ProtectedSettingsLockTests
{
    /// <summary>
    /// The password actually compiled into this build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written down in two places, the comment on the verifier constant and here, and they
    /// have to agree. A verifier that no longer matches the password everyone believes is
    /// set locks the copyright holder out of the worklist and destination settings in their
    /// own software, and the only symptom is an unlock that quietly refuses. There is
    /// nothing to debug from, because a verifier cannot be turned back into a password.
    /// </para>
    /// <para>
    /// So it is pinned. Changing the password means changing this line too, which is the
    /// point: the test fails until the two records of it agree.
    /// </para>
    /// <para>
    /// This does not weaken the lock any further than the comment on the constant already
    /// does. Both live in the source, the source is not part of what a site receives, and
    /// anybody holding the source can delete the check outright.
    /// </para>
    /// </remarks>
    [Fact]
    public void APasswordIsSetAtAll()
    {
        // All this can honestly assert now. The test used to name the shipped password so
        // that the comment recording it and the verifier could not drift apart; with the
        // comment gone, naming it here would simply move the plaintext from one file to
        // another, and this repository is public.
        Assert.True(ProtectedSettingsLock.IsProtected);
    }

    [Fact]
    public void TheShippedLockRefusesWhatIsObviouslyNotThePassword()
    {
        Assert.False(ProtectedSettingsLock.Verify("NeverWinterNights"));
        Assert.False(ProtectedSettingsLock.Verify(string.Empty));
        Assert.False(ProtectedSettingsLock.Verify(null));
    }

    [Fact]
    public void TheCorrectPasswordIsAccepted()
    {
        string verifier = ProtectedSettingsLock.CreateVerifier("a well chosen passphrase");

        Assert.True(ProtectedSettingsLock.VerifyAgainst("a well chosen passphrase", verifier));
    }

    [Theory]
    [InlineData("a well chosen passphras")]
    [InlineData("A well chosen passphrase")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsRejected(string? attempt)
    {
        string verifier = ProtectedSettingsLock.CreateVerifier("a well chosen passphrase");

        Assert.False(ProtectedSettingsLock.VerifyAgainst(attempt, verifier));
    }

    [Fact]
    public void TheVerifierDoesNotContainThePassword()
    {
        // The whole point of storing a hash rather than something encrypted: the stored
        // value cannot be turned back into the password.
        const string password = "Sardinha2026";
        string verifier = ProtectedSettingsLock.CreateVerifier(password);

        Assert.DoesNotContain(password, verifier, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(password)),
            verifier,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheSamePasswordProducesADifferentVerifierEachTime()
    {
        // A fresh salt every time, so two installations sharing a password do not share a
        // stored value, and nothing can be learned by comparing them.
        string first = ProtectedSettingsLock.CreateVerifier("Sardinha2026");
        string second = ProtectedSettingsLock.CreateVerifier("Sardinha2026");

        Assert.NotEqual(first, second);
        Assert.True(ProtectedSettingsLock.VerifyAgainst("Sardinha2026", first));
        Assert.True(ProtectedSettingsLock.VerifyAgainst("Sardinha2026", second));
    }

    [Fact]
    public void AnEmptyVerifierMeansNoPasswordHasBeenSet()
    {
        // Shipping with a placeholder nobody knows would lock the author out of their own
        // software. An unset lock is open, and the interface says so rather than implying
        // a protection that is not there.
        Assert.True(ProtectedSettingsLock.VerifyAgainst("anything at all", string.Empty));
    }

    [Fact]
    public void AMalformedVerifierIsTreatedAsABuildFaultNotAWrongPassword()
    {
        Assert.Throws<InvalidOperationException>(
            () => ProtectedSettingsLock.VerifyAgainst("anything", "not-a-valid-verifier"));
    }

    [Fact]
    public void TheVerifierRecordsItsOwnIterationCount()
    {
        // Verification uses the count stored in the verifier rather than today's constant,
        // so raising the constant later cannot invalidate a password already set.
        string verifier = ProtectedSettingsLock.CreateVerifier("Sardinha2026");
        string[] parts = verifier.Split('$');

        Assert.Equal(4, parts.Length);
        Assert.Equal("PBKDF2-SHA256", parts[0]);
        Assert.True(int.Parse(parts[1]) >= 210_000);
    }
}

/// <summary>
/// Covers how the configured date interval turns into a query.
/// </summary>
public sealed class WorklistDateRangeTests
{
    private static readonly DateTime Monday = new(2026, 10, 5);

    [Fact]
    public void TodayIsTheDefault()
    {
        var configuration = new WorklistConfiguration();

        Assert.Equal(WorklistDateRange.Today, configuration.DateRange);
        Assert.Equal((Monday, Monday), configuration.ResolveDates(Monday));
    }

    [Fact]
    public void YesterdayAndTodayCoversTwoDays()
    {
        var configuration = new WorklistConfiguration { DateRange = WorklistDateRange.YesterdayAndToday };

        Assert.Equal((Monday.AddDays(-1), Monday), configuration.ResolveDates(Monday));
    }

    [Fact]
    public void LastSevenDaysIncludesToday()
    {
        var configuration = new WorklistConfiguration { DateRange = WorklistDateRange.LastSevenDays };

        (DateTime From, DateTime To) range = configuration.ResolveDates(Monday)!.Value;

        Assert.Equal(Monday, range.To);
        Assert.Equal(7, (range.To - range.From).Days + 1);
    }

    [Fact]
    public void AnyDateSendsNoDateKeyAtAll()
    {
        var configuration = new WorklistConfiguration { DateRange = WorklistDateRange.AnyDate };

        Assert.Null(configuration.ResolveDates(Monday));
    }

    [Fact]
    public void AnExplicitRangeEnteredBackwardsIsPutTheRightWayRound()
    {
        // Easily done, and refusing to search would be a poor response to an obvious slip.
        var configuration = new WorklistConfiguration
        {
            DateRange = WorklistDateRange.Explicit,
            ExplicitFrom = Monday,
            ExplicitTo = Monday.AddDays(-3)
        };

        Assert.Equal((Monday.AddDays(-3), Monday), configuration.ResolveDates(Monday));
    }

    [Fact]
    public void AnExplicitRangeLeftBlankFallsBackToToday()
    {
        var configuration = new WorklistConfiguration { DateRange = WorklistDateRange.Explicit };

        Assert.Equal((Monday, Monday), configuration.ResolveDates(Monday));
    }

    // ------------------------------------------------- Finding an older examination

    [Fact]
    public void TypingASurnameMatchesAPatientWhoAlsoHasAGivenName()
    {
        // The defect this covers, reported from a site. The box is labelled "Surname
        // contains" and the typed text was sent as an exact key, so "Silva" was asked to
        // match the whole of Patient's Name. A record of "Silva^Maria" does not match that,
        // which is every record with a given name in it, so the box found nothing and the
        // worklist looked broken rather than strict.
        Assert.Equal("*Silva*", WorklistMatch.Contains("Silva"));
        Assert.Equal("*12345*", WorklistMatch.Contains(" 12345 "));
    }

    [Fact]
    public void AWildcardSomebodyTypedIsLeftExactlyAsTyped()
    {
        // The command line documents wildcards and people use them. Wrapping "Silva*" into
        // "*Silva**" would be rude, and on some servers slower.
        Assert.Equal("Silva*", WorklistMatch.Contains("Silva*"));
        Assert.Equal("1?3", WorklistMatch.Contains("1?3"));
    }

    [Fact]
    public void AnEmptyBoxStaysEmptyAndMatchesEverything()
    {
        // Empty is universal matching, which is "every value". Turning it into "**" would
        // ask the server to match the values that exist, excluding the ones that are absent.
        Assert.Equal(string.Empty, WorklistMatch.Contains(null));
        Assert.Equal(string.Empty, WorklistMatch.Contains("   "));
        Assert.False(WorklistMatch.IsPresent("  "));
        Assert.True(WorklistMatch.IsPresent("12345"));
    }

    [Fact]
    public void AQueryForOneDayCanBeWidenedWithoutChangingTheSavedSetting()
    {
        // Looking up one old examination is not a decision about how this workstation should
        // behave tomorrow, and the saved setting is behind the settings password.
        var configuration = new WorklistConfiguration { DateRange = WorklistDateRange.Today };

        Assert.Equal((Monday, Monday), configuration.ResolveDates(Monday));

        Assert.Null(configuration.ResolveDates(
            Monday, new WorklistDateWindow(WorklistDateRange.AnyDate)));

        Assert.Equal(
            (Monday.AddDays(-30), Monday),
            configuration.ResolveDates(
                Monday,
                new WorklistDateWindow(
                    WorklistDateRange.Explicit, Monday.AddDays(-30), Monday)));

        // And the saved setting is untouched by any of it.
        Assert.Equal(WorklistDateRange.Today, configuration.DateRange);
        Assert.Equal((Monday, Monday), configuration.ResolveDates(Monday));
    }

    [Fact]
    public void AnOverriddenExplicitRangeEnteredBackwardsIsStillAcceptedTheRightWayRound()
    {
        var configuration = new WorklistConfiguration { DateRange = WorklistDateRange.Today };

        Assert.Equal(
            (Monday.AddDays(-7), Monday),
            configuration.ResolveDates(
                Monday,
                new WorklistDateWindow(
                    WorklistDateRange.Explicit, Monday, Monday.AddDays(-7))));
    }

    // ------------------------------------------------- The phrases the script greps for

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void TheFoundMessageKeepsTheExactPhraseTheVerificationScriptLooksFor(int count)
    {
        // build/verify-against-dcmtk.sh decides whether three of its checks passed by
        // looking for this substring in the command line output. Rewording it once turned
        // five passing checks red while the behaviour underneath was never at fault, so the
        // phrase is pinned here and a rewording fails in a second rather than in four
        // minutes of CI.
        string message = WorklistMessages.Found(count, "dates 20261119");

        Assert.Contains($"{count} scheduled procedure(s) found.", message, StringComparison.Ordinal);

        // And the criteria still arrive, as a second sentence.
        Assert.Contains("Asked for dates 20261119", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmptyMessageKeepsTheExactPhraseTheVerificationScriptLooksFor()
    {
        string message = WorklistMessages.Nothing("dates 20200101-20200131");

        Assert.Contains("no scheduled procedures", message, StringComparison.Ordinal);
        Assert.Contains("Asked for dates 20200101-20200131", message, StringComparison.Ordinal);
    }

    // ------------------------------------------- Finding a name whatever its accents

    [Theory]
    [InlineData("Goncalves", "Gonçalves")]
    [InlineData("goncalves", "GONÇALVES")]
    [InlineData("GONCALVES", "Gonçalves")]
    [InlineData("Sofia", "Sofía")]
    [InlineData("Joao", "João")]
    [InlineData("Mendonca", "Mendonça")]
    [InlineData("Simoes", "Simões")]
    [InlineData("Agueda", "Águeda")]
    public void APortugueseNameIsFoundWhateverTheAccentsAndTheCase(string typed, string onTheWorklist)
    {
        // The requirement, in the author's words: looking up has to find any entry, so a
        // desk that cannot type ç finds the patient anyway. DICOM cannot express this at
        // all, which is why it is decided on the answers rather than in the query.
        Assert.True(WorklistFolding.Contains(onTheWorklist, typed));
    }

    [Theory]
    [InlineData("Lukasiewicz", "Łukasiewicz")]
    [InlineData("Osterg", "Østergaard")]
    [InlineData("Strauss", "Strauß")]
    public void ALetterThatCarriesItsMarkInsideItselfIsFoldedToo(string typed, string onTheWorklist)
    {
        // Decomposition turns é into e and an accent, and does nothing for these: the
        // stroke in Ł and the slash in Ø are part of the letter.
        Assert.True(WorklistFolding.Contains(onTheWorklist, typed));
    }

    [Fact]
    public void FoldingFindsPartOfANameRatherThanTheWholeOfIt()
    {
        // The boxes say "contains", and a surname is one component of Patient's Name, so an
        // operator typing a family name is looking inside a value that holds a given name
        // as well.
        Assert.True(WorklistFolding.Contains("Gonçalves Maria", "alves"));
        Assert.True(WorklistFolding.Contains("Gonçalves Maria", "MARIA"));
        Assert.False(WorklistFolding.Contains("Gonçalves Maria", "Silva"));
    }

    [Fact]
    public void AnEmptyFilterMatchesEverythingRatherThanNothing()
    {
        Assert.True(WorklistFolding.Contains("Gonçalves", null));
        Assert.True(WorklistFolding.Contains("Gonçalves", "   "));
        Assert.True(WorklistFolding.Contains(null, null));
        Assert.False(WorklistFolding.Contains(null, "Silva"));
    }

    [Fact]
    public void FoldingIsNotAffectedByTheMachinesOwnLanguage()
    {
        // Invariant upper casing, deliberately. A Turkish workstation upper cases a dotless
        // i differently, and finding a patient should not depend on where the machine is.
        Assert.Equal("ISIK", WorklistFolding.Fold("ışık"));
        Assert.Equal("ISIK", WorklistFolding.Fold("IŞIK"));
    }

    [Fact]
    public void FoldingIsForComparingAndTheWorklistsOwnSpellingIsWhatIsKept()
    {
        // The other half of the requirement, and the half that matters clinically: the
        // worklist is the authority on how a patient's name is written, so what reaches the
        // DICOM object is its spelling and not the folded form used to find it.
        var entry = new WorklistEntry
        {
            PatientId = "P0000042",
            FamilyName = "Gonçalves",
            GivenName = "Sofía"
        };

        Assert.True(WorklistFolding.Contains($"{entry.FamilyName} {entry.GivenName}", "goncalves sofia"));

        // Nothing in the folding reaches back into the entry.
        Assert.Equal("Gonçalves", entry.FamilyName);
        Assert.Equal("Sofía", entry.GivenName);
        Assert.Equal("GONCALVES", WorklistFolding.Fold(entry.FamilyName));
    }

    // ------------------------------------------------------------------ Paging

    [Fact]
    public void AnEmptyResultIsOnePageRatherThanNone()
    {
        // "Page 1 of 0" is not readable, and a pager that reports zero pages invites a
        // caller to clamp an index into a page that does not exist.
        Assert.Equal(1, WorklistPaging.PageCount(0, 500));
        Assert.Equal((0, 0), WorklistPaging.PageWindow(0, 0, 500));
        Assert.Equal(0, WorklistPaging.ClampPageIndex(3, 0, 500));
    }

    [Theory]
    [InlineData(1, 500, 1)]
    [InlineData(499, 500, 1)]
    [InlineData(500, 500, 1)]
    [InlineData(501, 500, 2)]
    [InlineData(1000, 500, 2)]
    [InlineData(1001, 500, 3)]
    [InlineData(3214, 500, 7)]
    [InlineData(12000, 200, 60)]
    public void ThePageCountCoversEveryEntry(int total, int pageSize, int expected)
    {
        Assert.Equal(expected, WorklistPaging.PageCount(total, pageSize));

        // The point of the count: the last page reaches the last entry. An off by one here
        // is a patient nobody can scroll to.
        (int _, int last) = WorklistPaging.PageWindow(expected - 1, total, pageSize);
        Assert.Equal(total, last);
    }

    [Fact]
    public void ThePageWindowIsNumberedFromOneAndStopsAtTheLastEntry()
    {
        Assert.Equal((1, 500), WorklistPaging.PageWindow(0, 3214, 500));
        Assert.Equal((501, 1000), WorklistPaging.PageWindow(1, 3214, 500));
        Assert.Equal((3001, 3214), WorklistPaging.PageWindow(6, 3214, 500));
    }

    [Fact]
    public void APageIndexFromAnywhereLandsOnAPageThatExists()
    {
        // Reachable without anybody intending it: a button left enabled for a moment, or a
        // second search that found fewer entries than the first.
        Assert.Equal(0, WorklistPaging.ClampPageIndex(-4, 3214, 500));
        Assert.Equal(6, WorklistPaging.ClampPageIndex(99, 3214, 500));
        Assert.Equal(0, WorklistPaging.ClampPageIndex(5, 10, 500));
    }

    [Fact]
    public void AUselessPageSizeFallsBackToTheDefaultInsteadOfShowingNothing()
    {
        // A configuration file is editable by hand, and a page size of zero would divide a
        // result into pages holding nothing at all.
        Assert.Equal(WorklistPaging.DefaultPageSize, WorklistPaging.SanePageSize(0));
        Assert.Equal(WorklistPaging.DefaultPageSize, WorklistPaging.SanePageSize(-20));
        Assert.Equal(25, WorklistPaging.SanePageSize(25));
        Assert.Equal(1, WorklistPaging.PageCount(400, 0));
        Assert.Equal((1, 500), WorklistPaging.PageWindow(0, 3214, 0));
    }

    [Fact]
    public void SkippingReachesTheFirstEntryOfThePage()
    {
        Assert.Equal(0, WorklistPaging.Skipped(0, 3214, 500));
        Assert.Equal(500, WorklistPaging.Skipped(1, 3214, 500));
        Assert.Equal(3000, WorklistPaging.Skipped(6, 3214, 500));

        // Every entry is reachable by walking the pages, which is the whole promise.
        var seen = new HashSet<int>();

        for (int page = 0; page < WorklistPaging.PageCount(3214, 500); page++)
        {
            int skipped = WorklistPaging.Skipped(page, 3214, 500);

            for (int i = skipped; i < Math.Min(skipped + 500, 3214); i++)
            {
                Assert.True(seen.Add(i), $"entry {i} appeared on two pages");
            }
        }

        Assert.Equal(3214, seen.Count);
    }

    [Fact]
    public void TheFetchCeilingIsFarAboveAPageAndIsNotDerivedFromOne()
    {
        // The defect this replaces: the fetch ceiling was ten times the page size, which is
        // five thousand by default, and entries past it were dropped as they arrived in the
        // server's own order, before anything was sorted. An examination scheduled a year
        // ago could be fetched, discarded and never counted while the list looked complete.
        Assert.True(
            WorklistService.FetchCeiling >= 50_000,
            "the ceiling exists to stop a runaway schedule, not to shorten a list");

        Assert.True(
            WorklistService.FetchCeiling > WorklistPaging.DefaultPageSize * 10,
            "how much a window shows at once must not decide how much a query may hold");
    }

    // ------------------------------------------------------------------ The keys sent

    /// <summary>
    /// The keys a worklist query sends, which nothing asserted until the worklist explorer
    /// in tools/ was compared against them.
    /// </summary>
    /// <remarks>
    /// Written as a list rather than a count. A key deleted by accident is not a crash and
    /// not a wrong answer: it is a column that silently comes back empty, and the one that
    /// matters most, the Study Instance UID, is the one whose absence files a report into a
    /// study of its own instead of beside the images.
    /// </remarks>
    private static readonly DicomTag[] ExpectedTopLevelKeys =
    [
        DicomTag.SpecificCharacterSet,
        DicomTag.PatientName,
        DicomTag.PatientID,
        DicomTag.PatientBirthDate,
        DicomTag.PatientSex,
        DicomTag.AccessionNumber,
        DicomTag.ReferringPhysicianName,
        DicomTag.StudyInstanceUID,
        DicomTag.RequestedProcedureDescription,
        DicomTag.RequestedProcedureID,
        DicomTag.ScheduledProcedureStepSequence
    ];

    private static readonly DicomTag[] ExpectedStepKeys =
    [
        DicomTag.Modality,
        DicomTag.ScheduledStationAETitle,
        DicomTag.ScheduledProcedureStepStartDate,
        DicomTag.ScheduledProcedureStepStartTime,
        DicomTag.ScheduledProcedureStepDescription,
        DicomTag.ScheduledProcedureStepID,
        DicomTag.ScheduledPerformingPhysicianName
    ];

    [Fact]
    public void TheQuerySendsExactlyTheKeysItMeansTo()
    {
        DicomDataset query = WorklistService.BuildQueryIdentifier(new WorklistConfiguration());

        Assert.Equal(
            ExpectedTopLevelKeys.Select(t => t.ToString()).OrderBy(s => s),
            query.Select(i => i.Tag.ToString()).OrderBy(s => s));

        DicomDataset step = query.GetSequence(DicomTag.ScheduledProcedureStepSequence).Items[0];

        Assert.Equal(
            ExpectedStepKeys.Select(t => t.ToString()).OrderBy(s => s),
            step.Select(i => i.Tag.ToString()).OrderBy(s => s));

        // The same eighteen the worklist explorer sends. If this test is changed, the
        // comparison table in tools/WorklistExplorer/README.md is out of date.
        Assert.Equal(18, ExpectedTopLevelKeys.Length + ExpectedStepKeys.Length);
    }

    /// <summary>
    /// A universal matching key: present in the identifier and holding no value at all.
    /// </summary>
    /// <remarks>
    /// Written as a helper because the obvious assertion is wrong in a way that passes.
    /// An element added with <c>string.Empty</c> holds zero values, so
    /// <c>GetSingleValueOrDefault(tag, string.Empty)</c> returns the default and reports
    /// success whether the key was sent or not, and <c>GetSingleValue</c> throws rather than
    /// returning an empty string. Both of those were written here first.
    /// </remarks>
    private static void AssertUniversalMatching(DicomDataset dataset, DicomTag tag)
    {
        Assert.True(dataset.Contains(tag), $"{tag} was not sent at all, so it will not be returned");
        Assert.Equal(0, dataset.GetValueCount(tag));
    }

    [Fact]
    public void EveryReturnKeyIsSentEmptySoTheServerReportsItsValue()
    {
        // Universal matching. A key sent with a value is a filter, and a filter nobody asked
        // for is a row excluded for a reason that appears nowhere on screen.
        DicomDataset query = WorklistService.BuildQueryIdentifier(new WorklistConfiguration());

        foreach (DicomTag tag in ExpectedTopLevelKeys.Where(t =>
                     t != DicomTag.SpecificCharacterSet && t != DicomTag.ScheduledProcedureStepSequence))
        {
            AssertUniversalMatching(query, tag);
        }

        DicomDataset step = query.GetSequence(DicomTag.ScheduledProcedureStepSequence).Items[0];

        // Every step key but the date. The date is the one deliberate filter in a default
        // configuration, which asks for today; the two tests below cover it and the modality
        // and station, which are filters when a site has set them.
        foreach (DicomTag tag in ExpectedStepKeys.Where(t => t != DicomTag.ScheduledProcedureStepStartDate))
        {
            AssertUniversalMatching(step, tag);
        }

        Assert.Equal(
            DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            step.GetSingleValue<string>(DicomTag.ScheduledProcedureStepStartDate));

        Assert.Equal(DicomConstants.Utf8CharacterSet, query.GetSingleValue<string>(DicomTag.SpecificCharacterSet));
    }

    [Fact]
    public void TheModalityAndStationFiltersAreTrimmedOnTheWayOut()
    {
        // The difference from the explorer, which sends them exactly as the file holds them.
        // A literally matching server does not match 'OT ', so a stray space in a
        // configuration file is the one case where the two tools disagree about a row.
        var configuration = new WorklistConfiguration
        {
            Modality = "  OT  ",
            ScheduledStationAeTitle = " CT1 "
        };

        DicomDataset step = WorklistService.BuildQueryIdentifier(configuration)
            .GetSequence(DicomTag.ScheduledProcedureStepSequence).Items[0];

        Assert.Equal("OT", step.GetSingleValue<string>(DicomTag.Modality));
        Assert.Equal("CT1", step.GetSingleValue<string>(DicomTag.ScheduledStationAETitle));
    }

    [Fact]
    public void NoTimeKeyIsEverSent()
    {
        // fo-dicom's own worklist helper sends 000000-000000 here, which asks for steps
        // scheduled at exactly midnight and returns nothing from a real department. This
        // test is why the query is assembled by hand.
        DicomDataset step = WorklistService.BuildQueryIdentifier(new WorklistConfiguration())
            .GetSequence(DicomTag.ScheduledProcedureStepSequence).Items[0];

        AssertUniversalMatching(step, DicomTag.ScheduledProcedureStepStartTime);
    }

    [Fact]
    public void APatientIdentifierSuppressesTheDateKeyEntirely()
    {
        var configuration = new WorklistConfiguration { DateRange = WorklistDateRange.Today };

        DicomDataset withId = WorklistService.BuildQueryIdentifier(configuration, patientIdFilter: "P0000042")
            .GetSequence(DicomTag.ScheduledProcedureStepSequence).Items[0];

        AssertUniversalMatching(withId, DicomTag.ScheduledProcedureStepStartDate);

        DicomDataset withoutId = WorklistService.BuildQueryIdentifier(configuration)
            .GetSequence(DicomTag.ScheduledProcedureStepSequence).Items[0];

        Assert.Equal(
            DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            withoutId.GetSingleValue<string>(DicomTag.ScheduledProcedureStepStartDate));
    }

    [Fact]
    public void ForgivingMatchingWrapsTheFiltersAndExactMatchingDoesNot()
    {
        DicomDataset forgiving = WorklistService.BuildQueryIdentifier(
            new WorklistConfiguration(), "123", "Silva", matching: WorklistMatching.Forgiving);

        Assert.Equal("*123*", forgiving.GetSingleValue<string>(DicomTag.PatientID));
        Assert.Equal("*Silva*", forgiving.GetSingleValue<string>(DicomTag.PatientName));

        DicomDataset exact = WorklistService.BuildQueryIdentifier(
            new WorklistConfiguration(), "123", "Silva");

        Assert.Equal("123", exact.GetSingleValue<string>(DicomTag.PatientID));
        Assert.Equal("Silva", exact.GetSingleValue<string>(DicomTag.PatientName));

        // The second, broad pass empties the name keys and matches here instead.
        DicomDataset broad = WorklistService.BuildQueryIdentifier(
            new WorklistConfiguration(), "123", "Silva",
            matching: WorklistMatching.Forgiving, askServerToFilter: false);

        AssertUniversalMatching(broad, DicomTag.PatientID);
        AssertUniversalMatching(broad, DicomTag.PatientName);
    }
}
