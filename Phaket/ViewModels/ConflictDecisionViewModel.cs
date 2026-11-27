using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Execution;
using Phaket.Services;

namespace Phaket.ViewModels;

// One two-sided file conflict, named the way the user knows it: the file name, with the
// full path kept for the tooltip. Both sides are described in plain words.
public partial class ConflictFileItem : ViewModelBase
{
    public ConflictFileItem(string path)
    {
        FullPath = path;
        FileName = System.IO.Path.GetFileName(path);
        Directory = System.IO.Path.GetDirectoryName(path) ?? "";
    }

    public string FullPath { get; }
    public string FileName { get; }
    public string Directory { get; }
    public string LocationHint => FullPath;
}

// The window-level dialog for "which version of these files do you keep?" (PAK-142).
// It is shown before anything is written: closing it without an answer cancels the
// install, and the journal the executor would have approved stays untouched.
public partial class ConflictDecisionViewModel : ObservableObject
{
    // The mod flow starts deep inside the shell (catalog -> mods), so the window shows
    // one instance, like the Steam prompt.
    public static ConflictDecisionViewModel Shared { get; } = new();

    [ObservableProperty] private bool isOpen;
    [ObservableProperty] private string statusMessage = "";
    public IReadOnlyList<ConflictFileItem> Files { get; private set; } = [];
    public bool HasFiles => Files.Count > 0;
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    private TaskCompletionSource<ConflictDecision?>? pending;
    private CancellationTokenRegistration registration;

    // Fills the dialog and waits for the answer. A cancel, Escape or an answer after the
    // caller gave up returns null: the install then runs nothing.
    public Task<ConflictDecision?> RequestAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var waiting = new TaskCompletionSource<ConflictDecision?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending?.TrySetResult(null);
        pending = waiting;
        Files = paths.Distinct(StringComparer.Ordinal).Select(path => new ConflictFileItem(path)).ToArray();
        StatusMessage = "";
        OnPropertyChanged(nameof(Files));
        OnPropertyChanged(nameof(HasFiles));
        IsOpen = true;
        registration = ct.Register(() => { if (pending == waiting) Cancel(); });
        return waiting.Task;
    }

    [RelayCommand]
    private void KeepLocal() => Answer(ConflictDecision.KeepLocal);

    [RelayCommand]
    private void TakeRecipe() => Answer(ConflictDecision.TakeRecipe);

    [RelayCommand]
    private void Cancel() => Answer(null);

    // Called by the view when the overlay is dismissed with Escape.
    public void Dismiss() => Cancel();

    private void Answer(ConflictDecision? decision)
    {
        var waiting = pending;
        if (waiting is null) return;
        pending = null;
        registration.Dispose();
        registration = default;
        IsOpen = false;
        Files = [];
        OnPropertyChanged(nameof(Files));
        OnPropertyChanged(nameof(HasFiles));
        waiting.TrySetResult(decision);
    }
}