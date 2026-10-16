using System.IO;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace RemotePlay.Tests;

public sealed class PodcastFeedHttpClientTests
{
    [Theory]
    [InlineData("93.184.216.34", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.0.0.1", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("192.88.99.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("198.51.100.1", false)]
    [InlineData("203.0.113.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("240.0.0.1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("::", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2002:c000:0201::1", false)]
    [InlineData("3fff::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    public void IsPublicAddress_RejectsNonGlobalAddresses(string address, bool expected)
    {
        Assert.Equal(expected, PodcastFeedHttpClient.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("https://feed.example/feed.xml", true)]
    [InlineData("http://127.0.0.1/feed.xml", false)]
    [InlineData("http://[::1]/feed.xml", false)]
    [InlineData("https://[2001:db8::1]/feed.xml", false)]
    [InlineData("https://[2606:4700:4700::1111]/feed.xml", true)]
    [InlineData("http://[::ffff:127.0.0.1]/feed.xml", false)]
    [InlineData("https://user:password@feed.example/feed.xml", false)]
    [InlineData("file:///C:/feed.xml", false)]
    [InlineData("https://localhost/feed.xml", false)]
    public void IsSyntacticallyPublicUrl_RejectsInvalidOrUnsafeUrls(string url, bool expected)
    {
        Assert.Equal(expected, PodcastFeedHttpClient.IsSyntacticallyPublicUrl(url));
    }

    [Fact]
    public async Task ConnectToValidatedAddress_UsesTheResolvedPublicAddress()
    {
        var publicAddress = IPAddress.Parse("93.184.216.34");
        var resolver = new SequencePodcastHostResolver([publicAddress]);
        IPAddress? connectedAddress = null;

        using var stream = await PodcastFeedHttpClient.ConnectToValidatedAddressAsync(
            "feed.example",
            443,
            resolver,
            (address, _, _) =>
            {
                connectedAddress = address;
                return ValueTask.FromResult<Stream>(new MemoryStream());
            },
            CancellationToken.None);

        Assert.Equal(publicAddress, connectedAddress);
    }

    [Fact]
    public async Task ConnectToValidatedAddress_RejectsDnsRebindingBeforeConnecting()
    {
        var resolver = new SequencePodcastHostResolver(
            [IPAddress.Parse("93.184.216.34")],
            [IPAddress.Parse("10.10.10.10")]);
        using var httpClient = new HttpClient(new EmptyHttpMessageHandler());
        var client = new PodcastFeedHttpClient(httpClient, resolver);
        var uri = new Uri("https://feed.example/feed.xml");
        var connectionAttempts = 0;

        await client.ValidatePublicUriAsync(uri);

        await Assert.ThrowsAsync<PodcastDestinationRejectedException>(async () =>
            await PodcastFeedHttpClient.ConnectToValidatedAddressAsync(
                "feed.example",
                443,
                resolver,
                (_, _, _) =>
                {
                    connectionAttempts++;
                    return ValueTask.FromResult<Stream>(new MemoryStream());
                },
                CancellationToken.None));

        Assert.Equal(0, connectionAttempts);
    }

    [Fact]
    public async Task ConnectToValidatedAddress_RejectsMixedPublicAndPrivateDnsResults()
    {
        var resolver = new SequencePodcastHostResolver(
            [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("192.168.1.20")]);
        var connectionAttempts = 0;

        await Assert.ThrowsAsync<PodcastDestinationRejectedException>(async () =>
            await PodcastFeedHttpClient.ConnectToValidatedAddressAsync(
                "feed.example",
                443,
                resolver,
                (_, _, _) =>
                {
                    connectionAttempts++;
                    return ValueTask.FromResult<Stream>(new MemoryStream());
                },
                CancellationToken.None));

        Assert.Equal(0, connectionAttempts);
    }

    [Fact]
    public async Task ReadBoundedContentAsync_RejectsChunkedContentOverTheLimit()
    {
        using var content = new StreamContent(new NonSeekableByteStream(64));

        await Assert.ThrowsAsync<PodcastFeedTooLargeException>(
            () => PodcastFeedHttpClient.ReadBoundedContentAsync(content, 16));
    }

    [Fact]
    public async Task ReadBoundedContentAsync_AcceptsContentAtTheLimit()
    {
        using var content = new StreamContent(new NonSeekableByteStream(16));

        using var body = await PodcastFeedHttpClient.ReadBoundedContentAsync(content, 16);

        Assert.Equal(16, body.Length);
    }

    private sealed class SequencePodcastHostResolver : IPodcastHostResolver
    {
        private readonly Queue<IPAddress[]> _responses;

        internal SequencePodcastHostResolver(params IPAddress[][] responses)
        {
            _responses = new Queue<IPAddress[]>(responses);
        }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.Count == 0)
                throw new InvalidOperationException("No DNS response was configured for this test.");

            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class EmptyHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("No HTTP request was expected.");
    }

    private sealed class NonSeekableByteStream(int length) : Stream
    {
        private int _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, _remaining);
            Array.Clear(buffer, offset, read);
            _remaining -= read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(buffer.Length, _remaining);
            buffer.Span[..read].Clear();
            _remaining -= read;
            return ValueTask.FromResult(read);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
