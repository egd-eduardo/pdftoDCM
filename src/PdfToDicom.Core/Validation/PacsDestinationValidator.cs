using System.Net;
using PdfToDicom.Core.Configuration;

namespace PdfToDicom.Core.Validation;

/// <summary>
/// Validates the network configuration of a destination before an association is
/// attempted, so that a typing mistake is reported clearly instead of surfacing as a
/// connection timeout several seconds later.
/// </summary>
public sealed class PacsDestinationValidator
{
    /// <summary>Validates a single destination together with the global calling AE Title.</summary>
    /// <param name="destination">The destination to check.</param>
    /// <param name="callingAeTitle">
    /// The calling AE Title that will be used when the destination does not override it.
    /// </param>
    public ValidationResult Validate(PacsDestination destination, string callingAeTitle)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(destination.Name))
        {
            result.AddError(nameof(destination.Name), "The destination needs a name so it can be identified in the log.");
        }

        if (!DicomValueRules.IsValidAeTitle(destination.CalledAeTitle, out string calledError))
        {
            result.AddError(nameof(destination.CalledAeTitle), $"Called AE Title: {calledError}");
        }

        string effectiveCallingAe = string.IsNullOrWhiteSpace(destination.CallingAeTitleOverride)
            ? callingAeTitle
            : destination.CallingAeTitleOverride;

        if (!DicomValueRules.IsValidAeTitle(effectiveCallingAe, out string callingError))
        {
            result.AddError(nameof(destination.CallingAeTitleOverride), $"Calling AE Title: {callingError}");
        }

        ValidateHost(destination, result);

        if (destination.Port is < 1 or > 65535)
        {
            result.AddError(
                nameof(destination.Port),
                "The port must be between 1 and 65535. DICOM archives most commonly listen on 104, 4242 or 11112.");
        }

        if (destination.TimeoutSeconds < 1)
        {
            result.AddError(nameof(destination.TimeoutSeconds), "The timeout must be at least one second.");
        }
        else if (destination.TimeoutSeconds > 300)
        {
            result.AddWarning(
                nameof(destination.TimeoutSeconds),
                "A timeout longer than five minutes will leave the operator waiting with no feedback.");
        }

        if (destination.RetryCount is < 0 or > 10)
        {
            result.AddError(nameof(destination.RetryCount), "The retry count must be between 0 and 10.");
        }

        if (destination.UseTls && destination.AcceptAnyTlsCertificate)
        {
            result.AddWarning(
                nameof(destination.AcceptAnyTlsCertificate),
                "Accepting any TLS certificate disables authentication of the receiving system. " +
                "Use this only on a closed network with a self signed certificate.");
        }

        return result;
    }

    /// <summary>
    /// Validates the whole configuration, confirming that at least one usable destination
    /// exists and that each individual destination is well formed.
    /// </summary>
    public ValidationResult ValidateAll(AppConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var result = new ValidationResult();

        if (!DicomValueRules.IsValidAeTitle(configuration.CallingAeTitle, out string callingError))
        {
            result.AddError(nameof(configuration.CallingAeTitle), $"Calling AE Title: {callingError}");
        }

        IReadOnlyList<PacsDestination> enabled = configuration.EnabledDestinations;

        if (enabled.Count == 0)
        {
            result.AddError(
                nameof(configuration.Destinations),
                "No enabled destination is configured, so nothing can be transmitted. Add one in Settings.");
            return result;
        }

        foreach (PacsDestination destination in enabled)
        {
            ValidationResult destinationResult = Validate(destination, configuration.CallingAeTitle);

            foreach (ValidationIssue issue in destinationResult.Issues)
            {
                result.Add(issue with { Message = $"{destination.Name}: {issue.Message}" });
            }
        }

        // Two destinations that resolve to the same endpoint would send the object twice,
        // producing a duplicate in the archive.
        var duplicates = enabled
            .GroupBy(d => $"{d.Host.Trim().ToLowerInvariant()}:{d.Port}:{d.CalledAeTitle.Trim().ToUpperInvariant()}")
            .Where(g => g.Count() > 1);

        foreach (var duplicate in duplicates)
        {
            result.AddWarning(
                nameof(configuration.Destinations),
                $"More than one enabled destination points at {duplicate.Key}. " +
                "The document would be sent there more than once.");
        }

        return result;
    }

    /// <summary>
    /// Confirms the host is present and is either a valid IP address or a plausible host
    /// name. Name resolution itself is not attempted here because that would block the
    /// user interface thread.
    /// </summary>
    private static void ValidateHost(PacsDestination destination, ValidationResult result)
    {
        string host = (destination.Host ?? string.Empty).Trim();

        if (host.Length == 0)
        {
            result.AddError(nameof(destination.Host), "A host name or IP address is required.");
            return;
        }

        if (IPAddress.TryParse(host, out _))
        {
            return;
        }

        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            result.AddError(
                nameof(destination.Host),
                $"'{host}' is neither a valid IP address nor a valid host name.");
        }
    }
}
