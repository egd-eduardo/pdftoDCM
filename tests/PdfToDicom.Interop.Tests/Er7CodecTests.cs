using PdfToDicom.Interop.Hl7V2;
using PdfToDicom.Interop.Hl7V2.Er7;
using PdfToDicom.Interop.Hl7V2.Model;
using Xunit;

namespace PdfToDicom.Interop.Tests;

public sealed class Er7CodecTests
{
    /// <summary>
    /// An ORU^R01 in the shape the supplied conformance statement gives on its example
    /// page, shortened but structurally identical: acknowledgement declared NE twice, a
    /// patient, an order and an observation.
    /// </summary>
    private const string ExampleOru =
        "MSH|^~\\&|||||19980814053924||ORU^R01|970814053924670|P|2.3|||NE|NE\r" +
        "PID|||6910828^Y^C8||Sample^Patient^H|||||||||773789090\r" +
        "OBR||||11490-0^^LN^^DISCHARGE NOTE\r" +
        "OBX||TX|11490-0^^LN||General Community Hospital";

    [Fact]
    public void TheExampleMessageParsesIntoTheSegmentsItShows()
    {
        Hl7Message message = Hl7Message.Parse(ExampleOru);

        Assert.Equal(4, message.Segments.Count);
        Assert.Equal(new[] { "MSH", "PID", "OBR", "OBX" },
            message.Segments.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void TheHeaderFieldsLandWhereTheStatementSaysTheyDo()
    {
        Hl7Message message = Hl7Message.Parse(ExampleOru);

        // If MSH were parsed with the ordinary rule, every one of these would be off by one.
        Assert.Equal("|", message["MSH-1"]);
        Assert.Equal("^~\\&", message["MSH-2"]);
        Assert.Equal("19980814053924", message["MSH-7"]);
        Assert.Equal("ORU", message["MSH-9.1"]);
        Assert.Equal("R01", message["MSH-9.2"]);
        Assert.Equal("970814053924670", message["MSH-10"]);
        Assert.Equal("P", message["MSH-11"]);
        Assert.Equal("2.3", message["MSH-12"]);
    }

    [Fact]
    public void TheAcknowledgementFieldsReadAsTheStatementDeclaresThem()
    {
        // NE in both is what makes "sent" the only state this interface can reach, so the
        // codec has to surface them accurately for a profile to act on.
        Hl7Message message = Hl7Message.Parse(ExampleOru);

        Assert.Equal("NE", message["MSH-15"]);
        Assert.Equal("NE", message["MSH-16"]);
    }

    [Fact]
    public void ComponentsAndSubcomponentsAreDivided()
    {
        Hl7Message message = Hl7Message.Parse(ExampleOru);

        // The statement puts the identifier list in PID-3 and the name in PID-5, so both
        // are pinned: a miscounted separator moves every field after it.
        Assert.Equal("6910828", message["PID-3.1"]);
        Assert.Equal("Sample", message["PID-5.1"]);
        Assert.Equal("Patient", message["PID-5.2"]);
        Assert.Equal("H", message["PID-5.3"]);
    }

    [Fact]
    public void AMessageSurvivesAParseAndAWriteUnchanged()
    {
        Assert.Equal(ExampleOru, Hl7Message.Parse(ExampleOru).ToEr7());
    }

    [Fact]
    public void WritingAlwaysUsesACarriageReturnWhateverTheInputUsed()
    {
        // Strict in what it writes: a receiving system is entitled to carriage returns.
        string withLineFeeds = ExampleOru.Replace("\r", "\n");
        string withBoth = ExampleOru.Replace("\r", "\r\n");

        Assert.Equal(ExampleOru, Hl7Message.Parse(withLineFeeds).ToEr7());
        Assert.Equal(ExampleOru, Hl7Message.Parse(withBoth).ToEr7());
    }

    [Fact]
    public void LeadingAndBlankLinesAreToleratedRatherThanRefused()
    {
        // Tolerant in what it reads: messages arrive pasted out of email and log files.
        string messy = "\r\n" + ExampleOru.Replace("\r", "\r\r\n");

        Assert.Equal(ExampleOru, Hl7Message.Parse(messy).ToEr7());
    }

    [Fact]
    public void RepetitionsAreKeptApartFromComponents()
    {
        // A second repetition of PID-3 is a second identifier; a second component is part
        // of the first one. Confusing them would merge two patient identifiers into one.
        Hl7Message message = Hl7Message.Parse("MSH|^~\\&|A\rPID|||111^^^HOSP~222^^^CLINIC");

        Hl7Field field = message.FindSegment("PID")!.Field(3);

        Assert.Equal(2, field.Repetitions.Count);
        Assert.Equal("111", message["PID-3(1).1"]);
        Assert.Equal("HOSP", message["PID-3(1).4"]);
        Assert.Equal("222", message["PID-3(2).1"]);
        Assert.Equal("CLINIC", message["PID-3(2).4"]);
    }

    [Fact]
    public void SubcomponentsAreDividedOnTheAmpersand()
    {
        Hl7Message message = Hl7Message.Parse("MSH|^~\\&|A\rOBX|||RXTHORAX&GDT");

        Assert.Equal("RXTHORAX", message["OBX-3.1.1"]);
        Assert.Equal("GDT", message["OBX-3.1.2"]);
    }

    [Fact]
    public void AValueHoldingADelimiterSurvivesBeingWrittenAndReadBack()
    {
        var message = new Hl7Message();
        message.AddSegment("MSH").Field(9).Value = "ORU";
        message["PID-11.1"] = "Smith & Sons | 4 High Street";

        string wire = message.ToEr7();

        // On the wire it must be escaped, or it would split the field.
        Assert.DoesNotContain("Sons & 4", wire);
        Assert.Contains("\\T\\", wire);
        Assert.Contains("\\F\\", wire);

        Assert.Equal("Smith & Sons | 4 High Street", Hl7Message.Parse(wire)["PID-11.1"]);
    }

    [Fact]
    public void TheEncodingCharactersAreNeverEscapedOnTheWayOut()
    {
        // Escaping MSH-2 would declare nothing, and the receiver could not read the rest.
        string wire = Hl7Message.Parse(ExampleOru).ToEr7();

        Assert.StartsWith("MSH|^~\\&|", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void AZSegmentIsCarriedLikeAnyOther()
    {
        // The supplied statement puts a Study Instance UID in ZDS-1. Nothing in the codec
        // knows the segment exists, which is exactly why a site can configure one.
        const string uid = "1.2.826.0.1.3680043.8.498.12345678901234567890";
        Hl7Message message = Hl7Message.Parse($"MSH|^~\\&|A\rZDS|{uid}");

        Assert.Equal(uid, message["ZDS-1"]);
        Assert.Contains($"ZDS|{uid}", message.ToEr7(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnusualDelimitersAreHonouredThroughout()
    {
        Hl7Message message = Hl7Message.Parse("MSH#@$%*#SENDER#FACILITY\rPID###Smith@John");

        Assert.Equal("SENDER", message["MSH-3"]);
        Assert.Equal("Smith", message["PID-3.1"]);
        Assert.Equal("John", message["PID-3.2"]);
        Assert.Equal("MSH#@$%*#SENDER#FACILITY\rPID###Smith@John", message.ToEr7());
    }

    [Fact]
    public void AnEmptyFieldAndAnExplicitNullAreNotTheSameThing()
    {
        // An empty field leaves a value alone at the far end; two quotes erase it.
        Hl7Message message = Hl7Message.Parse("MSH|^~\\&|A\rPID||\"\"|");

        Assert.True(message.FindSegment("PID")!.Field(2).IsExplicitNull);
        Assert.False(message.FindSegment("PID")!.Field(3).IsExplicitNull);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PID|||x")]
    [InlineData("MS")]
    public void TextThatIsNotAMessageIsRefusedRatherThanGuessedAt(string text)
    {
        Assert.Throws<Hl7ParseException>(() => Hl7Message.Parse(text));
    }

    [Fact]
    public void ASegmentNameThatIsNotOneIsRefused()
    {
        Assert.Throws<Hl7ParseException>(() => Hl7Message.Parse("MSH|^~\\&|A\r!!!|x"));
    }

    [Fact]
    public void TheSegmentTerminatorIsACarriageReturnAndStaysOne()
    {
        Assert.Equal('\r', Er7Codec.SegmentTerminator);
    }
}
