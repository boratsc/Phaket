#!/usr/bin/env bash
# Pobiera realne artefakty wymagane przez testy integracyjne (PAK-71).
# Nie uruchamia pobranych plików, tylko weryfikuje SHA-256 i strukturę ZIP.
set -euo pipefail

dest=/tmp/opencode/phaket-research/artifacts
mkdir -p "$dest"

# nazwa|url|sha256|rozmiar w bajtach; puste pole = brak pinu w kodzie
fixtures=(
  "amongus-loader-6.0.700.zip|https://thunderstore.io/package/download/BepInEx/BepInExPack_AmongUs/6.0.700/|88bbefc79b4f30fe42e10dadb9f536dbe669532b8d4b9e3b08239d88146e1564|31432183"
  "valheim-loader-5.4.2202.zip|https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/5.4.2202/|d9c3b16992aa6a02069a921a3e4d613240fe291282bb67fe92261b00c6fabf8c|711730"
  "jotunn-2.26.1.zip|https://thunderstore.io/package/download/ValheimModding/Jotunn/2.26.1/|37a408db8c5eec1345ae345bf97423e201805e21c6ed912923f6eb3fa4c5aec9|783931"
  "morevanillabuilds-1.1.5.zip|https://thunderstore.io/package/download/Galateam/MoreVanillaBuilds/1.1.5/|3f498b4b7bb5d621ac48533426b7ff63dbc07b65605cc1ac67f6051674163150|18836"
  "tabsbuilderapi-1.2.1.zip|https://thunderstore.io/package/download/Cosmella/TabsBuilderApi/1.2.1/|f73799520b987384514f88c3899a92d2f3fc895c6dceec60fd0e776b0666aeec|36775"
  "outfit-presets-1.1.1.zip|https://thunderstore.io/package/download/Cosmella/Outfit_Presets/1.1.1/|46b011e61ff8d5d4d4d7504c6517122df6ce6b148984a5de595ec039b85f10a6|76767"
  "betterpolus-1.2.2.zip|https://thunderstore.io/package/download/Eliam/BetterPolus/1.2.2/|a065635ac957c9847173cfbd072f012a003ab54b3004c3794ae5decd365d0c53|63920"
  "betterpolus-1.2.3.zip|https://thunderstore.io/package/download/Eliam/BetterPolus/1.2.3/||"
  "tou-mira-1.7.9-beta1-x64.zip|https://github.com/AU-Avengers/TOU-Mira/releases/download/1.7.9-beta1/TouMira.v1.7.9-beta1-x64-Steam-Epic-MsStore.zip|b2e2163f7d0f41fb3cdf50352119925c42c80bc55a804507cf9e923aeb150afd|"
  "bepinex-il2cpp-x64-be793.zip|https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip||"
)

command -v curl >/dev/null || { echo "BŁĄD: brak curl" >&2; exit 1; }
command -v unzip >/dev/null || { echo "BŁĄD: brak unzip" >&2; exit 1; }

sha256_of() { sha256sum "$1" | awk '{print $1}'; }
zip_ok() { unzip -tqq "$1" >/dev/null 2>&1; }

downloaded=()
skipped=()
failed=()

for entry in "${fixtures[@]}"; do
  IFS='|' read -r name url sha size <<<"$entry"
  file="$dest/$name"

  if [[ -e "$file" ]]; then
    if ! zip_ok "$file"; then
      echo "BŁĄD: $name nie jest poprawnym ZIP — zostawiam istniejący plik bez zmian." >&2
      failed+=("$name")
      continue
    fi
    if [[ -n "$sha" && "$(sha256_of "$file")" != "$sha" ]]; then
      echo "BŁĄD: $name ma niezgodny sha256 (oczekiwano $sha, jest $(sha256_of "$file")) — zostawiam plik bez zmian." >&2
      failed+=("$name")
      continue
    fi
    skipped+=("$name")
    continue
  fi

  echo "Pobieram $name"
  tmp="$file.part"
  if ! curl -fL --retry 3 --retry-delay 2 --connect-timeout 30 --max-time 1800 -o "$tmp" "$url"; then
    echo "BŁĄD: nie udało się pobrać $name ($url)." >&2
    rm -f "$tmp"
    failed+=("$name")
    continue
  fi
  if [[ -n "$sha" && "$(sha256_of "$tmp")" != "$sha" ]]; then
    echo "BŁĄD: $name — niezgodny sha256 (oczekiwano $sha, otrzymano $(sha256_of "$tmp"))." >&2
    rm -f "$tmp"
    failed+=("$name")
    continue
  fi
  if [[ -n "$size" && "$(stat -c %s "$tmp")" != "$size" ]]; then
    echo "BŁĄD: $name — rozmiar $(stat -c %s "$tmp") B, oczekiwano $size B." >&2
    rm -f "$tmp"
    failed+=("$name")
    continue
  fi
  if ! zip_ok "$tmp"; then
    echo "BŁĄD: $name — pobrany plik nie jest poprawnym ZIP." >&2
    rm -f "$tmp"
    failed+=("$name")
    continue
  fi
  mv "$tmp" "$file"
  downloaded+=("$name")
done

report() {
  local label=$1
  shift
  printf '%s: %d\n' "$label" "$#"
  local item
  for item in "$@"; do printf '  %s\n' "$item"; done
}

echo
echo "Katalog: $dest"
report "Pobrano" "${downloaded[@]}"
report "Pominięto (już były i są poprawne)" "${skipped[@]}"
(( ${#failed[@]} == 0 )) || report "Błędy" "${failed[@]}"
(( ${#failed[@]} == 0 ))