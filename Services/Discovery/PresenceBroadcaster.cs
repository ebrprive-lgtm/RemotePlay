using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Timer = System.Threading.Timer;

namespace RemotePlay.Services.Discovery;

/// <summary>
/// Represents a discovered RemotePlay instance on the local network.
/// </summary>
internal sealed record DiscoveredPeer
{
    public string Name { get; init; } = string.Empty;
    public string Scheme { get; init; } = "http";
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string Url => $"{Scheme}://{Host}:{Port}";
    public DateTimeOffset LastSeenUtc { get; init; }
    public bool IsSelf { get; init; }
}

/// <summary>
/// Broadcasts this instance's presence via UDP and listens for other RemotePlay instances.
/// </summary>
internal sealed class PresenceBroadcaster : IDisposable
{
    /// <summary>Fixed UDP discovery port shared by all instances on the LAN.</summary>
    public const int DiscoveryPort = 9091;

    private static readonly TimeSpan PeerExpiry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(5);

    private readonly AppConfig _config;
    private readonly ConcurrentDictionary<string, DiscoveredPeer> _peers = new();
    private UdpClient? _receiver;
    private Timer? _broadcastTimer;
    private CancellationTokenSource? _listenCts;
    private bool _disposed;

    public PresenceBroadcaster(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <summary>Starts broadcasting and listening.</summary>
    public void Start()
    {
        try
        {
            _receiver = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort))
            {
                EnableBroadcast = true
            };

            _listenCts = new CancellationTokenSource();
            _ = ListenLoopAsync(_listenCts.Token);

            _broadcastTimer = new Timer(_ => SendBeacon(), null,
                TimeSpan.Zero, BroadcastInterval);

            Logger.Info($"PresenceBroadcaster started on UDP port {DiscoveryPort}");
        }
        catch (Exception ex)
        {
            Logger.Error("PresenceBroadcaster failed to start", ex);
        }
    }

    /// <summary>Stops broadcasting and listening.</summary>
    public void Stop()
    {
        _broadcastTimer?.Dispose();
        _broadcastTimer = null;
        _listenCts?.Cancel();
        _receiver?.Close();
    }

    /// <summary>Returns all currently known peers (including self), with stale entries removed.</summary>
    public DiscoveredPeer[] GetPeers()
    {
        var now = DateTimeOffset.UtcNow;
        var staleKeys = _peers
            .Where(kv => !kv.Value.IsSelf && now - kv.Value.LastSeenUtc > PeerExpiry)
            .Select(kv => kv.Key)
            .ToArray();

        foreach (var key in staleKeys)
            _peers.TryRemove(key, out _);

        return _peers.Values.OrderBy(p => p.IsSelf ? 0 : 1).ThenBy(p => p.Name).ToArray();
    }

    private void SendBeacon()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up)
                .ToArray();

            foreach (var iface in interfaces)
            {
                foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    var ip = addr.Address;

                    // Skip loopback — 127.x.x.x is never reachable by other devices.
                    if (IPAddress.IsLoopback(ip)) continue;

                    var subnet = addr.IPv4Mask;
                    var broadcast = GetBroadcastAddress(ip, subnet);

                    var beacon = new BeaconPayload
                    {
                        InstanceId = _config.InstanceId,
                        Name = _config.InstanceName,
                        Scheme = _config.Scheme,
                        Ip = ip.ToString(),
                        Port = _config.Port
                    };

                    var json = JsonSerializer.Serialize(beacon);
                    var bytes = Encoding.UTF8.GetBytes(json);

                    // Register self so we appear in our own peer list.
                    var selfKey = _config.InstanceId;
                    _peers[selfKey] = new DiscoveredPeer
                    {
                        Name = _config.InstanceName,
                        Scheme = _config.Scheme,
                        Host = ip.ToString(),
                        Port = _config.Port,
                        LastSeenUtc = DateTimeOffset.UtcNow,
                        IsSelf = true
                    };

                    // Send to subnet broadcast so the source IP matches the interface IP.
                    try
                    {
                        using var sender = new UdpClient(new IPEndPoint(ip, 0)) { EnableBroadcast = true };
                        sender.Send(bytes, bytes.Length, new IPEndPoint(broadcast, DiscoveryPort));
                    }
                    catch { /* ignore individual send failures */ }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("PresenceBroadcaster beacon send failed", ex);
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _receiver!.ReceiveAsync(ct).ConfigureAwait(false);
                ProcessBeacon(result.Buffer, result.RemoteEndPoint.Address);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Logger.Error("PresenceBroadcaster receive error", ex);
            }
        }
    }

    private void ProcessBeacon(byte[] data, IPAddress sourceAddress)
    {
        try
        {
            var json = Encoding.UTF8.GetString(data);
            var beacon = JsonSerializer.Deserialize<BeaconPayload>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (beacon is null || beacon.Port <= 0) return;

            // Prefer the IP the beacon advertises; fall back to UDP source address.
            var host = string.IsNullOrWhiteSpace(beacon.Ip)
                ? sourceAddress.ToString()
                : beacon.Ip;

            // Use instanceId for deduplication when available (avoids duplicate entries
            // for hosts with multiple network interfaces); fall back to host:port.
            var key = string.IsNullOrWhiteSpace(beacon.InstanceId)
                ? $"{host}:{beacon.Port}"
                : beacon.InstanceId;

            var isSelf = IsOwnAddress(sourceAddress) && beacon.Port == _config.Port;

            _peers[key] = new DiscoveredPeer
            {
                Name = string.IsNullOrWhiteSpace(beacon.Name) ? host : beacon.Name,
                Scheme = beacon.Scheme ?? "http",
                Host = host,
                Port = beacon.Port,
                LastSeenUtc = DateTimeOffset.UtcNow,
                IsSelf = isSelf
            };
        }
        catch
        {
            // Ignore malformed beacons.
        }
    }

    private static bool IsOwnAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var addr in iface.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.Equals(address)) return true;
            }
        }
        return false;
    }

    private static IPAddress GetBroadcastAddress(IPAddress ip, IPAddress mask)
    {
        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var broadcast = new byte[ipBytes.Length];
        for (var i = 0; i < broadcast.Length; i++)
            broadcast[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
        return new IPAddress(broadcast);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _receiver?.Dispose();
        _listenCts?.Dispose();
        _broadcastTimer?.Dispose();
    }

    private sealed class BeaconPayload
    {
        [JsonPropertyName("instanceId")]
        public string InstanceId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("scheme")]
        public string Scheme { get; set; } = "http";

        [JsonPropertyName("ip")]
        public string Ip { get; set; } = string.Empty;

        [JsonPropertyName("port")]
        public int Port { get; set; }
    }
}
