// Server-Sent Events (SSE) client for RemotePlay.
//
// Strategy:
//   1. Open a persistent EventSource to /api/events.
//   2. On each "status" event, call pollStatus() once — same path the polling interval already uses.
//   3. Keep the 1 s polling interval alive; playback position is time-based and
//      must refresh even when no discrete SSE status events are sent.
//   4. On disconnect / error, fall back to the old 1 s polling so the UI never goes dark.
//
// Public surface:
//   startSse()   – called once from app-library.js instead of startPolling()
//   stopSse()    – tears down the EventSource (called on page unload / tests)

let _sseSource = null;
let _sseConnected = false;
let _sseRetryTimer = null;
const SSE_RETRY_MS = 5000; // manual retry delay when the browser won't auto-reconnect

function _clearSseRetry() {
  if (_sseRetryTimer !== null) {
    clearTimeout(_sseRetryTimer);
    _sseRetryTimer = null;
  }
}

function _openSseConnection() {
  if (_sseSource) return; // already open

  const es = new EventSource('/api/events');
  _sseSource = es;

  es.addEventListener('open', () => {
    _sseConnected = true;
    _clearSseRetry();
    startPolling();
  });

  es.addEventListener('status', () => {
    // Server signals state has changed; fetch the canonical status immediately.
    pollStatus();
  });

  // The "connected" data message sent by the server on every new connection.
  es.addEventListener('message', () => {
    if (!_sseConnected) {
      _sseConnected = true;
      startPolling();
    }
  });

  es.addEventListener('error', () => {
    _sseConnected = false;
    es.close();
    _sseSource = null;

    // Activate polling fallback so the UI keeps updating while SSE is down.
    startPolling();

    // Try to re-open after a delay (browser also has its own built-in retry via
    // the `retry:` field we set on the server, but we do our own as a belt-and-
    // suspenders measure for cases where the browser gives up).
    _clearSseRetry();
    _sseRetryTimer = setTimeout(_openSseConnection, SSE_RETRY_MS);
  });
}

/**
 * Opens the SSE connection and keeps the 1 s polling interval alive for counters.
 * Falls back automatically to polling if the connection drops.
 */
function startSse() {
  _openSseConnection();
  startPolling();

  // Belt-and-suspenders: if EventSource is not supported (extremely unlikely in
  // any modern browser) fall back to the regular polling loop.
  if (typeof EventSource === 'undefined') {
    startPolling();
  }
}

/**
 * Closes the SSE connection and cleans up retry timers.
 * Optionally restores polling after teardown.
 */
function stopSse(restorePolling = false) {
  _clearSseRetry();
  if (_sseSource) {
    _sseSource.close();
    _sseSource = null;
  }
  _sseConnected = false;
  if (restorePolling) startPolling();
}

// Export connection state for diagnostics / tests.
function isSseConnected() {
  return _sseConnected;
}
