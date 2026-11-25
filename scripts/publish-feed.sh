#!/usr/bin/env bash
# Publikuje katalog feedu Velopack (releases JSON + nupkg + instalatory) na VPS.
# Domyślnie DRY-RUN; kopiuje dopiero z --apply. Nie zawiera sekretów — uwierzytelnianie SSH użytkownika.
set -euo pipefail

REMOTE_HOST=${FEED_REMOTE_HOST:-debian@vps-b99a39c3.vps.ovh.net}
# DO UZGODNIENIA: docelowy katalog w /srv/synapsekit-boracik obsługiwany przez istniejący nginx.
# Podaj katalog kanału, np. /srv/synapsekit-boracik/phaket/feed/linux-x64-beta
REMOTE_DIR=${FEED_REMOTE_DIR:-}

apply=0
args=()
for arg in "$@"; do
  case "$arg" in
    --apply) apply=1 ;;
    -h|--help) sed -n '2,6p' "$0"; exit 0 ;;
    -*) echo "BŁĄD: nieznana opcja $arg" >&2; exit 1 ;;
    *) args+=("$arg") ;;
  esac
done

if (( ${#args[@]} != 1 )); then
  echo "Użycie: $0 [--apply] <katalog-feedu>" >&2
  echo "  FEED_REMOTE_DIR wymagane przy --apply (katalog kanału na VPS)." >&2
  exit 1
fi

local_dir=${args[0]}
[[ -d "$local_dir" ]] || { echo "BŁĄD: brak katalogu $local_dir" >&2; exit 1; }

shopt -s nullglob
jsons=("$local_dir"/releases.*.json)
(( ${#jsons[@]} > 0 )) || { echo "BŁĄD: brak releases.*.json w $local_dir" >&2; exit 1; }

for json in "${jsons[@]}"; do
  while IFS= read -r name; do
    [[ -f "$local_dir/$name" ]] || { echo "BŁĄD: $json wskazuje brakujący plik $name" >&2; exit 1; }
  done < <(grep -o '"FileName":"[^"]*"' "$json" | cut -d'"' -f4)
done

if (( apply )); then
  [[ -n "$REMOTE_DIR" ]] || { echo "BŁĄD: ustaw FEED_REMOTE_DIR (katalog kanału na VPS)." >&2; exit 1; }
  command -v rsync >/dev/null || { echo "BŁĄD: brak rsync" >&2; exit 1; }
fi

# Bez --delete: feed zbiera kolejne wersje, a katalog dzieli nginx z innymi usługami.
rsync_opts=(-av --human-readable)
(( apply )) || rsync_opts+=(--dry-run)

echo "Źródło:  $local_dir"
echo "Cel:     ${REMOTE_HOST}:${REMOTE_DIR:-<FEED_REMOTE_DIR — DO UZGODNIENIA>}"
(( apply )) || echo "Tryb:    DRY-RUN (użyj --apply, aby skopiować)"
du -sh "$local_dir"
rsync "${rsync_opts[@]}" "$local_dir"/ "${REMOTE_HOST}:${REMOTE_DIR}/"