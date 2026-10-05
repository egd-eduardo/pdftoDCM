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
    Task<WorklistQueryResult> QueryAsync(
        WorklistConfiguration configuration,
        string callingAeTitle,
        string? patientIdFilter = null,
        string? patientNameFilter = null,
        CancellationToken cancellationToken = default);
}
