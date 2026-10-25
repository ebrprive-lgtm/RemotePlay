using System.Diagnostics;
using System.Net;
using System.Text.Json;
using RemotePlay;
using RemotePlay.Models;
using Xunit;
using Xunit.Abstractions;

namespace RemotePlay.Tests;

public sealed class WebServerLibraryScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LargeDecadeLibraryRescanIndexesAndSearchesAllFiles()
    {
        const int decadeCount = 10;
        const int filesPerDecade = 125;
        const int expectedFileCount = decadeCount * filesPerDecade;
        var uniqueMarker = Guid.NewGuid().ToString("N");
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "RemotePlayLibraryScaleTests",
            Guid.NewGuid().ToString("N"));
        var moviesRoot = Path.Combine(temporaryDirectory, "movies");
        var targetDecade = Path.Combine(moviesRoot, "@Master.Movies", "1940s");
        var targetName = $"ScaleTarget-{uniqueMarker}";
        var targetPath = Path.Combine(targetDecade, targetName + ".mp4");
        var indexCacheDirectory = Path.Combine(temporaryDirectory, "cache");
        var indexCachePath = Path.Combine(indexCacheDirectory, "library-index.json");
        Directory.CreateDirectory(targetDecade);
        Directory.CreateDirectory(indexCacheDirectory);

        for (var decadeIndex = 0; decadeIndex < decadeCount; decadeIndex++)
        {
            var decade = 1920 + (decadeIndex * 10);
            var decadeDirectory = Path.Combine(moviesRoot, "@Master.Movies", $"{decade}s");
            Directory.CreateDirectory(decadeDirectory);
            for (var fileIndex = 0; fileIndex < filesPerDecade; fileIndex++)
            {
                var name = decade == 1940 && fileIndex == 0
                    ? targetName
                    : $"ScaleMovie-{decade}-{fileIndex:D4}";
                File.WriteAllBytes(Path.Combine(decadeDirectory, name + ".mp4"), [0x52, 0x50, 0x01]);
            }
        }

        var managedMemoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        using var process = Process.GetCurrentProcess();
        var privateMemoryBefore = process.PrivateMemorySize64;
        HttpClient? client = null;
        WebServer? server = null;
        try
        {
            var started = WebServerTestServer.Start(port => new WebServer(
                new AppConfig
                {
                    Port = port,
                    AdditionalMoviesPaths = [moviesRoot],
                    MaxRequestsPerIpPerWindow = 0,
                },
                WebServerApiTests.BuildStubCallbacks(),
                podcastTopPoolCacheFilePath: Path.Combine(temporaryDirectory, "podcast-top-pool.json"),
                libraryIndexCacheFilePath: indexCachePath));
            server = started.Server;
            client = new HttpClient { BaseAddress = new Uri($"http://localhost:{started.Port}") };

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var status = await WaitForLibraryScanAsync(client, timeout.Token);

            Assert.Equal(expectedFileCount, status.IndexedFiles);
            Assert.Equal(expectedFileCount, status.ScannedFiles);
            Assert.True(status.ScannedFolders >= decadeCount);
            Assert.True(status.ScanDurationMs >= 0);
            Assert.Empty(status.LastError);

            var managedMemoryAfter = GC.GetTotalMemory(forceFullCollection: true);
            process.Refresh();
            var privateMemoryAfter = process.PrivateMemorySize64;
            output.WriteLine(
                $"Scanned {status.IndexedFiles} decade-organized videos in {status.ScanDurationMs} ms; "
                + $"managed heap delta {managedMemoryAfter - managedMemoryBefore:N0} bytes; "
                + $"process private-memory delta {privateMemoryAfter - privateMemoryBefore:N0} bytes.");

            using var searchResponse = await client.GetAsync(
                "/api/search?q=" + Uri.EscapeDataString(uniqueMarker),
                timeout.Token);
            Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
            using var searchJson = JsonDocument.Parse(
                await searchResponse.Content.ReadAsStringAsync(timeout.Token));
            Assert.Equal(1, searchJson.RootElement.GetProperty("totalFiles").GetInt32());

            using var browseResponse = await client.GetAsync(
                "/api/browse?dir="
                    + Uri.EscapeDataString(WebPathHelpers.EncodePath(targetDecade)),
                timeout.Token);
            Assert.Equal(HttpStatusCode.OK, browseResponse.StatusCode);
            using var browseJson = JsonDocument.Parse(
                await browseResponse.Content.ReadAsStringAsync(timeout.Token));
            Assert.Contains(
                targetName,
                browseJson.RootElement.GetProperty("files")
                    .EnumerateArray()
                    .Select(file => file.GetProperty("name").GetString()));
        }
        finally
        {
            client?.Dispose();
            server?.Stop();
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task<LibraryScanSnapshot> WaitForLibraryScanAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var response = await client.GetAsync("/api/library-status", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var isScanning = root.GetProperty("IsScanning").GetBoolean();
            var startedUtc = root.GetProperty("StartedUtc").ValueKind != JsonValueKind.Null;
            var completedUtc = root.GetProperty("CompletedUtc").ValueKind != JsonValueKind.Null;
            var lastError = root.GetProperty("LastError").GetString() ?? string.Empty;

            if (!isScanning && startedUtc && completedUtc)
            {
                return new LibraryScanSnapshot(
                    root.GetProperty("IndexedFiles").GetInt32(),
                    root.GetProperty("ScannedFiles").GetInt32(),
                    root.GetProperty("ScannedFolders").GetInt32(),
                    root.GetProperty("ScanDurationMs").GetInt64(),
                    lastError);
            }

            if (!isScanning && !string.IsNullOrWhiteSpace(lastError))
                throw new InvalidOperationException($"Library scan failed: {lastError}");

            await Task.Delay(25, cancellationToken);
        }
    }

    private sealed record LibraryScanSnapshot(
        int IndexedFiles,
        int ScannedFiles,
        int ScannedFolders,
        long ScanDurationMs,
        string LastError);
}
