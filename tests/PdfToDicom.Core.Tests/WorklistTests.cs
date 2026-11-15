using PdfToDicom.Core.Configuration;
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
}
