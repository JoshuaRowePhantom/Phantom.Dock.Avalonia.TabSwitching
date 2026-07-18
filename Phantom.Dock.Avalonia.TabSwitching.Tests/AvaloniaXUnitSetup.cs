using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Phantom.Dock.Avalonia.TabSwitching.Tests.TabSwitchingTestAppBuilder))]

namespace Phantom.Dock.Avalonia.TabSwitching.Tests;

public static class TabSwitchingTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TabSwitchingTestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });

    private sealed class TabSwitchingTestApplication : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
        }
    }
}
