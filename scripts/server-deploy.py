#!/usr/bin/env python3
# Wdrożenie feedu wydania (release.yml) do działającego stacku serwera PAK-110.
# Na hoście serwera nazwa release-<os> -> pliki w releases_packages, releases_manifests -> manifesty (compose.yaml).
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
# Ścieżki wewnątrz obrazów z PhaketServer/compose.yaml.
PACKAGES = {system: f"/usr/share/nginx/html/releases/{system}" for system in SYSTEMS}
MANIFESTS = "/data/releases"


def run(*command: str) -> None:
    print("+", " ".join(command), flush=True)
    subprocess.run(command, check=True, cwd=Path.home() / "PhaketServer")


def staging_dir(root: Path, system: str | None = None, manifests: bool = False) -> Path:
    return root / ("manifests" if manifests else system or "")


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
        for system in SYSTEMS:
            run("docker", "compose", "exec", "-T", "nginx", "sh", "-c", f"mkdir -p {PACKAGES[system]}")
        print(f"{root}: gotowy na rsync")
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

    for system, container_path in PACKAGES.items():
        source = root / system
        files = sorted(path for path in source.iterdir() if path.is_file()) if source.is_dir() else []
        # Manifest (<system>/releases.<channel>.<system>.json) nie może wskazywać pliku, którego
        # nie ma w katalogu paczek w chwili publikacji.
        for name in manifests[system].get("Releases", []):
            if not (source / name["File"]).is_file():
                print(f"BŁĄD: manifest wskazuje brakujący plik {name['File']}", file=sys.stderr)
                return 1
        if not files:
            print(f"BŁĄD: brak paczek w {source}", file=sys.stderr)
            return 1
        # docker cp nie ma --no-overwrite: kopiujemy do wolumenu przez kontener nginx.
        for path in files:
            run("docker", "compose", "cp", str(path), f"nginx:{container_path}/{path.name}")

    for path in expected:
        run("docker", "compose", "cp", str(path), f"api:{MANIFESTS}/{path.name}")

    for path in sorted(root.rglob("*"), reverse=True):
        path.rmdir() if path.is_dir() else path.unlink()
    print(f"opublikowano kanał {args.channel}: {', '.join(path.name for path in expected)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())