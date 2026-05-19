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

    // ── Port boundary values ─────────────────────────────────────────────────

    [Theory]
    [InlineData(1024)]
    [InlineData(65535)]
    public void ValidateReturnsSuccess_ForPortBoundaryValues(int port)
    {
        var folder = Path.Combine(Path.GetTempPath(), "RemotePlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var service = new SettingsValidationService();

        var result = service.Validate(folder, port.ToString());

        Assert.True(result.IsValid);
        Assert.Equal(port, result.ParsedPort);
    }

    [Theory]
    [InlineData("1023")]
    [InlineData("65536")]
    public void ValidateReturnsFailure_ForOutOfRangePorts(string portText)
    {
        var folder = Path.Combine(Path.GetTempPath(), "RemotePlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var service = new SettingsValidationService();

        var result = service.Validate(folder, portText);

        Assert.False(result.IsValid);
        Assert.Equal("⚠️ Port must be a number between 1024 and 65535.", result.ErrorMessage);
    }

    // ── Non-numeric port ─────────────────────────────────────────────────────

    [Fact]
    public void ValidateReturnsFailure_WhenPortIsNonNumeric()
    {
        var folder = Path.Combine(Path.GetTempPath(), "RemotePlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var service = new SettingsValidationService();

        var result = service.Validate(folder, "abc");

        Assert.False(result.IsValid);
        Assert.Equal("⚠️ Port must be a number between 1024 and 65535.", result.ErrorMessage);
    }

    // ── Non-existent folder ──────────────────────────────────────────────────

    [Fact]
    public void ValidateReturnsFailure_WhenFolderDoesNotExist()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), "RemotePlay.Tests", Guid.NewGuid().ToString("N"), "NotCreated");
        var service = new SettingsValidationService();

        var result = service.Validate(nonExistent, "5000");

        Assert.False(result.IsValid);
        Assert.Equal("⚠️ Folder does not exist. Please choose a valid path.", result.ErrorMessage);
    }

    // ── Port whitespace trimming ─────────────────────────────────────────────

    [Fact]
    public void ValidateReturnsSuccess_WhenPortHasLeadingAndTrailingWhitespace()
    {
        var folder = Path.Combine(Path.GetTempPath(), "RemotePlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var service = new SettingsValidationService();

        var result = service.Validate(folder, " 5000 ");

        Assert.True(result.IsValid);
        Assert.Equal(5000, result.ParsedPort);
    }
}
