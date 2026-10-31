#!/usr/bin/env python3
"""Creates missing Unity .meta files for the package with stable, deterministic GUIDs.

Unity needs a .meta next to every file and folder of a package that is installed from a git URL (immutable),
so the repository ships them. Run this after adding files outside Unity: python3 tools/generate_metas.py
Existing .meta files are never modified.
"""
import hashlib
import pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
PACKAGE = ROOT / "Packages/com.thepromptcook.matterportable"
TAIL = "  userData: \n  assetBundleName: \n  assetBundleVariant: \n"

TEMPLATES = {
    "folder": "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n" + TAIL,
    ".cs": "MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n" + TAIL,
    ".asmdef": "AssemblyDefinitionImporter:\n  externalObjects: {}\n" + TAIL,
    ".shader": "ShaderImporter:\n  externalObjects: {}\n  defaultTextures: []\n  nonModifiableTextures: []\n" + TAIL,
    ".hlsl": "ShaderIncludeImporter:\n  externalObjects: {}\n" + TAIL,
    "package.json": "PackageManifestImporter:\n  externalObjects: {}\n" + TAIL,
    "text": "TextScriptImporter:\n  externalObjects: {}\n" + TAIL,
}


def hidden(path: pathlib.Path) -> bool:
    rel = path.relative_to(PACKAGE)
    return any(part.startswith(".") or part.endswith("~") for part in rel.parts)


def guid_for(path: pathlib.Path) -> str:
    rel = path.relative_to(PACKAGE).as_posix()
    return hashlib.md5(("com.thepromptcook.matterportable/" + rel).encode()).hexdigest()


def template_for(path: pathlib.Path) -> str:
    if path.is_dir():
        return TEMPLATES["folder"]
    if path.name == "package.json":
        return TEMPLATES["package.json"]
    return TEMPLATES.get(path.suffix, TEMPLATES["text"])


def main():
    created = 0
    for path in sorted(PACKAGE.rglob("*")):
        if path.suffix == ".meta" or hidden(path):
            continue
        meta = path.with_name(path.name + ".meta")
        if meta.exists():
            continue
        meta.write_text(f"fileFormatVersion: 2\nguid: {guid_for(path)}\n" + template_for(path))
        created += 1
    # Orphaned metas (file deleted) confuse Unity; report them.
    for meta in PACKAGE.rglob("*.meta"):
        if not meta.with_name(meta.name[:-5]).exists():
            print("orphaned:", meta.relative_to(ROOT))
    print(f"created {created} .meta file(s)")


if __name__ == "__main__":
    main()
