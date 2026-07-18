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
}
