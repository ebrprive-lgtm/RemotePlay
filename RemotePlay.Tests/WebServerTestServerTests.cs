using System.Net;
using RemotePlay.Models;
using Xunit;

namespace RemotePlay.Tests;

public sealed class WebServerTestServerTests
{
    [Fact]
    public void Start_RetriesWhenHttpListenerPrefixIsAlreadyRegistered()
    {
        var blockedPort = FindFreePort();
        using var blocker = new HttpListener();
        blocker.Prefixes.Add($"http://*:{blockedPort}/");
        blocker.Start();

        var candidatePorts = new Queue<int>([blockedPort, FindFreePort()]);
        var (server, port) = WebServerTestServer.Start(
            candidatePort => new WebServer(
                new AppConfig { Port = candidatePort },
                WebServerApiTests.BuildStubCallbacks()),
            () => candidatePorts.Dequeue());

        try
        {
            Assert.NotEqual(blockedPort, port);
            Assert.Empty(candidatePorts);
        }
        finally
        {
            server.Stop();
        }
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
