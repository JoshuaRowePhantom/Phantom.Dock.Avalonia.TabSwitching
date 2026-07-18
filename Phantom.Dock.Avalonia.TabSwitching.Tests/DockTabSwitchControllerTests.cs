using Avalonia.Collections;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using DockDocument = Dock.Model.Avalonia.Controls.Document;
using DockDocumentDock = Dock.Model.Avalonia.Controls.DocumentDock;
using DockRootDock = Dock.Model.Avalonia.Controls.RootDock;
using Xunit;

namespace Phantom.Dock.Avalonia.TabSwitching.Tests;

/// <summary>
/// Covers the <see cref="DockTabSwitchController"/> tunnel <c>KeyDown</c> activation pipeline and
/// modifier tracking (commit 3 of the #1073 epic, design §4.1/§4.5): a matched gesture activates the
/// indexed dockable through Dock's factory and marks the event handled, an out-of-range index is a
/// no-op, a bare key is not swallowed, and holding/releasing the activation modifier flips
/// <see cref="DockTabSwitchController.AreBadgesVisible"/>.
/// </summary>
public sealed class DockTabSwitchControllerTests
{
    private sealed record ActivationRecord(IDock Strip, IDockable Dockable);

    /// <summary>
    /// A Dock factory that records <see cref="SetActiveDockable"/>/<see cref="SetFocusedDockable"/>
    /// calls without performing any real docking work.
    /// </summary>
    private sealed class RecordingFactory : global::Dock.Model.Avalonia.Factory
    {
        public IDockable? LastActive { get; private set; }

        public ActivationRecord? LastFocused { get; private set; }

        public override void SetActiveDockable(IDockable dockable) => LastActive = dockable;

        public override void SetFocusedDockable(IDock dock, IDockable? dockable) =>
            LastFocused = dockable is null ? null : new ActivationRecord(dock, dockable);
    }

    private static (DockControl Dock, RecordingFactory Factory, IDockable[] Documents, IDock Strip)
        BuildDock(int documentCount)
    {
        var factory = new RecordingFactory();
        var documents = new IDockable[documentCount];
        var strip = new DockDocumentDock();

        var visible = new AvaloniaList<IDockable>();
        for (var i = 0; i < documentCount; i++)
        {
            var document = new DockDocument { Id = i.ToString(), Title = i.ToString() };
            document.Owner = strip;
            document.Factory = factory;
            documents[i] = document;
            visible.Add(document);
        }

        strip.VisibleDockables = visible;

        var root = new DockRootDock { VisibleDockables = new AvaloniaList<IDockable> { strip } };
        strip.Owner = root;

        var dock = new DockControl { Layout = root };
        DockTabSwitch.SetEnabled(dock, true);

        return (dock, factory, documents, strip);
    }

    private static KeyEventArgs KeyDown(Key key, KeyModifiers modifiers, object source) => new()
    {
        RoutedEvent = InputElement.KeyDownEvent,
        Key = key,
        KeyModifiers = modifiers,
        Source = source,
    };

    [AvaloniaFact]
    public void OnKeyDown_MatchedGesture_ActivatesIndexedDockable()
    {
        var (dock, factory, documents, strip) = BuildDock(3);

        // Alt+1 is the default first gesture; raising it through the wired tunnel handler must activate
        // the first document and swallow the event.
        var args = KeyDown(Key.D1, KeyModifiers.Alt, dock);
        dock.RaiseEvent(args);

        Assert.True(args.Handled);
        Assert.Same(documents[0], factory.LastActive);
        Assert.NotNull(factory.LastFocused);
        Assert.Same(documents[0], factory.LastFocused!.Dockable);
        Assert.Same(strip, factory.LastFocused.Strip);
    }

    [AvaloniaFact]
    public void OnKeyDown_MatchedLaterGesture_ActivatesCorrectDockable()
    {
        var (dock, factory, documents, _) = BuildDock(3);

        // Alt+3 → index 2.
        var args = KeyDown(Key.D3, KeyModifiers.Alt, dock);
        dock.RaiseEvent(args);

        Assert.True(args.Handled);
        Assert.Same(documents[2], factory.LastActive);
    }

    [AvaloniaFact]
    public void OnKeyDown_OutOfRangeIndex_IsNoOpAndLeavesUnhandled()
    {
        var (dock, factory, _, _) = BuildDock(2);
        var controller = DockTabSwitch.GetController(dock)!;

        // Alt+3 matches the gesture set (in-modifier) but index 2 is out of range for 2 documents.
        var args = KeyDown(Key.D3, KeyModifiers.Alt, dock);
        controller.ProcessKeyDown(args);

        Assert.False(args.Handled);
        Assert.Null(factory.LastActive);
        Assert.Null(factory.LastFocused);
    }

    [AvaloniaFact]
    public void OnKeyDown_BareKeyNoModifier_ReachesChildUnhandled()
    {
        var (dock, factory, _, _) = BuildDock(3);
        var controller = DockTabSwitch.GetController(dock)!;

        // A bare digit (no Alt) matches no gesture, so it is not swallowed and still reaches children.
        var args = KeyDown(Key.D1, KeyModifiers.None, dock);
        controller.ProcessKeyDown(args);

        Assert.False(args.Handled);
        Assert.Null(factory.LastActive);
    }

    [AvaloniaFact]
    public void ModifierDown_TogglesBadgeVisibilityFlag()
    {
        var (dock, _, _, _) = BuildDock(3);
        var controller = DockTabSwitch.GetController(dock)!;

        Assert.False(controller.AreBadgesVisible);

        // Holding the default activation modifier (Alt) shows the badges.
        controller.ProcessKeyDown(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.LeftAlt,
            KeyModifiers = KeyModifiers.Alt,
            Source = dock,
        });
        Assert.True(controller.AreBadgesVisible);

        // Releasing it hides them again.
        controller.ProcessKeyUp(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyUpEvent,
            Key = Key.LeftAlt,
            KeyModifiers = KeyModifiers.None,
            Source = dock,
        });
        Assert.False(controller.AreBadgesVisible);
    }

    [AvaloniaFact]
    public void CustomBinding_ControlShiftDigit_ActivatesIndexedDockable()
    {
        var (dock, factory, documents, _) = BuildDock(3);
        DockTabSwitch.SetBindings(dock, new DockTabSwitchBindings
        {
            new DockTabSwitchGestures
            {
                Modifiers = KeyModifiers.Control | KeyModifiers.Shift,
                Keys = DockTabSwitchKeys.Digits,
            },
        });
        var controller = DockTabSwitch.GetController(dock)!;

        // Ctrl+Shift+2 → index 1; a bare Alt+2 must not match this binding.
        var altArgs = KeyDown(Key.D2, KeyModifiers.Alt, dock);
        controller.ProcessKeyDown(altArgs);
        Assert.False(altArgs.Handled);
        Assert.Null(factory.LastActive);

        var args = KeyDown(Key.D2, KeyModifiers.Control | KeyModifiers.Shift, dock);
        controller.ProcessKeyDown(args);
        Assert.True(args.Handled);
        Assert.Same(documents[1], factory.LastActive);
    }
}
