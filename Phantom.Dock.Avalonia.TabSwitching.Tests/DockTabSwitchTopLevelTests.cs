using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using DockDocument = global::Dock.Model.Avalonia.Controls.Document;
using DockDocumentDock = global::Dock.Model.Avalonia.Controls.DocumentDock;
using DockRootDock = global::Dock.Model.Avalonia.Controls.RootDock;
using Xunit;

namespace Phantom.Dock.Avalonia.TabSwitching.Tests;

/// <summary>
/// Covers the #1124 top-level-sourcing + effective-visibility-gate mechanism: a DockControl that
/// opts into <see cref="DockTabSwitch.InstallOnTopLevelProperty"/> installs its tunnel handlers on
/// <see cref="TopLevel.GetTopLevel"/> so gestures fire regardless of keyboard focus, and only handles
/// while the target DockControl is <see cref="Visual.IsEffectivelyVisible"/>.
/// </summary>
public sealed class DockTabSwitchTopLevelTests
{
    private sealed record ActivationRecord(IDock Strip, IDockable Dockable);

    private sealed class RecordingFactory : global::Dock.Model.Avalonia.Factory
    {
        public IDockable? LastActive { get; private set; }

        public int ActivateCallCount { get; private set; }

        public ActivationRecord? LastFocused { get; private set; }

        public override void SetActiveDockable(IDockable dockable)
        {
            LastActive = dockable;
            ActivateCallCount++;
        }

        public override void SetFocusedDockable(IDock dock, IDockable? dockable) =>
            LastFocused = dockable is null ? null : new ActivationRecord(dock, dockable);
    }

    private static (DockControl Dock, RecordingFactory Factory, IDockable[] Documents, IDock Strip)
        BuildDock(int documentCount, string idPrefix = "d")
    {
        var factory = new RecordingFactory();
        var documents = new IDockable[documentCount];
        var strip = new DockDocumentDock { Factory = factory };
        var visible = new AvaloniaList<IDockable>();
        for (var i = 0; i < documentCount; i++)
        {
            var document = new DockDocument
            {
                Id = idPrefix + i,
                Title = idPrefix + i,
                Owner = strip,
                Factory = factory,
            };
            documents[i] = document;
            visible.Add(document);
        }
        strip.VisibleDockables = visible;

        var root = new DockRootDock
        {
            Factory = factory,
            VisibleDockables = new AvaloniaList<IDockable> { strip },
        };
        strip.Owner = root;

        var dock = new DockControl { Factory = factory, Layout = root };
        return (dock, factory, documents, strip);
    }

    private static KeyEventArgs KeyDown(Key key, KeyModifiers modifiers, object source) => new()
    {
        RoutedEvent = InputElement.KeyDownEvent,
        Key = key,
        KeyModifiers = modifiers,
        Source = source,
    };

    private static KeyEventArgs KeyUp(Key key, KeyModifiers modifiers, object source) => new()
    {
        RoutedEvent = InputElement.KeyUpEvent,
        Key = key,
        KeyModifiers = modifiers,
        Source = source,
    };

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TopLevel_GestureWhenFocusOutsideTarget_ActivatesIndexedDockable()
    {
        var (dock, factory, documents, strip) = BuildDock(3);
        DockTabSwitch.SetInstallOnTopLevel(dock, true);
        DockTabSwitch.SetEnabled(dock, true);

        // A sibling non-dock control simulates focus outside the DockControl.
        var focusHolder = new Border { Focusable = true };
        var window = new Window
        {
            Content = new StackPanel { Children = { focusHolder, dock } },
        };
        window.Show();
        Pump();

        // Route the event from a source outside the DockControl — the tunnel handler on the
        // TopLevel fires first.
        window.RaiseEvent(KeyDown(Key.D1, KeyModifiers.Alt, focusHolder));

        Assert.Same(documents[0], factory.LastActive);
        Assert.NotNull(factory.LastFocused);
        Assert.Same(strip, factory.LastFocused!.Strip);
    }

    [AvaloniaFact]
    public void TopLevel_TargetTransitivelyInvisible_DoesNotHandleGesture()
    {
        var (dock, factory, _, _) = BuildDock(3);
        DockTabSwitch.SetInstallOnTopLevel(dock, true);
        DockTabSwitch.SetEnabled(dock, true);

        var container = new StackPanel { Children = { dock } };
        var window = new Window { Content = container };
        window.Show();
        Pump();

        // Ancestor collapsed → the target's IsEffectivelyVisible is false, event unhandled.
        container.IsVisible = false;
        Pump();
        Assert.False(dock.IsEffectivelyVisible);

        var args = KeyDown(Key.D1, KeyModifiers.Alt, window);
        window.RaiseEvent(args);

        Assert.False(args.Handled);
        Assert.Null(factory.LastActive);
    }

    [AvaloniaFact]
    public void TopLevel_TargetOwnIsVisibleButAncestorHidden_DoesNotActivate()
    {
        var (dock, factory, _, _) = BuildDock(3);
        DockTabSwitch.SetInstallOnTopLevel(dock, true);
        DockTabSwitch.SetEnabled(dock, true);

        var container = new StackPanel { Children = { dock } };
        var window = new Window { Content = container };
        window.Show();
        Pump();

        // The DockControl's own IsVisible stays true; the ancestor is the one hidden.
        container.IsVisible = false;
        Pump();
        Assert.True(dock.IsVisible);
        Assert.False(dock.IsEffectivelyVisible);

        window.RaiseEvent(KeyDown(Key.D1, KeyModifiers.Alt, window));

        Assert.Null(factory.LastActive);
    }

    [AvaloniaFact]
    public void TopLevel_TwoDockControlsOneTopLevel_OnlyEffectivelyVisibleOneHandles()
    {
        // Two opted-in DockControls with the SAME chord. Only the effectively-visible one handles.
        var (a, factoryA, documentsA, _) = BuildDock(3, "a");
        var (b, factoryB, _, _) = BuildDock(3, "b");
        DockTabSwitch.SetInstallOnTopLevel(a, true);
        DockTabSwitch.SetInstallOnTopLevel(b, true);
        DockTabSwitch.SetEnabled(a, true);
        DockTabSwitch.SetEnabled(b, true);

        var hidden = new StackPanel { Children = { b }, IsVisible = false };
        var window = new Window
        {
            Content = new StackPanel { Children = { a, hidden } },
        };
        window.Show();
        Pump();

        Assert.True(a.IsEffectivelyVisible);
        Assert.False(b.IsEffectivelyVisible);

        window.RaiseEvent(KeyDown(Key.D2, KeyModifiers.Alt, window));

        Assert.Same(documentsA[1], factoryA.LastActive);
        Assert.Null(factoryB.LastActive);
    }

    [AvaloniaFact]
    public void TopLevel_TwoDockControlsBothVisibleDistinctChords_EachSwitchesOnlyItsOwn()
    {
        var (a, factoryA, documentsA, _) = BuildDock(3, "a");
        var (b, factoryB, documentsB, _) = BuildDock(3, "b");

        DockTabSwitch.SetBindings(a, new DockTabSwitchBindings
        {
            new DockTabSwitchGestures { Modifiers = KeyModifiers.Alt | KeyModifiers.Shift, Keys = DockTabSwitchKeys.Digits },
        });
        DockTabSwitch.SetBindings(b, new DockTabSwitchBindings
        {
            new DockTabSwitchGestures { Modifiers = KeyModifiers.Control | KeyModifiers.Alt, Keys = DockTabSwitchKeys.Digits },
        });
        DockTabSwitch.SetInstallOnTopLevel(a, true);
        DockTabSwitch.SetInstallOnTopLevel(b, true);
        DockTabSwitch.SetEnabled(a, true);
        DockTabSwitch.SetEnabled(b, true);

        var window = new Window
        {
            Content = new StackPanel { Children = { a, b } },
        };
        window.Show();
        Pump();

        // Alt+Shift+2 → only A activates.
        window.RaiseEvent(KeyDown(Key.D2, KeyModifiers.Alt | KeyModifiers.Shift, window));
        Assert.Same(documentsA[1], factoryA.LastActive);
        Assert.Null(factoryB.LastActive);

        // Ctrl+Alt+3 → only B activates.
        window.RaiseEvent(KeyDown(Key.D3, KeyModifiers.Control | KeyModifiers.Alt, window));
        Assert.Same(documentsB[2], factoryB.LastActive);
        // A didn't change:
        Assert.Same(documentsA[1], factoryA.LastActive);
    }

    [AvaloniaFact]
    public void TopLevel_DockControlReparented_RebindsHookToNewTopLevelAndUnbindsOld()
    {
        var (dock, factory, documents, _) = BuildDock(3);
        DockTabSwitch.SetInstallOnTopLevel(dock, true);
        DockTabSwitch.SetEnabled(dock, true);

        var wrapperA = new StackPanel { Children = { dock } };
        var windowA = new Window { Content = wrapperA };
        windowA.Show();
        Pump();

        var controller = DockTabSwitch.GetController(dock)!;
        Assert.Same(windowA, controller.BoundTopLevelForTest);

        // Reparent to a new window.
        wrapperA.Children.Remove(dock);
        Pump();

        var windowB = new Window { Content = dock };
        windowB.Show();
        Pump();

        Assert.Same(windowB, controller.BoundTopLevelForTest);

        // Gesture on the old TopLevel does nothing.
        windowA.RaiseEvent(KeyDown(Key.D1, KeyModifiers.Alt, windowA));
        Assert.Null(factory.LastActive);

        // Gesture on the new TopLevel activates.
        windowB.RaiseEvent(KeyDown(Key.D2, KeyModifiers.Alt, windowB));
        Assert.Same(documents[1], factory.LastActive);
    }

    [AvaloniaFact]
    public void TopLevel_HeldModifierThenDigit_RunsChordEndToEnd()
    {
        var (dock, factory, documents, _) = BuildDock(3);
        DockTabSwitch.SetInstallOnTopLevel(dock, true);
        DockTabSwitch.SetEnabled(dock, true);

        var window = new Window { Content = dock };
        window.Show();
        Pump();

        var controller = DockTabSwitch.GetController(dock)!;
        Assert.False(controller.AreBadgesVisible);

        // Modifier down → badges visible.
        window.RaiseEvent(KeyDown(Key.LeftAlt, KeyModifiers.Alt, window));
        Assert.True(controller.AreBadgesVisible);

        // Digit → activate index 1.
        window.RaiseEvent(KeyDown(Key.D2, KeyModifiers.Alt, window));
        Assert.Same(documents[1], factory.LastActive);

        // Modifier up → badges hidden.
        window.RaiseEvent(KeyUp(Key.LeftAlt, KeyModifiers.None, window));
        Assert.False(controller.AreBadgesVisible);
    }

    [AvaloniaFact]
    public void TopLevel_SingleChord_ActivatesExactlyOnce()
    {
        // With InstallOnTopLevel, the in-control AddHandler must be suppressed so the same physical
        // key produces exactly one SetActiveDockable.
        var (dock, factory, _, _) = BuildDock(3);
        DockTabSwitch.SetInstallOnTopLevel(dock, true);
        DockTabSwitch.SetEnabled(dock, true);

        var window = new Window { Content = dock };
        window.Show();
        Pump();

        window.RaiseEvent(KeyDown(Key.D1, KeyModifiers.Alt, window));

        Assert.Equal(1, factory.ActivateCallCount);
    }

    [AvaloniaFact]
    public void OnKeyUp_TopLevelSourced_ReleasesModifierOnce()
    {
        var (dock, _, _, _) = BuildDock(3);
        DockTabSwitch.SetInstallOnTopLevel(dock, true);
        DockTabSwitch.SetEnabled(dock, true);

        var window = new Window { Content = dock };
        window.Show();
        Pump();

        var controller = DockTabSwitch.GetController(dock)!;

        window.RaiseEvent(KeyDown(Key.LeftAlt, KeyModifiers.Alt, window));
        Assert.Equal(KeyModifiers.Alt, controller.HeldModifiersForTest);

        window.RaiseEvent(KeyUp(Key.LeftAlt, KeyModifiers.None, window));
        Assert.Equal(KeyModifiers.None, controller.HeldModifiersForTest);

        // A second KeyUp is idempotent (still None; no negative-ref).
        window.RaiseEvent(KeyUp(Key.LeftAlt, KeyModifiers.None, window));
        Assert.Equal(KeyModifiers.None, controller.HeldModifiersForTest);
    }

    [AvaloniaFact]
    public void TopLevel_BadgeVisibility_DrivenByExactModifierSet()
    {
        var (dock, _, _, _) = BuildDock(3);
        DockTabSwitch.SetBindings(dock, new DockTabSwitchBindings
        {
            new DockTabSwitchGestures { Modifiers = KeyModifiers.Alt | KeyModifiers.Shift, Keys = DockTabSwitchKeys.Digits },
        });
        DockTabSwitch.SetInstallOnTopLevel(dock, true);
        DockTabSwitch.SetEnabled(dock, true);

        var window = new Window { Content = dock };
        window.Show();
        Pump();

        var controller = DockTabSwitch.GetController(dock)!;

        // Alt alone does not match Alt+Shift ⇒ badges stay hidden.
        window.RaiseEvent(KeyDown(Key.LeftAlt, KeyModifiers.Alt, window));
        Assert.False(controller.AreBadgesVisible);

        // Add Shift ⇒ exact match ⇒ visible.
        window.RaiseEvent(KeyDown(Key.LeftShift, KeyModifiers.Alt | KeyModifiers.Shift, window));
        Assert.True(controller.AreBadgesVisible);

        // Release Shift ⇒ back to Alt-only ⇒ hidden again.
        window.RaiseEvent(KeyUp(Key.LeftShift, KeyModifiers.Alt, window));
        Assert.False(controller.AreBadgesVisible);
    }
}
