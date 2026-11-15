namespace PdfToDicom.Interop.Hl7V2.Profiles;

/// <summary>
/// The delimiters, character set and policy one interface expects on the wire.
/// </summary>
/// <param name="FieldSeparator">Normally a vertical bar.</param>
/// <param name="EncodingCharacters">The four, normally <c>^~\&amp;</c>.</param>
/// <param name="CharacterSet">An HL7 table 0211 value, for example <c>8859/1</c>.</param>
/// <param name="OnUnrepresentableCharacter">
/// What to do with a value the character set cannot carry. Refusing is the default, and the
/// reasoning is in the character set type itself.
/// </param>
public sealed record ProfileEncoding(
    string FieldSeparator = "|",
    string EncodingCharacters = "^~\\&",
    string CharacterSet = "8859/1",
    string OnUnrepresentableCharacter = "refuse");

/// <summary>
/// What a destination does about acknowledgements, which is not something to assume.
/// </summary>
/// <param name="Expect">
/// <c>none</c>, <c>accept</c> or <c>application</c>. A profile declaring <c>none</c> is
/// never waited on, because an interface that answers nothing turns every wait into a
/// timeout.
/// </param>
/// <param name="AcceptAckType">The value written to MSH-15.</param>
/// <param name="ApplicationAckType">The value written to MSH-16.</param>
/// <param name="TimeoutSeconds">How long to wait, where waiting happens at all.</param>
public sealed record ProfileAcknowledgement(
    string Expect = "application",
    string AcceptAckType = "AL",
    string ApplicationAckType = "AL",
    int TimeoutSeconds = 30)
{
    /// <summary>True when this interface replies to nothing.</summary>
    public bool ExpectsNone =>
        string.Equals(Expect, "none", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One segment's place in the message.</summary>
/// <param name="Id">The three character name, a Z segment included.</param>
/// <param name="Usage">Whether it is required, optional, conditional or forbidden.</param>
/// <param name="Min">The least number of occurrences.</param>
/// <param name="Max">The most, or zero for unbounded.</param>
/// <param name="Group">The segment group it belongs to, for documentation.</param>
public sealed record ProfileSegment(
    string Id,
    ProfileUsage Usage = ProfileUsage.Optional,
    int Min = 0,
    int Max = 1,
    string Group = "")
{
    /// <summary>True when the profile puts no upper limit on occurrences.</summary>
    public bool IsUnbounded => Max <= 0;
}

/// <summary>
/// One field's rules: where it is, what may go in it, and how much of it.
/// </summary>
/// <param name="Path">An HL7 path, as a conformance statement writes it.</param>
/// <param name="Name">The field's name, for messages shown to an administrator.</param>
/// <param name="DataType">The HL7 data type, carried for documentation and validation.</param>
/// <param name="Usage">Required, optional, conditional or not supported.</param>
/// <param name="MaxLength">The longest value accepted, or zero for no stated limit.</param>
/// <param name="AllowedValues">
/// The only values accepted, or empty when anything is. The specimen restricts ORC-1 to two
/// values and OBX-2 to three, and a message carrying anything else is not conformant.
/// </param>
public sealed record ProfileField(
    string Path,
    string Name = "",
    string DataType = "",
    ProfileUsage Usage = ProfileUsage.Optional,
    int MaxLength = 0,
    IReadOnlyList<string>? AllowedValues = null)
{
    /// <summary>The values this field accepts, empty when unrestricted.</summary>
    public IReadOnlyList<string> AllowedValues { get; init; } = AllowedValues ?? [];
}

/// <summary>
/// One instruction for filling one place in the message.
/// </summary>
/// <param name="Target">Where the value goes, as an HL7 path.</param>
/// <param name="Source">
/// Which piece of application data it comes from, or empty when <paramref name="Value"/>
/// supplies it instead.
/// </param>
/// <param name="Value">A fixed value, used when there is no source.</param>
/// <param name="Transform">
/// A named transform from the closed set, or empty for none. Several may be chained with a
/// vertical bar.
/// </param>
/// <param name="Table">A code translation table, applied after any transform.</param>
/// <param name="Required">
/// Whether an empty result is a reason to refuse the message. Separate from the field's own
/// usage: a field may be required by the receiver and still have no source here, which is
/// exactly the case worth catching before anything is sent.
/// </param>
public sealed record ProfileMapping(
    string Target,
    string Source = "",
    string Value = "",
    string Transform = "",
    string Table = "",
    bool Required = false);
