using FellowOakDicom;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.Core.Abstractions;

/// <summary>
/// Verifies that a generated object satisfies the Encapsulated PDF Information Object
/// Definition before it is written to disk or transmitted.
/// </summary>
/// <remarks>
/// This is a defence in depth measure. The builder is written to produce conformant
/// output, but an explicit check means a defect is caught locally with a precise
/// message rather than being rejected by the archive with an opaque status code.
/// </remarks>
public interface IDicomValidator
{
    /// <summary>Validates the supplied file against the Encapsulated PDF IOD.</summary>
    ValidationResult Validate(DicomFile file);
}
