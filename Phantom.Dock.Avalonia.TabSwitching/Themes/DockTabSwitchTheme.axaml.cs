using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Phantom.Dock.Avalonia.TabSwitching;

/// <summary>
/// The default theme dictionary for the Dock Tab-Switching API. Consumers merge an instance into
/// their <c>Application.Styles</c> to pick up the default index-badge visuals and the
/// <c>DocumentTabStripItem</c> composition theme (both supplied by later commits in the #1073 epic).
/// It is intentionally empty at this scaffolding stage but is already safely mergeable.
/// </summary>
public partial class DockTabSwitchTheme : Styles
{
    public DockTabSwitchTheme()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
