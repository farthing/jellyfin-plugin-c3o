#!/usr/bin/env python3
"""Build the release package for C-3.o and update the plugin repository manifest.

    python package.py

Does three things:
  1. builds the plugin in Release mode;
  2. writes dist/c-3.o_<version>.zip (the DLL, which is all Jellyfin needs);
  3. adds or replaces that version's entry in manifest.json, with the zip's MD5 checksum and
     the notes for that version from CHANGELOG.md.

The version comes from <Version> in the .csproj. Nothing is uploaded: the commands to publish
the release are printed at the end (and described in README "Releasing").
Needs Python 3.9+ and the .NET SDK; no extra Python packages.
"""
import datetime
import hashlib
import json
import re
import subprocess
import sys
import zipfile
from pathlib import Path

REPO = "farthing/jellyfin-plugin-c3o"
ROOT = Path(__file__).resolve().parent
PROJECT = ROOT / "Jellyfin.Plugin.C3o"
DLL = PROJECT / "bin" / "Release" / "net10.0" / "Jellyfin.Plugin.C3o.dll"


def read_version() -> str:
    text = (PROJECT / "Jellyfin.Plugin.C3o.csproj").read_text(encoding="utf-8")
    match = re.search(r"<Version>([^<]+)</Version>", text)
    if not match:
        sys.exit("Could not read <Version> from the csproj")
    return match.group(1).strip()


def read_changelog(version: str) -> str:
    """The lines under '## <version>' in CHANGELOG.md, up to the next '## ' heading."""
    lines = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8").splitlines()
    notes, inside = [], False
    for line in lines:
        if line.startswith("## "):
            if inside:
                break
            inside = line[3:].split()[0] == version
            continue
        if inside:
            notes.append(line)
    text = "\n".join(notes).strip()
    if not text:
        sys.exit(f"CHANGELOG.md has no notes under '## {version}'")
    return text


def main() -> None:
    version = read_version()
    changelog = read_changelog(version)

    subprocess.run(["dotnet", "build", str(PROJECT), "-c", "Release", "-nologo", "-v", "q"], check=True)

    dist = ROOT / "dist"
    dist.mkdir(exist_ok=True)
    package = dist / f"c-3.o_{version}.zip"
    # A fixed file date keeps the zip (and so its checksum) the same for the same DLL.
    entry = zipfile.ZipInfo(DLL.name, date_time=(2026, 1, 1, 0, 0, 0))
    entry.compress_type = zipfile.ZIP_DEFLATED
    entry.external_attr = 0o644 << 16
    with zipfile.ZipFile(package, "w") as archive:
        archive.writestr(entry, DLL.read_bytes())
    checksum = hashlib.md5(package.read_bytes()).hexdigest()

    meta = json.loads((ROOT / "meta.template.json").read_text(encoding="utf-8"))
    manifest_path = ROOT / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else []
    plugin = next((p for p in manifest if p.get("guid") == meta["guid"]), None)
    if plugin is None:
        plugin = {"versions": []}
        manifest.append(plugin)

    versions = [v for v in plugin["versions"] if v["version"] != version]
    versions.insert(0, {
        "version": version,
        "changelog": changelog,
        "targetAbi": meta["targetAbi"],
        "sourceUrl": f"https://github.com/{REPO}/releases/download/v{version}/{package.name}",
        "checksum": checksum,
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    })
    plugin.clear()
    plugin.update({
        "guid": meta["guid"],
        "name": meta["name"],
        "description": meta["description"],
        "overview": meta["overview"],
        "owner": meta["owner"],
        "category": meta["category"],
        "imageUrl": f"https://raw.githubusercontent.com/{REPO}/main/art/c3o.png",
        "versions": versions,
    })
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")

    print(f"Wrote {package.relative_to(ROOT)} (md5 {checksum}) and updated manifest.json.")
    print("To publish:")
    print(f'  git add -A && git commit -m "C-3.o {version}" && git tag v{version}')
    print("  git push origin main --tags")
    print(f"  gh release create v{version} dist/{package.name} --title \"C-3.o {version}\" --notes-file <notes>")


if __name__ == "__main__":
    main()
