using NovaPlayer.Core.Playback;
using Xunit;

namespace NovaPlayer.Tests;

public sealed class PlaybackPolicyTests
{
    [Theory]
    [InlineData(0.25)] [InlineData(1)] [InlineData(1.37)] [InlineData(8)] [InlineData(15)]
    public void AcceptsPresetAndCustomSpeeds(double speed) => Assert.Equal(speed, PlaybackPolicy.ValidateSpeed(speed));

    [Theory]
    [InlineData(0)] [InlineData(0.249)] [InlineData(15.001)] [InlineData(-1)]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    public void RejectsInvalidNativeSpeedInputs(double speed) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaybackPolicy.ValidateSpeed(speed));

    [Theory]
    [InlineData(false, 8, false)] [InlineData(false, 8.01, true)] [InlineData(false, 15, true)]
    [InlineData(true, 1, true)] [InlineData(true, 15, true)]
    public void AutomaticMuteDoesNotReplaceUserPreference(bool userMuted, double speed, bool expected) =>
        Assert.Equal(expected, PlaybackPolicy.EffectiveMute(userMuted, speed));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SpeedRoundTripRestoresOriginalMute(bool userMuted)
    {
        Assert.True(PlaybackPolicy.EffectiveMute(userMuted, 15));
        Assert.Equal(userMuted, PlaybackPolicy.EffectiveMute(userMuted, 1));
    }

    [Theory]
    [InlineData(-10, 100, 0)] [InlineData(110, 100, 100)] [InlineData(30, 100, 30)] [InlineData(10, 0, 0)]
    public void SeekCannotEscapeMediaBounds(double target, double duration, double expected) =>
        Assert.Equal(expected, PlaybackPolicy.ClampPosition(target, duration));

    [Fact]
    public void RejectsNonFiniteSeek() => Assert.Throws<ArgumentOutOfRangeException>(() => PlaybackPolicy.ClampPosition(double.NaN, 100));
}
