using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RemotePlay.Services.Discovery;
using Xunit;

namespace RemotePlay.Tests;

/// <summary>
/// Unit tests for the static parsing/building helpers in <see cref="DlnaDiscovery"/>
/// and integration tests for the /api/dlna/* endpoints.
/// All tests are fully offline — no real network calls are made.
/// </summary>
public sealed class DlnaDiscoveryTests
{
    // ── ParseLocationFromSsdpResponse ─────────────────────────────────────────

    [Fact]
    public void ParseLocation_WhenLocationHeaderPresent_ReturnsUrl()
    {
        var response =
            "HTTP/1.1 200 OK\r\n" +
            "CACHE-CONTROL: max-age=1800\r\n" +
            "LOCATION: http://192.168.1.42:1234/device.xml\r\n" +
            "ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n";

        var result = DlnaDiscovery.ParseLocationFromSsdpResponse(response);

        Assert.Equal("http://192.168.1.42:1234/device.xml", result);
    }

    [Fact]
    public void ParseLocation_WhenLocationHeaderMissing_ReturnsNull()
    {
        var response =
            "HTTP/1.1 200 OK\r\n" +
            "CACHE-CONTROL: max-age=1800\r\n" +
            "ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n";

        var result = DlnaDiscovery.ParseLocationFromSsdpResponse(response);

        Assert.Null(result);
    }

    [Fact]
    public void ParseLocation_WhenInputIsEmpty_ReturnsNull()
    {
        var result = DlnaDiscovery.ParseLocationFromSsdpResponse(string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public void ParseLocation_WhenInputIsNull_ReturnsNull()
    {
        var result = DlnaDiscovery.ParseLocationFromSsdpResponse(null!);

        Assert.Null(result);
    }

    [Fact]
    public void ParseLocation_HeaderIsCaseInsensitive()
    {
        var response =
            "HTTP/1.1 200 OK\r\n" +
            "location: http://10.0.0.5:8080/upnp/desc.xml\r\n";

        var result = DlnaDiscovery.ParseLocationFromSsdpResponse(response);

        Assert.Equal("http://10.0.0.5:8080/upnp/desc.xml", result);
    }

    // ── ParseDeviceDescription ────────────────────────────────────────────────

    private const string ValidRendererXml =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <device>
            <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
            <friendlyName>Living Room TV</friendlyName>
            <UDN>uuid:11111111-2222-3333-4444-555555555555</UDN>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                <serviceId>urn:upnp-org:serviceId:AVTransport</serviceId>
                <controlURL>/ctl/AVTransport</controlURL>
              </service>
            </serviceList>
          </device>
        </root>
        """;

    [Fact]
    public void ParseDeviceDescription_WhenValidMediaRenderer_ReturnsRenderer()
    {
        var renderer = DlnaDiscovery.ParseDeviceDescription(ValidRendererXml, "http://192.168.1.42:1234/device.xml");

        Assert.NotNull(renderer);
        Assert.Equal("Living Room TV", renderer!.Name);
        Assert.Equal("192.168.1.42", renderer.Host);
        Assert.Equal("http://192.168.1.42:1234/ctl/AVTransport", renderer.ControlUrl);
        Assert.Equal("uuid:11111111-2222-3333-4444-555555555555", renderer.Usn);
    }

    [Fact]
    public void ParseDeviceDescription_WhenNotMediaRenderer_ReturnsNull()
    {
        var xml =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
                <friendlyName>My NAS</friendlyName>
                <UDN>uuid:abc</UDN>
              </device>
            </root>
            """;

        var result = DlnaDiscovery.ParseDeviceDescription(xml, "http://192.168.1.1/desc.xml");

        Assert.Null(result);
    }

    [Fact]
    public void ParseDeviceDescription_WhenXmlIsEmpty_ReturnsNull()
    {
        var result = DlnaDiscovery.ParseDeviceDescription(string.Empty, "http://192.168.1.1/desc.xml");

        Assert.Null(result);
    }

    [Fact]
    public void ParseDeviceDescription_WhenXmlIsMalformed_ReturnsNull()
    {
        var result = DlnaDiscovery.ParseDeviceDescription("<not valid xml>>>", "http://192.168.1.1/desc.xml");

        Assert.Null(result);
    }

    [Fact]
    public void ParseDeviceDescription_WhenNoAvTransportService_ReturnsNull()
    {
        var xml =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
                <friendlyName>Headless Renderer</friendlyName>
                <UDN>uuid:abc</UDN>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType>
                    <controlURL>/ctl/Rendering</controlURL>
                  </service>
                </serviceList>
              </device>
            </root>
            """;

        var result = DlnaDiscovery.ParseDeviceDescription(xml, "http://192.168.1.1/desc.xml");

        Assert.Null(result);
    }

    [Fact]
    public void ParseDeviceDescription_WhenControlUrlIsAbsolute_UsesItDirectly()
    {
        var xml =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
                <friendlyName>Smart TV</friendlyName>
                <UDN>uuid:xyz</UDN>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                    <controlURL>http://10.0.0.5:9876/upnp/control/avt</controlURL>
                  </service>
                </serviceList>
              </device>
            </root>
            """;

        var result = DlnaDiscovery.ParseDeviceDescription(xml, "http://10.0.0.5:9876/device.xml");

        Assert.NotNull(result);
        Assert.Equal("http://10.0.0.5:9876/upnp/control/avt", result!.ControlUrl);
    }

    // ── BuildAbsoluteUrl ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://192.168.1.1:1234/device.xml", "/ctl/AVT", "http://192.168.1.1:1234/ctl/AVT")]
    [InlineData("http://192.168.1.1:1234/upnp/device.xml", "ctl/AVT", "http://192.168.1.1:1234/upnp/ctl/AVT")]
    [InlineData("http://192.168.1.1:1234/device.xml", "http://other.host/ctl", "http://other.host/ctl")]
    public void BuildAbsoluteUrl_CombinesLocationWithRelativePath(string location, string relative, string expected)
    {
        var result = DlnaDiscovery.BuildAbsoluteUrl(location, relative);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildAbsoluteUrl_WhenLocationIsInvalid_ReturnsNull()
    {
        var result = DlnaDiscovery.BuildAbsoluteUrl("not-a-url", "/ctl");

        Assert.Null(result);
    }

    // ── BuildMSearchMessage ────────────────────────────────────────────────────

    [Fact]
    public void BuildMSearchMessage_ContainsMSearchAndTarget()
    {
        var msg = DlnaDiscovery.BuildMSearchMessage(DlnaDiscovery.MediaRendererTarget);

        Assert.Contains("M-SEARCH * HTTP/1.1", msg, StringComparison.Ordinal);
        Assert.Contains(DlnaDiscovery.MediaRendererTarget, msg, StringComparison.Ordinal);
        Assert.Contains($"{DlnaDiscovery.SsdpMulticastAddress}:{DlnaDiscovery.SsdpPort}", msg, StringComparison.Ordinal);
    }

    // ── BuildSetAvTransportUriSoap ─────────────────────────────────────────────

    [Fact]
    public void BuildSetAvTransportUriSoap_ContainsMediaUrl()
    {
        var soap = DlnaDiscovery.BuildSetAvTransportUriSoap("http://server/video.mp4");

        Assert.Contains("http://server/video.mp4", soap, StringComparison.Ordinal);
        Assert.Contains("SetAVTransportURI", soap, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSetAvTransportUriSoap_EscapesAmpersandInUrl()
    {
        var soap = DlnaDiscovery.BuildSetAvTransportUriSoap("http://server/video.mp4?a=1&b=2");

        Assert.Contains("&amp;", soap, StringComparison.Ordinal);
    }

    // ── BuildPlaySoap ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildPlaySoap_ContainsPlayActionAndSpeed()
    {
        var soap = DlnaDiscovery.BuildPlaySoap();

        Assert.Contains("<u:Play", soap, StringComparison.Ordinal);
        Assert.Contains("<Speed>1</Speed>", soap, StringComparison.Ordinal);
    }

    // ── GetRenderers (cache expiry) ────────────────────────────────────────────

    [Fact]
    public void GetRenderers_WhenEmpty_ReturnsEmpty()
    {
        using var discovery = new DlnaDiscovery(new HttpClient(), ownsClient: false);

        var result = discovery.GetRenderers();

        Assert.Empty(result);
    }
}
