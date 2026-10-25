using NovaPlayer.Core.Playback;
using NovaPlayer.Core.Presentation;
using Xunit;

namespace NovaPlayer.Tests;

public sealed class ExperienceRulesTests
{
    [Fact]
    public void ConflictingAssignmentPreservesBothOriginalBindings()
    {
        var map = new ShortcutMap();
        Assert.False(map.TryAssign(ShortcutAction.TogglePause, new("F"), out var message));
        Assert.Contains("切换全屏", message);
        Assert.Equal(ShortcutAction.TogglePause, map.Resolve(new("Space")));
        Assert.Equal(ShortcutAction.Fullscreen, map.Resolve(new("F")));
    }
    [Fact]
    public void BatchLoadCanSwapBindingsWithoutIntermediateConflict()
    {
        var map = new ShortcutMap();
        var entries = new Dictionary<ShortcutAction, ShortcutGesture?>
        { [ShortcutAction.TogglePause] = new("F"), [ShortcutAction.Fullscreen] = new("Space") };
        Assert.True(map.TryReplace(entries, out _));
        Assert.Equal(ShortcutAction.TogglePause, map.Resolve(new("F")));
        Assert.Equal(ShortcutAction.Fullscreen, map.Resolve(new("Space")));
    }
    [Fact]
    public void ModifiersAreDistinctAndResetRestoresClearedBindings()
    {
        var map = new ShortcutMap();
        Assert.Equal(ShortcutAction.OpenFile, map.Resolve(new("O", ShortcutModifiers.Control)));
        Assert.Equal(ShortcutAction.OpenFolder, map.Resolve(new("O", ShortcutModifiers.Control | ShortcutModifiers.Shift)));
        Assert.Null(map.Resolve(new("O")));
        Assert.True(map.TryAssign(ShortcutAction.OpenFile, null, out _));
        Assert.Null(map.Resolve(new("O", ShortcutModifiers.Control)));
        map.Reset();
        Assert.Equal(ShortcutAction.OpenFile, map.Resolve(new("O", ShortcutModifiers.Control)));
    }
    [Theory]
    [InlineData("LeftCtrl", ShortcutModifiers.Control)]
    [InlineData("None", ShortcutModifiers.None)]
    [InlineData("F4", ShortcutModifiers.Alt)]
    [InlineData("Tab", ShortcutModifiers.Alt)]
    [InlineData("Delete", ShortcutModifiers.Control | ShortcutModifiers.Alt)]
    [InlineData("F", (ShortcutModifiers)8)]
    public void UnusableGesturesAreRejected(string key, ShortcutModifiers modifiers) =>
        Assert.False(new ShortcutMap().TryAssign(ShortcutAction.TogglePause, new(key, modifiers), out _));
    [Fact]
    public void OnlyContinuousActionsAllowKeyRepeat()
    {
        var map = new ShortcutMap();
        Assert.Null(map.Resolve(new("Space"), true));
        Assert.Null(map.Resolve(new("F"), true));
        Assert.Equal(ShortcutAction.Backward, map.Resolve(new("Left"), true));
    }
    [Fact]
    public void InvalidBatchDoesNotPartiallyApply()
    {
        var map = new ShortcutMap();
        Assert.False(map.TryReplace(new Dictionary<ShortcutAction, ShortcutGesture?> { [ShortcutAction.TogglePause] = new("G"), [(ShortcutAction)999] = new("H") }, out _));
        Assert.Equal(ShortcutAction.TogglePause, map.Resolve(new("Space")));
    }
    [Fact]
    public void WindowGeometryRejectsInvalidPersistedValues()
    {
        Assert.False(new WindowGeometry(double.NaN, 0, 400, 225).IsValid);
        Assert.False(new WindowGeometry(0, 0, 0, 225).IsValid);
        Assert.True(new WindowGeometry(-1920, 0, 1440, 900).IsValid);
    }
}
