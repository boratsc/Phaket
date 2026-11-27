using Phaket.Core.Accounts;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Core.Tests;
using Phaket.Core.Tools;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

public sealed class SteamInstallFlowViewModelTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "phaket-steam-vm-" + Guid.NewGuid().ToString("N"));
    private readonly Runner runner = new();
    private readonly LocalizationService loc = new();
    private readonly SteamInstallFlowViewModel vm;
    private readonly string binary;
    public SteamInstallFlowViewModelTests()
    {
        Directory.CreateDirectory(root);
        binary = Path.Combine(root, "depot"); File.WriteAllText(binary, "fake");
        var adapter = new DepotDownloaderInstallAdapter(runner, Path.Combine(root, "config"), binary);
        vm = new(new OwnedSteamGame("945360", "Among Us", null, null, "test", false), new GameDefinition("among-us", [], MainExecutable: "game.exe"), adapter, () => Task.FromResult<string?>(Path.Combine(root, "picked")), loc: loc);
        vm.TargetDir = Path.Combine(root, "games");
        vm.OsArch = "64";
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task OpenLoadsArchitectureAndUnknownRequiresChoice()
    {
        var adapter = new DepotDownloaderInstallAdapter(runner, Path.Combine(root, "arch-config"), binary);
        var flow = new SteamInstallFlowViewModel(new OwnedSteamGame("945360", "Game", null, null, "test", false),
            new GameDefinition("game", [], MainExecutable: "game.exe"), adapter, () => Task.FromResult<string?>(null),
            () => Task.FromResult<IReadOnlyList<BuildOption>>([new("64", Arch: "x64"), new("32", Arch: "x86"), new("unknown")]), loc);
        await flow.OpenCommand.ExecuteAsync(null);
        Assert.Equal("64", flow.OsArch);
        flow.SelectedBuild = flow.BuildOptions[1]; Assert.Equal("32", flow.OsArch);
        flow.SelectedBuild = flow.BuildOptions[2]; Assert.True(flow.ArchitectureUnknown);
        flow.PrepareCommand.Execute(null); Assert.Null(flow.Preview); Assert.Equal(loc["SteamInstall.Error.ArchRequired"], flow.Status);
    }

    [Fact]
    public async Task CancellingCredentialStopsInstall()
    {
        vm.Username = "user";
        runner.Action = async (_, output) => { await output(false, "Enter account password for user: ", runner.Send); return 0; };
        vm.PrepareCommand.Execute(null); vm.ConsentSession = vm.AcceptPlan = true;
        var install = vm.ExecuteCommand.ExecuteAsync(null);
        Assert.True(vm.DialogCredentialOpen);
        vm.CancelDialogCommand.Execute(null);
        await install;
        Assert.False(vm.Success); Assert.Equal(loc["SteamInstall.Error.CredentialCancelled"], vm.Status);
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task CancellingTwoFactorStopsInstall()
    {
        vm.Username = "user";
        runner.Action = async (_, output) =>
        {
            await output(true, "STEAM GUARD! Please enter code: ", runner.Send);
            return 0;
        };
        vm.PrepareCommand.Execute(null); vm.ConsentSession = vm.AcceptPlan = true;
        var install = vm.ExecuteCommand.ExecuteAsync(null);
        Assert.True(vm.DialogTwoFactorOpen);
        vm.CancelDialogCommand.Execute(null);
        await install;
        Assert.False(vm.Success);
        Assert.Equal(loc["SteamInstall.Error.CredentialCancelled"], vm.Status);
        Assert.Empty(runner.Inputs);
        Assert.False(Directory.Exists(vm.Preview!.Plan.TargetPath));
    }

    [Fact]
    public async Task PreviewAndResetAndFallback()
    {
        Assert.Single(vm.BuildOptions);
        Assert.Contains("rejestru", vm.SelectedBuild!.Name);
        Assert.Contains("x64", vm.ArchitectureWarning);
        vm.SelectedBuild = new("old", "10", "123", Arch: "x64");
        vm.PrepareCommand.Execute(null);
        Assert.Equal("945360", vm.Preview!.Plan.AppId);
        Assert.Equal("10", vm.Preview.Plan.DepotId);
        Assert.Equal("123", vm.Preview.Plan.ManifestId);
        Assert.Equal(Path.Combine(vm.TargetDir, "945360"), vm.Preview.Plan.TargetPath);
        Assert.Equal("64", vm.Preview.Plan.OsArch);
        Assert.Equal(loc["SteamInstall.UnknownSize"], vm.PreviewSize);
        vm.AcceptPlan = true; vm.OsArch = "32";
        Assert.Null(vm.Preview); Assert.False(vm.AcceptPlan);
        vm.PrepareCommand.Execute(null); vm.AcceptPlan = true;
        await vm.PickFolderCommand.ExecuteAsync(null);
        Assert.Null(vm.Preview); Assert.False(vm.AcceptPlan);
        vm.PrepareCommand.Execute(null); vm.AcceptPlan = true; vm.SelectedBuild = vm.BuildOptions[0];
        Assert.Null(vm.Preview); Assert.False(vm.AcceptPlan);
    }

    [Fact]
    public async Task ConsentGuardsAndConflict()
    {
        Directory.CreateDirectory(Path.Combine(vm.TargetDir, "945360"));
        File.WriteAllText(Path.Combine(vm.TargetDir, "945360", "existing"), "test");
        vm.PrepareCommand.Execute(null);
        Assert.Single(vm.Preview!.Plan.Conflicts);
        Assert.False(vm.ExecuteCommand.CanExecute(null));
        await vm.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(loc["SteamInstall.Error.ConsentRequired"], vm.Status);
        vm.ConsentSession = true;
        await vm.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(loc["SteamInstall.Error.PlanNotAccepted"], vm.Status);
        vm.AcceptPlan = true;
        await vm.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(loc["SteamInstall.Error.OverwriteRequired"], vm.Status);
        Assert.False(vm.ExecuteCommand.CanExecute(null));
        vm.ConsentOverwrite = true;
        Assert.True(vm.ExecuteCommand.CanExecute(null));
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task SuccessfulInstallUsesCredentialsAndHook()
    {
        vm.Username = "user";
        runner.Action = async (args, output) =>
        {
            await output(false, "Enter account password for user: ", runner.Send);
            await output(true, "STEAM GUARD! Please enter code: ", runner.Send);
            await output(false, "finished\n", runner.Send);
            string target = args[args.IndexOf("-dir") + 1];
            Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "game.exe"), "x");
            return 0;
        };
        bool installed = false; vm.OnInstalled = () => installed = true;
        vm.PrepareCommand.Execute(null); vm.ConsentSession = vm.AcceptPlan = true;
        var install = vm.ExecuteCommand.ExecuteAsync(null);
        Assert.True(vm.DialogCredentialOpen); Assert.False(install.IsCompleted);
        vm.Password = "password"; vm.ConfirmCredentialCommand.Execute(null);
        for (int i = 0; i < 100 && !vm.DialogTwoFactorOpen; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(vm.DialogTwoFactorOpen); Assert.False(install.IsCompleted);
        vm.TwoFactorCode = "guard"; vm.ConfirmTwoFactorCommand.Execute(null);
        await install;
        Assert.True(vm.Success); Assert.True(installed);
        Assert.Equal(new[] { "password", "guard" }, runner.Inputs);
        // Progress<T> posts to the test synchronization context.
        for (int i = 0; i < 100 && !vm.LogTail.Contains("finished"); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Contains("finished", vm.LogTail);
    }

    [Fact]
    public async Task FailureAndMissingBinaryAreLocalized()
    {
        vm.PrepareCommand.Execute(null); vm.ConsentSession = vm.AcceptPlan = true; vm.Username = "user";
        runner.Action = async (_, output) => { await output(true, "failure detail\n", runner.Send); return 1; };
        await vm.ExecuteCommand.ExecuteAsync(null);
        Assert.StartsWith(loc["SteamInstall.Error.Failed"], vm.Status);
        Assert.Contains("failure detail", vm.Status);
        // Bez binarium adapter sam pobiera narzędzie przez ToolProvider; offline kończy się
        // to zlokalizowana porażka, nigdy surowy wyjątek ani kod błędu.
        var offlineRunner = new Runner();
        var offline = new DepotDownloaderInstallAdapter(offlineRunner, Path.Combine(root, "offline-config"),
            Path.Combine(root, "missing-depot"), new ToolProvider(Path.Combine(root, "offline-tools"), new HttpClient(new OfflineHandler())));
        var offlineVm = new SteamInstallFlowViewModel(new OwnedSteamGame("945360", "Among Us", null, null, "test", false),
            new GameDefinition("among-us", [], MainExecutable: "game.exe"), offline, () => Task.FromResult<string?>(Path.Combine(root, "picked")), loc: loc);
        offlineVm.TargetDir = Path.Combine(root, "offline-games");
        offlineVm.OsArch = "64";
        offlineVm.Username = "user";
        offlineVm.PrepareCommand.Execute(null);
        offlineVm.ConsentSession = offlineVm.AcceptPlan = true;
        await offlineVm.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(loc["SteamInstall.Error.Failed"], offlineVm.Status);
        Assert.DoesNotContain("TOOL_OFFLINE", offlineVm.Status, StringComparison.Ordinal);
        Assert.Equal(0, offlineRunner.Calls);
    }

    // Every request fails as if there were no network, so ToolProvider reports TOOL_OFFLINE.
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("no network"));
    }

    [Fact]
    public async Task RegistryOptionsAndUnverifiedInstall()
    {
        var adapter = new DepotDownloaderInstallAdapter(runner, Path.Combine(root, "registry-config"), binary);
        var registryVm = new SteamInstallFlowViewModel(new OwnedSteamGame("945360", "Game", null, null, "test", false),
            new GameDefinition("game", [], MainExecutable: "game.exe"), adapter, () => Task.FromResult<string?>(null),
            () => Task.FromResult<IReadOnlyList<BuildOption>>([new("build", "12", "34", 100, "x64")]), loc);
        registryVm.TargetDir = Path.Combine(root, "registry-games");
        await registryVm.LoadBuildsCommand.ExecuteAsync(null);
        registryVm.PrepareCommand.Execute(null);
        Assert.Equal("12", registryVm.Preview!.Plan.DepotId);
        Assert.Equal("34", registryVm.Preview.Plan.ManifestId);
        Assert.Equal(100, registryVm.Preview.SizeBytes);
        registryVm.Username = "user"; registryVm.ConsentSession = registryVm.AcceptPlan = true;
        await registryVm.ExecuteCommand.ExecuteAsync(null);
        Assert.False(registryVm.Success);
        Assert.Equal(loc["SteamInstall.Error.Unverified"], registryVm.Status);
    }

    private sealed class Runner : IDepotProcessRunner
    {
        public int Calls { get; private set; }
        public List<string> Inputs { get; } = [];
        public List<string> LastArguments { get; private set; } = [];
        public Func<List<string>, Func<bool, string, Func<string, Task>, Task>, Task<int>> Action { get; set; } = (_, _) => Task.FromResult(0);
        public Task Send(string s) { Inputs.Add(s); return Task.CompletedTask; }
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment, Func<bool, string, Func<string, Task>, Task> output, CancellationToken ct)
        { Calls++; LastArguments = arguments.ToList(); return Action(LastArguments, output); }
    }

    // PAK-60: Steam signs in once; stored credentials auto-fill the tool.
    private sealed class Harness : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "phaket-steam-cred-" + Guid.NewGuid().ToString("N"));
        public readonly InMemorySecretStore Memory = new();
        public readonly Runner Runner = new();
        public readonly SteamCredentialStore Credentials;
        public readonly string Binary;
        public readonly SteamInstallFlowViewModel Flow;
        public readonly LocalizationService Loc = new();

        public Harness()
        {
            Directory.CreateDirectory(Root);
            Binary = Path.Combine(Root, "depot");
            File.WriteAllText(Binary, "fake");
            Credentials = new SteamCredentialStore(Memory);
            var adapter = new DepotDownloaderInstallAdapter(Runner, Path.Combine(Root, "config"), Binary);
            Flow = new(new OwnedSteamGame("945360", "Among Us", null, null, "test", false),
                new GameDefinition("among-us", [], MainExecutable: "game.exe"), adapter, () => Task.FromResult<string?>(null), loc: Loc, credentialStore: Credentials);
            Flow.TargetDir = Path.Combine(Root, "games");
            Flow.OsArch = "64";
            Runner.Action = async (args, output) =>
            {
                await output(false, "Enter account password for tester: ", Runner.Send);
                string target = args[args.IndexOf("-dir") + 1];
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "game.exe"), "x");
                return 0;
            };
        }

        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task StoredCredentialsAutoFillWithoutDialog()
    {
        using var h = new Harness();
        await h.Credentials.SaveAsync("tester", "stored-password", TestContext.Current.CancellationToken);
        await h.Flow.OpenCommand.ExecuteAsync(null);
        Assert.Equal("tester", h.Flow.Username);
        Assert.True(h.Flow.StoredCredentialKnown);
        h.Flow.PrepareCommand.Execute(null);
        h.Flow.ConsentSession = h.Flow.AcceptPlan = true;
        await h.Flow.ExecuteCommand.ExecuteAsync(null);
        Assert.True(h.Flow.Success);
        Assert.False(h.Flow.SigninDialog!.IsOpen);
        Assert.False(h.Flow.DialogCredentialOpen);
        // The tool got the account name as an argument and the password on stdin only.
        Assert.Equal(["stored-password"], h.Runner.Inputs);
        Assert.Contains("-username", h.Runner.LastArguments);
        Assert.Contains("tester", h.Runner.LastArguments);
    }

    // A stored password the tool rejects must not be auto-filled in a loop.
    [Fact]
    public async Task SecondPasswordPromptAsksTheUser()
    {
        using var h = new Harness();
        await h.Credentials.SaveAsync("tester", "wrong-password", TestContext.Current.CancellationToken);
        h.Runner.Action = async (args, output) =>
        {
            await output(false, "Enter account password for tester: ", h.Runner.Send);
            await output(false, "Enter account password for tester: ", h.Runner.Send);
            string target = args[args.IndexOf("-dir") + 1];
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "game.exe"), "x");
            return 0;
        };
        await h.Flow.OpenCommand.ExecuteAsync(null);
        h.Flow.PrepareCommand.Execute(null);
        h.Flow.ConsentSession = h.Flow.AcceptPlan = true;
        var install = h.Flow.ExecuteCommand.ExecuteAsync(null);
        for (int i = 0; i < 100 && !h.Flow.DialogCredentialOpen; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(h.Flow.DialogCredentialOpen);
        h.Flow.Password = "right-password";
        h.Flow.ConfirmCredentialCommand.Execute(null);
        await install;
        Assert.True(h.Flow.Success);
        Assert.Equal(["wrong-password", "right-password"], h.Runner.Inputs);
    }

    [Fact]
    public async Task MissingCredentialsAskOnceAndSaveAfterConfirm()
    {
        using var h = new Harness();
        h.Flow.Username = "tester";
        h.Flow.PrepareCommand.Execute(null);
        h.Flow.ConsentSession = h.Flow.AcceptPlan = true;
        var install = h.Flow.ExecuteCommand.ExecuteAsync(null);
        Assert.True(h.Flow.SigninDialog!.IsOpen);
        Assert.False(h.Flow.DialogCredentialOpen);
        Assert.False(h.Flow.SigninDialog.Remember);
        h.Flow.SigninDialog.Password = "fresh-password";
        // Storing the sign-in is opt-in (PAK-60): only this explicit tick saves it.
        h.Flow.SigninDialog.Remember = true;
        await h.Flow.SigninDialog.ConfirmCommand.ExecuteAsync(null);
        await install;
        Assert.True(h.Flow.Success);
        Assert.Equal(["fresh-password"], h.Runner.Inputs);
        // Consent given: the sign-in is now in the store and the dialog closed.
        var saved = await h.Credentials.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("fresh-password", saved!.Password);
        Assert.False(h.Flow.SigninDialog.IsOpen);
    }

    [Fact]
    public async Task WithoutConsentNothingIsStoredButInstallStillRuns()
    {
        using var h = new Harness();
        h.Flow.Username = "tester";
        h.Flow.PrepareCommand.Execute(null);
        h.Flow.ConsentSession = h.Flow.AcceptPlan = true;
        var install = h.Flow.ExecuteCommand.ExecuteAsync(null);
        h.Flow.SigninDialog!.Remember = false;
        h.Flow.SigninDialog.Password = "one-off";
        await h.Flow.SigninDialog.ConfirmCommand.ExecuteAsync(null);
        await install;
        Assert.True(h.Flow.Success);
        Assert.Equal(["one-off"], h.Runner.Inputs);
        Assert.Null(await h.Credentials.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellingSigninStopsInstallBeforeRunningTool()
    {
        using var h = new Harness();
        h.Flow.Username = "tester";
        h.Flow.PrepareCommand.Execute(null);
        h.Flow.ConsentSession = h.Flow.AcceptPlan = true;
        var install = h.Flow.ExecuteCommand.ExecuteAsync(null);
        h.Flow.SigninDialog!.CancelCommand.Execute(null);
        await install;
        Assert.False(h.Flow.Success);
        Assert.Equal(h.Loc["SteamInstall.Error.CredentialCancelled"], h.Flow.Status);
        Assert.Equal(0, h.Runner.Calls);
        Assert.Empty(h.Runner.Inputs);
        Assert.Null(await h.Credentials.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmptyUsernameBlocksSigninAndNothingIsStored()
    {
        using var h = new Harness();
        h.Flow.PrepareCommand.Execute(null);
        h.Flow.ConsentSession = h.Flow.AcceptPlan = true;
        await h.Flow.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(h.Loc["SteamInstall.Error.Username"], h.Flow.Status);
        Assert.Equal(0, h.Runner.Calls);
        Assert.False(h.Flow.SigninDialog!.IsOpen);
    }

    [Fact]
    public async Task TwoFactorAlwaysAsksAndIsNeverStored()
    {
        using var h = new Harness();
        await h.Credentials.SaveAsync("tester", "stored-password", TestContext.Current.CancellationToken);
        h.Runner.Action = async (args, output) =>
        {
            await output(false, "Enter account password for tester: ", h.Runner.Send);
            await output(true, "STEAM GUARD! Please enter code: ", h.Runner.Send);
            string target = args[args.IndexOf("-dir") + 1];
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "game.exe"), "x");
            return 0;
        };
        await h.Flow.OpenCommand.ExecuteAsync(null);
        h.Flow.PrepareCommand.Execute(null);
        h.Flow.ConsentSession = h.Flow.AcceptPlan = true;
        var install = h.Flow.ExecuteCommand.ExecuteAsync(null);
        for (int i = 0; i < 100 && !h.Flow.DialogTwoFactorOpen; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(h.Flow.DialogTwoFactorOpen);
        h.Flow.TwoFactorCode = "ABC12";
        h.Flow.ConfirmTwoFactorCommand.Execute(null);
        await install;
        Assert.True(h.Flow.Success);
        Assert.Equal(["stored-password", "ABC12"], h.Runner.Inputs);
        // The single-use code must not reach the store; the stored sign-in stays intact.
        Assert.Equal("stored-password", (await h.Credentials.ReadAsync(TestContext.Current.CancellationToken))!.Password);
        Assert.DoesNotContain("ABC12", System.Text.Encoding.UTF8.GetString(
            await h.Memory.GetAsync(SteamCredentialStore.SecretKey, TestContext.Current.CancellationToken) ?? []), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreFailureIsReportedAndInstallStops()
    {
        using var h = new Harness();
        string code = "SECRET_STORE_LOCKED";
        var locked = new SteamCredentialStore(new FailingStore(code));
        var adapter = new DepotDownloaderInstallAdapter(h.Runner, Path.Combine(h.Root, "locked-config"), h.Binary);
        var flow = new SteamInstallFlowViewModel(new OwnedSteamGame("945360", "Among Us", null, null, "test", false),
            new GameDefinition("among-us", [], MainExecutable: "game.exe"), adapter, () => Task.FromResult<string?>(null), null, h.Loc, locked);
        flow.TargetDir = Path.Combine(h.Root, "locked-games");
        flow.OsArch = "64";
        flow.Username = "tester";
        flow.PrepareCommand.Execute(null);
        flow.ConsentSession = flow.AcceptPlan = true;
        await flow.ExecuteCommand.ExecuteAsync(null);
        Assert.False(flow.Success);
        Assert.Equal(h.Loc["Account.SteamCred.Error.Locked"], flow.Status);
    }

    private sealed class FailingStore(string code) : ISecretStore
    {
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) => throw new SecretStoreException(code, "Magazyn zablokowany / Store locked.");
        public Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default) => throw new SecretStoreException(code, "Magazyn zablokowany / Store locked.");
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new SecretStoreException(code, "Magazyn zablokowany / Store locked.");
    }
}

