using Avalonia.Collections;

namespace Phantom.Dock.Avalonia.TabSwitching;

/// <summary>
/// An ordered collection of <see cref="DockTabSwitchGestures"/> — one entry per gesture → scope
/// binding on a <c>DockControl</c>. Held by the <c>DockTabSwitch.Bindings</c> attached property.
/// </summary>
public sealed class DockTabSwitchBindings : AvaloniaList<DockTabSwitchGestures>
{
}
