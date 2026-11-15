using PdfToDicom.Interop.Hl7V2;
using PdfToDicom.Interop.Hl7V2.Acknowledgement;
using Xunit;

namespace PdfToDicom.Interop.Tests;

public sealed class Hl7AcknowledgementTests
{
    private const string Header = "MSH|^~\\&|ARCHIVE|SITE|PDFTODICOM||20261115093000||ACK|9|P|2.4\r";

    private static Hl7Acknowledgement Read(string body) =>
        Hl7AcknowledgementReader.Read(Header + body);

    [Theory]
    [InlineData("AA", AcknowledgementKind.ApplicationAccept, true)]
    [InlineData("AE", AcknowledgementKind.ApplicationError, false)]
    [InlineData("AR", AcknowledgementKind.ApplicationReject, false)]
    [InlineData("CA", AcknowledgementKind.CommitAccept, true)]
    [InlineData("CE", AcknowledgementKind.CommitError, false)]
    [InlineData("CR", AcknowledgementKind.CommitReject, false)]
    public void EachCodeInTheTableIsUnderstood(string code, AcknowledgementKind kind, bool positive)
    {
        Hl7Acknowledgement ack = Read($"MSA|{code}|CTRL-1");

        Assert.Equal(code, ack.Code);
        Assert.Equal(kind, ack.Kind);
        Assert.Equal(positive, ack.IsPositive);
    }

    [Fact]
    public void ACodeThisBuildDoesNotKnowIsNotTreatedAsAYes()
    {
        // Guessing that an unrecognised answer meant yes is the one reading that could put
        // a report nowhere while the operator is told it arrived.
        Hl7Acknowledgement ack = Read("MSA|ZZ|CTRL-1");

        Assert.Equal(AcknowledgementKind.Unknown, ack.Kind);
        Assert.False(ack.IsPositive);
        Assert.Contains("does not recognise", ack.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AReplyWithNoMsaIsReportedRatherThanRefused()
    {
        // A receiver answering oddly has still answered, and the operator is better served
        // by being told what arrived. Nothing about that tolerance makes it a success.
        Hl7Acknowledgement ack = Read("ZZZ|something");

        Assert.Equal(AcknowledgementKind.Unknown, ack.Kind);
        Assert.False(ack.IsPositive);
        Assert.Equal(string.Empty, ack.Code);
    }

    [Fact]
    public void TheControlIdentifierBeingAnsweredIsRead()
    {
        Hl7Acknowledgement ack = Read("MSA|AA|P2D202611150930000001");

        Assert.Equal("P2D202611150930000001", ack.AcknowledgedControlId);
        Assert.True(ack.Answers("P2D202611150930000001"));
    }

    [Fact]
    public void AnAcknowledgementAboutADifferentMessageDoesNotAnswerThisOne()
    {
        // Usually two crossed conversations, which is worth finding out about immediately
        // rather than inferring later from a report nobody can find.
        Hl7Acknowledgement ack = Read("MSA|AA|SOMEONE-ELSE");

        Assert.False(ack.Answers("OURS"));
    }

    [Fact]
    public void AnEmptyControlIdentifierIsAcceptedBecauseSomeInterfacesLeaveItEmpty()
    {
        // Refusing those would break a working integration over a field nobody reads.
        Assert.True(Read("MSA|AA").Answers("OURS"));
    }

    [Fact]
    public void TheTextIsKeptBecauseItIsOftenTheOnlyExplanation()
    {
        Hl7Acknowledgement ack = Read("MSA|AE|CTRL-1|Unknown patient identifier");

        Assert.Equal("Unknown patient identifier", ack.Text);
        Assert.Contains("Unknown patient identifier", ack.Describe(), StringComparison.Ordinal);
        Assert.Contains("could not be processed", ack.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnErrorSegmentInTheOlderShapeIsRead()
    {
        // Through 2.3.1 the text lives in ERR-1.
        Hl7Acknowledgement ack = Read("MSA|AE|CTRL-1\rERR|PID^1^3^101^Required field missing");

        Assert.Contains(ack.Errors, e => e.Contains("Required field missing", StringComparison.Ordinal));
    }

    [Fact]
    public void AnErrorSegmentInTheNewerShapeIsReadToo()
    {
        // From 2.5 the code is in ERR-3 and the sentence for a person in ERR-8. Both are
        // read rather than branching on the version, which a receiver reporting an error
        // may well have got wrong.
        Hl7Acknowledgement ack = Read(
            "MSA|AE|CTRL-1\rERR|||207^Application error^HL70357|E|||disk quota exceeded|The archive is full");

        Assert.Contains(ack.Errors, e => e.Contains("Application error", StringComparison.Ordinal));
        Assert.Contains(ack.Errors, e => e.Contains("disk quota exceeded", StringComparison.Ordinal));
        Assert.Contains(ack.Errors, e => e.Contains("The archive is full", StringComparison.Ordinal));
    }

    [Fact]
    public void SeveralErrorSegmentsAreAllKept()
    {
        Hl7Acknowledgement ack = Read(
            "MSA|AR|CTRL-1\rERR|first problem\rERR|second problem");

        Assert.Contains(ack.Errors, e => e.Contains("first problem", StringComparison.Ordinal));
        Assert.Contains(ack.Errors, e => e.Contains("second problem", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSameTextTwiceIsNotRepeated()
    {
        Hl7Acknowledgement ack = Read("MSA|AE|CTRL-1\rERR|same|x|same");

        Assert.Single(ack.Errors);
    }

    [Fact]
    public void AReplyThatIsNotAMessageAtAllIsRefused()
    {
        Assert.Throws<Hl7ParseException>(() => Hl7AcknowledgementReader.Read("not a message"));
    }

    [Fact]
    public void AnAcceptanceWithNothingToSaySaysSoCleanly()
    {
        Assert.Equal("The message accepted.", Read("MSA|AA|CTRL-1").Describe());
    }

    [Fact]
    public void ACommitAcceptIsPositiveAndSaysItIsNotYetProcessed()
    {
        // An engine may answer before the application behind it has seen anything, so a
        // commit accept says nothing about whether a record now holds the report.
        Hl7Acknowledgement ack = Read("MSA|CA|CTRL-1");

        Assert.True(ack.IsPositive);
        Assert.Contains("not yet processed", ack.Describe(), StringComparison.Ordinal);
    }
}
