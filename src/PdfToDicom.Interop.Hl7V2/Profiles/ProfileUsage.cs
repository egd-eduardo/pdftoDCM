namespace PdfToDicom.Interop.Hl7V2.Profiles;

/// <summary>
/// How a segment or a field is used by one interface, in the vocabulary conformance
/// statements use.
/// </summary>
public enum ProfileUsage
{
    /// <summary>Optional. May be sent, may be omitted.</summary>
    Optional = 0,

    /// <summary>Required. A message without it does not conform.</summary>
    Required = 1,

    /// <summary>
    /// Conditional. Required when the condition on the element holds, optional otherwise.
    /// </summary>
    Conditional = 2,

    /// <summary>
    /// Not supported. The receiving system does not accept it, so this software must not
    /// emit it.
    /// </summary>
    /// <remarks>
    /// Worth having as a value of its own rather than treating it as "optional and never
    /// populated". The supplied specimen marks eight segments this way, and the difference
    /// between "you may omit this" and "do not send this" is the difference between a
    /// message that conforms and one a receiver may reject outright.
    /// </remarks>
    NotSupported = 3
}
