# Live TV Reliability Improvement Plan

Scope: the browser catalog and filters, stream selection and fallback, server-side
catalog refresh and stream probing, player handoff, status correlation, and stop /
channel-switch behavior.

## Completed

- [x] Trace the full path from the Live TV browser through `/api/live-tv/play`,
  the server player callback, `/api/status`, and stop handling.
- [x] Decode Base64-encoded playback status paths before comparing them with
  stream URLs; ignore unrelated/stale status errors during fallback attempts.
- [x] Recycle the isolated player before replacing an active HTTP stream, and
  ignore health-check results from a replaced player.
- [x] Default to all channels when no saved filter state exists; reset filters
  when changing to a route without saved state.
- [x] Render cached or fallback channels before the catalog request completes;
  keep the displayed/cache data when a refresh or metadata reindex fails.
- [x] Cancel the browser's pending play request on stop or replacement, and
  prevent a superseded server request from starting playback after its probe
  finishes.
- [x] Clear playback-error deduplication using the same `Live TV` domain used
  by the status poller.
- [x] Clear channel-probe timers on both success and failure, remove the unused
  legacy catalog mapper, and safely encode apostrophes in inline URL arguments.
- [x] Filter closed and NSFW catalog entries, and retain non-default source
  ports so distinct stream endpoints are not merged.
- [x] Dispose optional guide JSON on every path and propagate request
  cancellation instead of treating it as a best-effort guide failure.
- [x] Return definitive upstream 4xx responses before invoking VLC; do not
  retry those permanent failures against the same source.
- [x] Add offline tests for catalog filtering, port-aware deduplication,
  playback status handling, cancellation/supersession, and permanent upstream
  failures.
- [x] Route the server-side Live TV preflight through the existing DNS-pinned
  HTTP client; reject non-public destinations and unsafe redirect targets, and
  keep redirects bounded and HTTPS downgrade disabled.
- [x] Cancel the request-scoped probe on stop, playback replacement, or server
  shutdown; keep the eight-second probe bound and preserve the generation gate
  so a stale request cannot hand off to VLC.
- [x] Add deterministic API tests for safe and blocked redirects, redirect
  limits, private DNS answers, permanent and transient provider statuses,
  probe timeout, stop, replacement, and shutdown.
- [x] Add a Deno browser-flow harness with mocked fetch, status polling, and
  local storage for cache-first entry, route filters, fallback selection,
  cancellation, request correlation, health probes, and retry bounds.
- [x] Correlate browser playback telemetry with server logs using a request ID;
  record elapsed time and phase while limiting new diagnostics to host,
  status, and sanitized error type rather than full stream URLs.
- [x] Treat opaque and transient browser health-probe results as unknown, and
  distinguish provider rejection, unavailable streams, startup timeouts, and
  cancellation from channel playback failures.

## Prioritized follow-ups

### P1 — Validate server-side stream probes

- [x] Resolve hostnames and reject loopback, private, link-local, multicast,
  unspecified, and other non-public IPv4/IPv6 destinations before probing.
- [x] Replace automatic redirect following with a bounded redirect loop that
  revalidates every redirect target and its resolved addresses. Account for
  DNS rebinding and redirects to local services.
- [x] Add offline tests for private IP literals, hostnames resolving to private
  addresses, IPv4-mapped IPv6, redirect chains, and redirect-limit handling.

### P1 — Propagate cancellation to the upstream probe

- [x] Cancel the server-side probe when playback is stopped, replaced, or the
  server shuts down, while retaining the generation gate and bounded probe
  timeout. `HttpListener` does not expose a reliable request-aborted token, so
  cancellation on client disconnect is not claimed.
- [x] Verify stop and channel replacement while probes are pending; stale
  requests are rejected at the handoff gate and never invoke the player
  callback.

### P2 — Add deterministic playback integration coverage

- [x] Inject the stream-probe HTTP client so API tests can simulate success,
  timeout, redirects, permanent 4xx, transient 408/429, and 5xx without
  contacting real providers.
- [x] Add a browser-level test harness with mocked fetch, status polling, and
  local storage. Cover cache-first entry, route filter restoration, URL
  correlation, fallback selection, and stop/switch during startup.
- [x] Assert that player callbacks occur only for the latest valid play request
  and never for a request canceled before probe completion.

### P2 — Improve failure diagnosis

- [x] Correlate browser telemetry and server logs with a request identifier and
  record elapsed time and phase (probe, player handoff, startup confirmation).
- [x] Keep diagnostics useful without logging complete stream URLs or
  credentials; include the provider host, HTTP status, and sanitized failure
  reason.
- [x] Distinguish provider rejection, unavailable stream, startup timeout, and
  user cancellation in the UI so an intentional stop is not reported as a
  playback failure.

### P3 — Review health probes and retry policy

- [x] Avoid treating an opaque response or transient failure from the existing
  cross-origin `HEAD` health probe as proof that a channel cannot play. Real
  provider usefulness still requires a manual measurement.
- [x] Add tests for failure cooldown/recovery and verify that retry budgets stay
  bounded across alternate sources while preserving useful fallback choices.
- [ ] Revisit the 15-second per-source and 45-second total startup budgets using
  measured provider and VLC startup timings. The current budgets are bounded
  and covered by offline tests, but have not been measured against real streams.

## Scope note

The DNS pinning and redirect validation protect the server-side preflight probe.
VLC independently fetches the selected stream and any playlist segments after
handoff; this probe does not pin or constrain those later VLC connections.
Controlling VLC's full network egress would require a separate proxy or
operating-system network policy.

## Validation record

- [x] Focused Release API and regression tests: 24 passed, 0 failed.
- [x] Deno Live TV browser-flow tests: 10 passed, 0 failed.
- [x] `git diff --check` completed without whitespace errors.
- [x] Browser harness formatting check passed with `deno fmt --check`.
- [ ] Manually verify channel startup, source fallback, and stop/switch behavior
  with a real provider stream and VLC.
