using PdfToDicom.Interop.Hl7V2.Model;
using PdfToDicom.Interop.Hl7V2.Profiles;
using PdfToDicom.Interop.Messaging;
using Xunit;

namespace PdfToDicom.Interop.Tests;

/// <summary>
/// The two places where this engine refuses rather than improvises: a name its character
/// set cannot carry, and a message its profile does not allow.
/// </summary>
public sealed class Hl7CharacterPolicyAndValidationTests
{
    private const string PortugueseName = "Conceição";
    private const string PolishName = "Wałęsa";

    private static Hl7Profile Profile(string json) =>
        Hl7ProfileReader.Read(json).Profile!;

    private static string NameProfile(string characterSet, string policy) => $$"""
    {
      "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
      "encoding": { "characterSet": "{{characterSet}}", "onUnrepresentableCharacter": "{{policy}}" },
      "mappings": [ { "target": "PID-5.1.1", "source": "patient.familyName" } ]
    }
    """;

    private static Hl7BuildResult Build(string profileJson, string familyName) =>
        new Hl7MessageBuilder().Build(
            Profile(profileJson), new MessageSubject().With("patient.familyName", familyName));

    [Fact]
    public void APortugueseNameGoesThroughTheCharacterSetTheSpecimenNames()
    {
        Hl7BuildResult result = Build(NameProfile("8859/1", "refuse"), PortugueseName);

        Assert.True(result.IsSendable, string.Join("; ", result.Issues));
        Assert.Equal(PortugueseName, result.Message!["PID-5.1.1"]);
    }

    [Fact]
    public void ANameTheCharacterSetCannotCarryStopsTheMessageAndNamesTheCharacter()
    {
        Hl7BuildResult result = Build(NameProfile("8859/1", "refuse"), PolishName);

        Assert.False(result.IsSendable);

        Hl7MessageIssue error = Assert.Single(result.Errors);
        Assert.Equal("PID-5.1.1", error.Location);
        Assert.Contains("ł", error.Message, StringComparison.Ordinal);
        Assert.Contains("not built rather than built with the name altered",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedValueIsNotWrittenAtAll()
    {
        // Half a name is worse than none: a receiver would store it and match on it.
        Hl7BuildResult result = Build(NameProfile("8859/1", "refuse"), PolishName);

        Assert.Equal(string.Empty, result.Message!["PID-5.1.1"]);
    }

    [Fact]
    public void StrippingAccentsIsAllowedAndSaysExactlyWhatItDid()
    {
        Hl7BuildResult result = Build(NameProfile("8859/1", "transliterate"), PolishName);

        Assert.True(result.IsSendable);
        Assert.Equal("Walesa", result.Message!["PID-5.1.1"]);

        Hl7MessageIssue warning = Assert.Single(result.Issues);
        Assert.Equal(ProfileIssueSeverity.Warning, warning.Severity);
        Assert.Contains(PolishName, warning.Message, StringComparison.Ordinal);
        Assert.Contains("Walesa", warning.Message, StringComparison.Ordinal);
        Assert.Contains("different values", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAsciiOnlyDestinationRefusesAPortugueseNameToo()
    {
        // Worth pinning: the policy is not about exotic scripts, it is about whatever the
        // destination cannot carry, and plenty of destinations are ASCII only.
        Hl7BuildResult result = Build(NameProfile("ASCII", "refuse"), PortugueseName);

        Assert.False(result.IsSendable);
    }

    [Fact]
    public void UnicodeCarriesEverythingAndRefusesNothing()
    {
        Hl7BuildResult result = Build(NameProfile("UNICODE UTF-8", "refuse"), PolishName);

        Assert.True(result.IsSendable);
        Assert.Equal(PolishName, result.Message!["PID-5.1.1"]);
    }

    // ---------------------------------------------------------------- validation

    private static Hl7Message MessageOf(string er7) => Hl7Message.Parse(er7);

    [Fact]
    public void ASegmentTheDestinationRefusesIsAnErrorEvenWhenTheRestIsFine()
    {
        Hl7Profile profile = Profile("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "segments": [ { "id": "PD1", "usage": "X" } ]
        }
        """);

        IReadOnlyList<Hl7MessageIssue> issues =
            Hl7MessageValidator.Validate(MessageOf("MSH|^~\\&|A\rPD1|1"), profile);

        Assert.Contains(issues, i => i.Location == "PD1" && i.Severity == ProfileIssueSeverity.Error);
    }

    [Fact]
    public void ARequiredSegmentThatIsMissingIsReportedOnce()
    {
        Hl7Profile profile = Profile("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "segments": [ { "id": "PID", "usage": "R", "min": 1, "max": 1 } ],
          "fields": [ { "path": "PID-3.1", "usage": "R" },
                      { "path": "PID-5.1.1", "usage": "R" } ]
        }
        """);

        IReadOnlyList<Hl7MessageIssue> issues =
            Hl7MessageValidator.Validate(MessageOf("MSH|^~\\&|A"), profile);

        // One complaint about the missing segment, not one per field inside it.
        Hl7MessageIssue issue = Assert.Single(issues);
        Assert.Equal("PID", issue.Location);
    }

    [Fact]
    public void TooManyOccurrencesOfASegmentIsAnError()
    {
        Hl7Profile profile = Profile("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "segments": [ { "id": "PID", "usage": "R", "min": 1, "max": 1 } ]
        }
        """);

        IReadOnlyList<Hl7MessageIssue> issues =
            Hl7MessageValidator.Validate(MessageOf("MSH|^~\\&|A\rPID|1\rPID|2"), profile);

        Assert.Contains(issues, i => i.Message.Contains("At most 1", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnboundedSegmentAcceptsAsManyAsArrive()
    {
        Hl7Profile profile = Profile("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "segments": [ { "id": "OBX", "usage": "O", "min": 0, "max": 0 } ]
        }
        """);

        Assert.Empty(Hl7MessageValidator.Validate(
            MessageOf("MSH|^~\\&|A\rOBX|1\rOBX|2\rOBX|3"), profile));
    }

    [Fact]
    public void AValueLongerThanTheDestinationAcceptsIsRefusedAndTheRemedyIsGiven()
    {
        // Never truncated here. Shortening a name without being asked is the silent
        // alteration this product exists to avoid.
        Hl7Profile profile = Profile("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "fields": [ { "path": "PID-5.1.1", "name": "Family name", "maxLength": 5 } ]
        }
        """);

        IReadOnlyList<Hl7MessageIssue> issues =
            Hl7MessageValidator.Validate(MessageOf("MSH|^~\\&|A\rPID|||||Nascimento"), profile);

        Hl7MessageIssue issue = Assert.Single(issues);
        Assert.Contains("accepts 5 characters", issue.Message, StringComparison.Ordinal);
        Assert.Contains("truncate transform", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueOutsideTheAllowedListIsRefusedAndTheListIsShown()
    {
        Hl7Profile profile = Profile("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "fields": [ { "path": "ORC-1", "name": "Order control",
                        "allowedValues": ["CN", "RE"] } ]
        }
        """);

        IReadOnlyList<Hl7MessageIssue> issues =
            Hl7MessageValidator.Validate(MessageOf("MSH|^~\\&|A\rORC|NW"), profile);

        Hl7MessageIssue issue = Assert.Single(issues);
        Assert.Contains("'NW' is not one of", issue.Message, StringComparison.Ordinal);
        Assert.Contains("'CN', 'RE'", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyValueIsAllowedWhenTheListAllowsOne()
    {
        // The specimen allows an empty administrative sex, which is a Type 2 style answer
        // and must not be confused with a value outside the list.
        Hl7Profile profile = Profile("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "fields": [ { "path": "PID-8", "allowedValues": ["M", "F", "O", ""] } ]
        }
        """);

        Assert.Empty(Hl7MessageValidator.Validate(MessageOf("MSH|^~\\&|A\rPID|||x"), profile));
    }

    [Fact]
    public void AMessageThatSatisfiesItsProfileRaisesNothingAtAll()
    {
        Hl7Profile profile = Profile("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "segments": [ { "id": "MSH", "usage": "R", "min": 1, "max": 1 },
                        { "id": "PID", "usage": "R", "min": 1, "max": 1 } ],
          "fields": [ { "path": "PID-3.1", "usage": "R", "maxLength": 20 } ]
        }
        """);

        Assert.Empty(Hl7MessageValidator.Validate(
            MessageOf("MSH|^~\\&|A\rPID|||1234567"), profile));
    }

    [Fact]
    public void TheValidatorReportsAndNeverRepairs()
    {
        // A preview is only worth showing if it is what will go on the wire.
        Hl7Profile profile = Profile("""
        {
          "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
          "fields": [ { "path": "PID-5.1.1", "maxLength": 3 } ]
        }
        """);

        Hl7Message message = MessageOf("MSH|^~\\&|A\rPID|||||Nascimento");

        Hl7MessageValidator.Validate(message, profile);

        Assert.Equal("Nascimento", message["PID-5.1.1"]);
    }
}
