using RemotePlay;
using Xunit;

namespace RemotePlay.Tests;

public sealed class WebServerConfigHelpersTests
{
    private static readonly string[] DefaultExtensions = [".mp4", ".mkv"];
    private static readonly string[] DefaultNames = ["Subs", "Alt"];

    // ── BuildExtensionSet ──────────────────────────────────────────────────

    [Fact]
    public void BuildExtensionSet_NullValues_ReturnsDefaultExtensions()
    {
        var result = WebServerConfigHelpers.BuildExtensionSet(null, DefaultExtensions);

        Assert.Contains(".mp4", result, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(".mkv", result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildExtensionSet_EmptyValues_ReturnsDefaultExtensions()
    {
        var result = WebServerConfigHelpers.BuildExtensionSet([], DefaultExtensions);

        Assert.Contains(".mp4", result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildExtensionSet_ValuesWithDots_IncludesThemAsIs()
    {
        var result = WebServerConfigHelpers.BuildExtensionSet([".avi", ".flv"], DefaultExtensions);

        Assert.Contains(".avi", result, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(".flv", result, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(".mp4", result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildExtensionSet_ValuesWithoutDots_DotPrefixAdded()
    {
        var result = WebServerConfigHelpers.BuildExtensionSet(["avi", "flv"], DefaultExtensions);

        Assert.Contains(".avi", result, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(".flv", result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildExtensionSet_WhitespaceOnlyValues_SkippedAndFallsBackToDefault()
    {
        var result = WebServerConfigHelpers.BuildExtensionSet(["  ", "\t"], DefaultExtensions);

        Assert.Contains(".mp4", result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildExtensionSet_IsCaseInsensitive()
    {
        var result = WebServerConfigHelpers.BuildExtensionSet([".AVI"], DefaultExtensions);

        Assert.Contains(".avi", result, StringComparer.OrdinalIgnoreCase);
    }

    // ── BuildNameSet ───────────────────────────────────────────────────────

    [Fact]
    public void BuildNameSet_NullValues_ReturnsFallback()
    {
        var result = WebServerConfigHelpers.BuildNameSet(null, DefaultNames);

        Assert.Contains("Subs", result, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Alt", result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildNameSet_EmptyValues_ReturnsFallback()
    {
        var result = WebServerConfigHelpers.BuildNameSet([], DefaultNames);

        Assert.Contains("Subs", result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildNameSet_ValidValues_ReturnsBothValuesAndFallback()
    {
        var result = WebServerConfigHelpers.BuildNameSet(["Extras", "Behind The Scenes"], DefaultNames);

        Assert.Contains("Extras", result, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Behind The Scenes", result, StringComparer.OrdinalIgnoreCase);
        // Defaults are always included (additive, not replacement)
        Assert.Contains("Subs", result, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Alt", result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildNameSet_WhitespaceOnlyValues_SkippedAndFallsBackToDefault()
    {
        var result = WebServerConfigHelpers.BuildNameSet(["   "], DefaultNames);

        Assert.Contains("Subs", result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildNameSet_IsCaseInsensitive()
    {
        var result = WebServerConfigHelpers.BuildNameSet(["extras"], DefaultNames);

        Assert.Contains("EXTRAS", result, StringComparer.OrdinalIgnoreCase);
    }
}
