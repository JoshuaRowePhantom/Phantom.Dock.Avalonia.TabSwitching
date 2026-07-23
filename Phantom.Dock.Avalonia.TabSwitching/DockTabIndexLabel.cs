using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Phantom.Dock.Avalonia.TabSwitching;

/// <summary>
/// A single per-binding index label for a tab (design §4.3). A tab may be numbered by more than one
/// gesture set at once, so a <see cref="DockTabIndexContext"/> carries a collection of these — each
/// carrying the displayed <see cref="Text"/> ("1".."9", "0", "F1"…) and the gesture set that produced
/// it.
///
/// The label owns its own <see cref="IsVisible"/> flag so that the default badge theme can fade in
/// ONLY those labels whose gesture set exactly matches the currently held modifiers (#1121). Each
/// chord shows just its own indices — Alt+Shift no longer lights up the Alt-only labels.
/// </summary>
public sealed class DockTabIndexLabel : INotifyPropertyChanged
{
    private bool _isVisible;

    public DockTabIndexLabel(string text, DockTabSwitchGestures gestureSet)
    {
        Text = text;
        GestureSet = gestureSet;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The displayed label text, e.g. "1".."9", "0", or "F1"….</summary>
    public string Text { get; }

    /// <summary>The gesture set that numbers this tab with <see cref="Text"/>.</summary>
    public DockTabSwitchGestures GestureSet { get; }

    /// <summary>
    /// True while the currently held modifier set exactly matches <see cref="GestureSet"/>.Modifiers.
    /// Drives the per-label badge fade — so a tab numbered by both Alt and Alt+Shift shows only the
    /// label whose gesture set matches the held chord.
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }
}
