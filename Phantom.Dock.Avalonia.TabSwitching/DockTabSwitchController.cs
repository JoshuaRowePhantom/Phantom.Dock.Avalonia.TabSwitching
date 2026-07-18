using System;
using System.Collections.Generic;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dock.Avalonia.Controls;
using Dock.Model.Core;

namespace Phantom.Dock.Avalonia.TabSwitching;

/// <summary>
/// The per-<c>DockControl</c> manager for the Dock Tab-Switching API. This is the only stateful
/// object in the design (§5) and is created purely from the <see cref="DockTabSwitch"/> attached
/// properties — it never touches a view-model.
/// </summary>
/// <remarks>
/// This commit wires the tunnel <c>KeyDown</c> activation pipeline (design §4.1/§4.5): it installs a
/// tunnel <c>KeyDown</c>/<c>KeyUp</c> handler on the <see cref="DockControl"/> (mirroring Dock's own
/// selector), matches each configured <see cref="DockTabSwitchGestures"/> set, resolves the indexed
/// dockable and activates it through Dock's factory, and tracks the bare activation modifier to drive
/// <see cref="AreBadgesVisible"/>. The shared, scope-aware <c>DockTabOrder</c> service arrives in a
/// later commit of the #1073 epic; here a single default scope root (the whole layout) is used.
/// </remarks>
public sealed class DockTabSwitchController : IDisposable
{
    private static readonly DockTabSwitchGestures[] DefaultBindings = { new() };

    private bool _attached;
    private bool _disposed;
    private KeyModifiers _heldModifiers;

    public DockTabSwitchController(DockControl dockControl)
    {
        DockControl = dockControl ?? throw new ArgumentNullException(nameof(dockControl));
    }

    /// <summary>The <c>DockControl</c> this controller manages.</summary>
    public DockControl DockControl { get; }

    /// <summary>Whether <see cref="Attach"/> has run without a subsequent <see cref="Detach"/>.</summary>
    public bool IsAttached => _attached;

    /// <summary>
    /// Consumed by the badge template: <c>true</c> while an activation modifier is held. Driven by the
    /// controller's <c>KeyDown</c>/<c>KeyUp</c> modifier tracking.
    /// </summary>
    public bool AreBadgesVisible { get; internal set; }

    /// <summary>
    /// Installs the controller's behavior on the <see cref="DockControl"/>. Idempotent: calling it
    /// while already attached is a no-op.
    /// </summary>
    public void Attach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attached)
        {
            return;
        }

        _attached = true;

        // Tunnel so the DockControl sees the gesture before a focused editor/child swallows it — the
        // same routing strategy Dock uses for its own document selector.
        DockControl.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        DockControl.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Removes everything <see cref="Attach"/> installed. Idempotent: calling it while not attached
    /// is a no-op.
    /// </summary>
    public void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        _heldModifiers = KeyModifiers.None;
        AreBadgesVisible = false;

        DockControl.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        DockControl.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Detach();
        _disposed = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e) => ProcessKeyDown(e);

    private void OnKeyUp(object? sender, KeyEventArgs e) => ProcessKeyUp(e);

    /// <summary>
    /// Core tunnel <c>KeyDown</c> logic (exposed for tests). Updates the held-modifier state, then tries
    /// each configured gesture set; on the first exact match it activates the indexed dockable and marks
    /// the event handled. An in-modifier but out-of-range index is a no-op and leaves
    /// <see cref="RoutedEventArgs.Handled"/> unchanged so the key still reaches children.
    /// </summary>
    internal void ProcessKeyDown(KeyEventArgs e)
    {
        UpdateModifierState(e.Key, pressed: true);

        foreach (var binding in GetEffectiveBindings())
        {
            var map = binding.BuildMap();
            if (map.TryGetIndex(e, out var index))
            {
                if (Activate(binding, index))
                {
                    e.Handled = true;
                }

                // A gesture matched (whether or not the index was in range); stop looking. Other
                // bindings use different modifiers and could not also match this event.
                return;
            }
        }
    }

    /// <summary>Core tunnel <c>KeyUp</c> logic (exposed for tests): releases the held modifier.</summary>
    internal void ProcessKeyUp(KeyEventArgs e) => UpdateModifierState(e.Key, pressed: false);

    private bool Activate(DockTabSwitchGestures binding, int index)
    {
        var order = ComputeOrder(binding);
        if (index < 0 || index >= order.Count)
        {
            return false;
        }

        var (strip, dockable) = order[index];
        var factory = dockable.Factory;
        factory?.SetActiveDockable(dockable);
        factory?.SetFocusedDockable(strip, dockable);
        return true;
    }

    /// <summary>
    /// Computes the ordered <c>(strip, dockable)</c> activation list from a single default scope root —
    /// the whole layout — by walking <see cref="IDock.VisibleDockables"/> in visual order and collecting
    /// each leaf dockable together with its owning strip. The shared, scope-aware <c>DockTabOrder</c>
    /// service (which honours each binding's <c>Scope</c> and the inherited <c>IsSwitchable</c> opt-out)
    /// replaces this in a later commit of the #1073 epic.
    /// </summary>
    private IReadOnlyList<(IDock Strip, IDockable Dockable)> ComputeOrder(DockTabSwitchGestures binding)
    {
        _ = binding;
        var result = new List<(IDock, IDockable)>();
        if (DockControl.Layout is { } root)
        {
            Collect(root, result);
        }

        return result;
    }

    private static void Collect(IDock dock, List<(IDock, IDockable)> acc)
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
                    Collect(childDock, acc);
                    break;
                case not null:
                    acc.Add((dock, dockable));
                    break;
            }
        }
    }

    private IReadOnlyList<DockTabSwitchGestures> GetEffectiveBindings()
    {
        var bindings = DockTabSwitch.GetBindings(DockControl);
        return bindings is { Count: > 0 } ? bindings : DefaultBindings;
    }

    private void UpdateModifierState(Key key, bool pressed)
    {
        var flag = ModifierFor(key);
        if (flag == KeyModifiers.None)
        {
            return;
        }

        if (pressed)
        {
            _heldModifiers |= flag;
        }
        else
        {
            _heldModifiers &= ~flag;
        }

        RefreshBadgeVisibility();
    }

    private void RefreshBadgeVisibility()
    {
        var visible = false;
        foreach (var binding in GetEffectiveBindings())
        {
            var required = binding.Modifiers;
            if (required != KeyModifiers.None && (_heldModifiers & required) == required)
            {
                visible = true;
                break;
            }
        }

        AreBadgesVisible = visible;
    }

    private static KeyModifiers ModifierFor(Key key) => key switch
    {
        Key.LeftAlt or Key.RightAlt => KeyModifiers.Alt,
        Key.LeftCtrl or Key.RightCtrl => KeyModifiers.Control,
        Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
        Key.LWin or Key.RWin => KeyModifiers.Meta,
        _ => KeyModifiers.None,
    };
}
