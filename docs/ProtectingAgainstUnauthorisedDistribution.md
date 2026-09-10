# Protecting PDF to DICOM Against Unauthorised Distribution

**For the copyright holder · Version 1.5**

> **Largely superseded as of version 1.5.0.** The software is now **free to use,
> commercially and privately**, so there is no revenue for unauthorised use to divert and
> little left to protect against.
>
> What the licence still reserves is **redistribution outside your own organisation,
> modification, rebranding and charging for it**. Those are matters for the licence
> agreement and for knowing who has a copy, not for technical enforcement.
>
> The document is kept because the reasoning is still sound, and because a decision to
> charge for a future version would bring all of it back into play. Read it in that light:
> as a record of what was considered and what it was worth, not as a plan in progress.

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

## Layer 3 — Licence keys (built, then removed)

A signed offline licence key mechanism was built for version 1.1 and then **removed at the
copyright holder's request**. Nothing in the application checks entitlement today.

It is recorded here because the decision, not the code, is the useful part.

### What it was

An ECDSA P-256 code the customer pasted into Settings, carrying the licensee name, an
optional expiry, a seat count and an optional workstation binding. A licence problem
withheld transmission only: the application still opened, converted and saved, and an
expired licence kept working for a further fourteen days.

### Why it was removed

It is worth being blunt about what it would have bought. Against a hospital IT department
copying the application to twenty more workstations, it works. Against anyone willing to
open the assembly in a decompiler, it does not: the check is four lines and deleting them
is trivial. It therefore bought protection against casual copying, at the price of a
subsystem that could refuse to transmit a clinical document, plus a private key to keep
safe for the life of the product, plus a code to issue and reissue for every site and
every hardware change.

For a product distributed to a handful of known sites under a written agreement, that
trade is not obviously worth making. **The licence agreement and controlling who receives
a build are doing most of the work in any case.**

### If it is ever wanted again

It is in the git history, complete with tests, on the commits leading to version 1.1. It
was removed cleanly rather than disabled, so restoring it means reverting a known commit
rather than reconstructing anything.

Before doing so, note what changes: whoever holds the private key can issue licences
forever, losing it means reissuing every code in the field, and every build shipped must
carry the matching public key. Those are the real costs, and they are permanent.

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
| 5 | Signed offline licence keys | Free | Built, then removed by choice |
| 6 | Per customer build watermarking | Free | Half a day |
| 7 | Obfuscation | Free to 400 EUR | One day, plus ongoing care |

Items 1 to 4 give you most of the protection available. Item 5 was built and removed;
the reasoning is in Layer 3 above. Item 6 is what you add when you have real customers, and item 7 is
for when you have a competitor.

**Item 4 is the one that would undercut everything else, and it is done.** The repository
is private. That matters more than it might appear: while source is public, anyone can
read it, remove the licence check and build their own copy, and no amount of work on
layers 3 to 5 changes that. Everything below rests on the repository staying private.

---

## What is actually protecting the product today

With licence keys removed, the answer is short and worth being clear-eyed about:

1. **The licence agreement**, which every installation accepts before the installer runs.
   This is the layer with real teeth, because it is the one a court recognises.
2. **The repository being private**, so the source, and therefore a trivially modified
   build, is not available to anyone.
3. **Controlling who receives a build.** You hand out executables; there is no public
   download.
4. **The audit trail**, which names the machine and user on every document. A licensee
   under audit can be asked to produce it.

Nothing in the software enforces anything. That is a deliberate choice, and it means the
commercial questions below no longer block any work.

### Still worth deciding, when you sell

- **What does a licence cover?** Per workstation, per site, or unlimited within one
  organisation. This now lives entirely in the written agreement.
- **Perpetual or subscription?** Likewise a contractual matter.
- **Trial terms.** A time limited trial would need the mechanism back; a trial governed by
  agreement would not.

---

## One thing to avoid

Do not add **online activation or licence phone home**. It would break the offline
guarantee that makes this application deployable inside a hospital network, it would raise
information governance objections, and hospital firewalls would block it anyway. The signed
offline licence file achieves the same commercial result without any of that.
