using PdfToDicom.Interop.Hl7V2.Profiles;
using Xunit;

namespace PdfToDicom.Interop.Tests;

public sealed class Hl7ProfileReaderTests
{
    /// <summary>
    /// A complete profile in the shape the architecture document proposes, with values
    /// taken from the specimen conformance statement rather than invented. If this does not
    /// read, the configuration model does not work, whatever the types say.
    /// </summary>
    private const string WorkedProfile = """
    {
      "profileId": "example-oru-r01",
      "displayName": "Example ORU^R01, report with encapsulated document",
      "revision": 2,
      "enabled": true,
      "hl7Version": "2.4",
      "messageType": "ORU",
      "triggerEvent": "R01",
      "messageStructure": "ORU_R01",
      "processingId": "P",
      "encoding": {
        "fieldSeparator": "|",
        "encodingCharacters": "^~\\&",
        "characterSet": "8859/1",
        "onUnrepresentableCharacter": "refuse"
      },
      "acknowledgement": {
        "expect": "none",
        "acceptAckType": "NE",
        "applicationAckType": "NE",
        "timeoutSeconds": 30
      },
      "segments": [
        { "id": "MSH", "usage": "R", "min": 1, "max": 1 },
        { "id": "PID", "usage": "R", "min": 1, "max": 1, "group": "PATIENT" },
        { "id": "PD1", "usage": "X" },
        { "id": "NK1", "usage": "X" },
        { "id": "ORC", "usage": "O", "min": 0, "max": 1, "group": "ORDER_OBSERVATION" },
        { "id": "OBR", "usage": "R", "min": 1, "max": 1, "group": "ORDER_OBSERVATION" },
        { "id": "OBX", "usage": "O", "min": 0, "max": 0, "group": "OBSERVATION" },
        { "id": "ZDS", "usage": "O", "min": 0, "max": 1 }
      ],
      "fields": [
        { "path": "PID-3.1",   "name": "Patient identifier", "dataType": "ST", "usage": "R", "maxLength": 20 },
        { "path": "PID-5.1.1", "name": "Family name",        "dataType": "ST", "usage": "R", "maxLength": 30 },
        { "path": "PID-8",     "name": "Administrative sex", "dataType": "IS", "usage": "O",
          "allowedValues": ["M", "F", "O", ""] },
        { "path": "ORC-1",     "name": "Order control",      "dataType": "ID", "usage": "R",
          "allowedValues": ["CN", "RE"] },
        { "path": "OBX-2",     "name": "Value type",         "dataType": "ID", "usage": "R",
          "allowedValues": ["TX", "RP", "ED"] },
        { "path": "ZDS-1",     "name": "Study Instance UID", "dataType": "ST", "usage": "O", "maxLength": 64 }
      ],
      "mappings": [
        { "target": "MSH-3.1",   "value": "PDFTODICOM" },
        { "target": "MSH-7",     "source": "now",                 "transform": "hl7DateTime" },
        { "target": "PID-3.1",   "source": "patient.id",          "required": true },
        { "target": "PID-5.1.1", "source": "patient.familyName",  "transform": "truncate:30", "required": true },
        { "target": "PID-8",     "source": "patient.sex",         "table": "sex" },
        { "target": "OBR-25",    "value": "F" },
        { "target": "OBX-2",     "value": "ED" },
        { "target": "ZDS-1",     "source": "study.studyInstanceUid" }
      ],
      "tables": {
        "sex": { "Male": "M", "Female": "F", "Other": "O", "Unspecified": "" }
      }
    }
    """;

    [Fact]
    public void TheWorkedProfileReadsAndIsUsable()
    {
        ProfileReadResult result = Hl7ProfileReader.Read(WorkedProfile);

        Assert.True(result.IsUsable, string.Join("; ", result.Issues));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void EveryPartOfTheDocumentArrivesIntact()
    {
        Hl7Profile profile = Hl7ProfileReader.Read(WorkedProfile).Profile!;

        Assert.Equal("example-oru-r01", profile.ProfileId);
        Assert.Equal(2, profile.Revision);
        Assert.Equal("2.4", profile.Hl7Version);
        Assert.Equal("ORU^R01", profile.MessageTypeAndTrigger);
        Assert.Equal("ORU_R01", profile.MessageStructure);
        Assert.Equal("8859/1", profile.Encoding.CharacterSet);
        Assert.Equal(8, profile.Segments.Count);
        Assert.Equal(6, profile.Fields.Count);
        Assert.Equal(8, profile.Mappings.Count);
        Assert.Equal("F", profile.Tables["sex"]["Female"]);
    }

    [Fact]
    public void AnInterfaceThatAcknowledgesNothingSaysSoInATypeTheTransportCanAct0n()
    {
        Hl7Profile profile = Hl7ProfileReader.Read(WorkedProfile).Profile!;

        Assert.True(profile.Acknowledgement.ExpectsNone);
        Assert.Equal("NE", profile.Acknowledgement.AcceptAckType);
        Assert.Equal("NE", profile.Acknowledgement.ApplicationAckType);
    }

    [Fact]
    public void UsageIsReadInEitherSpelling()
    {
        Hl7Profile profile = Hl7ProfileReader.Read(WorkedProfile).Profile!;

        Assert.Equal(ProfileUsage.Required, profile.SegmentNamed("PID")!.Usage);
        Assert.Equal(ProfileUsage.NotSupported, profile.SegmentNamed("PD1")!.Usage);

        ProfileReadResult spelledOut = Hl7ProfileReader.Read("""
        { "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "segments": [ { "id": "NTE", "usage": "not supported" } ] }
        """);

        Assert.Equal(ProfileUsage.NotSupported, spelledOut.Profile!.SegmentNamed("NTE")!.Usage);
    }

    [Fact]
    public void AnUnboundedSegmentSaysSoRatherThanPickingALargeNumber()
    {
        Hl7Profile profile = Hl7ProfileReader.Read(WorkedProfile).Profile!;

        Assert.True(profile.SegmentNamed("OBX")!.IsUnbounded);
        Assert.False(profile.SegmentNamed("PID")!.IsUnbounded);
    }

    [Fact]
    public void AZSegmentIsJustAnotherSegment()
    {
        // ZDS-1 carries a Study Instance UID. Nothing here knows the segment exists, which
        // is the test of whether a site specific requirement really is configuration.
        Hl7Profile profile = Hl7ProfileReader.Read(WorkedProfile).Profile!;

        Assert.NotNull(profile.SegmentNamed("ZDS"));
        Assert.NotNull(profile.FieldAt("ZDS-1"));
        Assert.Contains(profile.Mappings, m => m.Target == "ZDS-1");
    }

    [Fact]
    public void ADocumentThatIsNotJsonIsRefusedWithSomethingReadable()
    {
        ProfileReadResult result = Hl7ProfileReader.Read("{ this is not json");

        Assert.False(result.IsUsable);
        Assert.Null(result.Profile);
        Assert.Contains("could not be read as JSON", Assert.Single(result.Issues).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AProfileMissingWhatItCannotDoWithoutIsRefused()
    {
        ProfileReadResult result = Hl7ProfileReader.Read("""{ "displayName": "nameless" }""");

        Assert.False(result.IsUsable);
    }

    [Fact]
    public void AMappingIntoASegmentTheReceiverRefusesIsAnError()
    {
        // The check worth having most. A value configured into PD1 would be built happily
        // and then rejected by the receiver, or worse, silently ignored by it.
        ProfileReadResult result = Hl7ProfileReader.Read("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "segments": [ { "id": "PD1", "usage": "X" } ],
          "mappings": [ { "target": "PD1-3", "source": "patient.id" } ]
        }
        """);

        Assert.False(result.IsUsable);
        Assert.Contains(result.Errors, i => i.Message.Contains("not supported", StringComparison.Ordinal));
    }

    [Fact]
    public void AMappingIntoAFieldTheReceiverRefusesIsAlsoAnError()
    {
        ProfileReadResult result = Hl7ProfileReader.Read("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "fields": [ { "path": "PID-19", "usage": "X" } ],
          "mappings": [ { "target": "PID-19", "source": "patient.ssn" } ]
        }
        """);

        Assert.False(result.IsUsable);
    }

    [Fact]
    public void ATransformThisBuildDoesNotKnowStopsTheProfileLoading()
    {
        // Caught while an administrator is looking at it, not when a report is waiting.
        ProfileReadResult result = Hl7ProfileReader.Read("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "mappings": [ { "target": "PID-5.1.1", "source": "patient.familyName",
                          "transform": "shellOut" } ]
        }
        """);

        Assert.False(result.IsUsable);
        Assert.Contains(result.Errors, i => i.Message.Contains("not a transform", StringComparison.Ordinal));
    }

    [Fact]
    public void AMappingNamingATableThatDoesNotExistIsAnError()
    {
        ProfileReadResult result = Hl7ProfileReader.Read("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "mappings": [ { "target": "PID-8", "source": "patient.sex", "table": "gender" } ]
        }
        """);

        Assert.False(result.IsUsable);
        Assert.Contains(result.Errors, i => i.Message.Contains("no code table", StringComparison.Ordinal));
    }

    [Fact]
    public void AMappingWithNeitherASourceNorAValueDoesNothingAndIsRefused()
    {
        ProfileReadResult result = Hl7ProfileReader.Read("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "mappings": [ { "target": "PID-8" } ]
        }
        """);

        Assert.False(result.IsUsable);
    }

    [Theory]
    [InlineData("\"fieldSeparator\": \"||\"")]
    [InlineData("\"encodingCharacters\": \"^~\\\\\"")]
    [InlineData("\"characterSet\": \"8859/15\"")]
    [InlineData("\"onUnrepresentableCharacter\": \"improvise\"")]
    public void EncodingThatCannotBeHonouredIsRefused(string encodingFragment)
    {
        ProfileReadResult result = Hl7ProfileReader.Read($$"""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "encoding": { {{encodingFragment}} }
        }
        """);

        Assert.False(result.IsUsable);
    }

    [Fact]
    public void ChoosingToStripAccentsIsAllowedAndIsNeverSilent()
    {
        ProfileReadResult result = Hl7ProfileReader.Read("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "encoding": { "onUnrepresentableCharacter": "transliterate" }
        }
        """);

        Assert.True(result.IsUsable);

        ProfileIssue warning = Assert.Single(result.Issues);
        Assert.Equal(ProfileIssueSeverity.Warning, warning.Severity);
        Assert.Contains("different names for the same patient", warning.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ASegmentBothForbiddenAndRequiredIsAContradictionAndIsRefused()
    {
        ProfileReadResult result = Hl7ProfileReader.Read("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "segments": [ { "id": "NTE", "usage": "X", "min": 1, "max": 1 } ]
        }
        """);

        Assert.False(result.IsUsable);
    }

    [Fact]
    public void TheSameSegmentOrFieldListedTwiceIsRefused()
    {
        Assert.False(Hl7ProfileReader.Read("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "segments": [ { "id": "PID" }, { "id": "PID" } ]
        }
        """).IsUsable);

        Assert.False(Hl7ProfileReader.Read("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "fields": [ { "path": "PID-5" }, { "path": "PID-5.1.1" } ]
        }
        """).IsUsable);
    }

    [Fact]
    public void AFieldPathThatIsNotOneIsRefused()
    {
        Assert.False(Hl7ProfileReader.Read("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "fields": [ { "path": "patient name" } ]
        }
        """).IsUsable);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreToleratedBecausePeopleEditTheseFiles()
    {
        ProfileReadResult result = Hl7ProfileReader.Read("""
        {
          // The site asked for this one.
          "profileId": "p",
          "messageType": "ORU",
          "triggerEvent": "R01",
        }
        """);

        Assert.True(result.IsUsable);
    }
}
