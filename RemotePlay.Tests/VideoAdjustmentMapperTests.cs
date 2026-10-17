using RemotePlay.Helpers;
using Xunit;

namespace RemotePlay.Tests;

public sealed class VideoAdjustmentMapperTests
{
    [Fact]
    public void Map_DefaultBrightness_ProducesNeutralVideoAdjustments()
    {
        var adjustments = VideoAdjustmentMapper.Map(brightness: 0.5, saturation: 1);

        Assert.Equal(1f, adjustments.Brightness);
        Assert.Equal(1f, adjustments.Contrast);
        Assert.Equal(1f, adjustments.Gamma);
        Assert.Equal(1f, adjustments.Saturation);
        Assert.False(VideoAdjustmentMapper.RequiresAdjustment(adjustments));
    }

    [Fact]
    public void Map_ClampsBrightnessToLibVlcRange()
    {
        Assert.Equal(0f, VideoAdjustmentMapper.Map(brightness: 0, saturation: 1).Brightness);
        Assert.Equal(2f, VideoAdjustmentMapper.Map(brightness: 1, saturation: 1).Brightness);
    }

    [Theory]
    [InlineData(0.25, 1.0)]
    [InlineData(0.5, 1.25)]
    public void Map_NonNeutralBrightnessOrSaturationRequiresAdjustment(double brightness, double saturation)
    {
        var adjustments = VideoAdjustmentMapper.Map(brightness, saturation);

        Assert.True(VideoAdjustmentMapper.RequiresAdjustment(adjustments));
        Assert.Equal(1f, adjustments.Contrast);
        Assert.Equal(1f, adjustments.Gamma);
    }
}
