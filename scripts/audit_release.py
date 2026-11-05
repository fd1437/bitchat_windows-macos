#!/usr/bin/env python3
"""Fail closed on profile data or unexpected files in a release payload.

Reports paths and digests only, never matching secret/message contents.
This is a packaging boundary check, not a claim that arbitrary binaries are safe.
"""
import argparse
import hashlib
import json
import re
import subprocess
from pathlib import Path


class AuditError(Exception):
    pass


SECRET_PATTERNS = [
    re.compile(rb"-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----"),
    re.compile(rb"\bgh[pousr]_[A-Za-z0-9]{36,255}\b"),
    re.compile(rb"\bgithub_pat_[A-Za-z0-9_]{60,255}\b"),
    re.compile(rb"\bAKIA[0-9A-Z]{16}\b"),
]
PROFILE_SUFFIXES = {".dpapi", ".sqlite", ".sqlite3", ".db", ".keychain", ".p12", ".pfx", ".pem", ".log", ".pdb", ".dmp"}
PROFILE_NAMES = {"state.json", "messages.json", "history.json", "chat.bitchat.plist", ".env", ".ds_store"}


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(chunk)
    return result.hexdigest()


def files(root):
    result = {}
    for path in root.rglob("*"):
        relative = path.relative_to(root).as_posix()
        if path.is_symlink():
            raise AuditError(f"Symbolic link in payload: {relative}")
        if path.is_file():
            result[relative] = digest(path)
    return result


def verify_inventory(actual, allowed, required):
    unexpected = set(actual) - set(allowed)
    missing = set(required) - set(actual)
    if unexpected or missing:
        raise AuditError(f"Unexpected files: {sorted(unexpected)}; missing files: {sorted(missing)}")


def verify_source(repo):
    tracked = subprocess.check_output(["git", "-C", str(repo), "ls-files", "-z"]).decode().split("\0")
    count = 0
    for relative in filter(None, tracked):
        path = repo / relative
        if path.is_symlink():
            raise AuditError(f"Tracked symbolic link: {relative}")
        name = path.name.lower()
        if path.suffix.lower() in PROFILE_SUFFIXES or name in PROFILE_NAMES or name.endswith((".keychain-db", ".sqlite-wal", ".sqlite-shm")):
            raise AuditError(f"Potential user data or signing material tracked: {relative}")
        data = path.read_bytes()
        if any(pattern.search(data) for pattern in SECRET_PATTERNS):
            raise AuditError(f"Potential credential in tracked file: {relative} (content withheld)")
        count += 1
    provenance = json.loads((repo / "windows/vendor/provenance.json").read_text())
    pins = {
        "windows/vendor/media/ffmpeg.exe": provenance["ffmpeg"]["binary_sha256"],
        "windows/vendor/media/source/ffmpeg-9.0.2.tar.xz": provenance["ffmpeg"]["source_sha256"],
        "windows/vendor/tor/tor.exe": provenance["tor"]["binary_sha256"],
        "windows/vendor/zlib/zlib-1.3.2.tar.gz": provenance["zlib"]["source_sha256"],
    }
    for relative, expected in pins.items():
        if digest(repo / relative) != expected:
            raise AuditError(f"Bundled dependency does not match its recorded provenance: {relative}")
    return {"mode": "source", "trackedFiles": count, "dependencyPins": len(pins), "passed": True}


def verify_windows(root, rid, repo):
    inventory = files(root)
    required = {"BitChat.exe", f"native/{rid}/bitchat_zlib.dll", "media/ffmpeg.exe", "tor/tor.exe"}
    allowed = required | {"media/LICENSE-FFmpeg.txt", "media/ffmpeg-config.mak"} | {
        "tor/notices/" + name for name in ("libevent.txt", "openssl.txt", "tor.txt", "zlib.txt")
    }
    # A local build may produce both zlib architectures; neither carries state.
    allowed |= {f"native/{arch}/bitchat_zlib.dll" for arch in ("win-x64", "win-arm64")}
    verify_inventory(inventory, allowed, required)
    for relative in inventory:
        if relative.startswith(("media/", "tor/")):
            if inventory[relative] != digest(repo / "windows/vendor" / relative):
                raise AuditError(f"Packaged dependency was modified: {relative}")
    return {"mode": "windows", "architecture": rid, "passed": True, "files": inventory}


def verify_macos(root, repo):
    inventory = files(root)
    fixed = {
        "Contents/Info.plist", "Contents/PkgInfo", "Contents/MacOS/bitchat",
        "Contents/_CodeSignature/CodeResources", "Contents/Resources/Assets.car",
        "Contents/Resources/AppIcon.icns", "Contents/Resources/PrivacyInfo.xcprivacy",
        "Contents/Resources/world_borders.geojson", "Contents/Resources/world_cities.geojson",
        "Contents/Resources/world_land.geojson", "Contents/Resources/online_relays_gps.csv",
        "Contents/Resources/README.md",
    }
    catalog = json.loads((repo / "bitchat/Localizable.xcstrings").read_text())
    locales = {catalog["sourceLanguage"]}
    for entry in catalog["strings"].values():
        locales.update(entry.get("localizations", {}))
    localized = {f"Contents/Resources/{locale}.lproj/{name}" for locale in locales
                 for name in ("Localizable.strings", "Localizable.stringsdict")}
    verify_inventory(inventory, fixed | localized, fixed - {"Contents/_CodeSignature/CodeResources"})
    source_resources = {"online_relays_gps.csv": "relays/online_relays_gps.csv",
                        "README.md": "bitchat/ViewModels/Extensions/README.md"}
    source_resources.update({f"world_{part}.geojson": f"bitchat/Resources/Globe/world_{part}.geojson"
                             for part in ("borders", "cities", "land")})
    for name, source in source_resources.items():
        if inventory["Contents/Resources/" + name] != digest(repo / source):
            raise AuditError(f"Packaged resource differs from tracked source: {name}")
    return {"mode": "macos", "passed": True, "files": inventory}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["source", "windows", "macos"])
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--root", type=Path)
    parser.add_argument("--rid", choices=["win-x64", "win-arm64"])
    parser.add_argument("--report", type=Path)
    parser.add_argument("--compare", type=Path, help="Require the payload to be identical to an earlier report")
    args = parser.parse_args()
    try:
        if args.mode == "source":
            result = verify_source(args.repo)
        elif args.mode == "windows":
            if args.root is None or args.rid is None:
                parser.error("windows requires --root and --rid")
            result = verify_windows(args.root, args.rid, args.repo)
        else:
            if args.root is None:
                parser.error("macos requires --root")
            result = verify_macos(args.root, args.repo)
        if args.compare and json.loads(args.compare.read_text()).get("files") != result.get("files"):
            raise AuditError("Payload changed during testing")
        if args.report:
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(result, indent=2) + "\n")
        print(f"PASS: {args.mode} release boundary audit")
    except (AuditError, OSError, KeyError, ValueError) as error:
        parser.exit(1, f"FAIL: {error}\n")


if __name__ == "__main__":
    main()
