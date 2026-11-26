using System.Text.Json;

namespace Phaket.Services;

// First-run marker (PAK-133). The wizard is shown until settings.json carries
// onboardingCompleted: true; a missing or unreadable file counts as a fresh profile, so a
// crashed first run shows the wizard again instead of silently skipping it.
public sealed class OnboardingService
{
    // Callback hooks let the owning list of wizard steps reset/probe per-step state
    // (e.g. a language step watching its own saved choice). The skeleton ships with none.
    public OnboardingService(string settingsPath,
        IReadOnlyList<Func<bool>>? isCompleteHooks = null,
        IReadOnlyList<Action>? resetHooks = null)
    {
        SettingsPath = settingsPath;
        IsCompleteHooks = isCompleteHooks ?? [];
        ResetHooks = resetHooks ?? [];
    }

    public string SettingsPath { get; }

    internal IReadOnlyList<Func<bool>> IsCompleteHooks { get; }
    internal IReadOnlyList<Action> ResetHooks { get; }

    public static string DefaultSettingsPath => CatalogSettings.DefaultSettingsPath;

    // Never throws: a missing or broken file is an unfinished onboarding.
    public static bool IsCompleted(string settingsPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return doc.RootElement.TryGetProperty("onboardingCompleted", out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return false;
    }

    // First run requires both the saved flag and every step's own state to agree.
    public bool IsCompleted() => IsCompleted(SettingsPath) && IsCompleteHooks.All(hook => hook());

    public bool Complete() => Services.SettingsFile.TryUpdate(SettingsPath, node => node["onboardingCompleted"] = true);

    // Also runs the reset hooks, so re-opening the wizard starts from the saved state.
    public bool Reset()
    {
        foreach (var hook in ResetHooks) hook();
        return Services.SettingsFile.TryUpdate(SettingsPath, node => node["onboardingCompleted"] = false);
    }
}