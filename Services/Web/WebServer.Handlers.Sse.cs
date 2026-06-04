using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace RemotePlay;

// Server-Sent Events (SSE) endpoint and push infrastructure.
// Clients connect to /api/events and receive a push whenever playback state changes,
// replacing the 1-second polling interval with immediate, server-initiated wake-ups.

internal sealed partial class WebServer
{
    // ── SSE client registry ────────────────────────────────────────────────────

    private sealed class SseClient(HttpListenerResponse response, CancellationTokenSource cts) : IDisposable
    {
        public readonly HttpListenerResponse Response = response;
        public readonly CancellationTokenSource Cts = cts;

        public void Dispose()
        {
            try { Cts.Cancel(); Cts.Dispose(); } catch { /* already cancelled */ }
            try { Response.OutputStream.Close(); } catch { /* already closed */ }
            try { Response.Close(); } catch { /* already closed */ }
        }
    }

    private readonly ConcurrentDictionary<string, SseClient> _sseClients = new();

    // ── /api/events handler ────────────────────────────────────────────────────

    /// <summary>
    /// Long-lived SSE handler: keeps the HTTP response open and parks until
    /// the server stops or the client navigates away.
    /// </summary>
    private async Task HandleSseEventsAsync(HttpListenerContext ctx, CancellationToken serverStopping)
    {
        var id = Guid.NewGuid().ToString("N");
        var clientCts = new CancellationTokenSource();
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(serverStopping, clientCts.Token);

        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.AddHeader("Cache-Control", "no-cache, no-store");
        ctx.Response.AddHeader("X-Accel-Buffering", "no"); // disable nginx/proxy buffering
        ctx.Response.SendChunked = true;

        var client = new SseClient(ctx.Response, clientCts);
        _sseClients[id] = client;

        try
        {
            // Tell the browser to wait 3 s before auto-reconnecting after a drop.
            // The initial "connected" event lets the JS onopen handler fire immediately.
            var initBytes = Encoding.UTF8.GetBytes("retry: 3000\ndata: {\"connected\":true}\n\n");
            await ctx.Response.OutputStream.WriteAsync(initBytes, combined.Token).ConfigureAwait(false);
            await ctx.Response.OutputStream.FlushAsync(combined.Token).ConfigureAwait(false);

            // Park this task until the server shuts down or the client disconnects.
            await Task.Delay(Timeout.Infinite, combined.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal exit: server stopped or client closed the tab.
        }
        catch
        {
            // Any I/O error means the client closed the connection before we did.
        }
        finally
        {
            _sseClients.TryRemove(id, out _);
            client.Dispose();
        }
    }

    // ── Push helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Broadcasts an SSE event to every connected client.
    /// Clients that have disconnected are removed automatically.
    /// </summary>
    internal void PushSseEvent(string eventType, string jsonData)
    {
        if (_sseClients.IsEmpty) return;

        var payload = Encoding.UTF8.GetBytes($"event: {eventType}\ndata: {jsonData}\n\n");

        foreach (var (id, client) in _sseClients)
        {
            try
            {
                client.Response.OutputStream.Write(payload, 0, payload.Length);
                client.Response.OutputStream.Flush();
            }
            catch
            {
                // Write failed — client is gone; cancel its parked task and remove it.
                if (_sseClients.TryRemove(id, out var dead))
                    dead.Cts.Cancel();
            }
        }
    }

    /// <summary>
    /// Schedules a <see cref="PushSseEvent"/> call, optionally after a delay so
    /// that asynchronous back-ends (e.g. VLC) have time to settle before clients poll.
    /// </summary>
    /// <param name="delayMs">
    /// Milliseconds to wait before pushing. Use 0 for synchronous state changes
    /// (stop, pause) and ~300 for play commands where VLC loads asynchronously.
    /// </param>
    internal void ScheduleSsePush(int delayMs = 0)
    {
        if (_sseClients.IsEmpty) return;

        if (delayMs <= 0)
        {
            PushSseEvent("status", "{}");
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            PushSseEvent("status", "{}");
        });
    }

    /// <summary>Cancels and disposes all connected SSE clients. Called from <see cref="Stop"/>.</summary>
    private void DisposeAllSseClients()
    {
        foreach (var (id, client) in _sseClients)
        {
            _sseClients.TryRemove(id, out _);
            client.Dispose();
        }
    }
}
