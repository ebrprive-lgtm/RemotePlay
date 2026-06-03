using RemotePlay.Services;
using Xunit;

namespace RemotePlay.Tests;

public sealed class SettingsValidationServiceTests
{
    [Fact]
    public void ValidateReturnsFailureForInvalidPort()
    {
        var service = new SettingsValidationService();

        var result = service.Validate("99999");

        Assert.False(result.IsValid);
        Assert.Equal("⚠️ Port must be a number between 1024 and 65535.", result.ErrorMessage);
    }

    [Fact]
    public void ValidateReturnsSuccessForValidPort()
    {
        var service = new SettingsValidationService();

        var result = service.Validate("5000");

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
        var service = new SettingsValidationService();

        var result = service.Validate(port.ToString());

        Assert.True(result.IsValid);
        Assert.Equal(port, result.ParsedPort);
    }

    [Theory]
    [InlineData("1023")]
    [InlineData("65536")]
    public void ValidateReturnsFailure_ForOutOfRangePorts(string portText)
    {
        var service = new SettingsValidationService();

        var result = service.Validate(portText);

        Assert.False(result.IsValid);
        Assert.Equal("⚠️ Port must be a number between 1024 and 65535.", result.ErrorMessage);
    }

    // ── Non-numeric port ─────────────────────────────────────────────────────

    [Fact]
    public void ValidateReturnsFailure_WhenPortIsNonNumeric()
    {
        var service = new SettingsValidationService();

        var result = service.Validate("abc");

        Assert.False(result.IsValid);
        Assert.Equal("⚠️ Port must be a number between 1024 and 65535.", result.ErrorMessage);
    }

    // ── Non-existent folder

    // (Folder validation was removed from SettingsValidationService - paths are
    //  validated lazily at scan time, not at save time.)

    // ── Port whitespace trimming

    [Fact]
    public void ValidateReturnsSuccess_WhenPortHasLeadingAndTrailingWhitespace()
    {
        var service = new SettingsValidationService();

        var result = service.Validate(" 5000 ");

        Assert.True(result.IsValid);
        Assert.Equal(5000, result.ParsedPort);
    }
}
