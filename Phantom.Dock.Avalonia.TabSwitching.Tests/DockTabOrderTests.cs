using System.Linq;
using Avalonia.Collections;
using Dock.Model.Core;
using DockDocument = Dock.Model.Avalonia.Controls.Document;
using DockDocumentDock = Dock.Model.Avalonia.Controls.DocumentDock;
using DockProportionalDock = Dock.Model.Avalonia.Controls.ProportionalDock;
using DockProportionalDockSplitter = Dock.Model.Avalonia.Controls.ProportionalDockSplitter;
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
    /// <summary>
    /// A minimal non-<see cref="Dock.Model.Controls.IDocument"/>, non-<see cref="IDock"/> leaf dockable.
    /// Represents any dockable kind that renders no badged DocumentTabStripItem, used to prove the #1342
    /// whitelist excludes everything except document leaves.
    /// </summary>
    private sealed class BareDockable : global::Dock.Model.Avalonia.Core.DockableBase
    {
    }

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

    [Fact]
    public void Compute_SplitParentWithSplitter_SkipsSplitterAndYieldsContiguousOrder()
    {
        // #1331: a ProportionalDockSplitter sits between two DocumentDocks in the split parent's
        // VisibleDockables. It is an IDockable but has no tab / no badge — it must not consume an ordinal.
        var root = new DockRootDock();
        var split = new DockProportionalDock { Owner = root };

        var a1 = Doc("a1", null!);
        var a2 = Doc("a2", null!);
        var b1 = Doc("b1", null!);
        var stripA = Strip(split, a1, a2);
        var stripB = Strip(split, b1);
        var splitter = new DockProportionalDockSplitter { Owner = split };

        split.VisibleDockables = new AvaloniaList<IDockable> { stripA, splitter, stripB };
        root.VisibleDockables = new AvaloniaList<IDockable> { split };

        var order = new DockTabOrder().Compute(root);

        // The splitter is skipped: a1, a2, b1 are contiguous with no gap and no splitter entry.
        Assert.Equal(new IDockable[] { a1, a2, b1 }, order.Select(e => e.Dockable).ToArray());
        Assert.DoesNotContain(order, e => ReferenceEquals(e.Dockable, splitter));
    }

    [Fact]
    public void Compute_MultipleSplitters_ProducesNoGapsAcrossRegionBoundaries()
    {
        // Three regions separated by two splitters must number exactly 1..N with no missing ordinal.
        var root = new DockRootDock();
        var split = new DockProportionalDock { Owner = root };

        var a1 = Doc("a1", null!);
        var b1 = Doc("b1", null!);
        var b2 = Doc("b2", null!);
        var c1 = Doc("c1", null!);
        var stripA = Strip(split, a1);
        var stripB = Strip(split, b1, b2);
        var stripC = Strip(split, c1);
        var s1 = new DockProportionalDockSplitter { Owner = split };
        var s2 = new DockProportionalDockSplitter { Owner = split };

        split.VisibleDockables = new AvaloniaList<IDockable> { stripA, s1, stripB, s2, stripC };
        root.VisibleDockables = new AvaloniaList<IDockable> { split };

        var order = new DockTabOrder().Compute(root);

        Assert.Equal(new IDockable[] { a1, b1, b2, c1 }, order.Select(e => e.Dockable).ToArray());
        Assert.DoesNotContain(order, e => ReferenceEquals(e.Dockable, s1) || ReferenceEquals(e.Dockable, s2));
    }

    [Fact]
    public void Compute_ProportionalDockWithSplitterBetweenStrips_DoesNotConsumeOrdinalForSplitter()
    {
        // #1342: a ProportionalDockSplitter interleaved between two strips renders no badge, so the
        // IDocument whitelist must not add it to the order (it consumes no ordinal).
        var root = new DockRootDock();
        var split = new DockProportionalDock { Owner = root };

        var a1 = Doc("a1", null!);
        var b1 = Doc("b1", null!);
        var stripA = Strip(split, a1);
        var stripB = Strip(split, b1);
        var splitter = new DockProportionalDockSplitter { Owner = split };

        split.VisibleDockables = new AvaloniaList<IDockable> { stripA, splitter, stripB };
        root.VisibleDockables = new AvaloniaList<IDockable> { split };

        var order = new DockTabOrder().Compute(root);

        Assert.DoesNotContain(order, e => ReferenceEquals(e.Dockable, splitter));
        Assert.Equal(new IDockable[] { a1, b1 }, order.Select(e => e.Dockable).ToArray());
    }

    [Fact]
    public void Compute_ProportionalDockWithSplitter_YieldsContiguousIndicesAcrossRegions()
    {
        // The whole point of the fix: indices are contiguous 0..N across both regions with no gap at
        // the split boundary, so Alt+<digit> labels read 1,2,3,... uninterrupted.
        var root = new DockRootDock();
        var split = new DockProportionalDock { Owner = root };

        var a1 = Doc("a1", null!);
        var a2 = Doc("a2", null!);
        var b1 = Doc("b1", null!);
        var b2 = Doc("b2", null!);
        var stripA = Strip(split, a1, a2);
        var stripB = Strip(split, b1, b2);
        var splitter = new DockProportionalDockSplitter { Owner = split };

        split.VisibleDockables = new AvaloniaList<IDockable> { stripA, splitter, stripB };
        root.VisibleDockables = new AvaloniaList<IDockable> { split };

        var order = new DockTabOrder().Compute(root);

        Assert.Equal(new IDockable[] { a1, a2, b1, b2 }, order.Select(e => e.Dockable).ToArray());
        // Contiguous: the positions of the second region's first tab immediately follow the first region.
        Assert.Equal(2, order.Select(e => e.Dockable).ToList().IndexOf(b1));
    }

    [Fact]
    public void Compute_MultipleSplittersBetweenStrips_AllSplittersSkipped()
    {
        // Every splitter in a multi-split layout is excluded by the whitelist, regardless of how many.
        var root = new DockRootDock();
        var split = new DockProportionalDock { Owner = root };

        var a1 = Doc("a1", null!);
        var b1 = Doc("b1", null!);
        var c1 = Doc("c1", null!);
        var stripA = Strip(split, a1);
        var stripB = Strip(split, b1);
        var stripC = Strip(split, c1);
        var s1 = new DockProportionalDockSplitter { Owner = split };
        var s2 = new DockProportionalDockSplitter { Owner = split };

        split.VisibleDockables = new AvaloniaList<IDockable> { stripA, s1, stripB, s2, stripC };
        root.VisibleDockables = new AvaloniaList<IDockable> { split };

        var order = new DockTabOrder().Compute(root);

        Assert.Equal(new IDockable[] { a1, b1, c1 }, order.Select(e => e.Dockable).ToArray());
        Assert.DoesNotContain(order, e => e.Dockable is DockProportionalDockSplitter);
    }

    [Fact]
    public void Compute_NonDocumentDockableInStrip_IsNotNumbered()
    {
        // Locks the whitelist: a non-IDocument leaf (a bare IDockable) in a strip renders no badge and
        // must not consume an ordinal. Under the previous "everything that isn't an IDock" catch-all this
        // leaf was numbered; the IDocument whitelist correctly excludes it.
        var root = new DockRootDock();
        var strip = new DockDocumentDock { Owner = root };

        var d1 = Doc("d1", strip);
        var tool = new BareDockable { Id = "x1", Title = "x1", Owner = strip };
        var d2 = Doc("d2", strip);
        d1.Owner = strip;
        d2.Owner = strip;

        strip.VisibleDockables = new AvaloniaList<IDockable> { d1, tool, d2 };
        root.VisibleDockables = new AvaloniaList<IDockable> { strip };

        var order = new DockTabOrder().Compute(root);

        Assert.Equal(new IDockable[] { d1, d2 }, order.Select(e => e.Dockable).ToArray());
        Assert.DoesNotContain(order, e => ReferenceEquals(e.Dockable, tool));
    }

    [Fact]
    public void Compute_OnlyDocumentLeaves_AreNumberedInVisualOrder()
    {
        // Whitelist end-to-end: across a split containing documents, a bare (non-document) dockable and a
        // splitter, only the IDocument leaves are numbered, and they appear in visual order.
        var root = new DockRootDock();
        var split = new DockProportionalDock { Owner = root };

        var a1 = Doc("a1", null!);
        var a2 = Doc("a2", null!);
        var b1 = Doc("b1", null!);
        var stripA = new DockDocumentDock { Owner = split };
        var tool = new BareDockable { Id = "x1", Title = "x1", Owner = stripA };
        a1.Owner = stripA;
        a2.Owner = stripA;
        stripA.VisibleDockables = new AvaloniaList<IDockable> { a1, tool, a2 };
        var stripB = Strip(split, b1);
        var splitter = new DockProportionalDockSplitter { Owner = split };

        split.VisibleDockables = new AvaloniaList<IDockable> { stripA, splitter, stripB };
        root.VisibleDockables = new AvaloniaList<IDockable> { split };

        var order = new DockTabOrder().Compute(root);

        Assert.Equal(new IDockable[] { a1, a2, b1 }, order.Select(e => e.Dockable).ToArray());
        Assert.All(order, e => Assert.IsAssignableFrom<global::Dock.Model.Controls.IDocument>(e.Dockable));
    }

    [Fact]
    public void Compute_SplitParentWithSplitter_LabelAndActivationResolveSameDockableForIndex()
    {
        // The #1067 no-divergence invariant, extended to split layouts containing a splitter.
        var root = new DockRootDock();
        var split = new DockProportionalDock { Owner = root };

        var a1 = Doc("a1", null!);
        var b1 = Doc("b1", null!);
        var stripA = Strip(split, a1);
        var stripB = Strip(split, b1);
        var splitter = new DockProportionalDockSplitter { Owner = split };

        split.VisibleDockables = new AvaloniaList<IDockable> { stripA, splitter, stripB };
        root.VisibleDockables = new AvaloniaList<IDockable> { split };

        var service = new DockTabOrder();
        var forLabels = service.Compute(root);
        var forActivation = service.Compute(root);

        Assert.Equal(forLabels.Count, forActivation.Count);
        for (var i = 0; i < forLabels.Count; i++)
        {
            Assert.Same(forLabels[i].Dockable, forActivation[i].Dockable);
            Assert.Same(forLabels[i].Strip, forActivation[i].Strip);
        }
    }
}
