using System;

namespace RemotePlay.Helpers;

public readonly record struct VideoAdjustmentValues(float Brightness, float Contrast, float Gamma, float Saturation);

public static class VideoAdjustmentMapper
{
    public static VideoAdjustmentValues Map(double brightness, double sharpness, double saturation)
    {
        var detailBoost = (float)Math.Clamp(sharpness, 0, 1);
        var contrast = 1f + (detailBoost * 1.35f);
        var gamma = 1f + (detailBoost * 0.14f);
        var compensationFactor = 1 + (detailBoost * 0.05f) - (detailBoost * detailBoost * 0.12f);
        var compensatedBrightness = Math.Clamp(Math.Clamp(brightness, 0, 1) * 2 * compensationFactor, 0, 2);

        return new VideoAdjustmentValues(
            (float)compensatedBrightness,
            contrast,
            gamma,
            (float)Math.Clamp(saturation, 0, 2));
    }
}
