using System.Text;
using PdfToDicom.Interop.Hl7V2.Encoding;
using Xunit;

namespace PdfToDicom.Interop.Tests;

public sealed class Hl7CharacterSetTests
{
    // A real Portuguese name, and the reason this whole type exists. It is representable in
    // 8859/1 and in UTF-8, and not in ASCII.
    private const string PortugueseName = "Conceição";

    // Polish. Representable in UTF-8 and in neither of the other two.
    private const string PolishName = "Wałęsa";

    [Theory]
    [InlineData("", "us-ascii")]
    [InlineData("ASCII", "us-ascii")]
    [InlineData("8859/1", "iso-8859-1")]
    [InlineData("UNICODE UTF-8", "utf-8")]
    [InlineData("  unicode utf-8  ", "utf-8")]
    public void TheTableValuesAStatementUsesResolveToAnEncoding(string declared, string expected)
    {
        Assert.True(Hl7CharacterSet.TryResolve(declared, out System.Text.Encoding? encoding));
        Assert.Equal(expected, encoding.WebName);
    }

    [Theory]
    [InlineData("8859/15")]
    [InlineData("JAS2020")]
    [InlineData("nonsense")]
    public void AnUnsupportedCharacterSetIsRefusedRatherThanApproximated(string declared)
    {
        // Approximating a character set is precisely how a name gets mangled in transit.
        Assert.False(Hl7CharacterSet.TryResolve(declared, out _));
        Assert.Throws<NotSupportedException>(() => Hl7CharacterSet.Resolve(declared));
    }

    [Fact]
    public void APortugueseNameSurvivesTheCharacterSetTheStatementNames()
    {
        System.Text.Encoding latin1 = Hl7CharacterSet.Resolve("8859/1");

        Assert.Null(Hl7CharacterSet.FindUnrepresentable(PortugueseName, latin1));
    }

    [Fact]
    public void ANameThatCannotBeRepresentedIsFoundBeforeAnythingIsSent()
    {
        System.Text.Encoding ascii = Hl7CharacterSet.Resolve("ASCII");
        System.Text.Encoding latin1 = Hl7CharacterSet.Resolve("8859/1");

        Assert.Equal('ç', Hl7CharacterSet.FindUnrepresentable(PortugueseName, ascii));
        Assert.Equal('ł', Hl7CharacterSet.FindUnrepresentable(PolishName, latin1));
        Assert.Null(Hl7CharacterSet.FindUnrepresentable(PolishName, Hl7CharacterSet.Resolve("UTF-8")));
    }

    [Fact]
    public void RefusingIsTheDefaultAndSaysWhichCharacterAndWhichSet()
    {
        System.Text.Encoding latin1 = Hl7CharacterSet.Resolve("8859/1");

        Hl7CharacterSetException error = Assert.Throws<Hl7CharacterSetException>(
            () => Hl7CharacterSet.Encode($"PID|||{PolishName}", latin1));

        Assert.Equal('ł', error.Character);
        Assert.Equal("iso-8859-1", error.CharacterSet);
        Assert.Contains("was not sent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TransliterationIsAvailableButNeverAutomatic()
    {
        System.Text.Encoding ascii = Hl7CharacterSet.Resolve("ASCII");

        // Not by default.
        Assert.Throws<Hl7CharacterSetException>(() => Hl7CharacterSet.Encode(PortugueseName, ascii));

        // Only when somebody chose it.
        byte[] encoded = Hl7CharacterSet.Encode(
            PortugueseName, ascii, UnrepresentableCharacterPolicy.Transliterate);

        Assert.Equal("Conceicao", System.Text.Encoding.ASCII.GetString(encoded));
    }

    [Fact]
    public void ACharacterWithNoUnaccentedFormBecomesVisiblyWrongRatherThanSubtlyWrong()
    {
        System.Text.Encoding ascii = Hl7CharacterSet.Resolve("ASCII");

        byte[] encoded = Hl7CharacterSet.Encode(
            "中文", ascii, UnrepresentableCharacterPolicy.Transliterate);

        Assert.Equal("??", System.Text.Encoding.ASCII.GetString(encoded));
    }

    [Fact]
    public void AMessageOfPlainTextEncodesIdenticallyInEverySupportedSet()
    {
        const string message = "MSH|^~\\&|PDFTODICOM|||";

        foreach (string declared in new[] { "ASCII", "8859/1", "UNICODE UTF-8" })
        {
            System.Text.Encoding encoding = Hl7CharacterSet.Resolve(declared);

            Assert.Equal(message, encoding.GetString(Hl7CharacterSet.Encode(message, encoding)));
        }
    }

    [Fact]
    public void Utf8IsWrittenWithoutAByteOrderMark()
    {
        // A byte order mark ahead of "MSH" stops a receiving system recognising the message.
        byte[] encoded = Hl7CharacterSet.Encode("MSH", Hl7CharacterSet.Resolve("UNICODE UTF-8"));

        Assert.Equal((byte)'M', encoded[0]);
    }

    [Fact]
    public void TransliterationLeavesTextThatNeedsNothingDoneToItAlone()
    {
        Assert.Equal("Smith", Hl7CharacterSet.Transliterate("Smith"));
        Assert.Equal(string.Empty, Hl7CharacterSet.Transliterate(null));
    }
}
