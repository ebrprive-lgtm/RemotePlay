using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RemotePlay;
using RemotePlay.Models;
using Xunit;

namespace RemotePlay.Tests;

public sealed class WebServerProgressiveVideoStreamTests
{
    [Fact]
    public async Task ProgressiveStreamDeliversFirstFragmentBeforeConversionFinishesAndSupportsRangeReplay()
    {
        var firstFragment = CreateFirstFragment();
        var finalFragment = CreateMovieFragment("last-frame");
        using var host = new ProgressiveVideoStreamTestHost(firstFragment, finalFragment);
        var sourcePath = host.WriteSource("progressive.mkv");
        var transcodeId = Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, host.GetProgressiveStreamUrl(sourcePath, transcodeId));

        using var response = await host.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var firstBytes = new byte[firstFragment.Length];
        await stream.ReadExactlyAsync(firstBytes, timeout.Token);

        Assert.Equal(firstFragment, firstBytes);
        Assert.False(File.Exists(host.GetReleasePath(sourcePath)));

        using var activeRangeRequest = new HttpRequestMessage(
            HttpMethod.Get,
            host.GetProgressiveStreamUrl(sourcePath, Guid.NewGuid().ToString("N")));
        activeRangeRequest.Headers.TryAddWithoutValidation("Range", "bytes=0-");
        using var activeRangeResponse = await host.Client.SendAsync(
            activeRangeRequest,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        Assert.Equal(HttpStatusCode.OK, activeRangeResponse.StatusCode);
        Assert.True(activeRangeResponse.Headers.TransferEncodingChunked);
        await using var activeRangeStream = await activeRangeResponse.Content.ReadAsStreamAsync(timeout.Token);
        var activeRangeFirstBytes = new byte[firstFragment.Length];
        await activeRangeStream.ReadExactlyAsync(activeRangeFirstBytes, timeout.Token);
        Assert.Equal(firstFragment, activeRangeFirstBytes);

        using var nonzeroRangeRequest = new HttpRequestMessage(
            HttpMethod.Get,
            host.GetProgressiveStreamUrl(sourcePath, Guid.NewGuid().ToString("N")));
        nonzeroRangeRequest.Headers.Range = new RangeHeaderValue(4, 16);
        var nonzeroRangeTask = host.Client.SendAsync(nonzeroRangeRequest, timeout.Token);
        await host.WaitForProbeCountAsync(3, timeout.Token);
        Assert.False(nonzeroRangeTask.IsCompleted);

        File.WriteAllText(host.GetReleasePath(sourcePath), "continue");
        using var firstRemaining = new MemoryStream();
        using var secondRemaining = new MemoryStream();
        var firstCopy = stream.CopyToAsync(firstRemaining, timeout.Token);
        var secondCopy = activeRangeStream.CopyToAsync(secondRemaining, timeout.Token);
        await Task.WhenAll(firstCopy, secondCopy, nonzeroRangeTask);
        var expected = firstFragment.Concat(finalFragment).ToArray();
        Assert.Equal(finalFragment, firstRemaining.ToArray());
        Assert.Equal(finalFragment, secondRemaining.ToArray());
        using var nonzeroRangeResponse = await nonzeroRangeTask;
        Assert.Equal(HttpStatusCode.PartialContent, nonzeroRangeResponse.StatusCode);
        Assert.Equal(
            expected.Skip(4).Take(13),
            await nonzeroRangeResponse.Content.ReadAsByteArrayAsync(timeout.Token));

        var expectedRanges = Enumerable.Range(0, 16)
            .Select(index => (Start: index * 4, Length: Math.Min(12, expected.Length - index * 4)));
        foreach (var (start, length) in expectedRanges)
        {
            using var rangeRequest = new HttpRequestMessage(
                HttpMethod.Get,
                host.GetProgressiveStreamUrl(sourcePath, Guid.NewGuid().ToString("N")));
            rangeRequest.Headers.Range = new RangeHeaderValue(start, start + length - 1);
            using var rangeResponse = await host.Client.SendAsync(rangeRequest, timeout.Token);

            Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
            Assert.Equal(expected.Skip(start).Take(length), await rangeResponse.Content.ReadAsByteArrayAsync(timeout.Token));
        }

        Assert.Empty(Directory.EnumerateFiles(host.CacheDirectory, "*.partial"));
        Assert.Single(Directory.EnumerateFiles(host.CacheDirectory, "*.mp4"));
    }

    [Fact]
    public async Task CancellingProgressiveSubscriberStopsConversionAndCleansPartialFiles()
    {
        var firstFragment = CreateFirstFragment();
        using var host = new ProgressiveVideoStreamTestHost(firstFragment, CreateMovieFragment("unused"));
        var sourcePath = host.WriteSource("cancel-progressive.mkv");
        var transcodeId = Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, host.GetProgressiveStreamUrl(sourcePath, transcodeId));
        using var response = await host.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var firstBytes = new byte[firstFragment.Length];
        await stream.ReadExactlyAsync(firstBytes, timeout.Token);
        Assert.Equal(firstFragment, firstBytes);

        using var cancelResponse = await host.Client.PostAsync(
            host.GetCancelUrl(sourcePath, transcodeId),
            content: null,
            timeout.Token);
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);
        using (var cancelJson = System.Text.Json.JsonDocument.Parse(
            await cancelResponse.Content.ReadAsStringAsync(timeout.Token)))
        {
            Assert.True(cancelJson.RootElement.GetProperty("cancelled").GetBoolean());
        }

        await stream.CopyToAsync(Stream.Null, timeout.Token);
        await host.WaitForPartialFilesToClearAsync(timeout.Token);
        Assert.Empty(Directory.EnumerateFiles(host.CacheDirectory, "*.partial"));
        Assert.Empty(Directory.EnumerateFiles(host.CacheDirectory, "*.mp4"));
    }

    private static byte[] CreateFirstFragment() =>
        CreateBox("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0"))
            .Concat(CreateBox("moov", []))
            .Concat(CreateMovieFragment("first-frame"))
            .ToArray();

    private static byte[] CreateMovieFragment(string payload) =>
        CreateBox("moof", [])
            .Concat(CreateBox("mdat", Encoding.UTF8.GetBytes(payload)))
            .ToArray();

    private static byte[] CreateBox(string type, byte[] payload)
    {
        var bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private sealed class ProgressiveVideoStreamTestHost : IDisposable
    {
        private readonly string _temporaryDirectory;
        private readonly WebServer _server;
        private readonly byte[] _firstFragment;
        private readonly byte[] _finalFragment;
        private int _mediaProbeCount;

        internal ProgressiveVideoStreamTestHost(byte[] firstFragment, byte[] finalFragment)
        {
            _firstFragment = firstFragment;
            _finalFragment = finalFragment;
            _temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "RemotePlayProgressiveVideoTests",
                Guid.NewGuid().ToString("N"));
            MoviesRoot = Path.Combine(_temporaryDirectory, "movies");
            CacheDirectory = Path.Combine(_temporaryDirectory, "transcode-cache");
            Directory.CreateDirectory(MoviesRoot);
            Directory.CreateDirectory(CacheDirectory);

            var transcoder = new VideoAudioCompatibilityTranscoder(
                cacheDirectory: CacheDirectory,
                processStartInfoFactory: CreateFakeProcessStartInfo,
                mediaInfoProbe: ProbeMediaInfoAsync);
            var started = WebServerTestServer.Start(port => new WebServer(
                new AppConfig
                {
                    Port = port,
                    AdditionalMoviesPaths = [MoviesRoot],
                    MaxRequestsPerIpPerWindow = 0,
                },
                WebServerApiTests.BuildStubCallbacks(),
                podcastTopPoolCacheFilePath: Path.Combine(_temporaryDirectory, "podcast-cache.json"),
                libraryIndexCacheFilePath: Path.Combine(_temporaryDirectory, "library-index.json"),
                videoAudioCompatibilityTranscoder: transcoder));
            _server = started.Server;
            Port = started.Port;
            Client = new HttpClient { BaseAddress = new Uri($"http://localhost:{Port}") };
        }

        internal string MoviesRoot { get; }
        internal string CacheDirectory { get; }
        internal int Port { get; }
        internal HttpClient Client { get; }

        internal string WriteSource(string name)
        {
            var path = Path.Combine(MoviesRoot, name);
            File.WriteAllBytes(path, [0x52, 0x50, 0x01]);
            return path;
        }

        internal string GetProgressiveStreamUrl(string sourcePath, string transcodeId) =>
            "/api/video/stream?path="
            + Uri.EscapeDataString(WebPathHelpers.EncodePath(sourcePath))
            + "&audioCompat=auto&progressive=1&audioCodecs=opus&transcodeId="
            + Uri.EscapeDataString(transcodeId);

        internal string GetCancelUrl(string sourcePath, string transcodeId) =>
            "/api/video/transcode-cancel?path="
            + Uri.EscapeDataString(WebPathHelpers.EncodePath(sourcePath))
            + "&transcodeId="
            + Uri.EscapeDataString(transcodeId);

        internal string GetReleasePath(string sourcePath) =>
            Path.Combine(_temporaryDirectory, Path.GetFileNameWithoutExtension(sourcePath) + ".release");

        internal async Task WaitForProbeCountAsync(int expected, CancellationToken cancellationToken)
        {
            while (Volatile.Read(ref _mediaProbeCount) < expected)
                await Task.Delay(10, cancellationToken);
        }

        internal async Task WaitForPartialFilesToClearAsync(CancellationToken cancellationToken)
        {
            while (Directory.EnumerateFiles(CacheDirectory, "*.partial").Any())
                await Task.Delay(20, cancellationToken);
        }

        private ProcessStartInfo CreateFakeProcessStartInfo(
            string sourcePath,
            string temporaryOutputPath,
            VideoAudioCompatibilityMode mode,
            bool progressive)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32",
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            string script;
            if (progressive)
            {
                var gatePath = PowerShellLiteral(GetReleasePath(sourcePath));
                var first = Convert.ToBase64String(_firstFragment);
                var tail = Convert.ToBase64String(_finalFragment);
                script = "$ErrorActionPreference='Stop';"
                    + "$s=[Console]::OpenStandardOutput();"
                    + $"$b=[Convert]::FromBase64String('{first}');$s.Write($b,0,$b.Length);$s.Flush();"
                    + $"while(-not(Test-Path -LiteralPath '{gatePath}')){{Start-Sleep -Milliseconds 20}};"
                    + $"$b=[Convert]::FromBase64String('{tail}');$s.Write($b,0,$b.Length);$s.Flush();";
            }
            else
            {
                var source = PowerShellLiteral(sourcePath);
                var destination = PowerShellLiteral(temporaryOutputPath);
                script = "$ErrorActionPreference='Stop';"
                    + $"Copy-Item -LiteralPath '{source}' -Destination '{destination}';";
            }

            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            return startInfo;
        }

        private Task<MediaCodecInfo> ProbeMediaInfoAsync(string sourcePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _mediaProbeCount);
            return Task.FromResult(new MediaCodecInfo
            {
                FilePath = sourcePath,
                AudioTracks = [new AudioTrackInfo { Codec = "opus", Channels = 2 }],
            });
        }

        private static string PowerShellLiteral(string value) =>
            value.Replace("'", "''", StringComparison.Ordinal);

        public void Dispose()
        {
            Client.Dispose();
            _server.Stop();
            if (Directory.Exists(_temporaryDirectory))
                Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
