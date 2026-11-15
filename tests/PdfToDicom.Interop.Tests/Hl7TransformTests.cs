using PdfToDicom.Interop.Hl7V2.Profiles;
using Xunit;

namespace PdfToDicom.Interop.Tests;

public sealed class Hl7TransformTests
{
    [Theory]
    [InlineData("trim", "  Silva  ", "Silva")]
    [InlineData("upper", "silva", "SILVA")]
    [InlineData("lower", "SILVA", "silva")]
    [InlineData("digitsOnly", "ACC-123/45", "12345")]
    [InlineData("truncate:5", "Conceicao", "Conce")]
    [InlineData("truncate:50", "Silva", "Silva")]
    [InlineData("padLeft:10", "77", "0000000077")]
    [InlineData("padLeft:4:x", "7", "xxx7")]
    [InlineData("padRight:5:.", "ab", "ab...")]
    [InlineData("substring:0:4", "20260115", "2026")]
    [InlineData("substring:4", "20260115", "0115")]
    [InlineData("substring:99", "short", "")]
    public void EachTransformDoesOneThing(string specification, string input, string expected)
    {
        Assert.Equal(expected, Hl7Transforms.Apply(specification, input));
    }

    [Fact]
    public void TruncationShortensButNeverLengthens()
    {
        // Padding a short value would invent content the receiver would then store.
        Assert.Equal("ab", Hl7Transforms.Apply("truncate:10", "ab"));
    }

    [Fact]
    public void TransformsChainInTheOrderTheyAreWritten()
    {
        Assert.Equal("CONCE", Hl7Transforms.Apply("trim;truncate:5;upper", "  Conceicao  "));

        // And the order matters, which is why it is the author's to choose.
        Assert.Equal("CONCEICAO", Hl7Transforms.Apply("upper;truncate:9", "Conceicao"));
    }

    [Theory]
    [InlineData("1970-05-21", "19700521")]
    [InlineData("1970-05-21T14:30:00", "19700521")]
    [InlineData("19700521", "19700521")]
    [InlineData("19700521143000", "19700521")]
    public void ADateBecomesTheEightDigitsHl7Wants(string input, string expected)
    {
        Assert.Equal(expected, Hl7Transforms.Apply("hl7Date", input));
    }

    [Fact]
    public void ADateAndTimeKeepsItsTime()
    {
        Assert.Equal("19700521143000", Hl7Transforms.Apply("hl7DateTime", "1970-05-21T14:30:00"));
        Assert.Equal("143000", Hl7Transforms.Apply("hl7Time", "1970-05-21T14:30:00"));
    }

    [Fact]
    public void SomethingThatIsNotADateIsLeftAloneRatherThanInvented()
    {
        // A plausible substitute would put a wrong date of birth in a record and nobody
        // would ever know. Leaving it alone makes the validator refuse the message, which
        // puts a person in front of the problem.
        Assert.Equal("not a date", Hl7Transforms.Apply("hl7Date", "not a date"));
        Assert.Equal(string.Empty, Hl7Transforms.Apply("hl7Date", ""));
    }

    [Fact]
    public void LineBreaksBecomeTheFormattingCommandForOne()
    {
        // A report keeps its shape inside one field.
        Assert.Equal("one\\.br\\two\\.br\\three",
            Hl7Transforms.Apply("escapeNewlines", "one\r\ntwo\nthree"));

        Assert.Equal("one\\.br\\two", Hl7Transforms.Apply("escapeNewlines", "one\rtwo"));
    }

    [Fact]
    public void Base64IsAvailableForAnEncapsulatedValue()
    {
        Assert.Equal("UmVwb3J0", Hl7Transforms.Apply("base64", "Report"));
    }

    [Fact]
    public void NoTransformMeansTheValueIsUntouched()
    {
        Assert.Equal("Silva", Hl7Transforms.Apply("", "Silva"));
        Assert.Equal("Silva", Hl7Transforms.Apply(null, "Silva"));
        Assert.Equal(string.Empty, Hl7Transforms.Apply("upper", null));
    }

    [Fact]
    public void ATransformThisBuildDoesNotKnowIsRefusedAndTheKnownOnesAreListed()
    {
        // The whole argument for a closed set: what runs is what somebody wrote and tested.
        Assert.False(Hl7Transforms.TryValidate("invokeShell:rm", out string error));
        Assert.Contains("is not a transform this build knows", error, StringComparison.Ordinal);
        Assert.Contains("truncate", error, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => Hl7Transforms.Apply("invokeShell:rm", "x"));
    }

    [Theory]
    [InlineData("truncate")]
    [InlineData("truncate:0")]
    [InlineData("truncate:none")]
    [InlineData("padLeft:5:toolong")]
    [InlineData("substring:-1")]
    [InlineData("substring:0:-2")]
    public void ATransformWithArgumentsThatMakeNoSenseIsRefused(string specification)
    {
        Assert.False(Hl7Transforms.TryValidate(specification, out _));
    }

    [Fact]
    public void AChainIsRefusedWhenAnyStepIsWrong()
    {
        Assert.False(Hl7Transforms.TryValidate("trim;nonsense;upper", out _));
        Assert.True(Hl7Transforms.TryValidate("trim;truncate:3;upper", out _));
    }

    [Fact]
    public void TheListOfNamesIsTheListTheDocumentationPromises()
    {
        Assert.Contains("hl7Date", Hl7Transforms.Names);
        Assert.Contains("escapeNewlines", Hl7Transforms.Names);
        Assert.DoesNotContain("eval", Hl7Transforms.Names);
    }
}
