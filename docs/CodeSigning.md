# Code Signing and Tamper Resistance

**Audience: the copyright holder.**

Two separate questions are answered here, because they are often confused:

- **Code signing** proves the file came from you and has not been altered. It is what stops
  Windows telling every hospital that the publisher is unknown. It does **not** hide how
  the software works.
- **Tamper resistance** raises the cost of decompiling and modifying the software. It does
  **not** prove who wrote it.

You want the first before you distribute anything. The second is a judgement call.

---

## Part 1 — Code signing

### What is unsigned today

The published builds carry no Authenticode signature. When a hospital runs one, Windows
SmartScreen says the publisher is unrecognised and requires **More info → Run anyway**.
Some managed environments block it outright, and a cautious IT department is entitled to
refuse it.

### How many things actually need signing

**Two files per architecture, and that is all:**

| File | Why |
|---|---|
| `PdfToDicom.exe` | The application |
| `PdfToDicom-Setup-<version>-<arch>.exe` | The installer |

This is a direct benefit of the self contained single file design. Every dependency,
fo-dicom, PdfPig, Serilog and the .NET runtime itself, is **embedded inside that one
executable**. Signing the outer file covers the whole bundle: alter any byte of any
embedded assembly and the signature breaks.

So the answer to "is there a way to sign all the components" is that **there are no
separate components to sign**. A conventional .NET application would scatter thirty DLLs
beside the executable and each would need its own signature. This one does not.

### Is there a free way to do it?

**For public trust, no.** Windows only trusts a certificate chaining to a root in its
trusted store, and those roots are operated by commercial certificate authorities. There
is no free issuer for publicly trusted code signing certificates, and there has not been
one for years.

Three routes exist, and only one of them is free:

#### Option A — Self signed certificate. Free, and genuinely useful inside one hospital

You create the certificate yourself. Windows will not trust it **until the certificate is
installed into the Trusted Publishers store on each machine**, which a hospital IT
department can do in seconds across the estate by Group Policy.

- **Cost:** nothing.
- **Good for:** deployment inside an organisation whose IT department cooperates. Once the
  certificate is trusted, SmartScreen is silent and the file is verifiably yours.
- **No good for:** handing a file to someone who will not, or cannot, install your
  certificate. To them it is no better than unsigned.
- **Real advantage even so:** it proves integrity. A recipient can confirm the file is
  byte for byte what you signed.

```powershell
# Once: create the certificate and export the public half for the customer's IT department
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject "CN=Eduardo Dias" `
    -CertStoreLocation Cert:\CurrentUser\My `
    -KeyUsage DigitalSignature `
    -NotAfter (Get-Date).AddYears(5)

Export-Certificate -Cert $cert -FilePath EduardoDias-CodeSigning.cer

# Each release: sign both files
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe"
& $signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `
    /sha1 $cert.Thumbprint `
    artifacts\win-x64\app\PdfToDicom.exe
```

The customer's IT department imports the `.cer` into **Trusted Publishers** by Group
Policy, and the warnings stop.

> **Always timestamp** (`/tr`). Without it the signature dies with the certificate. With
> it, files signed today stay valid after the certificate expires.

#### Option B — Azure Trusted Signing. Cheapest real certificate, about 10 USD a month

Microsoft's own service. It is the best value route to a **publicly trusted** signature
and avoids the hardware token that traditional certificates now require.

- **Cost:** roughly 10 USD per month.
- **Requires:** an Azure subscription and identity validation. Individuals are eligible;
  organisations generally need three years of verifiable history.
- **Result:** no SmartScreen warning anywhere, no certificate to install, no token to
  keep in a drawer.

This is what I would choose in your position.

#### Option C — Traditional OV or EV certificate. 200 to 600 EUR a year

From Sectigo, DigiCert, GlobalSign and others.

Since June 2023 the private key **must** live on a FIPS 140-2 hardware token, which the
issuer posts to you. That means signing requires the token physically present, which
complicates automated builds considerably. An EV certificate additionally clears
SmartScreen reputation immediately rather than after a build of downloads.

Choose this only if a customer's procurement rules demand a named commercial authority.

### What signing does not do

It does not stop anyone reading your code, and it does not stop them modifying it. A
modified copy simply loses your signature, or carries theirs. Signing proves provenance;
it is not a lock.

### Recommendation

1. **Now, free:** self sign, and hand the `.cer` to each hospital's IT department with the
   installer. This removes the warning wherever anyone cooperates, and proves integrity
   everywhere.
2. **Before selling more widely:** Azure Trusted Signing, at roughly 10 USD a month.

I can add a `build/sign.ps1` that signs both files as part of the release, and wire it into
`build.ps1` so signing is one flag rather than a remembered ritual. Say the word.

---

## Part 2 — Tamper resistance and reverse engineering

### The honest position

**.NET compiles to IL, which decompiles almost perfectly.** Anyone can open the executable
in ILSpy or dnSpy and read something very close to the original C#, including comments'
worth of structure, method names and string literals. This is true of every .NET
application, including commercial ones.

Nothing changes that completely. What follows raises cost, and it is worth knowing by how
much.

### What will not work here

**NativeAOT** compiles .NET to real machine code with no IL at all, and would be the
strongest answer by a wide margin. **It does not support WPF.** The user interface resolves
XAML generated types by reflection at runtime, which is exactly what NativeAOT removes.
This is a hard limitation, not a configuration problem.

The same reason is why trimming is deliberately disabled in this project. A trimmed WPF
build compiles cleanly and then fails when a window loads.

### What will work, in order of sense

| Measure | Cost | Effect | Risk |
|---|---|---|---|
| **Obfuscation** (Obfuscar, free) | A day | Method and variable names become meaningless. Structure remains readable | Can break WPF binding, which resolves names as text at runtime |
| **Commercial obfuscation** (Eazfuscator, .NET Reactor, 150 to 500 EUR) | A day | Adds control flow flattening and string encryption. Genuinely slows a reader down | Same, plus a dependency on a vendor |
| **Anti-debug and anti-tamper** | Included with the above | Detects a debugger, checks its own hash | Antivirus products treat these techniques as suspicious, and false positives in a hospital are expensive |

### The specific risk to weigh in this application

Obfuscation and WPF are an uneasy pair. Bindings such as `{Binding PatientId}` are resolved
**by name, at runtime, as text**. Rename that property and the binding silently stops
working: no compiler error, no crash, just an empty field on a form a clinician is using to
identify a patient.

Every obfuscator can be told to exclude the view models. Doing so protects the parts most
worth protecting, the DICOM construction and networking, and leaves the presentation layer
readable. That is the right trade, and it needs stating rather than discovering.

There is precedent in this repository for exactly this class of failure: version 1.0 would
not start on Windows ARM64 because of a WPF binding that compiled cleanly and failed when
the window loaded. `build/validate-xaml-bindings.py` exists because of it. Obfuscation
would need the same treatment: a real launch on real Windows before release, every time.

### What I would actually recommend

**Do the signing first, and consider obfuscation only if a specific threat appears.**

Ask what you are protecting against:

- **A competitor copying your approach?** The approach is not secret. It is an
  Encapsulated PDF Storage object built with a public library against a published standard.
  Any competent developer can read the DICOM standard and reach the same design. What you
  own is the working, tested, documented implementation, and obfuscation does not protect
  that from being rewritten.
- **A customer removing a licence check?** There is no longer a licence check to remove.
- **A customer redistributing your binary?** Obfuscation does not prevent copying a file at
  all. Signing plus the licence agreement is the answer to that.

On present evidence the honest recommendation is **obfuscation is not worth its risk yet**.
Sign the builds, keep the repository private, and revisit this if you start selling into a
market where a competitor might plausibly disassemble the product.

If you want it regardless, Obfuscar is free, integrates as a build step, and I would set it
up excluding the view models, with the XAML validator and a real launch on Windows as the
gate before any obfuscated build ships.

---

## Summary

| Question | Answer |
|---|---|
| How many files need signing? | Two per architecture: the application and the installer. Everything else is embedded inside them |
| Is there a free way? | Self signed, free, and effective wherever IT will deploy your certificate. Not publicly trusted |
| Cheapest publicly trusted? | Azure Trusted Signing, about 10 USD a month |
| Can reverse engineering be prevented? | No. .NET decompiles cleanly, and NativeAOT, which would help most, does not support WPF |
| Should we obfuscate? | Not yet. The risk to WPF bindings is real and the benefit is unclear until there is a threat to point at |
