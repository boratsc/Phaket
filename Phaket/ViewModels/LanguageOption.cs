using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Phaket.Services;

namespace Phaket.ViewModels;

// PAK-134: one radio button of the language step. Every locale shows its own name
// as the OS names it, so the list is the same in any UI language.
public sealed partial class LanguageOption : ObservableObject
{
    public LanguageOption(string code, Action<string> pick)
    {
        Code = code;
        DisplayName = CultureInfo.GetCultureInfo(code).DisplayName;
        AutomationId = $"onboarding.language.{code}";
        this.pick = pick;
    }

    private readonly Action<string> pick;

    // The same path the radio button's click takes.
    internal void Pick() => pick(Code);

    public string Code { get; }
    public string DisplayName { get; }
    public string AutomationId { get; }

    [ObservableProperty] private bool isSelected;

    // Preset sync is not a click, so it must not re-enter the pick callback.
    internal void SetPreset(bool value)
    {
        preset = true;
        try { IsSelected = value; }
        finally { preset = false; }
    }

    private bool preset;

    // Clicking a radio button selects it: that is the user's choice, previewed at once.
    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !preset) pick(Code);
    }
}