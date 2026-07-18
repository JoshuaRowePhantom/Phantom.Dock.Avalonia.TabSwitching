using System;
using System.Collections.Generic;
using Dock.Model.Core;

namespace Phantom.Dock.Avalonia.TabSwitching;

/// <summary>
/// A single ordered position in the shared tab order: a leaf <see cref="IDockable"/> together with the
/// <see cref="IDock"/> strip that owns it. Used by both label display and activation so the two can
/// never resolve a different dockable for the same index (design §4.5 / #1067).
/// </summary>
public readonly record struct DockTabEntry(IDock Strip, IDockable Dockable);

/// <summary>
/// The single ordering <b>source of truth</b> for the Dock Tab-Switching API (design §4.2/§4.5).
/// <see cref="Compute"/> walks <see cref="IDock.VisibleDockables"/> in visual order from a scope root
/// and yields the ordered <see cref="DockTabEntry"/> list. The same instance is shared by label
/// assignment and activation, so display and activation are always derived from the identical order —
/// the explicit fix for the #1067 divergence, where badges were computed from an internal flat
/// projection rather than the Dock structure.
/// </summary>
public sealed class DockTabOrder
{
    /// <summary>
    /// Computes the ordered <c>(strip, dockable)</c> list for <paramref name="scopeRoot"/> by walking
    /// <see cref="IDock.VisibleDockables"/> in visual order and collecting each leaf dockable together
    /// with its owning strip. Nested docks (splits) are recursed in order, so the result mirrors the
    /// Dock structure exactly and is recomputed live on every call — a reorder or close is reflected
    /// with no flat cache to invalidate.
    /// </summary>
    /// <param name="scopeRoot">
    /// The ordering root resolved from a gesture set's <see cref="DockTabSwitchScope"/>
    /// (see <see cref="DockTabScopeResolver.ResolveScopeRoot"/>): the whole layout for
    /// <see cref="DockTabSwitchScope.AllSwitchable"/>, or a single focused dock for
    /// <see cref="DockTabSwitchScope.FocusedDockOnly"/>. A <c>null</c> root yields an empty order.
    /// </param>
    /// <param name="isSwitchable">
    /// Optional per-strip opt-out predicate (the inherited <c>IsSwitchable</c> attached property, §4.2).
    /// When supplied and it returns <c>false</c> for a strip, that strip's dockables are excluded from
    /// the numbering. <c>null</c> ⇒ every strip participates.
    /// </param>
    public IReadOnlyList<DockTabEntry> Compute(IDockable? scopeRoot, Func<IDock, bool>? isSwitchable = null)
    {
        var result = new List<DockTabEntry>();
        if (scopeRoot is IDock dock)
        {
            Collect(dock, result, isSwitchable);
        }

        return result;
    }

    private static void Collect(IDock dock, List<DockTabEntry> acc, Func<IDock, bool>? isSwitchable)
    {
        var visible = dock.VisibleDockables;
        if (visible is null)
        {
            return;
        }

        foreach (var dockable in visible)
        {
            switch (dockable)
            {
                case IDock childDock:
                    Collect(childDock, acc, isSwitchable);
                    break;
                case not null when isSwitchable is null || isSwitchable(dock):
                    acc.Add(new DockTabEntry(dock, dockable));
                    break;
            }
        }
    }
}
