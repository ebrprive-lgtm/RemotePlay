using System.Text.Json;
using Xunit;

namespace RemotePlay.Tests;

public sealed class LiveTvCatalogTests
{
    [Fact]
    public void BuildLiveTvCatalog_ExcludesClosedAndNsfwChannels()
    {
        using var channels = JsonDocument.Parse(
            """
            [
              { "id": "safe", "name": "Safe Channel", "country": "us" },
              { "id": "closed", "name": "Closed Channel", "closed": true },
              { "id": "adult", "name": "Adult Channel", "is_nsfw": true }
            ]
            """);
        using var streams = JsonDocument.Parse(
            """
            [
              { "channel": "safe", "url": "https://safe.example/live.m3u8" },
              { "channel": "closed", "name": "Closed Channel", "url": "https://closed.example/live.m3u8" },
              { "channel": "adult", "name": "Adult Channel", "url": "https://adult.example/live.m3u8" }
            ]
            """);

        var catalog = WebServer.BuildLiveTvCatalog(channels.RootElement, streams.RootElement, null);

        var channel = Assert.Single(catalog);
        Assert.Equal("Safe Channel", channel.Name);
        Assert.Equal("safe.example", channel.SourceHost);
    }

    [Fact]
    public void BuildLiveTvCatalog_PreservesDistinctNonDefaultPorts()
    {
        using var channels = JsonDocument.Parse(
            """
            [
              { "id": "first", "name": "Example Channel", "country": "us" },
              { "id": "second", "name": "Example Channel", "country": "us" }
            ]
            """);
        using var streams = JsonDocument.Parse(
            """
            [
              { "channel": "first", "url": "https://stream.example:8443/live.m3u8" },
              { "channel": "second", "url": "https://stream.example:9443/live.m3u8" }
            ]
            """);

        var catalog = WebServer.BuildLiveTvCatalog(channels.RootElement, streams.RootElement, null);

        Assert.Equal(2, catalog.Length);
        Assert.Equal(
            new[] { "stream.example:8443", "stream.example:9443" },
            catalog.Select(channel => channel.SourceHost).OrderBy(host => host, StringComparer.Ordinal));
    }
}
