using System.Globalization;
using System.Text;
using FellowOakDicom;
using FellowOakDicom.Log;
using FellowOakDicom.Network;
using PdfToDicom.TestWorklistServer;

// ---------------------------------------------------------------------------- Arguments

int port = 1070;
string aeTitle = "TESTWL";

for (int i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--port" or "-p" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out port))
            {
                Console.Error.WriteLine("The port must be a number.");
                return 2;
            }

            break;

        case "--aet" or "-a" when i + 1 < args.Length:
            aeTitle = args[++i];
            break;

        case "--help" or "-h" or "/?":
            PrintUsage();
            return 0;

        default:
            Console.Error.WriteLine($"Unknown option '{args[i]}'. Run with --help.");
            return 2;
    }
}

// ---------------------------------------------------------------------------- Run

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine();
Console.WriteLine("  Test Modality Worklist server");
Console.WriteLine("  =============================");
Console.WriteLine();
Console.WriteLine($"  AE Title : {aeTitle}");
Console.WriteLine($"  Port     : {port}");
Console.WriteLine($"  Entries  : {SampleWorklist.Items.Count}, scheduled for today");
Console.WriteLine();
Console.WriteLine("  THIS IS FOR TESTING. It invents patients and holds nothing real.");
Console.WriteLine();
Console.WriteLine("  Point the application's worklist settings at this machine on the");
Console.WriteLine("  address and port above, then press Search.");
Console.WriteLine();
Console.WriteLine("  Press Ctrl+C to stop.");
Console.WriteLine();

using IDicomServer server = DicomServerFactory.Create<WorklistProvider>(port, userState: aeTitle);

var stopping = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine();
    Console.WriteLine("  Stopping.");
    stopping.TrySetResult();
};

await stopping.Task;
return 0;

static void PrintUsage()
{
    Console.WriteLine();
    Console.WriteLine("  testworklistserver [--port <n>] [--aet <title>]");
    Console.WriteLine();
    Console.WriteLine("    --port, -p   Port to listen on. Default 1070, which needs no privileges.");
    Console.WriteLine("    --aet,  -a   AE Title to answer as. Default TESTWL.");
    Console.WriteLine();
    Console.WriteLine("  Serves a handful of invented scheduled procedure steps so the worklist");
    Console.WriteLine("  feature can be exercised without a real hospital system. Some of the names");
    Console.WriteLine("  are deliberately longer than DICOM allows, and several carry accents.");
    Console.WriteLine();
}
