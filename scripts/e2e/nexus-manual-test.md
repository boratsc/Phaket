# Test manualny: Nexus Mods w Phaket (bez klucza w CI)

Procedura dla osoby z kluczem API Nexus. Automatyczny e2e nie może jej zastąpić: klucz jest osobisty,
a domyślny Client ID OAuth `public_test` pochodzi z przykładu dokumentacji i może zostać odrzucony.
Wszystko poniżej dzieje się na Twoim komputerze; Phaket nie wysyła klucza ani tokenów na serwer.

Stan po stronie klienta (zweryfikowany e2e, bez klucza): parser `nxm://`, adres moda, ręczne archiwum ZIP
i instalacja do katalogu gry działają (testy `E2ECatalogLiveTests.Nexus_manual_archive_and_nxm_links_work_without_an_api_key`
oraz `E2ENexusSettingsTests`). Ten dokument dotyczy wyłącznie części wymagającej Twojego konta Nexus.

## 0. Przygotowanie

1. Uruchom Phaket (build z gałęzi `0.1-czeladzki-saturn`).
2. Otwórz **Ustawienia → Nexus Mods**.
3. Sprawdź, że widzisz trzy elementy: logowanie OAuth („Zaloguj przez Nexus”), pole **Klucz API Nexus**
   z przyciskiem **Sprawdź klucz** i link **Otwórz stronę API Access w serwisie nexusmods.com**.

## 1. Klucz API (ścieżka podstawowa)

1. Kliknij **Otwórz stronę API Access w serwisie nexusmods.com**.
   - Oczekiwane: przeglądarka otwiera `https://www.nexusmods.com/users/myaccount?tab=api+access`.
2. Skopiuj **Personal API Key** (sam token, bez spacji i cudzysłowów).
3. Wklej do pola **Klucz API Nexus** i kliknij **Sprawdź klucz**.
   - Oczekiwane: w polu jest puste miejsce po zapisie (klucz nie jest wyświetlany ponownie),
     status **„Klucz działa i został zapisany.”**,
     konto: `Konto: <nazwa>`, `Konto Premium` albo `Konto bez Premium — pobieranie tylko z linku nxm://`,
     limit: `Pozostały limit: <x>/h, <y>/dzień`.
   - Gdy systemowy magazyn sekretów jest zablokowany: status **„Klucz działa. Magazyn systemowy jest
     niedostępny — klucz pozostaje tylko do końca tej sesji.”** (to zachowanie poprawne, nie błąd).
4. Kliknij **Sprawdź klucz** ponownie, bez wpisywania klucza.
   - Oczekiwane: **„Zapisany klucz działa.”** — czyli klucz został odczytany z magazynu.
5. Wpisz bzdurny klucz (`abc!@#`) i kliknij **Sprawdź klucz**.
   - Oczekiwane: **„Klucz zawiera niedozwolone znaki. Wklej sam token z API Access.”** (bez zapisu).
6. Kliknij **Usuń klucz**.
   - Oczekiwane: **„Klucz Nexus został usunięty.”**, konto/limit znikają.
7. Ponownie kliknij **Sprawdź klucz** przy pustym polu.
   - Oczekiwane (zweryfikowane e2e w pl i en): **„Najpierw zaloguj się przez Nexus albo zapisz klucz API w ustawieniach (sekcja Nexus Mods).”** /
     „Sign in with Nexus first, or save the API key in settings (Nexus Mods section).” — nie połączenie z siecią, tylko od razu czytelny komunikat.

## 2. Logowanie OAuth (ścieżka eksperymentalna)

1. W polu **Client ID (OAuth2 + PKCE)** zostaw `public_test` (albo wpisz własny Client ID, jeśli go masz).
2. Kliknij **Zaloguj przez Nexus**.
   - Oczekiwane: otwiera się przeglądarka; aplikacja czeka na kod przez ok. 120 s
     („Czekam na potwierdzenie w przeglądarce… (do 120 s)”), odbiornik działa pod `127.0.0.1:8642`.
3. Zaloguj się i zatwierdź dostęp.
   - Oczekiwane: status **„Zalogowano przez Nexus.”**, konto i limit widoczne; przycisk zmienia się w **Wyloguj**.
   - Jeśli rejestracja `public_test` odmówi (typowy wynik, oczekiwany): w przeglądarce pojawia się błąd
     rejestracji/autorizacji aplikacji. Wróć do Phaket — status to komunikat błędu OAuth
     (**„Autoryzacja Nexus nie powiodła się. Jeśli używasz public_test, zarejestruj aplikację i wpisz własny
     Client ID.”** albo **„Nie można otworzyć przeglądarki lub uruchomić lokalnego odbiornika OAuth
     (port 8642 zajęty?).”**),
     a przycisk **Zaloguj przez Nexus** pozostaje aktywny. To nie jest błąd Phaket: zarejestruj własną
     aplikację w Nexus i wpisz jej Client ID.
4. Kliknij **Wyloguj** (jeśli logowanie się udało).
   - Oczekiwane: **„Wylogowano — tokeny OAuth zostały usunięte.”**; klucz API (jeśli zapisany) zostaje.

## 3. Przegląd moda po adresie (Valheim, mod 3667 — Domena)

1. Ustaw język polski (Ustawienia → Język), żeby porównać komunikaty.
2. Otwórz **Nexus Mods → przeglądarka** i wklej `https://www.nexusmods.com/valheim/mods/3667`
   (albo samo `3667`), kliknij **Wczytaj**.
   - Oczekiwane z zapisanym kluczem: tytuł, autor, wersja, liczba poleceń i lista plików z rozmiarem.
   - Oczekiwane bez klucza: czytelny komunikat o braku logowania/klucza, bez pustej tabeli i bez wyjątku.
3. Wklej niepoprawny adres (`https://nexusmods.com.evil.test/valheim/mods/3667`).
   - Oczekiwane: **„Wpisz poprawny adres strony moda Nexus Mods lub numer moda.”** bez żadnego żądania sieciowego do tego hosta.
4. Drugie wejście w ten sam mod (cache TTL).
   - Oczekiwane: dane pojawiają się natychmiast; limit zapytań się nie zmniejsza (sprawdź `Pozostały limit`).

## 4. Pobranie przez `nxm://` (wymaga Premium albo linku z przeglądarki)

1. Zaloguj się na `nexusmods.com`, wejdź na stronę plików moda Valheim 3667.
2. Kliknij **Mod manager download** (albo **Slow download** → link `nxm://`) — przeglądarka pyta,
   czym otworzyć link.
3. Wybierz Phaket (jeśli system nie ma rejestracji protokołu, skopiuj link `nxm://valheim/mods/3667/files/<id>?key=...&expires=...`
   i wklej go do przeglądarki modów w Phaket).
   - Oczekiwane przy poprawnym linku: prefill „Mod z Nexus Mods” (nazwa, wersja, plik), pobranie do
     `~/.config/phaket/downloads/nexus`, następnie plan instalacji z podglądem plików.
   - Oczekiwane bez Premium i bez klucza: **„Konto bez Premium: pobieranie działa tylko z linkiem nxm:// z przeglądarki
     („Mod manager download”).”** — bez częściowego pliku.
   - Oczekiwane po wygaśnięciu linku: **„Link nxm:// wygasł. Skopiuj nowy z przeglądarki.”**
4. Zaakceptuj plan i zainstaluj do **instalacji testowej** (nie do prawdziwej gry, jeśli nie chcesz zmiany).
   - Oczekiwane: pliki w `BepInEx/plugins/...`, wpis w **Modach** z możliwością deinstalacji;
     deinstalacja przywraca poprzedni stan plików.

## 5. Ręczne archiwum (bez klucza i bez nxm)

1. Na stronie moda wybierz **Manual download**, zapisz ZIP.
2. W Phaket: **Nexus Mods → ręczne archiwum**, wklej URL moda, nazwę i wersję, wybierz plik ZIP.
3. Zainstaluj do instalacji testowej.
   - Oczekiwane: plan i instalacja jak wyżej; format `.7z`/`.rar` kończy się komunikatem
     **„Archiwum <plik> używa nieobsługiwanego formatu .7z. Wybierz plik ZIP.”** (bez rozpakowywania).
   - Uwaga (znany problem, zgłoszony w raporcie e2e): archiwum o układzie gry (`BepInEx/core/...`,
     `winhttp.dll` w katalogu głównym) trafia do `BepInEx/plugins/<nazwa>/...`, więc pliki loadera
     nie działają. Do czasu naprawy używaj paczek o układzie Thunderstore (`plugins/...`).

## Czego ten test nie obejmuje

- Realnego pobrania z CDN Nexus dla konta bez Premium (Nexus tego zabrania — oczekiwany komunikat wyżej).
- OAuth z prawdziwym Client ID (wymaga rejestracji aplikacji po stronie Nexus).
- Windows Credential Managera (test na Linuksie używa Secret Service; na Windows powtórz kroki 1.3–1.6).

## Zgłaszanie wyniku

Zapisz: wersję Phaket (`--version`/Ustawienia → O programie), system, wynik każdego kroku (OK/inny komunikat),
zrzuty ekranu sekcji Nexus w Ustawieniach i przeglądarki modów. Nie wklejaj klucza API ani linku `nxm://`
z parametrem `key` do zgłoszenia.