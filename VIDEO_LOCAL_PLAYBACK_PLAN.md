# Local Video Playback Plan

**Status:** Phases 0–6, tablet audio compatibility, Local Video track
diagnostics, transcode progress/ETA, and server-side transcode cancellation are
implemented. Focused automated tests pass, and a desktop Chrome smoke run is
complete. The user confirmed Local Video audio works on the tablet; first
playback can wait for conversion. Tablet fullscreen, Local Music audio/seek,
and the latest compact-player cancellation behavior still need device
verification.

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
| FFmpeg audio compatibility conversion for touch clients | Implemented as an optional stream path. It copies the first video stream, converts audio tracks to stereo AAC, and serves a cached MP4 through the same range handler. The host must have FFmpeg on `PATH`; first playback waits for conversion and needs temporary disk space. Video codecs are not converted, and subtitles are omitted, so this does not guarantee universal browser compatibility. |
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
  uses the same single-range parser so Local Music seeking follows standard
  byte-range response semantics.
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
  inside each file.
- Touch clients request `audioCompat=auto` and send only positively reported
  `canPlayType()` hints for known audio codec profiles. After authorizing the
  source, the server probes its tracks with FFprobe inside the cancellable
  operation. Known, supported stereo audio in MP4/M4V is streamed unchanged;
  supported audio in other containers is copied into an MP4 without audio
  re-encoding; unknown, unsupported, or multichannel audio is converted to
  stereo AAC. If FFprobe cannot provide usable metadata, the server keeps the
  conservative AAC fallback. Generated MP4s are cached by source path, size,
  modified time, and compatibility mode; copy-remux output is distinct from
  AAC output and cache entries expire after 24 hours without use. Desktop
  clients continue to stream the original file.
- Local Video diagnostics use `/api/video/codec-info` to authorize the same
  source/link path and probe source tracks with FFprobe on the RemotePlay host.
  Missing FFprobe or probe failures are shown explicitly instead of reporting
  an empty track list.

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
- [x] For `audioCompat=aac`, authorize the original source before conversion,
  then use the compatible MP4 as the range-stream source.
- [x] Add focused HTTP and parser tests for response status/headers/body,
  range edge cases, unsupported extensions, missing and outside-root files,
  additional configured roots, and authorized file/directory link targets.
  The Music stream handler also uses the parser and omits `Content-Range` from
  full `200 OK` responses.

### Phase 2: Add the browser-local video surface

- [x] Add a dedicated responsive `<video>` player surface in the Video view. Use
  `playsinline` and metadata preloading; provide accessible labels,
  click-to-fullscreen support, and the shared player dock's Stop action.
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
  - Bound handoff requests and status reads; show the pending target and
    release the pending UI before the background status refresh completes.
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

- [x] Keep streaming original bytes and verify the response body and container
  MIME type for every configured video extension.
- [x] Use `canPlayType()` synchronously as a conservative hint for known audio
  codec profiles. Send only positive `maybe`/`probably` results, and use
  FFprobe source metadata to select direct playback, audio-copy remuxing, or
  AAC encoding. Unknown profiles, unsupported results, and multichannel audio
  keep the AAC fallback; runtime media errors remain authoritative.
- [x] Explain uncertain/unsupported browser formats without preventing the
  playback attempt, and keep the Play on Server fallback visible.
- [x] Add touch-client codec-aware audio compatibility: preserve supported
  stereo audio when possible, remux it into MP4 without re-encoding when
  needed, and use stereo AAC when support is unknown or insufficient. Copy the
  video stream, preserve HTTP range seeking, cache each compatibility mode
  separately, and expire cached output after 24 hours without use. Desktop
  clients retain direct streaming.
- [x] Report missing FFmpeg and conversion failures explicitly, and show
  codec-checking, audio-copy, or AAC-conversion status as appropriate. Keep the
  cancel action visible only while FFmpeg is actively processing; cancellation
  also stops an in-progress FFprobe check when replacing or stopping playback.
- [x] Expose authorized FFmpeg audio-transcode progress and a speed-based
  remaining-time estimate in the Local player and diagnostics. If media
  duration is unavailable, show progress without inventing an ETA.
- [x] Stop Server playback when confirmed touch-client transcoding begins, so
  the isolated player closes during a long conversion. When no conversion is
  needed, retain the Local-readiness-first handoff; finalize only after both
  the server stop and Local readiness are confirmed.
- [x] Keep video conversion, subtitle preservation, and global CPU/storage
  limits out of scope. Copied video codecs still need to be supported by the
  browser; the server-side player remains the fallback.

Actual-library Chrome/Edge playback checks remain in the Phase 6 rollout
checklist; the repository contains no real-media fixtures for those checks.

### Phase 6: Verification and rollout

- [x] Add focused frontend regressions for Local/Server routing, status-poll
  isolation, HTML-media-backed controls, runtime error handling, and the Local
  mode handoff, including paused Server-to-Local startup.
- [x] Add server tests for stream range/path cases in Phase 1.
- [x] Test FFmpeg argument construction and source cache keys, and verify that
  compatibility requests still authorize the original path before conversion.
- [x] Smoke-test desktop Chrome using a generated H.264 MP4 served by the real
  WebServer range endpoint: start, pause/resume, seek, held skip, stop,
  fullscreen entry/exit, view switching, and previous-video navigation.
- [x] Verify the unsupported-format fallback with an empty `.flv` fixture.
- [x] Exercise paused Local-to-Server-to-Local handoff at about 5 seconds. The
  browser test simulated server playback/status responses because the stream
  test host has stub player callbacks; media streaming still used the real
  WebServer endpoint.
- [x] Verify a throttled 200,000-byte range response (206, about 7.74 seconds),
  abort a client during a full response, and confirm `/health` still returns
  200.
- [x] Retain existing queue, adjacent-video, progress/resume, and playback
  launch regression coverage. These behaviors are covered by the frontend and
  server tests; the smoke run did not manually exercise queue/resume history.
- [x] Retain existing movie-card click and playback-launch regression coverage.
  Existing frontend regressions also cover Podcast/Music queue and dock behavior.
- [x] Keep the compact Local preview in the player dock rather than above the
  browser list. A Server-to-Local handoff starts the Local player fullscreen;
  otherwise a dedicated overlay captures taps on the compact preview and opens
  fullscreen. Its prompt fades after 1.5 seconds of inactivity, but the preview
  remains tappable. Fullscreen shows the same fading center prompt to return to
  the thumbnail. While an audio compatibility transcode is active, that
  fullscreen button instead shows progress and the ETA, stays visible, and exits
  fullscreen when tapped. Native video controls are enabled only in fullscreen,
  while the shared player dock retains compact-mode playback controls.
- [x] Restore the Local player dock after startup cleanup stops previous media,
  and keep the selected Local/Server preference unchanged when stopping a Local
  video.
- [x] Return from fullscreen through the centered, fading "Tap to go to
  thumbnail" overlay, without a separate corner button. Use an in-page
  fullscreen fallback when the browser Fullscreen API is unavailable.
- [x] Request AAC-compatible audio on touch clients while keeping desktop
  Local video streaming unchanged.
- [x] Cover FFmpeg progress parsing and ETA calculation, secure status-endpoint
  access, and Local player/diagnostics status display.
- [x] Add an authorized transcode-cancel endpoint and operation-level
  cancellation. The compact player exposes "Cancel transcode" while tablet
  audio preparation is pending; normal stop and source replacement also request
  cancellation. Cancellation terminates FFmpeg and removes partial output.

Automated Phase 6 verification: the focused frontend, stream, and transcoder
Release test run for the cancellation follow-up reported 90 passed and 0 failed.
FFmpeg progress output was also checked against a short generated media stream.
The integrated browser identified as Chrome on Windows. Edge and mobile
browsers were not exercised, and no codec compatibility beyond the tested H.264
fixture is claimed. The sample AC-3 5.1-to-stereo-AAC conversion succeeded in a
manual 10-second FFmpeg check.
Tablet audio, fullscreen exit, the in-page fullscreen fallback, and Escape-key
behavior, and transcode cancellation are not device-verified.

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
9. At desktop docked widths, Local preview stays inside the player dock at no
   more than 320x180 CSS pixels; clicking its image toggles fullscreen and
   returns to the compact preview without intercepting native controls.

## External references

- [MDN: `<video>` element](https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/video)
- [MDN: `canPlayType()`](https://developer.mozilla.org/en-US/docs/Web/API/HTMLMediaElement/canPlayType)
- [MDN: Media Capabilities API](https://developer.mozilla.org/en-US/docs/Web/API/Media_Capabilities_API)
- [MDN: `HTMLMediaElement.play()` and autoplay behavior](https://developer.mozilla.org/en-US/docs/Web/API/HTMLMediaElement/play)
- [MDN: HTTP range requests](https://developer.mozilla.org/en-US/docs/Web/HTTP/Range_requests)
- [MDN: 206 Partial Content](https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/206) and
  [416 Range Not Satisfiable](https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/416)
