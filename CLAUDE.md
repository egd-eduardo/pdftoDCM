# Working on PDF to DICOM

Standing rules for this repository. Read them before starting, not after.

## Ask first

- **Never change the version number without the author's confirmation.** That means
  `Directory.Build.props`, `build/installer/PdfToDicom.iss`, the changelog heading,
  `docs/DicomConformanceStatement.md` and the examples in `docs/BuildingItYourself.md`.
  Describe what has changed and let the author decide what it is called, and whether it is
  a release at all.
- **Never push to a branch other than the designated one** without being asked.
- **Never open a pull request** unless it was asked for.

## This is clinical software

A document that reaches the wrong patient's record is the failure this product exists to
avoid, so the order of preference is: refuse the work, degrade the result, or do it wrong,
in that order, and never the third.

- Type 2 attributes with nothing to put in them are written empty. Inventing a date of
  birth, a sex or an accession number is never the answer.
- Nothing is transmitted until every object of a conversion has passed validation. Half a
  document in an archive is worse than none. This is why a long report's page images are
  all in memory at once; see section 9c of the technical documentation before trying to
  make that cheaper.
- A file name is not a trustworthy source of identity. Reading one fills the form; it never
  sends.
- Losing the page images is a degraded result. Losing the report is a failure. Every
  rendering failure path returns an empty list and says so in the log.

## Before pushing

Run all of these. They are fast, and each one exists because something got past the others.

```bash
dotnet test "PDF to DICOM.sln" -c Release          # 563 tests, across both test projects
python3 build/validate-xaml-bindings.py            # two way bindings to read only properties
python3 build/validate-viewmodel-commands.py       # unguarded RaiseCanExecuteChanged
python3 build/validate-installer-script.py         # installer sources and flags
python3 build/validate-powershell-strict.py        # .Count on something that is not a collection
bash build/verify-against-dcmtk.sh                 # 94 interoperability checks, needs DCMTK
```

**The desktop project cannot be built on Linux or macOS**, and the view models have no
automated coverage, because WPF will not run on a build agent. Two consequences:

1. Let CI compile `PdfToDicom.App` on its Windows runner before believing a XAML or view
   model change.
2. **Opening the main window, Settings and every one of its tabs on real Windows is a
   release step, not advice.** Version 3.3.0 shipped a dialog that would not open at all:
   one setter called `RaiseCanExecuteChanged()` on a command that did not exist yet, only
   when a worklist was configured, so every test, validator and CI run passed.
   `validate-viewmodel-commands.py` now catches that exact shape, which is not the same as
   catching the next one.

## Conventions worth keeping

- **Portuguese is European Portuguese**, in the interface and in the documentation.
- **Avoid the dash as punctuation.** Where one would appear, rewrite the sentence with a
  comma, a semicolon or a full stop. This applies to the interface strings, the
  documentation and the commit messages.
- **The two localisation files stay in exact step**: `en-US.json` and `pt-PT.json` must hold
  the same keys in the same order. A missing key is a label that silently reads as its own
  key name.
- **Comments say why, not what.** A decision that was made against an obvious alternative
  should record the alternative and why it lost, because the next person to read it will
  otherwise undo it.
- **Measure before optimising, and write the number down.** The memory work in 3.5.0 found
  that the suspected cause was not the cause: see section 9c of the technical documentation
  for what was measured and for two suspicions that were investigated and dismissed.
- **A copy written property by property is how a setting goes missing.** Clone through the
  serializer. That has already cost one release.
- Commit messages explain the reasoning, including what was rejected. No model identifier
  in a commit, a pull request, or a code comment.

## Things that must not ship to a customer

The build fails if they do, and that check is not decoration:

- `docs/CodeSigning.md`, `docs/BuildingItYourself.md`,
  `docs/ProtectingAgainstUnauthorisedDistribution.md`
- the test worklist server
- **any signing material**: `codesign.*`, `*.pfx`, `*.p12`, `*.password`, `*.key`. The
  package is assembled from a named list of files so none can reach one today. The check
  exists so that handing a private signing key to a hospital does not depend on nobody
  ever writing a wildcard.

**Signing material must never be committed, and once was.** `codesign.p12` and
`codesign.password` were added in `6f46f59` and deleted in `f6714ef`. The deletion removed
them from the working tree and not from the history, and the repository is now public, so
**that key and its password are published and the certificate must be treated as
compromised.** Anything signed with it proves nothing about who produced it. The key belongs
in `~/.pdftodicom` or `%USERPROFILE%\.pdftodicom`, which is where both build scripts look
for it, and `.gitignore` covers those names.

## The protected settings password

One password guards the Destinations, Worklist and Backup pages, stored as a PBKDF2
verifier.

**This repository is public, and the password is outstanding.** The plaintext used to be in
a comment on `ProtectedSettingsLock.Verifier` and has been removed from the working tree,
along with the two mentions in the changelog and the test that named it. None of that
unpublishes it: it is in the history, so the old password must be treated as known to
everybody and the verifier replaced.

To replace it: run `pdftodicom settings-password`, which turns a new password into a
verifier without the password itself ever being written down, and paste the single line it
prints over the constant. Do not write the new password into the source, a comment, a
commit message or the changelog.
