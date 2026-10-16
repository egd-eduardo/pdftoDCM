#!/usr/bin/env python3
"""
Static check for a view model constructor that refreshes a command before it exists.

The pattern these view models use is to assign a selection in the constructor:

    Worklists = new ObservableCollection<WorklistConfiguration>(Working.Worklists);
    SelectedWorklist = Worklists.FirstOrDefault();

and to have that property's setter refresh the commands whose enabled state depends on it:

    TestWorklistCommand.RaiseCanExecuteChanged();

Those two are fine apart and fatal together. When the collection is not empty, the setter
runs during construction, before the commands have been created, and throws a
NullReferenceException. The dialog then cannot be opened at all. When the collection IS
empty the setter short circuits and nothing happens, so the fault appears only at sites
that have the feature configured, which is the worst way for it to appear.

This shipped. Opening Settings threw for anybody with a worklist server configured, and
the compiler had nothing to say about it because the code is perfectly valid.

The rule enforced here is deliberately blunt and has no exceptions: inside a view model,
every RaiseCanExecuteChanged() call must be null conditional. Written as

    TestWorklistCommand?.RaiseCanExecuteChanged();

the call is harmless whenever it runs, so the ordering inside a constructor stops being
something anybody has to reason about. The cost is one character per call site.

A cast form is accepted too, since the command may be typed as an interface:

    (SearchCommand as RelayCommand)?.RaiseCanExecuteChanged();

Usage:  python3 build/validate-viewmodel-commands.py [project-directory]
Exit:   0 when clean, 1 when a call could throw.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

# A call is safe when the dot is preceded by "?" (null conditional) on the member access,
# which covers both "Command?.Raise..." and "(Command as T)?.Raise...".
UNGUARDED = re.compile(r"(?<!\?)\.\s*RaiseCanExecuteChanged\s*\(")

# Lines that merely define or document the method are not call sites.
DEFINITION = re.compile(r"(void|public|internal|protected|private)\s+RaiseCanExecuteChanged")


def main() -> int:
    root = Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()
    view_models = sorted(root.glob("src/**/ViewModels/*.cs"))

    if not view_models:
        print(f"{root}: no view models found, so nothing was checked.", file=sys.stderr)
        return 1

    problems: list[str] = []
    checked = 0

    for path in view_models:
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
            if "RaiseCanExecuteChanged" not in line:
                continue

            stripped = line.strip()

            if stripped.startswith("//") or stripped.startswith("///") or DEFINITION.search(line):
                continue

            checked += 1

            if UNGUARDED.search(line):
                problems.append(
                    f"  {path.relative_to(root)}:{number}  {stripped}\n"
                    f"      Use '?.RaiseCanExecuteChanged()'. This setter may run from the "
                    f"constructor, before the command exists, and would throw."
                )

    if problems:
        print()
        print("Command refresh calls that can throw during construction:")
        print()
        print("\n\n".join(problems))
        print()
        print("These fail when the window opens, not when the project compiles.")
        return 1

    print(f"View model command refreshes OK ({checked} call(s) checked "
          f"across {len(view_models)} view model(s))")
    return 0


if __name__ == "__main__":
    sys.exit(main())
