#!/usr/bin/env python3
"""
Static checks for WPF XAML bindings that fail at runtime rather than at build time.

WPF resolves bindings when a window loads, not when it compiles. A binding that cannot
work is therefore a clean build and a crash on startup, which on a clinical workstation
means the application simply refuses to open. The compiler says nothing.

This script catches the classes of error that do exactly that, on any platform, so they
do not have to be found by running the application on Windows.

  1. A two way binding to a read only view model property.

     Several WPF dependency properties bind two way BY DEFAULT, without the XAML saying
     so: TextBox.Text, ComboBox.SelectedItem, CheckBox.IsChecked, DatePicker.SelectedDate
     and RangeBase.Value among them. Binding one of these to a property with a private
     setter throws at load:

         "A TwoWay or OneWayToSource binding cannot work on the read-only property ..."

     This is what stopped the application starting on a Windows ARM64 machine, from
     ProgressBar.Value bound to a property with a private setter.

  2. A StaticResource key that is never defined, which throws while parsing the XAML.

Both are heuristics, deliberately biased against false positives: anything that cannot be
resolved with confidence is skipped rather than reported, because a validator that cries
wolf gets ignored.

Usage:  python3 build/validate-xaml-bindings.py [project-directory]
Exit:   0 when the XAML passes, 1 when a problem is found.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

# --------------------------------------------------------------------------- Reference data

# Dependency properties whose metadata sets BindsTwoWayByDefault. A binding to one of
# these is two way unless the XAML states otherwise, so the source must be writable.
TWO_WAY_BY_DEFAULT: dict[str, set[str]] = {
    "Text": {"TextBox", "RichTextBox", "ComboBox"},
    "SelectedItem": {"ComboBox", "ListBox", "ListView", "DataGrid", "TabControl",
                     "TreeView", "Selector"},
    "SelectedValue": {"ComboBox", "ListBox", "ListView", "DataGrid", "Selector"},
    "SelectedIndex": {"ComboBox", "ListBox", "ListView", "TabControl", "Selector"},
    "SelectedDate": {"DatePicker"},
    "SelectedDates": {"Calendar"},
    "IsChecked": {"CheckBox", "RadioButton", "ToggleButton", "MenuItem"},
    "Value": {"ProgressBar", "Slider", "ScrollBar", "RangeBase"},
    "IsExpanded": {"Expander", "TreeViewItem"},
    "IsSelected": {"ListBoxItem", "TabItem", "TreeViewItem", "ComboBoxItem"},
}

# Binding modes under which a read only source is perfectly legal.
SAFE_MODES = {"OneWay", "OneTime"}


class XamlBindingValidator:
    """Collects every problem in one pass so a single run reports all of them."""

    def __init__(self, project_root: Path) -> None:
        self.project_root = project_root
        self.problems: list[str] = []
        self.checked = 0
        self.settable: set[str] = set()
        self.read_only: set[str] = set()

    # ------------------------------------------------------------------ C# property scan

    def scan_properties(self) -> None:
        """
        Classifies every C# property as settable or read only.

        A property name is treated as settable if ANY class exposes it with a public
        setter. That deliberately errs towards silence: a name used by two types, one
        writable, is not reported.
        """
        search_roots = [self.project_root, self.project_root.parent / "PdfToDicom.Core"]

        for root in search_roots:
            if not root.exists():
                continue

            for source in root.rglob("*.cs"):
                if any(part in {"bin", "obj"} for part in source.parts):
                    continue

                text = source.read_text(encoding="utf-8", errors="replace")

                # Auto properties and expression bodied properties on one line.
                for match in re.finditer(
                    r"public\s+[\w<>\[\],\?\.\s]+?\s+(\w+)\s*\{([^}]*)\}", text
                ):
                    name, body = match.group(1), match.group(2)

                    if re.search(r"(?<!private )(?<!protected )(?<!internal )\bset\b", body) \
                            or "init" in body:
                        self.settable.add(name)
                    else:
                        self.read_only.add(name)

                # Expression bodied properties are always read only.
                for match in re.finditer(r"public\s+[\w<>\[\],\?\.\s]+?\s+(\w+)\s*=>", text):
                    self.read_only.add(match.group(1))

        # A name that is writable anywhere is not reported anywhere.
        self.read_only -= self.settable

    # ------------------------------------------------------------------ XAML scan

    def run(self) -> bool:
        self.scan_properties()

        xaml_files = [
            path for path in self.project_root.rglob("*.xaml")
            if not any(part in {"bin", "obj"} for part in path.parts)
        ]

        if not xaml_files:
            print(f"error: no XAML found beneath {self.project_root}")
            return False

        defined_keys = self._collect_resource_keys(xaml_files)

        for path in xaml_files:
            text = path.read_text(encoding="utf-8")
            self._check_two_way_bindings(path, text)
            self._check_static_resources(path, text, defined_keys)
            self._check_resource_order(path, text)

        return self._report(len(xaml_files))

    def _collect_resource_keys(self, xaml_files: list[Path]) -> set[str]:
        """Every x:Key defined anywhere, since dictionaries are merged application wide."""
        keys: set[str] = set()

        for path in xaml_files:
            text = path.read_text(encoding="utf-8")
            keys.update(re.findall(r'x:Key="([^"]+)"', text))

        return keys

    def _check_two_way_bindings(self, path: Path, text: str) -> None:
        """Finds bindings that are two way by default against a read only source."""
        for element_match in re.finditer(r"<(\w+)([^>]*?)/?>", text, re.S):
            element, attributes = element_match.group(1), element_match.group(2)

            for property_match in re.finditer(
                r'(\w+)\s*=\s*"(\{Binding[^"}]*(?:\{[^}]*\}[^"}]*)*\})"', attributes
            ):
                property_name, binding = property_match.group(1), property_match.group(2)

                owners = TWO_WAY_BY_DEFAULT.get(property_name)
                if not owners or element not in owners:
                    continue

                # An explicit safe mode settles it.
                mode = re.search(r"Mode\s*=\s*(\w+)", binding)
                if mode and mode.group(1) in SAFE_MODES:
                    continue

                source = self._binding_source(binding)
                if source is None:
                    continue

                self.checked += 1

                if source in self.read_only:
                    line = text[: element_match.start()].count("\n") + 1
                    self.problems.append(
                        f"  {path.name}:{line}  {element}.{property_name} binds two way by "
                        f"default to '{source}', which has no public setter.\n"
                        f"      Add Mode=OneWay, or give the property a public setter."
                    )

    def _binding_source(self, binding: str) -> str | None:
        """
        Returns the property that a two way binding would write to, or None when it
        cannot be determined with confidence.
        """
        # A binding with a source other than the data context is out of scope.
        if any(token in binding for token in
               ("RelativeSource", "ElementName", "Source=", "StaticResource")):
            return None

        match = re.search(r"\{Binding\s+(?:Path\s*=\s*)?([\w\.\[\]]+)", binding)
        if not match:
            return None

        path_expression = match.group(1)

        # Indexers and attached properties are not resolved.
        if "[" in path_expression:
            return None

        # For a dotted path only the final segment is written to.
        return path_expression.split(".")[-1]

    def _check_static_resources(self, path: Path, text: str, defined: set[str]) -> None:
        """A StaticResource that resolves to nothing throws while the XAML is parsed."""
        for match in re.finditer(r"\{StaticResource\s+([^}]+)\}", text):
            key = match.group(1).strip()

            if key.startswith("{"):
                continue

            self.checked += 1

            if key not in defined:
                line = text[: match.start()].count("\n") + 1
                self.problems.append(
                    f"  {path.name}:{line}  StaticResource '{key}' is not defined in any "
                    f"resource dictionary."
                )

    def _check_resource_order(self, path: Path, text: str) -> None:
        """
        A StaticResource is resolved as the XAML is parsed, so a key defined later in the
        same file than the point at which it is used cannot be found and throws. Only
        keys both defined and used within this file are checked; anything inherited from
        a merged dictionary is out of scope here.
        """
        definitions: dict[str, int] = {}

        for match in re.finditer(r'x:Key="([^"]+)"', text):
            key = match.group(1)
            definitions.setdefault(key, match.start())

        for match in re.finditer(r"\{StaticResource\s+([^}]+)\}", text):
            key = match.group(1).strip()
            definition = definitions.get(key)

            if definition is None or definition < match.start():
                continue

            self.checked += 1
            use_line = text[: match.start()].count("\n") + 1
            definition_line = text[:definition].count("\n") + 1

            self.problems.append(
                f"  {path.name}:{use_line}  StaticResource '{key}' is used before it is "
                f"defined on line {definition_line} of the same file.\n"
                f"      Move the definition above the use, or switch to DynamicResource."
            )

    # ------------------------------------------------------------------ Reporting

    def _report(self, file_count: int) -> bool:
        if self.problems:
            print(f"{len(self.problems)} problem(s) found in {file_count} XAML file(s)\n")
            print("\n\n".join(self.problems))
            print(
                "\nThese fail when the window loads, not when the project compiles. "
                "Fix them before shipping."
            )
            return False

        print(
            f"XAML bindings OK "
            f"({self.checked} binding and resource references checked "
            f"across {file_count} file(s))"
        )
        return True


def main() -> int:
    if len(sys.argv) > 1:
        project = Path(sys.argv[1]).resolve()
    else:
        project = Path(__file__).resolve().parent.parent / "src" / "PdfToDicom.App"

    return 0 if XamlBindingValidator(project).run() else 1


if __name__ == "__main__":
    sys.exit(main())
