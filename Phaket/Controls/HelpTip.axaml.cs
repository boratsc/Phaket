using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Phaket.Controls;

public partial class HelpTip : UserControl
{
    public static readonly Avalonia.StyledProperty<string> LabelProperty =
        Avalonia.AvaloniaProperty.Register<HelpTip, string>(nameof(Label), "");
    public static readonly Avalonia.StyledProperty<string> MessageProperty =
        Avalonia.AvaloniaProperty.Register<HelpTip, string>(nameof(Message), "");

    private readonly DispatcherTimer hoverCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private bool pointerOverTrigger;
    private bool pointerOverPopup;
    private bool dismissedWhileFocused;

    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    public HelpTip()
    {
        InitializeComponent();
        hoverCloseTimer.Tick += OnHoverCloseTimerTick;
    }

    private void OnTriggerClick(object? sender, RoutedEventArgs e)
    {
        dismissedWhileFocused = false;
        TipPopup.IsOpen = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !TipPopup.IsOpen) return;
        CloseTip();
        e.Handled = true;
    }

    private void OnPopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseTip();
        e.Handled = true;
    }

    private void CloseTip()
    {
        hoverCloseTimer.Stop();
        dismissedWhileFocused = true;
        TipPopup.IsOpen = false;
        Trigger.Focus(NavigationMethod.Unspecified);
    }

    private void OnTriggerGotFocus(object? sender, RoutedEventArgs e)
    {
        if (!dismissedWhileFocused) TipPopup.IsOpen = true;
    }

    private void OnTriggerLostFocus(object? sender, RoutedEventArgs e)
    {
        dismissedWhileFocused = false;
        ScheduleHoverClose();
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (ReferenceEquals(sender, Trigger))
        {
            pointerOverTrigger = true;
            dismissedWhileFocused = false;
        }
        else
        {
            pointerOverPopup = true;
        }

        hoverCloseTimer.Stop();
        TipPopup.IsOpen = true;
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (ReferenceEquals(sender, Trigger)) pointerOverTrigger = false;
        else pointerOverPopup = false;
        ScheduleHoverClose();
    }

    private void ScheduleHoverClose()
    {
        hoverCloseTimer.Stop();
        hoverCloseTimer.Start();
    }

    private void OnHoverCloseTimerTick(object? sender, EventArgs e)
    {
        hoverCloseTimer.Stop();
        if (!Trigger.IsFocused && !pointerOverTrigger && !pointerOverPopup)
            TipPopup.IsOpen = false;
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        hoverCloseTimer.Stop();
        pointerOverTrigger = false;
        pointerOverPopup = false;
        TipPopup.IsOpen = false;
    }
}
