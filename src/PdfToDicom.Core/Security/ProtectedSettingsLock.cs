using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PdfToDicom.Core.Security;

/// <summary>
/// Guards the settings that decide where patient data comes from and goes to, behind a
/// password compiled into the software.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it covers.</b> The worklist servers, which decide which patients are offered,
/// and the destinations, which decide where documents are sent. Both are addresses and AE
/// Titles, and both put patient data somewhere: a mistyped destination sends a report to
/// the wrong archive, and a mistyped worklist offers the wrong patients to choose from.
/// One password opens both, because they are the same kind of setting and the same person
/// is trusted with them.
/// </para>
/// <para>
/// <b>What this protects against, and what it does not.</b> It stops a curious member of
/// staff wandering into those pages and changing an address or an AE Title, which is the
/// realistic risk on a shared clinical workstation. It does not stop someone
/// determined: the check runs on the user's own machine, and anyone able to decompile or
/// patch the executable can remove it. That is true of every password a desktop
/// application checks by itself, and saying otherwise would be dishonest.
/// </para>
/// <para>
/// <b>A verifier, not the password.</b> The request was for the password to be stored
/// encrypted. A hash is used instead, and it is strictly stronger: anything encrypted
/// inside the binary must also carry its key inside the binary, so it can be decrypted
/// back to the password. What is stored here cannot be turned back into the password at
/// all. It can only answer whether a password offered to it is the right one.
/// </para>
/// <para>
/// <b>PBKDF2 with SHA-256 and 210,000 iterations</b>, which is the OWASP figure for that
/// combination. The cost is paid once when the dialog opens, where a fraction of a second
/// is unnoticeable, and it is what makes guessing the password from the stored verifier
/// expensive rather than instant.
/// </para>
/// <para>
/// <b>The tool that sets it never records the password.</b>
/// <c>pdftodicom settings-password</c> reads it without echoing, derives the verifier on
/// the spot and prints only the line to paste in. Nothing that could be turned back into
/// the password leaves that process.
/// </para>
/// <para>
/// <b>The current password is nevertheless written in the clear on the Verifier
/// constant below</b>, at the copyright holder's request, so that it cannot be forgotten.
/// That is a deliberate trade and the reasoning is recorded there. It is why the design
/// above still matters: the verifier remains one way, so removing that one comment is all
/// it takes to stop the password being recoverable from this repository.
/// </para>
/// </remarks>
public static class ProtectedSettingsLock
{
    /// <summary>
    /// The stored verifier, in the form <c>PBKDF2-SHA256$iterations$salt$hash</c>, with
    /// the salt and hash base64 encoded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty means no password has been set, and the protected settings are then open. That
    /// is deliberate: shipping with a placeholder password nobody knows would lock the
    /// author out of their own software, which is a worse failure than an unset lock. The
    /// application reports the setting as unprotected so it cannot be forgotten.
    /// </para>
    /// <para>
    /// <b>The password is no longer written down here.</b> It used to be, at the copyright
    /// holder's request, on the reasoning that the source was never shared and that
    /// anybody holding it could delete the check outright anyway. That reasoning ended the
    /// day this repository became public, so the plaintext was removed.
    /// </para>
    /// <para>
    /// <b>Removing it does not undo it.</b> The comment is in the repository's history and
    /// the history is public, so the password it named must be treated as known to
    /// everybody and the verifier below replaced. Until it is, this lock opens to anyone
    /// who reads an old commit.
    /// </para>
    /// <para>
    /// Nothing here is a secret worth keeping secret in the first place. The lock exists
    /// against a member of staff wandering into the worklist or destination configuration
    /// on a shared clinical workstation, not against somebody with the source.
    /// </para>
    /// <para>
    /// <b>To change it.</b> Run <c>pdftodicom settings-password</c>, paste the single line
    /// it prints over the constant below, update the comment above to match, and rebuild.
    /// </para>
    /// <para>
    /// <b>To remove it.</b> Set the constant to an empty string. The protected settings are
    /// then open to anyone and the dialog says so plainly, which is the honest behaviour
    /// for a lock that is not set rather than one that pretends otherwise.
    /// </para>
    /// </remarks>
    private const string Verifier = "PBKDF2-SHA256$210000$WD4eR31ApewdTMAQLuw0/w==$DYRQ73Fs9FKnMYvYp5hTSq1sjcd2RGPIqGnOsqd5QZg=";

    /// <summary>The algorithm label written into, and expected in, a verifier.</summary>
    private const string Algorithm = "PBKDF2-SHA256";

    /// <summary>Iteration count used for a newly generated verifier.</summary>
    /// <remarks>
    /// The OWASP recommendation for PBKDF2-HMAC-SHA256. Verification uses whatever count
    /// is recorded in the stored verifier, so raising this later does not invalidate a
    /// password already set.
    /// </remarks>
    private const int Iterations = 210_000;

    /// <summary>Length in bytes of the salt and of the derived key.</summary>
    private const int SaltLength = 16;
    private const int KeyLength = 32;

    /// <summary>
    /// True when a password has been compiled in. False means the protected settings are
    /// open to anyone, which the user interface says plainly rather than implying a
    /// protection that is not there.
    /// </summary>
    public static bool IsProtected => !string.IsNullOrWhiteSpace(Verifier);

    /// <summary>
    /// Checks a password against the stored verifier.
    /// </summary>
    /// <returns>
    /// True when the password is correct, or when no password has been set at all. An
    /// unset lock admits everyone by design; callers decide whether to prompt by asking
    /// <see cref="IsProtected"/> first.
    /// </returns>
    public static bool Verify(string? password) => VerifyAgainst(password, Verifier);

    /// <summary>
    /// Checks a password against a specific verifier.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="Verify(string?)"/> so the derivation and comparison can be
    /// exercised by the tests. With no password compiled in, the constant is empty and
    /// everything below it would otherwise be unreachable code that nothing had ever run,
    /// which is a poor state for the one routine standing between a shared workstation and
    /// the worklist configuration.
    /// </remarks>
    public static bool VerifyAgainst(string? password, string verifier)
    {
        if (string.IsNullOrWhiteSpace(verifier))
        {
            return true;
        }

        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        if (!TryParse(verifier, out int iterations, out byte[] salt, out byte[] expected))
        {
            // A malformed verifier is a build fault, not a wrong password. Refusing
            // everyone is the safe direction, and the message says where to look.
            throw new InvalidOperationException(
                "The worklist settings password verifier compiled into this build is malformed. " +
                "Regenerate it with 'pdftodicom worklist-password' and rebuild.");
        }

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);

        // Constant time, so the comparison cannot be used to learn the hash a byte at a
        // time by measuring how long it takes to fail.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Produces a verifier for a password, to be pasted into <see cref="Verifier"/>.
    /// </summary>
    /// <remarks>
    /// A fresh random salt each time, so the same password never produces the same
    /// verifier twice and the stored value reveals nothing by being compared with another.
    /// </remarks>
    public static string CreateVerifier(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);

        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeyLength);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Algorithm}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <summary>Splits a stored verifier into its parts.</summary>
    private static bool TryParse(string verifier, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = Array.Empty<byte>();
        hash = Array.Empty<byte>();

        string[] parts = verifier.Split('$');

        if (parts.Length != 4 || !string.Equals(parts[0], Algorithm, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out iterations)
            || iterations <= 0)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length > 0 && hash.Length > 0;
    }
}
