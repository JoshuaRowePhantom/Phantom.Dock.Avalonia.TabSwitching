using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Phantom.Dock.Avalonia.TabSwitching;

/// <summary>
/// The controller-owned, per-realized-header data object the index badge template binds to
/// (design §4.3). It is a dependency-free INPC object — it never references a dockable or a view-model.
/// Because a <c>DockControl</c> may carry several gesture sets, a single tab can be numbered by more
/// than one binding, so the context exposes a <see cref="Labels"/> collection plus a single-label
/// <see cref="Label"/> convenience.
/// </summary>
public sealed class DockTabIndexContext : INotifyPropertyChanged
{
    private IReadOnlyList<DockTabIndexLabel> _labels = Array.Empty<DockTabIndexLabel>();
    private int _index;
    private bool _isVisible;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// One entry per gesture set that numbers this tab (may be empty, one, or several). Empty ⇒ the tab
    /// is out of range for every gesture set and no badge is shown.
    /// </summary>
    public IReadOnlyList<DockTabIndexLabel> Labels
    {
        get => _labels;
        set
        {
            _labels = value ?? Array.Empty<DockTabIndexLabel>();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Label));
        }
    }

    /// <summary>
    /// Convenience: the primary (first) label's text, or <c>null</c> when the tab is out of range for
    /// every gesture set (⇒ hide the badge).
    /// </summary>
    public string? Label => _labels.Count > 0 ? _labels[0].Text : null;

    /// <summary>The zero-based order index of the primary binding.</summary>
    public int Index
    {
        get => _index;
        set
        {
            if (_index == value)
            {
                return;
            }

            _index = value;
            OnPropertyChanged();
        }
    }

    /// <summary>True while the activation modifier is held (drives the badge fade-in).</summary>
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
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
