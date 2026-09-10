namespace PdfToDicom.Core.Licensing;

/// <summary>
/// Reads what a licence code says, without checking whether it is genuine.
/// </summary>
/// <remarks>
/// <para>
/// A build that carries no publisher key cannot verify a signature, so
/// <see cref="LicenceService"/> reports every code as "not enforced" and says nothing
/// about the code itself. That is correct as an enforcement answer and useless as
/// feedback: the person setting the system up pastes a code and sees no indication of
/// whether it even arrived intact.
/// </para>
/// <para>
/// This reads the payload and describes it, and is explicit that nothing was verified.
/// It is a convenience for the publisher during setup, never a substitute for
/// <see cref="LicenceService.Check"/>, and nothing in the application makes a decision
/// on what it returns.
/// </para>
/// </remarks>
public static class LicenceInspector
{
    /// <summary>
    /// Describes a code's contents in plain language, or explains why it cannot be read.
    /// </summary>
    /// <returns>
    /// A sentence suitable for showing to an operator. Never throws, and never claims a
    /// code is valid, because without the publisher key that cannot be known.
    /// </returns>
    public static string Describe(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "No code entered.";
        }

        try
        {
            if (!LicenceCodec.TryDecode(code, out byte[] payloadBytes, out _))
            {
                return "This does not look like a licence code. Check that it was copied in full, " +
                       "including the part before the first full stop.";
            }

            LicencePayload? payload = LicencePayload.FromBytes(payloadBytes);

            if (payload is null)
            {
                return "The code is correctly formed but was issued for a different version of " +
                       "this product.";
            }

            var parts = new List<string> { $"Issued to {payload.Licensee}" };

            parts.Add(payload.ExpiresOn is { } expiry
                ? $"expires {expiry:yyyy-MM-dd}"
                : "no expiry");

            if (payload.Seats > 0)
            {
                parts.Add($"{payload.Seats} seat(s)");
            }

            if (payload.IsMachineBound)
            {
                parts.Add("tied to one workstation");
            }

            return string.Join(", ", parts) + ".";
        }
        catch (Exception)
        {
            // Reading a code is a convenience. It must not be able to disturb the dialog.
            return "The code could not be read.";
        }
    }
}
