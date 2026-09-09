using System.Globalization;
using System.Security.Cryptography;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Licensing;

// ---------------------------------------------------------------------------------------
//  PDF to DICOM licence generator
//
//  Holds the private key that signs licence codes. Keep it, and the key file it writes,
//  away from customers and out of source control.
// ---------------------------------------------------------------------------------------

const string DefaultKeyFile = "licence-private.key";

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "keygen" => RunKeygen(args),
        "issue" => RunIssue(args),
        "verify" => RunVerify(args),
        "machine-id" => RunMachineId(),
        "help" or "--help" or "-h" => PrintUsageAndSucceed(),
        _ => Unknown(args[0])
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

// ---------------------------------------------------------------------------------------

/// <summary>
/// Creates the publisher key pair. Run once, then keep the private key safe forever:
/// losing it means every future licence must be issued under a new key, which requires
/// a new build and reissuing every licence already in the field.
/// </summary>
static int RunKeygen(string[] args)
{
    string keyFile = ValueOf(args, "--out") ?? DefaultKeyFile;

    if (File.Exists(keyFile) && !args.Contains("--force"))
    {
        Console.Error.WriteLine(
            $"error: '{keyFile}' already exists. Overwriting it would invalidate every licence " +
            "ever issued under it. Pass --force only if you are certain.");
        return 1;
    }

    using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    string privateKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
    string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    File.WriteAllText(keyFile, privateKey);

    // Restrict the file where the platform supports it. On Windows the file inherits the
    // user's profile permissions, which is already private to that account.
    TryRestrictPermissions(keyFile);

    Console.WriteLine($"Private key written to '{Path.GetFullPath(keyFile)}'.");
    Console.WriteLine();
    Console.WriteLine("  KEEP THIS FILE SECRET. Anyone holding it can issue licences.");
    Console.WriteLine("  Back it up somewhere safe. It cannot be recovered if lost.");
    Console.WriteLine("  Never commit it to source control.");
    Console.WriteLine();
    Console.WriteLine("To switch licensing on, paste the line below into");
    Console.WriteLine("src/PdfToDicom.Core/Licensing/LicenceService.cs, replacing the empty value,");
    Console.WriteLine("then rebuild:");
    Console.WriteLine();
    Console.WriteLine($"    public const string PublisherPublicKey = \"{publicKey}\";");
    Console.WriteLine();
    Console.WriteLine("Until you do that, the application ignores licence codes entirely and");
    Console.WriteLine("behaves exactly as it does today.");

    return 0;
}

/// <summary>Signs a licence code for one customer.</summary>
static int RunIssue(string[] args)
{
    string keyFile = ValueOf(args, "--key") ?? DefaultKeyFile;
    string? licensee = ValueOf(args, "--licensee");

    if (string.IsNullOrWhiteSpace(licensee))
    {
        Console.Error.WriteLine("error: --licensee is required, for example --licensee \"Hospital de Exemplo\".");
        return 1;
    }

    if (!File.Exists(keyFile))
    {
        Console.Error.WriteLine($"error: the private key '{keyFile}' was not found. Run 'keygen' first.");
        return 1;
    }

    var payload = new LicencePayload
    {
        Licensee = licensee.Trim(),
        IssuedOn = DateTime.UtcNow.Date,
        Seats = int.TryParse(ValueOf(args, "--seats"), out int seats) ? seats : 0
    };

    // Expiry: either an explicit date or a number of days from today.
    string? expires = ValueOf(args, "--expires");
    string? days = ValueOf(args, "--days");

    if (!string.IsNullOrWhiteSpace(expires))
    {
        if (!DateTime.TryParseExact(
                expires, new[] { "yyyy-MM-dd", "yyyyMMdd" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime expiryDate))
        {
            Console.Error.WriteLine($"error: '{expires}' is not a valid date. Use YYYY-MM-DD.");
            return 1;
        }

        payload.ExpiresOn = expiryDate;
    }
    else if (!string.IsNullOrWhiteSpace(days))
    {
        if (!int.TryParse(days, out int dayCount) || dayCount <= 0)
        {
            Console.Error.WriteLine($"error: '{days}' is not a positive number of days.");
            return 1;
        }

        payload.ExpiresOn = DateTime.UtcNow.Date.AddDays(dayCount);
    }

    // Machine binding: either the identifier the customer quoted, or this machine.
    string? machine = ValueOf(args, "--machine");

    if (!string.IsNullOrWhiteSpace(machine))
    {
        payload.MachineHash = string.Equals(machine, "this", StringComparison.OrdinalIgnoreCase)
            ? MachineFingerprint.CurrentHash()
            : MachineFingerprint.HashOfDisplayedId(machine);
    }

    using ECDsa key = ECDsa.Create();
    key.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(keyFile).Trim()), out _);

    byte[] payloadBytes = payload.ToBytes();
    string code = LicenceCodec.Encode(payloadBytes, LicenceCodec.Sign(payloadBytes, key));

    Console.WriteLine("Licence issued.");
    Console.WriteLine();
    Console.WriteLine($"  Licensee : {payload.Licensee}");
    Console.WriteLine($"  Issued   : {payload.IssuedOn:yyyy-MM-dd}");
    Console.WriteLine($"  Expires  : {(payload.ExpiresOn is { } e ? e.ToString("yyyy-MM-dd") : "never")}");
    Console.WriteLine($"  Seats    : {(payload.Seats > 0 ? payload.Seats.ToString() : "unstated")}");
    Console.WriteLine($"  Machine  : {(payload.IsMachineBound ? "bound" : "any")}");
    Console.WriteLine();
    Console.WriteLine("Send the customer the code below. They paste it into Settings, Licence.");
    Console.WriteLine();
    Console.WriteLine(code);
    Console.WriteLine();

    string? output = ValueOf(args, "--out");
    if (!string.IsNullOrWhiteSpace(output))
    {
        File.WriteAllText(output, code);
        Console.WriteLine($"Also written to '{Path.GetFullPath(output)}'.");
    }

    return 0;
}

/// <summary>
/// Checks a code, exactly as the application would. Useful before sending one to a
/// customer, and for confirming a complaint that a code does not work.
/// </summary>
static int RunVerify(string[] args)
{
    string? code = ValueOf(args, "--code");
    string? codeFile = ValueOf(args, "--code-file");

    if (string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(codeFile))
    {
        code = File.ReadAllText(codeFile);
    }

    if (string.IsNullOrWhiteSpace(code))
    {
        Console.Error.WriteLine("error: supply --code or --code-file.");
        return 1;
    }

    string? publicKey = ValueOf(args, "--public-key");
    string keyFile = ValueOf(args, "--key") ?? DefaultKeyFile;

    // The public half can be derived from the private key file, which saves the publisher
    // having to keep the public key to hand just to check a code.
    if (string.IsNullOrWhiteSpace(publicKey) && File.Exists(keyFile))
    {
        using ECDsa key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(keyFile).Trim()), out _);
        publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    if (string.IsNullOrWhiteSpace(publicKey))
    {
        Console.Error.WriteLine(
            "error: no public key available. Supply --public-key, or place the private key file " +
            "beside this tool.");
        return 1;
    }

    var service = new LicenceService(publicKeyOverride: publicKey);
    LicenceStatus status = service.Check(code);

    Console.WriteLine($"State        : {status.State}");
    Console.WriteLine($"Licensee     : {status.Licensee ?? "(unreadable)"}");
    Console.WriteLine($"Expires      : {(status.ExpiresOn is { } e ? e.ToString("yyyy-MM-dd") : "never")}");
    Console.WriteLine($"Seats        : {(status.Seats > 0 ? status.Seats.ToString() : "unstated")}");
    Console.WriteLine($"Machine bound: {status.IsMachineBound}");
    Console.WriteLine($"May transmit : {status.MayTransmit}");
    Console.WriteLine();
    Console.WriteLine(status.Message);

    return status.State is LicenceState.Valid or LicenceState.ExpiringSoon ? 0 : 1;
}

/// <summary>Prints this machine's identifier, for issuing a bound licence locally.</summary>
static int RunMachineId()
{
    Console.WriteLine(MachineFingerprint.Current);
    return 0;
}

// ---------------------------------------------------------------------------------------

static string? ValueOf(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

static void TryRestrictPermissions(string path)
{
    try
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
    catch
    {
        // Best effort. The warning printed to the console is the real safeguard.
    }
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"error: unknown command '{command}'.");
    Console.Error.WriteLine();
    PrintUsage();
    return 1;
}

static int PrintUsageAndSucceed()
{
    PrintUsage();
    return 0;
}

static void PrintUsage() => Console.WriteLine(
    """
    pdftodicom-licence - issue licence codes for PDF to DICOM

      THIS TOOL IS FOR THE PUBLISHER ONLY. It signs licences with a private key.
      Never distribute it, and never commit the key file.

    COMMANDS

      keygen        Create the publisher key pair. Run once, then keep the key safe.
        --out <file>          Where to write the private key. Default licence-private.key
        --force               Overwrite an existing key file. Invalidates every licence
                              already issued.

      issue         Sign a licence code for a customer.
        --licensee <name>     Who the licence is for. Required.
        --key <file>          Private key file. Default licence-private.key
        --seats <n>           Number of workstations covered. Recorded, not enforced.
        --expires <date>      Expiry as YYYY-MM-DD.
        --days <n>            Expiry as a number of days from today.
        --machine <id>        Bind to one workstation. Pass the identifier the customer
                              quoted from Settings, or "this" for this machine.
        --out <file>          Also write the code to a file.

      verify        Check a code exactly as the application would.
        --code <code>         The code to check.
        --code-file <file>    Read the code from a file instead.
        --key <file>          Private key, from which the public half is derived.
        --public-key <b64>    Use this public key instead.

      machine-id    Print this machine's identifier.

    EXAMPLES

      pdftodicom-licence keygen
      pdftodicom-licence issue --licensee "Hospital de Exemplo" --seats 20
      pdftodicom-licence issue --licensee "Clinica Norte" --days 365 --machine A1B2-C3D4-E5F6-7890
      pdftodicom-licence verify --code PDFDCM1.xxxx.yyyy

    NOTES

      A licence with no expiry is perpetual. After an expiry date passes there is a grace
      period of 14 days during which transmission still works, so that a site is never cut
      off without warning.

      Licensing stays switched off until the public key from keygen is compiled into the
      application. Until then every licence code is ignored and nothing is restricted.
    """);
