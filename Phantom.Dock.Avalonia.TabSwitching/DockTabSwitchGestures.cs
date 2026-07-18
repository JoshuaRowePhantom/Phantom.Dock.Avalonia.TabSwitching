using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace Phantom.Dock.Avalonia.TabSwitching;

/// <summary>
/// Which key ranges participate in a <see cref="DockTabSwitchGestures"/> set, in the order they map
/// to indices <c>1..N</c>.
/// </summary>
[Flags]
public enum DockTabSwitchKeys
{
    /// <summary><c>D1..D9</c> then <c>D0</c> (so "0" is the 10th tab).</summary>
    Digits = 1,

    /// <summary><c>F1..F12</c>.</summary>
    FunctionKeys = 2,
}

/// <summary>
/// The ordering root a gesture set numbers and activates. Scope is a property of each gesture set,
/// not a single global switch, so multiple scopes can coexist on one <c>DockControl</c>.
/// </summary>
public enum DockTabSwitchScope
{
    /// <summary>Every switchable strip under the <c>DockControl</c> (opt-out via <c>IsSwitchable</c>).</summary>
    AllSwitchable,

    /// <summary>Only the strip belonging to the currently focused dock.</summary>
    FocusedDockOnly,
}

/// <summary>
/// A single gesture-set + scope binding (design §4.1). This is the stub type registered by the
/// scaffolding commit; the gesture → index map (<c>BuildMap</c>) and activation are filled in by the
/// gesture commit of the #1073 epic.
/// </summary>
public sealed class DockTabSwitchGestures
{
    /// <summary>Which modifier(s) must be held. Default: <see cref="KeyModifiers.Alt"/>.</summary>
    public KeyModifiers Modifiers { get; set; } = KeyModifiers.Alt;

    /// <summary>Which key ranges participate, in the order they map to <c>1..N</c>. Default: digits.</summary>
    public DockTabSwitchKeys Keys { get; set; } = DockTabSwitchKeys.Digits;

    /// <summary>The scope this gesture set numbers/activates. Default: <see cref="DockTabSwitchScope.AllSwitchable"/>.</summary>
    public DockTabSwitchScope Scope { get; set; } = DockTabSwitchScope.AllSwitchable;

    /// <summary>Optional explicit override: an ordered list wins over <see cref="Modifiers"/>/<see cref="Keys"/>.</summary>
    public IList<KeyGesture>? Gestures { get; set; }

    /// <summary>
    /// The digit keys, in the order they map to indices <c>0..9</c>: <c>D1→0 … D9→8, D0→9</c>
    /// (so "0" is the tenth tab, matching the legacy <c>GetDigitIndex</c>/<c>AltShortcutLabelForIndex</c>
    /// behavior).
    /// </summary>
    private static readonly Key[] DigitKeys =
    {
        Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.D8, Key.D9, Key.D0,
    };

    /// <summary>The function keys, in the order they map to indices: <c>F1→0 … F12→11</c>.</summary>
    private static readonly Key[] FunctionKeys =
    {
        Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6,
        Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12,
    };

    /// <summary>
    /// Builds the ordered gesture → index map for this gesture set (design §4.1). The index of a
    /// gesture is its position in the returned list. An explicit <see cref="Gestures"/> list wins over
    /// <see cref="Modifiers"/>/<see cref="Keys"/>; otherwise digits (<c>D1..D9, D0</c>) occupy the
    /// leading indices and, if <see cref="DockTabSwitchKeys.FunctionKeys"/> is also set, function keys
    /// (<c>F1..F12</c>) continue after them in a deterministic order.
    /// </summary>
    public DockTabSwitchGestureMap BuildMap()
    {
        // An explicit list overrides everything; index = position in the list.
        if (Gestures is { Count: > 0 })
        {
            return new DockTabSwitchGestureMap(new List<KeyGesture>(Gestures));
        }

        var gestures = new List<KeyGesture>();

        if (Keys.HasFlag(DockTabSwitchKeys.Digits))
        {
            foreach (var key in DigitKeys)
            {
                gestures.Add(new KeyGesture(key, Modifiers));
            }
        }

        if (Keys.HasFlag(DockTabSwitchKeys.FunctionKeys))
        {
            foreach (var key in FunctionKeys)
            {
                gestures.Add(new KeyGesture(key, Modifiers));
            }
        }

        return new DockTabSwitchGestureMap(gestures);
    }
}

/// <summary>
/// The ordered gesture → index map produced by <see cref="DockTabSwitchGestures.BuildMap"/>. The index
/// of a gesture is its position in <see cref="Gestures"/>. Matching is modifier-exact via
/// <see cref="KeyGesture.Matches(KeyEventArgs)"/>, so an <c>Alt</c>-only set does not fire on
/// <c>Alt+Shift</c>.
/// </summary>
public sealed class DockTabSwitchGestureMap
{
    internal DockTabSwitchGestureMap(IReadOnlyList<KeyGesture> gestures)
    {
        Gestures = gestures;
    }

    /// <summary>The ordered gestures; a gesture's index is its position in this list.</summary>
    public IReadOnlyList<KeyGesture> Gestures { get; }

    /// <summary>The number of mapped gestures.</summary>
    public int Count => Gestures.Count;

    /// <summary>
    /// Returns the zero-based index of the first gesture that exactly matches <paramref name="e"/>, or
    /// <c>-1</c> if none match. Modifier matching is exact.
    /// </summary>
    public bool TryGetIndex(KeyEventArgs e, out int index)
    {
        for (var i = 0; i < Gestures.Count; i++)
        {
            if (Gestures[i].Matches(e))
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }
}
