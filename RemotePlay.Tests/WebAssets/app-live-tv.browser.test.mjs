const liveTvSource = Deno.readTextFileSync(
  new URL("../../WebAssets/app-live-tv.js", import.meta.url),
);

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function assertEquals(actual, expected, message = "") {
  if (!Object.is(actual, expected)) {
    throw new Error(
      message || `Expected ${String(expected)}, got ${String(actual)}.`,
    );
  }
}

function createLiveTvHarness({
  fetchImpl = async () => {
    throw new TypeError("offline");
  },
  pollStatus = async () => ({}),
  storage = new Map(),
} = {}) {
  const events = [];
  const logRequests = [];
  const fetchProxy = (url, options) => {
    if (url === "/api/log") {
      logRequests.push({ url, options });
      return Promise.resolve({ ok: true, status: 200 });
    }
    return fetchImpl(url, options);
  };
  const document = {
    getElementById: () => null,
    querySelectorAll: () => [],
    dispatchEvent: (event) => {
      events.push(event.detail);
      return true;
    },
    createElement: () => {
      throw new Error("The test did not expect a playback modal.");
    },
    body: { appendChild: () => {} },
  };
  const localStorage = {
    getItem: (key) => storage.get(key) ?? null,
    setItem: (key, value) => storage.set(key, String(value)),
    removeItem: (key) => storage.delete(key),
  };
  const app = new Function(
    "document",
    "localStorage",
    "fetch",
    "pollStatus",
    "CustomEvent",
    "_viewMode",
    "console",
    "events",
    "logRequests",
    `${liveTvSource}
return {
  applyRoute(token) {
    liveTvApplyRouteToken(token);
    return {
      query: _liveTvFilterQuery,
      country: _liveTvFilterCountry,
      category: _liveTvFilterCategory,
      language: _liveTvFilterLanguage,
      favoritesOnly: _liveTvShowFavoritesOnly,
      hideFailed: _liveTvHideFailed,
    };
  },
  setChannels(channels) { _liveTvChannels = channels; },
  getCandidates(url, channel) { return _liveTvPlaybackCandidates(url, channel); },
  loadReliability() { _loadLiveTvReliability(); },
  setChannelApply(callback) { _liveTvApplyChannels = callback; },
  loadChannels() { return liveTvLoadChannels(); },
  async probe(channel) { await _liveTvProbeChannelHealth(channel); },
  markFailed(url, reason) { _markLiveTvChannelFailed(url, reason); },
  markRecovered(url) { _markLiveTvChannelRecovered(url); },
  reliability(url) { return _liveTvReliability(url); },
  isFailed(url) { return _isLiveTvChannelFailed(url); },
  failureCategory(message, statusCode) { return _liveTvFailureCategory(message, statusCode); },
  friendlyFailureReason(message, statusCode) { return _liveTvFriendlyFailureReason(message, statusCode); },
  async play(url, name) { await liveTvPlayChannel(url, name); },
  emitTelemetry(eventName, data) { _liveTvEmitTelemetry(eventName, data); },
  cancel() { return liveTvCancelPendingPlaybackStart(); },
  events,
  logs: logRequests,
  state() {
    return {
      currentUrl: _liveTvCurrentUrl,
      currentRequestId: _liveTvCurrentRequestId,
      failedCount: _liveTvFailedUrls.size,
      attemptingUrl: _liveTvAttemptingUrl,
    };
  },
  budgets() {
    return {
      perSourceMs: _liveTvPlaybackStartTimeoutMs,
      totalMs: _liveTvPlaybackStartBudgetMs,
      maxCandidates: 6,
    };
  },
};`,
  );

  return app(
    document,
    localStorage,
    fetchProxy,
    pollStatus,
    CustomEvent,
    { "live-tv": "grid" },
    { warn: () => {}, error: () => {} },
    events,
    logRequests,
  );
}

Deno.test("cache-first loading applies cached channels before the catalog request", async () => {
  const channel = {
    name: "Cached News",
    url: "https://cached.test/live.m3u8",
    sourceHost: "cached.test",
  };
  const storage = new Map([[
    "rp_live_tv_catalog_v1",
    JSON.stringify({ savedAt: Date.now(), channels: [channel] }),
  ]]);
  const harness = createLiveTvHarness({ storage });
  const applied = [];
  harness.setChannelApply((channels) => applied.push(channels));

  await harness.loadChannels();

  assertEquals(
    applied[0][0].url,
    channel.url,
    "Cached channels must render before refresh.",
  );
});

Deno.test("fallback channels render immediately when no catalog cache exists", async () => {
  const harness = createLiveTvHarness();
  const applied = [];
  harness.setChannelApply((channels) => applied.push(channels));

  await harness.loadChannels();

  assertEquals(applied[0].length, 5);
  assertEquals(applied[0][0].name, "Al Jazeera English");
});

Deno.test("route filter state restores for the selected route", () => {
  const storage = new Map([[
    "rp_live_tv_filters_v1:news",
    JSON.stringify({
      query: "world",
      country: "France",
      category: "News",
      language: "English",
      favoritesOnly: true,
      hideFailedExplicit: true,
      hideFailed: true,
    }),
  ]]);
  const harness = createLiveTvHarness({ storage });

  const state = harness.applyRoute("news");

  assertEquals(state.query, "world");
  assertEquals(state.country, "France");
  assertEquals(state.category, "News");
  assertEquals(state.language, "English");
  assertEquals(state.favoritesOnly, true);
  assertEquals(state.hideFailed, true);
});

Deno.test("fallback candidates are retained, cooldown candidates are deprioritized, and count is bounded", () => {
  const primary = "https://stream.test/primary.m3u8";
  const backup = "https://stream.test/backup.m3u8";
  const third = "https://stream.test/third.m3u8";
  const storage = new Map([[
    "rp_live_tv_reliability_v1",
    JSON.stringify({
      "https://stream.test/primary.m3u8": {
        failCount: 1,
        successCount: 0,
        lastFailure: Date.now(),
        cooldownUntil: Date.now() + 60_000,
      },
    }),
  ]]);
  const harness = createLiveTvHarness({ storage });
  harness.loadReliability();
  const channels = [{
    name: "News",
    countryCode: "US",
    url: primary,
    urls: [backup, third],
  }];
  harness.setChannels(channels);

  const candidates = harness.getCandidates(primary, channels[0]);
  const budgets = harness.budgets();

  assertEquals(
    candidates[0],
    backup,
    "A ready fallback should be preferred over a cooling source.",
  );
  assert(
    candidates.includes(primary),
    "A cooling source must remain available as a fallback.",
  );
  assert(
    candidates.length <= budgets.maxCandidates,
    "Candidate retry list must remain bounded.",
  );
  const expandedChannels = [
    ...channels,
    ...Array.from({ length: 8 }, (_, index) => ({
      name: "News",
      countryCode: "US",
      url: `https://alternate${index}.test/live.m3u8`,
    })),
  ];
  harness.setChannels(expandedChannels);
  assertEquals(
    harness.getCandidates(primary, expandedChannels[0]).length,
    budgets.maxCandidates,
    "Large fallback catalogs must be capped.",
  );
  assertEquals(budgets.perSourceMs, 15_000);
  assertEquals(budgets.totalMs, 45_000);
});

Deno.test("health probe network failures and opaque responses remain unknown", async () => {
  const failedNetwork = createLiveTvHarness({
    fetchImpl: async () => {
      throw new TypeError("network error");
    },
  });
  const opaqueResponse = createLiveTvHarness({
    fetchImpl: async () => ({ type: "opaque", status: 0, ok: false }),
  });
  const channel = { url: "https://health.test/live.m3u8" };

  await failedNetwork.probe(channel);
  await opaqueResponse.probe(channel);

  assertEquals(failedNetwork.reliability(channel.url).failCount, 0);
  assertEquals(opaqueResponse.reliability(channel.url).failCount, 0);
  assert(
    opaqueResponse.events.some((event) =>
      event.event === "health_probe_unknown" &&
      event.reason === "opaque_response"
    ),
    "Opaque health probe results should be identified as unknown.",
  );
});

Deno.test("playback failures enter cooldown and successful playback clears it", () => {
  const harness = createLiveTvHarness();
  const url = "https://health.test/live.m3u8";

  harness.markFailed(url, "startup failed");
  const failed = harness.reliability(url);
  harness.markRecovered(url);
  const recovered = harness.reliability(url);

  assert(
    failed.cooldownUntil > Date.now(),
    "Failed source should enter cooldown.",
  );
  assertEquals(harness.isFailed(url), false);
  assertEquals(recovered.cooldownUntil, 0);
  assertEquals(recovered.successCount, failed.successCount + 1);
});

Deno.test("failure UI distinguishes provider rejection, unavailable streams, and startup timeouts", () => {
  const harness = createLiveTvHarness();

  assertEquals(
    harness.failureCategory("Forbidden (403) from stream provider", 403),
    "provider_rejected",
  );
  assertEquals(
    harness.friendlyFailureReason("Forbidden (403) from stream provider", 403),
    "This stream is blocked by the provider (authorization or geo restriction).",
  );
  assertEquals(
    harness.failureCategory(
      "Stream provider returned HTTP 404 Not Found.",
      404,
    ),
    "stream_unavailable",
  );
  assertEquals(
    harness.failureCategory(
      "No playback confirmation after 15 seconds.",
      undefined,
    ),
    "startup_timeout",
  );
  assertEquals(
    harness.friendlyFailureReason("Stream destination is not allowed", 400),
    "This stream URL or redirect points to a private or unsupported destination.",
  );
});

Deno.test("playback fallback uses request correlation and telemetry omits full stream URLs", async () => {
  const primary = "https://primary.test/live.m3u8";
  const backup = "https://backup.test/live.m3u8";
  const requests = [];
  let activeUrl = "";
  const harness = createLiveTvHarness({
    fetchImpl: async (input, init) => {
      requests.push({ input, init });
      const endpoint = new URL(input, "http://remoteplay.test");
      activeUrl = endpoint.searchParams.get("url");
      if (activeUrl === primary) {
        return {
          ok: false,
          status: 404,
          text: async () => "Stream provider returned HTTP 404 Not Found.",
        };
      }
      return { ok: true, status: 200, text: async () => "OK" };
    },
    pollStatus: async () => ({ isPlaying: true, filePath: activeUrl }),
  });
  harness.setChannels([{
    name: "News",
    countryCode: "US",
    url: primary,
    urls: [backup],
  }]);

  await harness.play(primary, "News");

  const requestId = requests[0].init.headers["X-RemotePlay-Request-Id"];
  assert(requestId, "A playback request ID should be sent to the server.");
  assertEquals(requests[1].init.headers["X-RemotePlay-Request-Id"], requestId);
  assertEquals(harness.state().currentUrl, backup);
  const playEvents = harness.events.filter((event) =>
    event.event.startsWith("play_")
  );
  assert(playEvents.every((event) => event.requestId === requestId));
  assert(
    playEvents.some((event) =>
      event.event === "play_success" &&
      event.phase === "startup_confirmed" &&
      event.fallbackUsed
    ),
  );
  assert(
    playEvents.every((event) =>
      !("requestedUrl" in event) && !("candidateUrl" in event) &&
      !("playedUrl" in event)
    ),
    "Playback telemetry must not contain full stream URLs.",
  );
});

Deno.test("stopping startup aborts the request without recording a playback failure", async () => {
  let requestStarted;
  const started = new Promise((resolve) => {
    requestStarted = resolve;
  });
  const harness = createLiveTvHarness({
    fetchImpl: (_input, init) =>
      new Promise((_resolve, reject) => {
        requestStarted(init);
        init.signal.addEventListener(
          "abort",
          () => reject(new DOMException("Aborted", "AbortError")),
          { once: true },
        );
      }),
  });
  const playback = harness.play("https://stream.test/live.m3u8", "News");
  const init = await started;
  const requestId = init.headers["X-RemotePlay-Request-Id"];

  assert(requestId, "The startup request should carry its correlation ID.");
  assertEquals(harness.cancel(), true);
  await playback;

  assertEquals(harness.state().failedCount, 0);
  assert(harness.events.some((event) => event.event === "play_cancelled"));
  assert(!harness.events.some((event) => event.event === "play_failed"));
});

Deno.test("server cancellation is reported as cancellation, not a channel failure", async () => {
  const harness = createLiveTvHarness({
    fetchImpl: async () => ({
      ok: false,
      status: 409,
      text: async () => "Live TV playback request was superseded.",
    }),
  });

  await harness.play("https://stream.test/live.m3u8", "News");

  assertEquals(harness.state().failedCount, 0);
  assert(
    harness.events.some((event) =>
      event.event === "play_cancelled" && event.reason === "server"
    ),
  );
  assert(!harness.events.some((event) => event.event === "play_failed"));
});

Deno.test("startup timing logs allowlisted fields without channel or provider details", () => {
  const harness = createLiveTvHarness();

  harness.emitTelemetry("play_attempt_failed", {
    requestId: "live-tv-safe_123",
    phase: "player_startup",
    attempt: 2,
    elapsedMs: 834,
    statusCode: 502,
    errorType: "TypeError",
    channelName: "Private Channel",
    candidateHost: "private-provider.example",
    error: "private stream URL and credentials",
  });

  assertEquals(harness.logs.length, 1);
  assertEquals(harness.logs[0].url, "/api/log");
  const log = harness.logs[0].options.body;
  assert(log.includes("[LIVE-TV-TIMING]"));
  assert(log.includes("event=play_attempt_failed"));
  assert(log.includes("requestId=live-tv-safe_123"));
  assert(log.includes("attempt=2"));
  assert(log.includes("elapsedMs=834"));
  assert(log.includes("statusCode=502"));
  assert(log.includes("errorType=TypeError"));
  assert(!log.includes("Private Channel"));
  assert(!log.includes("private-provider.example"));
  assert(!log.includes("credentials"));
});
