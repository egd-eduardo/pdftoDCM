namespace PdfToDicom.Core.Models;

using PdfToDicom.Core.Validation;

/// <summary>
/// The result of the complete convert and send workflow for a single PDF document.
/// </summary>
public sealed class ConversionResult
{
    /// <summary>True when a DICOM object was successfully generated.</summary>
    public bool DicomGenerated { get; init; }

    /// <summary>Absolute path of the generated file, when it was written to disk.</summary>
    public string? GeneratedFilePath { get; init; }

    /// <summary>Study Instance UID of the generated object.</summary>
    public string? StudyInstanceUid { get; init; }

    /// <summary>Series Instance UID of the generated object.</summary>
    public string? SeriesInstanceUid { get; init; }

    /// <summary>SOP Instance UID of the generated object.</summary>
    public string? SopInstanceUid { get; init; }

    /// <summary>Validation issues raised before or after generation.</summary>
    public IReadOnlyList<ValidationIssue> ValidationIssues { get; init; } = Array.Empty<ValidationIssue>();

    /// <summary>One entry per configured destination that was attempted.</summary>
    public IReadOnlyList<TransmissionResult> Transmissions { get; init; } = Array.Empty<TransmissionResult>();

    /// <summary>True when the generated file was removed after successful transmission.</summary>
    public bool GeneratedFileDeleted { get; init; }

    /// <summary>Operator facing summary of the overall outcome.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// True when transmission was requested but withheld before any destination was
    /// contacted, which today means only a licence problem.
    /// </summary>
    /// <remarks>
    /// This is distinct from a run that never asked to transmit. Both produce an empty
    /// <see cref="Transmissions"/> list, but only one of them failed to do what the
    /// operator asked, and reporting that as success would be a lie the caller acts on.
    /// </remarks>
    public bool TransmissionWithheld { get; init; }

    /// <summary>True when at least one destination accepted the object.</summary>
    public bool AnyTransmissionSucceeded => Transmissions.Any(t => t.IsSuccess);

    /// <summary>True when every attempted destination accepted the object.</summary>
    public bool AllTransmissionsSucceeded => Transmissions.Count > 0 && Transmissions.All(t => t.IsSuccess);

    /// <summary>
    /// True when the workflow completed exactly as intended: the object was produced,
    /// nothing was withheld, and where destinations were configured every one of them
    /// accepted it.
    /// </summary>
    public bool IsCompleteSuccess =>
        DicomGenerated && !TransmissionWithheld && (Transmissions.Count == 0 || AllTransmissionsSucceeded);
}
