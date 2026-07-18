using System.Linq;
using Avalonia.Collections;
using Dock.Model.Core;
using DockDocument = Dock.Model.Avalonia.Controls.Document;
using DockDocumentDock = Dock.Model.Avalonia.Controls.DocumentDock;
using DockProportionalDock = Dock.Model.Avalonia.Controls.ProportionalDock;
using DockRootDock = Dock.Model.Avalonia.Controls.RootDock;
using Xunit;

namespace Phantom.Dock.Avalonia.TabSwitching.Tests;

/// <summary>
/// Covers the shared <see cref="DockTabOrder"/> ordering service (commit 4 of the #1073 epic, design
/// §4.2/§4.5): numbering follows <see cref="IDock.VisibleDockables"/> in visual order (including split
/// strips), the same order feeds both label display and activation (regression guard for the #1067
/// divergence), and a reorder/close is reflected live with no flat projection.
/// </summary>
public sealed class DockTabOrderTests
{
    private static DockDocument Doc(string id, IDock owner)
    {
        var document = new DockDocument { Id = id, Title = id, Owner = owner };
        return document;
    }

    private static DockDocumentDock Strip(IDock owner, params DockDocument[] docs)
    {
        var strip = new DockDocumentDock { Owner = owner };
        strip.VisibleDockables = new AvaloniaList<IDockable>(docs);
        foreach (var doc in docs)
        {
            doc.Owner = strip;
        }

        return strip;
    }

    [Fact]
    public void Compute_VisibleDockables_YieldsVisualOrder()
    {
        // A split layout: root → proportional dock → [strip A (a1,a2), strip B (b1)] in visual order.
        var root = new DockRootDock();
        var split = new DockProportionalDock { Owner = root };

        var a1 = Doc("a1", null!);
        var a2 = Doc("a2", null!);
        var b1 = Doc("b1", null!);
        var stripA = Strip(split, a1, a2);
        var stripB = Strip(split, b1);

        split.VisibleDockables = new AvaloniaList<IDockable> { stripA, stripB };
        root.VisibleDockables = new AvaloniaList<IDockable> { split };

        var order = new DockTabOrder().Compute(root);

        Assert.Equal(new IDockable[] { a1, a2, b1 }, order.Select(e => e.Dockable).ToArray());

        // Each leaf is paired with its own owning strip, not the split or the root.
        Assert.Same(stripA, order[0].Strip);
        Assert.Same(stripA, order[1].Strip);
        Assert.Same(stripB, order[2].Strip);
    }

    [Fact]
    public void Compute_LabelAndActivation_ResolveSameDockableForIndex()
    {
        var root = new DockRootDock();
        var a1 = Doc("a1", null!);
        var a2 = Doc("a2", null!);
        var strip = Strip(root, a1, a2);
        root.VisibleDockables = new AvaloniaList<IDockable> { strip };

        var service = new DockTabOrder();

        // The SAME service instance is what both display and activation consume; two computations over
        // the identical structure must agree entry-for-entry (the #1067 no-divergence guarantee).
        var forLabels = service.Compute(root);
        var forActivation = service.Compute(root);

        Assert.Equal(forLabels.Count, forActivation.Count);
        for (var i = 0; i < forLabels.Count; i++)
        {
            Assert.Same(forLabels[i].Dockable, forActivation[i].Dockable);
            Assert.Same(forLabels[i].Strip, forActivation[i].Strip);
        }
    }

    [Fact]
    public void Compute_AfterReorderOrClose_ReflectsNewOrderWithoutFlatProjection()
    {
        var root = new DockRootDock();
        var a1 = Doc("a1", null!);
        var a2 = Doc("a2", null!);
        var a3 = Doc("a3", null!);
        var strip = Strip(root, a1, a2, a3);
        root.VisibleDockables = new AvaloniaList<IDockable> { strip };

        var service = new DockTabOrder();

        Assert.Equal(new IDockable[] { a1, a2, a3 }, service.Compute(root).Select(e => e.Dockable).ToArray());

        // Reorder in VisibleDockables (e.g. #1065 insert-to-the-right) — recompute reflects it live.
        strip.VisibleDockables!.Remove(a3);
        strip.VisibleDockables!.Insert(0, a3); // a3 to the front
        Assert.Equal(new IDockable[] { a3, a1, a2 }, service.Compute(root).Select(e => e.Dockable).ToArray());

        // Close a tab — recompute drops it, still derived straight from VisibleDockables.
        strip.VisibleDockables!.Remove(a1);
        Assert.Equal(new IDockable[] { a3, a2 }, service.Compute(root).Select(e => e.Dockable).ToArray());
    }
}
