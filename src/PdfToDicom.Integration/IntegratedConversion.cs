using PdfToDicom.Core.Models;
using PdfToDicom.Interop.Hl7V2.Dispatch;

namespace PdfToDicom.Integration;

/// <summary>
/// In what order the two halves of a conversion happen, and whether either may stop the
/// other.
/// </summary>
public enum IntegrationSequence
{
    /// <summary>
    /// Both happen, and neither can stop the other. The default, and what a site gets
    /// without choosing anything.
    /// </summary>
    /// <remarks>
    /// The DICOM object reaching the archive and the message reaching the record system
    /// are two separate pieces of value. Letting a record system that is down prevent the
    /// archive receiving a report would turn one failure into two, and the archive is the
    /// one that matters clinically.
    /// </remarks>
    Independent = 0,

    /// <summary>
    /// The message is sent only once the DICOM object has been produced and accepted.
    /// </summary>
    /// <remarks>
    /// For a receiver whose message refers to the object: an ORU carrying a Study Instance
    /// UID that no archive holds is a message pointing at nothing.
    /// </remarks>
    DicomThenHl7 = 1,

    /// <summary>
    /// The message goes first, and the DICOM object is only sent once it was accepted.
    /// </summary>
    /// <remarks>
    /// For a receiver that will not file an image until an order exists. This is the one
    /// sequence where an HL7 failure stops a DICOM transmission, which is why it has to be
    /// chosen deliberately rather than arrived at.
    /// </remarks>
    Hl7ThenDicom = 2
}

/// <summary>What a conversion did, on both sides.</summary>
/// <param name="Dicom">The DICOM result, or null when the sequence stopped before it.</param>
/// <param name="Hl7">One result per destination the message was sent to.</param>
/// <param name="Sequence">The order that was used.</param>
public sealed record IntegratedConversionResult(
    ConversionResult? Dicom,
    IReadOnlyList<Hl7DispatchResult> Hl7,
    IntegrationSequence Sequence)
{
    /// <summary>True when the DICOM half did everything it was asked to.</summary>
    public bool DicomSucceeded => Dicom?.IsCompleteSuccess ?? false;

    /// <summary>True when every message that was attempted reached its destination.</summary>
    public bool Hl7Succeeded =>
        Hl7.Count == 0 || Hl7.All(h => h.Outcome is Hl7DispatchOutcome.Acknowledged
                                                 or Hl7DispatchOutcome.Sent
                                                 or Hl7DispatchOutcome.AlreadyDelivered);

    /// <summary>True when anything at all needs a person to look at it.</summary>
    public bool NeedsAttention => !DicomSucceeded || Hl7.Any(h => h.NeedsAttention);

    /// <summary>
    /// A summary that keeps the two halves apart.
    /// </summary>
    /// <remarks>
    /// One combined verdict was the obvious thing and was rejected. An operator needs to
    /// know which half failed, because the two are acted on completely differently: a
    /// failed archive transmission means the report is not where a clinician will look
    /// for it, and a failed message means a record system has not been told about a report
    /// that is nonetheless safely filed.
    /// </remarks>
    public string Summary
    {
        get
        {
            string dicom = Dicom?.Summary ?? "The DICOM object was not produced.";

            if (Hl7.Count == 0)
            {
                return dicom;
            }

            string messages = string.Join("; ", Hl7.Select(h => $"{h.Message}"));

            return $"{dicom} HL7: {messages}";
        }
    }
}
