using RemotePlay.Helpers;
using Xunit;

namespace RemotePlay.Tests;

public sealed class FullscreenBoundsEvaluatorTests
{
    [Fact]
    public void NeedsRepairReturnsFalseWhenNotVideoMode()
    {
        var result = FullscreenBoundsEvaluator.NeedsRepair(
            isVideoMode: false,
            windowStyle: "SingleBorderWindow",
            resizeMode: "CanResize",
            windowState: "Normal",
            topmost: false,
            windowLeft: 100,
            windowTop: 100,
            windowWidth: 800,
            windowHeight: 600,
            targetLeft: 0,
            targetTop: 0,
            targetWidth: 1920,
            targetHeight: 1080);

        Assert.False(result);
    }

    [Fact]
    public void NeedsRepairReturnsFalseWhenFullscreenMatchesTarget()
    {
        var result = FullscreenBoundsEvaluator.NeedsRepair(
            isVideoMode: true,
            windowStyle: "None",
            resizeMode: "NoResize",
            windowState: "Maximized",
            topmost: true,
            windowLeft: 0,
            windowTop: 0,
            windowWidth: 1920,
            windowHeight: 1080,
            targetLeft: 0,
            targetTop: 0,
            targetWidth: 1920,
            targetHeight: 1080);

        Assert.False(result);
    }

    [Fact]
    public void NeedsRepairReturnsTrueWhenWindowIsNotTopmost()
    {
        var result = FullscreenBoundsEvaluator.NeedsRepair(
            isVideoMode: true,
            windowStyle: "None",
            resizeMode: "NoResize",
            windowState: "Maximized",
            topmost: false,
            windowLeft: 0,
            windowTop: 0,
            windowWidth: 1920,
            windowHeight: 1080,
            targetLeft: 0,
            targetTop: 0,
            targetWidth: 1920,
            targetHeight: 1080);

        Assert.True(result);
    }

    [Fact]
    public void NeedsRepairReturnsTrueWhenBoundsDifferBeyondTolerance()
    {
        var result = FullscreenBoundsEvaluator.NeedsRepair(
            isVideoMode: true,
            windowStyle: "None",
            resizeMode: "NoResize",
            windowState: "Maximized",
            topmost: true,
            windowLeft: 0,
            windowTop: 0,
            windowWidth: 1500,
            windowHeight: 1080,
            targetLeft: 0,
            targetTop: 0,
            targetWidth: 1920,
            targetHeight: 1080);

        Assert.True(result);
    }
}
