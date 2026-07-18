using System;
using Dock.Avalonia.Controls;

namespace Phantom.Dock.Avalonia.TabSwitching;

/// <summary>
/// The per-<c>DockControl</c> manager for the Dock Tab-Switching API. This is the only stateful
/// object in the design (§5) and is created purely from the <see cref="DockTabSwitch"/> attached
/// properties — it never touches a view-model.
/// </summary>
/// <remarks>
/// This scaffolding skeleton wires the attach/detach lifecycle and holds the target
/// <see cref="DockControl"/>. Later commits of the #1073 epic add the tunnel <c>KeyDown</c> handler,
/// subscribe to strip <c>ContainerPrepared</c>/<c>ContainerClearing</c>, own the shared
/// <c>DockTabOrder</c> service, and own the default <c>IndexTheme</c> fallback.
/// </remarks>
public sealed class DockTabSwitchController : IDisposable
{
    private bool _attached;
    private bool _disposed;

    public DockTabSwitchController(DockControl dockControl)
    {
        DockControl = dockControl ?? throw new ArgumentNullException(nameof(dockControl));
    }

    /// <summary>The <c>DockControl</c> this controller manages.</summary>
    public DockControl DockControl { get; }

    /// <summary>Whether <see cref="Attach"/> has run without a subsequent <see cref="Detach"/>.</summary>
    public bool IsAttached => _attached;

    /// <summary>
    /// Placeholder flag consumed by the badge template: <c>true</c> while an activation modifier is
    /// held. Later commits drive it from the controller's <c>KeyDown</c>/<c>KeyUp</c> tracking.
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

        // Later commits: add the tunnel KeyDown handler (mirroring DockControl's own selector),
        // subscribe to each strip's ContainerPrepared/ContainerClearing, and initialise the shared
        // DockTabOrder service plus the default IndexTheme fallback.
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
        AreBadgesVisible = false;

        // Later commits: remove the handlers and subscriptions installed in Attach().
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
}
