# Web Player UI Improvement Plan

**Status:** Reviewed against the supplied screenshot and current frontend on
2026-10-16. This is a checked plan; implementation has not started.

## Goal and scope

Improve the persistent web audio player shown in the screenshot, especially its
small/narrow layout, while keeping Radio, Podcast, and Music playback reliable.
Use the current shared Radio/Podcast bar as the primary target and bring the
separate Music bar into visual alignment where practical. This plan does not
redesign the dedicated video/full-screen player or change the server playback
protocol.

## Checked baseline

- [x] Reviewed the supplied screenshot. The long now-playing title is squeezed
  into a very narrow column and wraps word-by-word; transport buttons compete
  for the same horizontal space; the player uses substantial vertical space;
  and a “Stations” control appears while the metadata looks podcast-related.
- [x] Reviewed the player markup in [index.html](./WebAssets/index.html). The
  shared `#radio-player-bar` contains metadata, podcast Prev/Next and episode
  count, Play/Pause, Stop, a combined volume/boost slider, and the radio globe
  section. `#music-player-bar` is a separate surface with its own seek row,
  options, effects, and queue-peek card.
- [x] Found a malformed opening tag at the radio globe section in
  [index.html](./WebAssets/index.html): `<div id="radio-bar-geo"` is missing
  its closing `>`. Repair and validate this markup before relying on mode CSS
  or computed layout.
- [x] Reviewed the relevant layout and mode rules in
  [styles.css](./WebAssets/styles.css). `.player-top` starts as a two-column
  grid (`minmax(0, 1fr) auto`); narrow-view rules stack it, while docked-player
  rules apply separate widths and title wrapping. The docked radio title has
  no line limit, so the actual viewport, dock state, and CSS cascade should be
  reproduced before choosing the final breakpoint fix.
- [x] Reviewed playback state in [app-radio.js](./WebAssets/app-radio.js),
  [app-local.js](./WebAssets/app-local.js), and
  [app-podcasts.js](./WebAssets/app-podcasts.js). Podcast navigation already
  tracks episode position, loading/error status, and continuation across feed
  pages. Stop/dismiss and play/pause have distinct existing semantics.
- [x] Checked existing frontend regression coverage in
  [WebAssetRegressionTests.cs](./RemotePlay.Tests/WebAssetRegressionTests.cs).
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
- **Add source-aware progress.** Provide a timeline for seekable podcasts and
  music; show a live indicator and elapsed time for live Radio rather than a
  misleading or non-functional seek bar. Handle unknown duration without
  reserving a broken range control.
- **Use artwork as a supporting cue.** Show available podcast artwork or a
  stable placeholder beside the title; do not block playback on artwork or add
  a new lookup dependency.
- **Align the Music and Radio/Podcast surfaces.** Reuse spacing, type scale,
  button states, and responsive rules, but keep their existing mode-specific
  controls and logic separate.

## Work plan

### Phase 1 — Reproduce and fix the narrow layout (highest priority)

- [ ] Capture the current player at 320, 375, 533, 768, 1024, and 1440 CSS-pixel
  widths. Record device scale factor, zoom, dock mode, and playback mode so the
  screenshot can be reproduced.
- [ ] Repair the malformed `#radio-bar-geo` opening tag and validate the
  resulting player DOM before doing visual tuning.
- [ ] Inspect the computed `.player-top`, `#radio-player-summary`,
  `#radio-bar-info`, title, and transport styles in Radio, Podcast, and Music
  modes. Confirm which media query is active at the screenshot width.
- [ ] Restructure the player header so metadata has a full-width row and
  transport controls occupy a separate responsive row; do not let the
  intrinsic width of the buttons collapse the title column.
- [ ] Set explicit `min-width: 0`, wrapping, and overflow rules for the grid
  children. Clamp the title to two lines and reserve ellipsis for long text.
- [ ] At narrow widths, allow transport controls to wrap or use a compact,
  deliberate grid. Keep Stop/Dismiss visible and separate; do not rely on
  browser overflow or horizontal scrolling to reach controls.
- [ ] Ensure the docked panel fits the viewport height or scrolls internally
  without covering the main controls.

**Phase 1 acceptance**

- At 533 px and below, the title never collapses to one narrow word per line;
  it remains readable in no more than two lines.
- The radio globe/station wrapper parses as the intended DOM element and is
  hidden in Podcast mode while remaining available in Radio mode.
- No title, metadata, or button overlap; no page-level horizontal overflow.
- Play/Pause and Stop/Dismiss remain easy to identify and operate, and the
  panel does not become taller solely because the title is long.

### Phase 2 — Improve now-playing information and mode awareness

- [ ] Establish consistent hierarchy: artwork/placeholder, title, secondary
  artist/show/source text, and mode-specific status. Hide empty metadata rows.
- [ ] Pass available podcast artwork into the shared player; preserve a
  placeholder when artwork is absent or unavailable.
- [ ] Verify `podcast-player-mode` state from both local and server podcast
  playback. Hide the Radio globe/“Stations” section and Radio-only favorite or
  health controls for Podcasts; retain them in Radio mode.
- [ ] Keep the podcast episode count, queued-page loading state, and retry
  message visible without letting them force the title or buttons to shrink.
- [ ] Apply the same spacing and typography tokens to
  `#music-player-bar` without merging its DOM or playback state with the
  Radio/Podcast bar.

### Phase 3 — Clarify transport, progress, and audio settings

- [ ] Standardize Play/Pause as the primary action, with Previous/Next adjacent
  and Stop/Dismiss visually separate. Preserve the current action semantics.
- [ ] Keep Prev/Next disabled states and accessible names synchronized with
  podcast queue position, page availability, and page-loading state.
- [ ] Add an elapsed/total timeline for finite, seekable podcast playback.
  Confirm local and server playback duration/seek support first; if server
  seeking is unavailable or unreliable, do not present a functional-looking
  scrubber for that mode.
- [ ] Keep Music’s existing seek behavior intact. For live Radio, present
  “Live” plus elapsed time and omit seeking unless the stream path supports it.
- [ ] Make volume’s standard range and the advanced boost range understandable.
  Prefer a primary volume control with boost under Audio settings, while
  preserving existing volume/boost mapping, endpoints, and reset-to-80% action.
- [ ] Keep EQ, Room/Reverb, and other advanced controls available where they
  are supported; collapse them rather than deleting or silently resetting
  active settings.

### Phase 4 — Accessibility and visual polish

- [ ] Give each player a semantic labelled region and ensure every icon-only
  control has a meaningful accessible name and state.
- [ ] Provide visible keyboard focus, clear disabled/loading states, sufficient
  contrast, and touch targets of at least 44 CSS pixels where practical.
- [ ] Ensure status/error text is announced without repeatedly interrupting
  playback updates. Do not use color alone to indicate playing, paused, live,
  or boost state.
- [ ] Respect reduced-motion preferences for artwork/waveform animations and
  avoid moving or marquee text by default.
- [ ] Check light/dark themes and long localized titles/labels.

### Phase 5 — Regression verification

- [ ] Test Radio, local Podcast, server Podcast, local Music, and server Music
  in playing, paused, loading, stopped, and error states.
- [ ] Verify podcast Previous/Next behavior at the beginning, within a loaded
  page, while another page loads, after a page-load failure, and across a page
  boundary.
- [ ] Verify Music queue display, add/remove/next behavior, and queue position.
  If hiding an empty “Up Next” shell, verify that populated queue items and
  their controls remain available.
- [ ] Verify volume, boost, reset, and any new progress controls with pointer,
  keyboard, and touch input.
- [ ] Run the existing movie-card click/playback regression checks, even though
  cards are out of scope, to catch shared CSS or event regressions.
- [ ] Run the relevant `WebAssetRegressionTests`, JavaScript syntax checks, and
  `git diff --check`; compare any known baseline test failures rather than
  attributing unrelated failures to the player work.
- [ ] Repeat the viewport matrix in a real browser with long titles, missing
  artwork, short/unknown duration, and a populated Music queue.

## Invariants and non-goals

- Do not change playback endpoints, queue semantics, or source-selection
  behavior as part of a visual-only layout change.
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
