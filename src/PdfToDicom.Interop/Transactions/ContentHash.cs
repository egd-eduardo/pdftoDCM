using System.Security.Cryptography;

namespace PdfToDicom.Interop.Transactions;

/// <summary>
/// Identifies a message by its content, so that the same document is recognisable as the
/// same document without keeping a copy of it.
/// </summary>
public static class ContentHash
{
    /// <summary>Hashes exactly the bytes that were, or would be, transmitted.</summary>
    /// <remarks>
    /// SHA-256 rather than something cheaper. The question being asked is whether two
    /// messages are the same document, and the cost of a wrong answer is either a duplicate
    /// report in a patient's record or a real report suppressed as a duplicate. Neither is
    /// worth the microseconds a weaker hash would save on a message sent a few times an
    /// hour.
    /// </remarks>
    public static string Of(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
