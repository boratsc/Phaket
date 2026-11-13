# Pakhet Desktop

Minimalny szkielet aplikacji desktopowej **Pakhet** (.NET 10 + Avalonia 12.1.6) na Windows
i Linux. Zakres PoC 0.0.1: sam szkielet okna — bez funkcji produktu (silnik przepisów
i Recipe Studio powstaną osobno).

## Wymagania

- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10/11 albo Linux z sesją X11. W sesji Wayland domyślny tryb wymaga działającego XWayland; bez niego można wypróbować opisany niżej natywny Wayland, z podanymi ograniczeniami dostępności.
- Opcjonalnie (opt-in natywnego Wayland): `libwayland-client` oraz sterownik EGL/OpenGL —
  standardowo obecne na desktopach Wayland

## Uruchomienie lokalne

```bash
dotnet run --project Pakhet          # uruchomienie aplikacji
dotnet build                         # build całej solucji
dotnet test                          # testy headless (nie wymagają sesji graficznej)
```

Projekt aplikacji: `Pakhet/Pakhet.csproj`, testy: `Pakhet.Tests/Pakhet.Tests.csproj`.

## Linux: Wayland vs XWayland

Domyślnie aplikacja używa backendu **X11** — w sesji Wayland działa przez **XWayland**.
Świadomy wybór: backend X11 ma pełną dostępność (AT-SPI2, czytniki ekranu, np. Orca),
a natywny backend Wayland w Avalonia 12.1 jest **eksperymentalny i nie ma jeszcze
backendu AT-SPI** (zweryfikowane w źródłach 12.1.6: `WaylandPlatform` nie inicjuje AT-SPI).

Natywny Wayland można włączyć warunkowo (opt-in):

```bash
PAKHET_NATIVE_WAYLAND=1 dotnet run --project Pakhet
```

Wtedy `UseWaylandWithFallback()` (z pakietu `Avalonia.Wayland`) łączy się bezpośrednio
z kompozytorem Wayland, a gdy jest niedostępny — automatycznie wraca do X11.
Na Windows zmienna nie ma żadnego efektu.

Ograniczenia natywnego Wayland (Avalonia 12.1):

- brak AT-SPI → brak obsługi czytników ekranu (dlatego nie jest domyślny),
- brak integracji KDE: globalne menu aplikacji, ikony okien, blur-behind,
- renderowanie przez EGL (OpenGL / OpenGL ES), opcjonalnie dma-buf,
- backend oznaczony jako eksperymentalny; `UsePlatformDetect()` go nie wybiera.

Weryfikacja, który backend faktycznie działa (gniazda procesu): znajdź PID działającej
aplikacji w monitorze systemu lub za pomocą `ps -ef`, a następnie podstaw go poniżej.
Nie używaj PID procesu `dotnet run`, który może być tylko procesem nadrzędnym.

```bash
lsof -p 12345 | grep -E 'wayland|X11' # 12345 zastąp PID-em aplikacji
# natywny Wayland: socket do $XDG_RUNTIME_DIR/wayland-*
# X11/XWayland:    socket do /tmp/.X11-unix/X*
```

## Dostępność

- Avalonia automatycznie tworzy *automation peers* dla standardowych kontrolek:
  Windows → UI Automation, Linux (X11/XWayland) → AT-SPI2 po D-Bus (bez konfiguracji,
  gdy działa usługa dostępności).
- `TextBlock`/`Button` z tekstem udostępniają go czytnikom automatycznie; dla ikon
  i niejednoznacznych kontrolek ustawiaj `AutomationProperties.Name`, etykiety pól
  przez `AutomationProperties.LabeledBy`.
- Motyw `FluentTheme` z `RequestedThemeVariant="Default"` podąża za jasnym/ciemnym
  motywem systemu; wskaźnik fokusu jest widoczny domyślnie.
- Test headless sprawdza m.in. nazwę dostępną okna (`WindowAutomationPeer`).
- Test headless i CI nie uruchamiają rzeczywistego backendu okien ani czytnika ekranu. Lokalne uruchomienie XWayland, natywnego Wayland i fallbacku sprawdzono w KDE; rzeczywistego Windows i czytnika ekranu jeszcze nie sprawdzono.

## Lokalizacja (pl-PL / en)

Szkielet nie zawiera jeszcze komunikatów użytkownika — widoczne teksty są neutralne
językowo (nazwa produktu i stack). Każdy przyszły tekst UI musi trafiać do zasobów
pl-PL/en; identyfikatory (`AutomationProperties.AutomationId`, klucze zasobów)
pozostają niezależne od języka.

## Struktura

```
Pakhet/            aplikacja (Program.cs, App.axaml, MainWindow.axaml, app.manifest)
Pakhet.Tests/      testy headless xUnit v3 (Avalonia.Headless.XUnit)
Pakhet.slnx        solucja (build/test z katalogu głównego)
```

Licencja: plik `LICENSE` (bez zmian).
