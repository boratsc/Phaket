namespace Phaket.InstallerStub;

/// <summary>Wariant konsolowy (bez okna): te same komunikaty, wypisywane przy zmianie etapu.</summary>
public sealed class ConsoleUi(bool pl) : IProgressUi
{
    public required TextWriter Output { get; init; }
    public required TextWriter Error { get; init; }
    private int lastPercent = -1;

    public void Report(StubStage stage, int percent)
    {
        if (percent == lastPercent && percent is > 0 and < 100) return;
        lastPercent = percent;
        Output.WriteLine(Messages.Stage(stage, percent, pl));
        Output.Flush();
    }

    public void Fail(string code) => Error.WriteLine(Messages.Error(code, pl));
}