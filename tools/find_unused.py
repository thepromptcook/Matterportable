#!/usr/bin/env python3
"""Lists public/internal members of the package that nothing references (dead code). Exit 1 if any are found.

A name counts as used if it appears anywhere else in the C# sources (tests and tools included) or the web client. Some names are
used by Unity or by callers outside this repo; list those in ALLOWED.
Usage: python3 tools/find_unused.py
"""
import collections
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
ALLOWED = {
    "OnInspectorGUI",   # Unity editor callback
    "MpConvert",        # extension-method container (members are used)
    "LogExtensions",    # extension-method container (members are used)
    "ViewModes",        # extension-method container (members are used)
    "_ExitTree",        # Godot callback
}

DECL = re.compile(r"(?:public|internal)\s+(?:static\s+|readonly\s+|override\s+|sealed\s+|const\s+|event\s+)*"
                  r"[\w<>\[\],. ?()]+?\s+(\w+)\s*(?:\(|\{|=>|=|;)")


def main():
    files = [p for p in ROOT.rglob("*.cs") if not any(d in p.parts for d in ("bin", "obj", ".godot", "MatterportCache", "Library"))]
    # The web client calls the WebAssembly Core's [JSExport] methods from JavaScript.
    scripts = list((ROOT / "web" / "client" / "js").glob("*.js"))
    uses = collections.Counter(re.findall(r"\w+", "\n".join(p.read_text() for p in files + scripts)))  # one pass, not one regex per name
    unused = []
    for p in files:
        rel = p.relative_to(ROOT).as_posix()
        if "/Tests/" in rel or rel.startswith("tools/"):
            continue
        for m in DECL.finditer(p.read_text()):
            name = m.group(1)
            if name not in ALLOWED and uses[name] <= 1:
                unused.append(f"{name}  ({rel})")
    for u in sorted(set(unused)):
        print("unused:", u)
    print("no unused members" if not unused else f"{len(set(unused))} unused member(s): delete them or add to ALLOWED")
    return 1 if unused else 0


if __name__ == "__main__":
    sys.exit(main())
