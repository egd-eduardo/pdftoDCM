#!/usr/bin/env python3
"""
Static checks for the Inno Setup installer script.

Inno Setup only runs on Windows, so a mistake in PdfToDicom.iss is otherwise not
discovered until the Windows packaging job runs, several minutes into a build, and
reports it as a bare line number. This script catches the most common classes of error
in seconds, on any platform, before that job starts.

It is not a substitute for compiling the script. It is a fast first filter for the
mistakes that are easy to make and tedious to diagnose:

  * a flag used in a section that does not accept it, which is what aborted two builds
    (skipifsourcedoesntexist is a [Files] flag; [Icons] wants createonlyiffileexists);
  * a referenced source file that does not exist and is not marked as optional;
  * an unbalanced preprocessor conditional;
  * a section name that Inno Setup does not recognise.

Usage:  python3 build/validate-installer-script.py [path-to.iss]
Exit:   0 when the script passes, 1 when a problem is found.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

# --------------------------------------------------------------------------- Reference data

# Flags accepted by each section, from the Inno Setup 6 documentation.
SECTION_FLAGS: dict[str, set[str]] = {
    "Files": {
        "32bit", "64bit", "allowunsafefiles", "comparetimestamp", "confirmoverwrite",
        "createallsubdirs", "deleteafterinstall", "dontcopy", "dontverifychecksum",
        "external", "gacinstall", "ignoreversion", "isreadme", "nocompression",
        "noencryption", "onlyifdestfileexists", "onlyifdoesntexist", "overwritereadonly",
        "recursesubdirs", "regserver", "regtypelib", "replacesameversion",
        "restartreplace", "setntfscompression", "sharedfile", "sign", "signcheck",
        "signonce", "skipifsourcedoesntexist", "solidbreak", "sortfilesbyextension",
        "sortfilesbyname", "touch", "uninsneveruninstall", "uninsremovereadonly",
        "uninsrestartdelete", "unsetntfscompression",
    },
    "Icons": {
        "closeonexit", "createonlyiffileexists", "dontcloseonexit",
        "excludefromshowinnewinstall", "foldershortcut", "preventpinning",
        "runmaximized", "runminimized", "uninsneveruninstall", "useapppaths",
    },
    "Registry": {
        "32bit", "64bit", "createvalueifdoesntexist", "deletekey", "deletevalue",
        "dontcreatekey", "noerror", "preservestringtype", "uninsclearvalue",
        "uninsdeletekey", "uninsdeletekeyifempty", "uninsdeletevalue",
    },
    "Run": {
        "32bit", "64bit", "hidewizard", "nowait", "postinstall", "runascurrentuser",
        "runasoriginaluser", "runhidden", "runmaximized", "runminimized", "shellexec",
        "skipifdoesntexist", "skipifnotsilent", "skipifsilent", "unchecked",
        "waituntilidle", "waituntilterminated",
    },
    "UninstallRun": {
        "32bit", "64bit", "hidewizard", "nowait", "runascurrentuser", "runhidden",
        "runmaximized", "runminimized", "shellexec", "skipifdoesntexist",
        "waituntilidle", "waituntilterminated",
    },
    "Tasks": {
        "checkablealone", "checkedonce", "dontinheritcheck", "exclusive", "restart",
        "unchecked",
    },
    "Dirs": {
        "deleteafterinstall", "setntfscompression", "uninsalwaysuninstall",
        "uninsneveruninstall", "unsetntfscompression",
    },
    "INI": {"createkeyifdoesntexist", "uninsdeleteentry", "uninsdeletesection",
            "uninsdeletesectionifempty"},
}

# Sections that accept no Flags parameter at all.
FLAGLESS_SECTIONS = {"Setup", "Languages", "Messages", "CustomMessages",
                     "LangOptions", "Types", "Components", "UninstallDelete", "Code"}

KNOWN_SECTIONS = set(SECTION_FLAGS) | FLAGLESS_SECTIONS


# --------------------------------------------------------------------------- Checks

class Validator:
    """Collects problems rather than failing on the first one, so one run reports everything."""

    def __init__(self, script_path: Path) -> None:
        self.path = script_path
        self.repo_root = script_path.parent.parent.parent
        self.problems: list[str] = []
        self.checked = 0

    def fail(self, line_number: int, message: str) -> None:
        self.problems.append(f"  line {line_number}: {message}")

    def run(self) -> bool:
        if not self.path.exists():
            print(f"error: {self.path} does not exist")
            return False

        lines = self.path.read_text(encoding="utf-8-sig").splitlines()

        section: str | None = None
        conditional_depth = 0

        for number, raw in enumerate(lines, start=1):
            line = raw.strip()

            if not line or line.startswith(";"):
                continue

            # Preprocessor conditionals must balance, or the compiler reports a
            # confusing error a long way from the real cause.
            if re.match(r"^#\s*(if|ifdef|ifndef|ifexist|ifnexist)\b", line):
                conditional_depth += 1
                continue
            if re.match(r"^#\s*endif\b", line):
                conditional_depth -= 1
                if conditional_depth < 0:
                    self.fail(number, "#endif without a matching #if")
                continue
            if line.startswith("#"):
                continue

            heading = re.match(r"^\[(\w+)\]$", line)
            if heading:
                section = heading.group(1)
                if section not in KNOWN_SECTIONS:
                    self.fail(number, f"unknown section [{section}]")
                continue

            if section is None:
                continue

            self._check_flags(number, line, section)
            self._check_source(number, line, section)

        if conditional_depth != 0:
            self.problems.append(f"  {conditional_depth} unclosed preprocessor conditional(s)")

        return self._report()

    def _check_flags(self, number: int, line: str, section: str) -> None:
        match = re.search(r"\bFlags:\s*([A-Za-z0-9 ]+)", line)
        if not match:
            return

        if section in FLAGLESS_SECTIONS:
            self.fail(number, f"[{section}] does not accept a Flags parameter")
            return

        allowed = SECTION_FLAGS.get(section, set())

        for flag in match.group(1).split():
            self.checked += 1
            if flag not in allowed:
                suggestion = self._suggest(flag, section)
                self.fail(number, f"[{section}] does not accept the flag '{flag}'{suggestion}")

    def _suggest(self, flag: str, section: str) -> str:
        """Points at the right flag when one section's flag was used in another."""
        for other, flags in SECTION_FLAGS.items():
            if other != section and flag in flags:
                if flag == "skipifsourcedoesntexist" and section == "Icons":
                    return f" (it belongs to [{other}]; [Icons] wants 'createonlyiffileexists')"
                return f" (it belongs to [{other}])"
        return ""

    def _check_source(self, number: int, line: str, section: str) -> None:
        """Confirms a referenced source file exists, unless it is marked optional."""
        if section != "Files":
            return

        match = re.search(r'Source:\s*"([^"]+)"', line)
        if not match:
            return

        source = match.group(1)

        # Paths containing a preprocessor variable or a wildcard cannot be resolved here.
        if "{#" in source or "*" in source or "?" in source:
            return

        if "skipifsourcedoesntexist" in line or "external" in line:
            return

        resolved = (self.path.parent / source.replace("\\", "/")).resolve()
        self.checked += 1

        if not resolved.exists():
            self.fail(number, f"source file not found: {source} (resolved to {resolved})")

    def _report(self) -> bool:
        if self.problems:
            print(f"{self.path}: {len(self.problems)} problem(s) found\n")
            print("\n".join(self.problems))
            print("\nThe installer would fail to compile. Fix these before pushing.")
            return False

        print(f"{self.path}: OK ({self.checked} flag and source references checked)")
        return True


def main() -> int:
    default = Path(__file__).resolve().parent / "installer" / "PdfToDicom.iss"
    target = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else default

    return 0 if Validator(target).run() else 1


if __name__ == "__main__":
    sys.exit(main())
