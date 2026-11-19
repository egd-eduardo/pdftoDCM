using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Models;

namespace PdfToDicom.Core.Abstractions;

/// <summary>
/// The outcome of a worklist query.
/// </summary>
/// <param name="IsSuccess">True when the server answered, even if it answered with nothing.</param>
/// <param name="Entries">Scheduled procedure steps returned, in scheduled order.</param>
/// <param name="Message">Operator facing summary, including the reason for a failure.</param>
/// <param name="WasTruncated">
/// True when the server had more to say than the configured maximum allowed, so the list
/// is incomplete. Worth showing: an operator who cannot find a patient needs to know the
/// list was cut rather than that the patient is absent.
/// </param>
public sealed record WorklistQueryResult(
    bool IsSuccess,
    IReadOnlyList<WorklistEntry> Entries,
    string Message,
    bool WasTruncated = false);

/// <summary>
/// A date range asked for on one query, rather than the one saved against the server.
/// </summary>
/// <param name="Range">Which range, including the one that sends no date key at all.</param>
/// <param name="From">The first date, used only when <paramref name="Range"/> is explicit.</param>
/// <param name="To">The last date, used only when <paramref name="Range"/> is explicit.</param>
public sealed record WorklistDateWindow(
    WorklistDateRange Range,
    DateTime? From = null,
    DateTime? To = null);

/// <summary>
/// Queries a Modality Worklist SCP for scheduled procedure steps.
/// </summary>
/// <remarks>
/// Typing a patient identifier by hand is the single likeliest way to file a document
/// against the wrong patient. Taking the identifiers from the worklist that scheduled the
/// examination removes that step entirely, which is why this exists.
/// </remarks>
public interface IWorklistService
{
    /// <summary>
    /// Confirms the worklist server is reachable and accepts an association.
    /// </summary>
    Task<(bool IsSuccess, string Message)> TestConnectionAsync(
        WorklistConfiguration configuration,
        string callingAeTitle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the worklist.
    /// </summary>
    /// <param name="configuration">Server and query settings.</param>
    /// <param name="callingAeTitle">The application's own AE Title, used when no override is set.</param>
    /// <param name="patientIdFilter">Optional Patient ID to narrow the query, with wildcards allowed.</param>
    /// <param name="patientNameFilter">Optional family name to narrow the query, with wildcards allowed.</param>
    /// <param name="dateRangeOverride">
    /// A date range for this query only, leaving the server's saved setting alone. The
    /// worklist window offers this because looking up an older examination is a thing an
    /// operator does occasionally, and the saved setting is behind the settings password.
    /// </param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<WorklistQueryResult> QueryAsync(
        WorklistConfiguration configuration,
        string callingAeTitle,
        string? patientIdFilter = null,
        string? patientNameFilter = null,
        WorklistDateWindow? dateRangeOverride = null,
        CancellationToken cancellationToken = default);
}
