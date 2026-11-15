using System.Text;
using PdfToDicom.Interop.Transactions;
using Xunit;

namespace PdfToDicom.Interop.Tests;

public sealed class TransactionStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "pdftodicom-transactions-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private JsonLinesTransactionStore Store(int lookBackDays = 7) =>
        new(_folder, lookBackDays);

    private static IntegrationTransaction Attempt(
        string controlId = "CTRL-1",
        string destination = "Report archive",
        string state = "Acknowledged",
        string hash = "abc123",
        DateTimeOffset? at = null,
        int attempt = 1) =>
        new()
        {
            TransactionId = Guid.NewGuid().ToString("N"),
            MessageControlId = controlId,
            DestinationName = destination,
            MessageType = "ORU^R01",
            AttemptedUtc = at ?? DateTimeOffset.UtcNow,
            AttemptNumber = attempt,
            DeliveryState = state,
            ContentHash = hash
        };

    [Fact]
    public async Task AnAttemptIsWrittenDownAndReadBack()
    {
        JsonLinesTransactionStore store = Store();
        IntegrationTransaction attempt = Attempt();

        await store.RecordAsync(attempt);

        IntegrationTransaction stored = Assert.Single(
            await store.ReadDayAsync(DateOnly.FromDateTime(DateTime.UtcNow)));

        Assert.Equal(attempt.TransactionId, stored.TransactionId);
        Assert.Equal("CTRL-1", stored.MessageControlId);
        Assert.Equal("ORU^R01", stored.MessageType);
        Assert.Equal("Acknowledged", stored.DeliveryState);
    }

    [Fact]
    public async Task EveryFieldSurvivesTheRoundTrip()
    {
        JsonLinesTransactionStore store = Store();

        var full = Attempt() with
        {
            ProfileId = "example-oru-r01",
            ProfileRevision = 3,
            AcknowledgementCode = "AE",
            AcknowledgementText = "Unknown patient identifier",
            ErrorDetail = "MSA-3 said so",
            RelatedSopInstanceUid = "1.2.826.0.1.3680043.8.498.1",
            ElapsedMilliseconds = 412.5,
            AttemptNumber = 2
        };

        await store.RecordAsync(full);

        IntegrationTransaction stored = Assert.Single(
            await store.ReadDayAsync(DateOnly.FromDateTime(DateTime.UtcNow)));

        Assert.Equal("example-oru-r01", stored.ProfileId);
        Assert.Equal(3, stored.ProfileRevision);
        Assert.Equal("AE", stored.AcknowledgementCode);
        Assert.Equal("Unknown patient identifier", stored.AcknowledgementText);
        Assert.Equal("MSA-3 said so", stored.ErrorDetail);
        Assert.Equal("1.2.826.0.1.3680043.8.498.1", stored.RelatedSopInstanceUid);
        Assert.Equal(412.5, stored.ElapsedMilliseconds);
        Assert.Equal(2, stored.AttemptNumber);
    }

    [Fact]
    public async Task TheReportItselfIsNotKeptUnlessItWasAskedFor()
    {
        // The body of an ORU is the patient's name, identifier and clinical text, in a file
        // somebody may later attach to a support request.
        JsonLinesTransactionStore store = Store();

        await store.RecordAsync(Attempt());

        IntegrationTransaction stored = Assert.Single(
            await store.ReadDayAsync(DateOnly.FromDateTime(DateTime.UtcNow)));

        Assert.Equal(string.Empty, stored.MessageBody);

        // What matters is that the clinical text is not on disk, not whether the field name
        // appears. An empty field is harmless; a patient's name in a file that gets attached
        // to a support request is not.
        string onDisk = await File.ReadAllTextAsync(
            Directory.GetFiles(_folder, "*.jsonl").Single());

        Assert.DoesNotContain("Conceicao", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("OBX", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReportIsKeptOnlyWhenTheRecordCarriesOne()
    {
        // Commissioning an interface is far easier with the message to hand, so the
        // capability exists. It is something a site turns on, not something that happens.
        JsonLinesTransactionStore store = Store();

        await store.RecordAsync(Attempt() with
        {
            MessageBody = "MSH|^~\\&|A\rPID|||1||Conceicao^Maria\rOBX|1|TX|||Report text"
        });

        IntegrationTransaction stored = Assert.Single(
            await store.ReadDayAsync(DateOnly.FromDateTime(DateTime.UtcNow)));

        Assert.Contains("Conceicao", stored.MessageBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttemptsAreKeptInTheOrderTheyWereMade()
    {
        JsonLinesTransactionStore store = Store();

        await store.RecordAsync(Attempt(controlId: "one"));
        await store.RecordAsync(Attempt(controlId: "two"));
        await store.RecordAsync(Attempt(controlId: "three"));

        IReadOnlyList<IntegrationTransaction> day =
            await store.ReadDayAsync(DateOnly.FromDateTime(DateTime.UtcNow));

        Assert.Equal(["one", "two", "three"], day.Select(t => t.MessageControlId));
    }

    [Fact]
    public async Task AttemptsAreFiledUnderTheDayTheyWereMade()
    {
        JsonLinesTransactionStore store = Store();
        DateTimeOffset yesterday = DateTimeOffset.UtcNow.AddDays(-1);

        await store.RecordAsync(Attempt(controlId: "old", at: yesterday));
        await store.RecordAsync(Attempt(controlId: "new"));

        Assert.Equal("old", Assert.Single(
            await store.ReadDayAsync(DateOnly.FromDateTime(yesterday.UtcDateTime))).MessageControlId);

        Assert.Equal("new", Assert.Single(
            await store.ReadDayAsync(DateOnly.FromDateTime(DateTime.UtcNow))).MessageControlId);
    }

    [Fact]
    public async Task ADayWithNothingInItReadsAsEmptyRatherThanFailing()
    {
        Assert.Empty(await Store().ReadDayAsync(new DateOnly(2001, 1, 1)));
    }

    [Fact]
    public async Task ADocumentAlreadyDeliveredToADestinationIsRecognised()
    {
        JsonLinesTransactionStore store = Store();

        await store.RecordAsync(Attempt(hash: "deadbeef", destination: "Archive"));

        IntegrationTransaction? found =
            await store.FindEarlierDeliveryAsync("deadbeef", "Archive");

        Assert.NotNull(found);
        Assert.Equal("Archive", found.DestinationName);
    }

    [Fact]
    public async Task TheSameDocumentToADifferentDestinationIsNotADuplicate()
    {
        // One report legitimately goes to several systems.
        JsonLinesTransactionStore store = Store();

        await store.RecordAsync(Attempt(hash: "deadbeef", destination: "Archive"));

        Assert.Null(await store.FindEarlierDeliveryAsync("deadbeef", "Record system"));
    }

    [Fact]
    public async Task AFailedAttemptIsNotADeliveryAndMustNotBlockARetry()
    {
        // The entire point of a retry is to send the thing again.
        JsonLinesTransactionStore store = Store();

        await store.RecordAsync(Attempt(hash: "deadbeef", state: "Failed"));

        Assert.Null(await store.FindEarlierDeliveryAsync("deadbeef", "Report archive"));
    }

    [Fact]
    public async Task SentCountsAsDeliveredEvenWithoutAnAcknowledgement()
    {
        // A destination that acknowledges nothing still received the document, and sending
        // it again would put a second copy in the record.
        JsonLinesTransactionStore store = Store();

        await store.RecordAsync(Attempt(hash: "deadbeef", state: "Sent"));

        Assert.NotNull(await store.FindEarlierDeliveryAsync("deadbeef", "Report archive"));
    }

    [Fact]
    public async Task ADeliveryOnAnEarlierDayIsStillFound()
    {
        JsonLinesTransactionStore store = Store(lookBackDays: 7);

        await store.RecordAsync(
            Attempt(hash: "deadbeef", at: DateTimeOffset.UtcNow.AddDays(-3)));

        Assert.NotNull(await store.FindEarlierDeliveryAsync("deadbeef", "Report archive"));
    }

    [Fact]
    public async Task ADeliveryOlderThanTheWindowIsNotFound()
    {
        JsonLinesTransactionStore store = Store(lookBackDays: 2);

        await store.RecordAsync(
            Attempt(hash: "deadbeef", at: DateTimeOffset.UtcNow.AddDays(-5)));

        Assert.Null(await store.FindEarlierDeliveryAsync("deadbeef", "Report archive"));
    }

    [Fact]
    public async Task ATruncatedLineCostsThatLineAndNotTheWholeDay()
    {
        // What a power cut leaves behind. A record of transmissions that cannot be read is
        // worse than none, because somebody reads it exactly when something has gone wrong.
        JsonLinesTransactionStore store = Store();

        await store.RecordAsync(Attempt(controlId: "first"));
        await store.RecordAsync(Attempt(controlId: "second"));

        string path = Directory.GetFiles(_folder, "*.jsonl").Single();
        await File.AppendAllTextAsync(path, "{\"TransactionId\":\"hal");

        IReadOnlyList<IntegrationTransaction> day =
            await store.ReadDayAsync(DateOnly.FromDateTime(DateTime.UtcNow));

        Assert.Equal(["first", "second"], day.Select(t => t.MessageControlId));
        Assert.Equal(1, store.UnreadableLines);
    }

    [Fact]
    public async Task WritingFromManyThreadsAtOnceLosesNothing()
    {
        JsonLinesTransactionStore store = Store();

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            store.RecordAsync(Attempt(controlId: $"ctrl-{i}"))));

        IReadOnlyList<IntegrationTransaction> day =
            await store.ReadDayAsync(DateOnly.FromDateTime(DateTime.UtcNow));

        Assert.Equal(50, day.Count);
        Assert.Equal(50, day.Select(t => t.MessageControlId).Distinct().Count());
    }

    [Fact]
    public void TheSameBytesHashTheSameAndDifferentBytesDoNot()
    {
        byte[] message = Encoding.ASCII.GetBytes("MSH|^~\\&|A");

        Assert.Equal(ContentHash.Of(message), ContentHash.Of(Encoding.ASCII.GetBytes("MSH|^~\\&|A")));
        Assert.NotEqual(ContentHash.Of(message), ContentHash.Of(Encoding.ASCII.GetBytes("MSH|^~\\&|B")));
        Assert.Equal(64, ContentHash.Of(message).Length);
    }

    [Fact]
    public void AStoreNeedsSomewhereToWrite()
    {
        Assert.ThrowsAny<ArgumentException>(() => new JsonLinesTransactionStore(" "));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new JsonLinesTransactionStore(_folder, lookBackDays: 0));
    }
}
