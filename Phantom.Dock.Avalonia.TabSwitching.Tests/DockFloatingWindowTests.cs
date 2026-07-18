using System.Linq;
using Avalonia.Collections;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using DockDocument = Dock.Model.Avalonia.Controls.Document;
using DockDocumentDock = Dock.Model.Avalonia.Controls.DocumentDock;
using DockProportionalDock = Dock.Model.Avalonia.Controls.ProportionalDock;
using DockRootDock = Dock.Model.Avalonia.Controls.RootDock;
using Xunit;

namespace Phantom.Dock.Avalonia.TabSwitching.Tests;

/// <summary>
/// Covers floating-window attachment via Dock's <see cref="IFactory.DockControls"/> registry (commit 7
/// of the #1073 epic, design §8.6): when <c>DockTabSwitch.Enabled</c> installs the controller on the root
/// <c>DockControl</c>, the controller subscribes to the factory registry and attaches the same
/// gesture/badge pipeline to every <c>DockControl</c> that appears (the inner control of each
/// <c>HostWindow</c> as floats are created), detaching as they are removed. Numbering/gestures resolve
/// against each floating window's own layout root, so <see cref="DockTabSwitchScope.FocusedDockOnly"/>
/// stays per-window.
/// </summary>
public sealed class DockFloatingWindowTests
{
    private sealed record ActivationRecord(IDock Strip, IDockable Dockable);

    /// <summary>
    /// A Dock factory that records <see cref="SetActiveDockable"/>/<see cref="SetFocusedDockable"/>
    /// calls without performing any real docking work. Its <see cref="IFactory.DockControls"/> registry
    /// is the observable collection the controller subscribes to. A <c>DockControl</c> registers itself
    /// there as soon as it is bound to this factory, mirroring a <c>HostWindow</c>'s inner control.
    /// </summary>
    private sealed class RecordingFactory : global::Dock.Model.Avalonia.Factory
    {
        public IDockable? LastActive { get; private set; }

        public ActivationRecord? LastFocused { get; private set; }

        public override void SetActiveDockable(IDockable dockable) => LastActive = dockable;

        public override void SetFocusedDockable(IDock dock, IDockable? dockable) =>
            LastFocused = dockable is null ? null : new ActivationRecord(dock, dockable);
    }

    private static DockDocument Doc(RecordingFactory factory, IDock owner, string id) =>
        new() { Id = id, Title = id, Owner = owner, Factory = factory };

    /// <summary>
    /// Builds a single-strip layout (a <see cref="DockRootDock"/> hosting one <see cref="DockDocumentDock"/>)
    /// wired to <paramref name="factory"/>, and wraps it in a <see cref="DockControl"/> — which registers
    /// itself in the factory registry, exactly as each <c>HostWindow</c>'s inner control does.
    /// </summary>
    private static (DockControl Dock, IDockable[] Documents, IDock Strip) BuildFloating(
        RecordingFactory factory, int documentCount, string prefix)
    {
        var strip = new DockDocumentDock();
        var documents = new IDockable[documentCount];
        var visible = new AvaloniaList<IDockable>();
        for (var i = 0; i < documentCount; i++)
        {
            var document = Doc(factory, strip, $"{prefix}{i}");
            documents[i] = document;
            visible.Add(document);
        }

        strip.VisibleDockables = visible;

        var root = new DockRootDock
        {
            IsFocusableRoot = true,
            Factory = factory,
            VisibleDockables = new AvaloniaList<IDockable> { strip },
        };
        strip.Owner = root;

        var dock = new DockControl { Factory = factory, Layout = root };
        return (dock, documents, strip);
    }

    private static (DockControl Root, RecordingFactory Factory, DockTabSwitchController Controller) BuildRoot()
    {
        var factory = new RecordingFactory();
        var (dock, _, _) = BuildFloating(factory, 2, "root");
        DockTabSwitch.SetEnabled(dock, true);
        return (dock, factory, DockTabSwitch.GetController(dock)!);
    }

    private static KeyEventArgs KeyDown(Key key, KeyModifiers modifiers, object source) => new()
    {
        RoutedEvent = InputElement.KeyDownEvent,
        Key = key,
        KeyModifiers = modifiers,
        Source = source,
    };

    [AvaloniaFact]
    public void FloatingDockControl_RegisteredInFactory_ReceivesPipeline()
    {
        var (root, factory, controller) = BuildRoot();

        // Only the root pipeline exists until another DockControl joins the registry.
        Assert.True(controller.IsAttachedTo(root));
        Assert.Equal(1, controller.AttachedPipelineCount);

        // A floating DockControl bound to the same factory registers itself in IFactory.DockControls
        // (as a HostWindow's inner control does); the controller attaches the same pipeline automatically.
        var (floating, _, _) = BuildFloating(factory, 3, "float");

        Assert.Contains(floating, factory.DockControls.OfType<DockControl>());
        Assert.True(controller.IsAttachedTo(floating));
        Assert.True(controller.IsAttachedTo(root));
        Assert.Equal(2, controller.AttachedPipelineCount);
    }

    [AvaloniaFact]
    public void FloatingDockControl_Removed_DetachesPipeline()
    {
        var (_, factory, controller) = BuildRoot();

        var (floating, _, _) = BuildFloating(factory, 3, "float");
        Assert.True(controller.IsAttachedTo(floating));

        // Removing a DockControl from the registry detaches its pipeline (no leak) — a subsequent gesture
        // on the removed control no longer activates anything.
        factory.DockControls.Remove(floating);

        Assert.False(controller.IsAttachedTo(floating));
        Assert.Equal(1, controller.AttachedPipelineCount);

        var args = KeyDown(Key.D1, KeyModifiers.Alt, floating);
        floating.RaiseEvent(args);
        Assert.False(args.Handled);
        Assert.Null(factory.LastActive);
    }

    [AvaloniaFact]
    public void FloatingWindow_GestureActivatesTabInFloatedLayout()
    {
        var (root, factory, _) = BuildRoot();

        // A floated layout with its own documents receives the same default Alt+digit gesture pipeline,
        // resolved against the floated layout's own root.
        var (floating, documents, strip) = BuildFloating(factory, 3, "float");

        // Alt+2 → index 1 inside the floated layout.
        var args = KeyDown(Key.D2, KeyModifiers.Alt, floating);
        floating.RaiseEvent(args);

        Assert.True(args.Handled);
        Assert.Same(documents[1], factory.LastActive);
        Assert.NotNull(factory.LastFocused);
        Assert.Same(documents[1], factory.LastFocused!.Dockable);
        Assert.Same(strip, factory.LastFocused.Strip);

        // The main window is unaffected — its own layout still activates from its own root.
        var rootArgs = KeyDown(Key.D1, KeyModifiers.Alt, root);
        root.RaiseEvent(rootArgs);
        Assert.True(rootArgs.Handled);
        Assert.Equal("root0", ((DockDocument)factory.LastActive!).Id);
    }

    [AvaloniaFact]
    public void FloatingWindow_FocusedDockOnly_ResolvesPerWindowRoot()
    {
        var factory = new RecordingFactory();

        // Root window with a FocusedDockOnly binding configured on the root DockControl (the whole
        // controller — including floating pipelines — resolves bindings from here).
        var (root, _, _) = BuildFloating(factory, 2, "root");
        DockTabSwitch.SetBindings(root, new DockTabSwitchBindings
        {
            new DockTabSwitchGestures
            {
                Modifiers = KeyModifiers.Alt,
                Keys = DockTabSwitchKeys.Digits,
                Scope = DockTabSwitchScope.FocusedDockOnly,
            },
        });
        DockTabSwitch.SetEnabled(root, true);
        var controller = DockTabSwitch.GetController(root)!;

        // A floating window with two strips; focus a document in the second strip via Dock's focus field.
        var floatRoot = new DockRootDock { IsFocusableRoot = true, Factory = factory };
        var split = new DockProportionalDock { Owner = floatRoot };
        var stripA = new DockDocumentDock { Owner = split };
        var stripB = new DockDocumentDock { Owner = split };
        var a0 = Doc(factory, stripA, "a0");
        var a1 = Doc(factory, stripA, "a1");
        var b0 = Doc(factory, stripB, "b0");
        var b1 = Doc(factory, stripB, "b1");
        var b2 = Doc(factory, stripB, "b2");
        stripA.VisibleDockables = new AvaloniaList<IDockable> { a0, a1 };
        stripB.VisibleDockables = new AvaloniaList<IDockable> { b0, b1, b2 };
        split.VisibleDockables = new AvaloniaList<IDockable> { stripA, stripB };
        floatRoot.VisibleDockables = new AvaloniaList<IDockable> { split };
        floatRoot.FocusedDockable = b0;

        var floating = new DockControl { Factory = factory, Layout = floatRoot };
        Assert.True(controller.IsAttachedTo(floating));

        // Alt+2 in the floating window resolves FocusedDockOnly against the floating window's own focused
        // dock (strip B) → its second tab, not strip A and not the main window's layout.
        var args = KeyDown(Key.D2, KeyModifiers.Alt, floating);
        floating.RaiseEvent(args);

        Assert.True(args.Handled);
        Assert.Same(b1, factory.LastActive);
        Assert.Same(stripB, factory.LastFocused!.Strip);
    }
}
