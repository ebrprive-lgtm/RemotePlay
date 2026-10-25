using System.Net;
using System.Net.Sockets;
using RemotePlay.Models;

namespace RemotePlay.Tests;

internal static class WebServerTestServer
{
    private const int MaxStartAttempts = 8;

    internal static (WebServer Server, int Port) Start(Func<int, WebServer> createServer) =>
        Start(createServer, FindFreePort);

    internal static (WebServer Server, int Port) Start(
        Func<int, WebServer> createServer,
        Func<int> findFreePort)
    {
        ArgumentNullException.ThrowIfNull(createServer);
        ArgumentNullException.ThrowIfNull(findFreePort);
        HttpListenerException? lastPortConflict = null;

        for (var attempt = 0; attempt < MaxStartAttempts; attempt++)
        {
            var port = findFreePort();
            var server = createServer(port);
            try
            {
                server.Start();
                return (server, port);
            }
            catch (HttpListenerException ex) when (IsPortConflict(ex))
            {
                server.Stop();
                lastPortConflict = ex;
            }
            catch
            {
                server.Stop();
                throw;
            }
        }

        throw new InvalidOperationException(
            $"Could not start the test web server after {MaxStartAttempts} port-conflict retries.",
            lastPortConflict);
    }

    private static bool IsPortConflict(HttpListenerException exception) =>
        exception.NativeErrorCode is 183 or 10048;

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
