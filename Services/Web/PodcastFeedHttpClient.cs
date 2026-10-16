using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace RemotePlay;

internal interface IPodcastHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal sealed class SystemPodcastHostResolver : IPodcastHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);
}

internal sealed class PodcastFeedHttpClient
{
    private const int MaxRedirects = 5;
    private static readonly TimeSpan HostResolutionTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly IPodcastHostResolver _hostResolver;

    internal const long MaxResponseBytes = 20L * 1024 * 1024;

    internal PodcastFeedHttpClient(HttpClient httpClient, IPodcastHostResolver hostResolver)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _hostResolver = hostResolver ?? throw new ArgumentNullException(nameof(hostResolver));
    }

    internal static PodcastFeedHttpClient CreateDefault()
    {
        var resolver = new SystemPodcastHostResolver();
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // A proxy would resolve the origin outside the validated connection path.
            UseProxy = false,
            ConnectCallback = (context, cancellationToken) =>
                ConnectToValidatedAddressAsync(
                    context.DnsEndPoint.Host,
                    context.DnsEndPoint.Port,
                    resolver,
                    OpenSocketAsync,
                    cancellationToken),
        };
        var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePlay/1.0");

        return new PodcastFeedHttpClient(httpClient, resolver);
    }

    internal async Task<HttpResponseMessage> GetAsync(
        Uri uri,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken = default,
        PodcastFeedValidators? validators = null)
    {
        if (!IsValidHttpUri(uri))
            throw new PodcastDestinationRejectedException();

        var currentUri = uri;
        var visitedUris = new HashSet<Uri>();
        var redirectCount = 0;

        while (true)
        {
            await ValidatePublicUriAsync(currentUri, cancellationToken).ConfigureAwait(false);

            if (!visitedUris.Add(currentUri))
                throw new PodcastDestinationRejectedException();

            HttpResponseMessage response;
            using (var request = new HttpRequestMessage(HttpMethod.Get, currentUri))
            {
                if (validators is { HasValidators: true }
                    && PodcastFeedCache.NormalizeUri(currentUri).Equals(
                        PodcastFeedCache.NormalizeUri(validators.Uri)))
                {
                    if (validators.ETag is not null)
                        request.Headers.IfNoneMatch.Add(validators.ETag);

                    if (validators.LastModified.HasValue)
                        request.Headers.IfModifiedSince = validators.LastModified;
                }

                response = await _httpClient.SendAsync(request, completionOption, cancellationToken)
                    .ConfigureAwait(false);
                response.RequestMessage ??= request;
            }

            if (!IsRedirect(response.StatusCode))
                return response;

            Uri nextUri;
            try
            {
                if (response.Headers.Location is not { } location)
                    throw new PodcastDestinationRejectedException();

                nextUri = location.IsAbsoluteUri
                    ? location
                    : new Uri(currentUri, location);

                if (!IsValidHttpUri(nextUri)
                    || (currentUri.Scheme == Uri.UriSchemeHttps && nextUri.Scheme == Uri.UriSchemeHttp)
                    || visitedUris.Contains(nextUri)
                    || redirectCount >= MaxRedirects)
                {
                    throw new PodcastDestinationRejectedException();
                }
            }
            catch (UriFormatException)
            {
                response.Dispose();
                throw new PodcastDestinationRejectedException();
            }
            catch
            {
                response.Dispose();
                throw;
            }

            response.Dispose();
            redirectCount++;
            currentUri = nextUri;
        }
    }

    internal async Task ValidatePublicUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (!IsValidHttpUri(uri))
            throw new PodcastDestinationRejectedException();

        await ResolvePublicAddressesAsync(uri.DnsSafeHost, _hostResolver, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static bool IsValidHttpUri(Uri? uri)
        => uri is { IsAbsoluteUri: true }
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
           && !string.IsNullOrWhiteSpace(uri.Host)
           && string.IsNullOrEmpty(uri.UserInfo)
           && !IsLocalHostName(uri.DnsSafeHost);

    internal static bool IsSyntacticallyPublicUrl(string? rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) || !IsValidHttpUri(uri))
            return false;

        return !IPAddress.TryParse(uri.DnsSafeHost, out var address) || IsPublicAddress(address);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
                return IsPublicAddress(address.MapToIPv4());

            if (address.ScopeId != 0
                || address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.IPv6Loopback)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast)
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xE0) != 0x20)
                return false;

            var isIetfProtocolAssignment = bytes[0] == 0x20
                                          && bytes[1] == 0x01
                                          && bytes[2] <= 0x01;
            var isDocumentationAddress = bytes[0] == 0x20
                                         && bytes[1] == 0x01
                                         && bytes[2] == 0x0D
                                         && bytes[3] == 0xB8;
            var isSixToFourAddress = bytes[0] == 0x20 && bytes[1] == 0x02;
            var isDocumentationPrefix = bytes[0] == 0x3F
                                        && bytes[1] == 0xFF
                                        && (bytes[2] & 0xF0) == 0x00;

            return !isIetfProtocolAssignment
                   && !isDocumentationAddress
                   && !isSixToFourAddress
                   && !isDocumentationPrefix;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var ipv4Bytes = address.GetAddressBytes();
        var ipv4 = ((uint)ipv4Bytes[0] << 24)
                   | ((uint)ipv4Bytes[1] << 16)
                   | ((uint)ipv4Bytes[2] << 8)
                   | ipv4Bytes[3];

        return !IsInIpv4Range(ipv4, 0x00000000, 8)
               && !IsInIpv4Range(ipv4, 0x0A000000, 8)
               && !IsInIpv4Range(ipv4, 0x64400000, 10)
               && !IsInIpv4Range(ipv4, 0x7F000000, 8)
               && !IsInIpv4Range(ipv4, 0xA9FE0000, 16)
               && !IsInIpv4Range(ipv4, 0xAC100000, 12)
               && !IsInIpv4Range(ipv4, 0xC0000000, 24)
               && !IsInIpv4Range(ipv4, 0xC0000200, 24)
               && !IsInIpv4Range(ipv4, 0xC0586300, 24)
               && !IsInIpv4Range(ipv4, 0xC0A80000, 16)
               && !IsInIpv4Range(ipv4, 0xC6120000, 15)
               && !IsInIpv4Range(ipv4, 0xC6336400, 24)
               && !IsInIpv4Range(ipv4, 0xCB007100, 24)
               && !IsInIpv4Range(ipv4, 0xE0000000, 4)
               && !IsInIpv4Range(ipv4, 0xF0000000, 4);
    }

    internal static async Task<MemoryStream> ReadBoundedContentAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (maxBytes <= 0 || maxBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        var contentLength = content.Headers.ContentLength;
        if (contentLength > maxBytes)
            throw new PodcastFeedTooLargeException();

        var initialCapacity = contentLength.HasValue ? (int)contentLength.Value : 0;
        var output = new MemoryStream(initialCapacity);
        try
        {
            await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[81920];
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                if (output.Length > maxBytes - read)
                    throw new PodcastFeedTooLargeException();

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            output.Position = 0;
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    internal static async ValueTask<Stream> ConnectToValidatedAddressAsync(
        string host,
        int port,
        IPodcastHostResolver hostResolver,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> connectToAddressAsync,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolvePublicAddressesAsync(host, hostResolver, cancellationToken)
            .ConfigureAwait(false);
        SocketException? lastSocketException = null;

        foreach (var address in addresses)
        {
            try
            {
                return await connectToAddressAsync(address, port, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                lastSocketException = ex;
            }
        }

        throw new HttpRequestException("Could not connect to the validated podcast host.", lastSocketException);
    }

    private static async Task<IPAddress[]> ResolvePublicAddressesAsync(
        string host,
        IPodcastHostResolver hostResolver,
        CancellationToken cancellationToken)
    {
        if (IsLocalHostName(host))
            throw new PodcastDestinationRejectedException();

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var address))
        {
            addresses = [address];
        }
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HostResolutionTimeout);
            try
            {
                addresses = await hostResolver.ResolveAsync(host, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PodcastHostResolutionException(ex);
            }
            catch (SocketException ex)
            {
                throw new PodcastHostResolutionException(ex);
            }
        }

        if (addresses.Length == 0)
            throw new PodcastHostResolutionException();

        if (addresses.Any(address => !IsPublicAddress(address)))
            throw new PodcastDestinationRejectedException();

        return addresses;
    }

    private static bool IsLocalHostName(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    private static bool IsInIpv4Range(uint address, uint network, int prefixLength)
    {
        var mask = uint.MaxValue << (32 - prefixLength);
        return (address & mask) == network;
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MultipleChoices
            or HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static async ValueTask<Stream> OpenSocketAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

internal sealed class PodcastDestinationRejectedException : Exception
{
    internal PodcastDestinationRejectedException()
        : base("Podcast URL destination is not allowed.")
    {
    }
}

internal sealed class PodcastHostResolutionException : HttpRequestException
{
    internal PodcastHostResolutionException()
        : base("Podcast host could not be resolved.")
    {
    }

    internal PodcastHostResolutionException(Exception innerException)
        : base("Podcast host could not be resolved.", innerException)
    {
    }
}

internal sealed class PodcastFeedTooLargeException : Exception
{
    internal PodcastFeedTooLargeException()
        : base("Podcast feed exceeded the response size limit.")
    {
    }
}

internal sealed class PodcastFeedTooManyAudioHostsException : Exception
{
    internal PodcastFeedTooManyAudioHostsException()
        : base("Podcast feed exceeded the audio host validation limit.")
    {
    }
}

internal sealed class PodcastFeedContentRejectedException : Exception
{
    internal PodcastFeedContentRejectedException()
        : base("Podcast feed did not contain a safe playable episode.")
    {
    }
}
