# RemotePlay — Feature Suggestions

A backlog of improvement ideas. Each suggestion is self-contained and can be implemented independently.

---

## 1. 🟢 Replace polling with Server-Sent Events (SSE)

**File targets:** `Services/Web/WebServer.cs`, `WebAssets/app-core.js`, `WebAssets/app-library.js`

**Status:** `[x] Completed`

### What
Replaced the client-side `pollStatus()` 1-second timer loop with a persistent `/api/events` endpoint that pushes `text/event-stream` events whenever playback state changes. The existing `/api/status` route is kept as the canonical status source; SSE events act as wake-up signals that trigger an immediate `pollStatus()` fetch.

---

### Implementation report

#### Server — new file `Services/Web/WebServer.Handlers.Sse.cs`
- `SseClient` record — wraps the open `HttpListenerResponse` and a `CancellationTokenSource` for clean teardown.
- `_sseClients` — `ConcurrentDictionary<string, SseClient>` keyed on a per-connection GUID; safe for any number of concurrent browsers.
- `HandleSseEventsAsync(HttpListenerContext, CancellationToken)` — sets `Content-Type: text/event-stream`, emits `retry: 3000` + initial `{"connected":true}` frame, then parks the response with `Task.Delay(Infinite, combinedToken)` until the server stops or the client disconnects.
- `PushSseEvent(string eventType, string jsonData)` — synchronous broadcast to all connected clients; stale/disconnected clients are detected on the write failure and removed automatically.
- `ScheduleSsePush(int delayMs = 0)` — zero-delay variant for synchronous actions (stop, pause, queue clear); 300 ms delayed variant for asynchronous VLC launches (play, music play, radio play).
- `DisposeAllSseClients()` — cancels and disposes every open SSE stream; called first in `Stop()` so clients get EOF before the listener socket closes.

#### Server — modified files
| File | Change |
|------|--------|
| `Services/Web/WebServer.Handlers.Request.cs` | Added `/api/events` to `_pollingPaths` (suppresses rate-limit logging); added `case "/api/events"` to the route switch; added `ScheduleSsePush()` after `/api/stop`, `/api/pause`, `/api/queue/clear`, `/api/music/pause`, `/api/music/stop`, `/api/radio/stop`. |
| `Services/Web/WebServer.Handlers.VideoPlayback.cs` | Added `ScheduleSsePush(delayMs: 300)` after `HandlePlay`; added `ScheduleSsePush()` after `HandleQueueAdd`, `HandleQueueRemove`, `HandleQueueMove`. |
| `Services/Web/WebServer.Handlers.MusicPlayback.cs` | Added `ScheduleSsePush(delayMs: 300)` in `HandleMusicPlay` (play-only path). |
| `Services/Web/WebServer.Handlers.Radio.cs` | Added `ScheduleSsePush(delayMs: 300)` in `HandleRadioPlay`. |
| `Services/Web/WebServer.cs` | Prepended `"app-sse.js"` to `_appJsModules`; added `DisposeAllSseClients()` as the first `try` block in `Stop()`. |

#### Client — new file `WebAssets/app-sse.js`
- `startSse()` — opens an `EventSource('/api/events')`; on `open`, suppresses the 1-second polling interval via `stopPolling()`; on `status` message, calls `pollStatus()` immediately; on `error`, re-enables polling as fallback and schedules a manual reconnect after 5 s; falls back to `startPolling()` in the (extremely unlikely) case where `EventSource` is unavailable.
- `stopSse(restorePolling?)` — tears down the `EventSource` cleanly; used in tests and future teardown scenarios.
- `isSseConnected()` — returns current connection state for diagnostics.

#### Client — modified files
| File | Change |
|------|--------|
| `WebAssets/app-library.js` | Replaced bare `startPolling()` page-init call (line ~4113) with `startSse()`; replaced `startPolling()` inside play-card click helper and `playQueueStart()` with `startSse()`. |

#### Tests — new file `RemotePlay.Tests/SseTests.cs`
13 integration tests against a real `WebServer` on a random loopback port:

| Test | What it covers |
|------|----------------|
| `SseEndpoint_Returns200` | Route is registered and reachable |
| `SseEndpoint_ContentTypeIsEventStream` | Correct MIME type |
| `SseEndpoint_SendsCacheControlNoCache` | Proxy/browser caching suppressed |
| `SseEndpoint_DeliversRetryDirective` | Initial SSE frame format |
| `SseEndpoint_DeliversConnectedMessage` | `{"connected":true}` handshake |
| `SseEndpoint_AcceptsMultipleConcurrentClients` | Registry handles ≥2 connections |
| `PushSseEvent_BroadcastsToConnectedClient` | Push reaches open stream |
| `ScheduleSsePush_WithZeroDelay_BroadcastsStatusEvent` | Synchronous push path |
| `ScheduleSsePush_WithDelay_BroadcastsAfterDelay` | Delayed push ≥150 ms guard |
| `PushSseEvent_WhenNoClientsConnected_DoesNotThrow` | Empty-registry guard |
| `ScheduleSsePush_WhenNoClientsConnected_DoesNotThrow` | Empty-registry no-op |
| `Stop_TerminatesOpenSseStream` | `DisposeAllSseClients` closes streams |
| `Status_StillReturnsOk_AfterSseEndpointAdded` | Regression: existing route intact |
| `Health_StillReturnsOk_AfterSseEndpointAdded` | Regression: existing route intact |

**Run result: 341/342 passed (pre-existing `AppConfigServiceTests.Load_WhenNoFileExists_ReturnsDefaultConfig` failure due to environment file lock; unrelated to SSE).**

---

## 2. 🎵 Synced scrolling lyrics in the browser music player

**File targets:** `Services/Web/WebServer.Handlers.MusicPlayback.cs`, `WebAssets/app-library.js`, `WebAssets/styles.css`

**Status:** `[ ] Pending`

### What
Expose the current LRC lyric line (and surrounding lines) via `/api/music/status` and render a live scrolling lyrics panel on the music player card in the web UI. If no local `.lrc` file is cached, fetch from the **LRCLIB API** (`https://lrclib.net/api/get?artist_name=...&track_name=...`).

### Why
The karaoke engine already exists but is WPF-only (fullscreen overlay). Your phone — the primary remote — shows nothing. This brings the experience to the remote itself.

### Rough approach
- Add `currentLyricLine`, `prevLyricLine`, `nextLyricLine` fields to the music status JSON response.
- In `MusicPlayer.GetStatus()`, read the current `_lrcLines` position and emit the three surrounding lines.
- Add LRCLIB fetch fallback in `WebServer.Handlers.MusicPlayback.cs` when no cached `.lrc` exists for the current artist+title.
- In JS: render a `<div id="lyrics-panel">` below the track info with prev/active/next styled blocks; update on each status poll (or SSE push from suggestion #1).

---

## 3. 🖼️ Album art from ID3 tags on music cards

**File targets:** `Services/Web/WebServer.Handlers.MusicPlayback.cs`, `WebAssets/app-library.js`, `WebAssets/_card_block.css`

**Status:** `[x] Completed`

### What
Extract embedded `APIC` / picture frames from MP3, FLAC, M4A, and OGG files. Serve them via `/api/music/art?path=...`. Render as card backgrounds in the music library and as a blurred full-screen backdrop during fullscreen karaoke.

### Why
Music cards currently show a generic note icon. Album art transforms the music section from functional to visually polished — it is the single highest visual-impact change for the music tab.

### Rough approach
- Add **TagLibSharp** NuGet package (`TagLib-Sharp`).
- Add `HandleMusicArt` handler: open the file with `TagLib.File.Create(path)`, read `Tag.Pictures[0].Data.Data`, respond as `image/jpeg` (or `image/png`).
- Cache extracted art bytes in a `ConcurrentDictionary<string, byte[]?>` keyed by file path (same pattern as `_thumbCache`).
- In the music card HTML template in JS, set `background-image: url('/api/music/art?path=...')` on the card.
- In the fullscreen karaoke XAML, add an `<Image>` behind the lyrics panel that fetches art via the same endpoint.

---

### Implementation report

#### Pre-existing (already fully implemented)
The **browser side** and **server backend** were already complete before this suggestion was implemented:

| Component | What existed |
|-----------|-------------|
| `TagLibSharp` | Already in `RemotePlay.csproj` (`Version="2.*"`) |
| `/api/music/cover?path=...` (`HandleMusicCover`) | TagLib embedded-picture read → folder cover fallback (`cover.jpg`, `folder.jpg`, `front.jpg`, `album.jpg`, variants), cached in `_coverCache` (`ConcurrentDictionary<string,(byte[],string)?>`) |
| `/api/music/album-art?album=&artist=` | MusicBrainz + Cover Art Archive online fallback with in-memory + disk cache + in-flight deduplication |
| Route registrations | Both routes wired in `WebServer.Handlers.Request.cs` |
| Browser track cards | `renderMusicCards` uses `<img class="mtc-thumb" src="/api/music/cover?path=...">` with `onerror` fallback to `/api/music/album-art` |
| Browser player bar | `#music-bar-art` uses the same two-stage cover → online art pipeline |
| Playlist cards | `_loadPlaylistAlbumArt` derives album art from the first track in each playlist |

#### New — WPF karaoke fullscreen album-art backdrop

The only missing piece was the **blurred album-art backdrop** on the `IdleOverlay` during music fullscreen (karaoke mode). The overlay was plain black; the suggestion called for rendering cover art as a blurred, dimmed full-screen background behind the lyrics panel.

##### `MainWindow.xaml`
Added two new elements inside `IdleOverlay`, z-ordered behind `KaraokeLyricsPanel`:

| Element | Purpose |
|---------|---------|
| `KaraokeArtBackdrop` (`<Image>`, ZIndex=1) | Stretch-fills the overlay with the album art; `BlurEffect Radius=32 KernelType=Gaussian` |
| `KaraokeArtScrim` (`<Border>`, ZIndex=2, `Background="#A0000000"`) | 63%-opaque dark overlay on top of the art so lyrics stay legible |

`KaraokeLyricsPanel` was given explicit `Panel.ZIndex="3"` to maintain correct stacking order.

##### `MainWindow.Karaoke.cs`
- Added `_karaokeCoverNames` — local copy of the cover file name list (`cover.jpg`, `folder.jpg`, `front.jpg`, `album.jpg` + `.png` variants).
- Added `LoadKaraokeArtBackdropAsync(string filePath)` — awaits a `Task.Run` that:
  1. Tries `TagLib.File.Create(filePath).Tag.Pictures[0].Data.Data` (embedded ID3 art).
  2. Falls back to scanning the track's folder for the first matching cover file.
  3. Returns `null` if nothing found.
  - Back on the UI thread (`Dispatcher.InvokeAsync`): creates a frozen `BitmapImage` from the bytes and shows `KaraokeArtBackdrop` + `KaraokeArtScrim`; clears them on failure/miss.
- Added `ClearKaraokeArtBackdrop()` — sets `Source = null` and collapses both elements.
- `StartKaraokeSession`: after starting the lyrics timer, fires `_ = LoadKaraokeArtBackdropAsync(status.CurrentPath)` (fire-and-forget is intentional — the backdrop loads asynchronously without blocking session startup).
- `StopKaraokeSession`: calls `ClearKaraokeArtBackdrop()` to reset state when music stops.

**Build result: success (0 errors, 0 warnings).**

---

## 4. 📍 Chapter markers on the video seek bar

**File targets:** `Services/Web/WebServer.Handlers.VideoPlayback.cs`, `WebAssets/app-playback.js`, `WebAssets/styles.css`

**Status:** `[x] Completed`

### What
Read MKV/MP4 chapter metadata from VLC (`libvlc_media_get_chapter_description`) while a video is playing. Expose it via `/api/chapters`. Render chapter dots on the `<input type="range">` seek bar in the browser; hovering a dot shows the chapter name.

### Why
Chapter markers are the #1 most-requested missing feature in media players. MKV files commonly carry them and users expect to be able to jump to chapters.

### Rough approach
- In `MainWindow.Playback.cs`, after a file starts playing, call `_mediaPlayer.ChapterDescription(-1)` to enumerate all chapters.
- Add a `/api/chapters` GET endpoint that returns `[{ title, startSeconds }]` from the current media.
- In JS: after a `play()` call, fetch `/api/chapters` and render `<span class="chapter-dot">` elements absolutely positioned over the seek bar track at the correct `left: X%` positions.
- Add a tooltip (`title` attribute or custom overlay) showing the chapter name on hover.

---

### ✅ Implementation Report

**Completed:** Chapter markers on the seek bar fully implemented and rendered from existing `/api/status` chapter data.

#### What was already in place
- `MainWindow.Playback.cs` `GetPlaybackStatus()` already populated `PlaybackStatus.Chapters` (array of `ChapterInfo { Id, Name, StartSeconds, DurationSeconds }`) and `CurrentChapter` from VLC's `FullChapterDescriptions(-1)`.
- `/api/status` already serialised `chapters` and `currentChapter` to the browser.
- `app-playback.js` `updateTrackControls()` already rendered a chapter dropdown select when `chapters.length > 1`.
- `/api/chapter?id=N` already existed to seek to a chapter by index.

#### Files modified
| File | Change |
|------|--------|
| `WebAssets/index.html` | Wrapped `#progress` input in a new `#seek-wrapper` (relative-positioned) div; added `#chapter-markers` overlay div inside the wrapper |
| `WebAssets/app-playback.js` | Added `updateChapterMarkers(s)` — renders `<span class="chapter-pin">` elements at `left: X%` positions derived from `chapter.startSeconds / duration * 100`; pins marked `.active` for current chapter; click/keyboard delegation calls `/api/chapter?id=N`; `updateTrackControls()` now calls `updateChapterMarkers(s)` on every poll |
| `WebAssets/styles.css` | Added `#seek-wrapper`, `#chapter-markers`, `.chapter-pin` (3 px white tick, hover scales + tooltip via `::after`), and `.chapter-pin.active` (accent colour) |

#### Architecture details
- Chapter first tick (index 0 / `startSeconds = 0`) is suppressed — no pin at the very start.
- Pins are deduplicated by a `data-signature` attribute to avoid DOM thrashing on every 1-second poll.
- Click delegation is wired once via an IIFE at module load — not on every re-render.
- The pin tooltip uses a CSS `::after` pseudo-element with `content: attr(title)` for zero JS overhead.

---

## 5. 📱 Touch gesture controls (mobile seek + volume)

**File targets:** `WebAssets/app-playback.js`, `WebAssets/styles.css`

**Status:** `[ ] Pending`

### What
Add touch gesture handlers to the video player area:
- **Swipe left / right** → seek ±10 s (with haptic)
- **Swipe up / down on right half** → volume up/down
- **Swipe up / down on left half** → brightness up/down
- **Double-tap left / right zones** → quick ±10 s skip (YouTube-style)

### Why
Zero backend changes required — pure JS `touchstart`/`touchend` event handling. Makes the phone feel like a native remote instead of a web form.

### Rough approach
- Attach `touchstart` / `touchmove` / `touchend` listeners to the player control area (`#player-section` or a new `#gesture-zone` overlay div).
- Track `deltaX` and `deltaY` to distinguish horizontal vs. vertical swipes, and left vs. right half for brightness/volume.
- Reuse existing `skip()`, `setVolume()`, `setBrightness()` JS functions.
- Show a brief OSD (on-screen display) toast (`+10s`, `Vol 70%`) using the existing `setStatus()` mechanism.
- Gate behind a media-query or a "gesture mode" toggle in Settings so desktop users are not affected.

---

## 6. 📺 "Next Episode" auto-detection and countdown

**File targets:** `Services/Web/WebServer.Handlers.VideoPlayback.cs`, `WebAssets/app-playback.js`, `WebAssets/index.html`, `WebAssets/styles.css`

**Status:** `[x] Completed`

### What
When a video ends (`PlaybackEndMode.Stop`), scan the same folder for the next file in natural sort order. If found, show a **"Up Next"** countdown card (10 s, dismissible) in the web UI that auto-plays the next episode.

### Why
All the required pieces exist: `NaturalStringComparer`, `PlaybackHistory`, and the end-of-playback callback. This closes the most common "I have to pick the next episode manually" friction point.

### Rough approach
- Add a `/api/next-in-folder?path=<current>` endpoint that returns the next file in the same directory using `NaturalStringComparer`.
- In JS: when the status response shows `isPlaying: false` and `position ≈ duration` (end of file), call `/api/next-in-folder`, if a result comes back render the "Up Next" card with a 10 s countdown.
- Wire the countdown to call `play(nextPath)` on expiry, or dismiss on user tap.
- Add a "Play Next" option to the video card context menu as well.

---

### ✅ Implementation Report

**Completed:** "Up Next" auto-detection and countdown fully implemented, tested, and integrated.

#### Files created
| File | Purpose |
|------|---------|
| `RemotePlay.Tests/NextInFolderTests.cs` | 16 integration tests for the new endpoint (bad path, missing path, single file, next/previous, natural sort, last file, direction param, non-video exclusion) + 3 chapter API tests |

#### Files modified
| File | Change |
|------|--------|
| `Services/Web/WebServer.Handlers.VideoPlayback.cs` | Added `HandleNextInFolder()` — decodes `?path=` (Base64), lists the same directory filtered to video extensions, orders by natural sort on filename, finds current index, returns `{ found, path, title }` for the adjacent file. Supports `?direction=previous` |
| `Services/Web/WebServer.Handlers.Request.cs` | Added `/api/next-in-folder` to the routing switch and to `_pollingPaths` (suppresses verbose log spam) |
| `WebAssets/index.html` | Added `#up-next-card` element (hidden by default) with Up Next label, title, Play Now / Dismiss buttons, and countdown span |
| `WebAssets/app-playback.js` | `updateTrackControls()` now calls `updateUpNext(s)` / `_clearUpNext()` on every poll. `updateUpNext()` triggers when `position >= duration - 30` and `duration > 60`; fetches `/api/next-in-folder` once per file path; shows the card. `_showUpNext()` starts a 1-second `setInterval` countdown; `upNextPlayNow()` / `upNextDismiss()` handle user interaction; `_triggerUpNextPlay()` calls `/api/play` then refreshes status |
| `WebAssets/styles.css` | Added `#up-next-card` (hidden/grid toggle via `.visible`), `.up-next-label`, `.up-next-title`, `.up-next-actions`, `.up-next-countdown` + responsive overrides for phone layout |

#### Architecture details
- **Debounce:** `_upNextFetchedForPath` prevents re-fetching on every poll tick for the same file; `_upNextDismissedPath` suppresses re-showing after user dismisses for the current file.
- **Timing:** Card appears with 30 s remaining (configurable in code); auto-plays after 10 s countdown.
- **Natural sort:** Uses `_naturalComparer` (`NaturalStringComparer`) — episodes sort Episode 2 < Episode 10 correctly.
- **Direction param:** `/api/next-in-folder?direction=previous` returns the preceding file (used by tests; available for future UI).
- **Non-video exclusion:** Uses the same `_videoExtensions` HashSet as the rest of the server — `.srt`, `.txt`, etc. are invisible.

#### Test results
- **16 integration tests** (`NextInFolderTests`) — all pass: missing param → 400, non-existent file → 200+found=false, single file → found=false, next file correct title/path, last file → found=false, natural sort ordering (ep2 < ep10), direction=previous, previous of first → found=false, non-video files ignored, status chapters/currentChapter fields present, chapter seek valid/missing/non-numeric IDs.
- **Total suite:** 384 / 384 tests pass, zero regressions (16 new + 368 existing).

---

## 7. 🎛️ Custom per-band EQ sliders in the web UI

**File targets:** `Services/Web/WebServer.Handlers.MusicPlayback.cs`, `Services/Audio/EqualizerSampleProvider.cs`, `WebAssets/app-library.js`, `WebAssets/styles.css`

**Status:** `[ ] Pending`

### What
Add a `/api/music/eq/custom` POST endpoint that accepts an array of 10 gain values (dB, ±12). Render 10 vertical `<input type="range">` sliders in a collapsible EQ panel on the music player — labelled 60 Hz through 16 kHz — alongside the existing preset dropdown.

### Why
The 10-band BiQuad engine and 18 named presets already exist server-side (`EqualizerSampleProvider`). Exposing per-band control in the browser turns the music player into a proper audiophile remote. Visually striking and genuinely useful.

### Rough approach
- Add `ApplyCustomGains(float[] gains)` to `EqualizerSampleProvider` (mirrors the existing `ApplyPreset` path but takes an explicit array).
- Add `HandleMusicEqCustom` handler: deserialise a `double[]` from the request body, clamp to ±12 dB, call `_musicPlayer.SetCustomEq(gains)`.
- In JS: render `<div id="eq-panel">` with 10 vertical range inputs; `oninput` debounces and POSTs the current gain array to `/api/music/eq/custom`.
- Sync slider positions when a preset is selected (read back the preset's gain table).

---

## 8. 🎉 Watch Party mode (multi-client sync lock)

**File targets:** `Services/Web/WebServer.cs`, `Services/Web/WebServer.Handlers.VideoPlayback.cs`, `WebAssets/app-playback.js`, `WebAssets/app-core.js`

**Status:** `[ ] Pending`

### What
A mode where one client is the "host" and all other connected clients are forced in sync. The host's play/pause/seek is broadcast to every other client in real time. Clients see a 🎉 badge and their controls become read-only (or show a "Take Control" button to become the new host).

### Why
Watching a movie with someone on a different device/room. All existing transport controls can be reused — the only new piece is the broadcast and the host-lock concept.

### Rough approach
- Requires SSE from suggestion #1 as a foundation (push state to all clients).
- Add `/api/party/start` and `/api/party/stop` endpoints; track `_partyHostIp` on the server.
- When `_partyHostIp` is set, every play/pause/seek API call that originates from a non-host IP is rejected with `403 + { "syncLocked": true }`.
- Push a `party` event type over SSE so all clients know to lock their UI.
- In JS: detect `syncLocked` and grey out transport controls, show 🎉 badge and "Take Control" button.

---

## 9. 🔖 Video bookmarks / scene markers

**File targets:** `Services/Web/WebServer.Handlers.VideoPlayback.cs`, `WebAssets/app-playback.js`, `WebAssets/styles.css`

**Status:** `[ ] Pending`

### What
Let users pin a named timestamp in any video. Store bookmarks in a JSON sidecar next to the file (`<movie>.bookmarks.json`) or in the app data folder. Render them as small coloured pins on the seek bar. Clicking a pin jumps to that timestamp. A bookmark list panel shows all pins for the current file.

### Why
"That scene you always want to re-watch" — a niche but memorable feature. Bookmarks are persistent across sessions and devices (stored server-side).

### Rough approach
- Add `/api/bookmarks?path=` GET (list), POST (add `{ position, label }`), DELETE (remove by position).
- Store as `<AppPaths.UserDataDirectory>/bookmarks/<base64-path>.json`.
- In JS: after video loads, fetch bookmarks and render `<span class="bm-pin">` elements over the seek bar (same positioning technique as chapter markers from suggestion #4).
- Add an "Add Bookmark" button next to the seek bar; prompt for an optional label.

---

## 10. 🌐 DLNA / UPnP renderer discovery ("Play on TV")

**File targets:** `Services/Discovery/PresenceBroadcaster.cs` (or new `DlnaDiscovery.cs`), `Services/Web/WebServer.Handlers.VideoPlayback.cs`, `WebAssets/app-library.js`

**Status:** `[x] Completed`

### What
Scan the LAN for **DLNA MediaRenderer devices** (smart TVs, AVRs, Chromecast via DLNA bridge) using SSDP on UDP port 1900. Show them alongside RemotePlay peer instances in the existing peers dropdown. "Play on TV" sends the media stream URL to the renderer using UPnP `AVTransport:SetAVTransportURI` + `Play` actions.

### Why
RemotePlay already does UDP LAN discovery for its own peers. Extending that to DLNA bridges it into the broader home media ecosystem — users can send a movie to their TV without a separate app.

### Rough approach
- Add a `DlnaDiscovery` service that sends the SSDP M-SEARCH multicast (`239.255.255.250:1900`) for `urn:schemas-upnp-org:device:MediaRenderer:1`.
- Parse the `LOCATION` header from responses, fetch the device description XML, extract the `AVTransport` control URL.
- Add a `/api/dlna/renderers` GET endpoint and a `/api/dlna/play` POST endpoint (takes renderer URL + media path).
- In JS: merge DLNA renderers into the peers dropdown with a 📺 icon; clicking one calls `/api/dlna/play` instead of navigating to the peer's RemotePlay instance.

---

### ✅ Implementation Report

**Completed:** DLNA / UPnP renderer discovery and "Cast to TV" feature fully implemented, tested, and integrated.

#### Files created
| File | Purpose |
|------|---------|
| `Services/Discovery/DlnaDiscovery.cs` | New service: SSDP M-SEARCH scanner, device XML parser, AVTransport SOAP helpers, renderer cache with TTL expiry |
| `RemotePlay.Tests/DlnaDiscoveryTests.cs` | 20 unit tests for all parsing and SOAP-building helpers |

#### Files modified
| File | Change |
|------|--------|
| `Services/Web/WebServer.cs` | Added `_dlna` field and optional `DlnaDiscovery? dlna = null` constructor parameter |
| `Services/Web/WebServer.Handlers.VideoPlayback.cs` | Added `HandleDlnaRenderers()` and `HandleDlnaPlay()` handler methods |
| `Services/Web/WebServer.Handlers.Request.cs` | Registered `/api/dlna/renderers` and `/api/dlna/play` routes; added `/api/dlna/renderers` to polling-paths (no verbose logging) |
| `WebAssets/app-library.js` | `refreshPeers()` fetches renderers in parallel via `Promise.all`; `renderPeersDropdown()` renders a "📺 DLNA renderers" section with Cast buttons; new `playOnDlna()` helper sends POST to `/api/dlna/play` |
| `WebAssets/styles.css` | Added `.peers-section-header`, `.peer-dot-dlna`, `.peer-dlna` CSS rules for the renderer section |
| `RemotePlay.Tests/WebServerApiTests.cs` | Added 6 integration tests for the new DLNA endpoints |

#### Architecture details
- **`DlnaDiscovery`** runs a background `Timer` every 30 s; each tick sends an SSDP M-SEARCH multicast to `239.255.255.250:1900` and collects responses for 3 s.
- Discovered renderer `LOCATION` URLs are fetched, device-description XML is parsed for `friendlyName`, `UDN` (dedup key), and the `AVTransport controlURL`.
- `/api/dlna/renderers` returns `[{ name, host, controlUrl, usn }, …]` — empty array when no renderers found or DLNA not started.
- `/api/dlna/play` accepts `{ "controlUrl": "…", "mediaUrl": "…" }` and sends two sequential SOAP actions: `SetAVTransportURI` then `Play`. Returns `{ ok: true/false, error?: "…" }`.
- `DlnaDiscovery` accepts an injected `HttpClient` so its XML-parsing and SOAP logic are fully testable without real network I/O.
- All static helpers (`ParseLocationFromSsdpResponse`, `ParseDeviceDescription`, `BuildAbsoluteUrl`, `BuildMSearchMessage`, `BuildSetAvTransportUriSoap`, `BuildPlaySoap`) are marked `internal` and covered by unit tests.

#### Test results
- **20 unit tests** (`DlnaDiscoveryTests`) — all pass, covering SSDP parsing, device-description XML (valid + 5 negative cases), URL combination, SOAP content, and empty cache.
- **6 integration tests** (added to `WebServerApiTests`) — all pass, covering renderer list response shape, content-type, 503 when DLNA not running, 400/503 for bad body, and 404 for unknown sub-paths.
- **Total suite:** 368 / 368 tests pass, zero regressions.

---

*Last updated: generated by GitHub Copilot based on codebase analysis.*

