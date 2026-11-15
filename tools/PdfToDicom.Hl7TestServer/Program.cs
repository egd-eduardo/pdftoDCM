using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PdfToDicom.Interop.Hl7V2.Model;
using PdfToDicom.Interop.Hl7V2.Transport;

namespace PdfToDicom.Hl7TestServer;

/// <summary>
/// How this receiver answers. Every one of these is something a real one has done.
/// </summary>
internal enum Behaviour
{
    /// <summary>Answer AA, which is what a working interface does.</summary>
    Accept,

    /// <summary>Answer AE, as a receiver does when it understood and could not act.</summary>
    Error,

    /// <summary>Answer AR, a flat refusal.</summary>
    Reject,

    /// <summary>Answer nothing at all, which is what a profile declaring NE expects.</summary>
    Silent,

    /// <summary>Read the message and hang up, which is what a crashing receiver does.</summary>
    HangUp,

    /// <summary>Answer with bytes that are not a frame, which a misconfigured one does.</summary>
    Garbage,

    /// <summary>Answer about a different message, which crossed conversations produce.</summary>
    WrongControlId
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        int port = Argument(args, "--port", 2575);
        int delaySeconds = Argument(args, "--delay", 0);
        Behaviour behaviour;

        try
        {
            behaviour = BehaviourFrom(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        Console.WriteLine($"HL7 test receiver listening on port {port}, answering: {behaviour}.");
        Console.WriteLine("This is a test tool. It is never part of what a site receives.");
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();

        using var stopping = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        try
        {
            while (!stopping.IsCancellationRequested)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(stopping.Token);
                await HandleAsync(client, behaviour, delaySeconds, stopping.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Stopped.");
        }
        finally
        {
            listener.Stop();
        }

        return 0;
    }

    private static async Task HandleAsync(
        TcpClient client, Behaviour behaviour, int delaySeconds, CancellationToken token)
    {
        await using NetworkStream stream = client.GetStream();

        byte[] payload;

        try
        {
            payload = await MllpFraming.ReadFrameAsync(
                stream, maximumPayloadBytes: 32 * 1024 * 1024, token);
        }
        catch (MllpProtocolException ex)
        {
            Console.WriteLine($"  the sender did not send a valid frame: {ex.Message}");
            return;
        }

        string text = Encoding.UTF8.GetString(payload);

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {payload.Length} bytes");

        foreach (string line in text.Split('\r', StringSplitOptions.RemoveEmptyEntries))
        {
            // Truncated, because an encapsulated document is megabytes of base64 and
            // nobody reading a console wants it.
            Console.WriteLine("  " + (line.Length > 160 ? line[..160] + " ..." : line));
        }

        string controlId = ControlIdOf(text);

        if (delaySeconds > 0)
        {
            Console.WriteLine($"  waiting {delaySeconds}s before answering");
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
        }

        switch (behaviour)
        {
            case Behaviour.Silent:
                Console.WriteLine("  answering nothing");
                return;

            case Behaviour.HangUp:
                Console.WriteLine("  hanging up");
                return;

            case Behaviour.Garbage:
                Console.WriteLine("  answering with something that is not a frame");
                await stream.WriteAsync(Encoding.ASCII.GetBytes("thanks very much"), token);
                return;
        }

        string code = behaviour switch
        {
            Behaviour.Error => "AE",
            Behaviour.Reject => "AR",
            _ => "AA"
        };

        string answered = behaviour == Behaviour.WrongControlId ? "SOMEBODY-ELSE" : controlId;
        string note = behaviour == Behaviour.Error ? "Test receiver was told to answer AE" : string.Empty;

        string acknowledgement =
            "MSH|^~\\&|HL7TESTSERVER|TEST|PDFTODICOM|TEST|" +
            DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) +
            $"||ACK|{Guid.NewGuid():N}|P|2.4\r" +
            $"MSA|{code}|{answered}|{note}";

        Console.WriteLine($"  answering {code} for '{answered}'");

        await stream.WriteAsync(MllpFraming.Frame(Encoding.UTF8.GetBytes(acknowledgement)), token);
        await stream.FlushAsync(token);
    }

    /// <summary>
    /// Reads MSH-10 out of what arrived, so the acknowledgement answers the right message.
    /// </summary>
    /// <remarks>
    /// Uses the product's own parser rather than splitting on bars. A test receiver that
    /// cannot read what it is sent is not much of a test, and if the parser is wrong this
    /// is one more place that says so.
    /// </remarks>
    private static string ControlIdOf(string text)
    {
        try
        {
            return Hl7Message.Parse(text)["MSH-10"];
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static Behaviour BehaviourFrom(string[] args)
    {
        string requested = Argument(args, "--answer", "accept").ToLowerInvariant();

        return requested switch
        {
            "error" or "ae" => Behaviour.Error,
            "reject" or "ar" => Behaviour.Reject,
            "silent" or "none" => Behaviour.Silent,
            "hangup" => Behaviour.HangUp,
            "garbage" => Behaviour.Garbage,
            "wrong-control-id" => Behaviour.WrongControlId,
            "accept" or "aa" => Behaviour.Accept,

            // Not a silent fallback to Accept. A mistyped behaviour that quietly answers
            // every message with AA would tell somebody commissioning an interface that
            // their sender handles rejections correctly, having never sent one.
            _ => throw new ArgumentException(
                $"'{requested}' is not a behaviour. Use accept, error, reject, silent, " +
                "hangup, garbage or wrong-control-id.")
        };
    }

    private static string Argument(string[] args, string name, string fallback)
    {
        int index = Array.IndexOf(args, name);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }

    private static int Argument(string[] args, string name, int fallback) =>
        int.TryParse(Argument(args, name, string.Empty), out int value) ? value : fallback;

    private static void PrintUsage()
    {
        Console.WriteLine("""
        hl7testserver - an HL7 receiver for testing, and only for testing

        USAGE
          hl7testserver [--port 2575] [--answer accept] [--delay 0]

        ANSWERS
          accept            AA, what a working interface does
          error             AE, understood and could not act on it
          reject            AR, a flat refusal
          silent            nothing at all, which a profile declaring NE expects
          hangup            read the message and close the connection
          garbage           bytes that are not a frame
          wrong-control-id  an acknowledgement about a different message

        --delay waits that many seconds before answering, for testing a timeout.

        This tool is never part of what a site receives.
        """);
    }
}
