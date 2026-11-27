#!/usr/bin/env python3
# Wdrożenie feedu wydania (release.yml) do działającego stacku serwera PAK-110.
# Na hoście serwera: paczki kanału -> releases_packages/.../releases/<kanał>/<os>, manifesty ->
# releases_manifests/data/releases (volumeny z PhaketServer/compose.yaml).
# Layout kanału jest tym, który serwuje nginx i ogłasza API: /releases/{channel}/{os}/File
# (PhaketServer README, Releases.cs: downloadBaseUrl kończy się na /releases/).
#
#   python3 scripts/server-deploy.py --channel beta --stage-dir ~/PhaketServer/staging            # przygotuj
#   ... rsync paczek do <stage-dir>/{win,linux} i manifestów do <stage-dir>/manifests/ ...
#   python3 scripts/server-deploy.py --channel beta --stage-dir ~/PhaketServer/staging --clean    # opublikuj
#
# Kolejność publikacji: paczki, dopiero potem manifesty (przerwany deploy zostawia stary manifest).
# Katalogi paczek nie są czyszczone: feed trzyma poprzednie wersje kanału.
import argparse
import json
import subprocess
import sys
from pathlib import Path

CHANNELS = ["beta", "release"]
SYSTEMS = ["win", "linux"]
# Kontener i ścieżka wewnątrz obrazu (nazwa serwisu compose zależy od projektu — stąd wykrywanie).
SERVICE = "nginx"
# Wewnątrz wolumenu releases_packages, który nginx montuje jako /usr/share/nginx/html/releases.
PACKAGES = "/usr/share/nginx/html/releases"
# Wewnątrz wolumenu releases_manifests, który API montuje jako /data/releases.
MANIFESTS = "/data/releases"


def run(*command: str) -> str:
    print("+", " ".join(command), flush=True)
    return subprocess.run(command, check=True, cwd=Path.home() / "PhaketServer",
                          capture_output=True, text=True).stdout


_container: list[str] = []


def container_id() -> str:
    if not _container:
        found = run("docker", "ps", "-q", "--filter", f"name={SERVICE}").split()
        if not found:
            raise RuntimeError(f"brak działającego kontenera {SERVICE} na hoście")
        _container.append(found[0])
    return _container[0]


def copy(source: str, destination: str) -> None:
    run("docker", "cp", source, f"{container_id()}:{destination}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--channel", required=True, choices=CHANNELS)
    parser.add_argument("--stage-dir", required=True, type=Path)
    parser.add_argument("--clean", action="store_true", help="opublikuj z stage-dir; bez tej flagi tylko przygotuj")
    args = parser.parse_args()

    root = args.stage_dir.expanduser().resolve()
    # Katalog stagingu musi leżeć w repozytorium serwera, nie obok; reszta ścieżek jest stała.
    if root.name != "staging" or root.parent.name != "PhaketServer":
        print(f"BŁĄD: --stage-dir musi być ~/PhaketServer/staging, dostałem {root}", file=sys.stderr)
        return 1

    if not args.clean:
        for system in SYSTEMS:
            (root / system).mkdir(parents=True, exist_ok=True)
        (root / "manifests").mkdir(parents=True, exist_ok=True)
        try:
            for system in SYSTEMS:
                run("docker", "exec", container_id(), "mkdir", "-p", f"{PACKAGES}/{args.channel}/{system}")
        except (RuntimeError, subprocess.CalledProcessError) as error:
            print(f"BŁĄD: {error}", file=sys.stderr)
            return 1
        print(f"{root}: gotowy na rsync (kanał {args.channel})")
        return 0

    manifests = root / "manifests"
    expected = [manifests / f"releases.{args.channel}.{system}.json" for system in SYSTEMS]
    missing = [str(path) for path in expected if not path.is_file()]
    if missing:
        print(f"BŁĄD: brak manifestów: {', '.join(missing)}", file=sys.stderr)
        return 1
    manifests = {}
    for system, path in zip(SYSTEMS, expected):
        try:
            manifest = json.loads(path.read_text(encoding="utf-8"))
            names = [entry["File"] for entry in manifest["Releases"]]
            if not names or not all(isinstance(name, str) and "/" not in name for name in names):
                raise ValueError("puste Releases albo File ze ścieżką")
            manifests[system] = manifest
        except (OSError, KeyError, TypeError, ValueError, json.JSONDecodeError) as error:
            print(f"BŁĄD: manifest {path} nie spełnia kontraktu serwera: {error}", file=sys.stderr)
            return 1

    try:
        for system in SYSTEMS:
            source = root / system
            files = sorted(path for path in source.iterdir() if path.is_file()) if source.is_dir() else []
            # Manifest nie może wskazywać pliku, którego nie ma w katalogu kanału w chwili publikacji.
            for name in manifests[system].get("Releases", []):
                if not (source / name["File"]).is_file():
                    print(f"BŁĄD: manifest wskazuje brakujący plik {name['File']} ({system})", file=sys.stderr)
                    return 1
            if not files:
                print(f"BŁĄD: brak paczek w {source}", file=sys.stderr)
                return 1
            folder = f"{PACKAGES}/{args.channel}/{system}"
            # docker cp nie ma --no-overwrite: kopiujemy do wolumenu przez kontener nginx.
            for path in files:
                copy(str(path), f"{folder}/{path.name}")

        for path in expected:
            copy(str(path), f"{MANIFESTS}/{path.name}")
    except (RuntimeError, subprocess.CalledProcessError) as error:
        print(f"BŁĄD: publikacja nie powiodła się: {error}", file=sys.stderr)
        return 1

    for path in sorted(root.rglob("*"), reverse=True):
        path.rmdir() if path.is_dir() else path.unlink()
    print(f"opublikowano kanał {args.channel}: /releases/{args.channel}/win, /releases/{args.channel}/linux")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())