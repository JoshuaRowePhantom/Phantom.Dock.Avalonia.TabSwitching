using System;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Phantom.Dock.Avalonia.TabSwitching;

/// <summary>
/// The default theme dictionary for the Dock Tab-Switching API. Consumers merge an instance into
/// their <c>Application.Styles</c> to pick up the default index-badge visuals (the replaceable
/// <c>IndexTheme</c> fallback, design §4.3). The <c>DocumentTabStripItem</c> composition theme
/// (Strategy A) is supplied by a later commit in the #1073 epic.
/// </summary>
public partial class DockTabSwitchTheme : Styles
{
    private const string DefaultIndexThemeKey = "DockTabSwitchDefaultIndexTheme";

    private static ControlTheme? _defaultIndexTheme;

    public DockTabSwitchTheme()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// The packaged default index-badge <see cref="ControlTheme"/> — the effective value of
    /// <see cref="DockTabSwitch.IndexThemeProperty"/> when it is left unset (design §4.3). Loaded once
    /// from this theme dictionary because an attached-property <c>defaultValue</c> cannot reference a
    /// resource directly.
    /// </summary>
    public static ControlTheme DefaultIndexTheme =>
        _defaultIndexTheme ??= LoadDefaultIndexTheme();

    private static ControlTheme LoadDefaultIndexTheme()
    {
        var theme = new DockTabSwitchTheme();
        if (theme.TryGetResource(DefaultIndexThemeKey, null, out var resource) &&
            resource is ControlTheme controlTheme)
        {
            return controlTheme;
        }

        throw new InvalidOperationException(
            $"The default index-badge ControlTheme '{DefaultIndexThemeKey}' is missing from the theme dictionary.");
    }
}
