#!/usr/bin/env python3
# Wdrożenie feedu wydania (release.yml) do działającego stacku serwera PAK-110.
# Na hoście serwera: paczki kanału -> wolumen releases_packages (serwuje nginx www pod
# /releases/<kanał>/<os>), manifesty -> wolumen releases_manifests (czyta API).
# Layout kanału jest tym, który serwuje nginx i ogłasza API: /releases/{channel}/{os}/File
# (PhaketServer README, Releases.cs: downloadBaseUrl kończy się na /releases/).
#
#   python3 scripts/server-deploy.py --channel beta --stage-dir ~/PhaketServer/staging            # przygotuj
#   ... rsync paczek do <stage-dir>/{win,linux} i manifestów do <stage-dir>/manifests/ ...
#   python3 scripts/server-deploy.py --channel beta --stage-dir ~/PhaketServer/staging --clean    # opublikuj
#
# FAZY publikacji (--clean): 1) walidacja KOMPLETU obu systemów i obu manifestów (bez kopiowania),
# 2) paczki, 3) Phaket-Setup.exe (temp + mv wewnątrz wolumenu), 4) manifesty na końcu.
# Przerwany deploy zostawia stary manifest, więc klient nie dostanie 404 ani nowej paczki bez wpisu.
# Katalogi paczek nie są czyszczone: feed trzyma poprzednie wersje kanału.
#
# Mounty, na których opiera się ten skrypt (potwierdzone `docker inspect` na stage, 27.11.2026):
#   phaketserver-www-1:  wolumen */releases_packages -> /usr/share/nginx/html/releases (ro)
#   phaketserver-api-1:  wolumen */releases_manifests -> /data/releases (ro) — www tego nie montuje,
#   dlatego `docker cp` do api jest jedyną drogą (zapis przez ro jest dozwolony dla roota,
#   `docker cp` w tej wersji Dockera działa mimo :ro).
import argparse
import json
import posixpath
import subprocess
import sys
from pathlib import Path

CHANNELS = ["beta", "release"]
SYSTEMS = ["win", "linux"]
# USŁUGA compose (nie kontener): w PhaketServer/compose.yaml serwis www serwuje paczki; api ma
# manifesty, a nie montuje wolumenu paczek. Nazwy kontenera (<projekt>-www-1) zależą od katalogu.
PACKAGES_SERVICE = "www"
MANIFESTS_SERVICE = "api"
# Wewnątrz wolumenu releases_packages, który www montuje jako /usr/share/nginx/html/releases.
PACKAGES = "/usr/share/nginx/html/releases"
# Wewnątrz wolumenu releases_manifests, który api montuje jako /data/releases.
MANIFESTS = "/data/releases"
# Stała nazwa wpisu w manifeście (stub szuka dokładnie jej); podmień atomowo: temp + mv.
SETUP = "Phaket-Setup.exe"

_container: dict[str, str] = {}


def run(*command: str) -> str:
    print("+", " ".join(command), flush=True)
    return subprocess.run(command, check=True, cwd=Path.home() / "PhaketServer",
                          capture_output=True, text=True).stdout


def container_id(service: str) -> str:
    # `docker compose ps -q` zna nazwę projektu i kontenerów z compose.yaml; zawężenie do jednej
    # usługi eliminuje `nginx` bez wolumenu paczek i postgres z `www` w nazwie wolumenu.
    if service not in _container:
        found = run("docker", "compose", "ps", "-q", service).split()
        if not found:
            raise RuntimeError(f"brak działającego kontenera usługi {service} (docker compose ps {service})")
        _container[service] = found[0]
    return _container[service]


def copy(source: str, service: str, destination: str) -> None:
    run("docker", "cp", source, f"{container_id(service)}:{destination}")


def publish_setup(source: Path, service: str, folder: str) -> None:
    # temp w tym samym katalogu (ten sam system plików) + mv: czytelnik widzi starą albo nową wersję.
    temp = posixpath.join(folder, f".{SETUP}.tmp")
    copy(str(source), service, temp)
    run("docker", "exec", container_id(service), "mv", temp, posixpath.join(folder, SETUP))


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
                run("docker", "exec", container_id(PACKAGES_SERVICE), "mkdir", "-p", f"{PACKAGES}/{args.channel}/{system}")
        except (RuntimeError, subprocess.CalledProcessError) as error:
            print(f"BŁĄD: {error}", file=sys.stderr)
            return 1
        print(f"{root}: gotowy na rsync (kanał {args.channel})")
        return 0

    # FAZA 1 — walidacja kompletu, zanim cokolwiek trafi do wolumenów. Brak pliku w jednym systemie
    # nie może zostawić drugiego systemu już opublikowanego ze starym manifestem.
    manifests = root / "manifests"
    expected = {system: manifests / f"releases.{args.channel}.{system}.json" for system in SYSTEMS}
    missing = [str(path) for path in expected.values() if not path.is_file()]
    if missing:
        print(f"BŁĄD: brak manifestów: {', '.join(missing)}", file=sys.stderr)
        return 1
    sources: dict[str, list[Path]] = {}
    for system in SYSTEMS:
        path = expected[system]
        source = root / system
        try:
            files = sorted(p for p in source.iterdir() if p.is_file()) if source.is_dir() else []
            manifest = json.loads(path.read_text(encoding="utf-8"))
            # Manifest czyta API (Releases.cs Read): Releases[] z samymi nazwami plików, unikalnymi.
            names = [entry["File"] for entry in manifest["Releases"]]
            if not names or not all(isinstance(name, str) and "/" not in name and "\\" not in name for name in names):
                raise ValueError("puste Releases albo File ze ścieżką")
            if len(set(names)) != len(names):
                raise ValueError("zduplikowany File w Releases")
        except (OSError, KeyError, TypeError, ValueError, json.JSONDecodeError) as error:
            print(f"BŁĄD: manifest {path} nie spełnia kontraktu serwera: {error}", file=sys.stderr)
            return 1
        # Manifest nie może wskazywać pliku, którego nie ma w katalogu kanału w chwili publikacji,
        # a w paczkach nie może zostać nic bez wpisu (każdy plik musi być w manifeście).
        absent = [name for name in names if not (source / name).is_file()]
        if absent:
            print(f"BŁĄD: manifest wskazuje brakujące pliki ({system}): {', '.join(absent)}", file=sys.stderr)
            return 1
        unlisted = [p.name for p in files if p.name not in names]
        if unlisted:
            print(f"BŁĄD: paczki bez wpisu w manifeście ({system}): {', '.join(unlisted)}", file=sys.stderr)
            return 1
        if not files:
            print(f"BŁĄD: brak paczek w {source}", file=sys.stderr)
            return 1
        sources[system] = files

    # FAZA 2 — paczki kanału, bez Phaket-Setup.exe (idzie na końcu, atomowo).
    try:
        for system in SYSTEMS:
            folder = f"{PACKAGES}/{args.channel}/{system}"
            for path in sources[system]:
                if path.name != SETUP:
                    copy(str(path), PACKAGES_SERVICE, f"{folder}/{path.name}")

        # FAZA 3 — stały Setup.exe per kanał, temp + mv wewnątrz wolumenu.
        for system in SYSTEMS:
            source = root / system / SETUP
            if source.is_file():
                publish_setup(source, PACKAGES_SERVICE, f"{PACKAGES}/{args.channel}/{system}")

        # FAZA 4 — manifesty (www ich nie montuje; czyta je api z własnego wolumenu).
        for path in expected.values():
            copy(str(path), MANIFESTS_SERVICE, f"{MANIFESTS}/{path.name}")
    except (RuntimeError, subprocess.CalledProcessError) as error:
        print(f"BŁĄD: publikacja nie powiodła się: {error}", file=sys.stderr)
        return 1

    for path in sorted(root.rglob("*"), reverse=True):
        path.rmdir() if path.is_dir() else path.unlink()
    print(f"opublikowano kanał {args.channel}: /releases/{args.channel}/win, /releases/{args.channel}/linux")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())