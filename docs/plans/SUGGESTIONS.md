# RemotePlay - Active Suggestions

A backlog of ideas that are still pending. File targets are relative to the
repository root. Completed implementation reports are preserved in the
[completed suggestions archive](../archive/COMPLETED_SUGGESTIONS.md).

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
- In JS: render a `<div id="lyrics-panel">` below the track info with prev/active/next styled blocks; update on each status poll (or SSE push from the completed [suggestion #1](../archive/COMPLETED_SUGGESTIONS.md)).

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
- Builds on the completed SSE foundation in [suggestion #1](../archive/COMPLETED_SUGGESTIONS.md) to push state to all clients.
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
- In JS: after video loads, fetch bookmarks and render `<span class="bm-pin">` elements over the seek bar (same positioning technique as the completed chapter-marker work in [suggestion #4](../archive/COMPLETED_SUGGESTIONS.md)).
- Add an "Add Bookmark" button next to the seek bar; prompt for an optional label.

---
