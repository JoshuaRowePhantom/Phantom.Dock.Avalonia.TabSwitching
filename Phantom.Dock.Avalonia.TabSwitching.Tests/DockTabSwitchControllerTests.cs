using Avalonia.Collections;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using DockDocument = global::Dock.Model.Avalonia.Controls.Document;
using DockDocumentDock = global::Dock.Model.Avalonia.Controls.DocumentDock;
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
}
