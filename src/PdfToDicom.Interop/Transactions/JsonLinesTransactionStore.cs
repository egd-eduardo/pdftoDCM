using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfToDicom.Interop.Transactions;

/// <summary>
/// Writes attempts to one file per day, one JSON object per line.
/// </summary>
/// <remarks>
/// <para>
/// A database was the obvious alternative and lost on three counts. This product installs
/// without administrator rights and writes nothing outside the user's application data
/// folder, which a database engine would change. A file of one object per line can be read
/// by anybody with a text editor at three in the morning, which is when somebody will want
/// to read it. And an append only file cannot be left half updated by a power cut: the
/// worst case is a truncated final line, which is handled below.
/// </para>
/// <para>
/// One file per day matches how the application already rotates its logs, so a site that
/// has a retention policy for one has it for the other.
/// </para>
/// </remarks>
public sealed class JsonLinesTransactionStore : ITransactionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = false
    };

    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly string _folder;
    private readonly int _lookBackDays;

    /// <summary>Creates a store beneath the supplied folder.</summary>
    /// <param name="folder">Where the daily files live. Created if it does not exist.</param>
    /// <param name="lookBackDays">
    /// How far back a duplicate is looked for. Seven days by default: long enough to catch
    /// the same report being sent again after a weekend, short enough that the search stays
    /// a handful of files.
    /// </param>
    public JsonLinesTransactionStore(string folder, int lookBackDays = 7)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentOutOfRangeException.ThrowIfLessThan(lookBackDays, 1);

        _folder = folder;
        _lookBackDays = lookBackDays;

        Directory.CreateDirectory(_folder);
    }

    /// <summary>How many lines could not be read, across every read so far.</summary>
    /// <remarks>
    /// Surfaced rather than hidden. A line that will not parse is skipped so that one bad
    /// record cannot make a day unreadable, but a count that climbs means something is
    /// writing rubbish and somebody should know.
    /// </remarks>
    public int UnreadableLines { get; private set; }

    /// <inheritdoc />
    public async Task RecordAsync(
        IntegrationTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        string line = JsonSerializer.Serialize(transaction, SerializerOptions);
        string path = PathFor(DateOnly.FromDateTime(transaction.AttemptedUtc.UtcDateTime));

        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Opened and closed per record rather than held open. A file handle kept across
            // a day boundary writes into yesterday, and one kept while the application is
            // idle is one more thing that can be holding a lock when a backup runs.
            await using var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, useAsync: true);

            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IntegrationTransaction>> ReadDayAsync(
        DateOnly day, CancellationToken cancellationToken = default)
    {
        string path = PathFor(day);

        if (!File.Exists(path))
        {
            return [];
        }

        var transactions = new List<IntegrationTransaction>();

        foreach (string line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                IntegrationTransaction? transaction =
                    JsonSerializer.Deserialize<IntegrationTransaction>(line, SerializerOptions);

                if (transaction is not null)
                {
                    transactions.Add(transaction);
                }
            }
            catch (JsonException)
            {
                // A truncated final line after a power cut, or a record from a future
                // version with a field this one cannot make sense of. Skipped rather than
                // fatal: one bad line must not cost the whole day.
                UnreadableLines++;
            }
        }

        return transactions;
    }

    /// <inheritdoc />
    public async Task<IntegrationTransaction?> FindEarlierDeliveryAsync(
        string contentHash, string destinationName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationName);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        for (int back = 0; back < _lookBackDays; back++)
        {
            IReadOnlyList<IntegrationTransaction> day =
                await ReadDayAsync(today.AddDays(-back), cancellationToken).ConfigureAwait(false);

            // Latest first within the day, so the answer is the most recent delivery.
            for (int index = day.Count - 1; index >= 0; index--)
            {
                IntegrationTransaction candidate = day[index];

                if (candidate.ContentHash == contentHash &&
                    string.Equals(candidate.DestinationName, destinationName, StringComparison.Ordinal) &&
                    candidate.DeliveryState is "Sent" or "Acknowledged")
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private string PathFor(DateOnly day) =>
        Path.Combine(_folder, $"transactions-{day:yyyy-MM-dd}.jsonl");
}
