# Web Player UI Improvement Plan

> Archived on 2026-10-18 after all eight phases were implemented and verified.
> Browser-automation limitations remain recorded below for historical context.

**Status:** Phases 1–8 implemented and verified. Evidence and known
browser-automation limitations are recorded below.

## Goal and scope

Improve the persistent web audio player shown in the screenshot, especially its
small/narrow layout, while keeping Radio, Podcast, and Music playback reliable.
Use the current shared Radio/Podcast bar as the primary target and bring the
separate Music bar into visual alignment where practical. This plan does not
redesign the dedicated video/full-screen player or change the server playback
protocol. The separate browser-local video feature is tracked in
[VIDEO_LOCAL_PLAYBACK_PLAN.md](../plans/VIDEO_LOCAL_PLAYBACK_PLAN.md).

## Checked baseline

- [x] Reviewed the supplied screenshot. The long now-playing title is squeezed
  into a very narrow column and wraps word-by-word; transport buttons compete
  for the same horizontal space; the player uses substantial vertical space;
  and a “Stations” control appears while the metadata looks podcast-related.
- [x] Reviewed the player markup in [index.html](../../WebAssets/index.html). The
  shared `#radio-player-bar` contains metadata, podcast Prev/Next and episode
  count, Play/Pause, Stop, a combined volume/boost slider, and the radio globe
  section. `#music-player-bar` is a separate surface with its own seek row,
  options, effects, and queue-peek card.
- [x] Found a malformed opening tag at the radio globe section in
  [index.html](../../WebAssets/index.html): `<div id="radio-bar-geo"` is missing
  its closing `>`. Repair and validate this markup before relying on mode CSS
  or computed layout.
- [x] Reviewed the relevant layout and mode rules in
  [styles.css](../../WebAssets/styles.css). `.player-top` starts as a two-column
  grid (`minmax(0, 1fr) auto`); narrow-view rules stack it, while docked-player
  rules apply separate widths and title wrapping. The docked radio title has
  no line limit, so the actual viewport, dock state, and CSS cascade should be
  reproduced before choosing the final breakpoint fix.
- [x] Reviewed playback state in [app-radio.js](../../WebAssets/app-radio.js),
  [app-local.js](../../WebAssets/app-local.js), and
  [app-podcasts.js](../../WebAssets/app-podcasts.js). Podcast navigation already
  tracks episode position, loading/error status, and continuation across feed
  pages. Stop/dismiss and play/pause have distinct existing semantics.
- [x] Checked existing frontend regression coverage in
  [WebAssetRegressionTests.cs](../../RemotePlay.Tests/WebAssetRegressionTests.cs).
  It includes checks that movie cards remain clickable and launch playback.

### Screenshot and code observations to verify during implementation

1. The screenshot looks like Podcast playback (`Podcast` metadata and an
   episode count), but also shows “Stations.” Current CSS intends to hide
   `#radio-bar-geo` under `body.podcast-player-mode`, while the opening globe
   wrapper is malformed in the HTML. Fix the markup, inspect the parsed DOM,
   then verify that the mode class and hide rule work in the actual browser.
2. The shared Radio/Podcast bar displays elapsed time but has no visible
   seek/progress range. Music has a separate seek control. Add seeking only
   when the active source exposes reliable duration and seek behavior.
3. The shared bar labels its combined `0–1.3` range as volume even though the
   upper range also controls audio boost. Make that distinction clearer without
   changing its current volume/boost mapping or reset behavior in the first
   visual pass.
4. The music queue-peek card is populated by existing queue logic. Keep queue
   navigation and display behavior intact; if an empty “Up Next” shell is
   visually removed, hide only the empty presentation, not the queue card or
   its data/actions.
5. The configured 320–768 px browser checks also show horizontal overflow in
   the existing top mode-tab row (for example, the Live TV tab extends beyond
   a 533 px viewport). This is independent of the player; Phase 1 will ensure
   the player does not add to it and will track the navigation overflow
   separately rather than changing unrelated tabs.

## Recommended design

- **Make content the first full-width row.** Give the now-playing title enough
  width before laying out transport controls. Clamp long titles to at most two
  lines, ellipsize rather than splitting into a narrow word column, and keep
  the full title available to assistive technology and on hover/focus.
- **Use a clear control hierarchy.** Put Previous, a visually primary
  Play/Pause, and Next together. Keep Stop/Dismiss distinct and label its
  close-player effect clearly. Show episode position and page-loading/error
  status near the podcast navigation controls.
- **Use a compact secondary-controls area.** Keep volume immediately
  accessible, but move infrequent effects/settings into a clearly labelled
  expandable “Audio” section. Show Radio-only globe/station controls only in
  Radio mode.
- **Add source-aware progress.** Show a timeline when a podcast has finite
  duration, but enable seeking only when the source exposes reliable seek
  behavior. Show a live indicator and elapsed time for live Radio rather than
  a misleading or non-functional seek bar. Handle unknown duration without
  reserving a broken range control.
- **Use artwork as a supporting cue.** Show available podcast artwork or a
  stable placeholder beside the title; do not block playback on artwork or add
  a new lookup dependency.
- **Align the Music and Radio/Podcast surfaces.** Reuse spacing, type scale,
  button states, and responsive rules, but keep their existing mode-specific
  controls and logic separate.

## Work plan

### Phase 1 — Reproduce and fix the narrow layout (highest priority)

- [x] Capture the current player at 320, 375, 533, 768, 1024, and 1440 CSS-pixel
  widths. Record device scale factor, zoom, dock mode, and playback mode so the
  screenshot can be reproduced.
- [x] Repair the malformed `#radio-bar-geo` opening tag and validate the
  resulting player DOM before doing visual tuning.
- [x] Inspect the computed layout in Podcast playback and a representative
  Radio globe state. Confirm Music remains separate and outside the changed
  Radio/Podcast selectors.
- [x] Restructure the player header so metadata has a full-width row and
  transport controls occupy a separate responsive row; do not let the
  intrinsic width of the buttons collapse the title column.
- [x] Set explicit `min-width: 0`, wrapping, and overflow rules for the grid
  children. Clamp the title to two lines and reserve ellipsis for long text.
- [x] At narrow widths, allow transport controls to wrap or use a compact,
  deliberate grid. Keep Stop/Dismiss visible and separate; do not rely on
  browser overflow or horizontal scrolling to reach controls.
- [x] Ensure the docked panel fits the viewport height or scrolls internally
  without covering the main controls.

**Phase 1 acceptance**

- At 533 px and below, the title never collapses to one narrow word per line;
  it remains readable in no more than two lines.
- The radio globe/station wrapper parses as the intended DOM element and is
  hidden in Podcast mode while remaining available in Radio mode.
- No title, metadata, or button overlap; the player adds no horizontal
  overflow. The pre-existing mode-tab overflow is recorded separately and is
  not worsened.
- Play/Pause and Stop/Dismiss remain easy to identify and operate, and the
  title clamp bounds player height instead of allowing unbounded growth.

**Phase 1 verification (2026-10-16)**

- The browser used the real frontend HTML, stylesheet, and concatenated
  JavaScript with local mock API responses. The viewport matrix was configured
  at 320, 375, 533, 768, 1024, and 1440 CSS pixels; device scale factor and
  visual viewport scale were both 1. Podcast playback used the docked player
  on desktop widths and the normal document flow at narrow widths.
- At every tested width, the title occupied at most two lines (36 px maximum),
  the player had no horizontal scrollable content, and no visible player
  descendant extended outside its container. Stop remained visible at narrow
  widths as a right-aligned 80-by-44 px control.
- At 1024 and 1440 px, the fixed player fit within the 900 px viewport. At
  widths through 768 px, it remained in normal page flow rather than covering
  content.
- The existing mode-tab row still caused page-level horizontal overflow at
  narrow widths (for example, document `scrollWidth` was 853 px at a 533 px
  viewport). The player itself did not add overflow; the unrelated tabs were
  left unchanged.
- The repaired globe wrapper contains the Stations button. A representative
  geolocated Radio station made the wrapper visible; the Podcast mode rule hid
  it even when its inline display style was `flex`.
- Browser interaction checks confirmed Play/Pause toggling, Next/Previous
  episode navigation, and Stop dismissing the player. The mock Music
  catalogue contained no tracks, so active Music playback was unavailable for
  a browser smoke test; no Music markup or playback code was changed, and the
  new layout rules are scoped to the Radio/Podcast player.
- Three targeted movie-card regression tests passed. Deno syntax checks,
  editor diagnostics for the changed HTML/CSS, and `git diff --check` passed.

### Phase 2 — Improve now-playing information and mode awareness

- [x] Establish consistent hierarchy: artwork/placeholder, title, secondary
  artist/show/source text, and mode-specific status. Hide empty metadata rows.
- [x] Pass available podcast artwork into the shared player; preserve a
  placeholder when artwork is absent or unavailable.
- [x] Verify `podcast-player-mode` state from both local and server podcast
  playback. Hide the Radio globe/“Stations” section and Radio-only favorite or
  health controls for Podcasts; retain them in Radio mode.
- [x] Keep the podcast episode count, queued-page loading state, and retry
  message visible without letting them force the title or buttons to shrink.
- [x] Apply the same spacing and typography tokens to
  `#music-player-bar` without merging its DOM or playback state with the
  Radio/Podcast bar.

### Phase 2 verification (2026-10-16)

- The real frontend HTML, stylesheet, and concatenated JavaScript were served
  with local mock API responses. The temporary mock server was removed after
  the browser checks.
- Local and server Podcast playback both showed the episode title, show name,
  Podcast status, artwork, and episode count. A valid image loaded; missing
  artwork and a deliberately failed image request both retained the Podcast
  placeholder.
- In both Podcast modes, the globe/Stations section, Radio favorite, health,
  history, quality, and station-detail rows were hidden. A Radio station
  remained in Radio mode with its artwork, favorite/health controls, quality
  and station metadata, and geolocated globe visible. Switching from Podcast
  state to Radio metadata also retained Radio presentation.
- Server Podcast status updated to show elapsed progress. At the end of a
  loaded page, the loading message was visible with `aria-busy=true`; the
  episode title, count, and transport controls remained visible. A simulated
  page-fetch failure displayed the retry message and kept Next available.
- Radio and Music computed title size, detail size, and info-row spacing
  matched (14.4 px, 11.52 px, and 1.28 px respectively). Their player bars
  remain separate; the Music queue-peek container and queue-next control
  remain present, and no Music playback or queue logic was changed.
- Ten focused web-asset regression tests passed. Deno checks, editor
  diagnostics, and `git diff --check` passed. The mock 404 artwork and 503
  page response were intentional fallback/retry scenarios.

### Phase 3 — Clarify transport, progress, and audio settings

- [x] Standardize Play/Pause as the primary action, with Previous/Next adjacent
  and Stop/Dismiss visually separate. Preserve the current action semantics.
- [x] Keep Prev/Next disabled states and accessible names synchronized with
  podcast queue position, page availability, and page-loading state.
- [x] Add an elapsed/total timeline for finite, seekable podcast playback.
  Confirm local and server playback duration/seek support first; if server
  seeking is unavailable or unreliable, do not present a functional-looking
  scrubber for that mode.
- [x] Keep Music’s existing seek behavior intact. For live Radio, present
  “Live” plus elapsed time and omit seeking unless the stream path supports it.
- [x] Make volume’s standard range and the advanced boost range understandable.
  Prefer a primary volume control with boost under Audio settings, while
  preserving existing volume/boost mapping, endpoints, and reset-to-80% action.
- [x] Keep EQ, Room/Reverb, and other advanced controls available where they
  are supported; collapse them rather than deleting or silently resetting
  active settings.

### Phase 3 verification (2026-10-16)

- Browser checks used the current frontend sources with local mock API
  responses at desktop width and a 378 px coarse-pointer viewport. Play/Pause
  stayed primary, Previous/Next remained adjacent, Stop occupied a separate
  row on mobile, and neither player overflowed horizontally.
- Podcast Previous/Next states were checked at the first and last loaded
  episodes, with another page available, while that page was loading, after a
  simulated page-load failure, when no next page remained, and during a
  playback transition. Disabled state, accessible name, busy state, and retry
  text matched each case.
- Local and server Podcast timelines appeared with finite duration and were
  enabled only when seek capability was confirmed. A seek commit succeeded in
  the mock browser check. Radio showed Live plus elapsed time and no scrubber,
  while the existing Music seek controls remained available.
- Volume and boost showed separate values and retained their existing ranges,
  endpoints, and reset mapping (80% volume and 0 dB/1× boost). EQ/Reverb
  controls remain available inside collapsed Audio settings.
- A populated Music queue displayed Next Queued, hid normal Prev/Next, and
  retained the queue-peek card; an empty queue restored normal transport.
  This exposed an optional pending-notice early-return bug, which was fixed
  and covered by a regression test.
- The focused `WebAssetRegressionTests` passed 23/23. Deno checks, editor
  diagnostics for changed files, and `git diff --check` passed. The isolated
  application build completed with zero warnings and errors.

### Phase 4 — Accessibility and visual polish

- [x] Give each player a semantic labelled region and ensure every icon-only
  control has a meaningful accessible name and state.
- [x] Provide visible keyboard focus, clear disabled/loading states, sufficient
  contrast, and touch targets of at least 44 CSS pixels where practical.
- [x] Ensure status/error text is announced without repeatedly interrupting
  playback updates. Do not use color alone to indicate playing, paused, live,
  or boost state.
- [x] Respect reduced-motion preferences for artwork/waveform animations and
  avoid moving or marquee text by default.
- [x] Check light/dark themes and long localized titles/labels.

### Phase 4 verification

- DOM and regression checks confirmed labelled player regions, accessible
  control names/states, visible focus styles, disabled/loading semantics, and
  polite status/error announcements.
- Dark/light title contrast measured 17.61:1 and at least 10.51:1 respectively.
  Reduced-motion behavior was checked, and the Radio title remains static.
- Coarse-pointer emulation applied the 44px CSS minimum and kept the Music
  queue-removal control visible. Long localized titles remained at most two
  lines in the narrow viewport checks.

### Phase 5 — Regression verification

- [x] Test Radio, local Podcast, server Podcast, local Music, and server Music
  in playing, paused, loading, stopped, and error states.
- [x] Verify podcast Previous/Next behavior at the beginning, within a loaded
  page, while another page loads, after a page-load failure, and across a page
  boundary.
- [x] Verify Music queue display, add/remove/next behavior, and queue position.
  If hiding an empty “Up Next” shell, verify that populated queue items and
  their controls remain available.
- [x] Verify volume, boost, reset, and any new progress controls with pointer,
  keyboard-style input/change events, and touch-end/coarse-pointer behavior.
- [x] Run the existing movie-card click/playback regression checks, even though
  cards are out of scope, to catch shared CSS or event regressions.
- [x] Run the relevant `WebAssetRegressionTests`, JavaScript syntax checks, and
  `git diff --check`; compare any known baseline test failures rather than
  attributing unrelated failures to the player work.
- [x] Repeat the viewport matrix in a real browser with long titles, missing
  artwork, short/unknown duration, and a populated Music queue.

### Phase 5 verification

- Radio, local/server Podcast, and local/server Music status and error paths
  were rechecked. Actual local Music and Podcast media errors produced the
  correct alert title, cleared busy/live status, and left Play unpressed.
- Podcast paging covered the initial and in-page Next actions, deferred
  loading, failed request/retry, successful page-boundary transition, and the
  disabled end-of-queue state.
- Music queue add/next/remove-cursor behavior and the populated queue display
  were verified; the empty queue restores normal transport. Volume, boost,
  reset values, and finite/unknown-duration seeking were also checked.
- Pointer seek behavior was checked in the browser. Keyboard-style `input` /
  `change` and touch-end event paths each produced exactly one seek commit.
  Coarse-pointer emulation confirmed the 44px CSS target rule. The integrated
  browser did not deliver native Arrow-key or physical-touch input, so those
  device-level interactions remain a manual follow-up.
- The player had no horizontal overflow at 320, 375, 533, 768, 1024, and
  1440px. With a long localized title, missing artwork, and a populated queue,
  the title stayed within two lines and the queue remained available. The
  pre-existing mode-tab row still causes document-level overflow (735px at
  320/375px and 851px at 533/768px); the player itself adds none.
- Focused `WebAssetRegressionTests` passed 29/29, including
  `MovieCardsRemainClickableForPlayback` and
  `MovieCardClickLaunchesPlaybackEndpoint`. Deno checks passed for the six
  modified player scripts, editor diagnostics found no errors, and
  `git diff --check` passed.

### Phase 6 — Podcast control polish and local handoff

- [x] Remove duplicate Volume and Boost headings from the shared
  Radio/Podcast controls without changing Music controls or audio mappings.
- [x] Present the episode position as a compact badge alongside, not between,
  the Prev/Play/Next transport group; keep Stop distinct and responsive.
- [x] Handoff a server Podcast to local playback using its episode URL and
  Podcast metadata, preserving the queue and current position rather than
  restarting server live-Radio playback. When duration is not yet known, use
  the server's elapsed-time estimate as the resume position.
- [x] Show a timeline whenever duration is finite; disable pointer seeking
  until the local or server source confirms it is seekable.
- [x] Add regression checks for the player layout, Podcast-specific handoff,
  resume-position handling, and finite-duration progress.

### Phase 6 verification

- Source regression tests, JavaScript syntax checks, editor diagnostics, and
  `git diff --check` passed. The PodcastServer handoff was confirmed to run
  before the generic Radio path and to use the direct episode URL and
  Podcast mode metadata.
- The episode badge occupies a separate secondary transport group. Volume and
  Boost each have a single visible label in the Radio/Podcast player; Music
  markup and queue behavior are unchanged.
- A finite-duration Podcast displays elapsed/total progress even when seeking
  is unsupported; the range stays disabled until seekability is confirmed.
- A local static browser preview at 1440 px and 375 px with coarse-pointer
  emulation confirmed that the primary buttons stay grouped, the episode badge
  and Stop remain distinct, and no player child overflows the card. At 375 px,
  the document and player both fit the viewport.

### Phase 7 — Waveform capture and episode details

- [x] Share the persistent local Web Audio source graph between boost and the
  analyser so the waveform reads the actual audio instead of failing on a
  duplicate `MediaElementSource`.
- [x] Use the same-origin stream proxy for local Podcast playback and forward
  byte ranges, preserving seeking when an episode host supports range
  requests while avoiding cross-origin audio-analysis silence.
- [x] Remove the synthetic server-mode waveform; only show bars when the
  browser has real local audio samples to analyze.
- [x] Move the episode position into the player metadata row and keep Stop
  alongside the transport controls.
- [x] Expose the current episode's complete description in an accessible,
  expandable player disclosure; keep the episode-list preview compact.
- [x] Add regression checks for waveform graph reuse, range forwarding,
  metadata placement, and full description availability.

### Phase 7 verification

- Focused `WebAssetRegressionTests` passed 34/34; Deno JavaScript checks,
  editor diagnostics, and `git diff --check` passed. Local Podcast playback
  uses the existing audio graph and range-aware proxy; synthetic server bars
  are no longer rendered.
- The episode index now appears with the show metadata above the transport
  controls. The full, plain-text episode description is available in the
  player disclosure and updates when navigating the queue.
- A static browser preview at 360 CSS px confirmed that the episode badge
  stays in the metadata row, the transport buttons and Stop remain visible,
  and no player child overflows the card. The existing page-level mode-tab
  overflow remains outside this player change.

### Phase 8 — Audio player visibility across modes

- [x] Hide the shared Radio/Podcast player in Video and other non-audio modes
  without stopping playback or changing the Podcast queue.
- [x] Scope dock layout rules to Radio and Podcasts modes so the player does
  not reserve space in the Video list.
- [x] Add regression coverage for audio-mode classes and player visibility.

### Phase 8 verification

- Focused `WebAssetRegressionTests` passed 35/35; `deno check` and editor
  diagnostics passed.
- A browser style check confirmed the player is visible in Radio/Podcasts,
  hidden in Video, and visible again when returning to Podcasts.

## Invariants and non-goals

- Do not change playback endpoints, queue semantics, or source-selection
  behavior beyond the local Podcast proxy's byte-range forwarding.
- Keep the shared Radio/Podcast player out of non-audio modes without stopping
  active playback or changing the Podcast queue.
- Preserve podcast paging and queue continuation across feed pages.
- Preserve Music queue behavior and its populated queue display. Only the
  empty visual “Up Next” presentation may be hidden.
- Do not modify movie-card click behavior or playback launch behavior. If shared
  CSS is touched, explicitly verify both existing movie-card regression tests.
- Do not redesign the dedicated video/full-screen controls in this plan.
- Do not add a new frontend testing dependency solely for this UI work; use the
  existing test project and focused browser checks unless a separate decision
  approves new tooling.

## Definition of done

- The screenshot’s narrow title/control collision is reproduced, fixed, and
  verified across the listed viewport widths.
- Radio, Podcast, and Music display only controls that make sense for the
  active mode, with no stray Radio-only “Stations” control in podcast playback.
- Existing transport, podcast page continuation, seek where supported, audio
  settings, and Music queue behavior remain correct.
- Keyboard/touch accessibility and the existing movie-card playback
  regressions are verified, and test outcomes (including known baseline
  failures) are recorded before marking implementation phases complete.
