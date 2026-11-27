# Runbook wydania Phaket Desktop

Dokument operacyjny (wewnętrzny): jak wydać kanał **beta**, jak promować go do **prod**
(kanał `release`), jak sprawdzić aktualizację end-to-end (Windows + Linux) i co zrobić,
gdy wydanie trzeba wycofać.

**Każde uruchomienie workflow `Release` JEST wydaniem i wymaga wyraźnej zgody właściciela.**
Push do `beta`/`main` i tagi również wymagają wyraźnej zgody; commity i push do gałęzi
wersji `0.1-czeladzki-saturn` są dozwolone bez pytania (AGENTS.md). Deploy na **stage**
jest dozwolony bez każdorazowej zgody; **prod** (phaket.app / VPS OVH) — tylko za zgodą.

---

## 0. Źródła prawdy

| Element | Plik w PhaketDesktop | Plik w PhaketServer |
|---|---|---|
| Workflow wydania | `.github/workflows/release.yml` | — |
| Stub instalatora (workflow) | `.github/workflows/installer-stub.yml` | — |
| Generacja manifestu | `scripts/feed-manifest.py` | — |
| Wdrożenie na serwer | `scripts/server-deploy.py` | `scripts/server-deploy.py` (uruchamiany na VPS; kopia w repo serwera musi być aktualna) |
| Kontrakt `/api/releases` | czytany w `Phaket/Services/UpdateService.cs` (`ServerFeedSource`) | `Phaket.Api/Releases.cs` |
| Stub instalatora | `installer/Phaket.InstallerStub/` (`InstallRunner.cs`, `Manifest.cs`, `FirstInstall.cs`) | — |

Kontrakt API (release.yml, nagłówek; Releases.cs):
`GET /api/releases?channel=<release|beta>&os=<win|linux>` →
`{success, latestVersion, downloadBaseUrl, releases[{Version, File, SHA256, SHA1?, Size}]}`.
Serwer składa odpowiedź z manifestu `releases.<channel>.<os>.json`, sortuje wersje malejąco
i zwraca jedną odpowiedź będącą nadzbiorem obu kontraktów (`manifest.Releases` dla klienta
Velopack, `releases[]` + `latestVersion` dla stubu) (Releases.cs, `Serve`).

**Kanoniczny layout paczek (ustalony 27.11.2026):** `/releases/{channel}/{os}/{File}` —
`channel` ∈ `beta|release`, `os` ∈ `win|linux`. Wynika z serwera, nie z workflow:
`PhaketServer/README.md:23` („Paczki serwuje statycznie nginx pod `/releases/{channel}/`”),
`PhaketServer/compose.yaml:49-50` („Velopack packages served under `/releases/{channel}/`”),
`Releases.cs:59` (`File` to jeden segment, klient dokleja go po `downloadBaseUrl`, które kończy
się na `/releases/`), `Releases.cs:20-23` (`downloadBaseUrl` = `https://phaket.app/releases/`).
Workflow pakuje więc do `/releases/{channel}/{os}/`, a `File` w manifeście pozostaje samą nazwą
pliku. `Phaket-Setup.exe` jest per kanał, bo leży w katalogu kanału.

---

## 1. Wymagania wstępne (działanie właściciela repo)

Workflow **nie zakłada**, że poniższe istnieje — bez tego deploy padnie na kroku
„Configure SSH” (release.yml, krok `Configure SSH`).

### 1.1 Sekrety i zmienne GitHub (Settings → Secrets and variables → Actions)

Sekrety (zakładka *Secrets*):

1. Wygeneruj klucz deployu:
   `ssh-keygen -t ed25519 -C phaket-ci -f phaket-ci -N ""`
2. Klucz **publiczny** (`phaket-ci.pub`) dopisz na serwerze do `~debian/.ssh/authorized_keys`.
3. Klucz **prywatny** wklej jako sekret `DEPLOY_SSH_PRIVATE_KEY` w formie base64:
   `base64 -w0 phaket-ci` (workflow sam go dekoduje: `base64 -d > ~/.ssh/deploy`).

Zmienne (zakładka *Variables*) — workflow czyta je jako `vars.*`:

4. `DEPLOY_HOST` = `vps-b99a39c3.vps.ovh.net`
5. `DEPLOY_USER` = `debian`
6. (opcjonalnie) `RELEASES_DOWNLOAD_BASE_URL` — prefiks paczek w manifeście. Workflow **czyta** go
   z `vars` jako `env.DOWNLOAD_BASE_URL` i porównuje z katalogiem kanału przebiegu
   (`https://phaket.app/releases/<channel>/`) w kroku „Check the download base URL matches the
   channel” — rozjazd zatrzymuje deploy przed SSH. Samą wartość `downloadBaseUrl` w odpowiedzi
   ustawia **serwer** ze swojego `.env` compose (`https://phaket.app/releases/` domyślnie —
   PhaketServer/compose.yaml:9); zmiana zmiennej w GitHubie nie zmienia feedu, zmienia się na VPS.

Uprawnienia: `GITHUB_TOKEN` wystarcza do draft release (`permissions: contents: write`);
brak sekretów płatnych (release.yml, nagłówek sekcji „Konfiguracja”).
**Sekretów nie zapisujemy w repozytorium** — repozytorium desktopowe jest publiczne.

### 1.2 Narzędzia na runnerach

- .NET SDK 10.0.x — `actions/setup-dotnet` w każdym jobie (release.yml, krok `setup-dotnet`).
- vpk (Velopack) **1.2.161** — instalowany w workflow, nie na runnerze:
  `dotnet tool install --tool-path .vpk vpk --version "$VPK_VERSION"`
  (release.yml, `env.VPK_VERSION`, kroki `Install vpk` / `Install vpk and AppImage tooling`).
- Linux: `squashfs-tools` i `file` instalowane przez `apt-get` na `ubuntu-26.04`
  (release.yml, krok `Install vpk and AppImage tooling`).
- Windows: MSVC obecny na `windows-latest` (potrzebny tylko dla AOT stubu — installer-stub.yml,
  komentarz przy `Test stub logic`).

### 1.3 Dostęp do serwera

- SSH `debian@vps-b99a39c3.vps.ovh.net` z klucza w pkt 1.1.
- Na serwerze: repozytorium serwera w `~/PhaketServer` (workflow używa `SERVER_ROOT: ~/PhaketServer`)
  i działający stack compose z wolumenami `releases_packages` i `releases_manifests`
  (PhaketServer/compose.yaml; release.yml, nagłówek).
- **Mounty (potwierdzone `docker inspect` na stage 27.11.2026, docker 29.10.0):**
  `phaketserver-www-1` montuje `<projekt>_releases_packages` → `/usr/share/nginx/html/releases`
  (**ro**), `phaketserver-api-1` montuje `<projekt>_releases_manifests` → `/data/releases` (**ro**)
  i `_signing_keys`. Kontener **www nie montuje wolumenu manifestów, a api nie montuje wolumenu
  paczek** — dlatego `server-deploy.py` kopiuje paczki przez `docker cp` do www, a manifesty do api
  (zapis przez `:ro` działa, bo `docker cp` wykonuje daemon jako root). Serwisy compose to `www`
  i `api` (`docker compose ps`), nazwy kontenerów `<projekt>-www-1` / `<projekt>-api-1`.

### Checklist wstępny

- [ ] Sekret `DEPLOY_SSH_PRIVATE_KEY` ustawiony (base64)
- [ ] Zmienne `DEPLOY_HOST`, `DEPLOY_USER` ustawione
- [ ] Klucz publiczny w `authorized_keys` na VPS
- [ ] `~/PhaketServer` na VPS zawiera `scripts/server-deploy.py` i uruchomiony stack
- [ ] CI (`desktop-ci.yml`) zielony na gałęzi `0.1-czeladzki-saturn`
- [ ] Zgoda właściciela na wydanie

---

## 2. Wydanie beta

Kanał beta = wersja z przyrostkiem prerelease, np. `0.1.1-beta.1` (release.yml, opis inputu
`version`). Wersja jest przekazywana do vpk jako `-v` — musi być wersją semver.

### 2.1 Uruchomienie

1. GitHub → Actions → **Release** → *Run workflow* (branch: `0.1-czeladzki-saturn`).
2. Inputy:
   - `version`: np. `0.1.1-beta.1` (wymagany)
   - `channel`: `beta`
   - `publishTarget`:
     | wartość | draft GitHub | serwer | publikacja draftu |
     |---|---|---|---|
     | `none` | — | — | — (tylko artefakty Actions) |
     | `github-draft` | tak | **nie** | nie — draft zostaje, publikacja ręczna |
     | `github-draft+server` | tak | tak | tak (`gh release edit --draft=false` na końcu) |
3. Job `validate` (pierwszy, bez niego nie rusza build) wymaga `version` semver i zgodności
   z kanałem: `beta` → wyłącznie przyrostek `-beta` / `-beta.N` (np. `0.1.1-beta.1`; `-rc.1` jest
   odrzucany), `release` → **bez** żadnego przyrostka. Niezgodność zatrzymuje przebieg przed
   pakowaniem (release.yml, krok „Version matches the channel”).
4. `concurrency: release-<channel>` blokuje dwa przebiegi tego samego kanału i nie kasuje
   przebiegu w toku (release.yml, `concurrency`).

### 2.2 Co się dzieje po kolei

| Etap (job / krok) | Efekt |
|---|---|
| `validate`: `Version matches the channel` | semver + zgodność `version`↔`channel`; przy błędzie reszta jobów nie startuje |
| `build-windows`: `Build and test` | `dotnet build` solucji **przed** pakowaniem — wydanie nie może powstać z kodu, który nie przechodzi testów (release.yml) |
| `build-windows`: `Publish win-x64` → `Pack Setup.exe and Portable.zip` | self-contained publish; `vpk pack` bez `--channel` (kanał należy do serwera) |
| `build-windows`: `Name the installer for the feed` | `Phaket-<wersja>-Setup.exe` → `Phaket-Setup.exe` (stała nazwa, bo stub szuka dokładnie tego wpisu; plik jest per kanał). `find .` po `cd releases/win-x64` |
| `build-windows`: `SHA256SUMS (Windows)` | `sha256sum -- * > SHA256SUMS.txt` w `releases/win-x64` |
| `build-linux`: `Publish` → `Pack AppImage` | AppImage, `--categories Utility`, `--compression gzip` (release.yml) |
| `build-linux`: `SHA256SUMS (Linux)` | sumy w `releases/linux-x64` |
| oba joby → artefakty | `phaket-win-x64-beta`, `phaket-linux-x64-beta` (nazwa z inputem `channel`) |
| `draft-release`: `Draft GitHub Release` | **tylko dla `publishTarget != none`**: `gh release create "v<wersja>" --draft` z paczkami (`assets/*`); note odsyła do `https://phaket.app/download/PhaketSetup.exe` (§6, §7) |
| `deploy`: `Check the download base URL matches the channel` | **tylko dla `github-draft+server`**: `RELEASES_DOWNLOAD_BASE_URL` musi wskazywać `<base>/releases/<channel>/` |
| `deploy`: `Configure SSH` | dekoduje klucz, `ssh-keyscan` hosta |
| `deploy`: `Build manifests and check the file set` | `feed-manifest.py --version <wersja> --out manifests/releases.<channel>.<os>.json -- assets/<os>-x64/*` dla obu systemów, potem kontrola: każdy plik artefaktu ma wpis w manifeście i odwrotnie. **Przed jakimkolwiek rsync** — brak paczki Linux nie wyśle Windows |
| `deploy`: `Upload release files to the server volumes` | seria `ssh`: `server-deploy.py` bez `--clean` (tworzy `~/PhaketServer/staging/{win,linux,manifests}` i katalogi `/releases/<channel>/<os>` w wolumenie paczek przez `docker compose ps -q www`), potem `rsync` paczek i `rsync` manifestów |
| `deploy`: `Commit release into the server volumes` | `server-deploy.py --clean`, 4 fazy: **(1)** walidacja kompletu obu systemów (manifesty + paczki: żadnego brakującego pliku, żadnej paczki bez wpisu) — nic nie kopiuje; **(2)** `docker cp` paczek → www (`/releases/<channel>/<os>`, bez `Phaket-Setup.exe`); **(3)** `Phaket-Setup.exe` per kanał przez temp + `mv` **wewnątrz wolumenu**; **(4)** manifesty → api (`/data/releases`). Czyści staging |
| `deploy`: `Publish GitHub Release` | `gh release edit "v<wersja>" --draft=false` — tylko przy `github-draft+server` |

Kolejność jest wymuszona: pliki paczek trafiają do wolumenu **przed** manifestem, więc
przerwany deploy zostawia stary manifest i klient nie dostanie 404 na brakującej paczce
(release.yml, komentarz przy `Commit release`; server-deploy.py, nagłówek).

`Phaket-Setup.exe` jest nadpisywany per kanał. Okno niespójności: między fazami 2 a 3 w katalogu
kanału leży nowa paczka ze starym Setup.exe — żaden wpis manifestu go wtedy nie wskazuje (stary
manifest wskazuje stary Setup.exe, który wciąż leży na miejscu do `mv`), a nowy manifest publikuje
się dopiero w fazie 4. `mv` jest atomowy w obrębie jednego systemu plików, więc klient widzi
starą **albo** nową wersję pliku, nigdy częściową.

### 2.3 Gdzie wylądują artefakty

- **GitHub**: draft release `v<wersja>` z paczkami per OS i `SHA256SUMS.txt` (draft-release) —
  tylko dla `publishTarget` ≠ `none`.
- **Serwer / paczki** (tylko `github-draft+server`): wolumen `releases_packages` →
  `/usr/share/nginx/html/releases/<channel>/win/` i `.../linux/`, serwowane pod
  `https://phaket.app/releases/<channel>/<os>/` (release.yml, nagłówek „Struktura katalogów”;
  compose.yaml `www`; layout w §0). Katalogi kanału nie są czyszczone (`--clean` czyści tylko
  staging), więc feed trzyma poprzednie wersje **tego kanału**; kanały są rozdzielone.
- **Serwer / manifesty**: wolumen `releases_manifests` → `/data/releases/releases.<channel>.<os>.json`
  czytane przez `/api/releases` (compose.yaml `api`, `RELEASES_DIRECTORY`).

### 2.4 Weryfikacja po wydaniu

```bash
# 1) API zwraca nową wersję jako latestVersion i manifest z wpisami
curl -s 'https://phaket.app/api/releases?channel=beta&os=win'   | head -c 600
curl -s 'https://phaket.app/api/releases?channel=beta&os=linux' | head -c 600

# 2) Każdy plik z odpowiedzi istnieje pod downloadBaseUrl + <channel>/<os>/ + File
curl -sI 'https://phaket.app/releases/beta/win/' | head -1   # katalog: 403 przy autoindex off — to OK
curl -sI "https://phaket.app/releases/beta/win/$(curl -s 'https://phaket.app/api/releases?channel=beta&os=win' | jq -r '.releases[0].File')" | head -1

# 3) Sumy zgodne z SHA256SUMS.txt z release'u (GitHub)
sha256sum Phaket-Setup.exe
```

Checklist:

- [ ] `latestVersion` = `<wersja>` w `channel=beta&os=win` **i** `os=linux`
- [ ] `downloadBaseUrl` w odpowiedzi = `https://phaket.app/releases/`; paczka jest pod
      `<downloadBaseUrl><channel>/<os>/<File>` — serwer nie dokleja kanału do `File`,
      kanał jest w ścieżce katalogu paczek (§0)
- [ ] plik pod `https://phaket.app/releases/beta/<os>/<File>` pobiera się (HTTP 200) i jego SHA-256 z pliku `SHA256SUMS.txt` oraz z odpowiedzi API jest identyczny
- [ ] draft/Release `v<wersja>` zawiera paczki obu OS i `SHA256SUMS.txt`
- [ ] (jeśli kanał stage) ten sam `curl` na `https://stage.phaket.app/api/releases?...`

---

## 3. Test end-to-end aktualizacji (Windows + Linux)

Cel: dowieść, że **świeża instalacja ze stubu** i **aktualizacja in-app** działają na
obu systemach, zanim wersja trafi do `release`.

### 3.1 Świeża instalacja

**Windows (stub):** uruchom `PhaketSetup.exe` (stały URL, §7).
Stub: manifest → najnowsza pełna paczka `.nupkg` + SHA-256 → `Phaket-Setup.exe` z feedu
+ SHA-256 → `Setup.exe --silent`; gdy w manifeście nie ma `Phaket-Setup.exe`, robi
seed-install (bez Update.exe — aplikacja pokaże tryb deweloperski) (InstallRunner.cs,
FirstInstall.cs). Stub czyta **zawsze** `channel=release&os=win` (Feed.cs,
`DefaultManifestUrl`) — do testów beta nadpisz to zmienną:

```powershell
$env:PHAKET_INSTALLER_MANIFEST = 'https://stage.phaket.app/api/releases?channel=beta&os=win'
.\PhaketSetup.exe
# log: "install=setup.exe" (prawidłowo) albo "install=seed" (fallback — brak Setup.exe w manifeście)
```

**Linux:** stub jest tylko dla Windows; świeża instalacja = pobranie AppImage
z `/releases/<channel>/linux/` (lub z załączników release'u) i uruchomienie.
**Uwaga:** `UpdateService` działa tylko w instalacji rozpoznanej przez Velopack
(`manager.IsInstalled`); w środowisku deweloperskim i po seed-install aplikacja pokaże
„Tryb deweloperski…” (UpdateService.cs `DevelopmentMode`; FirstInstall.cs, komentarz
przy `SeedInstall`). **Zależność wymaga potwierdzenia na AppImage** (§8).

### 3.2 Aktualizacja in-app beta → beta (wyższa wersja)

1. Zainstaluj wersję `N` (np. `0.1.1-beta.1`), wydaj `N+1` (`0.1.1-beta.2`) na kanał `beta`.
2. Aplikacja → **Ustawienia → Aktualizacje**: kanał **Beta** (radio; wybór zapisuje
   `updateChannel: "beta"` w `settings.json`) — SettingsViewModel.cs `SelectChannel`,
   UpdateService.cs `SetChannel`.
3. Klik **„Sprawdź aktualizacje aplikacji”** → oczekiwany status **„Dostępna jest wersja <N+1>.”**
4. Klik **„Pobierz i zainstaluj”** → **„Pobieram aktualizację… X%”** → **„Instaluję
   i uruchamiam ponownie…”** → proces kończy się i startuje nowa wersja (UpdateService.cs
   `ApplyAndRestart`; SettingsViewModel.cs `DownloadAndInstallUpdateAsync`).
5. Po restarcie: „Zainstalowana wersja” = `<N+1>`, ponowne sprawdzenie → „Masz najnowszą
   wersję”.

Oczekiwane **komunikaty błędów** (plik `pl-PL.d/updates.json`): suma kontrolna, sieć, pobieranie,
tryb deweloperski. Każdy z nich wskazuje właściwą warstwę (`UpdateService.KeyFor`).

### 3.3 Feed lokalny (bez serwera)

Aplikacja i stub pozwalają nadpisać adres:

- aplikacja: `PHAKET_UPDATE_FEED` = bazowy URL bez query (doklejane jest
  `?channel=…&os=…`; UpdateService.cs `ResolveFeedUrl`, `FeedUrl`). Np. `http://127.0.0.1:8080/api/releases`.
- stub: `PHAKET_INSTALLER_MANIFEST` (pełny URL z query) i opcjonalnie
  `PHAKET_INSTALLER_DOWNLOAD_BASE` (InstallRunner.cs, Feed.cs, DownloadBase.cs).

Manifest lokalny zrób generatorem z paczek vpk:
`python3 scripts/feed-manifest.py --version 0.1.1-beta.2 --out releases.beta.win.json <paczki>`
(feed-manifest.py). Serwuj katalog paczek statycznie; pole `downloadBaseUrl` w odpowiedzi
musi być **https://** (np. serwer stage albo reverse proxy), bo stub odrzuca nie-HTTPS
(Manifest.cs `IsWebSite`).

### Checklist e2e

- [ ] Windows: świeża instalacja przez stub kończy się „Gotowe! Uruchamiam Phaket.”
- [ ] Windows: aktualizacja beta→beta dochodzi do restartu i nowej wersji
- [ ] Linux: instalacja z AppImage, aktualizacja beta→beta dochodzi do restartu
- [ ] ustawienie kanału Beta przeżywa restart aplikacji
- [ ] log/ekran z kodem błędu (jeśli był) zapisany do zgłoszenia

### Jak zgłosić problem

Zgłoszenie do issue w Linear (PAK-109 lub nowe) z: wersja źródłowa i docelowa, OS,
backend (X11/XWayland/natywny Wayland), pełny komunikat z Ustawień → Aktualizacje, dla
stubu — kod błędu z okna (np. `E403 downloadBaseUrl`, `N404 manifest`, `E600 setup`,
`E503 install`; Messages.cs, InstallRunner.cs), a dla in-app — treść statusu
(klucz `Settings.Updates.*`). Nie wklejaj sekretów (SSH, tokeny) w zgłoszeniu.

---

## 4. Promocja beta → prod

Promocja to **to samo wydanie z `channel=release` i wersją bez przyrostka `-beta`**
(release.yml, nagłówek: „Promocja beta→prod = to samo uruchomienie z channel=release”).
Świadomie nie ma skryptu kopiującego pliki między kanałami — manifest `release` powstaje
z paczek przebiegu.

### 4.1 Checklist przed

- [ ] CI `desktop-ci.yml` zielony na `0.1-czeladzki-saturn`
- [ ] E2E beta (§3) zaliczone na Windows **i** Linux
- [ ] wpisy Linear/Outline zaktualizowane o wynik testów
- [ ] **wyraźna zgoda właściciela na prod**
- [ ] sekrety i zmienne (§1.1) nadal aktualne

### 4.2 Uruchomienie

Workflow **Release** → *Run workflow*: `version` = np. `0.1.0` (**bez** `-beta`),
`channel` = `release`, `publishTarget` = `github-draft+server` (pełna promocja) albo
`github-draft` (tylko draft na GitHubie, bez deployu — np. gdy serwer ma być wdrożony ręcznie).
Job `validate` odrzuci wersję z przyrostkiem prerelease na kanale `release`.
Podczas przebiegu vpk buduje paczki pod tą samą wersją — **licz się z tym, że nowa paczka
`release` ma inną sumę niż paczka `beta` tej samej wersji** (manifest i SHA z nowego
przebiegu). Nie mieszaj sum z beta w weryfikacji. Paczki obu kanałów leżą w osobnych
katalogach (`/releases/beta/...` i `/releases/release/...`), więc żadna promocja nie nadpisuje
plików drugiego kanału (§0).

### 4.3 Weryfikacja po

```bash
curl -s 'https://phaket.app/api/releases?channel=release&os=win'
curl -s 'https://phaket.app/api/releases?channel=release&os=linux'
```

- [ ] `latestVersion` = wersja bez `-beta` w **obu** manifestach
- [ ] aplikacja z kanałem **Stabilny** widzi nową wersję (Ustawienia → Aktualizacje)
- [ ] aplikacja na kanale Beta widzi tę samą wersję (beta nie schodzi niżej — UpdateService
      bez `AllowVersionDowngrade`, dopóki użytkownik nie wskaże kanału jawnie)
- [ ] `release` na GitHubie opublikowany lub przygotowany do publikacji

---

## 5. Awaryjne

### 5.1 Rollback wydania

**Najpierw zatrzymaj ruch, potem cofaj.** Użytkownik sprawdza aktualizacje sam; najprostszy
rollback to cofnięcie **manifestu** — aplikacja przestaje widzieć nową wersję.

1. **Manifesty na serwerze** (jedyna rzecz, którą czyta API): przywróć poprzedni
   `releases.<channel>.<os>.json` do wolumenu `releases_manifests`
   (`docker cp <plik> $(docker compose ps -q api):/data/releases/...`; wolumen jest montowany
   w api jako `:ro`, ale `docker cp` zapisuje przez daemon jako root). Paczki poprzedniej wersji
   **zostają** w wolumenie `releases_packages` (katalogi kanału nie są czyszczone —
   server-deploy.py). Jeśli manifest był publikowany z worktree, możesz go odtworzyć
   `feed-manifest.py` z poprzednich paczek albo wziąć kopię sprzed deploy.
2. **GitHub**: draft → usuń (`gh release delete v<wersja> --yes` na drafcie) lub — jeśli
   release jest opublikowany i wydanie jest błędne — oznacz jako prerelease / usuń według
   decyzji właściciela. GitHub release nie wpływa na feed aplikacji.
3. **Paczki delta**: zostają, nic ich nie czyści (workflow nie używa `--delete`,
   server-deploy.py nie usuwa plików z wolumenu).
4. **Weryfikacja**: `curl` na `channel=<kanał>` pokazuje poprzednią wersję jako `latestVersion`.

> Uwaga: rollback manifestu nie usuwa paczek nowej wersji z wolumenu — nadal są pod
> `https://phaket.app/releases/<channel>/<os>/`, tylko nikt do nich nie prowadzi. Rollback
> jednego kanału nie dotyka drugiego (osobne katalogi, §0).

### 5.2 Najczęstsze usterki

| Objaw | Przyczyna | Co zrobić |
|---|---|---|
| Deploy: `BŁĄD: manifest wskazuje brakujący plik` | manifest i paczki z różnych przebiegów / ręczne podmiany | przebuduj manifest z tych samych paczek (`feed-manifest.py`) i powtórz deploy |
| Deploy: `BŁĄD: --stage-dir musi być ~/PhaketServer/staging` | zły `SERVER_ROOT` | popraw `SERVER_ROOT`/ścieżkę na VPS |
| Deploy: `BŁĄD: brak działającego kontenera usługi www (docker compose ps www)` | stack compose nie działa albo usługa ma inną nazwę | `docker compose ps` w `~/PhaketServer`; skrypt szuka usługi `www` (paczki) i `api` (manifesty) w compose.yaml |
| Deploy: `BŁĄD: paczki bez wpisu w manifeście` / `manifest wskazuje brakujące pliki` | paczki i manifest z różnych przebiegów albo ręczna podmiana | przebuduj manifest `feed-manifest.py` z dokładnie tych paczek i powtórz deploy |
| Deploy: `BŁĄD: brak manifestów` | `rsync` manifests nie dotarł albo zły katalog | `manifests/releases.<channel>.{win,linux}.json` w stagingu (generuje je krok `Build manifests and check the file set`) |
| Deploy: `Check the download base URL matches the channel` | `vars.RELEASES_DOWNLOAD_BASE_URL` wskazuje inny katalog niż kanał przebiegu | popraw zmienną (§1.1) lub usuń ją (domyślne `https://phaket.app/releases/`) |
| Validate: `Kanał ... wymaga wersji ...` | `beta` z wersją bez `-beta` albo `release` z `-beta` | podaj wersję zgodną z kanałem |
| API: `400 {"error":"INVALID_PARAMETER"}` | zły `channel`/`os` (tylko `beta|release`, `win|linux`) | popraw URL; serwer waliduje przed dostępem do pliku |
| API: `404 {"error":"NOT_FOUND"}` | brak manifestu kanału (pusty feed do pierwszego wydania) | wydaj na ten kanał; brak domyślnego kanału jest zamierzony |
| API: `500 {"error":"INVALID_MANIFEST"}` | uszkodzony/niezgodny manifest (zły `SHA256`, `File` ze ścieżką, duplikat) | przebuduj manifest generatorem |
| Stub: `E403 downloadBaseUrl` / `E403 manifest` | adres nie-HTTPS: `downloadBaseUrl` w odpowiedzi API albo nadpisanie `PHAKET_INSTALLER_MANIFEST` / `PHAKET_INSTALLER_DOWNLOAD_BASE` | stub odrzuca każdy nie-HTTPS **przed** żądaniem; ustaw HTTPS w `RELEASES_DOWNLOAD_BASE_URL` (serwer) i w env testów |
| Stub: `install=seed` | brak `Phaket-Setup.exe` w manifeście | sprawdź krok `Name the installer for the feed` w release.yml |
| Aplikacja: „Tryb deweloperski” | build nie jest instalacją Velopack (dev / seed-install) | testuj na instalacji przez Setup.exe |
| Aplikacja: błąd sieci | feed nieosiągalny / `PHAKET_UPDATE_FEED` źle sformułowany | sprawdź `curl` na feed i zmienną |

(Źródła: server-deploy.py, Releases.cs, Manifest.cs, UpdateService.cs, InstallRunner.cs.)

---

## 6. Instalator-stub (`PhaketSetup.exe`)

**Zasada: stub jest niezmienniczy.** Leży pod stałym URL-em i nie zmienia się przy
wydaniach aplikacji (installer-stub.yml, nagłówek; Phaket.InstallerStub.Win32.csproj).

- **Kiedy przebudowywać:** zmiana kontraktu feedu/URL API, poprawka błędu w stubie,
  zmiana minimalnej wersji Windows/.NET AOT. Nie przy każdym wydaniu aplikacji.
- **Jak:** Actions → **Installer stub (PhaketSetup.exe)** → *Run workflow*:
  `deploy=false` = tylko artefakt `phaket-setup-stub` (build + testy + kontrola rozmiaru),
  `deploy=true` = dodatkowo `scp` na `/srv/synapsekit-boracik/nginx/html/phaket/download/PhaketSetup.exe`
  (wymaga sekretów `DEPLOY_*`).
- **Weryfikacja:** krok `Check size (<5 MB)` przerywa build przy ≥ 5 MB
  (`PublishSizeLimitBytes` = 4 900 000 w csproj); testy w `Phaket.InstallerStub.Tests`
  muszą przejść przed publikacją.
- **Stały URL:** `PhaketSetup.exe` w katalogu `phaket/download/` (ścieżka stała w workflow,
  installer-stub.yml krok `Upload stub`). **Bloker prod (§7):** vps nie serwuje publicznie
  `phaket.app/download/PhaketSetup.exe` — workflow wgrywa plik do
  `/srv/synapsekit-boracik/nginx/html/phaket/download/`, ale `PhaketWWW/nginx.conf` takiego
  katalogu nie mapuje (nieznane ścieżki dostają `302 /`). Do czasu osobnej zmiany vhosta URL
  w release notes jest zapowiedzią, nie działającym linkiem — stub trzeba na razie podać
  użytkownikom z artefaktu Actions.
- Kontrakt stubu: `channel=release&os=win`, manifest ≤ 4 MB, `File` bez ścieżek lub pełny
  HTTPS, SHA-256 obowiązkowy (Manifest.cs).

---

## 7. Stan obecny (27.11.2026)

- **API na prod jeszcze nie działa**: `phaket.app` to obecnie strona-zajawka (nginx
  `PhaketWWW`); wystawienie `/api/releases` i `/releases/` na phaket.app to osobny krok
  deployu serwera (PAK-110, konfiguracja vhosta — PhaketWWW/README.md: „Wdrożenie na
  phaket.app wymaga osobnej zgody”). Nawet gdy API odpowiada, `/releases/<ścieżka>` pod
  `PhaketWWW` ma `error_page 404 = @home` (302 do `/`) — paczki muszą być serwowane przez
  vhost prod, nie przez kontener zajawki (PhaketWWW/nginx.conf:44-50). **Bloker prod.**
- **Stub instalatora na prod**: `https://phaket.app/download/PhaketSetup.exe` (URL z release
  notes) jeszcze nie odpowiada — katalog `phaket/download/` nie jest mapowany przez żaden vhost.
  **Bloker prod** (§6), nie do naprawy po stronie workflow.
- **Stage** (192.168.1.184 / stage.phaket.app) ma **puste manifesty** do pierwszego wydania:
  do czasu pierwszego deployu `/api/releases` zwraca `404 NOT_FOUND`.
- **Sekrety i zmienne GitHub NIE są jeszcze skonfigurowane** — pierwszy deploy padnie na
  „Configure SSH”; najpierw §1.1.
- **Pierwsze uruchomienie workflow `Release` wymaga wyraźnej zgody właściciela.**
- Podpisy kodu: brak (decyzja 25.11.2026). Ogłoszeń Discord brak (release.yml, nagłówek).

---

## 8. Rozbieżności — stan domknięcia (27.11.2026)

Lista z przeglądu runbooka (PAK-109/111). Wszystkie punkty po stronie **PhaketDesktop**
domknięte; zostały tylko fakty serwera opisane jako blokery w §7.

| # | Rozbieżność | Stan |
|---|---|---|
| 1 | `publishTarget` nierozdzielony (`github-draft` też deployował) | **domknięte**: `draft-release` i `deploy` mają `if` odpowiednio `!= none` i `== github-draft+server`; `none` = same artefakty (§2.1) |
| 2 | Katalog paczek bez kanału (kolizja beta/release) | **domknięte**: `/releases/{channel}/{os}/` w `server-deploy.py` i w nagłówku release.yml; layout potwierdzony serwerem (§0) |
| 3 | `Phaket-Setup.exe` jeden dla obu kanałów | **domknięte** przez #2 — instalator leży w katalogu kanału, więc jest per kanał |
| 4 | `RELEASES_DOWNLOAD_BASE_URL` nieprzekazywany | **domknięte**: workflow ma `env.DOWNLOAD_BASE_URL` z `vars`/defaultu i weryfikuje, że wskazuje katalog kanału przed deployem; wartość w odpowiedzi ustawia serwer (§1.1) |
| 5 | Martwy `scripts/publish-feed.sh` | **domknięte**: usunięty, brak odwołań |
| 6 | URL stuba w release notes vs ścieżka wgrana przez workflow | **ustalone**: workflow stubu wgrywa `phaket/download/PhaketSetup.exe`, note i runbook używają `https://phaket.app/download/PhaketSetup.exe`; brak mapowania tego katalogu w vhost prod = **bloker prod** (§6, §7) |
| 7 | `installer-stub.yml` deklaruje `contents: read` | **OK**: job nie tworzy release'u, używa tylko `ssh`/`scp` — uprawnienie wystarcza; sprawdzić przy pierwszym realnym deployu stubu |
| 8 | In-app update na Linux wymaga `IsInstalled` dla AppImage | **otwarte**: wymaga testu e2e na prawdziwym AppImage (§3.1); poza zakresem workflow |
| 9 | Runbook niesprawdzany przez CI | **bez zmian**: zgodność utrzymywana ręcznie |

**Przegląd R3b (27.11.2026)** — domknięte w tym samym PR: `find` po `cd` w kroku Setup.exe
(ścieżka względna), detekcja kontenera przez `docker compose ps` na usługę `www`/`api`
(`docker ps --filter name=nginx` nie trafiał — serwis to `www`), publikacja manifestów do wolumenu
**api** (www go nie montuje), pełna wersja beta w manifeście (`version_of` gubił `-beta.N`),
stub odmawia HTTP przed żądaniem (env i `downloadBaseUrl`), walidacja kompletu przed deployem
i Setup.exe przez temp+mv, `validate` wymaga `-beta`/`-beta.N`.

Dodatkowo w tej naprawie: job `validate` wymusza zgodność wersji z kanałem (bez niego dało się
wydać `0.1.1-beta.1` na `release`), a `server-deploy.py` wykrywa kontener przez
`docker compose ps -q <usługa>` uruchamiane w `~/PhaketServer` (nazwa projektu compose zależy od
katalogu — dlatego skrypt zawsze ustawia `cwd` na repozytorium serwera i nie zgaduje nazwy kontenera).