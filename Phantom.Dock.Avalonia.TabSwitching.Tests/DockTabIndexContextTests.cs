using System;
using Xunit;

namespace Phantom.Dock.Avalonia.TabSwitching.Tests;

/// <summary>
/// Pure unit coverage for <see cref="DockTabIndexContext"/> (design §4.3): the single-label
/// convenience and the out-of-range (empty) case.
/// </summary>
public sealed class DockTabIndexContextTests
{
    [Fact]
    public void DockTabIndexContext_OutOfRange_LabelIsNull()
    {
        var context = new DockTabIndexContext();

        // No gesture set numbers this tab ⇒ empty Labels and a null Label convenience.
        Assert.Empty(context.Labels);
        Assert.Null(context.Label);

        // With labels, Label is the primary (first) entry's text.
        context.Labels = new[]
        {
            new DockTabIndexLabel("1", new DockTabSwitchGestures()),
            new DockTabIndexLabel("F1", new DockTabSwitchGestures()),
        };
        Assert.Equal("1", context.Label);

        // Clearing back to out-of-range restores the null convenience.
        context.Labels = Array.Empty<DockTabIndexLabel>();
        Assert.Null(context.Label);
    }
}
