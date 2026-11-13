namespace PdfToDicom.Interop.Hl7V2;

/// <summary>
/// Raised when a message cannot be read as ER7 at all.
/// </summary>
/// <remarks>
/// Reserved for structural failures: no MSH, delimiters that cannot be read, a segment
/// name that is not a segment name. A message that parses but does not match a profile is
/// not an error here; that is the validator's answer to give, and conflating the two would
/// make a profile mismatch look like corruption.
/// </remarks>
public sealed class Hl7ParseException : Exception
{
    /// <summary>Creates the exception.</summary>
    public Hl7ParseException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an underlying cause.</summary>
    public Hl7ParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
