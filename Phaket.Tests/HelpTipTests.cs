using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Phaket.Controls;

namespace Phaket.Tests;

// PAK-87 follow-up: the tooltip closes on a second click, Escape, a click outside, and
// hover-out while unpinned, without reopening itself in a focus/hover loop.
public sealed class HelpTipTests
{
    private static (Window Window, Button Trigger, Popup Popup, TextBlock Outside) Create()
    {
        var tip = new HelpTip { Label = "Etykieta", Message = "Opis pomocy" };
        var outside = new TextBlock { Text = "tło", Margin = new Thickness(0, 40, 0, 0) };
        var window = new Window { Width = 400, Height = 240, Content = new StackPanel { Children = { tip, outside } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, tip.FindControl<Button>("Trigger")!, tip.FindControl<Popup>("TipPopup")!, outside);
    }

    private static void Click(Window window, Control target)
    {
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Second_trigger_click_closes_the_tip()
    {
        var (window, trigger, popup, _) = Create();
        try
        {
            Click(window, trigger);
            Assert.True(popup.IsOpen);
            Click(window, trigger);
            Assert.False(popup.IsOpen);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Escape_closes_the_tip()
    {
        var (window, trigger, popup, _) = Create();
        try
        {
            Click(window, trigger);
            Assert.True(popup.IsOpen);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(popup.IsOpen);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Click_on_a_non_focusable_background_closes_the_tip()
    {
        var (window, trigger, popup, outside) = Create();
        try
        {
            Click(window, trigger);
            Assert.True(popup.IsOpen);
            Click(window, outside);
            Assert.False(popup.IsOpen);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Hover_out_closes_an_unpinned_tip()
    {
        var (window, trigger, popup, _) = Create();
        try
        {
            window.MouseMove(new Point(350, 200));
            window.MouseMove(trigger.TranslatePoint(new Point(18, 18), window)!.Value);
            Dispatcher.UIThread.RunJobs();
            Assert.True(popup.IsOpen);
            window.MouseMove(new Point(350, 200));
            await Task.Delay(400);
            Dispatcher.UIThread.RunJobs();
            Assert.False(popup.IsOpen);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Focused_tip_stays_open_without_flickering()
    {
        var (window, trigger, popup, _) = Create();
        try
        {
            trigger.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            Assert.True(popup.IsOpen);
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(250);
                Dispatcher.UIThread.RunJobs();
                Assert.True(popup.IsOpen, $"tip zamknął się w spoczynku po {i + 1} próbkach");
            }
        }
        finally { window.Close(); }
    }
}