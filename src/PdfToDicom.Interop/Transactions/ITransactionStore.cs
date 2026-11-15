namespace PdfToDicom.Interop.Transactions;

/// <summary>
/// Where attempts are written down and looked up again.
/// </summary>
public interface ITransactionStore
{
    /// <summary>Writes down one attempt.</summary>
    Task RecordAsync(IntegrationTransaction transaction, CancellationToken cancellationToken = default);

    /// <summary>Every attempt recorded on a given day, in the order they were made.</summary>
    Task<IReadOnlyList<IntegrationTransaction>> ReadDayAsync(
        DateOnly day, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds an earlier attempt that already delivered the same content to the same
    /// destination, or null when there is none.
    /// </summary>
    /// <remarks>
    /// The question duplicate protection actually needs answering: has this exact document
    /// already reached this exact destination? An attempt that failed does not count, since
    /// the whole point of a retry is to send it again.
    /// </remarks>
    Task<IntegrationTransaction?> FindEarlierDeliveryAsync(
        string contentHash, string destinationName, CancellationToken cancellationToken = default);
}
