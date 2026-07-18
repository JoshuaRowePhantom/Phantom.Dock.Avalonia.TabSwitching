using Avalonia.Input;
using Xunit;

namespace Phantom.Dock.Avalonia.TabSwitching.Tests;

/// <summary>
/// Pure unit coverage for <see cref="DockTabSwitchGestures.BuildMap"/> (design §4.1): the gesture →
/// index mapping for digit and function-key ranges, the explicit-override precedence, and
/// modifier-exact matching. No UI is required.
/// </summary>
public sealed class DockTabSwitchGesturesTests
{
    private static KeyEventArgs Key(Key key, KeyModifiers modifiers) =>
        new() { Key = key, KeyModifiers = modifiers };

    [Fact]
    public void BuildMap_Digits_MapsD1ToZeroAndD0ToNine()
    {
        var map = new DockTabSwitchGestures { Modifiers = KeyModifiers.Alt, Keys = DockTabSwitchKeys.Digits }
            .BuildMap();

        Assert.Equal(10, map.Count);
        Assert.Equal(global::Avalonia.Input.Key.D1, map.Gestures[0].Key);
        Assert.Equal(KeyModifiers.Alt, map.Gestures[0].KeyModifiers);
        Assert.Equal(global::Avalonia.Input.Key.D9, map.Gestures[8].Key);
        Assert.Equal(global::Avalonia.Input.Key.D0, map.Gestures[9].Key);

        Assert.True(map.TryGetIndex(Key(global::Avalonia.Input.Key.D1, KeyModifiers.Alt), out var first));
        Assert.Equal(0, first);
        Assert.True(map.TryGetIndex(Key(global::Avalonia.Input.Key.D0, KeyModifiers.Alt), out var tenth));
        Assert.Equal(9, tenth);
    }

    [Fact]
    public void BuildMap_FunctionKeys_MapF1ToZeroThroughF12ToEleven()
    {
        var map = new DockTabSwitchGestures { Modifiers = KeyModifiers.Alt, Keys = DockTabSwitchKeys.FunctionKeys }
            .BuildMap();

        Assert.Equal(12, map.Count);
        Assert.Equal(global::Avalonia.Input.Key.F1, map.Gestures[0].Key);
        Assert.Equal(global::Avalonia.Input.Key.F12, map.Gestures[11].Key);

        Assert.True(map.TryGetIndex(Key(global::Avalonia.Input.Key.F1, KeyModifiers.Alt), out var first));
        Assert.Equal(0, first);
        Assert.True(map.TryGetIndex(Key(global::Avalonia.Input.Key.F12, KeyModifiers.Alt), out var last));
        Assert.Equal(11, last);
    }

    [Fact]
    public void BuildMap_DigitsAndFunctionKeys_ContinuesFunctionKeysAfterDigits()
    {
        var map = new DockTabSwitchGestures
        {
            Modifiers = KeyModifiers.Control,
            Keys = DockTabSwitchKeys.Digits | DockTabSwitchKeys.FunctionKeys,
        }.BuildMap();

        // Digits occupy 0..9, function keys continue at 10..21.
        Assert.Equal(22, map.Count);
        Assert.Equal(global::Avalonia.Input.Key.D1, map.Gestures[0].Key);
        Assert.Equal(global::Avalonia.Input.Key.D0, map.Gestures[9].Key);
        Assert.Equal(global::Avalonia.Input.Key.F1, map.Gestures[10].Key);
        Assert.Equal(global::Avalonia.Input.Key.F12, map.Gestures[21].Key);
    }

    [Fact]
    public void BuildMap_ExplicitGestures_OverrideModifiersAndKeys()
    {
        var gestures = new DockTabSwitchGestures
        {
            // These would normally produce Alt+digits; the explicit list must win.
            Modifiers = KeyModifiers.Alt,
            Keys = DockTabSwitchKeys.Digits,
        };
        gestures.Gestures = new[]
        {
            new KeyGesture(global::Avalonia.Input.Key.Q, KeyModifiers.Control),
            new KeyGesture(global::Avalonia.Input.Key.W, KeyModifiers.Control),
        };

        var map = gestures.BuildMap();

        Assert.Equal(2, map.Count);
        Assert.Equal(global::Avalonia.Input.Key.Q, map.Gestures[0].Key);
        Assert.Equal(global::Avalonia.Input.Key.W, map.Gestures[1].Key);

        // Index equals position in the explicit list; the Alt/Digits config is ignored.
        Assert.True(map.TryGetIndex(Key(global::Avalonia.Input.Key.W, KeyModifiers.Control), out var index));
        Assert.Equal(1, index);
        Assert.False(map.TryGetIndex(Key(global::Avalonia.Input.Key.D1, KeyModifiers.Alt), out _));
    }

    [Fact]
    public void Matches_AltOnly_DoesNotFireOnAltShift()
    {
        var map = new DockTabSwitchGestures { Modifiers = KeyModifiers.Alt, Keys = DockTabSwitchKeys.Digits }
            .BuildMap();

        // Exact modifier matching: an Alt-only set must not fire on Alt+Shift.
        Assert.True(map.TryGetIndex(Key(global::Avalonia.Input.Key.D1, KeyModifiers.Alt), out _));
        Assert.False(map.TryGetIndex(
            Key(global::Avalonia.Input.Key.D1, KeyModifiers.Alt | KeyModifiers.Shift), out _));
    }
}
