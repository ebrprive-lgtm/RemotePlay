# Local Video Playback Plan

**Status:** Feasibility and Phase 0 defaults are complete. Phases 1–3 are
implemented and verified; Phases 4–6 remain.

**Recommendation:** Add browser-local playback with an HTML `<video>` element
fed by a same-origin, byte-range-aware endpoint. Keep the existing Server
playback path available and unchanged for the MVP.

## Decision and feasibility

Video can play in Local mode without installing a browser plugin or transferring
the whole file to the client first. In this plan, **Local** means that the
current browser device renders and decodes the video; the media file remains in
the RemotePlay library and is streamed from the RemotePlay server over HTTP.

This is feasible for source files whose container and codecs the client browser
supports. Direct streaming does not convert the media, so it cannot guarantee
that every configured video file will play in every browser. The existing
Server player should remain the fallback for unsupported formats.

## Options considered

| Approach | Assessment |
| --- | --- |
| Same-origin HTTP range stream into browser `<video>` | Recommended MVP. Reuses the library file and server; supports efficient loading and seeking without server-side decoding. Browser codec support remains the limit. |
| VLC/FFmpeg remux or transcode to browser-friendly media | Feasible as a later compatibility feature, but adds process management, CPU/bandwidth cost, seek and track handling, and cleanup work. No web transcode path exists today. |
| `file://` URL, full-file download, or capturing the server player's window | Not recommended. A remote browser cannot use the server's filesystem path; downloading wastes time/storage; capturing server output adds latency and complexity. |

## Verified current state

- The Local/Server preference is stored per browser. The Local handoff logic in
  [app-core.js](./WebAssets/app-core.js) handles audio playback; it does not
  hand off a Video session.
- Video selection in [app-library.js](./WebAssets/app-library.js) currently
  always calls `/api/video/play`. That route dispatches to `HandlePlay` in
  [WebServer.Handlers.VideoPlayback.cs](./Services/Web/WebServer.Handlers.VideoPlayback.cs),
  which starts the server-side player through its callback.
- [index.html](./WebAssets/index.html) has the server video status/control
  surface and a local `<audio>` element, but no local `<video>` element. The
  transport functions in [app-playback.js](./WebAssets/app-playback.js) send
  pause, seek, skip, and volume changes to server endpoints.
- Phase 1 adds `/api/video/stream` in
  [WebServer.Handlers.VideoStream.cs](./Services/Web/WebServer.Handlers.VideoStream.cs).
  The
  [Music stream handler](./Services/Web/WebServer.Handlers.MusicPlayback.cs)
  remains useful prior art for direct streaming, but its permissive range
  parsing was not copied unchanged.
- Video files are represented in the browser by encoded path tokens from
  [WebPathHelpers.cs](./Helpers/Web/WebPathHelpers.cs). The stream endpoint
  authorizes normal paths against configured movie roots and validates
  `.rplink` sources and targets. Base64-encoding a path is not authorization.
  The existing server video play handler still does not apply the same
  library-root check.
- Server-player progress is saved by the WPF playback tracker into
  `PlaybackHistory`. Browser clients have per-IP histories through
  [WebServer.History.cs](./Services/Web/WebServer.History.cs), but browser-local
  video has no progress reporter yet. The current `/api/local-playing` boolean
  is not a progress record.
- The web video controls also rely on server status polling, `/api/adjacent`,
  and the server-owned video queue. Local playback must route those interactions
  to the browser player instead of accidentally starting or controlling the
  server player.
- The default configured extensions include MP4, MKV, AVI, MOV, WMV, M4V, TS,
  and FLV. These are container extensions, not a guarantee about the codecs
  inside each file. No browser-oriented video transcode endpoint was found.

## Recommended implementation

### Phase 0: Feasibility and scope (complete)

- [x] Trace the Local preference, Video card launch, server playback route, and
  current browser media elements.
- [x] Confirm that HTTP byte-range streaming is already used for browser audio.
- [x] Select direct source streaming plus an HTML video element as the MVP.
- [x] Set the initial conformance target to desktop Chrome and Edge on Windows.
  Other browsers/devices are best-effort until tested; keep Server mode as the
  fallback and do not promise universal codec support.
- [x] Set the default that Local video pauses and preserves its position when
  leaving the Video view, so audio does not continue from a hidden surface.

### Phase 1: Add a secure video stream endpoint (complete)

- [x] Add a `GET /api/video/stream` route in
  [WebServer.Handlers.Request.cs](./Services/Web/WebServer.Handlers.Request.cs),
  implemented by
  [WebServer.Handlers.VideoStream.cs](./Services/Web/WebServer.Handlers.VideoStream.cs).
  It accepts the existing encoded video path token.
- [x] Decode and validate the requested file before opening it:
  - [x] Require an existing regular file with an allowed video extension.
  - [x] For a normal library file, require it to be inside one of
    `AllResolvedMoviesPaths`.
  - [x] For linked entries, validate a `.rplink` source inside a configured
    root. `linkPath` must resolve to the requested file, or the requested file
    must be under the linked directory target.
  - [x] Use the existing `WebPathHelpers.IsUnderRoot` full-path boundary
    policy, matching library enumeration and browse behavior. OS symlink and
    junction targets are not separately resolved; external `.rplink` targets
    require their validated in-library link source. An arbitrary path or
    Base64 token alone does not grant access.
- [x] Return container MIME types and `Content-Disposition: inline`. Stream
  through a fixed-size buffer rather than buffering the complete video.
- [x] Implement and test single-byte-range requests:
  - [x] No `Range`: `200 OK`, full length, and no `Content-Range`.
  - [x] Valid `bytes=start-end`, `bytes=start-`, and suffix ranges: `206 Partial Content`
    with exact `Content-Length`, `Content-Range`, and `Accept-Ranges`.
  - [x] Well-formed but unsatisfiable ranges: `416 Range Not Satisfiable` and
    `Content-Range: bytes */<file-length>`.
  - [x] Empty files return an empty `200`; a range against an empty file is
    unsatisfiable. Malformed, unsupported-unit, reversed, and multi-range
    requests that reach the handler are ignored and served as full `200`
    responses. `HttpListener` itself rejects some malformed `Range` syntax
    before route dispatch (for example, `bytes=invalid`) with `400`.
  - [x] Client disconnects are logged at detail level and server shutdown
    cancels an active stream without logging a disconnect as an application
    error.
- [x] Apply the existing library-root access policy; same-origin URLs remain
  the intended browser use, and CORS is not treated as path authorization.
- [x] Add focused HTTP and parser tests for response status/headers/body,
  range edge cases, unsupported extensions, missing and outside-root files,
  additional configured roots, and authorized file/directory link targets.
  The parser is video-specific and does not change Music stream behavior.

### Phase 2: Add the browser-local video surface

- [x] Add a dedicated responsive `<video>` player surface in the Video view. Use
  `playsinline` and metadata preloading; provide accessible labels, an explicit
  Stop action, and browser fullscreen support.
- [x] Keep the local video surface out of the shared Radio/Podcast/Music dock and
  hide it when Video is not the active view. Do not reuse the audio element or
  `_localMediaType` as the video player state.
- [x] In the Video `play(path, name)` flow, branch on `isPlayLocal()`:
  - [x] Local: set the video source to
    `/api/video/stream?path=<encoded-path-token>`.
    Include and validate the link context returned by browse for authorized
    linked files.
-  - [x] Server: retain the current `/api/video/play` request and behavior.
- [x] Preserve the browser user-activation window. The current Video `play()`
  awaits cleanup before starting playback; the Local branch must invoke
  `HTMLMediaElement.play()` from the original click/toggle gesture before any
  awaited network teardown that could cause autoplay policy to reject it.
- [x] Drive the Local UI from the video element's media events and properties
  (`play`, `pause`, `loadedmetadata`, `timeupdate`, `seeking`, `seeked`,
  `ended`, `error`, and `volumechange`). Do not let `/api/status` overwrite the
  local title, duration, position, pause state, or controls.
- [x] Show explicit load and playback errors. If the browser rejects the media,
  offer a clear **Play on Server** action rather than leaving a stale or
  success-shaped player state.
- [x] Disable the Local/Server mode toggle while a Local Video session is
  loaded; position-preserving mode handoff remains part of Phase 3.

### Phase 3: Wire controls, queue, and mode handoff

- [x] Route Play/Pause, Stop, seek-bar commit, quick-skip, volume, and mute to
  the browser video element while it owns playback. Continue using the current
  server endpoints in Server mode.
- [x] Keep server-only controls honest: audio-track/subtitle selection and
  server-side picture adjustments must either be implemented for browser
  playback or hidden/disabled in Local mode. Do not route them to the server
  while a browser video is active. In particular, do not imitate sharpening
  with brightness or contrast changes.
- [x] Preserve the existing Video queue, selection-to-queue action, and
  previous/next behavior. `/api/adjacent` and the current queue are
  server-oriented; Local mode must select the next item without asking the
  server player to start it. The browser's `ended` event should use the same
  queue policy and continue in the selected playback mode.
- [x] Support mode switching during playback:
  - Server to Local: capture the current encoded path, position, and paused
    state; start the browser player from the toggle gesture; stop server
    playback only after the browser source is ready to play.
  - Local to Server: capture the browser position and paused state; start the
    same file on the server at that position; stop the browser only after the
    handoff succeeds. `/api/video/play` accepts an optional validated `pos`
    value and starts the server player at that position.
  - Avoid duplicate audio during transition and retain the old player if the
    new target fails to start. Report handoff failures visibly.
- [x] Keep the browser video element and per-page Local Video state as the
  source of truth. `/api/local-playing` remains a server-wide hint for the WPF
  idle overlay; it does not determine Local Video playback state.
- [x] Treat a paused server player (`isPlaying: false`, `isPaused: true`) as
  active for handoff confirmation and shared player controls, preserving its
  paused state when switching modes.

Focused regression coverage in `WebAssetRegressionTests` and
`WebServerApiTests` passes (102 tests).

### Phase 4: Preserve resume, history, and lifecycle

- [x] Add `POST /api/video/local-progress` with library-root and `.rplink`
  authorization, finite position/duration validation, and per-client writes via
  `GetHistoryForIp` and `PlaybackHistory`. Record recent playback before media
  duration is available; include additional configured roots and authorized
  linked files in recent results.
- [x] Throttle Local progress writes to 15 seconds and force saves on play,
  pause, seek, end, stop/source change, and page exit where supported. Mark
  Local playback watched only after completion, while retaining explicit
  watched actions and the server history near-end semantics.
- [x] Adapt Resume and Start from Beginning to use the Local video's
  `currentTime`, clear the correct client's saved entry, preserve linked-file
  context, and keep the resume prompt confined to the Video view.
- [x] Save progress during Local cleanup without allowing status polling or a
  failed save to silently replace Local playback state.
- [x] Use the existing per-IP history behavior; no browser-global progress
  flag is used to merge progress from unrelated clients.
- [x] Persist Server progress to the client that initiated playback, so newer
  Server progress is not shadowed by that client's older Local progress and
  status polls from other clients do not overwrite its history.

### Phase 5: Codec compatibility and optional transcoding

- Start with unchanged source bytes. Test representative files from the actual
  library, including an H.264/AAC MP4 and files using containers/codecs that
  are known not to work in the target browser.
- Use `canPlayType()` only as a hint. If codec metadata is available, optionally
  use `MediaCapabilities.decodingInfo()` to improve the preflight message; still
  handle the runtime media `error` event.
- Explain unsupported formats and keep the Server playback fallback visible.
  Do not claim that all configured extensions are browser playable.
- Treat remux/transcode as a separate follow-up if real usage requires it.
  There is no existing browser transcoding pipeline. A later design must cover
  the selected VLC/FFmpeg tool, process lifetime, concurrent sessions, CPU and
  bandwidth limits, seek behavior, audio/subtitle tracks, cleanup, and
  user-visible failures. Prefer remuxing when compatible; transcode only when
  required.

### Phase 6: Verification and rollout

- Add focused frontend regressions for Local/Server routing, status-poll
  isolation, controls, error handling, and the Local mode handoff.
- [x] Add server tests for stream range/path cases in Phase 1.
- Manually test Chrome/Edge desktop and any explicitly supported mobile
  browsers using:
  - A known-supported MP4, with start, pause, resume, seek, skip, stop, and
    fullscreen.
  - A browser-unsupported or uncertain container/codec, verifying the fallback.
  - Local-to-Server and Server-to-Local handoff, including paused playback.
  - Video queue/next/previous, resume history, and navigation away from and
    back to Video.
  - A slow connection and a disconnected client during a range response.
- Retain existing movie-card click and playback-launch regression coverage.
  Confirm Podcast/Music queue and dock behavior are unchanged.

## MVP acceptance criteria

1. With Local selected, choosing a movie starts playback in the current browser
   through the video stream route and does not invoke `/api/video/play`.
2. With Server selected, the existing server player still launches as before.
3. Seeking causes correct range requests and does not buffer the entire file in
   memory; invalid ranges and paths fail with appropriate HTTP responses.
4. Local transport and status reflect the HTML video element, not the server's
   unrelated or stopped player.
5. Resume/progress, watched state, queue, and previous/next behavior remain
   usable in Local mode.
6. Mode handoff preserves file, position, and pause/play state without leaving
   both players active or silently losing playback on failure.
7. Unsupported browser media produces a useful message and a Server-mode
   fallback.
8. Local Video UI is confined to the Video view and does not reintroduce an
   audio dock in Video or alter movie-card click behavior.

## External references

- [MDN: `<video>` element](https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/video)
- [MDN: `canPlayType()`](https://developer.mozilla.org/en-US/docs/Web/API/HTMLMediaElement/canPlayType)
- [MDN: Media Capabilities API](https://developer.mozilla.org/en-US/docs/Web/API/Media_Capabilities_API)
- [MDN: `HTMLMediaElement.play()` and autoplay behavior](https://developer.mozilla.org/en-US/docs/Web/API/HTMLMediaElement/play)
- [MDN: HTTP range requests](https://developer.mozilla.org/en-US/docs/Web/HTTP/Range_requests)
- [MDN: 206 Partial Content](https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/206) and
  [416 Range Not Satisfiable](https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/416)
