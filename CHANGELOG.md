# Changelog

## 3.5.0

**A clean build, a visible way out, settings that can be carried to the next machine, a
fifth of the memory, and a build that produces both architectures and signs them.**

### The build

**Both architectures from one command.** `build.ps1` and `build.sh` now build win-x64 and
win-arm64 every time, because the machine a build is made on is not the machine it runs on:
the sites are 64 bit and the testing is done on a Windows on ARM virtual machine, where a
native build avoids the emulation layer. `-Runtimes` takes a list, `RID` and `RIDS` do the
same on a Mac, and `-Runtime` is kept as an alias so older notes still work.

**The command line tool is no longer built every time.** It is a second self contained
publish, as large and as slow as the application's, and a build about to be copied to a test
machine has no use for it. `-Cli`, or `CLI=1`, adds it; the release pipeline passes it so
what reaches a site is still complete. A stale one left by an earlier build is removed
rather than packaged, because shipping yesterday's tool beside today's application would be
worse than shipping none.

**A task that does the least possible**, for the loop of change something, build it, run it:

```
.\build\build.ps1 -Task app        build/build.sh app
```

No tests, no handover folder, no installer. Two signed executables and nothing else.

**Signing happens by default wherever it can**, and is not asked about. The certificate
goes in **one folder on both machines**, `~/.pdftodicom` or `%USERPROFILE%\.pdftodicom`,
under any of `codesign.pfx`, `codesign.p12`, `certificate.pfx` or `certificate.p12`, with
its password in `codesign.password` beside it if you would rather not be asked. On Windows
the certificate store is used as a fallback, so a self signed certificate still works with
no file at all. A machine with neither builds unsigned, says so, and carries on. `-Sign`
turns a missing certificate into an error for a release build, and `-NoSign` skips it.

**Both scripts can now explain themselves**, which they could not when this first shipped:
the Windows script read only the certificate store, so a certificate placed in that folder
on a Windows machine was ignored in silence, and neither script said where it had looked.

```
.\build\build.ps1 -Task signing        build/build.sh signing
```

It builds nothing and reports every path examined, what is actually in the folder when the
expected name is not there, the certificate store's contents on Windows, and which of the
two would be used.

**A password file is rarely plain text, and that cost a signing run.** PowerShell's
`> file` writes UTF-16 with a byte order mark, as does Notepad's "Unicode": handing those
bytes to `osslsigncode` offers it a password beginning `0xFF 0xFE` with a NUL between every
character, and it answers "invalid password", which sends somebody hunting for a wrong
password rather than a wrong encoding. `build.sh` now converts UTF-16 and strips a UTF-8
byte order mark; `sign.ps1` trims the trailing newline, without which the certificate fails
to open with "the password may be incorrect". Both were demonstrated failing first, against
a real certificate and all three encodings.

**`.gitignore` now covers `*.p12`, `codesign.password`, `*.password` and `password.txt`.**
It covered `*.pfx` only, while the documentation named `.p12` and `codesign.password` as
the files to create: a name a document tells somebody to use is a name that will eventually
be typed inside a repository, and that is exactly what happened.

**A defect found while doing this: a signed build was handing over an unsigned
executable.** The handover folder's copy is a hard link to the published one, so that 80 MB
is not stored three times. `osslsigncode` cannot sign in place, so it writes a second file,
and the script moved that over the original; a move unlinks the original, so the handover
copy kept the old, unsigned bytes while the file beside it was signed. The signed bytes are
now written through the existing file, and signing runs before the folder is assembled
rather than after. Both halves were demonstrated: a move leaves the two names on different
inodes, writing through leaves them on one.

Everything above was exercised end to end on a real build of both architectures: the PE
machine types checked (0x8664 and 0xAA64), the package sound with and without the command
line tool, the stale tool removed, and a throwaway certificate used to confirm that all
three copies of the executable carry the signature.

### Memory

Measured on a sixty page report, by sampling the process's own resident set rather than by
reasoning about it:

| | Peak resident |
|---|---|
| Before | 629 MB |
| Reading the rasterizer's own pixel layout | 247 MB |
| With the pixel data ceiling at 64 MB | 184 MB |

And across two conversions in one process, which is what a workstation actually does:

| | After one conversion | After two |
|---|---|---|
| Before | 246 MB | 311 MB |
| Now | 106 MB | 108 MB |

**The rasterizer's pixel layout is read where it lies.** Every page used to be converted to
one known layout first, which allocated and copied a second whole page, about 8 MB each at
150 DPI, for an image that was about to be read one pixel at a time anyway. The two four
byte layouts differ only in whether red sits at offset 0 or offset 2. That change alone took
the sixty page document from 629 MB to 247 MB, and **the pixel data is byte for byte
identical**: verified by hashing all sixty page objects before and after.

A silent failure was possible here, where a red stamp arrives blue, so a new test renders a
hand written PDF of a red half over a blue half and checks the channels. It was confirmed to
fail when the offsets are swapped. Rows are now read by stride as well, so a bitmap whose
stride exceeds its width can no longer come out sheared.

**A new ceiling on page image memory, `MaximumRenderedPixelMegabytes`, default 512.** Nothing
is transmitted until every object has passed validation, because half a document in an
archive is worse than none, so a long report's page images are all live at once, and the
product ships a 32 bit build. A document over the ceiling is rendered **at a lower
resolution, never in part**: a reader shown twelve pages of a twenty page report cannot tell
that eight are missing. Below 72 DPI nothing is rendered and the log says why. The
encapsulated document, the fidelity copy, is never affected. 0 removes the ceiling.

**The memory is handed back when the work is done.** Each rendered page is a multi megabyte
array, which lives on the large object heap: not compacted by default, swept only on a
generation two collection. Left alone, the report that has just been sent stays resident,
which is what Task Manager shows as high memory use. One blocking compacting collection now
runs at the end of a conversion, where nobody is waiting; measured at 17 to 35 milliseconds
for documents from one to sixty pages.

**Two suspicions that were measured and dismissed.** Holding the generated object for the
session was thought to keep the whole thing in memory, and a file handle with it: in fact
opening a 1 MB object grows the heap by 8 KB, the large values are read on demand and no
descriptor is retained. The result lists were thought to grow across conversions; they are
cleared at the start of every run. Nothing was changed on either count, and the
investigation is written down in the technical documentation so it is not repeated.

### Export and import the archives and worklist servers

A new **Backup** page in Settings writes the configured archives and worklist servers to a
file, and reads one back on another workstation. Configure one machine, export, import on
the rest. The same thing from a rollout script:

```
pdftodicom export-settings --out \\server\share\pacs-settings.json
pdftodicom import-settings --file \\server\share\pacs-settings.json
pdftodicom import-settings --file pacs-settings.json --worklists-only --replace
```

**Only the connection settings travel, and the omission is the point.** The file holds the
destinations and the worklist servers. It does **not** hold the calling AE Title: copying
that would give two workstations one identity on the hospital network, which is the single
thing a PACS administrator will object to, and it would have happened silently. Institution,
folders, language and templates are left out for the same reason, being local to the
machine.

**Importing the same file twice changes nothing the second time.** Two entries are the same
server when the AE Title, host and port match, ignoring case and spaces, so a file left on a
shared drive can be imported by the next person to set a machine up without doubling the
list. Identifiers are not used for the comparison, because they are generated per machine;
each imported entry gets a fresh one, and a name already in use gets a number rather than
leaving two identical entries in a list somebody picks from under pressure.

**Replace** is offered for when one workstation is the reference and the rest should match
it. It asks first, defaults to No, and says how many entries it is about to discard. In the
dialog nothing is written until Save, so Cancel still abandons an import; the command line
has nobody to press Save, so it writes at once and prints what it did.

Nothing secret is in the file: neither a destination nor a worklist server holds a password.
The page is behind the same password as Destinations and Worklist all the same, because the
file discloses the addresses of clinical systems and the page can replace them wholesale.

**One defect worth recording.** `FileKind`, the marker that says a file is ours, started out
as a property default. After deserialization a property left at its default is
indistinguishable from one the file never mentioned, so *any* JSON file passed the check. A
test written to prove the refusal is what caught it. The marker is now stamped on writing,
and the property defaults to empty.

21 tests cover the transfer: the round trip, every setting of an entry surviving it through a
serializer comparison rather than a hand written list, the AE-Title-stays-home guarantee,
idempotence, name collisions, replacing one kind while leaving the other, forgetting a
worklist that was discarded, and five ways a file can be wrong.

### A door icon that closes the application

The window can be moved or sized so that its own close button sits off the screen, which
on a shared workstation leaves somebody hunting for a corner that is not there. There is
now a door icon at the right of the header, set apart from Logs, Settings and About by a
separator because it is the one button there that ends what you are doing.

It asks before closing, and the question it asks depends on what is happening:

- **Nothing in progress.** It warns that anything typed into the form and not yet sent
  will be lost.
- **A conversion or transmission in progress.** It says so plainly, warns that a document
  part way through being sent may not have been accepted by the archive, and notes that
  nothing is deleted, so the document can be sent again from the output folder. The
  default answer here is **No**.

Answer yes and the work in flight is cancelled, the folder watcher and the configuration
listener are detached, the generated DICOM object, which can run to tens of megabytes, is
released, the result and validation lists are emptied and the log is flushed to disk
before the process ends. Cancellation is **requested, not waited for**: blocking the
window until a network operation notices could hold it open for as long as the association
timeout, which is exactly what somebody pressing an exit button is trying to escape. The
process exit ends the work either way.

The window's own close button and Alt+F4 go through the same confirmation, so there is one
closing path rather than two that could drift apart.

### The ImageSharp security advisories

Five advisories stand against every ImageSharp from 2.0.0 to 4.1.1. Each was read, and
each was checked against this code rather than assumed. **Four cannot be reached here**,
and the fifth is a slow decode of a file the operator chose on their own workstation:

| Advisory | Needs | Reachable? |
|---|---|---|
| `j3p4-wp97-rph4` | TIFF as `HalfVector4` + `HistogramEqualization()` | No, never called |
| `j9gm-c75j-xc9q` | TIFF CCITT Group 3 **encoder** | No, nothing encodes TIFF |
| `jjfr-hcj7-qf5w` | TIFF CCITT Group 4 **encoder** | No, same |
| `gwg2-r3hj-4w44` | ICC parser via `Convert` or `IccProfile.Entries` | No, default `Preserve` parses nothing |
| `wmxv-xphr-5c9g` | BigTIFF EXIF IFD, a decode of several seconds | **Yes**, availability only |

They are now suppressed **individually, by advisory URL**, with that analysis recorded
beside them in the project file and in the technical documentation. A new advisory, against
this package or any other, still fails the audit: confirmed by deleting one of the five and
watching only that one reappear. The blunt `NoWarn NU1902;NU1903` was rejected because it
would hide every future package vulnerability.

**Why not just upgrade.** The fix for all five is 4.1.2, and there is none in the 2.x line.
ImageSharp 3.0 moved to the Six Labors Split License, which does grant Apache-2.0 terms
below 1M USD annual gross revenue, so the terms are probably acceptable here. But **4.x
refuses to build without a licence key**: its build targets fail outright with
*"No Six Labors license found"*. A patched ImageSharp therefore needs a key from Six Labors
first, and until somebody obtains one the choice is 2.1.13 with these suppressions or no
image support at all.

Also moved 2.1.11 to **2.1.13**, the last of the 2.x line, which is worth having regardless.

**CI now installs two SDKs.** `NuGetAuditSuppress` needs NuGet 6.12 or later, so a .NET 9
or 10 SDK; the 8.0 SDK ignores it in silence. CI gets 8.0.x for the runtime the tests need
and 10.0.x for a NuGet that honours the suppressions, so the same repository no longer
produces five warnings on a build agent and none on a developer's machine.

### The CA1416 warnings

Three in `PdfPageRenderer.cs`, unrelated to the above. PDFtoImage declares its entry points
as supported on Android 31+, Linux, macOS and Windows; this assembly targets plain `net8.0`,
which also admits browser, iOS and tvOS, so the analyzer cannot prove the calls are not
reached there.

They are not, and the concern is already handled better than an attribute could: the
renderer loads the native library and asks it for real work before anything depends on it,
and a platform where it cannot load produces a document without page images rather than a
crash. Suppressed at the class with that reasoning written out. Narrowing with
`[SupportedOSPlatform]` was the alternative and was rejected: the attribute propagates to
every caller.

### Result

`dotnet build` reports **0 warnings and 0 errors** for all four projects. 265 tests pass and
the 94 DCMTK interoperability checks pass, including the page image rendering that depends
on the packages involved.

## 3.4.0

**Naming layouts and date formats can be built by picking from a list, and the build
produces a folder to hand over rather than a zip.**

### Picking fields instead of typing braces

The File names page now offers every available field as a button. Read a real file name
from left to right, press the fields off in that order, and the layout is assembled in the
order they were pressed:

```
Patient number  →  Name  →  Accession number     gives     {PatientId}_{Name}_{AccessionNumber}
```

The separator between fields is editable, an underscore by default, because it is whatever
the sending system uses. **Add to the list** puts the finished layout in the list, and
refuses a layout the reader could not compile, so a missing brace is caught here rather
than when a document arrives.

Date of birth formats work the same way, and each button shows the format **and the same
date written in it**, because `ddMMyyyy` means nothing to most people and `21051970` is
recognised instantly.

**Both boxes remain editable by hand.** The pickers are for the common case; anything they
cannot express, a regular expression among other things, is still typed directly. The
field buttons are generated from the reader's own list of fields, so a field added later
appears without anybody having to remember.

### The build produces a folder, not a zip

- **No zip unless you ask for one.** The handover folder can be copied to a machine, or
  onto a stick, exactly as it stands. Pass `ZIP=1` to `build.sh`, or `-Zip` to
  `build.ps1`, if you want an archive as well.
- **`build/build.sh ship`** builds 64 bit and ARM in one go into `artifacts/ship/`, one
  complete folder per architecture, with a note at the top explaining which to use and how
  to tell. Copy the whole thing, or just the one folder that matches the machine.
- A subfolder per architecture rather than the executables loose together, because the
  application is called `PdfToDicom.exe` in both and two of those cannot share a folder,
  and because what gets copied should be a complete package with its documentation and
  samples rather than a bare executable.
- CI now uploads the folder rather than the zip. `actions/upload-artifact` archives
  whatever it is given, so uploading a zip produced a zip inside a zip.

### build.sh can sign, like BUILD.bat does

`build/build.sh sign`, and signing as part of `ship`, using `osslsigncode` with the
certificate as a `.pfx` or `.p12` file:

```
SIGN_PFX=~/keys/codesign.pfx build/build.sh ship
```

Timestamped against the same three servers `sign.ps1` uses, falling back to an untimestamped
signature and saying so if none can be reached. The password is never put on a command line
where the process list would show it: it is read into a file only you can read, and deleted
immediately afterwards.

**It is opt in, and the reason is worth stating.** `sign.ps1` on Windows takes the
certificate from the Windows certificate store, where the private key can be marked non
exportable and never exists as a file. There is no equivalent here, so this needs the key
*as a file* on the disk, which is a real difference in exposure. Nothing is signed unless
`SIGN_PFX` names one. If a certificate is given and `osslsigncode` is missing, the build
fails rather than quietly handing over an unsigned build that was meant to be signed.

### Notes

- 265 tests pass. Signing was verified end to end on a real 45 MB Windows executable with
  a throwaway certificate: the signature validates against its issuer, and flipping a
  single byte breaks it.

## 3.3.1

**Fixes a crash in 3.3.0: Settings would not open at all for anybody with a worklist
server configured.**

Upgrade from 3.3.0 immediately. 3.3.0 should not be used.

### What happened

The Worklist page in Settings became a list in 3.3.0, and the view model assigns the
initial selection in its constructor:

```csharp
Worklists = new ObservableCollection<WorklistConfiguration>(Working.Worklists);
SelectedWorklist = Worklists.FirstOrDefault();
```

That setter refreshes the commands whose enabled state depends on the selection. The
commands were created further down the same constructor, so the refresh ran against a
command that did not exist yet and threw a `NullReferenceException`. The dialog could not
be opened.

**It only broke where the feature was configured.** With no worklist servers,
`FirstOrDefault()` returns null, the setter short circuits and nothing happens, which is
why every automated check and every local run passed. The same code with one server
configured throws every time.

The existing destination selection has carried a null conditional call for exactly this
reason since it was written. The new one was added without it. That is the whole defect.

### What stops it happening again

- The call sites are null conditional, so the ordering inside a constructor is no longer
  something anybody has to reason about.
- The commands are now created before any property is assigned, so the guard is not the
  only thing standing in the way.
- **A new build check, `build/validate-viewmodel-commands.py`**, fails the build on any
  `RaiseCanExecuteChanged()` in a view model that is not null conditional. It was confirmed
  to catch this exact line. It runs in CI and in both build scripts, alongside the XAML
  binding validator that exists for the same class of fault: code that compiles cleanly and
  fails when a window opens.

### Also: the application now says when it changes your settings file

Asked for after this incident, and worth having regardless. Three things, and one of them
is deliberately *not* what was asked for.

- **An upgraded file says so.** When the settings were written by an earlier version they
  are upgraded in place, and the window and `pdftodicom config` now say that happened and
  that the worklist server was kept. Nothing is lost; the file on disk has simply changed
  shape, and somebody who knows what was in it should see that rather than find out later.
- **A file from a NEWER version is a warning, and is not written over.** This is the
  direction that genuinely loses settings: an older build cannot model what a newer one
  added, so saving would write the file back without it. It now says so plainly and leaves
  the file alone on load, so there is something to go back to.
- **It does not say the file is "incompatible".** It is not. An older settings file is
  upgraded correctly and always was; what failed in 3.3.0 was the dialog, not the file.
  Telling people to recreate a working configuration would cost them a worklist connection
  and a set of destinations for no reason.

**If the Settings dialog cannot be opened at all**, the message now names the settings file
and says to rename rather than delete it, so the values can be copied back. That is the
message this release would have shown instead of "Object reference not set to an instance
of an object".

### Note

The view models have no automated test coverage, because WPF cannot run on a build agent
without a desktop session. These static checks stand in for that, and this is the second
defect of this shape to reach a release. **Opening every window once on a real Windows
machine remains a release step that cannot be skipped**, and this release is the reason to
take that seriously rather than as advice.

## 3.3.0

**Several worklist servers, chosen when the worklist is opened; the destinations are now
behind the same password as the worklist; and that password is `Odraude`.**

### Several worklist servers

A hospital does not have one worklist. Radiology and cardiology are usually different
systems, and until now this application could be pointed at only one of them at a time.
The Worklist page in Settings is now a list, like Destinations: add a server, give it a
name the operator will recognise, configure it, test it.

When the worklist is opened, a **Worklist to search** box appears at the top if there is
more than one. The choice is remembered, so a department that always uses the same one
never picks it again.

- **Look a patient up by number.** Type the patient number, press Search, and a single
  match is selected ready to use. Several scheduled procedures for the same patient all
  appear so the right one can be chosen.
- **"All worklists"** asks every enabled server at once, for when somebody has a document
  in their hand and does not know which department scheduled the examination. An extra
  **Worklist** column says which server each row came from, which is what tells two
  departments' entries apart when the same patient appears in both.
- **One server being down does not hide the others.** Each reports for itself, by name, so
  a failure is visibly one department's server rather than "the worklist is down".
- **`pdftodicom worklist --worklist <name>`** queries one named server. Without it every
  configured server is queried, which is what commissioning a site wants.

**Nothing has to be retyped when upgrading.** A configuration holding the old single
`Worklist` object is migrated into the list the first time this version starts, keeping
every setting, and the file is rewritten so it happens once. This is verified against
DCMTK over a real association, because a worklist connection is agreed with another
department and tested against a live server; losing it silently would mean somebody
retyping an AE Title from memory, and a wrong AE Title offers the wrong patients.

### The destinations are protected too, and the password is now Odraude

The one password now guards **both** the Worklist and the Destinations pages. They are the
same kind of setting: addresses and AE Titles that decide where patient data comes from and
where it goes. A mistyped destination sends a report to the wrong archive, which is as bad
as a mistyped worklist, and the same person is trusted with both.

Unlocking either page opens the other, since whoever opened one can open the other and
typing the password twice in one dialog would protect nothing. Closing Settings locks both
again. Both outcomes of an unlock attempt now go to the audit trail: one wrong attempt is a
typo, a run of them is somebody trying.

`pdftodicom settings-password` generates the verifier, and the old name
`pdftodicom worklist-password` still works. The password in effect is now pinned by a test
as well as recorded in the comment beside the verifier; they have to agree, because a
verifier that no longer matches the password everybody believes is set locks the copyright
holder out of their own software with no way to debug it.

### When the Worklist button does not appear

The button is shown only when at least one server is **enabled**, which is deliberate: a
button that always fails is worse than no button. The cost was that a site which configured
a server and left it disabled saw nothing and had nothing to read. Two things now answer
that outright:

- **`pdftodicom config`** lists every configured worklist server with its enabled state and
  then says in as many words whether the button is shown.
- **A configuration file that could not be read now says so.** It is still set aside and
  defaults still take over, because the application must always start, but the main window
  now reports it and names the file that was kept. Before this, a file hand edited into
  invalid JSON meant every destination and every worklist silently reverted to defaults,
  with the only record a text file beside the quarantined one that nobody would think to
  look for. If the worklist has ever vanished for no apparent reason, this is the first
  thing to check.

### Notes

- 265 tests pass. The interoperability script runs 94 checks against DCMTK, up
  from 84.
- The worklist servers and the destinations are stored as `Worklists` and `Destinations` in
  the configuration file; `samples/config.sample.json` is annotated with both, including
  what the migration does.

## 3.2.0

**The file name settings now have a page of their own in the dialog, and a short patient
number gets its leading zeros back.**

### A File names page in Settings

Everything that was configuration-file-only in 3.1.0 is now in **Settings**, on a **File
names** page placed after Folders, since it acts on what the monitored folder produces.
Naming layouts and date formats are edited one per line.

The page also has a **test box**. Type a real file name, press the button, and it shows
what would be read from it: which layout matched, every field, and every note the reader
produced. It uses the settings **as they are being edited**, so a layout can be got right
without saving, closing and reopening. Nothing is converted, nothing is sent, and the file
need not exist. It is `pdftodicom check-names` without the command line, and it is where a
layout agreed with another vendor should be checked against what that vendor actually
writes.

### Leading zeros on the patient number

A sending system routinely writes `77` where the hospital's record is `0000000077`. An
archive matches patients on the string, so without this the document opens a **second
record for the same person**. The patient number read from a file name is now padded with
leading zeros, **to ten digits by default**, configurable on the new page or with
`--pad-patient-id` on the command line.

Because this changes the one value that decides whose record a document joins, it is
deliberately narrow:

- **Only a value that is entirely digits is padded.** Zeros in front of `AB12` would be
  invention rather than restoration, so an identifier containing letters is left exactly as
  it was, and the reason is reported.
- **A longer value is never shortened.** The right hand end of an identifier is the part
  that tells two people apart.
- **Every padding is reported**, in the test box, in `pdftodicom check-names` and in the
  log, so the identifier written to the object is never quietly different from the one in
  the file name.

Set the width to `0` to use the number exactly as the file name has it.

### Fixed: Settings used to discard three settings on Save

The dialog edits a copy of the configuration so Cancel can discard changes. That copy was
a hand written method that assigned each property by name, and it had **silently stopped
copying three settings** as they were added: page rendering and its resolution from 2.1.0,
then the whole file name import section from 3.1.0. Opening Settings and pressing **Save**,
having changed nothing at all, reset those settings to their defaults. There was no error
and nothing in the log.

The copy is now a serialize and deserialize round trip, which cannot drift because it
enumerates the properties instead of repeating them. A test walks every setting by
reflection and compares by value, so a setting added in the future is covered the moment it
exists; it was confirmed to fail, naming the property, when the copy was deliberately
broken.

**If you configured page rendering or its resolution and later opened Settings, check those
values.** They may have been reset without your noticing.

### Notes

- Upgrading from 3.1.0 with file name import already enabled: the patient number will now
  be padded to ten digits, because that is the new default and the setting was not in your
  configuration file. Set it to `0` on the File names page if your archive stores patient
  numbers unpadded.
- 246 tests pass, up from 235. The interoperability script runs 84 checks against DCMTK, up
  from 82, and now confirms that a document named `77_JaneRoe_TEST777777.pdf` arrives at the
  archive as Patient ID `0000000077`.

## 3.1.0

**Where documents arrive from another system with the patient identifiers already in the
file name, those identifiers are now read instead of retyped.**

This is the same problem the worklist solved, approached from the other end. A worklist
needs a worklist server; a great many systems simply write a file called
`9999999999_JohnDoe_TEST123451.pdf` into a folder. Retyping what is already in that name
is pointless and it is where a report ends up in the wrong record.

**It is off by default**, and it fills the form rather than sending anything. The
confirmation before transmission is untouched, because anybody can rename a file and the
one step this application must not short circuit is the one that decides whose record a
document joins.

### What it does

- **A configurable naming layout**, written the way the names look:
  `{PatientId}_{Name}_{AccessionNumber}_{DateOfBirth}`. Tokens for Patient ID, name,
  surname, given name, accession number, date of birth and study description, plus `{*}`
  for a part to skip, and a `regex:` form for anything the tokens cannot express. Several
  layouts can be configured at once, because different sending systems name files
  differently, and the layout that reads the most fields wins rather than the first one
  listed.
- **Date of birth and accession number are optional**, since most naming schemes carry
  neither. An absent date of birth is left empty and the sex is recorded as `O`, rather
  than invented. Both attributes are Type 2, so an empty value is conformant; a guessed
  one would not be.
- **The study date is the file's creation time**, which is normally when the sending system
  issued the document. Content Date and Content Time remain the moment of conversion, so
  both facts survive.
- **The source file is put away afterwards** so it is not processed twice: moved into
  `Imported/` by default, or renamed to `report.pdf.imported` so it no longer matches
  `*.pdf`. This applies to any document the monitored folder produced, including one whose
  name said nothing and whose details were typed in by hand; what matters is that it has
  been dealt with. A file that failed always goes to `Failed/` instead, whatever the
  setting says, and so does one whose name matched no layout at all, because that one would
  otherwise be retried for ever and never looked at. Nothing is ever deleted and nothing is
  ever overwritten; a collision adds a number, then a timestamp.
- **`pdftodicom check-names`** compiles the layouts, says which are usable and why the
  others are not, and shows exactly what each file name would produce, without converting
  anything or touching a PACS. A naming layout is agreed with another vendor and then found
  to differ in practice; this is where that should be discovered.
- **`pdftodicom send --from-filename`** for unattended conversion. It **refuses** a name it
  cannot read rather than falling back to values given on the command line.
- **A new audit event**, `PATIENT_INFO_FROM_FILENAME`, recording the file name and the
  layout that interpreted it. Where a document's identifiers came from is an audit question
  in its own right.

### Notes

- A file name that matches no layout leaves the form exactly as every previous version left
  it. Nothing about the existing behaviour changed.
- A malformed layout never stops the application. It is reported, skipped, and
  `pdftodicom config` prints the reason, so a rejected layout cannot fail silently.
- The settings live in the `FilenameImport` section of the configuration file; see
  `samples/config.sample.json`, which is annotated. There is no Settings dialog page for
  them yet.

## 3.0.0

**Patient details can now be taken from a Modality Worklist, so the step where a document
is most likely to be filed against the wrong patient disappears.**

Typing a patient number by hand is the single likeliest way a report ends up in someone
else's record. Where a department has a modality worklist, the operator now presses
**From worklist...**, picks the scheduled procedure, and the Patient ID, name, date of
birth, sex, accession number and referring physician come from the order itself.

This is a major version because the worklist changes where identity comes from, not
because anything was removed. Nothing in the previous behaviour has gone: a site with no
worklist uses the application exactly as before, and the feature is disabled by default.

### What it does

- **Modality Worklist Information Model — FIND** (`1.2.840.10008.5.1.4.31`) as an SCU.
  Configured separately from the destinations, because a worklist server is usually a
  different system from the archive.
- **The order's Study Instance UID is carried into the document.** This is the most
  valuable thing the response holds: the order already created the study the images will
  land in, so the report is filed beside them rather than in a study of its own. It
  overrides study grouping exactly as a Study Instance UID typed by the operator does.
- **Configurable match keys**: Called AE Title, host, port, Calling AE Title override,
  modality and scheduled station, each of which may be left blank. Blank is not an
  incomplete setting; it is DICOM universal matching, which is what "any modality" should
  mean.
- **Date intervals**: today (the default), yesterday and today, the last seven days, any
  date, or an explicit range. A range entered backwards is put the right way round rather
  than refused.
- **Any character set.** The query declares `ISO_IR 192`, so accented and non Latin names
  come back intact rather than transliterated.
- **Search by patient number or surname**, wildcards included, or leave both blank for
  the whole list.
- **Read only.** Nothing is ever written back to the worklist, nothing is cached on disk,
  and a worklist that is unreachable never blocks anything: the operator types the
  details in as before.

### Long names

DICOM allows 64 characters for a Person Name component group, which is the whole
`Family^Given` pair and not 64 each. Some Portuguese names are longer, and no
implementation can send them whole; the limit is the standard's.

The surname is kept complete, because that is what an archive indexes and what a
clinician searches by, and the given name is shortened at a word boundary so it still
reads as a name. Where the surname alone is over the limit, it is cut at a word boundary
and the given name dropped.

This happens without interrupting anyone, as asked. It is not hidden: the worklist window
shows the name that will actually be used underneath the selected row, and every
shortening is written to the audit log with the original length, so a later question
about why a name differs from the RIS has an answer.

Middle names are not carried across. The department does not use them, and leaving them
out means they cannot consume characters the first and last name need.

### The worklist settings are locked

Pointing a workstation at the wrong worklist server does not produce an error. It
produces a list of the wrong patients, presented exactly as convincingly as the right
ones. The Worklist tab in Settings is therefore protected by a password compiled into the
executable, with no interface to change it.

What is stored is a **PBKDF2-HMAC-SHA256 verifier**, 210,000 iterations with a random
salt, not an encrypted password. A hash is strictly stronger here: anything encrypted
inside a binary must carry its key inside the binary too, and could be decrypted back to
the password. This cannot.

Stated honestly: the check runs on the workstation, so it deters a colleague who should
not be in there. It is not a defence against someone with a decompiler, and no password a
desktop application checks by itself could be.

### A test worklist server, for sites that have none

`tools/PdfToDicom.TestWorklistServer` serves seven invented scheduled procedure steps
over a real DICOM association, so the whole path can be exercised without touching
anything clinical. Its fixtures are deliberately awkward: accented names, a patient with
no given name, a 73 character name whose surname fits, and a 75 character name whose
surname alone is over the limit.

**It is never packaged**, and the build now fails outright if it, or any of the three
documents written for the copyright holder, turns up in an assembled package.

### Also in this release

- **`pdftodicom worklist`** queries the configured worklist and prints what came back,
  including the name as it would be written to the object whenever that differs. Written
  for commissioning a site: it answers "is the worklist reachable, and does it return
  what we expect?" from a machine with nothing on it but one file.
- **`pdftodicom worklist-password`** turns a password into the verifier to compile in,
  reading it without echoing so it never has to be told to anyone.
- **The interoperability script now verifies the worklist against DCMTK `wlmscpfs`**, not
  against this project's own test server. Verifying a client against our own server would
  only prove the two agree with each other.
- **The previous release is kept** under `dist/previous/2.1.0/`.

### Verified

173 automated tests, and the full interoperability script against DCMTK `storescp` and
`wlmscpfs`: the Patient ID, Accession Number and Study Instance UID come back intact, an
accented name survives the round trip, a 73 character name is shortened with the surname
kept whole, and the filters genuinely narrow the result — a wildcard matches, an unknown
Patient ID returns nothing, and a date range in the past returns nothing.

### Upgrading

Nothing to do. Existing configuration files load unchanged; the `Worklist` section is
added with the worklist disabled. Objects produced by this version carry Implementation
Version Name `PDFTODICOM_3_0`.

---

## 2.1.0

**A converted PDF now also arrives as page images, so a viewer that needs an image will
open it.**

Many PACS viewers will only launch on a study that contains an image. An Encapsulated PDF
is not an image SOP Class, so at such a site the viewer refused to open and the report
could not be read at all, however conformant the object was.

Converting one PDF now produces two series in a single study: the encapsulated document,
and one Secondary Capture image per page. The viewer opens, the report is legible on
screen, and the document remains authoritative with selectable text.

### Why the pages are rendered rather than a blank image sent

A blank placeholder would have satisfied the viewer with far less work. It was rejected:
a blank image in a patient's record is actively misleading. A clinician scanning a worklist
sees an empty thumbnail and reasonably concludes the study is empty or the acquisition
failed, so the study holding the actual report gets skipped. It would satisfy the viewer
while defeating the purpose of sending the report.

### Details

- **150 DPI by default**, which keeps body text comfortably readable and is what this was
  verified at. Configurable through `PdfRenderDotsPerInch`.
- **Greyscale when the page has no colour**, decided from the pixels rather than assumed.
  A report is usually black text on white, and storing that as RGB would triple every
  object for nothing.
- **Every page, never a subset.** Rendering only the first page would let a reader believe
  they had seen a report whose findings continue on page three.
- **Own series, same study.** The pages are numbered so a viewer pages through them in
  order, and stay distinguishable from the document.
- **Derivation Description** records that the pixels were rendered from the document in
  the same study, so an auditor need not guess why one study holds the report twice.
- **Switchable.** `RenderPdfPagesAsImages`, on the DICOM tab in Settings, restores the
  single object behaviour for a site whose viewer already displays encapsulated documents.
- **Rendering never blocks the report.** If the renderer cannot load or a document will not
  render, the failure is logged and the encapsulated PDF is produced and sent regardless.

### The first native dependency

Rendering needs PDFium, the PDF engine in Chrome, reached through PDFtoImage. PDFium is
Apache-2.0 and PDFtoImage is MIT, matching the existing licence set, and all the pieces
ship for win-x86, win-x64 and win-arm64. The alternatives were Ghostscript and MuPDF, both
AGPL, which would place conditions on every hospital receiving this software.

This is the first native code in the product and costs roughly 11 to 13 MB per executable
before compression. The command line tool now publishes with
`IncludeNativeLibrariesForSelfExtract`, which it never needed before; without that change
its single file promise would have quietly stopped being true.

### Fixed along the way

- **The interoperability check could test the wrong object.** It took the first file the
  archive stored, which with several objects arriving was no longer reliably the document.
- **Two of its checks could pass without reading anything.** They matched on the DICOM
  keyword, which `dcmdump` prints *after* the value, so the reads returned empty strings
  and comparing two empty strings succeeded. Attributes are now read by tag number.


## 2.0.1

- **Dragging an image onto the window did nothing.** The drop handler still required a
  `.pdf` extension, so every image was refused even though the window invited one and the
  Browse button accepted one. It now takes any single file and decides what it is from the
  content, as everything else in the application does.
- **A file that is neither a PDF nor an image** was reported as not having a PDF header,
  which is true and unhelpful. The message now names what can be converted.

## 2.0.0

**Images can now be converted, and several files can share one study.** The major version
reflects a new SOP Class and a new way of forming studies, not a rewrite: the PDF
conversion is unchanged and its objects are byte for byte what 1.5.5 produced.

### Images

JPEG, PNG, TIFF, BMP, GIF, WebP and TGA files are converted to **Secondary Capture Image
Storage** objects (`1.2.840.10008.5.1.4.1.1.7`), transmitted the same way documents are.

- **A multi page source, typically a scanned TIFF, becomes one DICOM image per page**, all
  sharing one series and numbered in order, so a viewer pages through them as one document.
  Single frame Secondary Capture is used rather than the multi frame SOP Classes because it
  is the most widely supported image class in the field, including on older archives.
- **Greyscale stays greyscale, decided from the pixels rather than the file format.** A
  flatbed scanner emitting an RGB JPEG of a black and white page produces a `MONOCHROME2`
  object: two thirds smaller, and nothing is lost, because if red, green and blue are equal
  in every pixel there is no colour to discard.
- **What a file is comes from its content, not its name.** A scanner that writes a TIFF and
  calls it `.jpg`, or a renamed file, converts correctly. This applies to PDFs too.
- **Lossy history is recorded.** A JPEG source sets Lossy Image Compression to `01` and the
  method to `ISO_10918_1`, because the pixels have already lost information that no later
  step recovers and a clinician judging fine detail is entitled to know it.
- **Modality for images is configured separately** from the document modality, so a site
  using `DOC` for documents does not end up labelling photographs as encapsulated documents.

Pixel data is stored uncompressed, as the documents are, so every conformant archive
accepts it and no codec library is needed. The cost is size: a twelve megapixel photograph
becomes an object of roughly 36 MB. The operator is shown the pixel dimensions before
converting, and a page beyond roughly 1.2 GB raises a warning.

### Study grouping

A new setting decides how converted files are gathered into studies:

| Mode | Behaviour |
|---|---|
| Each file in its own study | Every conversion creates a new study. **This is the default** and is what every earlier version did |
| Files for the same patient share one study | Conversions for the same Patient ID reuse one Study Instance UID |

**Each file always gets its own Series Instance UID, in both modes.** That is what keeps
the files distinguishable inside a study, and it is why grouping loses nothing: a viewer
still shows each document or image separately, it simply shows them together.

The association is held in memory and **never written to disk**. A patient returning in
three months is having a new examination, and their new report must not silently join the
study created for the old one. Changing the Patient ID, or closing the application, starts
a new study. Patient identifiers are compared exactly, including case, because they are
opaque strings from another system and treating `ab1` and `AB1` as one patient is a guess.

A Study Instance UID typed on the form, or passed as `--study-uid`, overrides grouping.

### Command line

- `--file` converts anything; `--pdf` and `--image` are accepted as the same option, since
  the content decides what the file is.
- **`--file` may be repeated** to convert several files in one run. That is what makes
  grouping usable from a script, because the association lives for one run of the tool.
- Multi page results are reported page by page.

### Executables carry their version

The standalone builds are now named `PdfToDicom-2.0.0-win-x64.exe` and so on, so a
downloaded file says which build it is without anyone opening its properties. The
executable **inside** the package and the installer stays `PdfToDicom.exe`, because the
manuals, the Start Menu shortcut and the file associations all name it; those already sit
inside a folder and an installer that carry the version.

### Under the hood

- The modules shared by both kinds of object, Patient, General Study, General Series,
  General Equipment, SC Equipment, SOP Common and the file meta information, are written in
  one place rather than two. A correction applied to one and forgotten in the other would
  land on the attributes that decide which patient a document is filed against.
- `SopClassDicomValidator` applies the rules for whichever SOP Class an object declares, so
  the workflow does not decide what kind of object it is holding in a second place.
- The image validator checks that the pixel data is exactly the length the dimensions
  promise. A viewer handed a buffer of the wrong length renders a torn image rather than
  refusing it, which is the worst outcome for a clinical image.

### Not changed

The PDF conversion path. Its objects were compared tag for tag against 1.5.5 output and are
identical, and the DCMTK interoperability check still proves the embedded PDF byte
identical to the input.

### Verification

136 tests, up from 113. The DCMTK check now also converts a hand built three page greyscale
TIFF, transmits it to a real archive, and confirms the archive stored three instances in
one series and one study with **every pixel of every page identical** after rendering back.

---

## 1.5.5

### A registered UID root

The software now owns a UID root registered with Medical Connections Ltd,
`1.2.826.0.1.3680043.9.6008`, and the "Registered UID root" field in Settings is filled in
from it rather than left for a site to complete.

- **The field is fixed and disabled.** A registered root identifies its registrant, so it
  is not a site's to set: a hospital generating identifiers beneath it would be claiming an
  arc it does not own. Anything left in `OrganizationUidRoot` by an earlier version is read
  and discarded rather than obeyed.
- **It is shown partly masked**, as `1.2.826.0.1.3680043.*.****`, and the field is disabled
  rather than merely read only, so the text cannot be selected and copied either. This
  stops the prefix being taken off a screen. It is **not** secrecy: every DICOM object the
  software produces carries UIDs built from the root, so anyone holding one can read it,
  and it is present in the executable.
- **The uniqueness undertaking is tested, not asserted.** The registration requires that
  UIDs beneath the prefix cannot be duplicated. The random component of each identifier is
  widened from twelve digits to fifteen, a thousand million million values within any one
  millisecond, and a new test generates 20,000 identifiers in a tight loop where most share
  a timestamp, so uniqueness rests entirely on that component. A generated UID is 62
  characters, two inside the 64 character limit.
- **The choice of strategy is unchanged.** UUID derived (`2.25.`) remains the default and
  is unique by construction. Select the registered root if your archive prefers identifiers
  under a registered arc.

### Two packaging faults, both of which only appeared on Windows

- **The handover folder and the installer lost the desktop application.** The two
  executables are named `PdfToDicom.exe` and `pdftodicom.exe`, which differ only in case.
  Windows filesystems are case insensitive, so copying both into one folder did not
  produce two files: the second overwrote the first and kept the first one's spelling.
  What was handed over was a `PdfToDicom.exe` that was really the command line tool, and
  double clicking it opened a console rather than the application. The installer put both
  into its application directory too, so the Start Menu shortcut had the same problem.

  The command line tool now goes into a `Command line` subfolder in both.

  This was invisible on Linux and macOS, where the two names are distinct, which is why an
  assembly verified there looked correct. The Windows build reported 11 files in the
  package where the same code on Linux reported 12.

- **The installer shipped three documents written for the copyright holder**, on building
  the software, code signing and protecting against unauthorised distribution, the last of
  which discusses the customer. It copied `docs\*` with a wildcard. It now copies the same
  named list the handover package uses.

- **The build refuses to produce either fault again.** Before the package is zipped it
  asserts that the required files are present under their exact names, and that no two
  entries differ only in case. The first catches the loss as Windows shows it, a missing
  name; the second catches it as Linux shows it, two names that cannot coexist. Both
  checks are in `build.ps1` and `build.sh`.

### Also

- **The XAML validator now checks localization keys.** A mistyped `{loc:Localize}` key does
  not throw; the localizer returns the key, so the label renders as
  `Settings.OrganizationUidRoot.Caption` on a clinical workstation. Nothing caught that
  before a user was looking at it. All 114 keys in the interface are now checked against
  the English string table on every build.

### Not changed

The conversion, validation and transmission behaviour is as in 1.5.0. 113 tests pass, six
more than 1.5.0, all six covering the registered root.

## 1.5.0

**Licensing dropped. The software is free to use, commercially and privately.**

There is no licence key, no activation, no expiry, no seat count and no feature is
withheld. No payment is due and no agreement need be signed to install and use it, on any
number of workstations, at any number of sites.

Copyright is retained. Free to use is not the same as open source: redistributing outside
your own organisation, modifying, rebranding or charging for it still need permission,
which is generally given on request. The point of that clause is knowing where the software
is running, not restricting who may run it. See [`LICENSE`](LICENSE).

### What changed

- **Licence key enforcement removed entirely.** The codec, signing, machine fingerprint,
  transmission gate, Settings tab and generator tool are gone from the code, not merely
  disabled. Nothing checks entitlement.
- **`LICENSE` rewritten** to grant free commercial and private use, with the clinical and
  no-warranty terms strengthened rather than relaxed. Supplying software free of charge
  into a clinical setting increases the need for those terms; it does not reduce it.
- **Code signing added.** `build/sign.ps1` signs the application, the command line tool and
  the installer with a self signed certificate, and `build.ps1 -Sign` runs it as part of a
  release. Every signature is timestamped, so it outlives the certificate.

  **Nothing needs to be installed.** It uses PowerShell's `Set-AuthenticodeSignature`,
  which is part of Windows. `signtool.exe` and the Windows SDK are not required.

  **Windows will still warn that the publisher is unrecognised.** A self signed certificate
  is not trusted by default and is deliberately not being deployed to client machines. What
  signing provides is tamper evidence, a consistent publisher identity, and the ability for
  anyone to verify a file is exactly what was signed. See
  [`docs/CodeSigning.md`](docs/CodeSigning.md).
- **`BUILD.bat` added**, so the application can be built by anyone with the free .NET SDK
  and no development environment. It checks prerequisites, runs the tests, refuses to build
  if any fails, and reports where the result is. See
  [`docs/BuildingItYourself.md`](docs/BuildingItYourself.md).
- **DICOM Implementation Version Name** is now `PDFTODICOM_2_0`, which is how a PACS
  administrator identifies which build produced an object.

### Upgrading

Nothing to do. A 1.5.0 build never asks for anything. Configuration files from earlier
versions are read unchanged; the obsolete `LicenceCode` setting, if present, is ignored.

### Not changed

The conversion, validation and transmission behaviour is identical to 1.1.0. The same 107
tests pass and the same end to end interoperability check against DCMTK passes, with a byte
identical PDF round trip.

---

## 1.1.0

Licence key enforcement, since removed in 1.5.0. Not recommended; use 1.5.0.

- Signed offline licence codes, entered in Settings, gating transmission only.
- Application published for x86, x64 and ARM64.

## 1.0.0

First release.

- Converts PDF documents to DICOM Encapsulated PDF (`1.2.840.10008.5.1.4.1.1.104.1`) and
  transmits them to a PACS by C-STORE, with C-ECHO verification first.
- Single window WPF interface, English and Portuguese (Portugal).
- Self contained single file deployment; no runtime installed on the workstation, no
  administrator rights to install or run.
- Per user installer with no elevation prompt.
- Folder monitoring, barcode scanner input, patient form templates, DICOM tag viewer,
  audit trail and exportable logs.
- Verified end to end against DCMTK, with the embedded PDF proven byte identical to the
  input.
