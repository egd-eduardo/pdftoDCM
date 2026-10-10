using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Importing;
using PdfToDicom.Core.Logging;
using PdfToDicom.Core.Models;
using Xunit;

namespace PdfToDicom.Core.Tests;

/// <summary>
/// Covers reading patient information out of a file name.
/// </summary>
/// <remarks>
/// These decide what patient a document is filed against, from a string anybody can
/// rename. That makes them the most safety relevant tests in the suite after the DICOM
/// construction itself, so the awkward cases are here deliberately rather than the tidy
/// ones.
/// </remarks>
public sealed class FilenamePatternTests
{
    [Fact]
    public void TheStatedFormatReadsAllFourFields()
    {
        var pattern = FilenamePattern.Compile("{PatientId}_{Name}_{AccessionNumber}_{DateOfBirth}");

        IReadOnlyDictionary<string, string>? fields = pattern.Match("012345_Ana Silva_ACC001_19700521.pdf");

        Assert.NotNull(fields);
        Assert.Equal("012345", fields[FilenameField.PatientId]);
        Assert.Equal("Ana Silva", fields[FilenameField.Name]);
        Assert.Equal("ACC001", fields[FilenameField.AccessionNumber]);
        Assert.Equal("19700521", fields[FilenameField.DateOfBirth]);
    }

    [Fact]
    public void AFieldMayContainTheSeparator()
    {
        // The reason a template is compiled to a regular expression rather than split on
        // the separator. "MARIA DO CARMO" holds two spaces and is one field.
        var pattern = FilenamePattern.Compile("{PatientId} {Name} {AccessionNumber}");

        IReadOnlyDictionary<string, string>? fields = pattern.Match("77 MARIA DO CARMO SILVA ACC9.pdf");

        Assert.NotNull(fields);
        Assert.Equal("77", fields[FilenameField.PatientId]);
        Assert.Equal("MARIA DO CARMO SILVA", fields[FilenameField.Name]);
        Assert.Equal("ACC9", fields[FilenameField.AccessionNumber]);
    }

    [Fact]
    public void TheDirectoryAndTheExtensionAreIgnored()
    {
        var pattern = FilenamePattern.Compile("{PatientId}_{Name}");

        Assert.NotNull(pattern.Match("/var/spool/reports/012345_Ana Silva.pdf"));
        Assert.NotNull(pattern.Match(@"C:\reports\012345_Ana Silva.PDF"));
    }

    [Fact]
    public void AnEmptyFieldDoesNotMatch()
    {
        // "_Ana Silva" has no patient id. Matching it with an empty one would file a
        // document against nobody.
        var pattern = FilenamePattern.Compile("{PatientId}_{Name}");

        Assert.Null(pattern.Match("_Ana Silva.pdf"));
    }

    [Fact]
    public void APatternMustBeAnchoredAtBothEnds()
    {
        // Trailing rubbish must not be absorbed silently; it means the name is not in the
        // expected format and somebody should look at it.
        var pattern = FilenamePattern.Compile("{PatientId}_{Name}");

        Assert.NotNull(pattern.Match("012345_Ana Silva.pdf"));
        Assert.NotNull(pattern.Match("012345_Ana Silva_extra.pdf"));
    }

    [Fact]
    public void ASkippedPartIsNotCaptured()
    {
        var pattern = FilenamePattern.Compile("{*}_{PatientId}_{Name}");

        IReadOnlyDictionary<string, string>? fields = pattern.Match("20260314_012345_Ana Silva.pdf");

        Assert.NotNull(fields);
        Assert.Equal("012345", fields[FilenameField.PatientId]);
        Assert.False(fields.ContainsKey("*"));
    }

    [Theory]
    [InlineData("PatientID")]
    [InlineData("patientid")]
    [InlineData("MRN")]
    [InlineData("Processo")]
    public void FieldNamesAreForgivingAboutSpelling(string alias)
    {
        var pattern = FilenamePattern.Compile($"{{{alias}}}_{{Name}}");

        IReadOnlyDictionary<string, string>? fields = pattern.Match("012345_Ana Silva.pdf");

        Assert.NotNull(fields);
        Assert.Equal("012345", fields[FilenameField.PatientId]);
    }

    [Fact]
    public void AcessionMissingItsSecondCIsAccepted()
    {
        // The spelling that turns up in practice. A pattern that silently failed to match
        // because of it would be a thoroughly unpleasant thing to diagnose.
        var pattern = FilenamePattern.Compile("{PatientId}_{Acession}");

        IReadOnlyDictionary<string, string>? fields = pattern.Match("1_ACC9.pdf");

        Assert.NotNull(fields);
        Assert.Equal("ACC9", fields[FilenameField.AccessionNumber]);
    }

    [Theory]
    [InlineData("{PatientId}_{Nonsense}", "not a field")]
    [InlineData("{PatientId_{Name}", "never closed")]
    [InlineData("{PatientId}_{Name", "never closed")]
    [InlineData("{}", "empty field")]
    [InlineData("nothing_here", "no fields")]
    [InlineData("{PatientId}_{PatientId}", "more than once")]
    public void AMalformedPatternSaysWhatIsWrongWithIt(string template, string expected)
    {
        FormatException thrown = Assert.Throws<FormatException>(() => FilenamePattern.Compile(template));

        Assert.Contains(expected, thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARegularExpressionIsAcceptedForALayoutATemplateCannotExpress()
    {
        var pattern = FilenamePattern.Compile(
            @"regex:^(?<PatientId>\d{6})(?<AccessionNumber>[A-Z]{3}\d+)$");

        IReadOnlyDictionary<string, string>? fields = pattern.Match("012345ACC99.pdf");

        Assert.NotNull(fields);
        Assert.Equal("012345", fields[FilenameField.PatientId]);
        Assert.Equal("ACC99", fields[FilenameField.AccessionNumber]);
    }

    [Fact]
    public void ARegularExpressionThatCapturesNothingIsRejected()
    {
        Assert.Throws<FormatException>(() => FilenamePattern.Compile(@"regex:^\d+$"));
    }
}

/// <summary>
/// Covers turning the captured fields into patient information.
/// </summary>
public sealed class FilenameMetadataReaderTests
{
    private static FilenameMetadataReader Reader(
        IEnumerable<string> patterns,
        bool splitRunTogether = false,
        string sex = "O",
        IEnumerable<string>? dateFormats = null) =>
        new(new FilenameImportConfiguration
        {
            IsEnabled = true,
            Patterns = patterns.ToList(),
            SplitRunTogetherNames = splitRunTogether,
            DefaultSex = sex,
            DateFormats = dateFormats?.ToList() ?? new List<string>()
        });

    [Fact]
    public void TheStatedFormatProducesSendableDemographics()
    {
        FilenameReadResult result = Reader(new[] { "{PatientId}_{Name}_{AccessionNumber}_{DateOfBirth}" })
            .Read("012345_Ana Silva_ACC001_19700521.pdf");

        Assert.True(result.IsMatch);
        PatientDemographics d = result.Demographics!;

        Assert.Equal("012345", d.PatientId);
        Assert.Equal("Silva", d.FamilyName);
        Assert.Equal("Ana", d.GivenName);
        Assert.Equal("ACC001", d.AccessionNumber);
        Assert.Equal(new DateTime(1970, 5, 21), d.DateOfBirth);
    }

    [Fact]
    public void SexIsNeverGuessedFromTheName()
    {
        // "Ana" is a female name in Portuguese and this must not notice. A file name does
        // not carry sex, and inventing it from a name is exactly the kind of plausible
        // fiction that makes a record untrustworthy.
        FilenameReadResult result = Reader(new[] { "{PatientId}_{Name}" }).Read("1_Ana Silva.pdf");

        Assert.Equal(PatientSex.Other, result.Demographics!.Sex);
    }

    [Fact]
    public void TheDefaultSexIsConfigurable()
    {
        FilenameReadResult result = Reader(new[] { "{PatientId}_{Name}" }, sex: string.Empty)
            .Read("1_Ana Silva.pdf");

        Assert.Equal(PatientSex.Unspecified, result.Demographics!.Sex);
        Assert.Equal(string.Empty, result.Demographics.Sex.ToDicomValue());
    }

    [Fact]
    public void TheFullestReadingWinsWhateverOrderThePatternsAreIn()
    {
        // The trap this rule exists for. The loose pattern matches the long name perfectly
        // well, by swallowing the accession number and the date of birth into the name, and
        // first-match-wins would file a patient called "Ana Silva_ACC001_19700521".
        var loose = new[] { "{PatientId}_{Name}", "{PatientId}_{Name}_{AccessionNumber}_{DateOfBirth}" };

        FilenameReadResult result = Reader(loose).Read("012345_Ana Silva_ACC001_19700521.pdf");

        Assert.True(result.IsMatch);
        Assert.Equal("Silva", result.Demographics!.FamilyName);
        Assert.Equal("Ana", result.Demographics.GivenName);
        Assert.Equal("ACC001", result.Demographics.AccessionNumber);
    }

    [Theory]
    [InlineData("1_Ana Silva.pdf", "Silva", "Ana")]
    [InlineData("1_Silva, Ana.pdf", "Silva", "Ana")]
    [InlineData("1_SILVA^ANA.pdf", "SILVA", "ANA")]
    [InlineData("1_Maria do Carmo Aparício.pdf", "Aparício", "Maria do Carmo")]
    [InlineData("1_Silva.pdf", "Silva", "")]
    public void AllThreeWaysOfWritingANameAreRead(string fileName, string family, string given)
    {
        FilenameReadResult result = Reader(new[] { "{PatientId}_{Name}" }).Read(fileName);

        Assert.Equal(family, result.Demographics!.FamilyName);
        Assert.Equal(given, result.Demographics.GivenName);
    }

    [Fact]
    public void SeparateComponentsAreUsedWhenThePatternCarriesThem()
    {
        FilenameReadResult result = Reader(new[] { "{PatientId}_{FamilyName}_{GivenName}" })
            .Read("1_SILVA_ANA.pdf");

        Assert.Equal("SILVA", result.Demographics!.FamilyName);
        Assert.Equal("ANA", result.Demographics.GivenName);
    }

    [Fact]
    public void ARunTogetherNameIsLeftWholeUnlessSplittingIsAskedFor()
    {
        FilenameReadResult result = Reader(new[] { "{PatientId}_{Name}_{AccessionNumber}" })
            .Read("9999999999_JohnDoe_TEST123451.pdf");

        Assert.Equal("JohnDoe", result.Demographics!.FamilyName);
        Assert.Equal(string.Empty, result.Demographics.GivenName);
    }

    [Fact]
    public void ARunTogetherNameIsSplitWhenItIs()
    {
        FilenameReadResult result =
            Reader(new[] { "{PatientId}_{Name}_{AccessionNumber}" }, splitRunTogether: true)
                .Read("9999999999_JohnDoe_TEST123451.pdf");

        Assert.Equal("Doe", result.Demographics!.FamilyName);
        Assert.Equal("John", result.Demographics.GivenName);

        // And it says that it did, because it is a guess.
        Assert.Contains(result.Notes, n => n.Contains("split at its capitals", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("JOHNDOE")]
    [InlineData("JDoe")]
    [InlineData("Silva")]
    public void TheSplitLeavesAloneWhatItCannotSplitSensibly(string name)
    {
        FilenameReadResult result =
            Reader(new[] { "{PatientId}_{Name}" }, splitRunTogether: true).Read($"1_{name}.pdf");

        Assert.Equal(name, result.Demographics!.FamilyName);
        Assert.Equal(string.Empty, result.Demographics.GivenName);
    }

    [Fact]
    public void AnAbsentDateOfBirthAndAccessionNumberAreSimplyAbsent()
    {
        FilenameReadResult result = Reader(new[] { "{PatientId}_{Name}" }).Read("012345_Ana Silva.pdf");

        Assert.True(result.IsMatch);
        Assert.Null(result.Demographics!.DateOfBirth);
        Assert.Equal(string.Empty, result.Demographics.AccessionNumber);
    }

    [Theory]
    [InlineData("19700521", 1970, 5, 21)]
    [InlineData("1970-05-21", 1970, 5, 21)]
    [InlineData("21051970", 1970, 5, 21)]
    [InlineData("21-05-1970", 1970, 5, 21)]
    [InlineData("21.05.1970", 1970, 5, 21)]
    public void TheDefaultDateFormatsCoverTheUsualSpellings(string text, int year, int month, int day)
    {
        FilenameReadResult result = Reader(new[] { "{PatientId}_{DateOfBirth}" }).Read($"1_{text}.pdf");

        Assert.Equal(new DateTime(year, month, day), result.Demographics!.DateOfBirth);
    }

    [Fact]
    public void OnlyTheDayFirstReadingOfAnAmbiguousDateIsAccepted()
    {
        // 04-03-1970 is two different dates depending on who wrote it. Only the day first
        // reading is configured, so it is read as 4 March and never as 3 April: a date of
        // birth quietly wrong by a few months is worse than one left blank.
        FilenameReadResult result = Reader(new[] { "{PatientId}_{DateOfBirth}" }).Read("1_04-03-1970.pdf");

        Assert.Equal(new DateTime(1970, 3, 4), result.Demographics!.DateOfBirth);
    }

    [Fact]
    public void NoDefaultFormatUsesASlashBecauseAFileNameCannotContainOne()
    {
        // A slash is the directory separator on both platforms, so a date written with
        // them cannot reach this code: "1_21/05/1970.pdf" has a file name of "1970".
        // A format that can never match would only mislead somebody reading the list to
        // work out why their date was not recognised.
        Assert.DoesNotContain(
            FilenameImportConfiguration.DefaultDateFormats,
            format => format.Contains('/', StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreadableDateIsReportedAndLeftBlankRatherThanRefusingTheFile()
    {
        FilenameReadResult result = Reader(new[] { "{PatientId}_{Name}_{DateOfBirth}" })
            .Read("1_Ana Silva_not-a-date.pdf");

        Assert.True(result.IsMatch);
        Assert.Null(result.Demographics!.DateOfBirth);
        Assert.Contains(result.Notes, n => n.Contains("left blank", StringComparison.Ordinal));
    }

    [Fact]
    public void TheStudyDateIsCarriedThroughWhenOneIsSupplied()
    {
        var created = new DateTime(2026, 3, 14, 10, 32, 0);

        FilenameReadResult result = Reader(new[] { "{PatientId}_{Name}" })
            .Read("1_Ana Silva.pdf", created);

        Assert.Equal(created, result.Demographics!.StudyDateTime);
    }

    [Fact]
    public void ANameThatMatchesNothingIsReportedRatherThanGuessedAt()
    {
        FilenameReadResult result = Reader(new[] { "{PatientId}_{Name}_{AccessionNumber}" })
            .Read("whatever.pdf");

        Assert.False(result.IsMatch);
        Assert.Null(result.Demographics);
        Assert.Contains(result.Notes, n => n.Contains("does not match", StringComparison.Ordinal));
    }

    [Fact]
    public void AMalformedPatternIsReportedAndTheOthersStillWork()
    {
        // A bad pattern must not stop the application starting, nor disable the good ones.
        FilenameMetadataReader reader = Reader(new[] { "{Nonsense}", "{PatientId}_{Name}" });

        Assert.Single(reader.PatternErrors);
        Assert.Equal(1, reader.UsablePatternCount);

        FilenameReadResult result = reader.Read("1_Ana Silva.pdf");

        Assert.True(result.IsMatch);
    }
}

/// <summary>
/// Covers what happens to a source file once it has been imported.
/// </summary>
/// <remarks>
/// This moves files that belong to somebody else, so the tests are about what it must
/// never do: overwrite, delete, or mark a failure as done. Each one works in a temporary
/// directory of its own and cleans up after itself.
/// </remarks>
public sealed class ImportedFileFilerTests : IDisposable
{
    private readonly string _folder;
    private readonly ImportedFileFiler _filer = new(new RecordingLogger());

    public ImportedFileFilerTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"pdftodicom-filer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test run over.
        }
    }

    private string Given(string name, string content = "x")
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static FilenameImportConfiguration Settings(
        ImportedFileDisposition disposition = ImportedFileDisposition.MoveToSubfolder) =>
        new() { IsEnabled = true, AfterImport = disposition };

    [Fact]
    public void AnImportedFileGoesToTheImportedSubfolder()
    {
        string path = Given("report.pdf");

        FiledSourceFile filed = _filer.File(path, Settings(), succeeded: true);

        Assert.True(filed.Moved);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(_folder, "Imported", "report.pdf")));
    }

    [Fact]
    public void AFailedImportGoesSomewhereElseEntirely()
    {
        // It must not be filed as imported, which would be untrue, and it must not be left
        // where it is, because the monitor would offer it again on every restart for ever.
        string path = Given("report.pdf");

        FiledSourceFile filed = _filer.File(path, Settings(), succeeded: false);

        Assert.True(filed.Moved);
        Assert.True(File.Exists(Path.Combine(_folder, "Failed", "report.pdf")));
        Assert.False(File.Exists(Path.Combine(_folder, "Imported", "report.pdf")));
    }

    [Fact]
    public void RenamingTakesTheNameOutOfThePdfPattern()
    {
        // The suffix goes after the extension on purpose. Before it, the file would still
        // be called something.pdf and the monitor would pick it straight back up.
        string path = Given("report.pdf");

        FiledSourceFile filed = _filer.File(path, Settings(ImportedFileDisposition.Rename), succeeded: true);

        Assert.True(filed.Moved);
        Assert.EndsWith(".imported", filed.NewPath, StringComparison.Ordinal);
        Assert.False(filed.NewPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(filed.NewPath));
    }

    [Fact]
    public void AFailureIsMovedEvenWhenTheSettingSaysRename()
    {
        // The point of the failed folder is that somebody can see what is in it. A renamed
        // failure sitting in the monitored folder would be invisible among the work still
        // to do.
        string path = Given("report.pdf");

        _filer.File(path, Settings(ImportedFileDisposition.Rename), succeeded: false);

        Assert.True(File.Exists(Path.Combine(_folder, "Failed", "report.pdf")));
    }

    [Fact]
    public void ASecondFileOfTheSameNameNeverOverwritesTheFirst()
    {
        // Routine when a sending system reuses a name. Overwriting would destroy the only
        // record of what was sent the first time.
        _filer.File(Given("report.pdf", "first"), Settings(), succeeded: true);
        _filer.File(Given("report.pdf", "second"), Settings(), succeeded: true);

        string[] kept = Directory.GetFiles(Path.Combine(_folder, "Imported"));

        Assert.Equal(2, kept.Length);
        Assert.Contains("first", kept.Select(File.ReadAllText));
        Assert.Contains("second", kept.Select(File.ReadAllText));
    }

    [Fact]
    public void LeaveMeansLeave()
    {
        string path = Given("report.pdf");

        FiledSourceFile filed = _filer.File(path, Settings(ImportedFileDisposition.Leave), succeeded: true);

        Assert.False(filed.Moved);
        Assert.True(File.Exists(path));
        Assert.False(Directory.Exists(Path.Combine(_folder, "Imported")));
    }

    [Fact]
    public void AFileThatIsNoLongerThereIsNotAnError()
    {
        // It may have been moved by somebody else between the conversion and this call.
        FiledSourceFile filed = _filer.File(
            Path.Combine(_folder, "never-existed.pdf"), Settings(), succeeded: true);

        Assert.False(filed.Moved);
    }

    [Fact]
    public void NothingIsEverDeleted()
    {
        string path = Given("report.pdf", "the only copy");

        FiledSourceFile filed = _filer.File(path, Settings(), succeeded: true);

        Assert.True(File.Exists(filed.NewPath));
        Assert.Equal("the only copy", File.ReadAllText(filed.NewPath));
    }
}

/// <summary>
/// The audit entry that records a file name as the source of the patient identifiers.
/// </summary>
/// <remarks>
/// Worth a test of its own because it is the only record, after the fact, that nobody
/// read these identifiers off a patient before the document was filed.
/// </remarks>
public sealed class FilenameImportAuditTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "PdfToDicomAudit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TheFileNameAndThePatternAreBothRecorded()
    {
        var audit = new AuditLogger(_folder, new RecordingLogger());

        audit.PatientInformationReadFromFilename(
            "9999999999_JohnDoe_TEST123451.pdf", "{PatientId}_{Name}_{AccessionNumber}");

        string written = string.Join(
            Environment.NewLine,
            Directory.GetFiles(audit.AuditFolder).Select(File.ReadAllText));

        Assert.Contains("PATIENT_INFO_FROM_FILENAME", written, StringComparison.Ordinal);
        Assert.Contains("file=9999999999_JohnDoe_TEST123451.pdf", written, StringComparison.Ordinal);
        Assert.Contains("pattern={PatientId}_{Name}_{AccessionNumber}", written, StringComparison.Ordinal);
    }
}
