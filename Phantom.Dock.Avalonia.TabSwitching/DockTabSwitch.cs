using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Dock.Avalonia.Controls;

namespace Phantom.Dock.Avalonia.TabSwitching;

/// <summary>
/// Static attached-property host for the Dock Tab-Switching API (design §4). Setting
/// <see cref="EnabledProperty"/> to <c>true</c> on a <c>DockControl</c> installs a
/// <see cref="DockTabSwitchController"/>; clearing it (or setting <c>false</c>) detaches and disposes
/// the controller. Follows the in-box <c>HotKeyManager</c> attached-property-drives-behavior pattern
/// and the <c>ToolTip</c> inherited-attached-property precedent — no view-model involvement.
/// </summary>
public static class DockTabSwitch
{
    /// <summary>
    /// When <c>true</c> on a <c>DockControl</c>, installs a <see cref="DockTabSwitchController"/>.
    /// </summary>
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Enabled", typeof(DockTabSwitch));

    /// <summary>
    /// The collection of gesture-set + scope bindings for this <c>DockControl</c> (populated in the
    /// gesture commit; registered here so consumers can bind it).
    /// </summary>
    public static readonly AttachedProperty<DockTabSwitchBindings?> BindingsProperty =
        AvaloniaProperty.RegisterAttached<Control, DockTabSwitchBindings?>("Bindings", typeof(DockTabSwitch));

    /// <summary>
    /// Per-strip opt-out. Inherited and defaults <c>true</c>, so a value on the <c>DockControl</c>
    /// (or any ancestor) cascades but can be overridden lower down (the <c>ToolTip</c> precedent).
    /// </summary>
    public static readonly AttachedProperty<bool> IsSwitchableProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>(
            "IsSwitchable", typeof(DockTabSwitch), defaultValue: true, inherits: true);

    /// <summary>
    /// The replaceable index-badge <see cref="ControlTheme"/>. Inherited so a Dock-level default
    /// flows to all strips but any strip can override it. Default supplied in a later commit.
    /// </summary>
    public static readonly AttachedProperty<ControlTheme?> IndexThemeProperty =
        AvaloniaProperty.RegisterAttached<Control, ControlTheme?>(
            "IndexTheme", typeof(DockTabSwitch), inherits: true);

    /// <summary>
    /// Private storage for the controller instance on the host, so <see cref="EnabledProperty"/>
    /// toggles are idempotent (never double-attach or leak).
    /// </summary>
    private static readonly AttachedProperty<DockTabSwitchController?> ControllerProperty =
        AvaloniaProperty.RegisterAttached<Control, DockTabSwitchController?>("Controller", typeof(DockTabSwitch));

    static DockTabSwitch()
    {
        EnabledProperty.Changed.AddClassHandler<Control>(OnEnabledChanged);
    }

    public static void SetEnabled(Control control, bool value) => control.SetValue(EnabledProperty, value);

    public static bool GetEnabled(Control control) => control.GetValue(EnabledProperty);

    public static void SetBindings(Control control, DockTabSwitchBindings? value) =>
        control.SetValue(BindingsProperty, value);

    public static DockTabSwitchBindings? GetBindings(Control control) => control.GetValue(BindingsProperty);

    public static void SetIsSwitchable(Control control, bool value) =>
        control.SetValue(IsSwitchableProperty, value);

    public static bool GetIsSwitchable(Control control) => control.GetValue(IsSwitchableProperty);

    public static void SetIndexTheme(Control control, ControlTheme? value) =>
        control.SetValue(IndexThemeProperty, value);

    public static ControlTheme? GetIndexTheme(Control control) => control.GetValue(IndexThemeProperty);

    /// <summary>The controller installed on <paramref name="control"/>, or <c>null</c> if not enabled.</summary>
    public static DockTabSwitchController? GetController(Control control) =>
        control.GetValue(ControllerProperty);

    private static void OnEnabledChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        var enabled = e.GetNewValue<bool>();
        var existing = control.GetValue(ControllerProperty);

        if (enabled)
        {
            // The API targets a DockControl; ignore the property on any other control type.
            if (control is not DockControl dockControl)
            {
                return;
            }

            if (existing is not null)
            {
                return;
            }

            var controller = new DockTabSwitchController(dockControl);
            control.SetValue(ControllerProperty, controller);
            controller.Attach();
        }
        else
        {
            if (existing is null)
            {
                return;
            }

            control.SetValue(ControllerProperty, null);
            existing.Detach();
            existing.Dispose();
        }
    }
}
