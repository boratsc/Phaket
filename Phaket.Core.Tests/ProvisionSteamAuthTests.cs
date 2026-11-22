using Phaket.Core.Accounts;
using Phaket.Core.Execution;
using Phaket.Core.Omrl;
using Phaket.Core.Platforms;
using Phaket.Core.Tools;

namespace Phaket.Core.Tests;

// PAK-102: a mod install that provisions its own vanilla runs DepotDownloader with the
// Steam sign-in from the Phaket store. The first password prompt is answered from the
// store, a rejected password asks the user once, and Steam Guard goes through the same
// shared prompt as the library install. Fixtures only: the process is faked.
public sealed class ProvisionSteamAuthTests : IDisposable
{
    private const string Manifest = "9223372036854775807";
    private readonly string root = Directory.CreateTempSubdirectory("phaket-provision-auth-").FullName;

    public void Dispose() => Directory.Delete(root, true);

    private sealed class FakeRunner : IDepotProcessRunner
    {
        public Func<IReadOnlyList<string>, Func<bool, string, Func<string, Task>, Task>, Task<int>> Action { get; set; } =
            (_, _) => Task.FromResult(0);
        public List<string> Arguments { get; } = [];
        public List<string> Inputs { get; } = [];
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task Send(string value) { Inputs.Add(value); return Task.CompletedTask; }

        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment,
            Func<bool, string, Func<string, Task>, Task> output, CancellationToken ct)
        {
            Calls++;
            Token = ct;
            Arguments.AddRange(arguments);
            return Action(arguments, output);
        }
    }

    private (ToolBaseGameSource Source, FakeRunner Runner, SteamCredentialStore Store) Fixture(
        Func<DepotPrompt, CancellationToken, Task<string>>? prompt, IProgress<string?>? mobile = null)
    {
        var tools = Path.Combine(root, "tools");
        var binary = Path.Combine(tools, "depotdownloader", "3.4.0", OperatingSystem.IsWindows() ? "DepotDownloader.exe" : "DepotDownloader");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "fake native binary");
        var store = new SteamCredentialStore(new InMemorySecretStore());
        var runner = new FakeRunner();
        var source = new ToolBaseGameSource(depotDownloaderPath: "", legendaryPath: "",
            toolProvider: new ToolProvider(tools), dotnetHostPath: binary,
            steamCredentials: store, steamPrompt: prompt, runner: runner,
            settingsPath: Path.Combine(root, "settings.json"), mobileConfirmation: mobile);
        return (source, runner, store);
    }

    // PAK-105: the host watches mobile confirmations through this sink; null closes it.
    private sealed class RecordingProgress : IProgress<string?>
    {
        public List<string?> Values { get; } = [];
        public void Report(string? value) => Values.Add(value);
    }

    private Task<BaseGamePrepareResult> PrepareAsync(ToolBaseGameSource source) =>
        source.TryPrepareAsync("steam", "2026.8.18", Manifest, Path.Combine(root, "cache"), TestContext.Current.CancellationToken);

    [Fact]
    public async Task StoredPasswordAndGuardCompleteTheProvision()
    {
        var asked = new List<DepotPrompt>();
        var (source, runner, store) = Fixture((prompt, _) => { asked.Add(prompt); return Task.FromResult("guard-code"); });
        await store.SaveAsync("tester", "stored-password", TestContext.Current.CancellationToken);
        runner.Action = async (_, output) =>
        {
            await output(false, "Enter account password for tester: ", runner.Send);
            await output(true, "STEAM GUARD! Please enter the Steam Guard code: ", runner.Send);
            await output(false, "done\n", runner.Send);
            return 0;
        };

        var prepared = await PrepareAsync(source);

        Assert.True(prepared.Success, prepared.ErrorCode);
        // The password never reaches the command line; the account name does.
        Assert.Contains("-username", runner.Arguments);
        Assert.Contains("tester", runner.Arguments);
        Assert.DoesNotContain("stored-password", runner.Arguments);
        Assert.Equal(["stored-password", "guard-code"], runner.Inputs);
        Assert.Equal([DepotPrompt.SteamGuard], asked);
    }

    [Fact]
    public async Task RejectedStoredPasswordAsksTheUserOnce()
    {
        var (source, runner, store) = Fixture((_, _) => Task.FromResult("right-password"));
        await store.SaveAsync("tester", "wrong-password", TestContext.Current.CancellationToken);
        runner.Action = async (_, output) =>
        {
            await output(false, "Enter account password for tester: ", runner.Send);
            await output(false, "Enter account password for tester: ", runner.Send);
            return 0;
        };

        var prepared = await PrepareAsync(source);

        Assert.True(prepared.Success, prepared.ErrorCode);
        Assert.Equal(["wrong-password", "right-password"], runner.Inputs);
    }

    [Fact]
    public async Task WithoutPromptTheProvisionRefusesBeforeStartingTheTool()
    {
        var (source, runner, store) = Fixture(null);
        await store.SaveAsync("tester", "stored-password", TestContext.Current.CancellationToken);

        var prepared = await PrepareAsync(source);

        Assert.False(prepared.Success);
        Assert.Equal(ErrorCodes.BaseGameSignInRequired, prepared.ErrorCode);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task ToolTailIsSanitizedAndLabelledBeforeItReachesTheErrorMessage()
    {
        var (source, runner, store) = Fixture((_, _) => Task.FromResult("guard-code"));
        await store.SaveAsync("tester", "stored-password", TestContext.Current.CancellationToken);
        runner.Action = async (_, output) =>
        {
            await output(false, "Enter account password for tester: ", runner.Send);
            await output(true, "bad\u0000line\r\n" + new string('x', 2500) + "\u001b\n", runner.Send);
            return 5;
        };

        var prepared = await PrepareAsync(source);

        Assert.False(prepared.Success);
        Assert.Equal(ErrorCodes.BaseGameToolFailed, prepared.ErrorCode);
        var detail = prepared.Parameters!["detail"];
        Assert.DoesNotContain(detail, char.IsControl);
        Assert.True(detail.Length <= ToolBaseGameSource.MaxToolOutput, $"tail length {detail.Length}");
        Assert.Contains("External tool output:",
            ErrorMessages.Format(prepared.ErrorCode!, "en", prepared.Parameters));
        Assert.Contains("Wyjście narzędzia zewnętrznego:",
            ErrorMessages.Format(prepared.ErrorCode!, "pl-PL", prepared.Parameters));
    }

    [Fact]
    public void ToolTailSanitizerDropsControlCharactersAndCapsTheLength()
    {
        Assert.Equal("a b c", ToolBaseGameSource.SanitizeToolOutput("a\nb\tc"));
        Assert.Equal("ab", ToolBaseGameSource.SanitizeToolOutput("\u0000a\u0007b\u001b"));
        Assert.Equal(ToolBaseGameSource.MaxToolOutput,
            ToolBaseGameSource.SanitizeToolOutput(new string('x', ToolBaseGameSource.MaxToolOutput * 2)).Length);
    }

    [Fact]
    public async Task CancelledGuardPromptIsAReadableAuthError()
    {
        var (source, runner, store) = Fixture((_, _) => throw new OperationCanceledException());
        await store.SaveAsync("tester", "stored-password", TestContext.Current.CancellationToken);
        runner.Action = async (_, output) =>
        {
            await output(true, "STEAM GUARD! Please enter the Steam Guard code: ", runner.Send);
            return 0;
        };

        var prepared = await PrepareAsync(source);

        Assert.False(prepared.Success);
        Assert.Equal(ErrorCodes.BaseGameAuthCancelled, prepared.ErrorCode);
    }

    [Fact]
    public async Task FailedToolKeepsTheTailButNeverTheCredentials()
    {
        var (source, runner, store) = Fixture((_, _) => Task.FromResult("guard-code"));
        await store.SaveAsync("tester", "stored-password", TestContext.Current.CancellationToken);
        runner.Action = async (_, output) =>
        {
            await output(false, "Enter account password for tester: ", runner.Send);
            await output(true, "InvalidPassword for stored-password\n", runner.Send);
            return 5;
        };

        var prepared = await PrepareAsync(source);

        Assert.False(prepared.Success);
        Assert.Equal(ErrorCodes.BaseGameToolFailed, prepared.ErrorCode);
        var tail = prepared.Parameters!["detail"];
        Assert.Contains("InvalidPassword", tail);
        Assert.DoesNotContain("stored-password", tail);
    }

    [Fact]
    public async Task ToolTimeoutIsReadable()
    {
        var tools = Path.Combine(root, "timeout-tools");
        var binary = Path.Combine(tools, "depotdownloader", "3.4.0", OperatingSystem.IsWindows() ? "DepotDownloader.exe" : "DepotDownloader");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "fake native binary");
        var store = new SteamCredentialStore(new InMemorySecretStore());
        await store.SaveAsync("tester", "stored-password", TestContext.Current.CancellationToken);
        var runner = new FakeRunner();
        runner.Action = async (_, _) => { await Task.Delay(Timeout.Infinite, runner.Token); return 0; };
        var source = new ToolBaseGameSource(depotDownloaderPath: "", legendaryPath: "", timeout: TimeSpan.FromMilliseconds(50),
            toolProvider: new ToolProvider(tools), dotnetHostPath: binary, steamCredentials: store,
            steamPrompt: (_, _) => Task.FromResult("guard"), runner: runner,
            settingsPath: Path.Combine(root, "settings.json"));

        var prepared = await PrepareAsync(source);

        Assert.False(prepared.Success);
        Assert.Equal(ErrorCodes.BaseGameTimeout, prepared.ErrorCode);
    }

    [Fact]
    public async Task MobileConfirmationNoticeOpensAndClosesTheState()
    {
        var mobile = new RecordingProgress();
        var (source, runner, store) = Fixture((_, _) => Task.FromResult("guard-code"), mobile);
        await store.SaveAsync("tester", "stored-password", TestContext.Current.CancellationToken);
        runner.Action = async (_, output) =>
        {
            await output(false, "Enter account password for tester: ", runner.Send);
            await output(true, DepotPromptReader.MobileConfirmation + "\n", runner.Send);
            await output(false, "done\n", runner.Send);
            return 0;
        };

        var prepared = await PrepareAsync(source);

        Assert.True(prepared.Success, prepared.ErrorCode);
        Assert.Equal(new string?[] { DepotPromptReader.MobileConfirmation, null }, mobile.Values);
    }

    [Fact]
    public async Task CancelledRunAlsoClosesTheMobileNotice()
    {
        var mobile = new RecordingProgress();
        var (source, runner, store) = Fixture((_, _) => Task.FromResult("guard-code"), mobile);
        await store.SaveAsync("tester", "stored-password", TestContext.Current.CancellationToken);
        runner.Action = async (_, output) =>
        {
            await output(true, DepotPromptReader.MobileConfirmation + "\n", runner.Send);
            throw new OperationCanceledException();
        };

        var prepared = await PrepareAsync(source);

        Assert.False(prepared.Success);
        Assert.Equal(new string?[] { DepotPromptReader.MobileConfirmation, null }, mobile.Values);
    }

    [Fact]
    public void SharedReaderRecognizesBothPromptsAndKeepsTheTailBounded()
    {
        var reader = new DepotPromptReader();
        DepotPrompt? prompt = null;
        foreach (var ch in "starting\n") reader.Append(false, ch, out _);
        foreach (var ch in "Enter account password for tester: ") prompt = reader.Append(false, ch, out _) ?? prompt;
        Assert.Equal(DepotPrompt.Password, prompt);
        prompt = null;
        string? completed = null;
        foreach (var ch in "STEAM GUARD! Please enter the Steam Guard code: ")
        {
            var found = reader.Append(true, ch, out completed);
            if (found is not null) prompt = found;
        }
        Assert.Equal(DepotPrompt.SteamGuard, prompt);
        Assert.Null(completed);
    }
}