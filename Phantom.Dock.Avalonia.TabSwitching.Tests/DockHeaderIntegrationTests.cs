using System.Linq;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Avalonia.Themes.Fluent;
using Dock.Model.Core;
using DockDocument = Dock.Model.Avalonia.Controls.Document;
using DockDocumentDock = Dock.Model.Avalonia.Controls.DocumentDock;
using DockFactory = Dock.Model.Avalonia.Factory;
using DockRootDock = Dock.Model.Avalonia.Controls.RootDock;
using Xunit;

namespace Phantom.Dock.Avalonia.TabSwitching.Tests;

/// <summary>
/// Headless UI coverage for automatic, composable header integration on <c>DocumentTabStripItem</c>
/// (commit 6 of the #1073 epic, design §4.4): the controller injects a sibling badge presenter without
/// replacing the header content (Strategy A), binds each badge's <c>Label</c>/<c>IsVisible</c> from the
/// controller-owned context, badges every realized (non-virtualized) header, clears the context on
/// recycle, and can instead overlay via the adorner layer (Strategy B) with no container-theme override.
/// </summary>
public sealed class DockHeaderIntegrationTests
{
    private sealed record Fixture(
        DockControl Dock,
        DockTabSwitchController Controller,
        DocumentTabStrip Strip,
        AvaloniaList<IDockable> Documents,
        Window Window);

    private static Fixture Build(int documentCount, DockTabSwitchComposition? composition = null)
    {
        var factory = new DockFactory();
        var documents = new AvaloniaList<IDockable>();
        var documentDock = new DockDocumentDock { Factory = factory };
        documentDock.VisibleDockables = documents;

        for (var i = 0; i < documentCount; i++)
        {
            var document = new DockDocument { Id = $"doc{i}", Title = $"Doc {i}", Owner = documentDock, Factory = factory };
            documents.Add(document);
        }

        documentDock.ActiveDockable = documents.Count > 0 ? documents[0] : null;

        var root = new DockRootDock { VisibleDockables = new AvaloniaList<IDockable> { documentDock } };
        documentDock.Owner = root;
        root.ActiveDockable = documentDock;
        root.DefaultDockable = documentDock;
        factory.InitLayout(root);

        var dock = new DockControl { Factory = factory, Layout = root };
        if (composition is { } value)
        {
            DockTabSwitch.SetComposition(dock, value);
        }

        DockTabSwitch.SetEnabled(dock, true);
        var controller = DockTabSwitch.GetController(dock)!;

        var window = new Window
        {
            Width = 800,
            Height = 400,
            Styles = { new DockFluentTheme(), new DockTabSwitchTheme() },
            Content = dock,
        };

        window.Show();
        Pump(dock);

        var strip = dock.GetVisualDescendants().OfType<DocumentTabStrip>().First();
        return new Fixture(dock, controller, strip, documents, window);
    }

    private static void Pump(DockControl dock)
    {
        dock.ApplyTemplate();
        dock.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        dock.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static DocumentTabStripItem TabItem(DocumentTabStrip strip, int index)
    {
        var item = Assert.IsType<DocumentTabStripItem>(strip.ContainerFromIndex(index));
        item.ApplyTemplate();
        item.UpdateLayout();
        return item;
    }

    private static ContentPresenter? Presenter(Control container, string name) =>
        container.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(p => p.Name == name);

    [AvaloniaFact]
    public void ContainerPrepared_StrategyA_InjectsBadgePresenterSibling()
    {
        var fixture = Build(3);
        var container = TabItem(fixture.Strip, 0);
        fixture.Controller.InjectBadge(container);

        // The injected sibling exists...
        Assert.NotNull(Presenter(container, DockIndexBadgeBehavior.BadgePresenterName));

        // ...and the real header presenter was NOT replaced (composition, not replacement).
        Assert.NotNull(Presenter(container, "PART_HeaderPresenter"));

        fixture.Window.Close();
    }

    [AvaloniaFact]
    public void ContainerPrepared_BindsLabelAndIsVisibleFromContext()
    {
        var fixture = Build(3);
        var container = TabItem(fixture.Strip, 0);
        fixture.Controller.InjectBadge(container);

        var context = DockTabSwitch.GetIndexContext(container);
        Assert.NotNull(context);

        // Label comes from the shared ordering (first tab → "1"); hidden until the modifier is held.
        Assert.Equal("1", context!.Label);
        Assert.False(context.IsVisible);

        // Holding the activation modifier flips every realized badge's IsVisible via the context.
        fixture.Controller.ProcessKeyDown(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.LeftAlt,
            KeyModifiers = KeyModifiers.Alt,
            Source = fixture.Dock,
        });
        Assert.True(context.IsVisible);

        fixture.Window.Close();
    }

    [AvaloniaFact]
    public void AllTabHeaders_NonVirtualizedStrip_EachHaveBadge()
    {
        var fixture = Build(4);

        for (var i = 0; i < fixture.Documents.Count; i++)
        {
            var container = TabItem(fixture.Strip, i);
            fixture.Controller.InjectBadge(container);
            Assert.NotNull(Presenter(container, DockIndexBadgeBehavior.BadgePresenterName));
        }

        fixture.Window.Close();
    }

    [AvaloniaFact]
    public void ContainerClearing_DetachesContext_NoStaleNumber()
    {
        var fixture = Build(3);
        var container = TabItem(fixture.Strip, 2);
        fixture.Controller.InjectBadge(container);

        Assert.NotNull(DockTabSwitch.GetIndexContext(container));
        Assert.NotNull(Presenter(container, DockIndexBadgeBehavior.BadgePresenterName));

        // Closing the tab recycles/clears the container — no stale badge or context may remain.
        fixture.Documents.RemoveAt(2);
        Pump(fixture.Dock);

        Assert.Null(DockTabSwitch.GetIndexContext(container));
        Assert.Null(Presenter(container, DockIndexBadgeBehavior.BadgePresenterName));

        fixture.Window.Close();
    }

    [AvaloniaFact]
    public void HeaderContent_AfterInjection_IsUnchanged()
    {
        var fixture = Build(2);
        var container = TabItem(fixture.Strip, 0);

        var headerBefore = Presenter(container, "PART_HeaderPresenter");
        var iconBefore = Presenter(container, "PART_IconPresenter");
        var closeBefore = Presenter(container, "PART_ClosePresenter");

        fixture.Controller.InjectBadge(container);

        // The icon/header/close presenters are the SAME instances after injection — untouched.
        Assert.Same(headerBefore, Presenter(container, "PART_HeaderPresenter"));
        Assert.Same(iconBefore, Presenter(container, "PART_IconPresenter"));
        Assert.Same(closeBefore, Presenter(container, "PART_ClosePresenter"));
        Assert.NotNull(headerBefore);

        fixture.Window.Close();
    }

    [AvaloniaFact]
    public void Composition_Adorner_OverlaysBadgeWithoutThemeOverride()
    {
        var fixture = Build(2, DockTabSwitchComposition.Adorner);
        var container = TabItem(fixture.Strip, 0);

        // Adorner strategy is inherited from the DockControl.
        Assert.Equal(DockTabSwitchComposition.Adorner, DockTabSwitch.GetComposition(container));

        fixture.Controller.InjectBadge(container);

        // The badge is overlaid via the adorner layer, adorning the container...
        var adorner = AdornerLayer.GetAdorner(container) as ContentPresenter;
        Assert.NotNull(adorner);
        Assert.Equal(DockIndexBadgeBehavior.BadgePresenterName, adorner!.Name);
        Assert.Same(container, AdornerLayer.GetAdornedElement(adorner));

        // ...and no sibling presenter was injected into the header host (no container-theme override).
        Assert.Null(Presenter(container, DockIndexBadgeBehavior.BadgePresenterName));

        fixture.Window.Close();
    }
}
