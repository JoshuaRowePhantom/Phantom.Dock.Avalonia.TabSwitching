using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Phantom.Dock.Avalonia.TabSwitching.Tests;

/// <summary>
/// Headless UI coverage for the default index-badge <see cref="Avalonia.Styling.ControlTheme"/>
/// (design §4.3): labels hidden until the activation modifier is held, multiple labels overlapping in a
/// single cell (not side-by-side), and the cell reserving width for the largest label.
/// </summary>
public sealed class DockDefaultBadgeThemeTests
{
    private static DockTabIndexLabel MakeLabel(string text) =>
        new(text, new DockTabSwitchGestures());

    private static DockTabIndexContext Context(bool isVisible, params string[] labels) => new()
    {
        IsVisible = isVisible,
        Labels = labels.Select(MakeLabel).ToList(),
    };

    /// <summary>
    /// Realizes a <see cref="ContentPresenter"/> with the default index theme applied and the given
    /// context, returns the badge borders, the overlapping items panel, and keeps the window alive.
    /// </summary>
    private static (IReadOnlyList<Border> Badges, Panel Panel, Window Window) Realize(DockTabIndexContext context)
    {
        var presenter = new ContentPresenter
        {
            Content = context,
            Theme = DockTabSwitchTheme.DefaultIndexTheme,
        };

        var window = new Window
        {
            Width = 400,
            Height = 300,
            Styles = { new DockTabSwitchTheme() },
            Content = presenter,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var items = presenter.GetVisualDescendants().OfType<ItemsControl>().Single();
        var panel = items.GetVisualDescendants().OfType<Panel>().First(p => p.GetType() == typeof(Panel));
        var badges = presenter.GetVisualDescendants()
            .OfType<Border>()
            .Where(b => b.Classes.Contains("alt-index-badge"))
            .ToList();

        return (badges, panel, window);
    }

    [AvaloniaFact]
    public void DefaultTheme_NoModifierHeld_HidesLabels()
    {
        var (badges, _, _) = Realize(Context(isVisible: false, "1"));

        var badge = Assert.Single(badges);

        // No activation modifier held ⇒ badge is not raised (base opacity 0, alt-held class absent).
        Assert.DoesNotContain("alt-held", badge.Classes);
        Assert.Equal(0d, badge.Opacity);
    }

    [AvaloniaFact]
    public void DefaultTheme_ModifierHeld_FadesLabelsIn()
    {
        var (badges, _, _) = Realize(Context(isVisible: true, "1"));

        var badge = Assert.Single(badges);

        // Holding the activation modifier flips the context's IsVisible, applying the alt-held class
        // whose style animates the opacity to 1.
        Assert.Contains("alt-held", badge.Classes);
    }

    [AvaloniaFact]
    public void DefaultTheme_MultipleLabels_OverlapInSingleCell()
    {
        var (badges, panel, _) = Realize(Context(isVisible: true, "1", "F1"));

        Assert.Equal(2, badges.Count);

        // A single-cell Panel (not a StackPanel) hosts the labels, so they stack in one spot rather
        // than being laid out side-by-side.
        Assert.Equal(typeof(Panel), panel.GetType());
        Assert.Equal(2, panel.Children.Count);

        // Both item containers occupy the same rect (overlap), not adjacent columns.
        var first = (Visual)panel.Children[0];
        var second = (Visual)panel.Children[1];
        Assert.Equal(first.Bounds, second.Bounds);
    }

    [AvaloniaFact]
    public void DefaultTheme_MixedWidthLabels_ReservesWidthForLargest()
    {
        var (_, onePanel, _) = Realize(Context(isVisible: true, "1"));
        var (_, fPanel, _) = Realize(Context(isVisible: true, "F1"));
        var (_, bothPanel, _) = Realize(Context(isVisible: true, "1", "F1"));

        // "F1" is wider than "1".
        Assert.True(fPanel.Bounds.Width > onePanel.Bounds.Width);

        // A tab carrying both reserves width for the largest ("F1"), not the sum.
        Assert.Equal(fPanel.Bounds.Width, bothPanel.Bounds.Width, precision: 3);
    }

    [AvaloniaFact]
    public void DefaultTheme_SingleShortLabel_ReservesOnlyItsWidth()
    {
        var (badges, onePanel, _) = Realize(Context(isVisible: true, "1"));
        var (_, bothPanel, _) = Realize(Context(isVisible: true, "1", "F1"));

        Assert.Single(badges);

        // A single short label reserves only its own width: adding a wider "F1" label grows the cell,
        // so the lone-"1" cell is strictly narrower — no extra space is reserved for absent labels.
        Assert.True(onePanel.Bounds.Width > 0);
        Assert.True(onePanel.Bounds.Width < bothPanel.Bounds.Width);
    }
}
