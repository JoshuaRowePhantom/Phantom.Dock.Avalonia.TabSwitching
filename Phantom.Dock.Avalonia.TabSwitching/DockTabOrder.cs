using System;
using System.Collections.Generic;
using Dock.Model.Controls;
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
    /// <see cref="IDock.VisibleDockables"/> in visual order and collecting each <see cref="IDocument"/>
    /// leaf together with its owning strip. Nested docks (splits) are recursed in order, so the result
    /// mirrors the Dock structure exactly and is recomputed live on every call — a reorder or close is
    /// reflected with no flat cache to invalidate. Numbering is restricted to <see cref="IDocument"/>
    /// leaves — the only dockables that render a badged <c>DocumentTabStripItem</c> — so structural
    /// siblings such as an <c>IProportionalDockSplitter</c> never consume an ordinal (#1342).
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

    /// <summary>
    /// Recursively walks <paramref name="dock"/>'s <see cref="IDock.VisibleDockables"/> in visual order,
    /// recursing into nested docks and appending only <see cref="IDocument"/> leaves — the badged type —
    /// to <paramref name="acc"/>. Non-document dockables (splitters, tools, any future kind) are ignored
    /// so they never consume an ordinal (#1342). The <paramref name="isSwitchable"/> opt-out is honoured.
    /// </summary>
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

                // #1342 whitelist: only IDocument leaves render a switchable, badged DocumentTabStripItem,
                // so only they may consume an ordinal. Any other non-IDock dockable — a
                // ProportionalDockSplitter interleaved between split regions, an ITool, or any future
                // dockable kind Dock adds to VisibleDockables — has no badge, and numbering it would
                // reintroduce the per-split gap and dead hotkey. Positively selecting IDocument is robust
                // where a splitter-specific blacklist was not.
                case IDocument when isSwitchable is null || isSwitchable(dock):
                    acc.Add(new DockTabEntry(dock, dockable));
                    break;
            }
        }
    }
}
