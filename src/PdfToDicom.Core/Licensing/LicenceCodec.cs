using System.Security.Cryptography;
using System.Text;

namespace PdfToDicom.Core.Licensing;

/// <summary>
/// Turns a payload and a signature into the code a customer pastes into Settings, and
/// back again.
/// </summary>
/// <remarks>
/// <para>
/// The code has three dot separated parts: a fixed prefix that identifies the format, the
/// payload, and the signature. Both binary parts use base64url, which contains no
/// characters that word processors or email clients mangle, and no padding to be lost.
/// </para>
/// <para>
/// Signing uses ECDSA over NIST P-256 with SHA-256. That is a 64 byte signature, which is
/// the practical floor for a scheme that cannot be forged. A shorter code would mean a
/// symmetric secret embedded in the application, which anyone could extract and use to
/// mint their own licences.
/// </para>
/// </remarks>
public static class LicenceCodec
{
    /// <summary>Prefix identifying a version 1 licence code.</summary>
    public const string Prefix = "PDFDCM1";

    /// <summary>Length in bytes of a P-256 signature in the fixed field format.</summary>
    private const int SignatureLength = 64;

    /// <summary>
    /// Formats a signed licence as the code that is given to a customer.
    /// </summary>
    public static string Encode(byte[] payload, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(signature);

        return $"{Prefix}.{ToBase64Url(payload)}.{ToBase64Url(signature)}";
    }

    /// <summary>
    /// Splits a code back into its payload and signature.
    /// </summary>
    /// <remarks>
    /// Whitespace anywhere in the code is ignored, because a code that travels by email
    /// frequently arrives wrapped across lines, and an operator should not have to know
    /// that.
    /// </remarks>
    /// <returns>True when the code is well formed. The contents are not verified here.</returns>
    public static bool TryDecode(string? code, out byte[] payload, out byte[] signature)
    {
        payload = Array.Empty<byte>();
        signature = Array.Empty<byte>();

        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        string cleaned = Normalise(code);
        string[] parts = cleaned.Split('.');

        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryFromBase64Url(parts[1], out byte[] decodedPayload) ||
            !TryFromBase64Url(parts[2], out byte[] decodedSignature))
        {
            return false;
        }

        if (decodedPayload.Length == 0 || decodedSignature.Length != SignatureLength)
        {
            return false;
        }

        payload = decodedPayload;
        signature = decodedSignature;
        return true;
    }

    /// <summary>
    /// Removes every whitespace character, so a code pasted from an email that wrapped it
    /// across several lines still validates.
    /// </summary>
    public static string Normalise(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        var builder = new StringBuilder(code.Length);

        foreach (char c in code)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Signs a payload with the publisher's private key.
    /// </summary>
    /// <remarks>
    /// Only the licence generator calls this. The private key never reaches the shipped
    /// application, which carries the public half alone.
    /// </remarks>
    public static byte[] Sign(byte[] payload, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(privateKey);

        return privateKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>
    /// Verifies a signature against the publisher's public key.
    /// </summary>
    /// <returns>
    /// True only when the signature is genuine. Any malformed input returns false rather
    /// than raising, because a bad licence code is an ordinary condition and not a fault.
    /// </returns>
    public static bool Verify(byte[] payload, byte[] signature, ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(publicKey);

        try
        {
            return publicKey.VerifyData(
                payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ base64url

    /// <summary>Encodes bytes using the URL safe alphabet, without padding.</summary>
    public static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes the URL safe alphabet, restoring any padding that was trimmed.</summary>
    public static bool TryFromBase64Url(string value, out byte[] result)
    {
        result = Array.Empty<byte>();

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string standard = value.Replace('-', '+').Replace('_', '/');

        // Base64 requires the input length to be a multiple of four.
        standard += (standard.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => "\0"
        };

        if (standard.EndsWith('\0'))
        {
            return false;
        }

        try
        {
            result = Convert.FromBase64String(standard);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
