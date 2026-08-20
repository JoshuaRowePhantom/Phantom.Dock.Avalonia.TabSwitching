using System.Linq;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Avalonia.Themes.Fluent;
using Dock.Model.Core;
using DockDocument = global::Dock.Model.Avalonia.Controls.Document;
using DockDocumentDock = global::Dock.Model.Avalonia.Controls.DocumentDock;
using DockProportionalDock = Dock.Model.Avalonia.Controls.ProportionalDock;
using DockProportionalDockSplitter = Dock.Model.Avalonia.Controls.ProportionalDockSplitter;
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

    // --- #1121: exact modifier equality (per-chord badge visibility) -----------------------------

    private static KeyEventArgs KeyEvent(RoutedEvent<KeyEventArgs> routed, Key key, KeyModifiers modifiers, object source) => new()
    {
        RoutedEvent = routed,
        Key = key,
        KeyModifiers = modifiers,
        Source = source,
    };

    private static void HoldModifiers(DockTabSwitchController controller, DockControl source, params Key[] modifierKeys)
    {
        var mods = KeyModifiers.None;
        foreach (var k in modifierKeys)
        {
            mods |= ModifierForTest(k);
            controller.ProcessKeyDown(KeyEvent(InputElement.KeyDownEvent, k, mods, source));
        }
    }

    private static void ReleaseModifier(DockTabSwitchController controller, DockControl source, Key modifierKey, KeyModifiers remaining)
    {
        controller.ProcessKeyUp(KeyEvent(InputElement.KeyUpEvent, modifierKey, remaining, source));
    }

    private static KeyModifiers ModifierForTest(Key key) => key switch
    {
        Key.LeftAlt or Key.RightAlt => KeyModifiers.Alt,
        Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
        Key.LeftCtrl or Key.RightCtrl => KeyModifiers.Control,
        Key.LWin or Key.RWin => KeyModifiers.Meta,
        _ => KeyModifiers.None,
    };

    [AvaloniaFact]
    public void AltOnlyHeld_ShowsAltIndicesOnly()
    {
        // Only the default Alt binding is configured. Holding Alt alone turns on the aggregate
        // and marks the Alt-labeled indices as visible.
        var (dock, _, _, _) = BuildDock(3);
        var controller = DockTabSwitch.GetController(dock)!;

        HoldModifiers(controller, dock, Key.LeftAlt);

        Assert.True(controller.AreBadgesVisible);
    }

    [AvaloniaFact]
    public void AltShiftHeld_ShowsAltShiftIndices_AndHidesAltIndices()
    {
        // With BOTH an Alt binding and an Alt+Shift binding on the same control, holding Alt+Shift
        // must NOT light the Alt-only labels (the #1121 regression). Aggregate stays true because
        // the Alt+Shift binding matches exactly; per-label visibility asserted below.
        var (dock, _, documents, _) = BuildDock(3);
        DockTabSwitch.SetBindings(dock, new DockTabSwitchBindings
        {
            new DockTabSwitchGestures { Modifiers = KeyModifiers.Alt, Keys = DockTabSwitchKeys.Digits },
            new DockTabSwitchGestures { Modifiers = KeyModifiers.Alt | KeyModifiers.Shift, Keys = DockTabSwitchKeys.Digits },
        });
        var controller = DockTabSwitch.GetController(dock)!;

        HoldModifiers(controller, dock, Key.LeftAlt, Key.LeftShift);

        Assert.True(controller.AreBadgesVisible);

        // Force label materialization for the first document without a visual tree, then re-run the
        // per-label visibility pass with the exact held chord.
        var container = new ContentControl { DataContext = documents[0] };
        controller.PrepareContainer(container);

        var context = DockTabSwitch.GetIndexContext(container)!;
        Assert.Equal(2, context.Labels.Count);
        var altLabel = context.Labels.Single(l => l.GestureSet.Modifiers == KeyModifiers.Alt);
        var altShiftLabel = context.Labels.Single(l => l.GestureSet.Modifiers == (KeyModifiers.Alt | KeyModifiers.Shift));

        Assert.False(altLabel.IsVisible);
        Assert.True(altShiftLabel.IsVisible);
    }

    [AvaloniaFact]
    public void AltControlHeld_ShowsNoIndices_WhenNoMatchingChord()
    {
        // Alt+Control is a superset of Alt but is not itself a configured chord ⇒ no badges show.
        var (dock, _, _, _) = BuildDock(3);
        var controller = DockTabSwitch.GetController(dock)!;

        HoldModifiers(controller, dock, Key.LeftAlt, Key.LeftCtrl);

        Assert.False(controller.AreBadgesVisible);
    }

    [AvaloniaFact]
    public void AltHeldThenShiftAdded_SwapsVisibleIndexSetLive()
    {
        // Alt alone lights the Alt label; adding Shift (without releasing Alt) swaps visibility to
        // the Alt+Shift label; releasing Shift restores the Alt-only visibility.
        var (dock, _, documents, _) = BuildDock(3);
        DockTabSwitch.SetBindings(dock, new DockTabSwitchBindings
        {
            new DockTabSwitchGestures { Modifiers = KeyModifiers.Alt, Keys = DockTabSwitchKeys.Digits },
            new DockTabSwitchGestures { Modifiers = KeyModifiers.Alt | KeyModifiers.Shift, Keys = DockTabSwitchKeys.Digits },
        });
        var controller = DockTabSwitch.GetController(dock)!;

        var container = new ContentControl { DataContext = documents[0] };
        controller.PrepareContainer(container);

        var context = DockTabSwitch.GetIndexContext(container)!;
        var altLabel = context.Labels.Single(l => l.GestureSet.Modifiers == KeyModifiers.Alt);
        var altShiftLabel = context.Labels.Single(l => l.GestureSet.Modifiers == (KeyModifiers.Alt | KeyModifiers.Shift));

        // Alt only.
        HoldModifiers(controller, dock, Key.LeftAlt);
        Assert.True(altLabel.IsVisible);
        Assert.False(altShiftLabel.IsVisible);

        // Add Shift while still holding Alt.
        HoldModifiers(controller, dock, Key.LeftShift);
        Assert.False(altLabel.IsVisible);
        Assert.True(altShiftLabel.IsVisible);

        // Release Shift; Alt remains held.
        ReleaseModifier(controller, dock, Key.LeftShift, KeyModifiers.Alt);
        Assert.True(altLabel.IsVisible);
        Assert.False(altShiftLabel.IsVisible);
    }

    [AvaloniaFact]
    public void AllModifiersReleased_HidesAllIndices()
    {
        // Extends ModifierDown_TogglesBadgeVisibilityFlag: releasing every modifier not only clears
        // the aggregate but drives all per-label IsVisible false.
        var (dock, _, documents, _) = BuildDock(3);
        var controller = DockTabSwitch.GetController(dock)!;

        var container = new ContentControl { DataContext = documents[0] };
        controller.PrepareContainer(container);
        var context = DockTabSwitch.GetIndexContext(container)!;
        var altLabel = context.Labels.Single(l => l.GestureSet.Modifiers == KeyModifiers.Alt);

        HoldModifiers(controller, dock, Key.LeftAlt);
        Assert.True(controller.AreBadgesVisible);
        Assert.True(altLabel.IsVisible);

        ReleaseModifier(controller, dock, Key.LeftAlt, KeyModifiers.None);

        Assert.False(controller.AreBadgesVisible);
        Assert.False(altLabel.IsVisible);
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

    // --- #1081: per-DockControl binding resolution + window-scoped auto-wire ---------------------

    private sealed record HostContentFixture(
        DockControl Host,
        DockControl Content,
        DockTabSwitchController HostController,
        DockTabSwitchController ContentController,
        global::Dock.Model.Avalonia.Controls.Document[] HostDocs,
        global::Dock.Model.Avalonia.Controls.Document[] ContentDocs,
        IDock HostStrip,
        IDock ContentStrip,
        RecordingFactory Factory);

    /// <summary>
    /// Builds the product's two-separate-DockControl topology (Option A): a shared factory owns
    /// both an outer host <see cref="DockControl"/> (numbered with <c>Alt+Shift+N</c>) and a nested
    /// content <see cref="DockControl"/> (numbered with <c>Alt+N</c>). Both register themselves
    /// in the factory's <c>DockControls</c> collection, so the controller registry auto-wire
    /// must not cross-bind them (window-scoped auto-wire).
    /// </summary>
    private static HostContentFixture BuildHostAndContent(int hostDocs, int contentDocs)
    {
        var factory = new RecordingFactory();

        var hostDocuments = new global::Dock.Model.Avalonia.Controls.Document[hostDocs];
        var hostStrip = new DockDocumentDock();
        var hostVisible = new AvaloniaList<IDockable>();
        for (var i = 0; i < hostDocs; i++)
        {
            var d = new global::Dock.Model.Avalonia.Controls.Document { Id = "h" + i, Title = "h" + i, Owner = hostStrip, Factory = factory };
            hostDocuments[i] = d;
            hostVisible.Add(d);
        }
        hostStrip.VisibleDockables = hostVisible;
        var hostRoot = new DockRootDock { Factory = factory, VisibleDockables = new AvaloniaList<IDockable> { hostStrip } };
        hostStrip.Owner = hostRoot;
        var host = new DockControl { Factory = factory, Layout = hostRoot };

        var contentDocuments = new global::Dock.Model.Avalonia.Controls.Document[contentDocs];
        var contentStrip = new DockDocumentDock();
        var contentVisible = new AvaloniaList<IDockable>();
        for (var i = 0; i < contentDocs; i++)
        {
            var d = new global::Dock.Model.Avalonia.Controls.Document { Id = "c" + i, Title = "c" + i, Owner = contentStrip, Factory = factory };
            contentDocuments[i] = d;
            contentVisible.Add(d);
        }
        contentStrip.VisibleDockables = contentVisible;
        var contentRoot = new DockRootDock { Factory = factory, VisibleDockables = new AvaloniaList<IDockable> { contentStrip } };
        contentStrip.Owner = contentRoot;
        var content = new DockControl { Factory = factory, Layout = contentRoot };

        // Independent gesture sets: host = Alt+Shift+Digits, content = Alt+Digits.
        DockTabSwitch.SetBindings(host, new DockTabSwitchBindings
        {
            new DockTabSwitchGestures { Modifiers = KeyModifiers.Alt | KeyModifiers.Shift, Keys = DockTabSwitchKeys.Digits },
        });
        DockTabSwitch.SetBindings(content, new DockTabSwitchBindings
        {
            new DockTabSwitchGestures { Modifiers = KeyModifiers.Alt, Keys = DockTabSwitchKeys.Digits },
        });

        DockTabSwitch.SetEnabled(host, true);
        DockTabSwitch.SetEnabled(content, true);

        return new HostContentFixture(
            host, content,
            DockTabSwitch.GetController(host)!, DockTabSwitch.GetController(content)!,
            hostDocuments, contentDocuments,
            hostStrip, contentStrip,
            factory);
    }

    [AvaloniaFact]
    public void Pipeline_ResolvesBindingsFromOwnDockControl()
    {
        // Host carries Alt+Shift+N; content carries Alt+N. Both bindings live on their own DockControl.
        // Each controller's own root pipeline must resolve the gesture set attached to its own control,
        // NOT the other one's.
        var fx = BuildHostAndContent(hostDocs: 2, contentDocs: 3);

        // Host: Alt+Shift+1 activates host doc #0; a bare Alt+1 does not match host's binding.
        var hostAlt = KeyDown(Key.D1, KeyModifiers.Alt, fx.Host);
        fx.Host.RaiseEvent(hostAlt);
        Assert.False(hostAlt.Handled);

        var hostAltShift = KeyDown(Key.D1, KeyModifiers.Alt | KeyModifiers.Shift, fx.Host);
        fx.Host.RaiseEvent(hostAltShift);
        Assert.True(hostAltShift.Handled);
        Assert.Same(fx.HostDocs[0], fx.Factory.LastActive);

        // Content: Alt+2 activates content doc #1; a bare Alt+Shift+2 does not match content's binding.
        var contentAltShift = KeyDown(Key.D2, KeyModifiers.Alt | KeyModifiers.Shift, fx.Content);
        fx.Content.RaiseEvent(contentAltShift);
        // Alt+Shift+2 does not match content's Alt-only binding; content pipeline leaves it unhandled.
        Assert.False(contentAltShift.Handled);

        var contentAlt = KeyDown(Key.D2, KeyModifiers.Alt, fx.Content);
        fx.Content.RaiseEvent(contentAlt);
        Assert.True(contentAlt.Handled);
        Assert.Same(fx.ContentDocs[1], fx.Factory.LastActive);
    }

    [AvaloniaFact]
    public void Pipeline_FallsBackToRootBindings_WhenOwnControlHasNone()
    {
        // A floating window's inner DockControl (or any auto-wired control) with no own bindings
        // inherits the controller root's binding set — the existing floating-window inheritance
        // (design §8.6) is preserved by the new per-DockControl resolution's root fallback.
        var factory = new RecordingFactory();
        var (root, _, _) = BuildDock_Full(factory, 2, "root");
        DockTabSwitch.SetBindings(root, new DockTabSwitchBindings
        {
            new DockTabSwitchGestures { Modifiers = KeyModifiers.Control | KeyModifiers.Shift, Keys = DockTabSwitchKeys.Digits },
        });
        DockTabSwitch.SetEnabled(root, true);
        var controller = DockTabSwitch.GetController(root)!;

        // A floating DockControl bound to the same factory — no bindings of its own — inherits
        // the root's Ctrl+Shift+Digits gesture set.
        var (floating, floatDocs, _) = BuildDock_Full(factory, 3, "float");
        Assert.True(controller.IsAttachedTo(floating));

        var altArgs = KeyDown(Key.D2, KeyModifiers.Alt, floating);
        floating.RaiseEvent(altArgs);
        Assert.False(altArgs.Handled);

        var ctrlShiftArgs = KeyDown(Key.D2, KeyModifiers.Control | KeyModifiers.Shift, floating);
        floating.RaiseEvent(ctrlShiftArgs);
        Assert.True(ctrlShiftArgs.Handled);
        Assert.Same(floatDocs[1], factory.LastActive);
    }

    [AvaloniaFact]
    public void AutoWire_IsWindowScoped_DoesNotCrossBindHostAndContent()
    {
        // Host and content share one factory (same IFactory.DockControls collection), but each has
        // its own controller. The controllers' registry auto-wire must NOT cross-bind — the host
        // controller must not attach a pipeline to the content DockControl (its Alt+N would then
        // hijack numbering) and vice versa. Only each controller's own DockControl is managed by it.
        var fx = BuildHostAndContent(hostDocs: 2, contentDocs: 3);

        // Both DockControls sit in the same factory registry.
        Assert.Contains(fx.Host, fx.Factory.DockControls.OfType<DockControl>());
        Assert.Contains(fx.Content, fx.Factory.DockControls.OfType<DockControl>());

        // Host controller owns ONLY the host DockControl.
        Assert.True(fx.HostController.IsAttachedTo(fx.Host));
        Assert.False(fx.HostController.IsAttachedTo(fx.Content));
        Assert.Equal(1, fx.HostController.AttachedPipelineCount);

        // Content controller owns ONLY the content DockControl.
        Assert.True(fx.ContentController.IsAttachedTo(fx.Content));
        Assert.False(fx.ContentController.IsAttachedTo(fx.Host));
        Assert.Equal(1, fx.ContentController.AttachedPipelineCount);
    }

    [AvaloniaFact]
    public void AutoWire_SharedFactory_DoesNotDoubleAttachPipelines()
    {
        // Both DockControls share a factory. Each controller must hold exactly ONE pipeline (its
        // own root) — no double-attach that would produce two tunnel handlers on the same control
        // or duplicate badge injection.
        var fx = BuildHostAndContent(hostDocs: 2, contentDocs: 3);

        Assert.Equal(1, fx.HostController.AttachedPipelineCount);
        Assert.Equal(1, fx.ContentController.AttachedPipelineCount);

        // Cross-check: neither controller holds a pipeline for the other's DockControl.
        Assert.False(fx.HostController.IsAttachedTo(fx.Content));
        Assert.False(fx.ContentController.IsAttachedTo(fx.Host));
    }

    [AvaloniaFact]
    public void AltDigit_ReachesFocusedContentControl_NotOuterHost()
    {
        // The product topology: host wraps content in the visual tree; but with window-scoped
        // auto-wire, the host controller does NOT attach to the content control, so raising Alt+N
        // directly on the content DockControl is handled by ITS pipeline and activates a content
        // tab — the host is never given a chance to hijack Alt+N.
        var fx = BuildHostAndContent(hostDocs: 2, contentDocs: 3);

        var args = KeyDown(Key.D3, KeyModifiers.Alt, fx.Content);
        fx.Content.RaiseEvent(args);

        Assert.True(args.Handled);
        Assert.Same(fx.ContentDocs[2], fx.Factory.LastActive);
        // The host is left untouched — no cross-attached pipeline fired first.
        Assert.NotSame(fx.HostDocs[0], fx.Factory.LastActive);
    }

    [AvaloniaFact]
    public void AltShiftDigit_ReachesHostControl()
    {
        var fx = BuildHostAndContent(hostDocs: 3, contentDocs: 3);

        var args = KeyDown(Key.D2, KeyModifiers.Alt | KeyModifiers.Shift, fx.Host);
        fx.Host.RaiseEvent(args);

        Assert.True(args.Handled);
        Assert.Same(fx.HostDocs[1], fx.Factory.LastActive);
    }

    [AvaloniaFact]
    public void BadgeNumbering_IsPerDockControl()
    {
        // The host and content pipelines number their own tabs independently: index 0 of one has
        // no relationship to index 0 of the other. Alt+Shift+1 activates the host's first tab;
        // Alt+1 activates the content's first tab. Both use "1" as the badge label locally.
        var fx = BuildHostAndContent(hostDocs: 2, contentDocs: 3);

        var hostArgs = KeyDown(Key.D1, KeyModifiers.Alt | KeyModifiers.Shift, fx.Host);
        fx.Host.RaiseEvent(hostArgs);
        Assert.Same(fx.HostDocs[0], fx.Factory.LastActive);

        var contentArgs = KeyDown(Key.D1, KeyModifiers.Alt, fx.Content);
        fx.Content.RaiseEvent(contentArgs);
        Assert.Same(fx.ContentDocs[0], fx.Factory.LastActive);
    }

    [AvaloniaFact]
    public void NestedDockControls_BindingResolution_Headless_NoVisualTree()
    {
        // Deterministic no-visual-tree variant: raising the events without attaching to any window
        // or triggering LayoutUpdated still resolves bindings from each control's own configuration
        // and activates the correct dockable through its own pipeline (design §4.1/§4.5).
        var fx = BuildHostAndContent(hostDocs: 3, contentDocs: 4);

        // Neither DockControl is attached to a Window/TopLevel; we drive the pipeline directly via
        // its ProcessKeyDown surface. Content controller resolves Alt from its OWN bindings.
        var contentArgs = KeyDown(Key.D4, KeyModifiers.Alt, fx.Content);
        fx.ContentController.ProcessKeyDown(contentArgs);
        Assert.True(contentArgs.Handled);
        Assert.Same(fx.ContentDocs[3], fx.Factory.LastActive);

        // Host controller resolves Alt+Shift from its OWN bindings — Alt alone doesn't match.
        var altOnHost = KeyDown(Key.D1, KeyModifiers.Alt, fx.Host);
        fx.HostController.ProcessKeyDown(altOnHost);
        Assert.False(altOnHost.Handled);

        var altShiftOnHost = KeyDown(Key.D2, KeyModifiers.Alt | KeyModifiers.Shift, fx.Host);
        fx.HostController.ProcessKeyDown(altShiftOnHost);
        Assert.True(altShiftOnHost.Handled);
        Assert.Same(fx.HostDocs[1], fx.Factory.LastActive);
    }

    private static (DockControl Dock, IDockable[] Documents, IDock Strip) BuildDock_Full(
        RecordingFactory factory, int count, string prefix)
    {
        var strip = new DockDocumentDock();
        var docs = new IDockable[count];
        var visible = new AvaloniaList<IDockable>();
        for (var i = 0; i < count; i++)
        {
            var d = new global::Dock.Model.Avalonia.Controls.Document { Id = prefix + i, Title = prefix + i, Owner = strip, Factory = factory };
            docs[i] = d;
            visible.Add(d);
        }
        strip.VisibleDockables = visible;
        var root = new DockRootDock { Factory = factory, VisibleDockables = new AvaloniaList<IDockable> { strip } };
        strip.Owner = root;
        var dock = new DockControl { Factory = factory, Layout = root };
        return (dock, docs, strip);
    }

    /// <summary>
    /// Builds a DockControl whose layout is a two-region <see cref="DockProportionalDock"/> with an
    /// <see cref="DockProportionalDockSplitter"/> interleaved between the strips — the production shape
    /// (#1334) that reproduces the #1342 per-split ordinal gap. Returns the documents in visual order.
    /// </summary>
    private static (DockControl Dock, RecordingFactory Factory, IDockable[] Documents)
        BuildSplitDock(int countA, int countB)
    {
        var factory = new RecordingFactory();
        var stripA = new DockDocumentDock();
        var stripB = new DockDocumentDock();
        var documents = new IDockable[countA + countB];

        var visibleA = new AvaloniaList<IDockable>();
        for (var i = 0; i < countA; i++)
        {
            var d = new DockDocument { Id = "a" + i, Title = "a" + i, Owner = stripA, Factory = factory };
            documents[i] = d;
            visibleA.Add(d);
        }

        stripA.VisibleDockables = visibleA;

        var visibleB = new AvaloniaList<IDockable>();
        for (var i = 0; i < countB; i++)
        {
            var d = new DockDocument { Id = "b" + i, Title = "b" + i, Owner = stripB, Factory = factory };
            documents[countA + i] = d;
            visibleB.Add(d);
        }

        stripB.VisibleDockables = visibleB;

        var split = new DockProportionalDock();
        var splitter = new DockProportionalDockSplitter { Owner = split };
        split.VisibleDockables = new AvaloniaList<IDockable> { stripA, splitter, stripB };
        stripA.Owner = split;
        stripB.Owner = split;

        var root = new DockRootDock { Factory = factory, VisibleDockables = new AvaloniaList<IDockable> { split } };
        split.Owner = root;

        var dock = new DockControl { Factory = factory, Layout = root };
        DockTabSwitch.SetEnabled(dock, true);

        return (dock, factory, documents);
    }

    [AvaloniaFact]
    public void RefreshLabels_TwoDocumentDocksSeparatedBySplitter_LabelsAre1Through9WithoutGap()
    {
        // #1342: two document docks (5 + 4 = 9 documents) separated by a ProportionalDockSplitter. The
        // splitter must not consume an ordinal, so the rendered Alt labels read "1".."9" with no gap.
        var (dock, _, documents) = BuildSplitDock(5, 4);
        var controller = DockTabSwitch.GetController(dock)!;

        var actual = new string?[documents.Length];
        for (var i = 0; i < documents.Length; i++)
        {
            var container = new ContentControl { DataContext = documents[i] };
            controller.PrepareContainer(container);
            var context = DockTabSwitch.GetIndexContext(container)!;
            actual[i] = context.Label;
        }

        Assert.Equal(new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9" }, actual);
    }

    [AvaloniaFact]
    public void Activate_IndexFollowingSplitter_ActivatesFirstTabOfSecondRegion()
    {
        // Regression guard for #1067: the activation index and the displayed label agree across a split.
        // stripA has 2 documents (labels "1","2"); the first tab of stripB is label "3" ⇒ Alt+3 must
        // activate it, with NO off-by-one from the interleaved splitter.
        var (dock, factory, documents) = BuildSplitDock(2, 2);

        var args = KeyDown(Key.D3, KeyModifiers.Alt, dock);
        dock.RaiseEvent(args);

        Assert.True(args.Handled);
        Assert.Same(documents[2], factory.LastActive);
        Assert.NotNull(factory.LastFocused);
        Assert.Same(documents[2], factory.LastFocused!.Dockable);
    }

    // --- #1124: top-level sourcing ---------------------------------------------------------------

    [AvaloniaFact]
    public void Enabled_TopLevelSourced_SkipsInControlKeyHandlers()
    {
        // With InstallOnTopLevel set, Attach must not install its own KeyDown/KeyUp tunnel
        // handlers on the DockControl — the TopLevel is the sole source.
        var factory = new RecordingFactory();
        var (dock, _, _) = BuildDock_Full(factory, 3, "d");
        DockTabSwitch.SetInstallOnTopLevel(dock, true);
        DockTabSwitch.SetEnabled(dock, true);

        var controller = DockTabSwitch.GetController(dock)!;

        Assert.True(controller.SourcedFromTopLevelForTest);
        Assert.True(controller.RootPipelineSuppressedInControlHandlersForTest);

        // Sanity: without a hosting TopLevel, raising the event on the DockControl itself does
        // NOT fire the pipeline — the in-control handlers were suppressed.
        var args = KeyDown(Key.D1, KeyModifiers.Alt, dock);
        dock.RaiseEvent(args);
        Assert.False(args.Handled);
        Assert.Null(factory.LastActive);
    }

    // --- #1344: nested inner DockControl strip-ownership guard ------------------------------------

    private sealed record NestedFixture(
        Window Window,
        DockControl Outer,
        DockControl Inner,
        DockTabSwitchController OuterController,
        DocumentTabStrip OuterStrip,
        DocumentTabStrip InnerStrip,
        IDock OuterDock,
        IDock InnerDock);

    private static void Pump(DockControl dock)
    {
        dock.ApplyTemplate();
        dock.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        dock.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// #1344: builds the product's overlapping topology — an outer <see cref="DockControl"/> whose active
    /// document's <c>Content</c> is a nested inner <see cref="DockControl"/> (its own separate
    /// <see cref="DockRootDock"/>), mirroring a <c>WorkspacePaneDocument</c>. Both are rendered in one
    /// window so the inner control's <see cref="DocumentTabStrip"/> is a visual descendant of the outer
    /// control. Only the outer controller is enabled, so its root pipeline is the only one that could
    /// (wrongly) reach into the inner strip.
    /// </summary>
    private static NestedFixture BuildNested(int outerDocs, int innerDocs)
    {
        var innerFactory = new global::Dock.Model.Avalonia.Factory();
        var innerDock = new DockDocumentDock { Factory = innerFactory };
        var innerDocuments = new AvaloniaList<IDockable>();
        for (var i = 0; i < innerDocs; i++)
        {
            innerDocuments.Add(new DockDocument { Id = "in" + i, Title = "in" + i, Owner = innerDock, Factory = innerFactory });
        }
        innerDock.VisibleDockables = innerDocuments;
        innerDock.ActiveDockable = innerDocuments.Count > 0 ? innerDocuments[0] : null;
        var innerRoot = new DockRootDock { VisibleDockables = new AvaloniaList<IDockable> { innerDock } };
        innerDock.Owner = innerRoot;
        innerRoot.ActiveDockable = innerDock;
        innerRoot.DefaultDockable = innerDock;
        innerFactory.InitLayout(innerRoot);
        var inner = new DockControl { Factory = innerFactory, Layout = innerRoot };

        var outerFactory = new global::Dock.Model.Avalonia.Factory();
        var outerDock = new DockDocumentDock { Factory = outerFactory };
        var outerDocuments = new AvaloniaList<IDockable>();
        for (var i = 0; i < outerDocs; i++)
        {
            var d = new DockDocument { Id = "out" + i, Title = "out" + i, Owner = outerDock, Factory = outerFactory };
            if (i == 0)
            {
                d.Content = inner;
            }

            outerDocuments.Add(d);
        }
        outerDock.VisibleDockables = outerDocuments;
        outerDock.ActiveDockable = outerDocuments[0];
        var outerRoot = new DockRootDock { VisibleDockables = new AvaloniaList<IDockable> { outerDock } };
        outerDock.Owner = outerRoot;
        outerRoot.ActiveDockable = outerDock;
        outerRoot.DefaultDockable = outerDock;
        outerFactory.InitLayout(outerRoot);
        var outer = new DockControl { Factory = outerFactory, Layout = outerRoot };

        DockTabSwitch.SetEnabled(outer, true);
        var outerController = DockTabSwitch.GetController(outer)!;

        var window = new Window
        {
            Width = 800,
            Height = 400,
            Styles = { new DockFluentTheme(), new DockTabSwitchTheme() },
            Content = outer,
        };
        window.Show();
        Pump(outer);
        Pump(inner);

        var strips = outer.GetVisualDescendants().OfType<DocumentTabStrip>().ToList();
        var outerStrip = strips.First(s => ReferenceEquals(s.DataContext, outerDock));
        var innerStrip = strips.First(s => ReferenceEquals(s.DataContext, innerDock));

        return new NestedFixture(window, outer, inner, outerController, outerStrip, innerStrip, outerDock, innerDock);
    }

    [AvaloniaFact]
    public void DiscoverStrips_NestedInnerDockControl_DoesNotHookInnerStrips()
    {
        // The inner DockControl's DocumentTabStrip is a visual descendant of the outer DockControl, but
        // its model (DataContext, walked up .Owner) terminates at the INNER root — not the outer's
        // Layout. The outer pipeline's strip discovery must therefore skip it, so the outer controller
        // never overwrites the inner containers' single IndexContext (the #1344 last-writer-wins race).
        var fx = BuildNested(outerDocs: 2, innerDocs: 3);

        fx.OuterController.DiscoverStrips();

        Assert.False(fx.OuterController.StripBelongsToRootDockControl(fx.InnerStrip));
        Assert.False(fx.OuterController.IsStripHooked(fx.InnerStrip));

        fx.Window.Close();
    }

    [AvaloniaFact]
    public void DiscoverStrips_OuterStrips_AreHooked()
    {
        // The outer DockControl's own DocumentTabStrip (its model chains up to the outer Layout) IS owned
        // by the outer pipeline: it must be hooked and its realized containers numbered.
        var fx = BuildNested(outerDocs: 2, innerDocs: 3);

        fx.OuterController.DiscoverStrips();

        Assert.True(fx.OuterController.StripBelongsToRootDockControl(fx.OuterStrip));
        Assert.True(fx.OuterController.IsStripHooked(fx.OuterStrip));

        // A realized outer container carries the outer controller's IndexContext (numbered "1"..).
        var container = Assert.IsType<DocumentTabStripItem>(fx.OuterStrip.ContainerFromIndex(0));
        Assert.NotNull(DockTabSwitch.GetIndexContext(container));

        fx.Window.Close();
    }

    [AvaloniaFact]
    public void StripBelongsToDockControl_UnboundStrip_ReturnsFalse()
    {
        // A strip whose DataContext has not yet bound to an IDockable model cannot be attributed to any
        // DockControl, so the ownership predicate returns false and DiscoverStrips leaves it un-hooked
        // (it is re-evaluated on the next LayoutUpdated once the model settles).
        var (dock, _, _, _) = BuildDock(3);
        var controller = DockTabSwitch.GetController(dock)!;

        var unbound = new DocumentTabStrip();
        Assert.Null(unbound.DataContext);

        Assert.False(controller.StripBelongsToRootDockControl(unbound));
    }
}
