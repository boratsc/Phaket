#!/usr/bin/env python3
# Manifest aplikacji (PAK-110) z manifestu Velopack: {success, latestVersion, downloadBaseUrl,
# releases[{Version, File, SHA256}]}. Uruchamiane przez release.yml po wygenerowaniu
# releases.<channel>.<os>.json na serwerze; nie zawiera sekretów.
import argparse
import hashlib
import json
import sys
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", required=True, help="publiczny prefiks paczek, np. https://phaket.app/phaket/releases/release/win-x64/")
    parser.add_argument("--out", required=True, help="wyjściowy manifest aplikacji")
    parser.add_argument("source", help="manifests/<os>/releases.<channel>-<os>.json wygenerowany przez vpk")
    args = parser.parse_args()

    source = Path(args.source)
    try:
        assets = json.loads(source.read_text(encoding="utf-8"))["Assets"]
    except (OSError, KeyError, json.JSONDecodeError) as error:
        print(f"BŁĄD: nie mogę przeczytać manifestu Velopack {source}: {error}", file=sys.stderr)
        return 1

    base = args.base.rstrip("/") + "/"
    channels = source.parent
    releases = []
    for asset in assets:
        name = asset.get("FileName", "")
        # Tylko pełne paczki: delta nie zawiera całej aplikacji, więc nie nadaje się do pierwszej instalacji.
        if not name.endswith("-full.nupkg"):
            continue
        package = channels / name
        if not package.is_file():
            print(f"BŁĄD: manifest wskazuje brakujący plik {package}", file=sys.stderr)
            return 1
        digest = hashlib.sha256(package.read_bytes()).hexdigest().upper()
        releases.append((asset.get("Version", ""), {"Version": asset.get("Version", ""), "File": base + name, "SHA256": digest}))

    if not releases:
        print(f"BŁĄD: brak paczek *-full.nupkg w {source}", file=sys.stderr)
        return 1

    # latestVersion liczymy tą samą regułą co instalator-stub i Velopack, nie z kolejności pliku.
    def key(version: str) -> tuple[int, int, int, int, int, str]:
        core = version.split("+")[0]
        numbers, _, pre = core.partition("-")
        parts = [int(p) if p.isdigit() else 0 for p in numbers.split(".")][:3]
        parts += [0] * (3 - len(parts))
        return (*parts, 0 if pre else 1, pre)

    releases.sort(key=lambda item: key(item[0]), reverse=True)
    releases = [entry for _, entry in releases]

    out = Path(args.out)
    out.write_text(json.dumps({
        "success": True,
        "latestVersion": releases[0]["Version"],
        "downloadBaseUrl": base,
        "releases": releases,
    }, indent=2) + "\n", encoding="utf-8")
    print(f"{out}: {len(releases)} paczek, najnowsza {releases[0]['Version']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())