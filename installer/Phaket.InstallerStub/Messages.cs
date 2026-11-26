using System.Globalization;
using System.Text;

namespace Phaket.InstallerStub;

public enum StubStage
{
    Downloading,
    Verifying,
    Installing,
    Done
}

/// <summary>Teksty dwujęzyczne (pl-PL pierwszy, en zawsze obecny) i kody techniczne do zgłoszeń.</summary>
public static class Messages
{
    public static string Title(bool pl) => pl ? "Instalacja Phaket" : "Phaket installation";

    public static string Stage(StubStage stage, int percent, bool pl) => stage switch
    {
        StubStage.Downloading => pl
            ? string.Create(CultureInfo.InvariantCulture, $"Pobieram Phaket… {percent}%")
            : string.Create(CultureInfo.InvariantCulture, $"Downloading Phaket… {percent}%"),
        StubStage.Verifying => pl ? "Sprawdzam plik…" : "Checking the file…",
        StubStage.Installing => pl ? "Instaluję…" : "Installing…",
        _ => pl ? "Gotowe! Uruchamiam Phaket." : "Done! Starting Phaket."
    };

    public static string Error(string code, bool pl) => pl
        ? string.Create(CultureInfo.InvariantCulture,
            $"Nie udało się zainstalować Phaket. Spróbuj ponownie za chwilę.\n\n" +
            $"If the problem repeats, report it with this code.\nKod / Code: {code}")
        : string.Create(CultureInfo.InvariantCulture,
            $"Phaket could not be installed. Please try again in a moment.\n\n" +
            $"Jeśli problem się powtarza, zgłoś go z tym kodem. / If it repeats, report this code: {code}");

    /// <summary>Kod błędu: grupa + wariant, np. E400 manifest / N503 HTTP.</summary>
    internal static string Code(string group, string detail)
    {
        var sb = new StringBuilder(group.Length + detail.Length + 1);
        sb.Append(group).Append(' ');
        foreach (var c in detail)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_' or '/')
                sb.Append(c);
        }
        return sb.ToString();
    }
}