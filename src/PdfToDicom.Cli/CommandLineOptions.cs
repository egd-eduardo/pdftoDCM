using System.Globalization;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.Cli;

/// <summary>
/// The command line arguments, parsed and validated.
/// </summary>
/// <remarks>
/// Parsing is written by hand rather than taken from a package. The option set is small
/// and stable, and avoiding another dependency keeps the published tool small and its
/// licence position simple.
/// </remarks>
public sealed class CommandLineOptions
{
    /// <summary>The verb the user asked for.</summary>
    public string Command { get; private set; } = "help";

    /// <summary>
    /// Path of the first file to convert, kept for the commands that operate on exactly
    /// one file: inspect and extract.
    /// </summary>
    public string? PdfPath => SourcePaths.FirstOrDefault();

    /// <summary>
    /// Every file to convert, in the order they were given.
    /// </summary>
    /// <remarks>
    /// Repeating the option converts several files in one run. That is what makes study
    /// grouping usable from a script: the association between a patient and their study
    /// is held in memory and deliberately not persisted, so it lasts for one run of the
    /// tool and no longer.
    /// </remarks>
    public List<string> SourcePaths { get; } = new();

    /// <summary>Patient and order information assembled from the arguments.</summary>
    public PatientDemographics Demographics { get; } = new();

    /// <summary>Explicit configuration file, when one was supplied.</summary>
    public string? ConfigurationPath { get; private set; }

    /// <summary>Output path for the extract command.</summary>
    public string? OutputPath { get; private set; }

    /// <summary>Names of the destinations to use. Empty means every enabled destination.</summary>
    public List<string> DestinationNames { get; } = new();

    /// <summary>
    /// File name patterns supplied on the command line, which replace the configured ones.
    /// </summary>
    /// <remarks>
    /// So a format can be tried before anybody writes it into a configuration file, which
    /// is the whole point of check-names.
    /// </remarks>
    public List<string> Patterns { get; } = new();

    /// <summary>File names to try against the patterns.</summary>
    public List<string> NamesToCheck { get; } = new();

    /// <summary>
    /// Read the patient information from the file's own name rather than from options.
    /// </summary>
    public bool FromFilename { get; private set; }

    /// <summary>
    /// Try splitting a run-together name such as JohnDoe, overriding the configuration.
    /// </summary>
    public bool SplitNames { get; private set; }

    /// <summary>
    /// Width to pad a patient number read from a file name to, overriding the
    /// configuration. Null leaves the configured width alone.
    /// </summary>
    public int? PadPatientIdTo { get; private set; }

    /// <summary>Generate and save the object without transmitting it.</summary>
    public bool NoSend { get; private set; }

    /// <summary>Print the generated tags before sending.</summary>
    public bool ShowTags { get; private set; }

    /// <summary>Include debug level entries in the log.</summary>
    public bool Verbose { get; private set; }

    /// <summary>Errors encountered while parsing.</summary>
    public List<string> ParseErrors { get; } = new();

    /// <summary>Parses the supplied arguments.</summary>
    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();

        if (args.Length == 0)
        {
            return options;
        }

        options.Command = args[0].TrimStart('-').ToLowerInvariant();

        for (int i = 1; i < args.Length; i++)
        {
            string argument = args[i];

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                options.ParseErrors.Add($"Unexpected argument '{argument}'.");
                continue;
            }

            string name = argument[2..].ToLowerInvariant();

            // Supports both "--name value" and "--name=value".
            string? value = null;
            int equalsIndex = name.IndexOf('=');
            if (equalsIndex >= 0)
            {
                value = name[(equalsIndex + 1)..];
                name = name[..equalsIndex];

                // The value must keep its original casing, which the lowering above lost.
                value = argument[(2 + equalsIndex + 1)..];
            }

            switch (name)
            {
                case "verbose":
                    options.Verbose = true;
                    continue;
                case "no-send":
                    options.NoSend = true;
                    continue;
                case "show-tags":
                    options.ShowTags = true;
                    continue;
                case "from-filename":
                    options.FromFilename = true;
                    continue;
                case "split-names":
                    options.SplitNames = true;
                    continue;
            }

            value ??= i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : null;

            if (value is null)
            {
                options.ParseErrors.Add($"The option '--{name}' requires a value.");
                continue;
            }

            options.ApplyOption(name, value);
        }

        return options;
    }

    private void ApplyOption(string name, string value)
    {
        switch (name)
        {
            case "pdf":
            case "image":
            case "file":
                // One list behind three names. The workflow decides what each file
                // actually is by reading its content, so which name was typed changes
                // nothing; "--image report.pdf" converts a PDF quite happily.
                SourcePaths.Add(value);
                break;
            case "patient-id":
                Demographics.PatientId = value;
                break;
            case "family-name":
            case "surname":
                Demographics.FamilyName = value;
                break;
            case "given-name":
                Demographics.GivenName = value;
                break;
            case "middle-name":
                Demographics.MiddleName = value;
                break;
            case "birth-date":
            case "dob":
                if (TryParseDate(value, out DateTime birthDate))
                {
                    Demographics.DateOfBirth = birthDate;
                }
                else
                {
                    ParseErrors.Add(
                        $"'{value}' is not a valid date. Use YYYY-MM-DD or YYYYMMDD.");
                }

                break;
            case "sex":
                Demographics.Sex = PatientSexExtensions.FromDicomValue(value);
                break;
            case "accession":
                Demographics.AccessionNumber = value;
                break;
            case "referring-physician":
                Demographics.ReferringPhysicianName = value;
                break;
            case "study-description":
                Demographics.StudyDescription = value;
                break;
            case "series-description":
                Demographics.SeriesDescription = value;
                break;
            case "document-title":
                Demographics.DocumentTitle = value;
                break;
            case "study-uid":
                Demographics.ExistingStudyInstanceUid = value;
                break;
            case "study-id":
                Demographics.StudyId = value;
                break;
            case "destination":
                DestinationNames.Add(value);
                break;
            case "pattern":
                Patterns.Add(value);
                break;
            case "pad-patient-id":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int padWidth) &&
                    padWidth >= 0 && padWidth <= DicomValueRules.MaxLongStringLength)
                {
                    PadPatientIdTo = padWidth;
                }
                else
                {
                    ParseErrors.Add(
                        $"'--pad-patient-id {value}' is not a width between 0 and " +
                        $"{DicomValueRules.MaxLongStringLength}. 0 means do not pad.");
                }

                break;
            case "name":
                NamesToCheck.Add(value);
                break;
            case "config":
                ConfigurationPath = value;
                break;
            case "out":
                OutputPath = value;
                break;
            default:
                ParseErrors.Add($"Unknown option '--{name}'.");
                break;
        }
    }

    /// <summary>Accepts both the ISO form and the compact DICOM form of a date.</summary>
    private static bool TryParseDate(string value, out DateTime result) =>
        DateTime.TryParseExact(
            value,
            new[] { "yyyy-MM-dd", "yyyyMMdd", "dd/MM/yyyy" },
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out result);

    /// <summary>The help text shown for --help and for an unrecognised verb.</summary>
    public static string HelpText =>
        """
        pdftodicom - convert a PDF or an image into a DICOM object and send it to a PACS

        USAGE
          pdftodicom <command> [options]

        COMMANDS
          send        Convert a file and transmit it to the configured destination(s).
          generate    Convert a file and save it locally, without transmitting.
          echo        Send a C-ECHO verification to the configured destination(s).
          worklist    Query the configured Modality Worklist and print the result.
          check-names Try the file name patterns against names, converting nothing.
          inspect     Print the DICOM tags of an existing .dcm file.
          extract     Recover the original PDF from a .dcm file, byte for byte.
          config      Show the effective configuration and where it is stored.
          help        Show this text.

        FILE NAME IMPORT OPTIONS
          --from-filename              On send and generate, read the patient information
                                       from the file's own name using the configured
                                       patterns, instead of from the options below.
          --pattern <template>         For check-names, a pattern to try instead of the
                                       configured ones. May be repeated. Fields are
                                       {PatientId} {Name} {FamilyName} {GivenName}
                                       {AccessionNumber} {DateOfBirth} {StudyDescription}
                                       and {*} for a part to skip. A pattern starting
                                       "regex:" is a regular expression with named groups.
          --name <text>                For check-names, a name to try. May be repeated.
                                       Names are also read from standard input, one per
                                       line, so a folder listing can be piped in.
          --split-names                Try splitting a run-together name such as JohnDoe
                                       at its capitals. A guess, so it is off unless asked
                                       for and what it did is always reported.
          --pad-patient-id <n>        Left-pad a patient number read from a file name
                                       with zeros to n digits, overriding the configured
                                       width. 0 does not pad. Only a value that is
                                       entirely digits is padded, and a longer one is
                                       never shortened.

        WORKLIST OPTIONS
          --patient-id <value>         Narrow the query to this Patient ID. Wildcards are
                                       allowed: * matches any run of characters and ?
                                       matches one. Omit it to match every patient.
          --surname <value>            Narrow the query to this family name, with the
                                       same wildcards.

        PATIENT OPTIONS (mandatory for send and generate)
          --file <path>                The file to convert. May be repeated to convert
                                       several files in one run, which is what lets study
                                       grouping put them in the same study. A PDF becomes an Encapsulated
                                       PDF object; a JPEG, PNG, TIFF, BMP, GIF, WebP or
                                       TGA becomes Secondary Capture image object(s).
                                       --pdf and --image are accepted as the same option,
                                       because the file's content decides, not its name.
                                       A multi page TIFF produces one instance per page,
                                       all in one series.
          --patient-id <value>         Patient ID.
          --surname <value>            Patient surname.
          --given-name <value>         Patient given name.
          --dob <YYYY-MM-DD>           Date of birth.
          --accession <value>          Accession number.

        OPTIONAL
          --sex <M|F|O>                Patient sex.
          --middle-name <value>        Patient middle name.
          --referring-physician <name> Referring physician.
          --study-description <text>   Study description.
          --series-description <text>  Series description.
          --document-title <text>      Document title.
          --study-uid <uid>            File into an existing study instead of creating one.
                                       Overrides the study grouping setting.
          --study-id <value>           Study ID.
          --destination <name>         Use only this destination. May be repeated.
          --config <path>              Use a specific configuration file.
          --out <path>                 Where extract writes the recovered PDF.
          --no-send                    Generate and save without transmitting.
          --show-tags                  Print the generated DICOM tags.
          --verbose                    Include debug entries in the log.

        EXAMPLES
          pdftodicom echo
          pdftodicom worklist
          pdftodicom check-names --pattern "{PatientId}_{Name}_{AccessionNumber}_{DateOfBirth}" \\
                                 --name "012345_Ana Silva_ACC001_19700521.pdf"
          ls *.pdf | pdftodicom check-names
          pdftodicom send --pdf "012345_Ana Silva_ACC001_19700521.pdf" --from-filename
          pdftodicom worklist --patient-id P0000004
          pdftodicom worklist --surname 'SILVA*'
          pdftodicom generate --pdf report.pdf --patient-id 12345 --surname Silva \
                              --given-name Maria --dob 1970-05-21 --accession ACC001
          pdftodicom send --pdf report.pdf --patient-id 12345 --surname Silva \
                          --given-name Maria --dob 1970-05-21 --accession ACC001
          pdftodicom send --image scan.tiff --patient-id 12345 --surname Silva \
                          --given-name Maria --dob 1970-05-21 --accession ACC001
          pdftodicom inspect --pdf out/12345_ACC001_1234.dcm
          pdftodicom extract --pdf out/12345_ACC001_1234.dcm --out recovered.pdf

        EXIT CODES
          0  success
          1  invalid arguments or invalid patient information
          2  the DICOM object could not be generated or failed validation
          3  the object was generated but at least one transmission failed
        """;
}
