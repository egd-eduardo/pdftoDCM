using PdfToDicom.Interop.Hl7V2;
using PdfToDicom.Interop.Hl7V2.Encoding;
using Xunit;

namespace PdfToDicom.Interop.Tests;

public sealed class Hl7DelimiterTests
{
    [Fact]
    public void TheDefaultsAreTheOnesEverybodyUses()
    {
        Hl7Delimiters delimiters = Hl7Delimiters.Default;

        Assert.Equal('|', delimiters.Field);
        Assert.Equal("^~\\&", delimiters.EncodingCharacters);
    }

    [Fact]
    public void TwoDelimitersMayNotBeTheSameCharacter()
    {
        // Otherwise a message would parse into a structure nobody wrote, rather than fail.
        Assert.Throws<ArgumentException>(() => new Hl7Delimiters('|', '|', '~', '\\', '&'));
    }

    [Fact]
    public void ADelimiterMayNotBeAControlCharacter()
    {
        Assert.Throws<ArgumentException>(() => new Hl7Delimiters('|', '^', '\r', '\\', '&'));
    }

    [Fact]
    public void TheDelimitersAreReadFromTheOpeningOfTheMessage()
    {
        Hl7Delimiters delimiters =
            Hl7Delimiters.ReadFromMessageHeader("MSH|^~\\&|SENDER|FACILITY");

        Assert.Equal('|', delimiters.Field);
        Assert.Equal('^', delimiters.Component);
        Assert.Equal('~', delimiters.Repetition);
        Assert.Equal('\\', delimiters.Escape);
        Assert.Equal('&', delimiters.Subcomponent);
    }

    [Fact]
    public void UnusualButLegalDelimitersAreHonoured()
    {
        // Nothing in the codec may assume the usual five.
        Hl7Delimiters delimiters = Hl7Delimiters.ReadFromMessageHeader("MSH#@$%*#SENDER");

        Assert.Equal('#', delimiters.Field);
        Assert.Equal('@', delimiters.Component);
        Assert.Equal('$', delimiters.Repetition);
        Assert.Equal('%', delimiters.Escape);
        Assert.Equal('*', delimiters.Subcomponent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("PID|||x")]
    [InlineData("MSH|^")]
    public void AnythingThatIsNotAMessageHeaderIsRefused(string text)
    {
        Assert.Throws<Hl7ParseException>(() => Hl7Delimiters.ReadFromMessageHeader(text));
    }

    [Theory]
    [InlineData("a|b", "a\\F\\b")]
    [InlineData("a^b", "a\\S\\b")]
    [InlineData("a~b", "a\\R\\b")]
    [InlineData("a&b", "a\\T\\b")]
    [InlineData("a\\b", "a\\E\\b")]
    public void EveryDelimiterIsEscapedAndComesBackUnchanged(string raw, string escaped)
    {
        Hl7Delimiters delimiters = Hl7Delimiters.Default;

        Assert.Equal(escaped, delimiters.EscapeValue(raw));
        Assert.Equal(raw, delimiters.UnescapeValue(escaped));
    }

    [Fact]
    public void TheEscapeCharacterIsEscapedFirstSoItIsNotDoubled()
    {
        // Replacing "|" before "\" would rewrite the backslash the first replacement just
        // introduced, and the value would arrive corrupted.
        Hl7Delimiters delimiters = Hl7Delimiters.Default;

        Assert.Equal("\\E\\\\F\\", delimiters.EscapeValue("\\|"));
        Assert.Equal("\\|", delimiters.UnescapeValue("\\E\\\\F\\"));
    }

    [Fact]
    public void AnAddressWithAnAmpersandSurvivesTheRoundTrip()
    {
        Hl7Delimiters delimiters = Hl7Delimiters.Default;
        const string address = "Smith & Sons, 4 High Street";

        Assert.Equal(address, delimiters.UnescapeValue(delimiters.EscapeValue(address)));
    }

    [Theory]
    [InlineData("line one\\.br\\line two")]
    [InlineData("before\\X0D\\after")]
    [InlineData("\\Z99\\")]
    public void AnEscapeSequenceThisLibraryDoesNotKnowIsLeftExactlyAsFound(string value)
    {
        // Dropping a formatting command would lose part of a report while appearing to
        // succeed, which is the worst way for a parser to fail.
        Assert.Equal(value, Hl7Delimiters.Default.UnescapeValue(value));
    }

    [Fact]
    public void AnUnterminatedEscapeIsKeptRatherThanDiscarded()
    {
        Assert.Equal("text\\F", Hl7Delimiters.Default.UnescapeValue("text\\F"));
    }

    [Fact]
    public void AValueWithNothingToEscapeIsReturnedUnchanged()
    {
        const string plain = "Maria Conceicao";

        Assert.Equal(plain, Hl7Delimiters.Default.EscapeValue(plain));
        Assert.Equal(plain, Hl7Delimiters.Default.UnescapeValue(plain));
    }
}
