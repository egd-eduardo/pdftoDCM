using PdfToDicom.Interop.Hl7V2;
using PdfToDicom.Interop.Hl7V2.Dispatch;
using PdfToDicom.Interop.Hl7V2.Inspection;
using PdfToDicom.Interop.Hl7V2.Model;
using PdfToDicom.Interop.Hl7V2.Profiles;
using PdfToDicom.Interop.Hl7V2.Transport;
using PdfToDicom.Interop.Messaging;
using PdfToDicom.Interop.Transactions;

namespace PdfToDicom.Cli;

/// <summary>
/// The command line half of commissioning an interface: check a message against a profile
/// without a network, and send one with a network.
/// </summary>
/// <remarks>
/// These exist for two audiences. Somebody setting up an integration needs to try a
/// profile against a real endpoint before anything clinical goes near it, and the
/// interoperability script needs a way to drive the engine end to end rather than calling
/// its classes from a test, which would prove only that the test agrees with the code.
/// </remarks>
internal static class Hl7Commands
{
    /// <summary>Lays a message out against a profile and prints what is wrong with it.</summary>
    public static int Check(string profilePath, string messagePath)
    {
        if (!TryReadProfile(profilePath, out Hl7Profile? profile))
        {
            return ExitCodes.InvalidArguments;
        }

        if (!File.Exists(messagePath))
        {
            Console.Error.WriteLine($"error: there is no message at '{messagePath}'.");
            return ExitCodes.InvalidArguments;
        }

        Hl7Message message;

        try
        {
            message = Hl7Message.Parse(File.ReadAllText(messagePath));
        }
        catch (Hl7ParseException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCodes.InvalidArguments;
        }

        MessageInspection inspection = Hl7MessageInspector.Inspect(message, profile!);

        foreach (InspectedSegment segment in inspection.Segments.Where(s => s.IsPresent))
        {
            Console.WriteLine(segment.Label);

            foreach (InspectedField field in segment.Fields.Where(f => !f.IsEmpty))
            {
                string name = field.Name.Length > 0 ? $"  ({field.Name})" : string.Empty;
                Console.WriteLine($"  {field.Path,-12}{field.Value,-40}{name}");
            }
        }

        if (inspection.MissingMandatoryFields.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Mandatory fields that are empty:");

            foreach (InspectedField field in inspection.MissingMandatoryFields)
            {
                Console.WriteLine($"  {field.Path,-14}{field.Name}");
            }
        }

        if (inspection.Issues.Count > 0)
        {
            Console.WriteLine();

            foreach (Hl7MessageIssue issue in inspection.Issues)
            {
                Console.WriteLine($"  {issue.Severity}: {issue.Location}: {issue.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(inspection.IsSendable
            ? "The message satisfies this profile."
            : $"The message does not satisfy this profile: {inspection.Errors.Count} objection(s).");

        return inspection.IsSendable ? ExitCodes.Success : ExitCodes.GenerationFailed;
    }

    /// <summary>Builds a message from a profile and sends it.</summary>
    public static async Task<int> SendAsync(
        string profilePath,
        string target,
        IReadOnlyList<string> values,
        bool testData,
        bool noSend,
        int timeoutSeconds,
        string transactionFolder)
    {
        if (!TryReadProfile(profilePath, out Hl7Profile? profile))
        {
            return ExitCodes.InvalidArguments;
        }

        MessageSubject subject = BuildSubject(values, testData);

        if (noSend)
        {
            Hl7BuildResult built = new Hl7MessageBuilder().Build(profile!, subject);

            foreach (Hl7MessageIssue issue in built.Issues)
            {
                Console.Error.WriteLine($"  {issue.Severity}: {issue.Location}: {issue.Message}");
            }

            // Written with real carriage returns so that what is printed is what would go
            // on the wire, byte for byte. A console shows them as line breaks; a file
            // redirected from here can be fed straight back into hl7-check.
            Console.Write(built.Message?.ToEr7());
            Console.WriteLine();

            return built.IsSendable ? ExitCodes.Success : ExitCodes.GenerationFailed;
        }

        if (!TryReadTarget(target, out string host, out int port))
        {
            return ExitCodes.InvalidArguments;
        }

        var destination = new Hl7Destination
        {
            Name = $"{host}:{port}",
            Host = host,
            Port = port,

            // No retry from the command line. A retry can duplicate a document at the far
            // end when the first attempt was received but unacknowledged, and the engine
            // says as much in its result; whether that is acceptable is a decision for
            // whoever is watching, not a default.
            RetryCount = 0,
            ReceiveTimeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 30
        };

        var dispatcher = new Hl7Dispatcher(
            new JsonLinesTransactionStore(transactionFolder),
            (d, expectAck) => new MllpTransport(d.ToTransportOptions(expectAck)));

        Hl7DispatchResult result = await dispatcher.SendAsync(profile!, destination, subject);

        foreach (Hl7MessageIssue issue in result.Issues)
        {
            Console.Error.WriteLine($"  {issue.Severity}: {issue.Location}: {issue.Message}");
        }

        Console.WriteLine($"control id : {result.MessageControlId}");
        Console.WriteLine($"outcome    : {result.Outcome}");
        Console.WriteLine($"message    : {result.Message}");

        if (result.Acknowledgement.Length > 0)
        {
            Console.WriteLine($"reply      : {result.Acknowledgement.Replace('\r', '|')}");
        }

        return result.Outcome switch
        {
            Hl7DispatchOutcome.Acknowledged or Hl7DispatchOutcome.Sent
                or Hl7DispatchOutcome.AlreadyDelivered => ExitCodes.Success,
            Hl7DispatchOutcome.Refused => ExitCodes.GenerationFailed,
            _ => ExitCodes.TransmissionFailed
        };
    }

    private static bool TryReadProfile(string path, out Hl7Profile? profile)
    {
        profile = null;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Console.Error.WriteLine($"error: there is no profile at '{path}'.");
            return false;
        }

        ProfileReadResult result = Hl7ProfileReader.Read(File.ReadAllText(path));

        foreach (ProfileIssue issue in result.Issues)
        {
            Console.Error.WriteLine($"  {issue.Severity}: {issue.Location}: {issue.Message}");
        }

        if (!result.IsUsable)
        {
            Console.Error.WriteLine("error: the profile cannot be used.");
            return false;
        }

        profile = result.Profile;
        return true;
    }

    private static bool TryReadTarget(string target, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        string[] parts = (target ?? string.Empty).Split(':');

        if (parts.Length != 2 || parts[0].Length == 0 || !int.TryParse(parts[1], out port))
        {
            Console.Error.WriteLine("error: --to takes a host and a port, as host:port.");
            return false;
        }

        host = parts[0];
        return true;
    }

    /// <summary>
    /// Builds the subject from the command line, optionally on top of invented values.
    /// </summary>
    /// <remarks>
    /// The invented names are deliberately absurd. A message that escapes into a real
    /// system during commissioning should be recognisable as a test by anybody who reads
    /// it, rather than looking like a patient nobody can find.
    /// </remarks>
    private static MessageSubject BuildSubject(IReadOnlyList<string> values, bool testData)
    {
        var subject = new MessageSubject();

        if (testData)
        {
            subject = subject
                .With("patient.id", "TEST0000001")
                .With("patient.familyName", "ZZZTEST")
                .With("patient.givenName", "Donotuse")
                .With("patient.dateOfBirth", "1970-05-21T00:00:00")
                .With("patient.sex", "Other")
                .With("patient.sexCode", "O")
                .With("study.accessionNumber", "TESTACC001")
                .With("study.description", "Interface test, not a real study")
                .With("study.instanceUid", "1.2.826.0.1.3680043.8.498.99999999999999999999")
                .With("document.title", "Interface test")
                .With("now", DateTimeOffset.Now.ToString("o"));
        }

        foreach (string pair in values)
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                Console.Error.WriteLine($"  warning: '--set {pair}' is not name=value and was ignored.");
                continue;
            }

            subject = subject.With(pair[..separator], pair[(separator + 1)..]);
        }

        return subject;
    }
}
