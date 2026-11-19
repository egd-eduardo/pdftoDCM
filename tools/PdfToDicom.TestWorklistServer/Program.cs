using System.Globalization;
using System.Text;
using FellowOakDicom;
using FellowOakDicom.Log;
using FellowOakDicom.Network;
using PdfToDicom.TestWorklistServer;

// ---------------------------------------------------------------------------- Arguments

int port = 1070;
string aeTitle = "TESTWL";
int randomCount = SampleWorklist.DefaultRandomCount;
int? seed = null;

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

        case "--random" or "-r" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out randomCount) || randomCount < 0)
            {
                Console.Error.WriteLine("The number of random entries must be zero or more.");
                return 2;
            }

            break;

        case "--seed" or "-s" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out int parsedSeed))
            {
                Console.Error.WriteLine("The seed must be a number.");
                return 2;
            }

            seed = parsedSeed;
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
// Drawn once, here, rather than per association, so the list does not change under
// somebody who is pressing Search to find out whether their worklist works.
IReadOnlyList<SampleWorklist.Item> serving = SampleWorklist.Choose(randomCount, seed);

Console.WriteLine($"  AE Title : {aeTitle}");
Console.WriteLine($"  Port     : {port}");
Console.WriteLine(
    $"  Entries  : {serving.Count}, scheduled over the past year, " +
    $"{serving.Count(i => i.DaysBeforeToday == 0)} of them today");
Console.WriteLine($"             {SampleWorklist.Awkward.Count} fixed, the ones that break something");
Console.WriteLine($"             {serving.Count - SampleWorklist.Awkward.Count} drawn at random " +
                  $"from {SampleWorklist.Pool.Count}" + (seed is null ? string.Empty : $", seed {seed}"));
Console.WriteLine();
Console.WriteLine("  THIS IS FOR TESTING. Every patient here is invented.");
Console.WriteLine();
Console.WriteLine($"  Every identifier and description carries \"{SampleWorklist.TestMarker}\", so a study");
Console.WriteLine("  generated from this list is recognisable as a test wherever it ends");
Console.WriteLine("  up. The names are left realistic on purpose, because their length and");
Console.WriteLine("  their accents are what is being tested.");
Console.WriteLine();
Console.WriteLine("  Even so, point the application at a test archive and not a live one.");
Console.WriteLine();
// Said out loud, because a list in front of somebody who then searches for today and sees
// a quarter of it needs to explain itself before they conclude the server is broken.
Console.WriteLine(
    $"  Serving {serving.Count} entr{(serving.Count == 1 ? "y" : "ies")}, " +
    $"{serving.Count(i => i.DaysBeforeToday == 0)} of them scheduled today and the rest " +
    "spread over the preceding year. Ask for every date to see them all.");
Console.WriteLine();
Console.WriteLine("  The list, oldest first:");

foreach (SampleWorklist.Item item in serving
    .OrderByDescending(i => i.DaysBeforeToday)
    .ThenBy(i => i.MinutesFromStartOfDay))
{
    string name = item.GivenName.Length == 0
        ? item.FamilyName
        : $"{item.FamilyName}, {item.GivenName}";

    Console.WriteLine(
        $"    {SampleWorklist.ScheduledDay(item, DateTime.Today):yyyy-MM-dd} " +
        $"{TimeSpan.FromMinutes(item.MinutesFromStartOfDay):hh\\:mm}  " +
        $"{item.PatientId,-14}  {item.Modality,-2}  {name}");
}

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
    Console.WriteLine("  testworklistserver [--port <n>] [--aet <title>] [--random <n>] [--seed <n>]");
    Console.WriteLine();
    Console.WriteLine("    --port,   -p   Port to listen on. Default 1070, which needs no privileges.");
    Console.WriteLine("    --aet,    -a   AE Title to answer as. Default TESTWL.");
    Console.WriteLine($"    --random, -r   How many names to draw from the pool of {SampleWorklist.Pool.Count}.");
    Console.WriteLine($"                   Default {SampleWorklist.DefaultRandomCount}. Zero serves only the fixed entries.");
    Console.WriteLine("    --seed,   -s   Fix the draw, so a run can be repeated exactly.");
    Console.WriteLine();
    Console.WriteLine("  Serves invented scheduled procedure steps so the worklist feature can be");
    Console.WriteLine("  exercised without a real hospital system. A few fixed entries are always");
    Console.WriteLine("  present because each one breaks something: two names longer than DICOM");
    Console.WriteLine("  allows, a patient with no given name, and a different modality. The rest");
    Console.WriteLine("  are drawn fresh each run from ordinary Portuguese names, accents included.");
    Console.WriteLine();
    Console.WriteLine($"  Every identifier and description carries \"{SampleWorklist.TestMarker}\", so anything");
    Console.WriteLine("  generated from this list is recognisable as a test.");
    Console.WriteLine();
}
