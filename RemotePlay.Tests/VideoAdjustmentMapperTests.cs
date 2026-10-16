using RemotePlay.Helpers;
using Xunit;

namespace RemotePlay.Tests;

public sealed class VideoAdjustmentMapperTests
{
    [Fact]
    public void Map_DefaultBrightness_ProducesNeutralVideoAdjustments()
    {
        var adjustments = VideoAdjustmentMapper.Map(brightness: 0.5, sharpness: 0, saturation: 1);

        Assert.Equal(1f, adjustments.Brightness);
        Assert.Equal(1f, adjustments.Contrast);
        Assert.Equal(1f, adjustments.Gamma);
        Assert.Equal(1f, adjustments.Saturation);
    }

    [Fact]
    public void Map_ClampsBrightnessToLibVlcRange()
    {
        Assert.Equal(0f, VideoAdjustmentMapper.Map(brightness: 0, sharpness: 0, saturation: 1).Brightness);
        Assert.Equal(2f, VideoAdjustmentMapper.Map(brightness: 1, sharpness: 0, saturation: 1).Brightness);
    }
}
