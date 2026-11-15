using PdfToDicom.Interop.Hl7V2.Inspection;
using PdfToDicom.Interop.Hl7V2.Model;
using PdfToDicom.Interop.Hl7V2.Profiles;
using Xunit;

namespace PdfToDicom.Interop.Tests;

public sealed class Hl7MessageInspectorTests
{
    private const string ReportProfile = """
    {
      "profileId": "example-oru-r01",
      "messageType": "ORU", "triggerEvent": "R01",
      "segments": [
        { "id": "MSH", "usage": "R", "min": 1, "max": 1 },
        { "id": "PID", "usage": "R", "min": 1, "max": 1, "group": "PATIENT" },
        { "id": "PD1", "usage": "X" },
        { "id": "OBR", "usage": "R", "min": 1, "max": 1, "group": "ORDER_OBSERVATION" },
        { "id": "OBX", "usage": "O", "min": 0, "max": 0, "group": "OBSERVATION" }
      ],
      "fields": [
        { "path": "PID-3.1",   "name": "Patient identifier", "usage": "R", "maxLength": 20 },
        { "path": "PID-5.1.1", "name": "Family name",        "usage": "R", "maxLength": 30 },
        { "path": "PID-8",     "name": "Administrative sex", "usage": "O",
          "allowedValues": ["M", "F", "O", ""] },
        { "path": "OBR-3.1",   "name": "Accession number",   "usage": "R" }
      ]
    }
    """;

    private static Hl7Profile Profile(string json = ReportProfile) =>
        Hl7ProfileReader.Read(json).Profile!;

    private const string CompleteMessage =
        "MSH|^~\\&|PDFTODICOM||||||ORU^R01|1|P|2.4\r" +
        "PID|||1234567||Silva^Maria|||F\r" +
        "OBR|||ACC001";

    [Fact]
    public void EverySegmentTheProfileDescribesAppears()
    {
        MessageInspection inspection =
            Hl7MessageInspector.Inspect(Hl7Message.Parse(CompleteMessage), Profile());

        Assert.Contains(inspection.Segments, s => s.Name == "MSH" && s.IsPresent);
        Assert.Contains(inspection.Segments, s => s.Name == "PID" && s.IsPresent);
        Assert.Contains(inspection.Segments, s => s.Name == "OBR" && s.IsPresent);
    }

    [Fact]
    public void ASegmentTheProfileExpectsAndTheMessageLacksIsShownAsAbsent()
    {
        // Shown rather than left out, so its absence is visible rather than inferred from a
        // list that simply does not mention it.
        MessageInspection inspection =
            Hl7MessageInspector.Inspect(Hl7Message.Parse(CompleteMessage), Profile());

        InspectedSegment obx = Assert.Single(inspection.Segments.Where(s => s.Name == "OBX"));

        Assert.False(obx.IsPresent);
    }

    [Fact]
    public void SegmentsComeInTheProfilesOrderBecauseThatIsTheDocumentsOrder()
    {
        MessageInspection inspection =
            Hl7MessageInspector.Inspect(Hl7Message.Parse(CompleteMessage), Profile());

        Assert.Equal(
            ["MSH", "PID", "PD1", "OBR", "OBX"],
            inspection.Segments.Select(s => s.Name));
    }

    [Fact]
    public void ASegmentTheProfileNeverMentionedIsShownLastRatherThanHidden()
    {
        // An unexpected segment in a message is exactly the sort of thing worth seeing.
        MessageInspection inspection = Hl7MessageInspector.Inspect(
            Hl7Message.Parse(CompleteMessage + "\rZDS|1.2.3"), Profile());

        Assert.Equal("ZDS", inspection.Segments.Last().Name);
        Assert.True(inspection.Segments.Last().IsPresent);
    }

    [Fact]
    public void RepeatedSegmentsAreNumberedForTheTree()
    {
        MessageInspection inspection = Hl7MessageInspector.Inspect(
            Hl7Message.Parse(CompleteMessage + "\rOBX|1|TX|||one\rOBX|2|TX|||two"), Profile());

        IEnumerable<InspectedSegment> obx = inspection.Segments.Where(s => s.Name == "OBX");

        Assert.Equal(["OBX", "OBX (2)"], obx.Select(s => s.Label));
    }

    [Fact]
    public void AFieldCarriesWhatTheProfileCallsItSoTheTreeIsReadable()
    {
        MessageInspection inspection =
            Hl7MessageInspector.Inspect(Hl7Message.Parse(CompleteMessage), Profile());

        InspectedField name = FieldAt(inspection, "PID-5.1.1");

        Assert.Equal("Family name", name.Name);
        Assert.Equal("Silva", name.Value);
        Assert.Equal(ProfileUsage.Required, name.Usage);
        Assert.Equal(InspectedFieldOrigin.InBoth, name.Origin);
    }

    [Fact]
    public void AMissingMandatoryFieldIsTheWholePointAndIsEasyToFind()
    {
        Hl7Message message = Hl7Message.Parse("MSH|^~\\&|A\rPID|||1234567\rOBR|||ACC001");

        MessageInspection inspection = Hl7MessageInspector.Inspect(message, Profile());

        InspectedField missing = Assert.Single(inspection.MissingMandatoryFields);

        Assert.Equal("PID-5.1.1", missing.Path);
        Assert.Equal("Family name", missing.Name);
        Assert.True(missing.IsMissingAndRequired);
        Assert.Equal(InspectedFieldOrigin.InProfileOnly, missing.Origin);
        Assert.False(inspection.IsSendable);
    }

    [Fact]
    public void AMessageThatSatisfiesItsProfileHasNothingMissingAndNothingWrong()
    {
        MessageInspection inspection =
            Hl7MessageInspector.Inspect(Hl7Message.Parse(CompleteMessage), Profile());

        Assert.Empty(inspection.MissingMandatoryFields);
        Assert.Empty(inspection.Errors);
        Assert.True(inspection.IsSendable);
    }

    [Fact]
    public void AnIssueIsAttachedToTheFieldItIsAbout()
    {
        Hl7Message message = Hl7Message.Parse(
            "MSH|^~\\&|A\rPID|||1234567||Silva|||X\rOBR|||ACC001");

        MessageInspection inspection = Hl7MessageInspector.Inspect(message, Profile());

        InspectedField sex = FieldAt(inspection, "PID-8");

        Assert.True(sex.HasError);
        Assert.Contains("'X' is not one of", Assert.Single(sex.Issues).Message,
            StringComparison.Ordinal);

        // And the segment holding it reports an error, so a collapsed tree still shows it.
        Assert.True(inspection.Segments.Single(s => s.Name == "PID" && s.Occurrence == 1).HasError);
    }

    [Fact]
    public void ASegmentTheDestinationRefusesIsFlaggedOnTheSegmentItself()
    {
        MessageInspection inspection = Hl7MessageInspector.Inspect(
            Hl7Message.Parse(CompleteMessage + "\rPD1|1"), Profile());

        InspectedSegment pd1 = inspection.Segments.Single(s => s.Name == "PD1");

        Assert.True(pd1.IsPresent);
        Assert.True(pd1.HasError);
    }

    [Fact]
    public void AFieldTheProfileNeverMentionedIsStillShown()
    {
        MessageInspection inspection =
            Hl7MessageInspector.Inspect(Hl7Message.Parse(CompleteMessage), Profile());

        InspectedSegment msh = inspection.Segments.Single(s => s.Name == "MSH");

        Assert.Contains(msh.Fields, f =>
            f.Value == "PDFTODICOM" && f.Origin == InspectedFieldOrigin.InMessageOnly);

        // The delimiters are shown too, because somebody debugging an encoding problem
        // needs them, and they are never flagged because nobody configured them.
        Assert.Contains(msh.Fields, f => f.Path == "MSH-1" && f.Value == "|");
        Assert.DoesNotContain(msh.Fields.Where(f => f.Path == "MSH-1"), f => f.HasError);
    }

    [Fact]
    public void ACompositeFieldTheProfileNeverMentionedIsShownWhole()
    {
        // MSH-9 carries "ORU^R01". Showing its first component would read "ORU" and invite
        // the conclusion that the trigger event is missing, which is the opposite of what
        // this screen is opened to establish.
        MessageInspection inspection =
            Hl7MessageInspector.Inspect(Hl7Message.Parse(CompleteMessage), Profile());

        InspectedSegment msh = inspection.Segments.Single(s => s.Name == "MSH");

        Assert.Contains(msh.Fields, f => f.Path == "MSH-9" && f.Value == "ORU^R01");
    }

    [Fact]
    public void AProfileFieldThatNamesAWholeFieldIsShownWhole()
    {
        const string profile = """
        {
          "profileId": "composite", "messageType": "ORU", "triggerEvent": "R01",
          "segments": [
            { "id": "MSH", "usage": "R", "min": 1, "max": 1 },
            { "id": "PID", "usage": "R", "min": 1, "max": 1 }
          ],
          "fields": [
            { "path": "PID-5", "name": "Patient name", "usage": "R" },
            { "path": "PID-5.1.1", "name": "Family name", "usage": "R" }
          ]
        }
        """;

        MessageInspection inspection =
            Hl7MessageInspector.Inspect(Hl7Message.Parse(CompleteMessage), Profile(profile));

        InspectedSegment pid = inspection.Segments.Single(s => s.Name == "PID");

        // The whole field and one of its components, side by side, each read as written.
        Assert.Contains(pid.Fields, f => f.Path == "PID-5" && f.Value == "Silva^Maria");
        Assert.Contains(pid.Fields, f => f.Path == "PID-5.1.1" && f.Value == "Silva");
    }

    [Fact]
    public void TheInspectionCarriesExactlyWhatWouldGoOnTheWire()
    {
        // A preview is only worth showing if it is the thing that will be sent.
        MessageInspection inspection =
            Hl7MessageInspector.Inspect(Hl7Message.Parse(CompleteMessage), Profile());

        Assert.Equal(CompleteMessage, inspection.Er7);
    }

    [Fact]
    public void TwoProfilesThatAgreeAreReportedAsAgreeing()
    {
        Hl7Message message = Hl7Message.Parse(CompleteMessage);

        ProfileComparison comparison =
            Hl7MessageInspector.Compare(message, Profile(), Profile());

        Assert.True(comparison.Agree);
        Assert.True(comparison.LeftAccepts);
        Assert.True(comparison.RightAccepts);
    }

    [Fact]
    public void ComparingTwoProfilesShowsOnlyWhatTheyDisagreeAbout()
    {
        // The overlap is almost always the uninteresting part, which is why this answers
        // with the difference rather than with two lists.
        Hl7Profile strict = Profile();
        Hl7Profile lenient = Profile("""
        {
          "profileId": "lenient",
          "messageType": "ORU", "triggerEvent": "R01",
          "fields": [ { "path": "PID-3.1", "name": "Patient identifier", "usage": "R" } ]
        }
        """);

        Hl7Message message = Hl7Message.Parse("MSH|^~\\&|A\rPID|||1234567");

        ProfileComparison comparison = Hl7MessageInspector.Compare(message, strict, lenient);

        Assert.False(comparison.Agree);
        Assert.False(comparison.LeftAccepts);
        Assert.True(comparison.RightAccepts);

        Assert.NotEmpty(comparison.OnlyLeftObjects);
        Assert.Empty(comparison.OnlyRightObjects);
        Assert.Contains(comparison.OnlyLeftObjects, i => i.Location == "OBR");
    }

    [Fact]
    public void AMessageSomebodyPastedInCanBeInspectedWithoutBeingBuiltHere()
    {
        // Importing a sample from a vendor and asking why it was rejected is the other half
        // of what this screen is for.
        const string pasted = "MSH|^~\\&|THEIRS|||||ORU^R01|9|P|2.4\rPID|||999";

        MessageInspection inspection =
            Hl7MessageInspector.Inspect(Hl7Message.Parse(pasted), Profile());

        Assert.False(inspection.IsSendable);
        Assert.Contains(inspection.MissingMandatoryFields, f => f.Path == "PID-5.1.1");
        Assert.Contains(inspection.Errors, i => i.Location == "OBR");
    }

    private static InspectedField FieldAt(MessageInspection inspection, string path) =>
        inspection.Segments.SelectMany(s => s.Fields).Single(f => f.Path == path);
}
