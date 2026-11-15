using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Services;

namespace PdfToDicom.Integration;

/// <summary>
/// The one thing this assembly needs from the conversion workflow.
/// </summary>
/// <remarks>
/// <para>
/// Declared here rather than in <c>PdfToDicom.Core</c>, and that is the point. Core is not
/// changed at all by any of the HL7 work, so the conversion path in clinical use carries no
/// new interface, no new branch and no new assembly reference.
/// </para>
/// <para>
/// It also makes the ordering testable. Proving that an HL7 failure cannot stop a document
/// reaching an archive requires a conversion that can be made to succeed or fail on demand,
/// and the real workflow needs eleven collaborators before it will do either.
/// </para>
/// </remarks>
public interface IConversionRunner
{
    /// <summary>Converts one document, exactly as the front ends do today.</summary>
    Task<ConversionResult> RunAsync(
        string sourcePath,
        PatientDemographics demographics,
        ConversionOptions? options,
        IProgress<TransmissionProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Passes the call straight through to the real workflow.</summary>
public sealed class ConversionWorkflowRunner : IConversionRunner
{
    private readonly ConversionWorkflowService _workflow;

    /// <summary>Wraps the workflow.</summary>
    public ConversionWorkflowRunner(ConversionWorkflowService workflow)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
    }

    /// <inheritdoc />
    public Task<ConversionResult> RunAsync(
        string sourcePath,
        PatientDemographics demographics,
        ConversionOptions? options,
        IProgress<TransmissionProgress>? progress,
        CancellationToken cancellationToken) =>
        _workflow.RunAsync(sourcePath, demographics, options, progress, cancellationToken);
}
