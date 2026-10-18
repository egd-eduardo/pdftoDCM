#!/usr/bin/env python3
"""
Static check for a .Count read that Windows PowerShell refuses to perform.

Both build scripts run under Set-StrictMode -Version Latest. Under that setting Windows
PowerShell will not read a property an object does not really have, and a single object
is not a collection, so it has no Count:

    $targets = @(
        (Join-Path $Artifacts "$Runtime\\app\\PdfToDicom.exe"),
        (Join-Path $Artifacts "$Runtime\\cli\\pdftodicom.exe")
    ) | Where-Object { Test-Path $_ }

    Write-Step "Signing $($targets.Count) file(s)"

The array subexpression there wraps the list being filtered, not the pipeline. A pipeline
that emits one object assigns that object, so an application only build with no installer
yet, which is the ordinary case, left $targets holding a plain string and the signing step
died with "The property 'Count' cannot be found on this object".

PowerShell 7 answers 1 for the same expression, because it gives every object a synthetic
Count. So does every macOS and Linux run, and CI has no PowerShell step at all. The fault
can therefore only ever appear on somebody's own Windows machine, in the middle of a
build, which is why it is worth a static check rather than a convention.

The rule: before .Count is read from a variable, that variable's assignment must be
wrapped so the result is a collection whatever the pipeline emitted.

    $targets = @( ... | Where-Object { ... } )      the whole pipeline, not part of it
    $fileCount = @(Get-ChildItem $p -Recurse -File).Count

Built in collections ($Matches, $args, $PSBoundParameters, $input, $error) are exempt, as
is $_, since those are genuine collections wherever they are defined.

Usage:  python3 build/validate-powershell-strict.py [repository-directory]
Exit:   0 when clean, 1 when a read could throw.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

# Anchored to this file rather than to the current directory, for the reason written out
# in validate-viewmodel-commands.py: a validator that silently checks nothing is worse
# than no validator.
DEFAULT_ROOT = Path(__file__).resolve().parent.parent

EXEMPT = {"_", "matches", "args", "psboundparameters", "input", "error", "host", "psitem"}

VARIABLE_COUNT = re.compile(r"\$([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*Count\b")
PAREN_COUNT = re.compile(r"(?<![\w@])\(")
ASSIGNMENT = re.compile(r"^\s*\$([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$")
COMMAND = re.compile(r"^[A-Za-z]+-[A-Za-z]+\b")


def strip_comment(line: str) -> str:
    """Removes a trailing comment, leaving anything inside quotes alone."""
    out = []
    quote = None

    for index, char in enumerate(line):
        if quote:
            out.append(char)
            if char == quote:
                quote = None
            continue

        if char in "'\"":
            quote = char
            out.append(char)
            continue

        if char == "#":
            # "$#" and "#" inside a variable name are not comments; a comment starts a
            # token, so only treat it as one at the start or after whitespace.
            if index == 0 or line[index - 1].isspace():
                break

        out.append(char)

    return "".join(out)


def depth_after(text: str, start: int = 0) -> int:
    """Net bracket depth of a line, ignoring quoted sections."""
    depth = start
    quote = None

    for char in text:
        if quote:
            if char == quote:
                quote = None
            continue

        if char in "'\"":
            quote = char
        elif char in "([{":
            depth += 1
        elif char in ")]}":
            depth -= 1

    return depth


def top_level_pipe(text: str) -> bool:
    """True when a | appears outside every bracket and every quoted string."""
    depth = 0
    quote = None

    for char in text:
        if quote:
            if char == quote:
                quote = None
            continue

        if char in "'\"":
            quote = char
        elif char in "([{":
            depth += 1
        elif char in ")]}":
            depth -= 1
        elif char == "|" and depth == 0:
            return True

    return False


def wrapped_whole(text: str) -> bool:
    """True when the expression is one @( ... ) that spans all of it."""
    if not text.startswith("@("):
        return False

    depth = 0
    quote = None

    for index, char in enumerate(text):
        if quote:
            if char == quote:
                quote = None
            continue

        if char in "'\"":
            quote = char
        elif char in "([{":
            depth += 1
        elif char in ")]}":
            depth -= 1
            if depth == 0:
                return text[index + 1:].strip() == ""

    return False


def collect_assignments(lines: list[str]) -> dict[str, list[tuple[int, str]]]:
    """Every "$name = ..." in the file, with its right hand side joined across lines."""
    found: dict[str, list[tuple[int, str]]] = {}

    for index, raw in enumerate(lines):
        match = ASSIGNMENT.match(strip_comment(raw))

        if not match:
            continue

        name, rhs = match.group(1), match.group(2).strip()
        number = index + 1
        depth = depth_after(rhs)
        cursor = index

        # A right hand side continues while brackets are open, while the line ends in a
        # pipe, and while it ends in the line continuation character.
        while cursor + 1 < len(lines) and (
            depth > 0 or rhs.rstrip().endswith("|") or rhs.rstrip().endswith("`")
        ):
            cursor += 1
            nxt = strip_comment(lines[cursor]).strip()
            rhs = rhs.rstrip("`").rstrip() + " " + nxt
            depth = depth_after(rhs)

        found.setdefault(name.lower(), []).append((number, rhs.strip()))

    return found


def unsafe_reason(rhs: str) -> str | None:
    """Says why a right hand side may leave a single object behind, or None if it cannot."""
    if not rhs:
        return None

    if wrapped_whole(rhs):
        return None

    if top_level_pipe(rhs):
        return "the pipeline is not wrapped, so one result is assigned as a single object"

    if rhs.startswith("@(") or rhs.startswith("@{"):
        # Wrapped, but something follows the closing bracket; the pipe case above has
        # already been answered, so whatever this is cannot be judged here.
        return None

    if COMMAND.match(rhs) or (rhs.startswith("(") and COMMAND.match(rhs[1:].lstrip())):
        return "a command is assigned unwrapped, so one result is assigned as a single object"

    return None


def check_file(path: Path, root: Path) -> list[str]:
    lines = path.read_text(encoding="utf-8").splitlines()
    assignments = collect_assignments(lines)
    problems: list[str] = []

    for index, raw in enumerate(lines):
        line = strip_comment(raw)

        for match in VARIABLE_COUNT.finditer(line):
            name = match.group(1)

            if name.lower() in EXEMPT:
                continue

            for number, rhs in assignments.get(name.lower(), []):
                if number > index + 1:
                    continue

                reason = unsafe_reason(rhs)

                if reason:
                    problems.append(
                        f"  {path.relative_to(root)}:{index + 1}  reads ${name}.Count\n"
                        f"      ${name} is assigned at line {number}, where {reason}.\n"
                        f"      Wrap the whole right hand side: ${name} = @( ... )"
                    )
                    break

        # (Get-ChildItem ...).Count, as opposed to @(Get-ChildItem ...).Count
        for match in PAREN_COUNT.finditer(line):
            tail = line[match.start():]
            closing = tail.find(").Count")

            if closing == -1:
                continue

            inner = tail[1:closing].strip()

            if not inner or "(" in inner:
                continue

            if COMMAND.match(inner) or top_level_pipe(inner):
                problems.append(
                    f"  {path.relative_to(root)}:{index + 1}  reads .Count from an "
                    f"unwrapped command\n"
                    f"      One result, or none, is not a collection. Write "
                    f"@({inner}).Count"
                )

    return problems


def main() -> int:
    root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else DEFAULT_ROOT
    scripts = sorted((root / "build").glob("*.ps1"))

    if not scripts:
        print(f"{root}: no PowerShell scripts found, so nothing was checked")
        return 1

    problems: list[str] = []

    for script in scripts:
        problems.extend(check_file(script, root))

    if problems:
        print()
        print("Count reads that Windows PowerShell will refuse under Set-StrictMode:")
        print()
        print("\n\n".join(problems))
        print()
        print("PowerShell 7, macOS, Linux and CI all answer these happily. Only a build")
        print("on Windows fails, and only once it has reached that line.")
        return 1

    print(f"PowerShell strict mode OK ({len(scripts)} script(s) checked)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
