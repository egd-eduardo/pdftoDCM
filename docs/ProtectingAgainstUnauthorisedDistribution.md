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

### What a good implementation looks like here

The architecture already accommodates this cleanly. It would be:

- `ILicenceService` in `PdfToDicom.Core/Abstractions/`
- `SignedLicenceService` in a new `PdfToDicom.Core/Licensing/` folder
- A check in `ApplicationServices` at startup, and a licence status panel in Settings
- A small `pdftodicom-licence-gen` tool, kept private and never published, that signs
  licence files with your private key

**Estimated effort: one to two days**, including tests. I have not built it yet because the
policy decisions are yours to make first. See the questions at the end.

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
| 4 | Make the repository private | Free | Minutes |
| 5 | Signed offline licence keys | Free | One to two days |
| 6 | Per customer build watermarking | Free | Half a day |
| 7 | Obfuscation | Free to 400 EUR | One day, plus ongoing care |

Items 1 to 4 give you most of the protection available. Items 5 and 6 are what you add when
you have real customers. Item 7 is for when you have a competitor.

---

## Decisions I need from you before building the licence system

I have not implemented Layer 3 because these are commercial choices, not technical ones,
and building the wrong model wastes the work:

1. **What does a licence cover?** Per workstation, per site, per hospital, or unlimited
   within one organisation?
2. **Perpetual or subscription?** Subscription needs an expiry date and a renewal process.
3. **What happens when a licence is invalid?** I recommend the soft degradation described
   above, but it is your call.
4. **How will you deliver licence files?** Email attachment is normal and works offline.
5. **Do you want a trial mode?** For example, thirty days fully functional, then transmit
   is disabled.

Tell me the answers and I will build it.

---

## One thing to avoid

Do not add **online activation or licence phone home**. It would break the offline
guarantee that makes this application deployable inside a hospital network, it would raise
information governance objections, and hospital firewalls would block it anyway. The signed
offline licence file achieves the same commercial result without any of that.
