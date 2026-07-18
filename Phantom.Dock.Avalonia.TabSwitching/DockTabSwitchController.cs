using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
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

        _rootPipeline = AttachPipeline(DockControl);
        SubscribeToRegistry();
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

    internal void ClearContainer(Control container) => _rootPipeline?.ClearContainer(container);

    internal IReadOnlyList<DockTabEntry> ComputeOrder(DockTabSwitchGestures binding) =>
        _rootPipeline?.ComputeOrder(binding) ?? Array.Empty<DockTabEntry>();

    // --- Shared state used by every pipeline -----------------------------------------------------

    /// <summary>
    /// The effective gesture-set bindings for the whole controller, resolved from the <b>root</b>
    /// <see cref="DockControl"/> so floating windows use the same configuration.
    /// </summary>
    internal IReadOnlyList<DockTabSwitchGestures> GetEffectiveBindings()
    {
        var bindings = DockTabSwitch.GetBindings(DockControl);
        return bindings is { Count: > 0 } ? bindings : DefaultBindings;
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

        // Fade every realized badge across every attached DockControl in/out with the held modifier.
        foreach (var pipeline in _pipelines.Values)
        {
            pipeline.SetBadgeVisibility(visible);
        }
    }

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

        public Pipeline(DockTabSwitchController owner, DockControl dockControl)
        {
            _owner = owner;
            _dockControl = dockControl;
        }

        public void Attach()
        {
            if (_attached)
            {
                return;
            }

            _attached = true;

            // Tunnel so the DockControl sees the gesture before a focused editor/child swallows it — the
            // same routing strategy Dock uses for its own document selector.
            _dockControl.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            _dockControl.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);

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

            _dockControl.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            _dockControl.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
        }

        /// <summary>Fades every realized badge on this control in/out with the shared held modifier.</summary>
        public void SetBadgeVisibility(bool visible)
        {
            foreach (var context in _containers.Values)
            {
                context.IsVisible = visible;
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
                foreach (var binding in _owner.GetEffectiveBindings())
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
            context.IsVisible = _owner.AreBadgesVisible;
        }

        /// <summary>
        /// Core tunnel <c>KeyDown</c> logic (exposed for tests). Updates the shared held-modifier state,
        /// then tries each configured gesture set; on the first exact match it activates the indexed
        /// dockable and marks the event handled.
        /// </summary>
        public void ProcessKeyDown(KeyEventArgs e)
        {
            _owner.UpdateModifierState(e.Key, pressed: true);

            foreach (var binding in _owner.GetEffectiveBindings())
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
                .FirstOrDefault(ts => ReferenceEquals(ts.DataContext, strip));

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
