# Podcast Improvements Implementation Plan

## Purpose and scope

This plan records the podcast fixes already present in the worktree and the
remaining improvements identified during the podcast review. It is specific to
the current implementation in [WebAssets/app-podcasts.js](./WebAssets/app-podcasts.js),
[Services/Web/WebServer.Handlers.Podcasts.cs](./Services/Web/WebServer.Handlers.Podcasts.cs),
and the related player integration and API tests.

The tagged [RemotePlay.VideoPlayer/MainWindow.xaml](./RemotePlay.VideoPlayer/MainWindow.xaml)
is not part of the podcast path; podcast discovery and playback use the web
assets and WebServer handlers.

Checkboxes in **Current baseline** are verified as already implemented.
Unchecked items in **Remaining implementation** are follow-up work and should
be checked off only after their acceptance criteria pass.

## Current baseline

- [x] Podcasts opens with discovery content (including topic/country
  directories) rather than a blank or search-only view.
- [x] Direct podcast-feed routes are preserved during initialization, and
  refreshing country choices preserves the selected country.
- [x] Topic/country changes avoid duplicate searches; request-generation guards
  prevent older responses from replacing newer results.
- [x] Repeated load-more requests are guarded, and the browse results remain
  separate from the active playback episode list.
- [x] Podcast previous/next/count controls are wired into the shared player
  UI; playback stop and polling are integrated with the existing radio/player
  mode transitions.
- [x] Topic-only all-country search uses the intended search path, and the
  multi-country scan can stop once it has enough unique results.
- [x] Feed XML parsing prohibits DTDs and limits feed input; private/local
  literal episode and feed URLs are rejected by the current validation.
- [x] Podcast API tests cover country listing and rejection of private-host
  feed/play URLs in [RemotePlay.Tests/WebServerApiTests.cs](./RemotePlay.Tests/WebServerApiTests.cs).

### Baseline verification

Before Phase 1, the focused podcast API tests passed (3 passed). The broader
`WebServerApiTests` run had 27 passing tests and one rate-limit test failure
(expected HTTP 429, received HTTP 200); that same failure reproduced when the
test was run alone. Phase 1 verification then passed all 63 podcast-focused
tests. The broader API/unit selection passed 85 tests and reproduced only that
same rate-limit failure, including when run alone. Treat it as a known baseline
issue unless a planned change touches rate-limit behavior.

There is no JavaScript test runner or JS test file pattern currently wired into
the repository. Earlier frontend checks were run with Deno as targeted syntax
and behavior checks; use the established .NET test project for API coverage and
add browser-level coverage only through an existing or deliberately approved
test harness.

## Remaining implementation

### Phase 1 — Add a testable podcast HTTP boundary and harden feed fetching

**Why:** Podcast feeds are arbitrary external URLs. The current podcast
`HttpClient` is created with its default handler, which follows redirects
automatically; validating only the starting URL does not validate every
redirect destination or protect against DNS rebinding.

**Primary files:** [Services/Web/WebServer.Handlers.Podcasts.cs](./Services/Web/WebServer.Handlers.Podcasts.cs),
[RemotePlay.Tests/WebServerApiTests.cs](./RemotePlay.Tests/WebServerApiTests.cs).

- [x] Add the narrowest practical injection seam for the feed HTTP transport
  and DNS/connect validation so tests can use deterministic fake upstream
  responses. Keep iTunes lookup throttling and existing catalog behavior
  intact.
- [x] Restrict feed and episode URLs to absolute HTTP/HTTPS URLs; reject
  malformed URLs, user-info credentials, and non-public destinations.
- [x] Replace automatic redirect following for arbitrary feeds with a bounded
  redirect loop (recommended maximum: 5 hops). Resolve relative `Location`
  values and validate every destination before requesting it. Reject redirect
  loops and, by default, HTTPS-to-HTTP downgrades.
- [x] Validate resolved IPv4 and IPv6 addresses, not just host text. Reject
  loopback, private, link-local, unspecified, multicast, metadata-service, and
  other non-global addresses, including IPv4-mapped IPv6 forms. Fail closed
  when DNS resolution cannot be validated.
- [x] Prevent DNS rebinding for feed requests by ensuring each direct socket
  connection uses an address validated for that request, while preserving
  correct Host and TLS certificate/SNI validation.
- [x] Apply the public-destination policy to server-fetched feeds and
  server-side play requests. Validate episode audio hosts before returning
  enclosures to the browser; cap validation at 64 distinct hosts and 10
  seconds per feed, omitting unsafe episodes and returning an error if no safe
  playable episode remains.
- [x] Preserve the XML DTD prohibition, 15-second timeout, and existing
  character bound. Enforce a 20 MiB response-byte limit while streaming,
  before buffering or parsing the full body.
- [x] Return an explicit client error for rejected user-supplied destinations;
  log upstream/redirect failures without exposing internal addresses or
  credentials in responses.

**Acceptance checks**

- [x] No outbound request is made for a literal private/loopback URL or a
  hostname resolving to a non-public address.
- [x] A public feed redirecting to a private address is rejected before the
  redirected request is sent.
- [x] Relative redirects to valid public URLs work; loops, excessive hops,
  invalid `Location` values, and HTTPS downgrades are rejected.
- [x] Feed requests and `/api/podcasts/play` enforce the validated
  destination policy; unsafe enclosure URLs are not returned to the browser.
- [x] Existing DTD, size-limit, country-list, and private-host tests still
  pass; new tests use fake upstreams and do not depend on live podcast
  providers.

**Phase 1 implementation notes and limits**

- The pinned feed client bypasses system proxies for arbitrary feed URLs;
  otherwise the proxy could resolve the destination outside the validated
  connection path. Environments that require an outbound proxy will need
  explicit proxy support compatible with address pinning.
- `/api/podcasts/play` checks DNS immediately before invoking the player, but
  `RadioPlayer`/Media Foundation creates its own connection and can resolve the
  host again. That connection is destination-checked but not DNS-pinned;
  eliminating this remaining rebinding window requires player transport
  support or a streaming relay.
- Enclosure URLs are checked before being returned, but the browser performs
  the audio request and may follow redirects that this server cannot pin.
  Audio proxying remains out of scope unless that protection is explicitly
  required.

### Phase 2 — Cache RSS responses with validators and bounded storage

**Why:** Search and top-pool data already have daily caches, but no
podcast-feed response cache or conditional `ETag`/`Last-Modified` revalidation
was found. Feed paging can otherwise repeatedly download and parse the same
feed.

**Primary files:** [Services/Web/WebServer.Handlers.Podcasts.cs](./Services/Web/WebServer.Handlers.Podcasts.cs),
[RemotePlay.Tests/WebServerApiTests.cs](./RemotePlay.Tests/WebServerApiTests.cs).

- [x] Add a feed cache keyed by a normalized, validated feed URL. Store
  validators and the bounded parsed feed data needed to serve offset pages.
- [x] Send `If-None-Match` and/or `If-Modified-Since` when validators are
  available; on `304 Not Modified`, refresh cache freshness and serve the
  cached feed without reparsing.
- [x] Respect upstream `Cache-Control: no-store` and revalidate `no-cache`
  responses. Select and document a conservative freshness interval and
  maximum entry/aggregate memory limits; do not cache an entry that exceeds
  those limits.
- [x] Coalesce concurrent requests for the same feed so simultaneous page
  requests do not trigger duplicate downloads.
- [x] Do not silently return stale content indefinitely after an upstream
  error. Surface fetch/parse failures through the existing API error behavior.
- [x] Ensure cache keys and cached data cannot cross-contaminate different
  feed URLs, query parameters, or paging offsets.

**Acceptance checks**

- [x] A repeated request within the freshness interval is served from cache.
- [x] An expired entry sends validators; a `304` serves the prior parsed
  content, while a changed `200` replaces it.
- [x] `no-store`, oversized responses, eviction, concurrent requests, and
  upstream failures have deterministic tests.
- [x] Feed pagination returns the same `episodes`, `hasMore`, and `total`
  contract before and after caching.

**Phase 2 implementation notes and limits**

- The cache is in-memory and scoped to each `WebServer` instance. Its key
  canonicalizes scheme/host/default port, removes URL fragments, and preserves
  the path and query; pagination parameters are deliberately not part of the
  feed key.
- Freshness defaults to five minutes and is capped at five minutes even when
  the origin requests a longer lifetime. `no-cache` is immediately stale.
  `max-age`, `Age`, `Date`, and `Expires` are considered; when the origin gives
  no freshness metadata, the conservative five-minute default is used.
- Cache storage is bounded to an estimated 4 MiB per entry, 32 MiB total, and
  128 entries, with least-recently-used eviction. Oversized entries are served
  to the current request but not retained. `no-store` and `Vary: *` responses
  evict any prior representation and are not retained.
- Validators are associated with the final URI after redirects and are sent
  only on the redirect hop matching that URI. Concurrent callers share one
  refresh; cancelling one caller cancels only its wait, not the shared fetch.
  A refresh has a 15-second timeout. Failed refreshes propagate through the
  existing API errors rather than serving expired content.
- Feed host and episode audio host destinations are revalidated for every
  API request, including cache hits, preserving the Phase 1 public-destination
  policy.

**Phase 2 verification**

- [x] Podcast-focused API and cache tests passed: 78 passed, 0 failed.
- [x] The complete `WebServerApiTests` selection ran: 54 passed; the existing
  rate-limit test still fails with expected HTTP 429 / actual HTTP 200, matching
  the previously documented baseline failure.

### Phase 3 — Balance global top-podcast discovery across countries

**Why:** `FetchGlobalTopPodcastsAsync` currently queries countries in a fixed
order beginning with the US and returns as soon as it reaches the requested
limit. A well-populated early country can therefore dominate the global
directory.

**Primary file:** [Services/Web/WebServer.Handlers.Podcasts.cs](./Services/Web/WebServer.Handlers.Podcasts.cs).

- [ ] Change the all-country top-pool merge to sample countries in rounds
  (for example, one new unique show per country per round) instead of filling
  the result from the first country that reaches the limit.
- [ ] Keep feed-URL deduplication, stable ordering between requests, the
  existing result limit, and the daily top-pool cache.
- [ ] Keep explicit country searches country-specific; do not change their
  filtering or ranking semantics as part of global balancing.
- [ ] Bound the countries sampled and the number of results requested from
  each source; keep calls sequential through the existing iTunes throttle and
  retain early termination when sources are exhausted or the pool is full.

**Acceptance checks**

- [ ] When multiple country sources have unique results, the global pool
  includes results from multiple sources before any one source fills the pool.
- [ ] Duplicate feed URLs appear only once, and repeated requests against the
  same cached pool return results in the same order.
- [ ] Explicitly selected country results and current pagination contracts
  remain unchanged.

### Phase 4 — Cancel superseded requests and continue playback across feed pages

**Why:** Generation IDs protect the UI from stale responses, but do not stop
the obsolete network work. Playback navigation currently stops at the end of
the episodes already loaded, even when the feed has another page.

**Primary files:** [WebAssets/app-podcasts.js](./WebAssets/app-podcasts.js),
[WebAssets/app-library.js](./WebAssets/app-library.js),
[WebAssets/app-local.js](./WebAssets/app-local.js),
[WebAssets/app-radio.js](./WebAssets/app-radio.js),
[WebAssets/index.html](./WebAssets/index.html).

- [ ] Add `AbortController` handling to podcast directory, search, feed, and
  load-more requests. Abort the prior request when a newer selection or query
  supersedes it, or when the user leaves the relevant view.
- [ ] Keep request-generation checks as a second guard against late responses
  and browsers/environments where abort is unavailable. Do not present an
  intentional abort as a user-visible failure.
- [ ] Track the active playback feed and its loaded offset/`hasMore`
  independently of the currently displayed show and browse results.
- [ ] When Next (including automatic end-of-episode advance) reaches the end
  of the loaded page and `hasMore` is true, fetch the next page, append it to
  the playback list, and play the next episode exactly once.
- [ ] Prevent double-advance and duplicate-page races; keep Previous/Next
  disabled or visibly busy while a required page is loading.
- [ ] On a failed next-page request, show an explicit error and a retry path;
  do not silently skip episodes or discard the remaining queue.
- [ ] Preserve the current playback queue when browsing another show or
  returning to the directory, and preserve the existing radio/player mode,
  stop, and polling behavior.

**Acceptance checks**

- [ ] A slow old search/feed response cannot replace the current selection;
  superseded requests are aborted where supported.
- [ ] Advancing at a page boundary loads and plays the next episode without
  a duplicate or skipped episode, including automatic advance at end of
  playback.
- [ ] Rapid/repeated Next clicks do not trigger multiple page loads or
  multiple playback starts.
- [ ] A failed page load leaves the existing playback state recoverable and
  reports a clear error.
- [ ] Browsing, route changes, local/server playback switching, and stop
  behavior do not corrupt or unexpectedly clear the active queue.

### Phase 5 — Regression verification and completion

- [ ] Add focused deterministic API tests for redirect validation, DNS/address
  rejection, feed caching, cache limits, and balanced global discovery.
- [ ] Add focused frontend tests for cancellation and queue page-boundary
  behavior if a suitable lightweight harness is available; otherwise run and
  record targeted Deno/browser checks without introducing an unrelated
  dependency stack.
- [ ] Run the focused podcast API tests:

  ```powershell
  dotnet test .\RemotePlay.Tests\RemotePlay.Tests.csproj --filter "FullyQualifiedName~Podcast"
  ```

- [ ] Run the complete API test class and compare failures with the documented
  rate-limit baseline. Investigate any new or changed failure.
- [ ] Run the applicable JavaScript syntax/behavior checks and verify the
  podcast entry, topic/country selection, feed paging, and playback controls
  in the web UI.
- [ ] Run `git diff --check`; review the final diff for unrelated changes.
- [ ] Update this plan's checkboxes only after the corresponding behavior and
  tests have been verified.

## Dependencies and delivery order

1. Phase 1's injectable test boundary is the foundation for deterministic
   redirect, DNS, and cache tests.
2. Complete redirect/DNS validation before caching arbitrary feed responses;
   a cache must not bypass URL validation.
3. Phase 3 is independent of feed caching and can follow the test seam.
4. Phase 4 is frontend work; keep the existing generation guards and playback
   queue semantics while adding cancellation and page continuation.
5. Complete Phase 5 after all implementation phases; compare full-suite
   results against the baseline recorded above.

## Risks and constraints

- Podcast feeds vary in redirect behavior, XML quality, and validator support.
  Keep limits bounded and ensure cache misses/invalid feeds produce explicit
  errors.
- DNS validation must be tied to the actual connection to avoid a
  check-then-connect rebinding gap.
- Building a balanced global pool can require more cold-cache iTunes requests
  than the current early-return path. Bound the source/sample budget, preserve
  the existing throttle and daily cache, and verify the added cold-cache
  latency.
- Playback paging must remain independent from browse state. Do not remove or
  replace the underlying queue behavior while updating its visible controls.
- The existing rate-limit API test failure is documented above; distinguish
  it from regressions introduced by these phases.
