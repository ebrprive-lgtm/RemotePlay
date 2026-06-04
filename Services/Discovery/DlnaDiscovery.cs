using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Timer = System.Threading.Timer;

namespace RemotePlay.Services.Discovery;

/// <summary>Represents a DLNA / UPnP media renderer discovered on the LAN.</summary>
internal sealed record DiscoveredRenderer
{
    /// <summary>Friendly display name from the device description XML.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>IP address of the renderer.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>Absolute URL of the AVTransport control endpoint.</summary>
    public string ControlUrl { get; init; } = string.Empty;

    /// <summary>UPnP USN used for deduplication.</summary>
    public string Usn { get; init; } = string.Empty;

    public DateTimeOffset LastSeenUtc { get; init; }
}

/// <summary>
/// Discovers UPnP / DLNA MediaRenderer devices on the LAN using SSDP M-SEARCH
/// and provides helpers to send AVTransport SOAP commands.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Real network I/O paths are excluded; parsing helpers are tested separately.")]
internal sealed class DlnaDiscovery : IDisposable
{
    // SSDP multicast group and port defined by the UPnP specification.
    internal const string SsdpMulticastAddress = "239.255.255.250";
    internal const int SsdpPort = 1900;
    internal const string MediaRendererTarget = "urn:schemas-upnp-org:device:MediaRenderer:1";

    private static readonly TimeSpan RendererExpiry   = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ScanInterval     = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SsdpResponseWait = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<string, DiscoveredRenderer> _renderers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private Timer? _scanTimer;
    private CancellationTokenSource? _scanCts;
    private bool _disposed;

    /// <summary>
    /// Creates a <see cref="DlnaDiscovery"/> that owns its own <see cref="HttpClient"/>.
    /// </summary>
    public DlnaDiscovery() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, ownsClient: true) { }

    /// <summary>
    /// Creates a <see cref="DlnaDiscovery"/> with an injected <see cref="HttpClient"/>
    /// (used by tests so no real HTTP calls are made).
    /// </summary>
    internal DlnaDiscovery(HttpClient httpClient, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient  = httpClient;
        _ownsHttpClient = ownsClient;
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    /// <summary>Starts the periodic SSDP scan loop.</summary>
    public void Start()
    {
        _scanCts   = new CancellationTokenSource();
        _scanTimer = new Timer(_ => _ = ScanAsync(_scanCts.Token), null, TimeSpan.Zero, ScanInterval);
        Logger.Info("DlnaDiscovery started");
    }

    /// <summary>Stops the scan loop.</summary>
    public void Stop()
    {
        _scanTimer?.Dispose();
        _scanTimer = null;
        _scanCts?.Cancel();
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Returns all non-stale renderers, removing expired ones.</summary>
    public DiscoveredRenderer[] GetRenderers()
    {
        var now      = DateTimeOffset.UtcNow;
        var staleKeys = _renderers
            .Where(kv => now - kv.Value.LastSeenUtc > RendererExpiry)
            .Select(kv => kv.Key)
            .ToArray();

        foreach (var key in staleKeys)
            _renderers.TryRemove(key, out _);

        return _renderers.Values.OrderBy(r => r.Name).ToArray();
    }

    /// <summary>
    /// Sends a DLNA SetAVTransportURI + Play sequence to the specified control URL.
    /// Returns (true, null) on success or (false, errorMessage) on failure.
    /// </summary>
    public async Task<(bool Ok, string? Error)> PlayOnRendererAsync(string controlUrl, string mediaUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(controlUrl);
        ArgumentNullException.ThrowIfNull(mediaUrl);

        try
        {
            var setUri = BuildSetAvTransportUriSoap(mediaUrl);
            var setResult = await PostSoapAsync(controlUrl, "SetAVTransportURI", setUri, ct).ConfigureAwait(false);
            if (!setResult.Ok) return setResult;

            var play = BuildPlaySoap();
            return await PostSoapAsync(controlUrl, "Play", play, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error("DlnaDiscovery.PlayOnRendererAsync", ex);
            return (false, ex.Message);
        }
    }

    // ── SSDP scan ──────────────────────────────────────────────────────────────

    private async Task ScanAsync(CancellationToken ct)
    {
        try
        {
            var responses = await SendSsdpSearchAsync(ct).ConfigureAwait(false);
            foreach (var location in responses)
            {
                if (ct.IsCancellationRequested) break;
                await TryFetchAndRegisterRendererAsync(location, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error("DlnaDiscovery scan error", ex);
        }
    }

    private async Task<IReadOnlyList<string>> SendSsdpSearchAsync(CancellationToken ct)
    {
        var locations = new List<string>();
        var multicastEp = new IPEndPoint(IPAddress.Parse(SsdpMulticastAddress), SsdpPort);
        var msearch = BuildMSearchMessage(MediaRendererTarget);
        var bytes   = Encoding.UTF8.GetBytes(msearch);

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            await udp.SendAsync(bytes, bytes.Length, multicastEp).WithCancellation(ct).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + SsdpResponseWait;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                udp.Client.ReceiveTimeout = (int)Math.Max(200, (deadline - DateTime.UtcNow).TotalMilliseconds);
                try
                {
                    var result   = await udp.ReceiveAsync().WithCancellation(ct).ConfigureAwait(false);
                    var response = Encoding.UTF8.GetString(result.Buffer);
                    var location = ParseLocationFromSsdpResponse(response);
                    if (location is not null && !locations.Contains(location, StringComparer.OrdinalIgnoreCase))
                        locations.Add(location);
                }
                catch (SocketException) { break; }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (SocketException ex)
        {
            Logger.Warning("DlnaDiscovery", $"SSDP send failed: {ex.Message}");
        }

        return locations;
    }

    private async Task TryFetchAndRegisterRendererAsync(string location, CancellationToken ct)
    {
        try
        {
            var xml = await _httpClient.GetStringAsync(location, ct).ConfigureAwait(false);
            var renderer = ParseDeviceDescription(xml, location);
            if (renderer is null) return;

            _renderers[renderer.Usn] = renderer with { LastSeenUtc = DateTimeOffset.UtcNow };
            Logger.Detail("DlnaDiscovery", $"Renderer found: {renderer.Name} @ {renderer.ControlUrl}");
        }
        catch (Exception ex)
        {
            Logger.Detail("DlnaDiscovery", $"Failed to fetch device description from {location}: {ex.Message}");
        }
    }

    // ── SOAP helpers ───────────────────────────────────────────────────────────

    private async Task<(bool Ok, string? Error)> PostSoapAsync(string controlUrl, string action, string soapBody, CancellationToken ct)
    {
        using var content = new StringContent(soapBody, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", $"\"urn:schemas-upnp-org:service:AVTransport:1#{action}\"");

        using var response = await _httpClient.PostAsync(controlUrl, content, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return (true, null);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (false, $"HTTP {(int)response.StatusCode}: {body}");
    }

    // ── Static parsing helpers (internal so tests can exercise them) ───────────

    /// <summary>Builds an SSDP M-SEARCH multicast message for the given search target.</summary>
    internal static string BuildMSearchMessage(string searchTarget) =>
        $"M-SEARCH * HTTP/1.1\r\n" +
        $"HOST: {SsdpMulticastAddress}:{SsdpPort}\r\n" +
        $"MAN: \"ssdp:discover\"\r\n" +
        $"MX: 3\r\n" +
        $"ST: {searchTarget}\r\n" +
        $"\r\n";

    /// <summary>
    /// Extracts the LOCATION header value from a raw SSDP response.
    /// Returns <c>null</c> if not found.
    /// </summary>
    internal static string? ParseLocationFromSsdpResponse(string ssdpResponse)
    {
        if (string.IsNullOrWhiteSpace(ssdpResponse)) return null;

        foreach (var line in ssdpResponse.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                return trimmed["LOCATION:".Length..].Trim();
        }
        return null;
    }

    /// <summary>
    /// Parses a UPnP device-description XML document and returns a
    /// <see cref="DiscoveredRenderer"/> if the device is a MediaRenderer.
    /// Returns <c>null</c> when the XML is invalid or not a MediaRenderer.
    /// </summary>
    internal static DiscoveredRenderer? ParseDeviceDescription(string xml, string locationUrl)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = "urn:schemas-upnp-org:device-1-0";

            var device = doc.Descendants(ns + "device").FirstOrDefault();
            if (device is null) return null;

            var deviceType = device.Element(ns + "deviceType")?.Value ?? string.Empty;
            if (!deviceType.Contains("MediaRenderer", StringComparison.OrdinalIgnoreCase))
                return null;

            var friendlyName = device.Element(ns + "friendlyName")?.Value
                               ?? device.Element(ns + "modelName")?.Value
                               ?? "Unknown Renderer";

            var usn = device.Element(ns + "UDN")?.Value ?? locationUrl;

            // Locate the AVTransport service and its control URL.
            var services = device.Descendants(ns + "service");
            var avTransport = services.FirstOrDefault(s =>
                (s.Element(ns + "serviceType")?.Value ?? string.Empty)
                .Contains("AVTransport", StringComparison.OrdinalIgnoreCase));

            if (avTransport is null) return null;

            var relControlUrl = avTransport.Element(ns + "controlURL")?.Value;
            if (string.IsNullOrWhiteSpace(relControlUrl)) return null;

            // Build the absolute control URL from the LOCATION base.
            var controlUrl = BuildAbsoluteUrl(locationUrl, relControlUrl);
            if (controlUrl is null) return null;

            // Extract the host for display purposes.
            var host = Uri.TryCreate(locationUrl, UriKind.Absolute, out var baseUri)
                ? baseUri.Host
                : locationUrl;

            return new DiscoveredRenderer
            {
                Name       = friendlyName.Trim(),
                Host       = host,
                ControlUrl = controlUrl,
                Usn        = usn,
                LastSeenUtc = DateTimeOffset.UtcNow,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Builds the SOAP body for SetAVTransportURI.</summary>
    internal static string BuildSetAvTransportUriSoap(string mediaUrl)
    {
        var escapedUrl = System.Security.SecurityElement.Escape(mediaUrl) ?? mediaUrl;
        return
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
              "<s:Body>" +
                "<u:SetAVTransportURI xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">" +
                  "<InstanceID>0</InstanceID>" +
                 $"<CurrentURI>{escapedUrl}</CurrentURI>" +
                  "<CurrentURIMetaData></CurrentURIMetaData>" +
                "</u:SetAVTransportURI>" +
              "</s:Body>" +
            "</s:Envelope>";
    }

    /// <summary>Builds the SOAP body for Play.</summary>
    internal static string BuildPlaySoap() =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
          "<s:Body>" +
            "<u:Play xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">" +
              "<InstanceID>0</InstanceID>" +
              "<Speed>1</Speed>" +
            "</u:Play>" +
          "</s:Body>" +
        "</s:Envelope>";

    /// <summary>
    /// Combines a base LOCATION URL with a (possibly relative) control URL
    /// to produce an absolute URL. Returns <c>null</c> on failure.
    /// </summary>
    internal static string? BuildAbsoluteUrl(string locationUrl, string relativeUrl)
    {
        if (Uri.TryCreate(relativeUrl, UriKind.Absolute, out _))
            return relativeUrl;

        if (!Uri.TryCreate(locationUrl, UriKind.Absolute, out var baseUri))
            return null;

        if (Uri.TryCreate(baseUri, relativeUrl, out var combined))
            return combined.ToString();

        return null;
    }

    // ── IDisposable ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _scanCts?.Dispose();
        _scanTimer?.Dispose();
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}

/// <summary>Extension helpers for async/await with cancellation on pre-.NET 6 UdpClient.</summary>
file static class UdpClientExtensions
{
    internal static async Task<T> WithCancellation<T>(this Task<T> task, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        await using var reg = ct.Register(s => ((TaskCompletionSource<bool>)s!).TrySetResult(true), tcs);
        if (task == await Task.WhenAny(task, tcs.Task).ConfigureAwait(false))
            return await task.ConfigureAwait(false);
        throw new OperationCanceledException(ct);
    }
}
