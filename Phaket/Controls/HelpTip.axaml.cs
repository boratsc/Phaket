using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
    private bool pinnedByClick;
    private TopLevel? topLevel;

    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    public HelpTip()
    {
        InitializeComponent();
        hoverCloseTimer.Tick += OnHoverCloseTimerTick;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        topLevel = TopLevel.GetTopLevel(this);
        topLevel?.AddHandler(PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        topLevel?.RemoveHandler(PointerPressedEvent, OnTopLevelPointerPressed);
        topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    // A click pins the tip open; a second click, Escape or any click outside closes it.
    private void OnTriggerClick(object? sender, RoutedEventArgs e)
    {
        hoverCloseTimer.Stop();
        pinnedByClick = !pinnedByClick;
        TipPopup.IsOpen = pinnedByClick;
    }

    private void CloseTip()
    {
        hoverCloseTimer.Stop();
        pinnedByClick = false;
        TipPopup.IsOpen = false;
    }

    private void OnTopLevelPointerPressed(object? sender, PointerEventArgs e)
    {
        if (!TipPopup.IsOpen || IsInsideTip(e.Source as Visual)) return;
        CloseTip();
    }

    private bool IsInsideTip(Visual? source)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
            if (ReferenceEquals(visual, Trigger) || ReferenceEquals(visual, TipContent)) return true;
        return false;
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !TipPopup.IsOpen) return;
        CloseTip();
        e.Handled = true;
    }

    private void OnTriggerGotFocus(object? sender, FocusChangedEventArgs e)
    {
        // Only keyboard focus opens the tip: pointer focus arrives together with Click,
        // so opening here too would flip the first click back to closed.
        if (e.NavigationMethod == NavigationMethod.Tab) TipPopup.IsOpen = true;
    }

    private void OnTriggerLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!pinnedByClick) ScheduleHoverClose();
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (ReferenceEquals(sender, Trigger)) pointerOverTrigger = true;
        else pointerOverPopup = true;

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
        if (!pinnedByClick && !pointerOverTrigger && !pointerOverPopup)
            TipPopup.IsOpen = false;
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        hoverCloseTimer.Stop();
        pointerOverTrigger = false;
        pointerOverPopup = false;
        pinnedByClick = false;
    }
}