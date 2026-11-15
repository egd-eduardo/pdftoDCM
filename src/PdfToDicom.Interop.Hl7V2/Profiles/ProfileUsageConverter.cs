using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfToDicom.Interop.Hl7V2.Profiles;

/// <summary>
/// Reads usage written either the way a conformance statement writes it, as R, O, C or X,
/// or spelled out.
/// </summary>
/// <remarks>
/// Both spellings are accepted because both will be written. Somebody copying a vendor's
/// table will type R; somebody writing a profile from scratch will type "required". Making
/// either of them wrong would be a trap with no purpose.
/// </remarks>
public sealed class ProfileUsageConverter : JsonConverter<ProfileUsage>
{
    /// <inheritdoc />
    public override ProfileUsage Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string value = reader.GetString() ?? string.Empty;

        return value.Trim().ToUpperInvariant() switch
        {
            "R" or "REQUIRED" => ProfileUsage.Required,
            "O" or "OPTIONAL" => ProfileUsage.Optional,
            "C" or "CONDITIONAL" => ProfileUsage.Conditional,
            "X" or "NOTSUPPORTED" or "NOT SUPPORTED" => ProfileUsage.NotSupported,
            _ => throw new JsonException(
                $"'{value}' is not a usage. Write R, O, C or X, or the word spelled out.")
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ProfileUsage value, JsonSerializerOptions options)
    {
        // Written back in the short form, because that is what the documents an
        // administrator is holding use.
        writer.WriteStringValue(value switch
        {
            ProfileUsage.Required => "R",
            ProfileUsage.Conditional => "C",
            ProfileUsage.NotSupported => "X",
            _ => "O"
        });
    }
}
