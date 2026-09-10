# Licensing Workflow

**Audience: the copyright holder. Not for customers.**

This document explains how to switch licensing on, how to issue a code, and what a
customer sees. It assumes you have read
[Protecting Against Unauthorised Distribution](ProtectingAgainstUnauthorisedDistribution.md),
which explains where this fits and what it is and is not worth.

---

## Current state: switched ON, as of version 1.1

`LicenceService.PublisherPublicKey` carries the publisher key. **Every build from 1.1
onward requires a valid licence code before it will transmit to a PACS.**

Two automated tests guard this: one asserts the constant is not empty and that the build
reports itself as enforcing, the other that the key genuinely imports as a P-256 public
key. That second test matters more than it looks. A corrupted key does not raise; by
design it degrades to "not enforced" and logs, so that our own packaging mistake never
stops a hospital transmitting. The cost of that safety property is that a broken key would
silently ship a build enforcing nothing. The test is what notices.

**Switching it off again is one edit.** Set the constant back to an empty string and
rebuild: the whole subsystem goes inert, the Licence tab says codes are not checked, and
the workflow withholds nothing. That path is still fully tested, so it is a safe thing to
do in a hurry if licensing ever needs withdrawing.

> **Anyone running a version 1.0 build is unaffected.** Codes are checked only by builds
> that carry the key. Upgrading someone to 1.1 without giving them a code will stop them
> transmitting, so issue the code first.

---

## 0. Get the tool

Prebuilt executables are committed at [`dist/licence-tool/`](../dist/licence-tool/), so
nothing has to be installed and no development environment is needed.

**`pdftodicom-licence-win-x86.exe` runs on every Windows this software targets**: 32 bit,
64 bit, Windows Server, and Windows 10 or 11 on ARM through the built in x86 emulation.
Native x64 and ARM64 builds are there too, but the tool runs for milliseconds, so there is
no practical difference.

Every command below can be run either against that executable or from source with
`dotnet run --project src\PdfToDicom.LicenceTool --`. They are the same program.

---

## 1. Generate your key pair, once, ever

```powershell
.\pdftodicom-licence-win-x86.exe keygen
```

This writes `licence-private.key` and prints the matching public key.

> **The private key is the whole system.**
>
> - Anyone holding it can issue licences that your application will accept.
> - If you lose it, every licence already issued becomes unissuable again: you must
>   generate a new pair, rebuild, and reissue every customer's code.
> - **Never commit it.** The repository's `.gitignore` excludes `*.key` and
>   `licence-private.key`, but that is a safety net, not a policy. Keep it in a password
>   manager or an offline backup, in at least two places.

Do this once and never again.

---

## 2. Switch licensing on

**Already done for the current key.** This section is kept for the day you rotate.

Paste the printed line into `src/PdfToDicom.Core/Licensing/LicenceService.cs`:

```csharp
public const string PublisherPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...";
```

Rebuild. From this build onward, the application requires a licence code before it will
transmit to a PACS.

**Every build you ship must carry the same public key**, or codes issued for one build will
be rejected by another. **Rotating the key invalidates every licence already issued**, so
reissue them all in the same sitting, and remember that anyone still running the previous
build keeps working on their old code.

---

## 3. Issue a code for a customer

```powershell
# Perpetual, twenty workstations
dotnet run --project src\PdfToDicom.LicenceTool -- issue `
    --licensee "Hospital de Exemplo" --seats 20

# Annual subscription
dotnet run --project src\PdfToDicom.LicenceTool -- issue `
    --licensee "Clinica Norte" --days 365

# Tied to one workstation, using the identifier the customer read out to you
dotnet run --project src\PdfToDicom.LicenceTool -- issue `
    --licensee "Clinica Sul" --machine A1B2-C3D4-E5F6-7890

# Thirty day trial
dotnet run --project src\PdfToDicom.LicenceTool -- issue `
    --licensee "Hospital de Avaliacao" --days 30
```

The output is one line of text beginning `PDFDCM1.`. Email it to the customer. It contains
no secret: it is signed, not encrypted, and it is worthless on a machine it is not licensed
for.

### The options

| Option | Effect |
|---|---|
| `--licensee` | Who the licence is for. Required. Shown to the customer in Settings. |
| `--seats` | Workstation count. **Recorded, not enforced.** An offline application cannot count installations. Its value is contractual and evidential. |
| `--expires` | Expiry as `YYYY-MM-DD`. |
| `--days` | Expiry as a number of days from today. |
| `--machine` | Bind to one workstation. Pass the identifier the customer quoted, or `this` for the machine you are sitting at. |
| `--out` | Also write the code to a file. |

Omit both `--expires` and `--days` for a perpetual licence.

---

## 4. Check a code before you send it

```powershell
dotnet run --project src\PdfToDicom.LicenceTool -- verify --code PDFDCM1.xxxx.yyyy
```

This runs **the same code path the application runs**, so what it reports is what the
customer will see. Use it before sending a code, and whenever a customer reports that a
code does not work.

---

## What the customer does

1. Opens **Settings**, then the **Licence** tab.
2. Pastes the code into the box. The status underneath updates as they type, so a
   truncated paste is obvious immediately.
3. Presses **Save**.

If the licence is bound to a machine, they first read the identifier shown under **This
workstation** to you. It looks like `A1B2-C3D4-E5F6-7890`, is safe to send by email, and is
derived from the machine name and network adapter address. It does not identify a person.

---

## What happens when a licence is missing, wrong, or expired

The application **always opens, always converts, and always saves locally.** Only
transmission to a PACS is withheld, and the reason is stated plainly:

| Situation | Transmission | What the operator sees |
|---|---|---|
| No code entered | Withheld | Notice band, and an explanation on the run |
| Code invalid or altered | Withheld | "Not valid for this product. Check it with your supplier." |
| Code bound to another machine | Withheld | The refusal, plus **this** machine's identifier to quote |
| Expired, within 14 days | **Allowed** | "Expired on *date*. Transmission continues for a further *n* days. Please renew." |
| Expired, beyond 14 days | Withheld | The expiry date and the fact that the grace period has ended |
| Valid, expiring within 30 days | Allowed | The expiry date, so a renewal is not a surprise |
| Valid | Allowed | Nothing at all |

**This is a deliberate clinical decision, not an oversight.** A licence that stops working
at midnight, in a hospital, is a clinical incident rather than a commercial success. The
fourteen day grace period means nobody is ever cut off without warning, and withholding
only transmission means a clinician can always produce the document and get it to the
archive by another route while the commercial position is sorted out.

The same reasoning runs through the code. If the licence check itself throws, transmission
is **allowed** and the fault is logged. A bug in the licensing subsystem must never be able
to stop a report reaching a patient's record.

---

## Never distribute the generator

`src/PdfToDicom.LicenceTool/` is a publisher tool. It is in the solution so it builds and
stays working, but:

- the build scripts **do not** publish it;
- the installer **does not** include it;
- the portable package **does not** contain it.

Its source being readable does not weaken the system. Security rests entirely on the
private key, not on the tool being secret. Someone who has the tool and not the key can do
nothing with it.

---

## Reissuing a machine bound licence

Hardware gets replaced. Expect to reissue perhaps one licence in twenty each year.

1. The customer opens Settings, Licence, and reads out the new identifier.
2. You reissue with `--machine <new identifier>`.
3. They paste the new code.

If this becomes a burden, issue unbound codes and rely on the licence agreement instead.
Machine binding is a deterrent against casual copying, not a technical guarantee.

---

## Honest limits

Stated plainly, because a protection mechanism you overestimate is worse than one you
understand:

- **A capable person can decompile the application and remove the check.** This is true of
  every offline licensing scheme in any managed language. Obfuscation raises the bar and
  does not eliminate this.
- **It depends entirely on the repository staying private.** Anyone who can read the
  source can delete the check and build their own copy. The repository is private today;
  publishing it would undo the whole mechanism at a stroke.
- **Seat counts are not enforced.** An application with no network access to you cannot
  count installations. The number is contractual.
- **The machine fingerprint is not tamper proof.** It is derived from the machine name and
  a network adapter address, both of which a determined user can change.

What this system does achieve is real: an ordinary hospital IT department cannot copy the
application to twenty more workstations and have it work, and every copy in the field is
tied to a named licensee. That is the realistic goal.
