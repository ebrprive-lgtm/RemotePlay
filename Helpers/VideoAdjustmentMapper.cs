using System;

namespace RemotePlay.Helpers;

public readonly record struct VideoAdjustmentValues(float Brightness, float Contrast, float Gamma, float Saturation);

public static class VideoAdjustmentMapper
{
    private const float NeutralTolerance = 0.0001f;

    public static VideoAdjustmentValues Map(double brightness, double saturation)
    {
        return new VideoAdjustmentValues(
            (float)(Math.Clamp(brightness, 0, 1) * 2),
            1f,
            1f,
            (float)Math.Clamp(saturation, 0, 2));
    }

    public static bool RequiresAdjustment(VideoAdjustmentValues adjustments) =>
        MathF.Abs(adjustments.Brightness - 1f) > NeutralTolerance ||
        MathF.Abs(adjustments.Contrast - 1f) > NeutralTolerance ||
        MathF.Abs(adjustments.Gamma - 1f) > NeutralTolerance ||
        MathF.Abs(adjustments.Saturation - 1f) > NeutralTolerance;
}
