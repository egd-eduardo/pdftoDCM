# Protecting PDF to DICOM Against Unauthorised Distribution

**For the copyright holder · Version 1.0**

---

## The honest starting point

**Software that runs on someone else's computer cannot be made uncopyable.** Any mechanism
you add runs on hardware the other party controls, so a sufficiently determined and
capable person can defeat it. Anyone who tells you otherwise is selling something.

What you *can* do is realistic and worthwhile:

- make casual copying inconvenient enough that it does not happen by accident,
- make unauthorised use **detectable** and **attributable**,
- make it **legally actionable**, which is what actually recovers money.

The measures below are ordered by return on effort. The first two cost almost nothing and
do most of the work. The later ones cost real money or real complexity and add
proportionally less.

---

## Layer 1 — Legal foundation (do this first, it costs nothing)

This is the single highest value step, and it is already done in this repository.

| Item | Status | Purpose |
|---|---|---|
| `LICENSE` at the repository root | **Added** | Without an explicit licence, your rights are ambiguous in practice and awkward to enforce. This states them plainly. |
| Copyright in assembly metadata | **Added** | `Copyright (c) 2026 Eduardo Dias` is compiled into the executable and visible in the file's Properties dialog in Windows. |
| EULA shown by the installer | **Added** | The user must accept it before installation. Acceptance is what turns the licence from a notice into an agreement. |
| Publisher name in the installer | **Added** | `Eduardo Dias` |

**Why this matters more than any technical measure.** If a hospital deploys your software
on two hundred workstations having paid for twenty, the remedy is a letter from a lawyer,
not a clever anti-piracy routine. The letter only works if the licence terms are
unambiguous and were accepted. Institutions, unlike individuals, almost always comply once
the position is clear, because their procurement and audit obligations require it.

**Recommended next step:** have a Portuguese lawyer review `LICENSE` before you sell a
single copy. The text provided is a sound starting point, but it is not legal advice, and
the medical context makes the liability clauses worth a professional eye.

---

## Layer 2 — Code signing (buy this before you distribute anything)

An Authenticode signature does three things:

1. **Proves the file came from you.** The signature names you as publisher.
2. **Proves it has not been altered.** Any modification, including patching out a licence
   check, invalidates the signature.
3. **Removes the SmartScreen warning** your users currently see. This alone justifies the
   cost: an unsigned installer looks untrustworthy to hospital IT, who will often simply
   refuse it.

| Certificate type | Approximate cost | Notes |
|---|---|---|
| **OV** (Organisation Validated) | 200 to 400 EUR per year | Reputation with SmartScreen builds over time |
| **EV** (Extended Validation) | 300 to 600 EUR per year | Immediate SmartScreen reputation, requires a hardware token |

Since June 2023 all code signing certificates require the private key to be held on a
hardware token or in a cloud HSM, so plan for that.

Signing is already documented in `TechnicalDocumentation.md` under Deployment. Sign the
executable **before** building the installer, then sign the installer.

> **This does not stop copying.** It stops *tampering* and it establishes provenance. Those
> are different problems, and both are worth solving.

---

## Layer 3 — Licence keys with machine binding (the practical mechanism)

This is what actually limits how many workstations run your software.

**The constraint that shapes the design:** this application must work entirely offline
except for DICOM transmission. Online activation is therefore unavailable. The right
answer is a **signed offline licence file**.

### How it works

```
  You (once)                          The customer's workstation
  ─────────────────────               ────────────────────────────────────
  Generate an ECDSA key pair
  Keep the private key secret
  Embed the public key in the app  ──►  Public key compiled into the binary

  Customer buys a 20 seat licence
  You issue licence.lic containing:
     customer name
     seat count
     expiry date (optional)
     allowed machine fingerprints
     ECDSA signature over all of it
                                   ──►  App reads licence.lic on startup
                                        Verifies the signature with the public key
                                        Compares the machine fingerprint
                                        Refuses to run if either fails
```

### Why it is reasonably robust

- The customer **cannot forge a licence**, because they do not have your private key.
- The customer **cannot edit an existing licence**, because any change breaks the
  signature.
- A licence copied to an unlicensed machine **fails the fingerprint check**.
- It works **completely offline**, which the clinical requirement demands.

### Its honest limits

- A capable person can decompile the .NET assembly and remove the check entirely. Layer 4
  raises that bar; nothing eliminates it.
- Machine fingerprints change when hardware is replaced, so you need a re-issue process.
  Base the fingerprint on something stable, for example the Windows machine GUID plus the
  motherboard serial, and expect perhaps five percent of licences to need re-issuing each
  year.

### This is now built

Layer 3 is implemented and in the repository. It differs from the sketch above in one
respect: there is **no licence file**. The licence is a single code the customer pastes
into Settings, which removes an entire class of support call about a file in the wrong
folder.

| Part | Where |
|---|---|
| `ILicenceService`, `LicenceState`, `LicenceStatus` | `src/PdfToDicom.Core/Abstractions/ILicenceService.cs` |
| Signing, verification and the code format | `src/PdfToDicom.Core/Licensing/` |
| Workstation fingerprint | `src/PdfToDicom.Core/Licensing/MachineFingerprint.cs` |
| Transmission gate | `src/PdfToDicom.Core/Services/ConversionWorkflowService.cs` |
| Licence tab and notice band | `SettingsWindow.xaml`, `MainWindow.xaml` |
| The generator, **publisher only** | `src/PdfToDicom.LicenceTool/` |
| Prebuilt generators, **publisher only** | `dist/licence-tool/` |

**It is switched off.** `LicenceService.PublisherPublicKey` is blank in the repository, and
a build with a blank key enforces nothing whatsoever: the Licence tab states plainly that
codes are not checked, the notice band never appears, and the workflow is byte for byte
the workflow that shipped before licensing existed. A test asserts that the constant is still blank, so the feature
cannot be switched on by accident.

See **[the licensing workflow](LicensingWorkflow.md)** for how to switch it on and issue
codes.

### Grace and failure behaviour matters clinically

Whatever you build, decide deliberately what happens when a licence is missing, expired or
invalid. In a hospital this is not a neutral choice.

**Recommended:** the application still opens, still converts, still saves locally, but
**refuses to transmit** and displays a prominent licence warning. A hard refusal to start
means a ward cannot send a report at two in the morning because a licence expired at
midnight, and that turns your commercial protection into a clinical incident. A soft
degradation protects your revenue without ever standing between a clinician and a patient
record.

---

## Layer 4 — Obfuscation (optional, modest value)

.NET compiles to intermediate language, which decompiles almost perfectly with free tools
such as ILSpy or dnSpy. Anyone can read your source in minutes.

Obfuscators rename symbols, encrypt strings and flatten control flow:

| Tool | Cost | Notes |
|---|---|---|
| **ConfuserEx 2** | Free | Community maintained, effective, occasional compatibility work needed |
| **Eazfuscator.NET** | Around 400 EUR | Commercial, good WPF support |
| **Dotfuscator** | Community edition free | Ships with Visual Studio, limited features |

**Two warnings specific to this application.** WPF resolves types named in XAML by
reflection, so an obfuscator must be configured to exclude those types or the interface
breaks at runtime. And obfuscation makes crash logs unreadable unless you keep the mapping
file for every release you ship.

**My assessment:** worth doing once you have paying customers and a licence check worth
protecting. Not worth doing before that.

> Native AOT compilation would remove the IL entirely, which is stronger than any
> obfuscator. **It is not available here**, because WPF does not support AOT. Do not let
> anyone tell you otherwise.

---

## Layer 5 — Controlled distribution and watermarking

Cheap, and surprisingly effective at answering *"who leaked it?"*.

- **Do not publish binaries publicly.** Remove the executable from the repository once you
  begin selling, and distribute through a channel you control.
- **Make the repository private**, or move the source to a private repository and keep only
  documentation public.
- **Build per customer.** Embed a customer identifier in the assembly metadata, so any copy
  that surfaces can be traced to the customer it was issued to. This is the single most
  useful deterrent against a customer redistributing to a sister hospital, because they
  know it is traceable.
- **Record deployments.** The application already writes an audit trail naming the machine
  and user. A licensee under audit can be asked to produce it.

---

## What I recommend, in order

| Priority | Action | Cost | Effort |
|---|---|---|---|
| 1 | Licence file and EULA | Free | **Done** |
| 2 | Lawyer reviews the licence | 300 to 800 EUR | One meeting |
| 3 | Code signing certificate | 200 to 600 EUR per year | Half a day to set up |
| 4 | Make the repository private | Free | **Done** |
| 5 | Signed offline licence keys | Free | **Done, switched off** |
| 6 | Per customer build watermarking | Free | Half a day |
| 7 | Obfuscation | Free to 400 EUR | One day, plus ongoing care |

Items 1 to 4 give you most of the protection available. Item 5 is built and waiting for
you to run `keygen`. Item 6 is what you add when you have real customers, and item 7 is
for when you have a competitor.

**Item 4 is the one that would undercut everything else, and it is done.** The repository
is private. That matters more than it might appear: while source is public, anyone can
read it, remove the licence check and build their own copy, and no amount of work on
layers 3 to 5 changes that. Everything below rests on the repository staying private.

---

## Decisions still outstanding on commercial policy

The mechanism is built and supports every model below, so none of these blocks you any
longer. They are commercial choices to make before you issue the first code:

1. **What does a licence cover?** Per workstation, per site, per hospital, or unlimited
   within one organisation? The code carries a seat count, and can be bound to a single
   workstation with `--machine`. The seat count is **recorded, not enforced**: nothing in
   the application counts installations, because an offline application cannot.
2. **Perpetual or subscription?** Both work. Omit `--expires` for perpetual; pass
   `--days 365` for an annual subscription.
3. **What happens when a licence is invalid?** Decided, and implemented as the soft
   degradation described above: the application opens, converts and saves, and withholds
   only transmission.
4. **How will you deliver codes?** Email is normal and works offline. The code is one
   line of text; the Settings field tolerates the line breaks an email client inserts.
5. **Do you want a trial mode?** `--days 30` gives exactly that, with the fourteen day
   grace period after it.

---

## One thing to avoid

Do not add **online activation or licence phone home**. It would break the offline
guarantee that makes this application deployable inside a hospital network, it would raise
information governance objections, and hospital firewalls would block it anyway. The signed
offline licence file achieves the same commercial result without any of that.
