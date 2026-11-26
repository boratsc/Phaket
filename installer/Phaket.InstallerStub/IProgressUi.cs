namespace Phaket.InstallerStub;

public enum UiMode
{
    Window,
    Console,
    Silent
}

/// <summary>Pasek postępu widziany przez logikę instalacji (okno Win32 albo konsola).</summary>
public interface IProgressUi
{
    void Report(StubStage stage, int percent);
    void Fail(string code);
}