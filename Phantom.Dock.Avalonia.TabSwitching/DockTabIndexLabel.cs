namespace Phantom.Dock.Avalonia.TabSwitching;

/// <summary>
/// A single per-binding index label for a tab (design §4.3). A tab may be numbered by more than one
/// gesture set at once, so a <see cref="DockTabIndexContext"/> carries a collection of these — each
/// carrying the displayed <see cref="Text"/> ("1".."9", "0", "F1"…) and the gesture set that produced
/// it.
/// </summary>
public sealed class DockTabIndexLabel
{
    public DockTabIndexLabel(string text, DockTabSwitchGestures gestureSet)
    {
        Text = text;
        GestureSet = gestureSet;
    }

    /// <summary>The displayed label text, e.g. "1".."9", "0", or "F1"….</summary>
    public string Text { get; }

    /// <summary>The gesture set that numbers this tab with <see cref="Text"/>.</summary>
    public DockTabSwitchGestures GestureSet { get; }
}
