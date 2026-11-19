# Phaket Desktop

Aplikacja desktopowa **Phaket** (.NET 10 + Avalonia 12.1.6) na Windows i Linux.
**Wydanie 0.1.0 „Czeladzki Saturn”** — lokalny, bezpieczny silnik modowania z wizualnym
edytorem przepisów (Recipe Studio) oraz elementami MVP: katalog buildów, graf
zależności, współdzielenie profili, zgłaszanie kompatybilności, wiedza ProtonDB
i logowanie Discord.

> PoC potwierdza *uniwersalność i bezpieczeństwo* lokalnego modowania, nie liczbę
> obsługiwanych gier. Zestaw testowy: Among Us (Unity IL2CPP) i Valheim (Unity Mono).
> Czat i konta online to MVP 0.1 — poza zakresem PoC.

## Zakres PoC

**Silnik przepisów OMRL** (`Phaket.Core`):
- parser YAML/JSON + JSON Schema + walidator semantyczny (zamknięty katalog operacji),
- deterministyczny planner (dry-run, `OperationPlan` bez mutacji),
- transakcyjny executor: journal przed mutacjami, atomowe zapisy, staging, rollback,
  wznowienie po awarii z detekcją rozbieżności,
- model własności zasobów (odinstalowanie moda A nie niszczy zmian moda B),
- bezpieczeństwo: granice ścieżek (także symlinki), zip-slip, bomby dekompresyjne,
  weryfikacja SHA-256, allowlista źródeł.

**UI (zgodny z makietami `phaket_makiety_v1`):**
- Biblioteka gier (karty z okładkami, „Graj" 1 klik przez `steam://rungameid`),
- Widok gry (hero, status, Graj/Instaluj/Mody/Kopia/Szczegóły),
- Mody, Profile, Kopie zapasowe, Kompatybilność (ProtonDB), Instalacja (stepper),
- Recipe Studio (round-trip YAML ↔ model, walidacja, podgląd planu),
- Ustawienia (język pl-PL/en), Pomoc, Konto (logowanie Discord).

Okładki i hero gier: oficjalne grafiki Steam CDN (`library_600x900_2x`, `library_hero`),
prawa autorskie wydawców (Innersloth / Iron Gate Studio), wyświetlanie w launcherze.
Na przyszłość: SteamGridDB / IGDB / RAWG (klucze API) dla gier spoza Steam.

**Elementy MVP:**
- katalog buildów z backendu (offline-honest, tylko metadane),
- graf zależności (przechodniość, cykle, konflikty),
- współdzielenie profili (export/import z podglądem diff; import tworzy szkic),
- zgłaszanie kompatybilności (zaufane/niezaufane rozdzielone),
- import zrzutu ProtonDB (ODbL), bezpieczne opcje uruchomienia (plan → zgoda → backup → cofnij),
- logowanie Discord (OAuth2 + PKCE, publiczny klient bez sekretu w aplikacji; client_id
  z publicznego config Clair; tokeny szyfrowane: Windows DPAPI / Linux AES-GCM).

## Wymagania

- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10/11 albo Linux z sesją X11/XWayland (natywny Wayland opt-in, niżej).
- Opcjonalnie: backend `PhaketServer` (osobne repo) dla katalogu buildów; aplikacja
  działa offline na lokalnym cache.

## Uruchomienie lokalne

```bash
dotnet run --project Phaket          # uruchomienie aplikacji
dotnet build Phaket.slnx             # build całej solucji
dotnet test Phaket.slnx              # testy (Core + headless UI, bez sesji graficznej)
```

Build PoC (self-contained):

```bash
dotnet publish Phaket/Phaket.csproj -c Release -r linux-x64 --self-contained true
dotnet publish Phaket/Phaket.csproj -c Release -r win-x64  --self-contained true
```

## Architektura

```
Phaket.Core/       logika lokalna (bez zależności UI):
  Domain/          model domeny (Game/GameEdition/Installation/Profile/OperationPlan/Journal)
  Omrl/            parser, schema, walidator, planner, katalog operacji, URI, i18n błędów
  Execution/       executor, journal, ledger własności, SafeArchive, InstallOrchestrator
  Platforms/       Steam (VDF/ACF), Legendary, ProtonDB cache, CompatibilityReportStore
  Catalog/         CatalogClient + CatalogStore (SQLite, snapshot/delty/ETag/offline)
  Profiles/        ProfileLockfile, ProfileShareService, DependencyGraph
Phaket/            aplikacja Avalonia (MVVM, CommunityToolkit.Mvvm):
  ViewModels/      MainWindow, Library, Game, Mods, Profiles, Backup, Compatibility,
                   Install, Builds, Share, Graph, Report, RecipeStudio, Settings, Help
  Views/           widoki XAML (design: grafit + bursztyn, Material.Icons)
  Services/        LocalizationService (pl-PL/en), ProfileStore, LaunchService, SteamLaunchOptions
  Assets/          okładki/hero gier, lwica (branding), zasoby i18n
Phaket.Core.Tests/ testy jednostkowe silnika (xUnit v3)
Phaket.Tests/      testy headless UI (Avalonia.Headless.XUnit) + rendery Skia
```

Zasada: **jedno źródło prawdy** — edytor wizualny i YAML operują na tym samym typowanym
modelu; plan jest deterministyczny i niezależny od locale; backend jest źródłem prawdy
dla katalogu, graf jest odbudowywalną projekcją.

## Linux: Wayland vs XWayland

Domyślnie aplikacja używa backendu **X11** — w sesji Wayland działa przez **XWayland**.
Świadomy wybór: backend X11 ma pełną dostępność (AT-SPI2, czytniki ekranu, np. Orca),
a natywny backend Wayland w Avalonia 12.1 jest **eksperymentalny i nie ma jeszcze
backendu AT-SPI**.

Natywny Wayland można włączyć warunkowo (opt-in):

```bash
PHAKET_NATIVE_WAYLAND=1 dotnet run --project Phaket
```

`UseWaylandWithFallback()` łączy się bezpośrednio z kompozytorem Wayland, a gdy jest
niedostępny — wraca do X11. Na Windows zmienna nie ma efektu.

Ograniczenia natywnego Wayland (Avalonia 12.1): brak AT-SPI (czytniki ekranu), brak
integracji KDE (globalne menu, ikony okien, blur-behind), renderowanie przez EGL.

Weryfikacja backendu (gniazda procesu):

```bash
lsof -p <PID> | grep -E 'wayland|X11'
# natywny Wayland: socket do $XDG_RUNTIME_DIR/wayland-*
# X11/XWayland:    socket do /tmp/.X11-unix/X*
```

## Dostępność

- Avalonia tworzy *automation peers*: Windows → UI Automation, Linux (X11/XWayland) →
  AT-SPI2 po D-Bus. Teksty kontrolek są udostępniane automatycznie; ikony i niejednoznaczne
  kontrolki mają `AutomationProperties.Name`.
- Motyw ciemny (grafit) z bursztynowym akcentem; wskaźnik fokusu widoczny; kontrast zgodny
  z tokenami `phaket_makiety_v1/spec/tokens.json`.
- Testy headless sprawdzają nazwy dostępne (WindowAutomationPeer, nawigacja). Zweryfikowano
  AT-SPI na XWayland (KDE); **rzeczywisty Windows i słyszalny test Orca — do wykonania**.

## Lokalizacja (pl-PL / en)

Wszystkie teksty UI pochodzą z zasobów `Phaket/Assets/i18n/{pl-PL,en}.json`
(`LocalizationService`, przełączanie bez przebudowy). Polski pierwszy, angielski pełny.
Identyfikatory, klucze, kody błędów i formaty danych pozostają niezależne od języka.
Błędy silnika: stabilny kod + parametry + komunikat pl/en; ten sam przepis w pl/en daje
identyczny `OperationPlan`. Testy sprawdzają pary kluczy i placeholdery.

## Testy i weryfikacja

- `Phaket.Core.Tests`: silnik (walidator, planner, executor, rollback, własność,
  bezpieczeństwo, katalog, graf, współdzielenie, raporty) — ~192 testy.
- `Phaket.Tests`: headless UI (nawigacja, lokalizacja, dostępność, round-trip Studio)
  + `RenderTests` (zrzuty Skia 1280×800 do `/tmp/opencode/phaket-render`).
- CI (GitHub Actions): build + testy na `ubuntu-26.04` i `windows-latest`.
- E2E: install→verify→uninstall (sandbox), katalog desktop→backend, share round-trip.
- **Pierwszy realny przepis** (`Phaket.Core.Tests/fixtures/recipes/among-us-betterpolus.yaml`):
  BetterPolus 1.2.2 + loader BepInEx 6.0.700 z Thunderstore — prawdziwe URL-e pobierania
  i SHA-256 paczek (zweryfikowane 2026-11-13), `stripComponents: 1` dla folderu-wrapper
  paczki loadera, opcje uruchomienia Linux/Proton (`WINEDLLOVERRIDES="winhttp.dll=n,b"
  %command%`) jako dane definicji gry z proweniencją (docs.bepinex.dev). Testy planują
  i wykonują przepis na fixture o strukturze identycznej z realnymi paczkami.
- **Macierz wersji Town of Us Mira** (`among-us-tou-mira-1.7.3.yaml`,
  `among-us-tou-mira-1.6.3-beta2.yaml`): wydanie moda ↔ dokładna wersja gry
  (1.7.3 → 2026.8.18, 1.6.3-beta2 → 2026.6.5) jak `ModVersion`/`AmongVersion` w SUSModderze,
  ale vanilla provisionuje operacja `game.provision` przez oficjalne narzędzia
  (DepotDownloader `-app 945360 -depot 945361 -manifest <pin>` / legendary dla Epic),
  z cache per wersja i markerem — nigdy z biblioteki Steam użytkownika i nigdy
  z prywatnego serwera. Warianty paczek per sklep (x86 Steam / x64 Epic) wybierane
  warunkami `store:`; uninstall przepisu zostawia provisionowaną bazę gry.
- **Pętla wersji gry w UI**: widok Instalacji wybiera przepis z `Phaket.Core/recipes`
  i wersję gry — „Najnowsza" albo konkretna z datą wydania z katalogu buildów
  (`releasedAt` z backendu, odkrywane automatycznie z publicznych źródeł); wybór
  resolwuje się raz przy planowaniu, więc plan pozostaje deterministyczny.
  Widok Buildów pokazuje datę wydania obok statusu. Vanilla pobiera host przez
  `ToolBaseGameSource` (DepotDownloader/legendary, ścieżki z `PHAKET_DEPOTDOWNLOADER`
  /`PHAKET_LEGENDARY`); bez skonfigurowanych narzędzi plan kończy się czytelnym
  błędem, nigdy cichym pobraniem z niezaufanego źródła.

## Znane ograniczenia PoC

- Instalacja w UI działa na **sandbox fixture** (nie na prawdziwej grze); E2E na realnej
  instalacji wymaga zgody i zasobów użytkownika.
- Silnik **nie pobiera** artefaktów ani gry vanilla: host przygotowuje pliki w katalogu
  pakietów (pobranie z Thunderstore/GitHub po weryfikacji SHA-256; gra Steam wyłącznie
  przez narzędzia typu DepotDownloader, Epic przez legendary — jak w SUSModderze).
  Plan z brakującym artefaktem zgłasza `requires-download` i czytelny błąd przy wykonaniu.
- Dane grafu/współdzielenia/ProtonDB w demo są przykładowe; import zrzutu ODbL pozwala
  wczytać realne dane.
- Natywny Wayland bez AT-SPI; rzeczywisty Windows GUI i czytnik Orca niezweryfikowane.
- Zatwierdzenie specyfikacji OMRL/ADR-038 — otwarte, blokuje publiczne wydanie.
  Licencja zdecydowana: LGPL-3.0-or-later (plik `LICENSE`, szczegóły w `NOTICE`).

## English summary

Phaket desktop 0.1.0 "Czeladzki Saturn": a local, safe modding engine (OMRL
parser/validator, deterministic dry-run planner, transactional executor with journal +
rollback + ownership) with a mockup-matching UI (library, game hub, mods, profiles,
backups, compatibility, install stepper, Recipe Studio, settings, help) and MVP bridges
(build catalog, dependency graph, profile sharing, compatibility reporting, ProtonDB
ODbL import, safe launch options). .NET 10 + Avalonia 12.1.6, Windows + Linux (XWayland
default, native Wayland opt-in without AT-SPI). pl-PL/en localization. ~210 tests +
headless UI + Skia renders; CI on ubuntu-26.04 + windows-latest. Chat is MVP 0.1 (out of
PoC scope); Discord sign-in (OAuth2+PKCE, SUSModder/Clair pattern) is implemented.
The first real recipe (BetterPolus + BepInEx pack from Thunderstore, real digests and
Linux/Proton launch options as definition data) is planned and executed in tests; the
engine itself never downloads artifacts or the base game (host responsibility).
A Town of Us Mira version matrix (mod release pinned to an exact game build) shows
`game.provision`: vanilla fetched per store via DepotDownloader/legendary with a
per-version cache, platform/arch pack variants per store, and uninstall that keeps
the provisioned base game.
Licensed LGPL-3.0-or-later; OMRL/ADR-038 approval pending
(blocks public release only).

## Licencja / License

**LGPL-3.0-or-later** — GNU Lesser General Public License v3 lub późniejsza.
Pełny tekst: `LICENSE` (LGPL-3.0) + `COPYING` (GPL-3.0, włączony przez odniesienie).
Prawa autorskie, zakres i zależności stron trzecich: `NOTICE`. Backend
PhaketServer pozostaje zamknięty (all rights reserved).

**LGPL-3.0-or-later** — GNU Lesser General Public License v3 or later.
Full text: `LICENSE` (LGPL-3.0) + `COPYING` (GPL-3.0, incorporated by reference).
Copyright, scope and third-party dependencies: `NOTICE`. The PhaketServer
backend remains proprietary (all rights reserved).