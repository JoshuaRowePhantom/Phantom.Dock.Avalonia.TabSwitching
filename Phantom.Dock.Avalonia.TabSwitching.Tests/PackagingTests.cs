using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Xunit;

namespace Phantom.Dock.Avalonia.TabSwitching.Tests;

/// <summary>
/// Guards the packaging boundary established in commit 1 of the #1073 epic: the
/// <c>Phantom.Dock.Avalonia.TabSwitching</c> assembly depends only on Avalonia and Dock.Avalonia,
/// ships as part of the product build, and its placeholder default theme dictionary is mergeable.
/// </summary>
public sealed class PackagingTests
{
    private static Assembly TabSwitchingAssembly => typeof(DockTabSwitchTheme).Assembly;

    private static string TabSwitchingOutputDir =>
        Path.GetDirectoryName(TabSwitchingAssembly.Location)
        ?? throw new InvalidOperationException("Could not resolve the TabSwitching assembly output directory.");

    [Fact]
    public void TabSwitchingAssembly_References_OnlyAvaloniaAndDock()
    {
        var referenced = TabSwitchingAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        // The reusable assembly must reference Avalonia...
        Assert.Contains(referenced, n => n.StartsWith("Avalonia", StringComparison.Ordinal));

        // ...and must never reference any Phantom.Workspaces product assembly (guards separate release).
        Assert.DoesNotContain(referenced, n => n.StartsWith("Phantom.Workspaces", StringComparison.Ordinal));

        // The Dock.Avalonia dependency must ship alongside the assembly in the build output.
        Assert.True(
            File.Exists(Path.Combine(TabSwitchingOutputDir, "Dock.Avalonia.dll")),
            "Dock.Avalonia.dll must be present in the TabSwitching build output.");

        // No product assembly may leak into the output next to the reusable assembly.
        Assert.Empty(Directory.GetFiles(TabSwitchingOutputDir, "Phantom.Workspaces*.dll"));
    }

    [Fact]
    public void TabSwitchingAssembly_Loads_FromProductBuildOutput()
    {
        Assert.Equal("Phantom.Dock.Avalonia.TabSwitching", TabSwitchingAssembly.GetName().Name);
        Assert.True(File.Exists(TabSwitchingAssembly.Location));
        Assert.True(File.Exists(Path.Combine(TabSwitchingOutputDir, "Phantom.Dock.Avalonia.TabSwitching.dll")));
    }

    [AvaloniaFact]
    public void DockTabSwitchTheme_IsMergeable_WithoutThrowing()
    {
        var theme = new DockTabSwitchTheme();

        // Merging into a Styles collection (as a consumer's Application.Styles would) must not throw.
        var host = new Styles();
        var exception = Record.Exception(() => host.Add(theme));

        Assert.Null(exception);
        Assert.Contains(theme, host);
    }
}
