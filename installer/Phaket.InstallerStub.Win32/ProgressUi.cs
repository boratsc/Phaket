using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Phaket.InstallerStub;

/// <summary>
/// Najprostsze AOT-friendly UI: jedno natywne okno Win32 (CreateWindowEx + STATIC + PROGRESS),
/// bez WinForms/WinUI/XAML — zero zarządzanego UI, więc publish AOT zostaje mały. Gdy okna nie
/// da się utworzyć (sesja bez interaktywnego pulpitu), ten sam przebieg idzie w konsoli.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProgressUi : IProgressUi, IDisposable
{
    private const int WsOverlappedWindow = 0x00CF0000;
    private const int WsVisible = 0x10000000;
    private const int WsChild = 0x40000000;
    private const int SsLeft = 0x00000000;
    private const int SwShow = 5;
    private const int WmDestroy = 0x0002;
    private const int WmClose = 0x0010;
    private const int WmSetText = 0x000C;
    private const int WmSetFont = 0x0030;
    private const int WmCtlColorStatic = 0x0138;
    private const int WmPaint = 0x000F;
    private const int WmApp = 0x8000;
    private const int WmRefresh = WmApp + 1;
    private const int GwlpUserData = -21;
    private const int PbmSetRange = 0x0401;
    private const int PbmSetPos = 0x0402;

    private static readonly Win32.WndProc Proc = CallWndProc;

    private readonly Lock gate = new();
    private readonly Queue<(int Percent, string Text)> updates = new();
    private readonly ManualResetEventSlim painted = new(false);
    private readonly bool pl;
    private volatile bool closed;
    private GCHandle self;
    private IntPtr window;
    private IntPtr label;
    private IntPtr bar;

    public ProgressUi(bool pl) => this.pl = pl;

    public bool WindowFailed { get; private set; }

    public static IntPtr CallWndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam) =>
        GCHandle.FromIntPtr(Win32.GetWindowLongPtrW(hwnd, GwlpUserData)).Target is ProgressUi ui
            ? ui.WndProc(hwnd, message, wParam, lParam)
            : IntPtr.Zero;

    public void Start()
    {
        if (!Environment.UserInteractive || !OperatingSystem.IsWindows()) return;

        var thread = new Thread(WindowThread) { IsBackground = true, Name = "PhaketSetup-ui" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        painted.Wait(TimeSpan.FromSeconds(5));   // okno od pierwszej klatki: komunikat błędu zawsze widać
        if (window == IntPtr.Zero) WindowFailed = true;
    }

    public void Report(StubStage stage, int value)
    {
        var clamped = Math.Clamp(value, 0, 100);
        lock (gate) { updates.Enqueue((clamped, Messages.Stage(stage, clamped, pl))); }
        Post(WmRefresh);
    }

    public void Fail(string code)
    {
        lock (gate) { updates.Enqueue((0, Messages.Error(code, pl))); }
        Post(WmRefresh);
        // Okno z komunikatem zostaje do zamknięcia przez użytkownika (max 10 min, potem wychodzimy).
        var stop = Environment.TickCount64 + 10 * 60_000;
        while (!closed && Environment.TickCount64 < stop) Thread.Sleep(50);
    }

    public void Dispose()
    {
        Post(WmClose);
        painted.Dispose();
        if (self.IsAllocated) self.Free();
    }

    private void WindowThread()
    {
        self = GCHandle.Alloc(this);
        try
        {
            if (!Win32.RegisterWndClass(Proc, GetModuleHandle(null), "PhaketInstallerStub")) return;
            window = Win32.CreateWindowExW(0, "PhaketInstallerStub", Messages.Title(pl), WsOverlappedWindow,
                -220, -100, 440, 200, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                GCHandle.ToIntPtr(self));
            if (window == IntPtr.Zero) return;

            label = Win32.CreateWindowExW(0, "STATIC", Messages.Stage(StubStage.Downloading, 0, pl),
                WsChild | WsVisible | SsLeft, 20, 45, 390, 56, window, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            bar = Win32.CreateWindowExW(0, "msctls_progress32", "", WsChild | WsVisible, 20, 115, 390, 24,
                window, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Win32.SendMessageW(label, WmSetFont, Win32.GetStockObject(17 /* DEFAULT_GUI_FONT */), IntPtr.Zero);
            Win32.SendMessageW(bar, PbmSetRange, IntPtr.Zero, 100);

            Win32.ShowWindow(window, SwShow);
            Win32.UpdateWindow(window);
            Drain();
            painted.Set();

            while (Win32.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                Win32.TranslateMessage(ref message);
                Win32.DispatchMessageW(ref message);
            }
        }
        finally
        {
            window = IntPtr.Zero;
            painted.Set();
        }
    }

    private void Post(int message)
    {
        var handle = window;
        if (handle != IntPtr.Zero) Win32.PostMessageW(handle, message, IntPtr.Zero, IntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmRefresh:
                Drain();
                return IntPtr.Zero;
            case WmPaint:
                var paint = Win32.BeginPaint(hwnd, out var ps);
                Win32.FillRect(paint, ref ps.Rect, Win32.GetSysColorBrush(15 /* COLOR_BTNFACE */));
                Win32.EndPaint(hwnd, ref ps);
                Drain();
                return IntPtr.Zero;
            case WmClose:
                Drain();
                Win32.DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmDestroy:
                closed = true;
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
            case WmCtlColorStatic:
                Win32.SetBkMode(wParam, 1 /* TRANSPARENT */);
                return Win32.GetSysColorBrush(15);
            default:
                return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private void Drain()
    {
        (int Percent, string Text)? latest = null;
        lock (gate) { while (updates.TryDequeue(out var next)) latest = next; }
        if (latest is not { } update) return;
        Win32.SendMessageW(bar, PbmSetPos, update.Percent, IntPtr.Zero);
        Win32.SendMessageTextW(label, WmSetText, IntPtr.Zero, update.Text);
    }

    private static IntPtr GetModuleHandle(string? name) => Win32.GetModuleHandleW(name);
}