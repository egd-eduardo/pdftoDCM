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

    /// <summary>
    /// SOP Instance UID of the generated object, or of the first page when the source had
    /// several.
    /// </summary>
    public string? SopInstanceUid { get; init; }

    /// <summary>
    /// Every SOP Instance UID produced, in page order. A PDF or a single page image
    /// yields one; a multi page TIFF yields one per page, all in the same series.
    /// </summary>
    public IReadOnlyList<string> SopInstanceUids { get; init; } = Array.Empty<string>();

    /// <summary>Absolute paths of every generated file that was written to disk.</summary>
    public IReadOnlyList<string> GeneratedFilePaths { get; init; } = Array.Empty<string>();

    /// <summary>How many DICOM instances the conversion produced.</summary>
    public int InstanceCount => SopInstanceUids.Count;

    /// <summary>Validation issues raised before or after generation.</summary>
    public IReadOnlyList<ValidationIssue> ValidationIssues { get; init; } = Array.Empty<ValidationIssue>();

    /// <summary>One entry per configured destination that was attempted.</summary>
    public IReadOnlyList<TransmissionResult> Transmissions { get; init; } = Array.Empty<TransmissionResult>();

    /// <summary>True when the generated file was removed after successful transmission.</summary>
    public bool GeneratedFileDeleted { get; init; }

    /// <summary>Operator facing summary of the overall outcome.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>True when at least one destination accepted the object.</summary>
    public bool AnyTransmissionSucceeded => Transmissions.Any(t => t.IsSuccess);

    /// <summary>True when every attempted destination accepted the object.</summary>
    public bool AllTransmissionsSucceeded => Transmissions.Count > 0 && Transmissions.All(t => t.IsSuccess);

    /// <summary>
    /// True when the workflow completed exactly as intended: the object was produced and,
    /// where destinations were configured, every one of them accepted it.
    /// </summary>
    public bool IsCompleteSuccess => DicomGenerated && (Transmissions.Count == 0 || AllTransmissionsSucceeded);
}
