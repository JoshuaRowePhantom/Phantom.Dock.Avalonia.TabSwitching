using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace Phantom.Dock.Avalonia.TabSwitching;

/// <summary>
/// The header-composition strategy for the index badge (design §4.4).
/// </summary>
public enum DockTabSwitchComposition
{
    /// <summary>
    /// Strategy A (default): compose a sibling <c>PART_IndexBadgePresenter</c> into the tab-header host
    /// (<c>PART_HeaderHost</c>), leaving the header/icon/modified/close presenters untouched.
    /// </summary>
    ContentPresenter,

    /// <summary>
    /// Strategy B: overlay the badge via the <see cref="AdornerLayer"/> with no container-theme override.
    /// </summary>
    Adorner,
}

/// <summary>
/// Per-<c>DocumentTabStripItem</c> badge adornment helper (design §4.4). Composes the index badge onto a
/// realized tab-header container using the selected <see cref="DockTabSwitchComposition"/> strategy —
/// either a sibling <see cref="ContentPresenter"/> injected into <c>PART_HeaderHost</c> (Strategy A,
/// composition not replacement) or an <see cref="AdornerLayer"/> overlay (Strategy B). All operations
/// are idempotent so repeated layout/prepare passes never duplicate the badge.
/// </summary>
internal static class DockIndexBadgeBehavior
{
    /// <summary>The name of the injected badge presenter, mirroring the design's Strategy A part.</summary>
    public const string BadgePresenterName = "PART_IndexBadgePresenter";

    private const string HeaderHostName = "PART_HeaderHost";

    /// <summary>
    /// Composes the badge for <paramref name="context"/> onto <paramref name="container"/> using
    /// <paramref name="composition"/>. Idempotent.
    /// </summary>
    public static void Attach(Control container, DockTabIndexContext context, DockTabSwitchComposition composition)
    {
        if (composition == DockTabSwitchComposition.Adorner)
        {
            AttachAdorner(container, context);
        }
        else
        {
            AttachContentPresenter(container, context);
        }
    }

    /// <summary>Removes any badge (either strategy) from <paramref name="container"/>. Idempotent.</summary>
    public static void Detach(Control container)
    {
        DetachContentPresenter(container);
        DetachAdorner(container);
    }

    /// <summary>Finds the injected sibling badge presenter (Strategy A), or <c>null</c>.</summary>
    public static ContentPresenter? FindContentPresenter(Control container)
    {
        var host = FindHeaderHost(container);
        return host?.Children.OfType<ContentPresenter>().FirstOrDefault(c => c.Name == BadgePresenterName);
    }

    /// <summary>Finds the adorner badge presenter (Strategy B), or <c>null</c>.</summary>
    public static ContentPresenter? FindAdorner(Control container) =>
        AdornerLayer.GetAdorner(container) as ContentPresenter;

    private static void AttachContentPresenter(Control container, DockTabIndexContext context)
    {
        var host = FindHeaderHost(container);
        if (host is null)
        {
            // Template not applied yet; the caller retries once the container is loaded.
            return;
        }

        var existing = host.Children.OfType<ContentPresenter>().FirstOrDefault(c => c.Name == BadgePresenterName);
        if (existing is not null)
        {
            existing.Theme = DockTabSwitch.GetEffectiveIndexTheme(container);
            existing.Content = context;
            return;
        }

        host.Children.Add(new ContentPresenter
        {
            Name = BadgePresenterName,
            Theme = DockTabSwitch.GetEffectiveIndexTheme(container),
            Content = context,
        });
    }

    private static void DetachContentPresenter(Control container)
    {
        var host = FindHeaderHost(container);
        var existing = host?.Children.OfType<ContentPresenter>().FirstOrDefault(c => c.Name == BadgePresenterName);
        if (host is not null && existing is not null)
        {
            host.Children.Remove(existing);
        }
    }

    private static void AttachAdorner(Control container, DockTabIndexContext context)
    {
        if (AdornerLayer.GetAdorner(container) is ContentPresenter existing && existing.Name == BadgePresenterName)
        {
            existing.Theme = DockTabSwitch.GetEffectiveIndexTheme(container);
            existing.Content = context;
            return;
        }

        // SetAdorner tracks the container's visual-tree attachment and adds/removes the overlay from the
        // adorner layer automatically, so the badge overlays the header with no container-theme override.
        AdornerLayer.SetAdorner(container, new ContentPresenter
        {
            Name = BadgePresenterName,
            Theme = DockTabSwitch.GetEffectiveIndexTheme(container),
            Content = context,
        });
    }

    private static void DetachAdorner(Control container)
    {
        if (AdornerLayer.GetAdorner(container) is ContentPresenter existing && existing.Name == BadgePresenterName)
        {
            AdornerLayer.SetAdorner(container, null);
        }
    }

    private static Panel? FindHeaderHost(Control container) =>
        container.GetVisualDescendants().OfType<Panel>().FirstOrDefault(p => p.Name == HeaderHostName);
}
