using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Dock.Avalonia.Controls;
using Xunit;

namespace Phantom.Dock.Avalonia.TabSwitching.Tests;

/// <summary>
/// Covers the <see cref="DockTabSwitch"/> attached properties and the
/// <see cref="DockTabSwitchController"/> attach/detach lifecycle skeleton (commit 2 of the #1073 epic).
/// </summary>
public sealed class DockTabSwitchTests
{
    [AvaloniaFact]
    public void Enabled_SetTrue_InstallsController()
    {
        var dock = new DockControl();

        DockTabSwitch.SetEnabled(dock, true);

        var controller = DockTabSwitch.GetController(dock);
        Assert.NotNull(controller);
        Assert.True(controller!.IsAttached);
        Assert.Same(dock, controller.DockControl);
    }

    [AvaloniaFact]
    public void Enabled_SetFalse_DetachesAndDisposesController()
    {
        var dock = new DockControl();

        DockTabSwitch.SetEnabled(dock, true);
        var controller = DockTabSwitch.GetController(dock);
        Assert.NotNull(controller);

        DockTabSwitch.SetEnabled(dock, false);

        // The controller is removed from the host and detached.
        Assert.Null(DockTabSwitch.GetController(dock));
        Assert.False(controller!.IsAttached);

        // Disposed controllers refuse to re-attach.
        Assert.Throws<System.ObjectDisposedException>(() => controller.Attach());
    }

    [AvaloniaFact]
    public void Enabled_ToggledRepeatedly_IsIdempotent()
    {
        var dock = new DockControl();

        // Repeated true never double-installs.
        DockTabSwitch.SetEnabled(dock, true);
        var first = DockTabSwitch.GetController(dock);
        DockTabSwitch.SetEnabled(dock, true);
        Assert.Same(first, DockTabSwitch.GetController(dock));

        // Repeated false never throws and leaves no controller.
        DockTabSwitch.SetEnabled(dock, false);
        DockTabSwitch.SetEnabled(dock, false);
        Assert.Null(DockTabSwitch.GetController(dock));

        // A fresh enable installs a brand-new controller.
        DockTabSwitch.SetEnabled(dock, true);
        var second = DockTabSwitch.GetController(dock);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.True(second!.IsAttached);
    }

    [AvaloniaFact]
    public void Enabled_OnNonDockControl_DoesNotInstallController()
    {
        var control = new Border();

        DockTabSwitch.SetEnabled(control, true);

        Assert.Null(DockTabSwitch.GetController(control));
    }

    [AvaloniaFact]
    public void IsSwitchable_Default_IsTrue()
    {
        var control = new Border();

        Assert.True(DockTabSwitch.GetIsSwitchable(control));
    }

    [AvaloniaFact]
    public void IsSwitchable_SetOnAncestor_InheritsToDescendantStrip()
    {
        var descendant = new Border();
        var ancestor = new StackPanel { Children = { descendant } };
        var window = new Window { Content = ancestor };
        window.Show();

        // Opting the ancestor out cascades to the descendant (inherited attached property).
        DockTabSwitch.SetIsSwitchable(ancestor, false);
        Assert.False(DockTabSwitch.GetIsSwitchable(ancestor));
        Assert.False(DockTabSwitch.GetIsSwitchable(descendant));

        // The descendant can override back to opted-in.
        DockTabSwitch.SetIsSwitchable(descendant, true);
        Assert.True(DockTabSwitch.GetIsSwitchable(descendant));
    }
}
