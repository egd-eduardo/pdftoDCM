# Previous releases

## 2.1.0 — the last release before the modality worklist

A site that commissioned against 2.1.0, or that has no worklist and would rather not move,
can stay on it or go back to it.

**The executables are not kept here.** They are not committed anywhere any more, for the
reason given in [`../app/README.md`](../app/README.md): three 70 MB files per release,
kept for ever by git.

To obtain a 2.1.0 build:

```powershell
git checkout 281ab63        # 2.1.0: send a converted PDF as page images as well
.\build\build.ps1 -Runtime win-x64
```

or run the *Build Windows release* workflow by hand against that commit, which produces
all three architectures with the installer and the handover package.

[`../../CHANGELOG.md`](../../CHANGELOG.md) says what changed between 2.1.0 and the current
release.

---

## Going back

Nothing special is required. The executables are self contained and write nothing outside
the user's profile, so replacing the file is the whole procedure.

**The configuration file still loads.** An older version reads a configuration written by
a newer one and ignores the settings it does not know, so going back requires no editing.

One caveat: if the older version then **saves** the settings, it writes out only the
settings it knows about, and the worklist section is dropped. Coming forward again, the
worklist then has to be configured once more. Keeping a copy of `config.json` before going
back avoids that.

**Objects already sent are unaffected.** They are in the archive and have nothing further
to do with this application.

One difference worth knowing: objects produced by 2.1.0 carry Implementation Version Name
`PDFTODICOM_2_1`, and 3.0.0 carries `PDFTODICOM_3_0`. Nothing depends on it, but it is how
an archive's log tells which version produced an object.
