using PdfToDicom.Interop.Hl7V2.Model;
using PdfToDicom.Interop.Hl7V2.Profiles;
using PdfToDicom.Interop.Messaging;
using Xunit;

namespace PdfToDicom.Interop.Tests;

public sealed class Hl7MessageBuilderTests
{
    private static readonly DateTimeOffset FixedMoment =
        new(2026, 11, 15, 9, 30, 0, TimeSpan.Zero);

    private static Hl7MessageBuilder Builder() =>
        new(new Hl7ControlIdGenerator(() => FixedMoment), () => FixedMoment);

    private static Hl7Profile Profile(string json) =>
        Hl7ProfileReader.Read(json).Profile
        ?? throw new InvalidOperationException("the test profile did not read");

    private const string ReportProfile = """
    {
      "profileId": "example-oru-r01",
      "messageType": "ORU",
      "triggerEvent": "R01",
      "messageStructure": "ORU_R01",
      "hl7Version": "2.4",
      "controlIdPrefix": "P2D",
      "encoding": { "characterSet": "8859/1" },
      "acknowledgement": { "expect": "none", "acceptAckType": "NE", "applicationAckType": "NE" },
      "segments": [
        { "id": "MSH", "usage": "R", "min": 1, "max": 1 },
        { "id": "PID", "usage": "R", "min": 1, "max": 1 },
        { "id": "PD1", "usage": "X" },
        { "id": "OBR", "usage": "R", "min": 1, "max": 1 },
        { "id": "ZDS", "usage": "O", "min": 0, "max": 1 }
      ],
      "fields": [
        { "path": "PID-3.1",   "name": "Patient identifier", "usage": "R", "maxLength": 20 },
        { "path": "PID-5.1.1", "name": "Family name",        "usage": "R", "maxLength": 30 },
        { "path": "PID-8",     "name": "Administrative sex", "usage": "O",
          "allowedValues": ["M", "F", "O", ""] },
        { "path": "OBR-3.1",   "name": "Accession number",   "usage": "R" }
      ],
      "mappings": [
        { "target": "MSH-3.1",   "value": "PDFTODICOM" },
        { "target": "PID-3.1",   "source": "patient.id",          "required": true },
        { "target": "PID-5.1.1", "source": "patient.familyName",  "transform": "truncate:30", "required": true },
        { "target": "PID-5.2",   "source": "patient.givenName" },
        { "target": "PID-7.1",   "source": "patient.dateOfBirth", "transform": "hl7Date" },
        { "target": "PID-8",     "source": "patient.sex",         "table": "sex" },
        { "target": "OBR-3.1",   "source": "study.accessionNumber", "required": true },
        { "target": "ZDS-1",     "source": "study.studyInstanceUid" }
      ],
      "tables": {
        "sex": { "Male": "M", "Female": "F", "Other": "O", "Unspecified": "" }
      }
    }
    """;

    private static MessageSubject CompleteSubject() =>
        new MessageSubject()
            .With("patient.id", "0000000077")
            .With("patient.familyName", "Silva")
            .With("patient.givenName", "Maria")
            .With("patient.dateOfBirth", "1970-05-21")
            .With("patient.sex", "Female")
            .With("study.accessionNumber", "ACC001")
            .With("study.studyInstanceUid", "1.2.826.0.1.3680043.8.498.1");

    [Fact]
    public void AProfileAndASubjectProduceASendableMessage()
    {
        Hl7BuildResult result = Builder().Build(Profile(ReportProfile), CompleteSubject());

        Assert.True(result.IsSendable, string.Join("; ", result.Issues));
        Assert.NotNull(result.Message);
    }

    [Fact]
    public void TheHeaderCarriesWhatTheProfileDeclares()
    {
        Hl7Message message = Builder().Build(Profile(ReportProfile), CompleteSubject()).Message!;

        Assert.Equal("PDFTODICOM", message["MSH-3.1"]);
        Assert.Equal("20261115093000", message["MSH-7"]);
        Assert.Equal("ORU", message["MSH-9.1"]);
        Assert.Equal("R01", message["MSH-9.2"]);
        Assert.Equal("ORU_R01", message["MSH-9.3"]);
        Assert.Equal("P", message["MSH-11"]);
        Assert.Equal("2.4", message["MSH-12"]);
        Assert.Equal("8859/1", message["MSH-18"]);
    }

    [Fact]
    public void TheAcknowledgementFieldsSayWhatTheInterfaceWillDo()
    {
        // A transport reads these to decide whether to wait at all.
        Hl7Message message = Builder().Build(Profile(ReportProfile), CompleteSubject()).Message!;

        Assert.Equal("NE", message["MSH-15"]);
        Assert.Equal("NE", message["MSH-16"]);
    }

    [Fact]
    public void TheControlIdentifierIsPrefixedTraceableAndNeverRepeats()
    {
        var builder = Builder();
        Hl7Profile profile = Profile(ReportProfile);

        string first = builder.Build(profile, CompleteSubject()).Message!["MSH-10"];
        string second = builder.Build(profile, CompleteSubject()).Message!["MSH-10"];

        Assert.StartsWith("P2D20261115093000", first, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ValuesArriveWhereTheMappingsPutThem()
    {
        Hl7Message message = Builder().Build(Profile(ReportProfile), CompleteSubject()).Message!;

        Assert.Equal("0000000077", message["PID-3.1"]);
        Assert.Equal("Silva", message["PID-5.1.1"]);
        Assert.Equal("Maria", message["PID-5.2"]);
        Assert.Equal("ACC001", message["OBR-3.1"]);
    }

    [Fact]
    public void ATransformRunsOnTheWayIn()
    {
        MessageSubject subject = CompleteSubject().With("patient.familyName", new string('A', 40));

        Hl7Message message = Builder().Build(Profile(ReportProfile), subject).Message!;

        Assert.Equal(30, message["PID-5.1.1"].Length);
    }

    [Fact]
    public void ADateBecomesTheFormTheStandardWants()
    {
        Hl7Message message = Builder().Build(Profile(ReportProfile), CompleteSubject()).Message!;

        Assert.Equal("19700521", message["PID-7.1"]);
    }

    [Fact]
    public void ACodeTableTurnsOneVocabularyIntoAnother()
    {
        // The whole point: a sex of "Female" becomes F without anybody writing code.
        Hl7Message message = Builder().Build(Profile(ReportProfile), CompleteSubject()).Message!;

        Assert.Equal("F", message["PID-8"]);
    }

    [Fact]
    public void AValueTheTableDoesNotMentionPassesThroughAndIsThenRefusedByName()
    {
        // Emptying it would quietly drop information. Passing it through lets the field
        // rules object, and the objection names the field and the value.
        MessageSubject subject = CompleteSubject().With("patient.sex", "Unknown");

        Hl7BuildResult result = Builder().Build(Profile(ReportProfile), subject);

        Assert.False(result.IsSendable);
        Assert.Contains(result.Errors, i =>
            i.Location == "PID-8" && i.Message.Contains("'Unknown'", StringComparison.Ordinal));
    }

    [Fact]
    public void ZDS1CarriesTheStudyInstanceUidWithoutAnybodyKnowingWhatZdsIs()
    {
        Hl7Message message = Builder().Build(Profile(ReportProfile), CompleteSubject()).Message!;

        Assert.Equal("1.2.826.0.1.3680043.8.498.1", message["ZDS-1"]);
        Assert.Contains("ZDS|1.2.826", message.ToEr7(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRequiredValueStopsTheMessageAndNamesTheSource()
    {
        MessageSubject subject = CompleteSubject().With("study.accessionNumber", "");

        Hl7BuildResult result = Builder().Build(Profile(ReportProfile), subject);

        Assert.False(result.IsSendable);
        Assert.Contains(result.Errors, i =>
            i.Location == "OBR-3.1" &&
            i.Message.Contains("study.accessionNumber", StringComparison.Ordinal));
    }

    [Fact]
    public void AMessageWithErrorsIsStillReturnedSoSomebodyCanLookAtIt()
    {
        // The editor that finds a missing mandatory field has to show something. Nothing is
        // transmitted on that basis: the sending path asks IsSendable.
        Hl7BuildResult result = Builder().Build(Profile(ReportProfile), MessageSubject.Empty);

        Assert.False(result.IsSendable);
        Assert.NotNull(result.Message);
        Assert.Contains("MSH", result.Message!.ToEr7(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyOptionalValueIsLeftOutRatherThanWrittenEmpty()
    {
        MessageSubject subject = CompleteSubject().With("study.studyInstanceUid", "");

        Hl7Message message = Builder().Build(Profile(ReportProfile), subject).Message!;

        Assert.Null(message.FindSegment("ZDS"));
    }

    [Fact]
    public void AFixedValueIsUsedOnlyWhenTheSourceHasNothing()
    {
        Hl7Profile profile = Profile("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "mappings": [
            { "target": "PID-8", "source": "patient.sex", "value": "O" }
          ]
        }
        """);

        Assert.Equal("O", Builder().Build(profile, MessageSubject.Empty).Message!["PID-8"]);

        Assert.Equal("M", Builder()
            .Build(profile, new MessageSubject().With("patient.sex", "M"))
            .Message!["PID-8"]);
    }

    [Fact]
    public void AMessageThatWasBuiltCanBeReadBackByTheCodec()
    {
        Hl7Message built = Builder().Build(Profile(ReportProfile), CompleteSubject()).Message!;

        Hl7Message reparsed = Hl7Message.Parse(built.ToEr7());

        Assert.Equal(built.ToEr7(), reparsed.ToEr7());
        Assert.Equal("Silva", reparsed["PID-5.1.1"]);
    }

    [Fact]
    public void NothingInTheBuilderKnowsWhatAnOruIs()
    {
        // The same builder, a different profile, a different message. If this needed a
        // branch on message type, the engine would not be generic.
        Hl7Profile admit = Profile("""
        {
          "profileId": "admit", "messageType": "ADT", "triggerEvent": "A04",
          "hl7Version": "2.3",
          "segments": [ { "id": "MSH", "usage": "R", "min": 1, "max": 1 },
                        { "id": "EVN", "usage": "R", "min": 1, "max": 1 } ],
          "mappings": [ { "target": "EVN-1", "value": "A04" },
                        { "target": "PID-5.1.1", "source": "patient.familyName" } ]
        }
        """);

        Hl7BuildResult result = Builder().Build(
            admit, new MessageSubject().With("patient.familyName", "Silva"));

        Assert.True(result.IsSendable, string.Join("; ", result.Issues));
        Assert.Equal("ADT", result.Message!["MSH-9.1"]);
        Assert.Equal("A04", result.Message["EVN-1"]);
        Assert.Equal("2.3", result.Message["MSH-12"]);
    }
}
