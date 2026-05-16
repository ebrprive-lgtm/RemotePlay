using RemotePlay.Services;
using Xunit;

namespace RemotePlay.Tests;

public sealed class SettingsValidationServiceTests
{
    [Fact]
    public void ValidateReturnsFailureForMissingFolder()
    {
        var service = new SettingsValidationService();

        var result = service.Validate("", "5000");

        Assert.False(result.IsValid);
        Assert.Equal("⚠️ Folder does not exist. Please choose a valid path.", result.ErrorMessage);
    }

    [Fact]
    public void ValidateReturnsFailureForInvalidPort()
    {
        var folder = Path.Combine(Path.GetTempPath(), "RemotePlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var service = new SettingsValidationService();

        var result = service.Validate(folder, "99999");

        Assert.False(result.IsValid);
        Assert.Equal("⚠️ Port must be a number between 1024 and 65535.", result.ErrorMessage);
    }

    [Fact]
    public void ValidateReturnsSuccessForExistingFolderAndValidPort()
    {
        var folder = Path.Combine(Path.GetTempPath(), "RemotePlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var service = new SettingsValidationService();

        var result = service.Validate(folder, "5000");

        Assert.True(result.IsValid);
        Assert.Equal(5000, result.ParsedPort);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void AppConfigNormalizesLibraryPageSize()
    {
        var config = new AppConfig { LibraryPageSize = 5000 };

        Assert.Equal(1000, config.EffectiveLibraryPageSize);
    }

    [Fact]
    public void AppConfigNormalizesVideoExtensions()
    {
        var config = new AppConfig { VideoFileExtensions = ["mp4", ".MKV", "mp4"] };

        Assert.Equal([".mp4", ".MKV"], config.EffectiveVideoFileExtensions);
    }
}
