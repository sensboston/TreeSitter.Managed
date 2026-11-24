#!/usr/bin/env python3
"""Prints a Markdown coverage table per source file from Cobertura XML files.

Usage: coverage-summary.py <dir-with-coverage.cobertura.xml> [package-prefix ...]
Only packages whose name starts with one of the prefixes are reported
(default: TreeSitter.Managed). Line and branch hits are merged across files.
"""
import glob
import os
import sys
import xml.etree.ElementTree as ET


def main() -> int:
    root_dir = sys.argv[1]
    prefixes = sys.argv[2:] or ["TreeSitter.Managed"]
    lines: dict[str, dict[int, int]] = {}
    branches: dict[str, dict[int, tuple[int, int]]] = {}
    files = glob.glob(os.path.join(root_dir, "**", "coverage.cobertura.xml"), recursive=True)
    if not files:
        print("No coverage files found.")
        return 0
    for path in files:
        tree = ET.parse(path)
        for package in tree.getroot().iter("package"):
            name = package.get("name", "")
            if not any(name == p or name.startswith(p + ".") for p in prefixes):
                continue
            for cls in package.iter("class"):
                file_name = cls.get("filename", "").replace("\\", "/")
                if "/obj/" in file_name:
                    continue
                file_lines = lines.setdefault(file_name, {})
                file_branches = branches.setdefault(file_name, {})
                for line in cls.iter("line"):
                    number = int(line.get("number", "0"))
                    hits = int(line.get("hits", "0"))
                    file_lines[number] = max(file_lines.get(number, 0), hits)
                    if line.get("branch") == "True":
                        cond = line.get("condition-coverage", "")
                        # format: "50% (1/2)"
                        if "(" in cond:
                            covered, total = cond.split("(")[1].rstrip(")").split("/")
                            old = file_branches.get(number, (0, int(total)))
                            file_branches[number] = (max(old[0], int(covered)), int(total))
    print("| File | Lines | Branches |")
    print("|---|---|---|")
    all_l = all_lc = all_b = all_bc = 0
    for file_name in sorted(lines):
        fl = lines[file_name]
        fb = branches.get(file_name, {})
        lc = sum(1 for h in fl.values() if h > 0)
        bc = sum(c for c, _ in fb.values())
        bt = sum(t for _, t in fb.values())
        all_l += len(fl); all_lc += lc; all_b += bt; all_bc += bc
        short = file_name.split("/src/", 1)[-1]
        lp = f"{100.0 * lc / len(fl):.1f}% ({lc}/{len(fl)})" if fl else "-"
        bp = f"{100.0 * bc / bt:.1f}% ({bc}/{bt})" if bt else "-"
        print(f"| {short} | {lp} | {bp} |")
    if all_l:
        bp = f"{100.0 * all_bc / all_b:.1f}%" if all_b else "-"
        print(f"| **Total** | **{100.0 * all_lc / all_l:.1f}%** | **{bp}** |")
    return 0


if __name__ == "__main__":
    sys.exit(main())
