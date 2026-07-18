using Dock.Model.Controls;
using Dock.Model.Core;

namespace Phantom.Dock.Avalonia.TabSwitching;

/// <summary>
/// Resolves each gesture set's ordering root from its own <see cref="DockTabSwitchScope"/> (design §4.2).
/// Scope is a property of the gesture set — not a global switch — so a single <c>DockControl</c> can
/// carry several bindings that resolve independent roots simultaneously
/// (e.g. <c>Alt+digit → AllSwitchable</c> alongside <c>Ctrl+Shift+digit → FocusedDockOnly</c>).
/// </summary>
internal static class DockTabScopeResolver
{
    /// <summary>
    /// Resolves the ordering root for <paramref name="scope"/> against <paramref name="layout"/>:
    /// the whole layout for <see cref="DockTabSwitchScope.AllSwitchable"/>, or the single focused dock
    /// (via Dock's own focus API) for <see cref="DockTabSwitchScope.FocusedDockOnly"/>.
    /// </summary>
    public static IDockable? ResolveScopeRoot(IDock? layout, DockTabSwitchScope scope)
    {
        if (layout is null)
        {
            return null;
        }

        return scope == DockTabSwitchScope.FocusedDockOnly
            ? ResolveFocusedDock(layout)
            : layout;
    }

    /// <summary>
    /// Resolves the focused dock through Dock's own focus API — never Avalonia's visual
    /// <c>FocusManager</c>. Reads the focusable root's <see cref="IDock.FocusedDockable"/> (the field
    /// <c>IFactory.SetFocusedDockable</c> maintains) and walks the <see cref="IDockable.Owner"/> chain to
    /// the owning <see cref="IDock"/> (the tab strip) whose <see cref="IDock.VisibleDockables"/> are then
    /// numbered.
    /// </summary>
    public static IDock? ResolveFocusedDock(IDock layout)
    {
        var root = FindFocusableRoot(layout) ?? layout;
        var focused = root.FocusedDockable;
        if (focused is null)
        {
            return null;
        }

        // If focus landed on a strip directly, number that strip; otherwise walk up to the owning dock.
        if (focused is IDock focusedDock && HasLeafDockables(focusedDock))
        {
            return focusedDock;
        }

        return OwningDock(focused);
    }

    private static IRootDock? FindFocusableRoot(IDock dock)
    {
        if (dock is IRootDock { IsFocusableRoot: true } focusableRoot)
        {
            return focusableRoot;
        }

        var visible = dock.VisibleDockables;
        if (visible is null)
        {
            return null;
        }

        foreach (var dockable in visible)
        {
            if (dockable is IDock childDock && FindFocusableRoot(childDock) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static IDock? OwningDock(IDockable dockable)
    {
        var owner = dockable.Owner;
        while (owner is not null and not IDock)
        {
            owner = owner.Owner;
        }

        return owner as IDock;
    }

    private static bool HasLeafDockables(IDock dock)
    {
        var visible = dock.VisibleDockables;
        if (visible is null)
        {
            return false;
        }

        foreach (var dockable in visible)
        {
            if (dockable is not IDock)
            {
                return true;
            }
        }

        return false;
    }
}
