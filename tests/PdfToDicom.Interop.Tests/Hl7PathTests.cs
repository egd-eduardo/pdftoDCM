using PdfToDicom.Interop.Hl7V2.Model;
using Xunit;

namespace PdfToDicom.Interop.Tests;

public sealed class Hl7PathTests
{
    [Fact]
    public void AFieldOnItsOwnDefaultsToTheFirstOfEverythingElse()
    {
        Hl7Path path = Hl7Path.Parse("PID-3");

        Assert.Equal("PID", path.Segment);
        Assert.Equal(1, path.SegmentOccurrence);
        Assert.Equal(3, path.Field);
        Assert.Equal(1, path.Repetition);
        Assert.Equal(1, path.Component);
        Assert.Equal(1, path.Subcomponent);
    }

    [Fact]
    public void ComponentsAndSubcomponentsAreReadAsAStatementWritesThem()
    {
        // PID-5.1.1 is how the supplied conformance statement addresses a surname.
        Hl7Path path = Hl7Path.Parse("PID-5.1.1");

        Assert.Equal(5, path.Field);
        Assert.Equal(1, path.Component);
        Assert.Equal(1, path.Subcomponent);

        Hl7Path deeper = Hl7Path.Parse("OBR-4.2");

        Assert.Equal(4, deeper.Field);
        Assert.Equal(2, deeper.Component);
        Assert.Equal(1, deeper.Subcomponent);
    }

    [Fact]
    public void FieldRepetitionIsWrittenInParentheses()
    {
        Hl7Path path = Hl7Path.Parse("PID-3(2).1");

        Assert.Equal(3, path.Field);
        Assert.Equal(2, path.Repetition);
        Assert.Equal(1, path.Component);
    }

    [Fact]
    public void SegmentOccurrenceIsWrittenInSquareBrackets()
    {
        Hl7Path path = Hl7Path.Parse("OBX[2]-5");

        Assert.Equal("OBX", path.Segment);
        Assert.Equal(2, path.SegmentOccurrence);
        Assert.Equal(5, path.Field);
    }

    [Fact]
    public void AZSegmentIsAnOrdinaryPath()
    {
        // ZDS-1 carries a Study Instance UID in the supplied statement. If this needed a
        // special case, a site specific segment would need code rather than configuration.
        Hl7Path path = Hl7Path.Parse("ZDS-1");

        Assert.Equal("ZDS", path.Segment);
        Assert.Equal(1, path.Field);
    }

    [Fact]
    public void ASegmentNameIsUpperCasedSoTwoSpellingsAddressOnePlace()
    {
        Assert.Equal("PID", Hl7Path.Parse("pid-5").Segment);
        Assert.Equal(Hl7Path.Parse("PID-5"), Hl7Path.Parse("pid-5"));
    }

    [Theory]
    [InlineData("PID-5.1.1", "PID-5")]
    [InlineData("PID-5.2.1", "PID-5.2")]
    [InlineData("PID-5.2.3", "PID-5.2.3")]
    [InlineData("PID-3(2)", "PID-3(2)")]
    [InlineData("OBX[2]-5", "OBX[2]-5")]
    public void RenderingDropsThePositionsThatMeanNothing(string input, string rendered)
    {
        // A mapping file is read by people holding a vendor document, so "PID-5" should not
        // come back as "PID-5.1.1".
        Assert.Equal(rendered, Hl7Path.Parse(input).ToString());
    }

    [Fact]
    public void ARenderedPathParsesBackToTheSamePlace()
    {
        foreach (string text in new[] { "PID-5.1.1", "PID-3(2).1", "OBX[2]-5.2.3", "ZDS-1" })
        {
            Hl7Path path = Hl7Path.Parse(text);

            Assert.Equal(path, Hl7Path.Parse(path.ToString()));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PID")]
    [InlineData("PID-")]
    [InlineData("PI-5")]
    [InlineData("PIDX-5")]
    [InlineData("PID-0")]
    [InlineData("PID-5.0")]
    [InlineData("PID-3(0)")]
    [InlineData("OBX[0]-5")]
    [InlineData("PID-5.1.1.1")]
    [InlineData("PID-five")]
    public void AnythingThatIsNotAPathIsRefused(string text)
    {
        Assert.False(Hl7Path.TryParse(text, out _));
        Assert.Throws<FormatException>(() => Hl7Path.Parse(text));
    }

    [Fact]
    public void TwoPathsToTheSamePlaceAreEqualAndHashAlike()
    {
        Hl7Path first = Hl7Path.Parse("OBX[2]-5.1.1");
        Hl7Path second = Hl7Path.Parse("OBX[2]-5");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, Hl7Path.Parse("OBX[3]-5"));
    }
}
