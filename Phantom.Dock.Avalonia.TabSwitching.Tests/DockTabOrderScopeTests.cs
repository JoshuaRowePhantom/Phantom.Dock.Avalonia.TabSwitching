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
/// Covers per-gesture-set scope resolution (commit 4 of the #1073 epic, design §4.2): the inherited
/// <c>IsSwitchable</c> opt-out excludes a strip from <see cref="DockTabSwitchScope.AllSwitchable"/>
/// numbering, <see cref="DockTabSwitchScope.FocusedDockOnly"/> follows Dock's own focus API to the
/// owning dock (no visual <c>FocusManager</c>), and multiple gesture sets on one <c>DockControl</c>
/// resolve independent scope roots simultaneously.
/// </summary>
public sealed class DockTabOrderScopeTests
{
    private static DockDocument Doc(string id) => new() { Id = id, Title = id };

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

    private static (DockRootDock Root, DockDocumentDock StripA, DockDocumentDock StripB) TwoStrips()
    {
        var root = new DockRootDock { IsFocusableRoot = true };
        var split = new DockProportionalDock { Owner = root };
        var stripA = Strip(split, Doc("a1"), Doc("a2"));
        var stripB = Strip(split, Doc("b1"), Doc("b2"), Doc("b3"));
        split.VisibleDockables = new AvaloniaList<IDockable> { stripA, stripB };
        root.VisibleDockables = new AvaloniaList<IDockable> { split };
        return (root, stripA, stripB);
    }

    [Fact]
    public void AllSwitchable_ExcludesStrip_WhenIsSwitchableFalse()
    {
        var (root, stripA, stripB) = TwoStrips();

        var scopeRoot = DockTabScopeResolver.ResolveScopeRoot(root, DockTabSwitchScope.AllSwitchable);

        // With no opt-out, both strips number.
        var all = new DockTabOrder().Compute(scopeRoot);
        Assert.Equal(5, all.Count);

        // Opt stripB out (the inherited IsSwitchable=False predicate): its dockables drop out entirely,
        // and stripA's numbering is unaffected.
        var filtered = new DockTabOrder().Compute(scopeRoot, dock => !ReferenceEquals(dock, stripB));
        Assert.All(filtered, e => Assert.Same(stripA, e.Strip));
        Assert.Equal(new[] { "a1", "a2" }, filtered.Select(e => e.Dockable.Id).ToArray());
    }

    [Fact]
    public void FocusedDockOnly_FollowsDockFocusApi_MovesNumberingToOwningDock()
    {
        var (root, stripA, stripB) = TwoStrips();

        // Focus a document in strip B through Dock's focus field (the same field SetFocusedDockable
        // writes). FocusedDockOnly must number strip B, not strip A and not the whole layout.
        root.FocusedDockable = stripB.VisibleDockables![0];

        var scopeRoot = DockTabScopeResolver.ResolveScopeRoot(root, DockTabSwitchScope.FocusedDockOnly);
        Assert.Same(stripB, scopeRoot);

        var order = new DockTabOrder().Compute(scopeRoot);
        Assert.Equal(new[] { "b1", "b2", "b3" }, order.Select(e => e.Dockable.Id).ToArray());
        Assert.All(order, e => Assert.Same(stripB, e.Strip));

        // Moving focus to strip A moves the numbering with it.
        root.FocusedDockable = stripA.VisibleDockables![1];
        var moved = DockTabScopeResolver.ResolveScopeRoot(root, DockTabSwitchScope.FocusedDockOnly);
        Assert.Same(stripA, moved);
        Assert.Equal(new[] { "a1", "a2" }, new DockTabOrder().Compute(moved).Select(e => e.Dockable.Id).ToArray());
    }

    [Fact]
    public void MultipleGestureSets_ResolveIndependentScopeRoots()
    {
        var (root, stripA, stripB) = TwoStrips();
        root.FocusedDockable = stripB.VisibleDockables![0];

        // Two gesture sets on one DockControl, each with its own Scope, resolve independent roots
        // simultaneously: AllSwitchable → whole layout; FocusedDockOnly → the focused strip only.
        var allScope = new DockTabSwitchGestures { Scope = DockTabSwitchScope.AllSwitchable };
        var focusedScope = new DockTabSwitchGestures { Scope = DockTabSwitchScope.FocusedDockOnly };

        var allRoot = DockTabScopeResolver.ResolveScopeRoot(root, allScope.Scope);
        var focusedRoot = DockTabScopeResolver.ResolveScopeRoot(root, focusedScope.Scope);

        Assert.Same(root, allRoot);
        Assert.Same(stripB, focusedRoot);

        var service = new DockTabOrder();
        Assert.Equal(5, service.Compute(allRoot).Count);
        Assert.Equal(3, service.Compute(focusedRoot).Count);
        _ = stripA;
    }
}
