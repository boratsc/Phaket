#!/usr/bin/env python3
# Manifest feedu serwera PAK-110: {"Releases":[{"Version","File","SHA256","SHA1","Size"}]}.
# Serwer czyta TYLKO te klucze (Phaket.Api/Releases.cs Read) i wymaga unikalnej nazwy pliku bez
# ścieżek (ValidFile); klient dokleja File do downloadBaseUrl. Plik musi się nazywać
# releases.<channel>.<os>.json — to jedyna nazwa, którą serwer serwuje.
import argparse
import hashlib
import json
import sys
from pathlib import Path


def version_of(name: str) -> str:
    # Velopack: <Id>-<wersja>-full.nupkg / -delta.nupkg.
    parts = name.split("-")
    return parts[1] if len(parts) >= 3 else ""


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True, help="np. manifests/releases.release.win.json")
    parser.add_argument("files", nargs="+", help="paczki .nupkg z katalogu kanału+systemu")
    parser.add_argument("--version", help="wersja dla plików bez wersji w nazwie (Phaket-Setup.exe)")
    args = parser.parse_args()

    releases = []
    for name in args.files:
        package = Path(name)
        version = version_of(package.name) if package.suffix == ".nupkg" else args.version
        if not version or not all(part.isdigit() for part in version.split("+")[0].split("-")[0].split(".")):
            print(f"BŁĄD: nie odczytam wersji z nazwy {package.name} (podaj --version)", file=sys.stderr)
            return 1
        data = package.read_bytes()
        releases.append((version, {
            "Version": version,
            "File": package.name,
            "SHA256": hashlib.sha256(data).hexdigest().upper(),
            "SHA1": hashlib.sha1(data).hexdigest().upper(),
            "Size": len(data),
        }))
    if not releases:
        print("BŁĄD: brak paczek do manifestu", file=sys.stderr)
        return 1

    # Ta sama kolejność co serwer: numeracja naturalna, wydanie stabilne nad prerelease.
    def key(version: str) -> tuple[list[int], int, str]:
        numbers, _, pre = version.split("+")[0].partition("-")
        return [int(p) for p in numbers.split(".")], 0 if pre else 1, pre

    releases.sort(key=lambda item: key(item[0]), reverse=True)
    Path(args.out).write_text(json.dumps({"Releases": [entry for _, entry in releases]}, indent=2) + "\n",
                              encoding="utf-8")
    print(f"{args.out}: {len(releases)} paczek, najnowsza {releases[0][0]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())