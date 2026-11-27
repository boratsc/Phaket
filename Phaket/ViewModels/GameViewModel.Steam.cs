using CommunityToolkit.Mvvm.ComponentModel;
using Phaket.Core.Execution;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Launch;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms.SteamShortcuts;
using Phaket.Services;

namespace Phaket.ViewModels;

// PAK-145/146-B: the Steam question and the "play" path of the game page, kept out of the main
// file. Nothing is written without the user's yes, and every result is said in plain words.
public partial class GameViewModel
{
    // Seams for tests: the real service and roots in the app; a fixture Steam for a test.
    internal SteamShortcutService? SteamShortcuts { get; set; }
    internal string? SteamHome { get; set; }
    internal string HostOs { get; set; } = OperatingSystem.IsWindows() ? "windows" : "linux";

    // Seams for tests: a fixture game that is not really its MainExecutable passes its own
    // arguments (on Windows the game is a shell script), and LastLaunch keeps the diagnostic of
    // the last attempt, which LaunchOutcome.Failed alone cannot (it is one shared instance).
    internal string? HelperArguments { get; set; }
    internal LaunchOutcome? LastLaunch { get; private set; }

    // Where a managed launch points: the modded copy Phaket installed (registry.first is the
    // newest), its saves stay beside it; otherwise the game folder the library detected.
    internal string LaunchRoot => ManagedGameRoot() ?? Game.SelectedInstallation?.RootPath ?? "";

    private string LaunchExe => Path.Combine(LaunchRoot, Game.Definition.MainExecutable ?? "");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SteamToggleLabel))]
    private bool hasSteamShortcut;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSteamConsentOpen), nameof(ShowSteamRestart), nameof(SteamConsentText), nameof(SteamConsentYes), nameof(SteamConfirmHeader))]
    private bool steamRemoveMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SteamConsentCardVisible), nameof(ShowSteamRestart), nameof(SteamConsentText), nameof(SteamConsentYes))]
    private bool isSteamConsentOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SteamConsentCardVisible), nameof(ShowSteamRestart))]
    private bool isSteamBusy;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isPreparingSaves;
    [ObservableProperty] private bool isSavesSharedNoticeVisible;

    // The card is visible while the question is open and nothing is being written yet.
    public bool SteamConsentCardVisible => IsSteamConsentOpen && !IsSteamBusy;
    // Steam reads its shortcut file at start, so a pass writes nothing we could see: restart.
    public bool ShowSteamRestart => SteamConsentCardVisible && SteamService.IsSteamRunning;
    public string SteamConfirmHeader => Loc[SteamRemoveMode ? "SteamShortcut.RemoveHeader" : "SteamShortcut.AddHeader"];
    public string SteamConsentText => Loc[SteamRemoveMode ? "SteamShortcut.RemoveConsent" : "SteamShortcut.AddConsent"];
    public string SteamConsentYes => Loc[SteamRemoveMode ? "SteamShortcut.Remove" : "SteamShortcut.Add"];
    public string SteamToggleLabel => Loc[HasSteamShortcut ? "SteamShortcut.Remove" : "SteamShortcut.Add"];

    private static LocalizationService Loc => LocalizationService.Instance;

    private SteamShortcutService SteamService => SteamShortcuts ??= new SteamShortcutService();

    // The one Steam profile Phaket writes to: the user's pick when it exists, otherwise the
    // first. No Steam installed means there is nothing to toggle.
    private (string Root, string User)? SteamTarget()
    {
        var home = SteamHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = (SteamRoots ?? SteamShortcutService.FindSteamRoots(HostOs, home)).FirstOrDefault();
        var user = root is null ? null : SteamShortcutService.DefaultUser(root, SteamUser);
        return root is null || user is null ? null : (root, user);
    }

    // Steam's list carries ids only; the service's phaket-tagged listing is the source of truth
    // (it parses the same file the same way). Of those entries the page keeps the one it wrote
    // for this game — the exe Phaket computed for it — so the toggle never claims another entry.
    private bool InSteam()
    {
        try
        {
            if (SteamTarget() is not { } target) return false;
            var path = SteamShortcutService.ShortcutsPath(Path.GetFullPath(target.Root), target.User);
            var ours = SteamService.ListPhaketShortcuts(target.Root, target.User).Select(result => result.AppId).ToHashSet();
            if (ours.Count == 0) return false;
            var exe = CraftedEntry().GetString("Exe");
            return SteamShortcutsFile.Entries(SteamShortcutsFile.Parse(File.ReadAllBytes(path)))
                .Any(entry => ours.Contains(entry.GetInt32("appid")) && entry.GetString("Exe") == exe);
        }
        catch (Exception e) when (e is PhaketException or FormatException or ArgumentException or IOException)
        {
            // Includes STEAM_SHORTCUTS_CORRUPT: nothing was written, the words are the service's.
            StatusMessage = e is PhaketException phaket ? phaket.LocalizedMessage(Loc.CurrentLanguage) : Loc["SteamShortcut.Failed"];
            return false;
        }
    }

    // The entry AddShortcutAsync builds for this game (same ids, same fields), used read-only to
    // know which exe is ours; nothing is applied from it.
    private BinaryVdfNode CraftedEntry() =>
        SteamShortcutsFile.CreateEntry(Game.Title, LaunchExe, LaunchRoot, "");

    // The shortcut this page wrote for this game: recorded next to the other app state.
    private string SteamRecordPath => Path.Combine(appDataRoot, "steam-shortcuts", Game.Definition.Slug + ".appid");

    private int RecordedAppId
    {
        get
        {
            try
            {
                return int.TryParse(File.Exists(SteamRecordPath) ? File.ReadAllText(SteamRecordPath).Trim() : "", out var appId)
                    ? appId : 0;
            }
            catch (IOException) { return 0; }
        }
        set
        {
            try
            {
                if (value == 0) { if (File.Exists(SteamRecordPath)) File.Delete(SteamRecordPath); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(SteamRecordPath)!);
                File.WriteAllText(SteamRecordPath, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (IOException e) { System.Diagnostics.Trace.WriteLine($"phaket: shortcut record failed: {e.Message}"); }
        }
    }

    // Called when the page opens and after every change: the toggle depends on it.
    public void RefreshSteamShortcut()
    {
        if (!Game.IsInstalled) { HasSteamShortcut = false; return; }
        HasSteamShortcut = InSteam();
    }

    // Step 1: ask. Nothing is written until the yes.
    [RelayCommand]
    private void ToggleSteamShortcut()
    {
        if (SteamTarget() is null) { StatusMessage = Loc["Game.NoSteam"]; return; }
        SteamRemoveMode = HasSteamShortcut;
        IsSteamConsentOpen = true;
        StatusMessage = "";
    }

    // Closing the question is "no": nothing was written, so nothing is announced.
    [RelayCommand]
    private void CancelSteamShortcut() => IsSteamConsentOpen = false;

    // Step 2: the only write into Steam's folder, and always with consent: true.
    [RelayCommand]
    private async Task ConfirmSteamShortcutAsync()
    {
        if (!IsSteamConsentOpen || SteamTarget() is not { } target) return;
        var remove = SteamRemoveMode;
        IsSteamConsentOpen = false;
        IsSteamBusy = true;
        try
        {
            // The shortcut starts the game as it is (the managed folder when Phaket has one),
            // with no arguments: a plain double-click works, Steam only adds its overlay.
            var result = remove
                ? await SteamService.RemoveShortcutAsync(target.Root, target.User, null, SteamShortcutsFile.Quote(LaunchExe), consent: true)
                : await SteamService.AddShortcutAsync(target.Root, target.User,
                    new SteamShortcut(Game.Title, LaunchExe, LaunchRoot, "", CoverJpg(), null, null), consent: true);
            if (result is not null) RecordedAppId = remove ? 0 : result.AppId;
            StatusMessage = result switch
            {
                null => Loc["SteamShortcut.NotFound"],
                { SteamRunning: true } => Loc[remove ? "SteamShortcut.RemovedRestartSteam" : "SteamShortcut.AddedRestartSteam"],
                _ => Loc[remove ? "SteamShortcut.Removed" : "SteamShortcut.Added"],
            };
        }
        catch (PhaketException e)
        {
            // Includes STEAM_SHORTCUTS_CORRUPT: the user's other shortcuts are untouched.
            StatusMessage = e.LocalizedMessage(Loc.CurrentLanguage);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"phaket: Steam shortcut failed: {e}");
            StatusMessage = Loc["SteamShortcut.Failed"];
        }
        finally
        {
            IsSteamBusy = false;
            RefreshSteamShortcut();
        }
    }

    // The game's cover, when it is a real picture: a generated card is not a file, so nothing
    // is sent and Steam draws the title. Hero and logo stay Steam's own.
    private byte[]? CoverJpg()
    {
        try
        {
            if (string.IsNullOrEmpty(Game.Cover) || Avalonia.Platform.AssetLoader.Open(new Uri(Game.Cover)) is not { } stream)
                return null;
            using var memory = new MemoryStream();
            using (stream) stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    // "Graj": a Phaket shortcut means Steam starts the package (its own id); with no shortcut the
    // game starts by Steam (the store id) or, for a package without Steam, from its folder. Saves
    // kept beside the package only for a direct launch (PAK-146 B): Steam never carries our env.
    private async Task PlayDirectAsync()
    {
        var root = LaunchRoot;
        var managed = ManagedGameRoot();
        var parallelLaunch = new ParallelLaunchContext(Game.Definition, "steam", HostOs);
        // Exists only when this page really wrote the shortcut; a stale record is a store id.
        var shortcutAppId = HasSteamShortcut ? RecordedAppId : 0;
        try
        {
            IsBusy = true;
            IsSavesSharedNoticeVisible = false;
            StatusMessage = "";
            // Only a direct launch of a package has saves to keep beside it: vanilla (no plan)
            // keeps the user's own profile, exactly as before.
            var plan = new PackageSavesIsolation.Result(SavesIsolationPlanner.Plan(HostOs,
                Game.Definition.SavesIsolationFor(HostOs), null), null);
            if (managed is not null)
            {
                // The first launch of a package copies the existing saves beside it; saying so —
                // only when there is really something to copy — beats a page that looks frozen.
                plan = await Task.Run(() => SavesLaunchPreparation.Prepare(HostOs, Game.Definition, managed, ProfileRootFor(managed)));
                IsPreparingSaves = plan.Seed?.Status == PackageSavesSeed.SeedStatus.Seeded;
            }
            LaunchOutcome started;
            if (shortcutAppId != 0 || managed is null)
                // Through Steam: the shortcut runs the package, the store id the vanilla game.
                // Either way Steam starts it, so the saves are the user's own (SavesShared).
                started = Launch.LaunchShortcut(shortcutAppId != 0 ? shortcutAppId.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                    Game.SteamAppId, Game.ProfileName, parallelLaunch);
            else
                started = Launch.LaunchExecutable(LaunchExe, root, Game.SteamAppId, Game.ProfileName,
                    HelperArguments ?? null, plan.Isolation.Environment, plan.Isolation.SavesShared, parallelLaunch);
            LastLaunch = started;
            if (!started.Started)
            {
                StatusMessage = started.BlockedByParallelPolicy
                    ? started.ParallelLaunchReason!.Message(Loc.CurrentLanguage)
                    : Loc["Game.LaunchFailed"];
                return;
            }
            // Saves that cannot be kept beside the game are said once and the game still starts.
            IsSavesSharedNoticeVisible = started.SavesShared;
            StatusMessage = Loc["Game.Launched"];
            OnPropertyChanged(nameof(CanStopGame));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PhaketException)
        {
            System.Diagnostics.Trace.WriteLine($"phaket: launch failed: {e}");
            StatusMessage = Loc["Game.LaunchFailed"];
        }
        finally
        {
            IsPreparingSaves = false;
            IsBusy = false;
        }
    }


    // The modded copy of this game Phaket installed (a deleted one is not used), and one path:
    // the shortcut and the launch always point at the same folder.
    internal string? ManagedGameRoot()
    {
        try
        {
            var slug = Game.Definition.Slug;
            var installed = new InstallRegistry(Path.Combine(appDataRoot, "installs.json")).All()
                .Where(entry => entry.GameSlug == slug && Directory.Exists(entry.GameRoot))
                .FirstOrDefault();
            return installed?.GameRoot;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null; // an unreadable registry is "no managed copy", never a crash on "play"
        }
    }

    // Vanilla saves live in this installation's own profile; only that folder is ever read.
    private string ProfileRootFor(string installationRoot) => Path.Combine(appDataRoot, "profiles", Game.Definition.Slug,
        SavesLaunchPreparation.Hash(installationRoot)[..12]);

    [RelayCommand]
    private void DismissSavesSharedNotice() => IsSavesSharedNoticeVisible = false;
}