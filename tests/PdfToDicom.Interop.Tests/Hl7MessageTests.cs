using PdfToDicom.Interop.Hl7V2.Model;
using Xunit;

namespace PdfToDicom.Interop.Tests;

public sealed class Hl7MessageTests
{
    [Fact]
    public void ReadingSomethingAbsentAnswersEmptyRatherThanThrowing()
    {
        // A mapping that asks for an optional field should not have to check first.
        Hl7Message message = Hl7Message.Parse("MSH|^~\\&|A");

        Assert.Equal(string.Empty, message["PID-5.1.1"]);
        Assert.Equal(string.Empty, message["ZZZ-99"]);
        Assert.Equal(string.Empty, message["MSH-40"]);
    }

    [Fact]
    public void WritingCreatesEverythingOnTheWay()
    {
        // This is what lets a profile build a message by assignment alone.
        var message = new Hl7Message();

        message["MSH-9.1"] = "ORU";
        message["MSH-9.2"] = "R01";
        message["PID-5.1.1"] = "Conceicao";
        message["PID-5.2"] = "Maria";

        Assert.Equal("ORU", message["MSH-9.1"]);
        Assert.Equal("Conceicao", message["PID-5.1.1"]);
        Assert.Equal("Maria", message["PID-5.2"]);
        Assert.Equal(new[] { "MSH", "PID" }, message.Segments.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void WritingToALaterSegmentOccurrenceAppendsRatherThanOverwriting()
    {
        // A profile driven builder adds repeating segments in order. Writing into the first
        // one instead would put an observation on the wrong line.
        var message = new Hl7Message();

        message["OBX-5"] = "line one";
        message["OBX[2]-5"] = "line two";
        message["OBX[3]-5"] = "line three";

        Assert.Equal(3, message.FindSegments("OBX").Count);
        Assert.Equal("line one", message["OBX-5"]);
        Assert.Equal("line two", message["OBX[2]-5"]);
        Assert.Equal("line three", message["OBX[3]-5"]);
    }

    [Fact]
    public void AskingForALaterOccurrenceCreatesEveryOneBeforeIt()
    {
        var message = new Hl7Message();

        message["OBX[3]-5"] = "third";

        Assert.Equal(3, message.FindSegments("OBX").Count);
        Assert.Equal(string.Empty, message["OBX-5"]);
        Assert.Equal("third", message["OBX[3]-5"]);
    }

    [Fact]
    public void SegmentsAreFoundByNameWithoutRegardToCase()
    {
        Hl7Message message = Hl7Message.Parse("MSH|^~\\&|A\rPID|1\rOBX|1\rOBX|2");

        Assert.NotNull(message.FindSegment("pid"));
        Assert.Equal(2, message.FindSegments("obx").Count);
        Assert.Null(message.FindSegment("OBX", 3));
        Assert.Null(message.FindSegment("NTE"));
    }

    [Fact]
    public void ASegmentNameIsAlwaysThreeCharacters()
    {
        Assert.Throws<ArgumentException>(() => new Hl7Segment("PI"));
        Assert.Throws<ArgumentException>(() => new Hl7Segment("PIDX"));
    }

    [Fact]
    public void PositionsAreCountedFromOneAndZeroIsRefused()
    {
        var segment = new Hl7Segment("PID");

        Assert.Throws<ArgumentOutOfRangeException>(() => segment.Field(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => segment.Field(1).Repetition(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => segment.Field(1).Repetition(1).Component(0));
    }

    [Fact]
    public void AnEmptyFieldReportsItselfEmptyWhateverItsDepth()
    {
        var segment = new Hl7Segment("PID");
        segment.Field(5).Repetition(1).Component(3).SetSubcomponent(2, string.Empty);

        Assert.True(segment.Field(5).IsEmpty);

        segment.Field(5).Repetition(1).Component(3).SetSubcomponent(2, "x");

        Assert.False(segment.Field(5).IsEmpty);
    }

    [Fact]
    public void AMessageBuiltByHandWritesTheSameTextAsOneThatWasParsed()
    {
        var built = new Hl7Message();
        built["MSH-3"] = "PDFTODICOM";
        built["MSH-9.1"] = "ORU";
        built["MSH-9.2"] = "R01";
        built["PID-3.1"] = "1234567";

        Hl7Message reparsed = Hl7Message.Parse(built.ToEr7());

        Assert.Equal(built.ToEr7(), reparsed.ToEr7());
        Assert.Equal("PDFTODICOM", reparsed["MSH-3"]);
        Assert.Equal("1234567", reparsed["PID-3.1"]);
    }
}
