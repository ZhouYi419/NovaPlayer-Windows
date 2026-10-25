using NovaPlayer.Core.Playback;
using NovaPlayer.Core.Presentation;
using Xunit;

namespace NovaPlayer.Tests;

public sealed class ShellRulesTests
{
    [Theory]
    [InlineData(1440, false, true)]
    [InlineData(1200, false, true)]
    [InlineData(1199, false, false)]
    [InlineData(1049, true, false)]
    [InlineData(960, true, false)]
    public void ResponsiveBoundariesPreserveNavigationAndVideoSpace(double width, bool compact, bool playlist)
    {
        var layout = ShellLayout.Calculate(width, true);
        Assert.Equal(compact, layout.CompactNavigation);
        Assert.Equal(playlist, layout.ShowPlaylist);
    }
    [Fact] public void ManualPlaylistChoiceSurvivesWideWindow() => Assert.False(ShellLayout.Calculate(1440, false).ShowPlaylist);
    [Theory]
    [InlineData(4, 90, true, 0)]
    [InlineData(20, 90, true, 20)]
    [InlineData(20, 90, false, 0)]
    [InlineData(65, 90, true, 0)]
    [InlineData(960, 1000, true, 0)]
    public void ResumeUsesOnlyMeaningfulUnfinishedPosition(double position, double duration, bool enabled, double expected) => Assert.Equal(expected, ResumePolicy.Position(position, duration, enabled));
}
