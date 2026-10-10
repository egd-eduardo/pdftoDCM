using System.Globalization;
using System.Text;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Models;

namespace PdfToDicom.Core.Logging;

/// <summary>
/// Writes a tab separated audit trail, one line per event, into a daily file.
/// </summary>
/// <remarks>
/// The format is deliberately simple and stable so that it can be opened in a
/// spreadsheet or ingested by a hospital's information governance tooling. Each line
/// records the timestamp, the Windows account that performed the action, the machine it
/// was performed on, the event type and the event detail.
/// </remarks>
public sealed class AuditLogger : IAuditLogger
{
    private const string HeaderLine =
        "Timestamp\tUser\tMachine\tEvent\tDetail";

    private readonly object _writeLock = new();
    private readonly string _userName;
    private readonly string _machineName;
    private readonly IAppLogger _appLogger;

    /// <summary>
    /// Creates the audit logger.
    /// </summary>
    /// <param name="auditFolder">Folder into which audit files are written.</param>
    /// <param name="appLogger">
    /// Diagnostic logger used to report a failure to write the audit trail. An audit
    /// write failure must be visible; it must not silently disappear.
    /// </param>
    public AuditLogger(string auditFolder, IAppLogger appLogger)
    {
        _appLogger = appLogger ?? throw new ArgumentNullException(nameof(appLogger));

        try
        {
            Directory.CreateDirectory(auditFolder);
            AuditFolder = auditFolder;
        }
        catch
        {
            AuditFolder = Path.Combine(Path.GetTempPath(), "PdfToDicom", "Audit");
            Directory.CreateDirectory(AuditFolder);
        }

        _userName = SafeGet(() => Environment.UserName, "unknown");
        _machineName = SafeGet(() => Environment.MachineName, "unknown");
    }

    /// <inheritdoc />
    public string AuditFolder { get; }

    /// <inheritdoc />
    public void ApplicationStarted(string version) =>
        Write("APPLICATION_START", $"version={version}");

    /// <inheritdoc />
    public void ApplicationClosed() =>
        Write("APPLICATION_CLOSE", string.Empty);

    /// <inheritdoc />
    public void PdfSelected(PdfDocumentInfo document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Write(
            "PDF_SELECTED",
            $"file={document.FileName}; bytes={document.SizeInBytes}; " +
            $"pages={document.PageCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; " +
            $"created={document.CreatedUtc:yyyy-MM-ddTHH:mm:ssZ}");
    }

    /// <inheritdoc />
    public void PatientInformationEntered(PatientDemographics demographics)
    {
        ArgumentNullException.ThrowIfNull(demographics);
        Write(
            "PATIENT_INFO_ENTERED",
            $"patientId={demographics.PatientId}; " +
            $"name={demographics.FamilyName}^{demographics.GivenName}; " +
            $"dob={demographics.DateOfBirth?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty}; " +
            $"sex={demographics.Sex.ToDicomValue()}; " +
            $"accession={demographics.AccessionNumber}");
    }

    /// <inheritdoc />
    public void PatientInformationReadFromFilename(string fileName, string pattern) =>
        Write(
            "PATIENT_INFO_FROM_FILENAME",
            $"file={fileName}; pattern={pattern}");

    /// <inheritdoc />
    public void DicomGenerated(string studyUid, string seriesUid, string sopUid, string? filePath) =>
        Write(
            "DICOM_GENERATED",
            $"studyUid={studyUid}; seriesUid={seriesUid}; sopUid={sopUid}; file={filePath ?? "(not written to disk)"}");

    /// <inheritdoc />
    public void TransmissionAttempted(TransmissionResult result, string sopInstanceUid)
    {
        ArgumentNullException.ThrowIfNull(result);
        Write(
            result.IsSuccess ? "TRANSMISSION_SUCCESS" : "TRANSMISSION_FAILURE",
            $"destination={result.DestinationName}; ae={result.CalledAeTitle}; " +
            $"host={result.Host}:{result.Port}; sopUid={sopInstanceUid}; " +
            $"outcome={result.Outcome}; status={result.DicomStatusCode ?? "n/a"}; " +
            $"attempts={result.AttemptCount}; durationMs={(long)result.Duration.TotalMilliseconds}; " +
            $"message={Flatten(result.Message)}");
    }

    /// <inheritdoc />
    public void GeneratedFileDeleted(string filePath) =>
        Write("GENERATED_FILE_DELETED", $"file={filePath}");

    /// <inheritdoc />
    public void ConfigurationChanged(string summary) =>
        Write("CONFIGURATION_CHANGED", Flatten(summary));

    /// <summary>
    /// Appends one audit line. Failures are reported to the diagnostic log but never
    /// propagate, because losing the audit trail must not abort a clinical workflow that
    /// has otherwise succeeded.
    /// </summary>
    private void Write(string eventType, string detail)
    {
        try
        {
            string path = Path.Combine(AuditFolder, $"audit-{DateTime.Now:yyyy-MM-dd}.tsv");

            lock (_writeLock)
            {
                bool needsHeader = !File.Exists(path);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                if (needsHeader)
                {
                    writer.WriteLine(HeaderLine);
                }

                writer.WriteLine(
                    string.Join(
                        '\t',
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        _userName,
                        _machineName,
                        eventType,
                        Flatten(detail)));
            }
        }
        catch (Exception ex)
        {
            _appLogger.Error($"Failed to write the audit entry '{eventType}'.", ex);
        }
    }

    /// <summary>Removes tabs and newlines so that one event always occupies one line.</summary>
    private static string Flatten(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace('\t', ' ').Replace("\r", string.Empty).Replace('\n', ' ');

    private static string SafeGet(Func<string> accessor, string fallback)
    {
        try
        {
            string value = accessor();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }
}
