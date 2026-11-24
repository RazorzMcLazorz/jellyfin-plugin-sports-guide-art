#!/usr/bin/env python3
"""Add a release to a Jellyfin plugin repository manifest.

Usage: update_manifest.py <manifest.json> <zip> <version> <source_url> [notes.md]
"""
import datetime
import hashlib
import json
import pathlib
import sys

GUID = "5b3f9b0e-6a0e-4c43-9d7a-2f6c1e8a4b17"
TARGET_ABI = "12.4.0.0"

PLUGIN = {
    "guid": GUID,
    "name": "Sports Guide Art",
    "overview": "Gives every Live TV sports program artwork",
    "description": (
        "Gives every Live TV sports program artwork. "
        "Game posters on every slot \u00b7 Matchup images with ESPN or TheSportsDB logos \u00b7 "
        "Re-applied after every guide refresh \u00b7 Cached locally"
    ),
    "imageUrl": "https://raw.githubusercontent.com/RazorzMcLazorz/jellyfin-plugin-sports-guide-art/production/assets/logo.png",
    "owner": "RazorzMcLazorz",
    "category": "Live TV",
    "versions": [],
}


def main() -> None:
    manifest_path, zip_path, version, source_url = sys.argv[1:5]
    notes_path = sys.argv[5] if len(sys.argv) > 5 else ""

    manifest = json.loads(pathlib.Path(manifest_path).read_text() or "[]")
    plugin = next((p for p in manifest if p.get("guid") == GUID), None)
    if plugin is None:
        plugin = dict(PLUGIN)
        manifest.append(plugin)
    else:
        plugin.update({k: v for k, v in PLUGIN.items() if k != "versions"})

    notes = pathlib.Path(notes_path)
    changelog = notes.read_text().strip() if notes_path and notes.is_file() else f"See {source_url.rsplit('/download/', 1)[0]}"

    entry = {
        "version": version,
        "changelog": changelog,
        "targetAbi": TARGET_ABI,
        "sourceUrl": source_url,
        "checksum": hashlib.md5(pathlib.Path(zip_path).read_bytes()).hexdigest(),
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    plugin["versions"] = [entry] + [v for v in plugin.get("versions", []) if v.get("version") != version]

    pathlib.Path(manifest_path).write_text(json.dumps(manifest, indent=2) + "\n")


if __name__ == "__main__":
    main()
