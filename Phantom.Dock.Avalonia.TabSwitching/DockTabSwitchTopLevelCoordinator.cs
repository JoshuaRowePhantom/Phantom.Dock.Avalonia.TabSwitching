using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Phantom.Dock.Avalonia.TabSwitching;

/// <summary>
/// Per-<see cref="TopLevel"/> router for top-level-sourced tab-switch chords (#1332). Replaces the old
/// per-controller effective-visibility gate (<see cref="DockTabSwitchController"/>) with focus / most-
/// recently-focused-live-dock-region routing.
/// </summary>
/// <remarks>
/// One coordinator exists per <see cref="TopLevel"/> (kept in a <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// so it dies with the window). It owns the single tunnel <c>KeyDown</c>/<c>KeyUp</c> pair on the
/// <see cref="TopLevel"/> and a MRU-ordered list of registered controllers held by
/// <see cref="System.WeakReference{T}"/> (index 0 = most-recently-focused). On each chord it:
/// <list type="number">
/// <item>evicts dead weak refs and promotes any controller whose Dock focus is currently inside its
/// <c>DockControl.Layout</c> to the MRU head;</item>
/// <item>updates held-modifier / badge state on every live registered controller;</item>
/// <item>dispatches activation to exactly one controller — the focused (MRU-ordered) matching region, or
/// the MRU-head matching region when none is focused; a region whose host is not effectively visible is
/// never a target (#1124), so a chord over a hidden dock is a no-op.</item>
/// </list>
/// A re-templated-away region is unregistered on <c>Detach</c>/visual-tree-detach and, as a backstop, is
/// never the focused or MRU-head live region — so it can no longer steal and no-op the chord.
/// </remarks>
internal sealed class DockTabSwitchTopLevelCoordinator
{
    private static readonly ConditionalWeakTable<TopLevel, DockTabSwitchTopLevelCoordinator> Coordinators = new();

    private readonly TopLevel _topLevel;

    // MRU-first: index 0 is the most-recently-focused live controller.
    private readonly List<System.WeakReference<DockTabSwitchController>> _controllers = new();

    private bool _handlersInstalled;

    private DockTabSwitchTopLevelCoordinator(TopLevel topLevel)
    {
        _topLevel = topLevel;
    }

    /// <summary>Registers <paramref name="controller"/> as a routing candidate for <paramref name="topLevel"/>.</summary>
    public static void Register(TopLevel topLevel, DockTabSwitchController controller)
    {
        var coordinator = Coordinators.GetValue(topLevel, static t => new DockTabSwitchTopLevelCoordinator(t));
        coordinator.Add(controller);
    }

    /// <summary>Removes <paramref name="controller"/> from <paramref name="topLevel"/>'s routing candidates.</summary>
    public static void Unregister(TopLevel topLevel, DockTabSwitchController controller)
    {
        if (Coordinators.TryGetValue(topLevel, out var coordinator))
        {
            coordinator.Remove(controller);
        }
    }

    private void Add(DockTabSwitchController controller)
    {
        PruneDead();

        if (IndexOf(controller) < 0)
        {
            // Append at the tail (least-recent) — focus promotion moves it to the head.
            _controllers.Add(new System.WeakReference<DockTabSwitchController>(controller));
        }

        EnsureHandlers();
    }

    private void Remove(DockTabSwitchController controller)
    {
        for (var i = _controllers.Count - 1; i >= 0; i--)
        {
            if (!_controllers[i].TryGetTarget(out var target) || ReferenceEquals(target, controller))
            {
                _controllers.RemoveAt(i);
            }
        }

        if (_controllers.Count == 0)
        {
            RemoveHandlers();
        }
    }

    private int IndexOf(DockTabSwitchController controller)
    {
        for (var i = 0; i < _controllers.Count; i++)
        {
            if (_controllers[i].TryGetTarget(out var target) && ReferenceEquals(target, controller))
            {
                return i;
            }
        }

        return -1;
    }

    private void EnsureHandlers()
    {
        if (_handlersInstalled)
        {
            return;
        }

        // Tunnel so the chord is seen before a focused editor/child swallows it (#1124/#1332).
        _topLevel.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _topLevel.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        _handlersInstalled = true;
    }

    private void RemoveHandlers()
    {
        if (!_handlersInstalled)
        {
            return;
        }

        _topLevel.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        _topLevel.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
        _handlersInstalled = false;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var live = SnapshotLive();

        // Modifier / badge state tracks across every registered region.
        foreach (var controller in live)
        {
            controller.UpdateModifierState(e.Key, pressed: true);
        }

        PickTarget(e)?.ActivateFromTopLevel(e);
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        foreach (var controller in SnapshotLive())
        {
            controller.UpdateModifierState(e.Key, pressed: false);
        }
    }

    /// <summary>
    /// Materializes the live controllers in MRU order, evicting dead weak refs and promoting any region
    /// that currently holds Dock focus to the MRU head. Reordering is done on a snapshot to avoid any
    /// mutate-while-iterating hazard when several nested regions report focus.
    /// </summary>
    private List<DockTabSwitchController> SnapshotLive()
    {
        PruneDead();

        var live = new List<DockTabSwitchController>(_controllers.Count);
        foreach (var weak in _controllers)
        {
            if (weak.TryGetTarget(out var controller))
            {
                live.Add(controller);
            }
        }

        var focused = new List<DockTabSwitchController>();
        foreach (var controller in live)
        {
            if (controller.IsDockFocusInside())
            {
                focused.Add(controller);
            }
        }

        // Move focused regions to the front, preserving their relative order (last processed ends up first).
        for (var i = focused.Count - 1; i >= 0; i--)
        {
            MoveToFront(focused[i]);
        }

        return live;
    }

    private void MoveToFront(DockTabSwitchController controller)
    {
        var index = IndexOf(controller);
        if (index <= 0)
        {
            return;
        }

        var weak = _controllers[index];
        _controllers.RemoveAt(index);
        _controllers.Insert(0, weak);
    }

    private DockTabSwitchController? PickTarget(KeyEventArgs e)
    {
        DockTabSwitchController? firstMatch = null;

        // _controllers is MRU-ordered; the first focused-and-matching region wins, else the MRU-head
        // matching region. A region whose host is not effectively visible (collapsed pane, hidden
        // ancestor) is never a target — the chord is a no-op rather than switching a hidden dock (#1124).
        foreach (var weak in _controllers)
        {
            if (!weak.TryGetTarget(out var controller)
                || !controller.IsEffectivelyVisible
                || !controller.MatchesActivationChord(e))
            {
                continue;
            }

            firstMatch ??= controller;

            if (controller.IsDockFocusInside())
            {
                return controller;
            }
        }

        return firstMatch;
    }

    private void PruneDead()
    {
        for (var i = _controllers.Count - 1; i >= 0; i--)
        {
            if (!_controllers[i].TryGetTarget(out _))
            {
                _controllers.RemoveAt(i);
            }
        }
    }
}
