using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Core;

namespace Phantom.Dock.Avalonia.TabSwitching;

/// <summary>
/// The manager for the Dock Tab-Switching API. This is the only stateful object in the design (§5) and
/// is created purely from the <see cref="DockTabSwitch"/> attached properties — it never touches a
/// view-model.
/// </summary>
/// <remarks>
/// The controller owns the shared state (held-modifier tracking, <see cref="AreBadgesVisible"/>, and the
/// gesture-set bindings resolved from the root <see cref="DockControl"/>) and installs a per-<c>DockControl</c>
/// <see cref="Pipeline"/> that wires the tunnel <c>KeyDown</c> activation pipeline (design §4.1/§4.5),
/// discovers tab strips and injects badges (§4.4), and resolves scope-aware ordering (§4.2/§4.5).
///
/// Floating windows (design §8.6): a floated document lives in a Dock <c>HostWindow</c> whose template
/// hosts its own inner <see cref="DockControl"/>. Every <c>DockControl</c> — main or floating — registers
/// itself in the factory's <see cref="IFactory.DockControls"/> registry (an observable collection). So the
/// controller does not need a hand-attached <c>Enabled</c> on each window: on <see cref="Attach"/> it
/// subscribes to that registry and attaches the same pipeline to every <c>DockControl</c> that appears
/// (including each <c>HostWindow</c>'s inner control as floats are created), detaching on removal.
/// </remarks>
public sealed class DockTabSwitchController : IDisposable
{
    private static readonly DockTabSwitchGestures[] DefaultBindings = { new() };

    private readonly Dictionary<DockControl, Pipeline> _pipelines = new();

    private Pipeline? _rootPipeline;
    private INotifyCollectionChanged? _registry;
    private bool _attached;
    private bool _disposed;
    private KeyModifiers _heldModifiers;

    // #1124: top-level sourcing. When DockTabSwitch.InstallOnTopLevel is true on the root
    // DockControl, the controller installs its tunnel key handlers on the hosting TopLevel
    // instead of on the DockControl itself, and re-binds on visual-tree attach/detach.
    private bool _sourcedFromTopLevel;
    private TopLevel? _boundTopLevel;

    public DockTabSwitchController(DockControl dockControl)
    {
        DockControl = dockControl ?? throw new ArgumentNullException(nameof(dockControl));
    }

    /// <summary>The root <c>DockControl</c> this controller was installed on.</summary>
    public DockControl DockControl { get; }

    /// <summary>Whether <see cref="Attach"/> has run without a subsequent <see cref="Detach"/>.</summary>
    public bool IsAttached => _attached;

    /// <summary>
    /// Consumed by the badge template: <c>true</c> while an activation modifier is held. Driven by the
    /// controller's <c>KeyDown</c>/<c>KeyUp</c> modifier tracking.
    /// </summary>
    public bool AreBadgesVisible { get; internal set; }

    /// <summary>
    /// Installs the controller's behavior on the root <see cref="DockControl"/> and — via the factory's
    /// <see cref="IFactory.DockControls"/> registry — on every floating <c>DockControl</c>. Idempotent:
    /// calling it while already attached is a no-op.
    /// </summary>
    public void Attach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attached)
        {
            return;
        }

        _attached = true;

        // #1124/#1332: if the DockControl declared InstallOnTopLevel, source key events from the
        // hosting TopLevel via the per-TopLevel coordinator, which routes each chord to the focused /
        // most-recently-focused live region. Per-pipeline in-control AddHandler calls are suppressed
        // via _sourcedFromTopLevel so exactly one ProcessActivation runs per physical key event.
        _sourcedFromTopLevel = DockTabSwitch.GetInstallOnTopLevel(DockControl);
        if (_sourcedFromTopLevel)
        {
            DockControl.AttachedToVisualTree += OnRootAttachedToVisualTree;
            DockControl.DetachedFromVisualTree += OnRootDetachedFromVisualTree;
            RebindTopLevel(TopLevel.GetTopLevel(DockControl));
        }

        _rootPipeline = AttachPipeline(DockControl);
        // Reclaim ownership: if any other controller had auto-attached to this controller's root
        // DockControl (order-of-creation race, e.g. a host controller attached before the nested
        // content DockControl's own controller existed), tell it to release the pipeline so our
        // own root-pipeline is the sole tunnel handler here (design §8.6 / #1081).
        StealRootFromOtherControllers();
        SubscribeToRegistry();
    }

    private void StealRootFromOtherControllers()
    {
        if (Factory?.DockControls is not { } controls)
        {
            return;
        }

        foreach (var dc in controls.OfType<DockControl>())
        {
            if (ReferenceEquals(dc, DockControl))
            {
                continue;
            }

            var other = DockTabSwitch.GetController(dc);
            if (other is not null && !ReferenceEquals(other, this))
            {
                other.DetachAutoWirePipelineFor(DockControl);
            }
        }
    }

    /// <summary>
    /// Detaches any auto-wire pipeline this controller may hold for <paramref name="dockControl"/>,
    /// which is <b>not</b> this controller's own root <see cref="DockControl"/>. Called by another
    /// controller taking over ownership of <paramref name="dockControl"/> (design §8.6 / #1081).
    /// </summary>
    internal void DetachAutoWirePipelineFor(DockControl dockControl)
    {
        if (ReferenceEquals(dockControl, DockControl))
        {
            return;
        }

        if (_pipelines.Remove(dockControl, out var pipeline))
        {
            pipeline.Detach();
        }
    }

    /// <summary>
    /// Removes everything <see cref="Attach"/> installed, on the root and every floating <c>DockControl</c>.
    /// Idempotent: calling it while not attached is a no-op.
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

        // #1124: unbind top-level source, if any.
        if (_sourcedFromTopLevel)
        {
            DockControl.AttachedToVisualTree -= OnRootAttachedToVisualTree;
            DockControl.DetachedFromVisualTree -= OnRootDetachedFromVisualTree;
            RebindTopLevel(null);
            _sourcedFromTopLevel = false;
        }

        UnsubscribeFromRegistry();

        foreach (var pipeline in _pipelines.Values.ToList())
        {
            pipeline.Detach();
        }

        _pipelines.Clear();
        _rootPipeline = null;
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

    // --- #1124: top-level sourcing ---------------------------------------------------------------

    /// <summary>Test hook: whether the controller has bound its tunnel handlers to a <c>TopLevel</c>.</summary>
    internal TopLevel? BoundTopLevelForTest => _boundTopLevel;

    /// <summary>Test hook: whether the root pipeline suppressed its own in-control key handlers (#1124).</summary>
    internal bool SourcedFromTopLevelForTest => _sourcedFromTopLevel;

    /// <summary>Test hook (#1124): whether the root pipeline skipped its in-control AddHandler calls.</summary>
    internal bool RootPipelineSuppressedInControlHandlersForTest =>
        _rootPipeline?.SuppressedInControlHandlersForTest ?? false;

    private void OnRootAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e) =>
        RebindTopLevel(TopLevel.GetTopLevel(DockControl));

    private void OnRootDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e) =>
        RebindTopLevel(null);

    private void RebindTopLevel(TopLevel? newTopLevel)
    {
        if (ReferenceEquals(_boundTopLevel, newTopLevel))
        {
            return;
        }

        // #1332: routing is centralized per-TopLevel. Registering/unregistering with the coordinator
        // (rather than installing our own TopLevel handlers) is what lets a re-templated-away region
        // drop out of routing and prevents a stale controller from stealing the chord.
        if (_boundTopLevel is not null)
        {
            DockTabSwitchTopLevelCoordinator.Unregister(_boundTopLevel, this);
        }

        _boundTopLevel = newTopLevel;

        if (_boundTopLevel is not null)
        {
            DockTabSwitchTopLevelCoordinator.Register(_boundTopLevel, this);
        }
    }

    /// <summary>
    /// #1332: dispatches only the activation half of a chord (no modifier tracking) to this controller's
    /// root pipeline. The coordinator tracks modifiers on every region but activates exactly one.
    /// </summary>
    internal void ActivateFromTopLevel(KeyEventArgs e) => _rootPipeline?.ProcessActivation(e);

    /// <summary>
    /// #1332: whether any of the controller's effective gesture sets maps <paramref name="e"/> to a tab
    /// index — i.e. this region is a candidate target for this chord.
    /// </summary>
    internal bool MatchesActivationChord(KeyEventArgs e)
    {
        foreach (var binding in GetEffectiveBindings())
        {
            if (binding.BuildMap().TryGetIndex(e, out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// #1332: whether Dock focus is currently inside this controller's <c>DockControl.Layout</c> (directly
    /// or indirectly), read via Dock's own focus field — never Avalonia's visual <c>FocusManager</c>.
    /// </summary>
    internal bool IsDockFocusInside() =>
        DockControl.Layout is { } layout && DockTabScopeResolver.IsFocusInsideLayout(layout);

    /// <summary>
    /// #1124/#1332: whether this controller's root <see cref="DockControl"/> is currently effectively
    /// visible. A region whose host is hidden (a collapsed pane, a detail dock behind a hidden ancestor)
    /// must never be a top-level chord target, so a gesture is a no-op rather than switching a hidden dock.
    /// </summary>
    internal bool IsEffectivelyVisible => DockControl.IsEffectivelyVisible;

    // --- Floating-window attachment via IFactory.DockControls (design §8.6) -----------------------

    /// <summary>The factory whose registry lists every main/floating <c>DockControl</c>.</summary>
    private IFactory? Factory => DockControl.Factory ?? DockControl.Layout?.Factory;

    private void SubscribeToRegistry()
    {
        if (Factory?.DockControls is not { } controls)
        {
            return;
        }

        // Attach to any DockControls already registered (the root usually self-registers on load).
        foreach (var dockControl in controls.OfType<DockControl>().ToList())
        {
            AttachPipeline(dockControl);
        }

        if (controls is INotifyCollectionChanged incc)
        {
            _registry = incc;
            incc.CollectionChanged += OnDockControlsChanged;
        }
    }

    private void UnsubscribeFromRegistry()
    {
        if (_registry is not null)
        {
            _registry.CollectionChanged -= OnDockControlsChanged;
            _registry = null;
        }
    }

    private void OnDockControlsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var dockControl in e.OldItems.OfType<DockControl>())
            {
                DetachPipeline(dockControl);
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // A Reset clears the registry: detach every floating pipeline (never the root).
            foreach (var dockControl in _pipelines.Keys.Where(k => !ReferenceEquals(k, DockControl)).ToList())
            {
                DetachPipeline(dockControl);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var dockControl in e.NewItems.OfType<DockControl>())
            {
                AttachPipeline(dockControl);
            }
        }
    }

    private Pipeline AttachPipeline(DockControl dockControl)
    {
        if (_pipelines.TryGetValue(dockControl, out var existing))
        {
            return existing;
        }

        // Window-scoped auto-wire (design §8.6 / #1081): don't cross-bind a DockControl that
        // already carries its own controller (e.g., a nested content DockControl configured with
        // its own gesture set). Each controller manages the DockControls it owns; sibling
        // controls sharing the same factory but wired to a different controller are left alone.
        // Only the controller root and DockControls without their own controller (typically
        // Dock's floating HostWindow inner controls) get an auto-attached pipeline.
        if (!ReferenceEquals(dockControl, DockControl))
        {
            var otherController = DockTabSwitch.GetController(dockControl);
            if (otherController is not null && !ReferenceEquals(otherController, this))
            {
                return null!;
            }
        }

        var pipeline = new Pipeline(this, dockControl);
        _pipelines[dockControl] = pipeline;
        pipeline.Attach();
        return pipeline;
    }

    private void DetachPipeline(DockControl dockControl)
    {
        // The root pipeline's lifetime is tied to the controller, not the registry.
        if (ReferenceEquals(dockControl, DockControl))
        {
            return;
        }

        if (_pipelines.Remove(dockControl, out var pipeline))
        {
            pipeline.Detach();
        }
    }

    /// <summary>Test hook: whether a pipeline is currently attached to <paramref name="dockControl"/>.</summary>
    internal bool IsAttachedTo(DockControl dockControl) => _pipelines.ContainsKey(dockControl);

    /// <summary>Test hook: the number of attached pipelines (root + floating).</summary>
    internal int AttachedPipelineCount => _pipelines.Count;

    // --- Root-pipeline delegation (test / back-compat surface) -----------------------------------

    internal void ProcessKeyDown(KeyEventArgs e) => _rootPipeline?.ProcessKeyDown(e);

    internal void ProcessKeyUp(KeyEventArgs e) => _rootPipeline?.ProcessKeyUp(e);

    internal void InjectBadge(Control container) => _rootPipeline?.InjectBadge(container);

    internal void DiscoverStrips() => _rootPipeline?.DiscoverStrips();

    internal void PrepareContainer(Control container) => _rootPipeline?.PrepareContainer(container);

    /// <summary>#1344: exposes the root pipeline's model-level strip-ownership predicate for tests.</summary>
    internal bool StripBelongsToRootDockControl(DocumentTabStrip strip) =>
        _rootPipeline?.StripBelongsToDockControl(strip) ?? false;

    /// <summary>#1344: whether the root pipeline has hooked <paramref name="strip"/> (test hook).</summary>
    internal bool IsStripHooked(DocumentTabStrip strip) => _rootPipeline?.IsStripHooked(strip) ?? false;

    internal void ClearContainer(Control container) => _rootPipeline?.ClearContainer(container);

    internal IReadOnlyList<DockTabEntry> ComputeOrder(DockTabSwitchGestures binding) =>
        _rootPipeline?.ComputeOrder(binding) ?? Array.Empty<DockTabEntry>();

    // --- Shared state used by every pipeline -----------------------------------------------------

    /// <summary>
    /// The effective gesture-set bindings for the controller root — the fallback for pipelines
    /// whose own <c>DockControl</c> has no <see cref="DockTabSwitch.BindingsProperty"/> set. See
    /// <see cref="GetEffectiveBindings(DockControl)"/> for the per-<c>DockControl</c> resolution
    /// (design §8.6, own control first, root as fallback).
    /// </summary>
    internal IReadOnlyList<DockTabSwitchGestures> GetEffectiveBindings() =>
        GetEffectiveBindings(DockControl);

    /// <summary>
    /// The effective gesture-set bindings for <paramref name="dockControl"/>. Resolution order:
    /// (1) <see cref="DockTabSwitch.GetBindings"/> on <paramref name="dockControl"/> itself, then
    /// (2) the same property on the controller root (so floating windows without an explicit
    /// configuration inherit the root's), then (3) the packaged default. This lets host and
    /// nested content <c>DockControl</c>s sharing one factory carry independent gesture sets.
    /// </summary>
    internal IReadOnlyList<DockTabSwitchGestures> GetEffectiveBindings(DockControl dockControl)
    {
        var own = DockTabSwitch.GetBindings(dockControl);
        if (own is { Count: > 0 })
        {
            return own;
        }

        if (!ReferenceEquals(dockControl, DockControl))
        {
            var root = DockTabSwitch.GetBindings(DockControl);
            if (root is { Count: > 0 })
            {
                return root;
            }
        }

        return DefaultBindings;
    }

    internal void UpdateModifierState(Key key, bool pressed)
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
        // #1121: exact modifier equality. Each chord shows ONLY its own gesture set's indices —
        // Alt+Shift no longer lights up the Alt-only labels via a subset (HasFlag-style) match.
        // The aggregate `AreBadgesVisible` is true iff SOME configured binding's modifiers exactly
        // equal the currently held modifiers.
        var visible = false;
        if (_heldModifiers != KeyModifiers.None)
        {
            foreach (var pipeline in _pipelines.Values)
            {
                foreach (var binding in GetEffectiveBindings(pipeline.DockControl))
                {
                    if (binding.Modifiers != KeyModifiers.None && binding.Modifiers == _heldModifiers)
                    {
                        visible = true;
                        break;
                    }
                }

                if (visible)
                {
                    break;
                }
            }
        }

        AreBadgesVisible = visible;

        // Push held-modifier state to every pipeline so each label decides its own visibility from
        // its own gesture set (per-label exact match — fixes the multi-label superset regression).
        foreach (var pipeline in _pipelines.Values)
        {
            pipeline.RefreshLabelVisibility(_heldModifiers);
        }
    }

    /// <summary>
    /// Test/theme helper: whether <paramref name="labelModifiers"/> should currently show, given the
    /// held modifier state. A label is visible iff its gesture set's modifiers are non-empty and
    /// exactly equal the held modifier chord (#1121).
    /// </summary>
    internal static bool IsLabelVisibleFor(KeyModifiers held, KeyModifiers labelModifiers) =>
        labelModifiers != KeyModifiers.None && held == labelModifiers;

    /// <summary>Test seam: the currently held modifier chord.</summary>
    internal KeyModifiers HeldModifiersForTest => _heldModifiers;

    private static KeyModifiers ModifierFor(Key key) => key switch
    {
        Key.LeftAlt or Key.RightAlt => KeyModifiers.Alt,
        Key.LeftCtrl or Key.RightCtrl => KeyModifiers.Control,
        Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
        Key.LWin or Key.RWin => KeyModifiers.Meta,
        _ => KeyModifiers.None,
    };

    /// <summary>
    /// The per-<c>DockControl</c> gesture/badge pipeline (design §4.1/§4.4/§4.5). One instance is attached
    /// to the root <c>DockControl</c> and to each floating <c>DockControl</c> that appears in the factory
    /// registry; all instances share the owning controller's modifier/badge state and root-resolved
    /// bindings, but resolve ordering and discover strips against their own <c>DockControl.Layout</c> and
    /// visual tree — so numbering/gestures/badges work independently inside each floating window.
    /// </summary>
    private sealed class Pipeline
    {
        private readonly DockTabSwitchController _owner;
        private readonly DockControl _dockControl;
        private readonly DockTabOrder _order = new();
        private readonly Dictionary<Control, DockTabIndexContext> _containers = new();
        private readonly HashSet<DocumentTabStrip> _hookedStrips = new();

        private bool _attached;
        private bool _suppressedInControlHandlers;

        public Pipeline(DockTabSwitchController owner, DockControl dockControl)
        {
            _owner = owner;
            _dockControl = dockControl;
        }

        /// <summary>The <see cref="DockControl"/> this pipeline is attached to.</summary>
        public DockControl DockControl => _dockControl;

        /// <summary>Test hook (#1124): whether Attach skipped installing the in-control tunnel handlers.</summary>
        public bool SuppressedInControlHandlersForTest => _suppressedInControlHandlers;

        public void Attach()
        {
            if (_attached)
            {
                return;
            }

            _attached = true;

            // #1124: when the owner is top-level-sourced AND this pipeline is the root pipeline
            // (i.e. this pipeline's DockControl is the opted-in DockControl), suppress the
            // in-control tunnel AddHandler calls — the TopLevel is now the sole source, so
            // ProcessKeyDown / ProcessKeyUp must run exactly once per physical key event.
            var suppressInControlHandlers =
                _owner._sourcedFromTopLevel && ReferenceEquals(_dockControl, _owner.DockControl);

            if (!suppressInControlHandlers)
            {
                // Tunnel so the DockControl sees the gesture before a focused editor/child swallows it — the
                // same routing strategy Dock uses for its own document selector.
                _dockControl.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
                _dockControl.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
            }

            _suppressedInControlHandlers = suppressInControlHandlers;

            // Discover tab strips and (re)apply the per-container badge context as the layout materializes.
            _dockControl.LayoutUpdated += OnLayoutUpdated;
            DiscoverStrips();
        }

        public void Detach()
        {
            if (!_attached)
            {
                return;
            }

            _attached = false;

            _dockControl.LayoutUpdated -= OnLayoutUpdated;

            foreach (var strip in _hookedStrips.ToList())
            {
                UnhookStrip(strip);
            }

            _hookedStrips.Clear();

            foreach (var container in _containers.Keys.ToList())
            {
                ClearContainer(container);
            }

            _containers.Clear();

            if (!_suppressedInControlHandlers)
            {
                _dockControl.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
                _dockControl.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
            }

            _suppressedInControlHandlers = false;
        }

        /// <summary>
        /// Recomputes per-label visibility from the currently held modifier chord (#1121). Each label
        /// is visible iff its gesture set's modifiers exactly equal <paramref name="held"/>, so a tab
        /// numbered by both Alt and Alt+Shift shows only the label whose chord is held. The
        /// context-level aggregate <see cref="DockTabIndexContext.IsVisible"/> is set to "any label
        /// visible on this container" for consumers that only care about the on/off aggregate.
        /// </summary>
        public void RefreshLabelVisibility(KeyModifiers held)
        {
            foreach (var context in _containers.Values)
            {
                var anyVisible = false;
                foreach (var label in context.Labels)
                {
                    var labelVisible = IsLabelVisibleFor(held, label.GestureSet.Modifiers);
                    label.IsVisible = labelVisible;
                    if (labelVisible)
                    {
                        anyVisible = true;
                    }
                }

                context.IsVisible = anyVisible;
            }
        }

        private void OnKeyDown(object? sender, KeyEventArgs e) => ProcessKeyDown(e);

        private void OnKeyUp(object? sender, KeyEventArgs e) => ProcessKeyUp(e);

        // --- Automatic composable header integration (design §4.4) -------------------------------

        private void OnLayoutUpdated(object? sender, EventArgs e) => DiscoverStrips();

        /// <summary>
        /// Discovers every realized <see cref="DocumentTabStrip"/> under this <c>DockControl</c> and, for
        /// each not yet hooked, subscribes to its container lifecycle events and prepares its already
        /// realized containers. Called on <c>LayoutUpdated</c> as strips materialize.
        /// </summary>
        public void DiscoverStrips()
        {
            foreach (var strip in _dockControl.GetVisualDescendants().OfType<DocumentTabStrip>())
            {
                // #1344: A DockControl's visual descendants include the strips of any nested inner
                // DockControl (each WorkspacePaneDocument hosts its own). Only hook strips this
                // pipeline actually owns — those whose model, walked up .Owner, reaches this
                // DockControl.Layout — so the outer pipeline never reaches into a nested inner
                // pipeline's strips (which would race for the single per-container IndexContext).
                if (!StripBelongsToDockControl(strip))
                {
                    continue;
                }

                if (!_hookedStrips.Add(strip))
                {
                    continue;
                }

                strip.ContainerPrepared += OnContainerPrepared;
                strip.ContainerClearing += OnContainerClearing;
                strip.ContainerIndexChanged += OnContainerIndexChanged;

                foreach (var container in strip.GetRealizedContainers())
                {
                    PrepareContainer(container);
                }
            }
        }

        private void UnhookStrip(DocumentTabStrip strip)
        {
            strip.ContainerPrepared -= OnContainerPrepared;
            strip.ContainerClearing -= OnContainerClearing;
            strip.ContainerIndexChanged -= OnContainerIndexChanged;
        }

        internal bool IsStripHooked(DocumentTabStrip strip) => _hookedStrips.Contains(strip);

        private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        {
            PrepareContainer(e.Container);
            RefreshAllLabels();
        }

        private void OnContainerClearing(object? sender, ContainerClearingEventArgs e)
        {
            ClearContainer(e.Container);
            RefreshAllLabels();
        }

        private void OnContainerIndexChanged(object? sender, ContainerIndexChangedEventArgs e) => RefreshAllLabels();

        /// <summary>
        /// Attaches the controller-owned <see cref="DockTabIndexContext"/> to a realized tab-header container
        /// and composes the badge onto it. Badge injection is deferred to the container's <c>Loaded</c>
        /// callback so the visual tree is never mutated inside a layout pass.
        /// </summary>
        public void PrepareContainer(Control container)
        {
            if (_containers.TryGetValue(container, out var existing))
            {
                RefreshLabels(container, existing);
                ScheduleInject(container);
                return;
            }

            var context = new DockTabIndexContext();
            _containers[container] = context;
            DockTabSwitch.SetIndexContext(container, context);
            RefreshLabels(container, context);

            container.Loaded += OnContainerLoaded;
            if (container.IsLoaded)
            {
                ScheduleInject(container);
            }
        }

        private void ScheduleInject(Control container) =>
            Dispatcher.UIThread.Post(() => InjectBadge(container));

        private void OnContainerLoaded(object? sender, RoutedEventArgs e)
        {
            if (sender is Control container)
            {
                InjectBadge(container);
            }
        }

        /// <summary>Composes (or refreshes) the badge on <paramref name="container"/>. Idempotent.</summary>
        public void InjectBadge(Control container)
        {
            if (!_containers.TryGetValue(container, out var context))
            {
                return;
            }

            DockIndexBadgeBehavior.Attach(container, context, DockTabSwitch.GetComposition(container));
        }

        /// <summary>Removes the badge and clears the context so a recycled container shows no stale number.</summary>
        public void ClearContainer(Control container)
        {
            container.Loaded -= OnContainerLoaded;
            DockIndexBadgeBehavior.Detach(container);
            DockTabSwitch.SetIndexContext(container, null);
            _containers.Remove(container);
        }

        private void RefreshAllLabels()
        {
            foreach (var pair in _containers)
            {
                RefreshLabels(pair.Key, pair.Value);
            }
        }

        /// <summary>
        /// Recomputes the per-binding labels for <paramref name="container"/>'s dockable from the shared
        /// ordering (§4.2/§4.5), so display and activation never diverge.
        /// </summary>
        private void RefreshLabels(Control container, DockTabIndexContext context)
        {
            var labels = new List<DockTabIndexLabel>();
            var primaryIndex = 0;

            if (container.DataContext is IDockable dockable)
            {
                foreach (var binding in _owner.GetEffectiveBindings(_dockControl))
                {
                    var order = ComputeOrder(binding);
                    var index = IndexOf(order, dockable);
                    if (index < 0)
                    {
                        continue;
                    }

                    var map = binding.BuildMap();
                    if (index >= map.Count)
                    {
                        continue;
                    }

                    if (labels.Count == 0)
                    {
                        primaryIndex = index;
                    }

                    labels.Add(new DockTabIndexLabel(LabelText(map.Gestures[index].Key), binding));
                }
            }

            if (!LabelsEqual(context.Labels, labels))
            {
                context.Labels = labels;
            }

            context.Index = primaryIndex;

            // Seed per-label visibility from current held-modifier state (#1121). Also update the
            // aggregate context.IsVisible to "any label visible" so single-boolean consumers
            // (and tests reading AreBadgesVisible) still see the right value.
            var held = _owner._heldModifiers;
            var anyVisible = false;
            foreach (var label in context.Labels)
            {
                var labelVisible = IsLabelVisibleFor(held, label.GestureSet.Modifiers);
                label.IsVisible = labelVisible;
                if (labelVisible)
                {
                    anyVisible = true;
                }
            }

            context.IsVisible = anyVisible;
        }

        /// <summary>
        /// Core tunnel <c>KeyDown</c> logic (exposed for tests). Updates the shared held-modifier state,
        /// then tries each configured gesture set; on the first exact match it activates the indexed
        /// dockable and marks the event handled.
        /// </summary>
        public void ProcessKeyDown(KeyEventArgs e)
        {
            _owner.UpdateModifierState(e.Key, pressed: true);
            ProcessActivation(e);
        }

        /// <summary>
        /// The activation half of <see cref="ProcessKeyDown"/> (no modifier tracking, #1332): tries each
        /// configured gesture set and, on the first exact match, activates the indexed dockable and marks
        /// the event handled. The coordinator calls this on exactly one region while tracking modifiers
        /// on all of them.
        /// </summary>
        public void ProcessActivation(KeyEventArgs e)
        {
            foreach (var binding in _owner.GetEffectiveBindings(_dockControl))
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
        public void ProcessKeyUp(KeyEventArgs e) => _owner.UpdateModifierState(e.Key, pressed: false);

        private bool Activate(DockTabSwitchGestures binding, int index)
        {
            var order = ComputeOrder(binding);
            if (index < 0 || index >= order.Count)
            {
                return false;
            }

            var entry = order[index];
            var factory = entry.Dockable.Factory;
            factory?.SetActiveDockable(entry.Dockable);
            factory?.SetFocusedDockable(entry.Strip, entry.Dockable);
            return true;
        }

        /// <summary>
        /// Computes the ordered <c>(strip, dockable)</c> activation list for <paramref name="binding"/> from
        /// this pipeline's own <c>DockControl.Layout</c> (design §4.2/§4.5), so a floating window resolves
        /// its scope against its own root. The binding's <see cref="DockTabSwitchGestures.Scope"/> resolves
        /// the ordering root and the inherited <c>IsSwitchable</c> opt-out filters strips.
        /// </summary>
        public IReadOnlyList<DockTabEntry> ComputeOrder(DockTabSwitchGestures binding)
        {
            var scopeRoot = DockTabScopeResolver.ResolveScopeRoot(_dockControl.Layout, binding.Scope);
            return _order.Compute(scopeRoot, IsStripSwitchable);
        }

        /// <summary>
        /// The inherited <c>IsSwitchable</c> opt-out (§4.2), resolved against the realized
        /// <see cref="DocumentTabStrip"/> control for <paramref name="strip"/> so a value on the strip (or
        /// any ancestor) cascades.
        /// </summary>
        private bool IsStripSwitchable(IDock strip)
        {
            var control = FindStripControl(strip);
            return control is null || DockTabSwitch.GetIsSwitchable(control);
        }

        private Control? FindStripControl(IDock strip) =>
            _dockControl.GetVisualDescendants()
                .OfType<DocumentTabStrip>()
                .Where(StripBelongsToDockControl)
                .FirstOrDefault(ts => ReferenceEquals(ts.DataContext, strip));

        /// <summary>
        /// #1344: Model-level ownership test. A <see cref="DocumentTabStrip"/> belongs to this pipeline's
        /// <c>DockControl</c> iff its bound model (its <c>DataContext</c>, an <see cref="IDockable"/>),
        /// walked up the <see cref="IDockable.Owner"/> chain, reaches this <c>DockControl.Layout</c>
        /// (the pipeline's <see cref="IRootDock"/>). This is the same source of truth used by
        /// <see cref="ComputeOrder"/> and <c>DockTabScopeResolver.OwningDock</c>, and correctly excludes
        /// strips owned by a nested inner <c>DockControl</c> (they chain to the inner root) while a
        /// floating window's strips are claimed by the floating <c>DockControl</c>'s own pipeline. An
        /// unbound <c>DataContext</c>, a transient null <c>Owner</c>, or a null <c>Layout</c> returns
        /// false; discovery is retried on <c>LayoutUpdated</c> once the model settles.
        /// </summary>
        internal bool StripBelongsToDockControl(DocumentTabStrip strip)
        {
            if (strip.DataContext is not IDockable dockable)
            {
                return false;
            }

            var root = _dockControl.Layout;
            if (root is null)
            {
                return false;
            }

            for (IDockable? d = dockable; d is not null; d = d.Owner)
            {
                if (ReferenceEquals(d, root))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool LabelsEqual(IReadOnlyList<DockTabIndexLabel> current, IReadOnlyList<DockTabIndexLabel> next)
        {
            if (current.Count != next.Count)
            {
                return false;
            }

            for (var i = 0; i < current.Count; i++)
            {
                if (!string.Equals(current[i].Text, next[i].Text, StringComparison.Ordinal) ||
                    !ReferenceEquals(current[i].GestureSet, next[i].GestureSet))
                {
                    return false;
                }
            }

            return true;
        }

        private static int IndexOf(IReadOnlyList<DockTabEntry> order, IDockable dockable)
        {
            for (var i = 0; i < order.Count; i++)
            {
                if (ReferenceEquals(order[i].Dockable, dockable))
                {
                    return i;
                }
            }

            return -1;
        }

        private static string LabelText(Key key) => key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            >= Key.F1 and <= Key.F24 => "F" + ((int)(key - Key.F1) + 1),
            _ => key.ToString(),
        };
    }
}
