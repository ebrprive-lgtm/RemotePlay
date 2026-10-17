let currentDir = null,
  currentData = null,
  playingPath = null;
let browseHistory = [];
let pollInterval = null,
  seekDragging = false,
  searchTimer = null,
  lastQueue = [];
let lastVolumeBeforeMute = 0.7,
  lastBoostBeforeMute = 0.3;

// ── Global sleep timer ───────────────────────────────────────────────
// One timer shared across all player domains (video, music, radio).
// The tick detects which domain is active for volume-fade and stop.
const SLEEP_FADE_SECS = 60;
let _sleepTimerEnd = 0;
let _sleepTimerInterval = null;
let _sleepFadeActive = false;
let _sleepPreFadeVolume = null;

function sleepSet(minutes) {
  _sleepTimerEnd = Date.now() + minutes * 60 * 1000;
  _sleepFadeActive = false;
  _sleepPreFadeVolume = null;
  clearInterval(_sleepTimerInterval);
  _sleepTimerInterval = setInterval(_sleepTimerTick, 1000);
  _sleepTimerTick();
  _sleepUpdateUI(false);
}

function sleepCancel() {
  if (_sleepFadeActive && _sleepPreFadeVolume !== null) {
    _sleepSetActiveVolume(_sleepPreFadeVolume);
  }
  _sleepTimerEnd = 0;
  _sleepFadeActive = false;
  _sleepPreFadeVolume = null;
  clearInterval(_sleepTimerInterval);
  _sleepTimerInterval = null;
  _sleepUpdateUI(true);
}

// Toggle countdown vs preset buttons in the sleep-timer popup.
function _sleepUpdateUI(showPresets) {
  const btns = document.getElementById('sleep-popup-btns');
  const cd   = document.getElementById('sleep-popup-countdown');
  if (btns) btns.style.display = showPresets ? '' : 'none';
  if (cd)   cd.style.display   = showPresets ? 'none' : '';
  // Reflect active state on the sleep icon button itself
  const iconBtn = document.getElementById('sleep-icon-btn');
  if (iconBtn) iconBtn.classList.toggle('active', !showPresets);
  const badge = document.getElementById('sleep-icon-badge');
  if (badge) badge.style.display = showPresets ? 'none' : '';
}

function _sleepTimerTick() {
  const rem = Math.max(0, _sleepTimerEnd - Date.now());
  const m = Math.floor(rem / 60000);
  const s = Math.floor((rem % 60000) / 1000);
  const txt = 'Stops in ' + m + ':' + (s < 10 ? '0' : '') + s;
  const el = document.getElementById('sleep-popup-remain');
  if (el) el.textContent = txt;
  // Also update the icon button badge so the countdown is visible without opening popup
  const badge = document.getElementById('sleep-icon-badge');
  if (badge) badge.textContent = m + ':' + (s < 10 ? '0' : '') + s;

  const fadeSecs = SLEEP_FADE_SECS * 1000;
  if (!_sleepFadeActive && rem > 0 && rem <= fadeSecs) {
    _sleepFadeActive = true;
    _sleepPreFadeVolume = _sleepGetActiveVolume();
  }
  if (_sleepFadeActive && rem > 0) {
    const t = Math.min(1, 1 - rem / fadeSecs);
    _sleepSetActiveVolume(Math.max(0, (_sleepPreFadeVolume || 0.8) * (1 - t)));
  }

  if (rem <= 0) {
    sleepCancel();
    _sleepStopActive();
  }
}

function _sleepGetActiveVolume() {
  if (window._radioIsPlaying) {
    const s = document.getElementById('radio-bar-volume');
    return s ? Math.min(1.0, Math.max(0, parseFloat(s.value) || 0)) : 0.8;
  }
  const mb = document.getElementById('music-player-bar');
  if (mb && mb.style.display !== 'none') {
    const s = document.getElementById('music-bar-volume');
    return s ? Math.min(1.0, Math.max(0, parseFloat(s.value) || 0)) : 0.8;
  }
  const s = document.getElementById('video-combined-vol');
  return s ? Math.min(1.0, parseFloat(s.value)) : 1.0;
}

function _sleepSetActiveVolume(vol) {
  if (window._radioIsPlaying) {
    const s = document.getElementById('radio-bar-volume');
    if (s) { s.value = vol; if (typeof radioVolume === 'function') radioVolume(vol); }
    return;
  }
  const mb = document.getElementById('music-player-bar');
  if (mb && mb.style.display !== 'none') {
    const s = document.getElementById('music-bar-volume');
    if (s) { s.value = vol; if (typeof musicVolume === 'function') musicVolume(vol); }
    return;
  }
  const s = document.getElementById('video-combined-vol');
  if (s) { s.value = vol; if (typeof setVideoCombinedSlider === 'function') setVideoCombinedSlider(vol); }
}

function _sleepStopActive() {
  if (window._radioIsPlaying) {
    if (typeof radioDismiss === 'function') radioDismiss();
    return;
  }
  const mb = document.getElementById('music-player-bar');
  if (mb && mb.style.display !== 'none') {
    if (typeof musicStop === 'function') musicStop();
    return;
  }
  if (typeof stop === 'function') stop();
}

// Returns a debounced version of fn that delays invocation by `wait` ms.
// Only the most-recent call within the window fires; use for high-frequency
// slider events to avoid flooding the API with a request on every tick.
function debounce(fn, wait) {
  let timer = null;
  return function (...args) {
    clearTimeout(timer);
    timer = setTimeout(() => { timer = null; fn.apply(this, args); }, wait);
  };
}

// Decode a server-side Base64-encoded file path back to the raw FS path string.
function _decodePath(encoded) {
  try {
    return decodeURIComponent(escape(atob(encoded)));
  } catch {
    return null;
  }
}

function _encodePath(rawPath) {
  if (!rawPath) return null;
  try {
    return btoa(unescape(encodeURIComponent(rawPath)));
  } catch {
    return null;
  }
}

// Build a URL path segment from breadcrumb data (array of {name, dir} objects)
function _buildFriendlyPathFromBreadcrumbs(breadcrumbs) {
  if (!breadcrumbs || !breadcrumbs.length) return '';

  // Build a path using ALL breadcrumbs (hierarchy), but extract only the folder name from each
  const parts = breadcrumbs.map(bc => {
    let name = (bc.name || '').trim();
    if (!name) return null;

    // Extract only the last folder name if it contains a path separator
    const sep = name.includes('\\') ? '\\' : '/';
    if (name.includes(sep)) {
      const pathParts = name.split(sep).filter(p => p.trim());
      name = pathParts[pathParts.length - 1] || name;
    }

    return encodeURIComponent(name);
  }).filter(p => p);

  return parts.join('/');
}

// Convert folder path to base64url for URL encoding (used as URL fragment after friendly name)
function _folderPathToBase64Url(folderPath) {
  if (!folderPath) return null;
  try {
    const encoded = btoa(unescape(encodeURIComponent(folderPath)));
    return encoded.replace(/\+/g, '-').replace(/\//g, '_').replace(/=/g, '');
  } catch {
    return null;
  }
}

// Convert base64url back to folder path
function _base64UrlToFolderPath(encoded) {
  if (!encoded) return null;
  try {
    const normalized = encoded.replace(/-/g, '+').replace(/_/g, '/');
    const padded = normalized + '='.repeat((4 - (normalized.length % 4)) % 4);
    return decodeURIComponent(escape(atob(padded)));
  } catch {
    return null;
  }
}

// Extract last N folder names from a filesystem path for URL-friendly display
function _extractPathComponents(fsPath, depth = 3) {
  if (!fsPath) return [];
  const sep = fsPath.includes('\\') ? '\\' : '/';
  const parts = fsPath.split(sep).filter(p => p.trim());
  return parts.slice(-depth).map(p => encodeURIComponent(p));
}

function _encodeRouteFolder(folderPath) {
  if (!folderPath) return null;
  // Extract only the last folder name (basename) for a clean URL
  const sep = folderPath.includes('\\') ? '\\' : '/';
  const parts = folderPath.split(sep).filter(p => p.trim());
  const lastFolder = parts[parts.length - 1];
  return lastFolder ? encodeURIComponent(lastFolder) : null;
}

// Reconstruct a filesystem path from breadcrumb data; requires the full encoded path backup
function _decodeRouteFolder(friendlyPath, breadcrumbs) {
  if (!breadcrumbs || !breadcrumbs.length) return null;
  // Return the .dir of the last breadcrumb (the target folder)
  const last = breadcrumbs[breadcrumbs.length - 1];
  return last ? last.dir : null;
}

function _buildUiRoute(mode, folderPath = null) {
  const routeMode = mode === 'music' || mode === 'podcasts' || mode === 'radio' || mode === 'live-tv' ? mode : 'video';
  let path = routeMode === 'live-tv' ? '/LiveTV' : '/' + routeMode;
  if (folderPath && (routeMode === 'video' || routeMode === 'music')) {
    const token = _encodeRouteFolder(folderPath);
    if (token) path += '/' + token;
  }
  return path;
}

function _syncUiRoute(mode, folderPath = null) {
  if (!window.history || typeof window.history.replaceState !== 'function') return;
  const path = _buildUiRoute(mode, folderPath);
  if (window.location.pathname === path) return;
  window.history.replaceState(window.history.state, '', path);
}

function _parseInitialUiRoute() {
  const pathname = window.location.pathname || '/';
  console.log('[ROUTE] _parseInitialUiRoute: pathname =', pathname);
  const parts = pathname.split('/').filter(Boolean);
  console.log('[ROUTE] parsed parts:', parts);

  if (!parts.length || parts[0].toLowerCase() === 'index.html') {
    console.log('[ROUTE] No explicit route; defaulting to video');
    return { hasExplicitRoute: false, mode: 'video', folderPath: null };
  }

  const rawMode = parts[0].toLowerCase();
  const mode = rawMode === 'livetv' ? 'live-tv' : rawMode;
  if (mode !== 'video' && mode !== 'music' && mode !== 'podcasts' && mode !== 'radio' && mode !== 'live-tv') {
    console.log('[ROUTE] Unknown mode:', mode, '- defaulting to video');
    return { hasExplicitRoute: false, mode: 'video', folderPath: null };
  }

  console.log('[ROUTE] Explicit route detected: mode =', mode);

  // Extract folder path from URL if present
  // URL format: /mode/friendly-name/base64url-encoded-path
  // or just: /mode/base64url-encoded-path
  let folderPath = null;
  let routeToken = null;
  if (parts.length > 1) {
    const lastPart = parts[parts.length - 1];
    if (mode === 'live-tv') {
      try {
        routeToken = decodeURIComponent(lastPart);
      } catch {
        routeToken = lastPart;
      }
    } else if (/^[A-Za-z0-9_-]+$/.test(lastPart)) {
      folderPath = _base64UrlToFolderPath(lastPart);
      if (folderPath) {
        console.log('[ROUTE] Decoded folderPath from URL:', folderPath);
      }
    }
  }

  return { hasExplicitRoute: true, mode, folderPath, routeToken };
}

// Return the parent directory of a decoded FS path (works for both / and \ separators).
function _parentDir(fsPath) {
  if (!fsPath) return null;
  const sep = fsPath.includes('\\') ? '\\' : '/';
  const idx = fsPath.lastIndexOf(sep);
  return idx > 0 ? fsPath.substring(0, idx) : null;
}

// Derive the Base64-encoded parent folder of a Base64-encoded file path.
// The browse APIs (browseMusic, browse) expect encoded paths.
function _encodedParentDir(encodedFilePath) {
  const fsPath = _decodePath(encodedFilePath);
  const folder = _parentDir(fsPath);
  if (!folder) return null;
  return _encodePath(folder);
}

  seekHoldInterval = null,
  seekHoldTriggered = false,
  suppressNextSeekTap = false;
let currentPlaybackSpeed = 1;
let statusFailures = 0;
let cardHoldTimer = null,
  cardHoldOpened = false;
let zoomDragging = false;
let sharpnessDragging = false;
let brightnessPendingUntil = 0;
let saturationPendingUntil = 0;
let sharpnessPendingUntil = 0;
let zoomPendingUntil = 0;
let pendingSearchAbort = null;
let thumbnailObserver = null;
let _lastHandledWebUiNavigateId = 0;

async function _applyPendingWebUiNavigation(status) {
  const navId = Number(status?.webUiNavigateId ?? status?.WebUiNavigateId ?? 0);
  const folderPath = status?.webUiNavigateFolderPath ?? status?.WebUiNavigateFolderPath ?? '';
  if (!navId || !folderPath || navId === _lastHandledWebUiNavigateId)
    return;

  _lastHandledWebUiNavigateId = navId;

  try {
    if (typeof switchMode === 'function' && currentMode !== 'video')
      switchMode('video', true);

    if (typeof browse === 'function' && typeof _encodePath === 'function') {
      const encodedFolder = _encodePath(folderPath);
      await browse(encodedFolder, 0, false, false);
    }
  } catch (e) {
    console.warn('[WEBUI-NAV] failed to apply navigation command', e);
  }
}

function _viewModeStorageBaseKey(section) {
  return section === 'live-tv'
    ? 'remotePlayViewLiveTv'
    : 'remotePlayView' + section.charAt(0).toUpperCase() + section.slice(1);
}

function _normalizeViewMode(value) {
  return value === 'list' ? 'list' : 'grid';
}

function _sanitizeViewScopeName(name) {
  const clean = String(name || '')
    .replace(/\s+/g, ' ')
    .replace(/[<>:"/\\|?*]/g, '')
    .trim();
  return clean || 'Root';
}

function _buildViewScopeKey(level, name) {
  const lvl = Math.max(1, Number(level) || 1);
  return String(lvl) + _sanitizeViewScopeName(name);
}

function _readScopedViewMode(section, scopeKey) {
  const baseKey = _viewModeStorageBaseKey(section);
  if (scopeKey) {
    const scoped = localStorage.getItem(baseKey + ':' + scopeKey);
    if (scoped === 'grid' || scoped === 'list') return scoped;
  }
  const legacy = localStorage.getItem(baseKey);
  return _normalizeViewMode(legacy);
}

const _viewModeScope = {
  video: _buildViewScopeKey(1, 'Video Root'),
  music: _buildViewScopeKey(1, 'Music Root'),
  podcasts: _buildViewScopeKey(1, 'Podcasts'),
  radio: _buildViewScopeKey(1, 'Radio Root'),
  'live-tv': _buildViewScopeKey(1, 'Live TV Root'),
};

// View mode: 'grid' or 'list' per section, persisted in localStorage
const _viewMode = {
  video: _readScopedViewMode('video', _viewModeScope.video),
  music: _readScopedViewMode('music', _viewModeScope.music),
  podcasts: _readScopedViewMode('podcasts', _viewModeScope.podcasts),
  radio: _readScopedViewMode('radio', _viewModeScope.radio),
  'live-tv': _readScopedViewMode('live-tv', _viewModeScope['live-tv']),
};

function _setViewModeScope(section, level, name) {
  const scopeKey = _buildViewScopeKey(level, name);
  if (_viewModeScope[section] === scopeKey) return;
  _viewModeScope[section] = scopeKey;
  _viewMode[section] = _readScopedViewMode(section, scopeKey);
}

function _setViewModeScopeFromSegments(section, segments) {
  const list = Array.isArray(segments) ? segments.filter((s) => String(s || '').trim().length > 0) : [];
  const level = list.length || 1;
  const name = list.length ? list[list.length - 1] : 'Root';
  _setViewModeScope(section, level, name);
}

function _setViewMode(section, mode) {
  const normalized = _normalizeViewMode(mode);
  _viewMode[section] = normalized;
  const storageKey = _viewModeStorageBaseKey(section);
  const scopeKey = _viewModeScope[section];
  if (scopeKey) localStorage.setItem(storageKey + ':' + scopeKey, normalized);
  localStorage.setItem(storageKey, normalized);
  _applyViewToggleBtn(section);
  if (section === 'video' && currentData) {
    if (typeof _applyVcbViewBtn === 'function') _applyVcbViewBtn();
    render(currentData, Boolean(currentData.query));
  } else if (section === 'music' && currentMusicData) {
    if (typeof _syncMusicCommandBar === 'function') _syncMusicCommandBar();
    renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
  } else if (section === 'radio') {
    if (typeof _syncRadioCommandBar === 'function') _syncRadioCommandBar();
    renderRadioCards(_radioStations, _radioStations.length === _radioPageSize * (_radioPage + 1));
  } else if (section === 'podcasts') {
    if (typeof _renderPodcastsCurrentView === 'function') _renderPodcastsCurrentView();
  } else if (section === 'live-tv') {
    if (typeof _syncLiveTvCommandBar === 'function') _syncLiveTvCommandBar();
    if (typeof renderLiveTvCards === 'function') renderLiveTvCards();
  }
}
function _applyViewToggleBtn(section) {
  const btn = document.getElementById('view-toggle-' + section);
  if (!btn) return;
  const isGrid = _viewMode[section] === 'grid';
  btn.title = isGrid ? 'Switch to list view' : 'Switch to grid view';
  btn.innerHTML = isGrid ? '&#9776;' : '&#9632;&#9632;';
}
const queuedVideos = new Set();
const playedVideos = new Set(loadPlayedVideos());
const favoriteVideos = new Set();
const installHint = document.getElementById('install-hint');
const phoneLayoutQuery = window.matchMedia('(max-width:760px) and (pointer:coarse)');
let isPhoneRemoteOnly = phoneLayoutQuery.matches;
let gestureStart = null;
applySavedTheme();
if (
  !localStorage.getItem('remotePlayInstallHintDismissed') &&
  window.matchMedia('(display-mode: browser)').matches
)
  installHint.style.display = 'flex';

function applySavedTheme() {
  setTheme(localStorage.getItem('remotePlayTheme') || 'default', false);
}

// ── Play-local toggle ──────────────────────────────────────────────────
function isPlayLocal() {
  return localStorage.getItem('remotePlayLocal') === 'true';
}
function setPlayLocal(on) {
  localStorage.setItem('remotePlayLocal', on ? 'true' : 'false');
  const btn = document.getElementById('play-local-toggle');
  if (btn) {
    btn.classList.toggle('active', on);
    btn.title = on ? 'Playing on this device — click to switch to server' : 'Playing on server — click to play on this device';
  }
  // Toggle local-play-mode class on body to hide server-only audio effects
  document.body.classList.toggle('local-play-mode', on);
}
let _togglePlayLocalLast = 0;
function togglePlayLocal() {
  // Debounce: on touch devices a single tap fires touchend + synthetic click;
  // without this guard the function runs twice and the active state flips back.
  const now = Date.now();
  if (now - _togglePlayLocalLast < 400) return;
  _togglePlayLocalLast = now;
  const turningOn = !isPlayLocal();
  setPlayLocal(turningOn);
  if (turningOn) {
    // If radio is currently playing on the server, hand it off to local immediately
    if (typeof _radioIsPlaying !== 'undefined' && _radioIsPlaying && typeof _radioCurrentUrl !== 'undefined' && _radioCurrentUrl) {
      const handoffName    = typeof _radioCurrentName !== 'undefined' ? _radioCurrentName : '';
      const handoffCountry = (typeof _radioCurrentCountry !== 'undefined' ? _radioCurrentCountry : '') || '';
      const handoffTag     = (typeof _radioCurrentTag !== 'undefined' ? _radioCurrentTag : '') || '';
      const handoffStation = (typeof _radioCurrentStation !== 'undefined' ? _radioCurrentStation : null) || null;
      const proxyUrl = '/api/radio/stream-proxy?url=' + encodeURIComponent(_radioCurrentUrl);
      // Update UI state immediately (synchronous, in user-gesture scope)
      _radioIsPlaying = true;
      if (typeof updateRadioBar !== 'undefined')
        updateRadioBar(handoffName, handoffCountry, handoffTag, true, handoffStation);
      // Start local audio NOW — any fetch() before this would consume the user-gesture token
      // and Chromium would silently block audio output.
      localPlay(proxyUrl, handoffName || 'Radio', 'Radio');
      if (typeof startWaveform === 'function') startWaveform();
      // Notify server and log AFTER localPlay() — deferred so we never race the gesture
      setTimeout(() => {
        fetch('/api/local-playing', { method: 'POST', body: 'true' }).catch(() => {});
        fetch('/api/radio/stop', { method: 'POST' }).catch(() => {});
        fetch('/api/log', { method: 'POST', body: 'Switched playback mode to Local' }).catch(() => {});
      }, 0);
    } else if (typeof musicIsPlaying !== 'undefined' && musicIsPlaying && typeof musicCurrentPath !== 'undefined' && musicCurrentPath) {
      // If music is playing on the server, hand it off to local immediately
      const title = document.getElementById('music-bar-title')?.textContent || 'Track';
      // Capture current playback position from the seek bar (populated by last poll)
      const seekEl = document.getElementById('music-seek');
      const resumePos = seekEl ? parseFloat(seekEl.value) || 0 : 0;
      // Stop the poll synchronously so no in-flight tick races with the handoff.
      if (typeof stopMusicPlaybackPoll !== 'undefined') stopMusicPlaybackPoll();
      musicIsPlaying = false;

      // CRITICAL: call localPlay() NOW — still inside the user-gesture activation window.
      // If we wait for the fetch().then() the browser's autoplay policy will have expired
      // and play() will be rejected with MEDIA_ERR_SRC_NOT_SUPPORTED.
      // Start muted so server audio and local audio don't overlap while the stop propagates.
      const aHandoff = typeof _getLocalAudio !== 'undefined' ? _getLocalAudio() : null;
      if (aHandoff) aHandoff.muted = true;
      localPlay('/api/music/stream?path=' + encodeURIComponent(musicCurrentPath), title, 'Music');

      // Seek to saved position once the stream is ready, then unmute.
      const doSeekAndUnmute = () => {
        const a = typeof _getLocalAudio !== 'undefined' ? _getLocalAudio() : null;
        if (!a) return;
        const applySeek = () => {
          if (resumePos > 0.5) {
            a.currentTime = resumePos;
            const onSeeked = () => { a.muted = false; a.removeEventListener('seeked', onSeeked); };
            a.addEventListener('seeked', onSeeked);
            // Fallback: unmute after 400 ms in case seeked never fires
            setTimeout(() => { a.muted = false; a.removeEventListener('seeked', onSeeked); }, 400);
          } else {
            a.muted = false;
          }
        };
        if (a.readyState >= 2) applySeek();
        else { a.addEventListener('canplay', function h() { a.removeEventListener('canplay', h); applySeek(); }); }
      };

      // Tell the server to stop AFTER play() is already running; unmute once confirmed.
      fetch('/api/music/stop').then(doSeekAndUnmute).catch(doSeekAndUnmute);
    } else {
      // No active playback to hand off — just log the mode switch
      setTimeout(() => {
        fetch('/api/local-playing', { method: 'POST', body: 'true' }).catch(() => {});
        fetch('/api/log', { method: 'POST', body: 'Switched playback mode to Local' }).catch(() => {});
      }, 0);
    }
  } else {
    fetch('/api/log', { method: 'POST', body: 'Switched playback mode to Server' }).catch(() => {});
    fetch('/api/local-playing', { method: 'POST', body: 'false' }).catch(() => {});
    // Capture position and state BEFORE localStop() wipes the audio element (sets src='')
    const a = typeof _getLocalAudio !== 'undefined' ? _getLocalAudio() : null;
    const localType = typeof _localMediaType !== 'undefined' ? _localMediaType : '';
    const wasPlaying = a && a.src && !a.paused;
    const localResumePos = (a && localType === 'Music') ? (a.currentTime || 0) : 0;
    // Use localStop() with keepPlayerVisible=true for music/radio handoff to prevent flicker
    const keepPlayerVisible = wasPlaying && (localType === 'Music' || localType === 'Radio');
    if (typeof localStop !== 'undefined') localStop(keepPlayerVisible);
    // Leave the Web Audio boost graph running — localStop() already cleared a.src so the
    // MediaElementSource produces silence. Suspending risks a resume-race where the context
    // is still in the suspended→running transition when the next localPlay() calls a.play(),
    // which Chrome rejects with MEDIA_ERR_SRC_NOT_SUPPORTED.
    if (wasPlaying) {
      if (localType === 'Radio' && typeof _radioCurrentUrl !== 'undefined' && _radioCurrentUrl) {
        // Resume radio on server
        fetch('/api/radio/play?' + new URLSearchParams({ url: _radioCurrentUrl, name: _radioCurrentName || '' }), { method: 'POST' })
          .then(() => {
            if (typeof updateRadioBar !== 'undefined')
              updateRadioBar(_radioCurrentName, _radioCurrentCountry, _radioCurrentTag, true, _radioCurrentStation);
          }).catch(() => {});
      } else if (localType === 'Music' && typeof musicCurrentPath !== 'undefined' && musicCurrentPath) {
        // Resume music on server, starting at the exact position local left off (atomic — no separate seek needed)
        const playUrl = '/api/music/play?path=' + encodeURIComponent(musicCurrentPath)
          + (localResumePos > 0.5 ? '&pos=' + localResumePos.toFixed(2) : '');
        fetch(playUrl).then(() => {
          if (typeof musicIsPlaying !== 'undefined') musicIsPlaying = true;
          // Immediately update the music bar to keep it visible during handoff
          if (typeof updateMusicBar === 'function') {
            updateMusicBar({ 
              isPlaying: true, 
              isPaused: false, 
              currentPath: musicCurrentPath,
              position: localResumePos
            });
          }
          if (typeof startMusicPlaybackPoll !== 'undefined') startMusicPlaybackPoll();
        }).catch(() => {});
      }
    }
  }
}
// Apply saved state on load
document.addEventListener('DOMContentLoaded', () => {
  // Restore user's last selected playback mode (server/local).
  // Local playback still requires a user gesture when starting media.
  const savedLocalMode = localStorage.getItem('remotePlayLocal') === 'true';
  setPlayLocal(savedLocalMode);

  // On load, switch to whichever domain is currently playing on this server instance.
  // This means navigating to an instance always lands on the active player, not the default tab.
  (async () => {
    try {
      const routeState = _parseInitialUiRoute();
      if (routeState.hasExplicitRoute) return;

      const [videoRes, musicRes, radioRes] = await Promise.all([
        fetch('/api/status').then(r => r.ok ? r.json() : null).catch(() => null),
        fetch('/api/music/status').then(r => r.ok ? r.json() : null).catch(() => null),
        fetch('/api/radio/playback-status').then(r => r.ok ? r.json() : null).catch(() => null),
      ]);
      let targetMode = null;
      if (musicRes && (musicRes.isPlaying || musicRes.isPaused)) targetMode = 'music';
      else if (radioRes && (radioRes.isPlaying || radioRes.IsPlaying)) targetMode = 'radio';
      else if (videoRes && (videoRes.isPlaying || videoRes.isPaused)) targetMode = 'video';

      if (!targetMode || targetMode === 'video') {
        // Ensure currentMode is 'video' regardless of what localStorage had,
        // so render() correctly shows the video command bar.
        if (typeof switchMode === 'function') switchMode('video', true);

        // If the app was initially restored into a non-video mode (e.g. music)
        // and then forced back to video here, ensure the video root browse runs.
        // Without this, the tab can show only header chrome until a manual refresh.
        if ((typeof currentData === 'undefined' || !currentData) && typeof browse === 'function') {
          browse(null);
        }

        return;
      }

      // Apply the correct mode + browse folder. We call this twice:
      //  1. Immediately — for fast rendering when the video browse(null) hasn't fired yet.
      //  2. After ~900 ms — to override the video tab/command-bar re-activation that
      //     render() triggers when the module-level browse(null) response arrives.
      function _applyActiveMode() {
        // Pre-seed music folder sentinel so switchMode doesn't fire browseMusic(null) first
        if (targetMode === 'music' && musicRes && musicRes.currentPath) {
          const folder = _parentDir(_decodePath(musicRes.currentPath));
          if (folder && typeof currentMusicFolder !== 'undefined') {
            currentMusicFolder = folder;
            if (typeof currentMusicData !== 'undefined' && !currentMusicData) currentMusicData = {};
          }
        }

        if (typeof switchMode === 'function') switchMode(targetMode);

        if (targetMode === 'music' && musicRes && (musicRes.isPlaying || musicRes.isPaused)) {
          if (typeof musicIsPlaying !== 'undefined') musicIsPlaying = true;
          if (typeof musicCurrentPath !== 'undefined') musicCurrentPath = musicRes.currentPath || null;
          if (typeof updateMusicBar === 'function') updateMusicBar(musicRes);
          if (typeof startMusicPlaybackPoll === 'function') startMusicPlaybackPoll();
          if (musicRes.currentPath && typeof browseMusic === 'function') {
            const folder = _parentDir(_decodePath(musicRes.currentPath));
            if (folder) browseMusic(folder);
          }
        } else if (targetMode === 'radio') {
          // Restore radio player UI state
          const isPlaying = radioRes && (radioRes.isPlaying || radioRes.IsPlaying);
          console.debug('[RADIO-RESTORE] targetMode=radio, radioRes=', radioRes, 'isPlaying=', isPlaying);
          if (isPlaying) {
            const name = radioRes.stationName || radioRes.StationName || 'Radio';
            const url = radioRes.stationUrl || radioRes.StationUrl || '';
            const country = radioRes.country || radioRes.Country || '';
            const tag = radioRes.tag || radioRes.Tag || '';
            const station = radioRes.station || radioRes.Station || null;

            // Set global variables
            if (typeof _radioIsPlaying !== 'undefined') _radioIsPlaying = true;
            if (typeof _radioCurrentUrl !== 'undefined' && url) _radioCurrentUrl = url;
            if (typeof _radioCurrentName !== 'undefined' && name) _radioCurrentName = name;
            if (typeof _radioCurrentCountry !== 'undefined') _radioCurrentCountry = country;
            if (typeof _radioCurrentTag !== 'undefined') _radioCurrentTag = tag;
            if (typeof _radioCurrentStation !== 'undefined' && station) _radioCurrentStation = station;

            // Show radio player bar
            const radioBar = document.getElementById('radio-player-bar');
            console.debug('[RADIO-RESTORE] radioBar element=', radioBar, 'display BEFORE=', radioBar?.style.display);
            if (radioBar) radioBar.style.display = 'flex';
            console.debug('[RADIO-RESTORE] display AFTER=', radioBar?.style.display);
            document.body.classList.add('radio-player-docked');

            // Update radio bar UI
            if (typeof updateRadioBar !== 'undefined') {
              updateRadioBar(name, country, tag, true, station);
            }
          }
          if (typeof startRadioStatusPoll === 'function') startRadioStatusPoll();
        }
      }

      _applyActiveMode();
      // Re-apply after the initial video browse response has rendered (race fix)
      setTimeout(_applyActiveMode, 900);
    } catch {}
  })();

  // Initialize the independent volume and boost controls to their defaults.
  if (typeof syncMusicAudioControls === 'function') syncMusicAudioControls(0.8, 1);
  if (typeof musicVolume === 'function') musicVolume(0.8);
  if (typeof musicBoost === 'function') musicBoost(1);
  if (typeof setVideoCombinedSlider === 'function') setVideoCombinedSlider(1.0);
  if (typeof syncRadioAudioControls === 'function') syncRadioAudioControls(0.8, 1);

  // Keep --header-h in sync so sticky elements below the header don't overlap it.
  const hdr = document.querySelector('header');
  if (hdr && typeof ResizeObserver !== 'undefined') {
    const ro = new ResizeObserver(entries => {
      const h = entries[0].borderBoxSize?.[0]?.blockSize ?? entries[0].contentRect.height;
      document.documentElement.style.setProperty('--header-h', h + 'px');
    });
    ro.observe(hdr);
  }
  // Keep --nav-row-h in sync so the video list-view column header sticks below the pinned strip.
  const navRow = document.getElementById('browse-nav-row');
  if (navRow && typeof ResizeObserver !== 'undefined') {
    const ro2 = new ResizeObserver(entries => {
      const h = entries[0].borderBoxSize?.[0]?.blockSize ?? entries[0].contentRect.height;
      document.documentElement.style.setProperty('--nav-row-h', h + 'px');
    });
    ro2.observe(navRow);
  }
});
function setTheme(theme, save = true) {
  document.body.classList.remove(
    'theme-amoled',
    'theme-blue',
    'theme-sunset',
    'theme-neon',
    'theme-forest',
    'theme-nord',
    'theme-lavender',
    'theme-peach',
    'theme-mint',
    'theme-crimson',
    'theme-ocean',
    'theme-gold',
    'theme-slate',
    'theme-rose',
    'theme-dracula',
    'theme-aurora',
    'theme-dusk',
    'theme-mocha',
    'theme-sky',
    'theme-lemon',
    'theme-graphite',
    'theme-paper',
    'theme-cyberpunk',
    'theme-solarized-dark',
    'theme-solarized-light',
    'theme-high-contrast',
    'theme-cb-safe-dark',
    'theme-cb-safe-light',
    'theme-cb-protanopia',
    'theme-cb-deuteranopia',
    'theme-steel',
    'theme-olive',
    'theme-violet',
    'theme-silver',
    'theme-caramel'
  );
  if (theme && theme !== 'default') document.body.classList.add('theme-' + theme);
  // sync custom picker button label + active item
  const activeKey = theme || 'default';
  const panel = document.getElementById('theme-picker-panel');
  const btnLabel = document.getElementById('theme-picker-label');
  if (panel) {
    panel.querySelectorAll('.tp-item').forEach(el => {
      const isActive = el.dataset.theme === activeKey;
      el.classList.toggle('active', isActive);
      if (isActive && btnLabel) btnLabel.textContent = el.textContent.trim();
    });
    panel.classList.remove('open');
    const btn = document.getElementById('theme-picker-btn');
    if (btn) btn.setAttribute('aria-expanded', 'false');
  }
  if (save) localStorage.setItem('remotePlayTheme', activeKey);
}

function toggleSleepPopup() {
  const popup = document.getElementById('sleep-popup-panel');
  const btn   = document.getElementById('sleep-icon-btn');
  if (!popup) return;
  const open = popup.classList.toggle('open');
  if (btn) btn.setAttribute('aria-expanded', String(open));
  if (open) {
    const close = e => {
      const wrap = document.getElementById('sleep-popup-wrap');
      if (wrap && !wrap.contains(e.target)) {
        popup.classList.remove('open');
        btn && btn.setAttribute('aria-expanded', 'false');
        document.removeEventListener('pointerdown', close, true);
      }
    };
    document.addEventListener('pointerdown', close, true);
  }
}

function toggleThemePicker() {
  const panel = document.getElementById('theme-picker-panel');
  const btn   = document.getElementById('theme-picker-btn');
  if (!panel) return;
  const open = panel.classList.toggle('open');
  if (btn) btn.setAttribute('aria-expanded', String(open));
  if (open) {
    // close when clicking outside
    const close = e => {
      if (!document.getElementById('theme-picker').contains(e.target)) {
        panel.classList.remove('open');
        btn && btn.setAttribute('aria-expanded', 'false');
        document.removeEventListener('pointerdown', close, true);
      }
    };
    document.addEventListener('pointerdown', close, true);
  }
}

function applyPhonePlaybackState(isPlaying) {
  if (!isPhoneRemoteOnly) return;
  document.body.classList.toggle('phone-playing', Boolean(isPlaying));
  setPlayerBarVisible(isPlaying);
}

function _applyLiveTvPlayerUi(isLiveTvMode, status) {
  const navGroup = document.querySelector('.transport-nav-group');
  const queueCard = document.getElementById('queue-card');
  const speedRow = document.getElementById('speed-row');
  const progressRow = document.getElementById('progress-row');
  const resumeCard = document.getElementById('resume-card');
  const upNextCard = document.getElementById('up-next-card');
  const seekBack = document.getElementById('seek-back-btn');
  const seekForward = document.getElementById('seek-forward-btn');
  const timeLabel = document.getElementById('time-label');

  if (!isLiveTvMode) {
    if (navGroup) navGroup.style.display = '';
    if (queueCard) queueCard.style.display = '';
    if (speedRow) speedRow.style.display = '';
    if (progressRow) progressRow.style.display = '';
    if (resumeCard) resumeCard.style.display = '';
    if (upNextCard) upNextCard.style.display = '';
    if (seekBack) seekBack.style.display = '';
    if (seekForward) seekForward.style.display = '';
    return;
  }

  if (navGroup) navGroup.style.display = 'none';
  if (queueCard) queueCard.style.display = 'none';
  if (speedRow) speedRow.style.display = 'none';
  if (progressRow) progressRow.style.display = 'none';
  if (resumeCard) resumeCard.style.display = 'none';
  if (upNextCard) upNextCard.style.display = 'none';
  if (seekBack) seekBack.style.display = 'none';
  if (seekForward) seekForward.style.display = 'none';
  if (timeLabel) {
    timeLabel.textContent = status && status.isPlaying
      ? 'Live stream'
      : 'Buffering live stream…';
  }
}

function setPlayerBarVisible(visible) {
  const bar = document.getElementById('now-playing-bar');
  const hdr = document.querySelector('header');
  if (bar) bar.style.display = visible ? 'flex' : 'none';
  if (hdr) hdr.classList.toggle('player-visible', Boolean(visible));
}

function applyDesktopDockedLayout(isPlaying) {
  const useDocked = !isPhoneRemoteOnly && Boolean(isPlaying);
  document.body.classList.toggle('desktop-player-docked', useDocked);
  document.body.classList.toggle(
    'tablet-docked',
    useDocked && window.matchMedia('(min-width:900px) and (max-width:1280px)').matches
  );
}
function applyPhoneLayout() {
  isPhoneRemoteOnly = phoneLayoutQuery.matches;
  document.body.classList.toggle('phone-remote-only', isPhoneRemoteOnly);
  if (!isPhoneRemoteOnly) {
    document.body.classList.remove('phone-playing');
    document.body.classList.remove('landscape-controls');
    applyDesktopDockedLayout(Boolean(playingPath));
    if (!playingPath) setPlayerBarVisible(false);
    return;
  }

  document.body.classList.remove('desktop-player-docked');
  document.body.classList.remove('tablet-docked');
  const isLandscape = window.matchMedia('(orientation: landscape)').matches;
  document.body.classList.toggle('landscape-controls', isLandscape);
  applyPhonePlaybackState(Boolean(playingPath));
}

function haptic(ms) {
  if (!('vibrate' in navigator)) return;
  try {
    navigator.vibrate(ms);
  } catch {}
}
function onPlayerGestureStart(event) {
  if (!isPhoneRemoteOnly || !playingPath) return;
  if (event.touches && event.touches.length !== 1) return;
  const target = event.target;
  if (target && target.closest('button,input,select,label,a')) return;
  const p = event.changedTouches ? event.changedTouches[0] : event;
  gestureStart = { x: p.clientX, y: p.clientY, t: Date.now() };
}
function onPlayerGestureEnd(event) {
  if (!gestureStart || !isPhoneRemoteOnly || !playingPath) return;
  const p = event.changedTouches ? event.changedTouches[0] : event;
  const dx = p.clientX - gestureStart.x;
  const dy = p.clientY - gestureStart.y;
  const dt = Date.now() - gestureStart.t;
  gestureStart = null;
  if (dt > 900) return;
  if (Math.abs(dx) < 35 && Math.abs(dy) < 35) return;
  if (Math.abs(dx) > Math.abs(dy)) {
    quickSkip(dx > 0 ? 10 : -10);
    return;
  }
  adjustVolumeBy(dy < 0 ? 0.05 : -0.05);
}
function adjustVolumeBy(delta) {
  const slider = document.getElementById('video-combined-vol');
  if (!slider) return;
  const current = Math.max(0, Math.min(1.3, Number(slider.value) || 0));
  const next = Math.max(0, Math.min(1.3, current + delta));
  slider.value = next.toFixed(2);
  setVideoCombinedSlider(slider.value);
  haptic(8);
}
function startPolling() {
  if (pollInterval) return;
  pollInterval = setInterval(pollStatus, 1000);
}
function stopPolling() {
  clearInterval(pollInterval);
  pollInterval = null;
}

/** Expert Mode — set by server config, gates expert-only UI via body.expert-mode */
function _setExpertMode(on) {
  document.body.classList.toggle('expert-mode', !!on);
  const chk = document.getElementById('expert-mode-chk');
  if (chk) chk.checked = !!on;
}

async function _refreshExpertMode() {
  try {
    const res = await fetch('/api/expert-mode');
    if (!res.ok) return;
    const s = await res.json();
    if (s && typeof s.expertMode === 'boolean') _setExpertMode(s.expertMode);
  } catch {}
}

/** Debug Mode — set by server config, gates debug-only UI via body.debug-mode */
function _setDebugMode(on) {
  document.body.classList.toggle('debug-mode', !!on);
  const chk = document.getElementById('debug-mode-chk');
  if (chk) chk.checked = !!on;
}

async function _refreshDebugMode() {
  try {
    const res = await fetch('/api/debug-mode');
    if (!res.ok) return;
    const s = await res.json();
    if (s && typeof s.debugMode === 'boolean') _setDebugMode(s.debugMode);
  } catch {}
}

async function pollStatus() {
  try {
    const res = await fetch('/api/status');
    if (!res.ok) {
      setConnectionStatus('Retrying connection...', true);
      statusFailures++;
      return null;
    }
    const s = await res.json();
    await _applyPendingWebUiNavigation(s);
    statusFailures = 0;
    setConnectionStatus('Connected', false, true);
    updateDiagnosticsIndicator(s.lastError ? 'error' : 'ok');
    if (typeof s.expertMode === 'boolean') _setExpertMode(s.expertMode);
    if (typeof s.debugMode  === 'boolean') _setDebugMode(s.debugMode);
    const bar = document.getElementById('now-playing-bar');
    const hasQueue = Array.isArray(s.queue) && s.queue.length > 0;
    const isLiveTvMode = typeof currentMode !== 'undefined' && currentMode === 'live-tv';
    const liveTvInfo = isLiveTvMode && typeof liveTvGetNowPlayingInfo === 'function'
      ? liveTvGetNowPlayingInfo()
      : null;

    _applyLiveTvPlayerUi(isLiveTvMode, s);
    if (isLiveTvMode) {
      if (s.lastError) {
        showPlaybackErrorPopup('Live TV', s.lastError);
        if (typeof liveTvHandleStatusPlaybackError === 'function') {
          liveTvHandleStatusPlaybackError(s.lastError);
        }
      } else {
        clearPlaybackErrorDedup('Live TV');
        if (typeof liveTvClearStatusPlaybackError === 'function') {
          liveTvClearStatusPlaybackError();
        }
      }
    }
    if (!isLiveTvMode) updateQueueControls(s);
    lastQueue = hasQueue ? s.queue : [];
    updateStopBtn(s.isPlaying, isLiveTvMode ? false : hasQueue);
    if (!isLiveTvMode) updateAdjacentButtons(s);
    if (!isLiveTvMode) {
      const err = document.getElementById('error');
      if (s.lastError) {
        if (err) err.style.display = 'none';
        showPlaybackErrorPopup('Video', s.lastError);
      } else {
        if (err) {
          err.style.display = 'none';
          err.textContent = '';
        }
        clearPlaybackErrorDedup('Video');
      }
    }
    if (s.isPlaying || hasQueue) {
      if (typeof currentMode === 'undefined' || currentMode === 'video' || isLiveTvMode)
        setPlayerBarVisible(true);
    }
    if (s.isPlaying) {
      if (s.filePath && s.filePath !== playingPath) setPlayerPoster(s.filePath);
      if (s.filePath) {
        const oldPath = playingPath && playingPath !== s.filePath ? playingPath : null;
        playingPath = s.filePath;
        updatePlayingCard(s.filePath, oldPath);
      }
      const pb = document.getElementById('pause-btn');
      pb.textContent = s.isPaused ? '\u25B6 Resume' : '\u23F8 Pause';
      document.getElementById('player-title').textContent = liveTvInfo?.name
        ? liveTvInfo.name
        : (s.title || 'Now playing').replace(/^\s*[▶⏸]\s*/, '');
      const optionsCard = document.getElementById('options-card');
      const volume = Math.max(0, Math.min(1, Number(s.volume) || 0));
      if (volume > 0.001) lastVolumeBeforeMute = volume;
      updateVolumeIcon(volume);
      const boostGain = Math.max(1, Number(s.audioBoost) || 1);
      const boostAmount = boostGain - 1; // keep for buildPlayerMeta compatibility
      // Map vol + boostGain back to combined slider and update its gradient
      const combined = boostGain > 1.0
        ? Math.min(1.3, 1.0 + (boostGain - 1) * 0.3)
        : Math.min(1.0, volume);
      const cs = document.getElementById('video-combined-vol');
      if (cs) {
        cs.value = combined.toFixed(2);
        // Update the track-fill gradient without firing API calls
        const totalRange  = 1.3;
        const volBoundary = (1.0 / totalRange * 100).toFixed(2) + '%';
        const fillPct     = (combined / totalRange * 100).toFixed(2) + '%';
        if (combined > 1.0) {
          cs.classList.add('slider-boosting');
          cs.style.setProperty('--cvs-vol',   volBoundary);
          cs.style.setProperty('--cvs-boost', fillPct);
          cs.style.setProperty('--cvs-thumb', '#f97316');
          const db = Math.round(20 * Math.log10(boostGain));
          const volLbl = document.getElementById('volume-label');
          if (volLbl) volLbl.textContent = '100% +' + db + 'dB';
        } else {
          cs.classList.remove('slider-boosting');
          cs.style.setProperty('--cvs-vol',   fillPct);
          cs.style.setProperty('--cvs-boost', fillPct);
          cs.style.setProperty('--cvs-thumb', 'var(--player-accent,#e94560)');
          const volLbl = document.getElementById('volume-label');
          if (volLbl) volLbl.textContent = Math.round(volume * 100) + '%';
        }
      }
      const brightness = Math.max(0.3, Math.min(0.7, Number(s.brightness) || 0.5));
      if (Date.now() >= brightnessPendingUntil) {
        document.getElementById('brightness').value = brightness;
        document.getElementById('brightness-label').textContent = Math.round(brightness * 100) + '%';
      }
      const rawSaturation = Number(s.saturation);
      const saturation = Math.max(
        0,
        Math.min(2, Number.isFinite(rawSaturation) ? rawSaturation : 1)
      );
      if (Date.now() >= saturationPendingUntil) {
        document.getElementById('saturation').value = saturation;
        document.getElementById('saturation-label').textContent = Math.round(saturation * 100) + '%';
      }
      const rawSharpness = Number(s.sharpness);
      const sharpness = Math.max(0, Math.min(1, Number.isFinite(rawSharpness) ? rawSharpness : 0));
      if (!sharpnessDragging && Date.now() >= sharpnessPendingUntil) {
        document.getElementById('sharpness').value = sharpness;
        document.getElementById('sharpness-label').textContent = Math.round(sharpness * 100) + '%';
      }
      const rawZoom = Number(s.zoom);
      const zoom = Math.max(0.5, Math.min(2, Number.isFinite(rawZoom) ? rawZoom : 1));
      if (!zoomDragging && Date.now() >= zoomPendingUntil) {
        document.getElementById('zoom').value = zoom;
        document.getElementById('zoom-label').textContent = Math.round(zoom * 100) + '%';
      }
      updateResumePrompt(s);
      const progress = document.getElementById('progress');
      if (s.duration > 0) {
        progress.max = s.duration;
        if (!seekDragging) progress.value = s.position;
        updateSeekFill(progress);
        document.getElementById('time-label').textContent = fmt(s.position) + ' / ' + fmt(s.duration);
      } else if (!s.isPlaying) {
        // Not playing and no duration — show zeroes
        document.getElementById('time-label').textContent = fmt(0) + ' / ' + fmt(0);
      }
      // If isPlaying but duration still 0 (VLC hasn't reported length yet), keep whatever was shown
      if (s.duration > 0) updateCardProgress(s.filePath, s.position, s.duration);
      currentPlaybackSpeed = Math.max(0.5, Math.min(2, Number(s.playbackSpeed) || 1));
      syncSpeedChips(currentPlaybackSpeed);
      updateTrackControls(s);
      document.getElementById('player-meta').textContent = liveTvInfo
        ? ['Live TV', liveTvInfo.countryName, liveTvInfo.category, liveTvInfo.sourceHost].filter(Boolean).join(' • ')
        : buildPlayerMeta(s, volume, boostAmount);
      requestWakeLock();
      if (isPhoneRemoteOnly) applyPhonePlaybackState(true);
      else if (typeof currentMode === 'undefined' || currentMode === 'video' || isLiveTvMode) applyDesktopDockedLayout(true);
    } else if (isPhoneRemoteOnly) {
      releaseWakeLock();
      updateResumePrompt(null);
      if (playingPath) {
        updatePlayingCard(null, playingPath);
        playingPath = null;
      }
      document.getElementById('player-title').textContent = hasQueue
        ? 'Queue ready'
        : 'Nothing playing';
      document.getElementById('player-meta').textContent = hasQueue
        ? 'Next up: ' + s.queue.length + ' queued video(s)'
        : '';
      if (isLiveTvMode && liveTvInfo) {
        document.getElementById('player-title').textContent = liveTvInfo.name || 'Live TV';
        document.getElementById('player-meta').textContent = ['Live TV', liveTvInfo.countryName, liveTvInfo.category, liveTvInfo.sourceHost].filter(Boolean).join(' • ');
        setConnectionStatus('Buffering live stream…', true);
      }
      applyPhonePlaybackState(false);
    } else {
      releaseWakeLock();
      updateResumePrompt(null);
      if (playingPath) {
        updatePlayingCard(null, playingPath);
        playingPath = null;
      }
      document.getElementById('player-title').textContent = hasQueue
        ? 'Queue ready'
        : 'Nothing playing';
      document.getElementById('player-meta').textContent = hasQueue
        ? 'Next up: ' + s.queue.length + ' queued video(s)'
        : '';
      if (isLiveTvMode && liveTvInfo) {
        document.getElementById('player-title').textContent = liveTvInfo.name || 'Live TV';
        document.getElementById('player-meta').textContent = ['Live TV', liveTvInfo.countryName, liveTvInfo.category, liveTvInfo.sourceHost].filter(Boolean).join(' • ');
        setConnectionStatus('Buffering live stream…', true);
        setPlayerBarVisible(true);
      }
      if (typeof currentMode === 'undefined' || currentMode === 'video' || isLiveTvMode) {
        if (hasQueue) {
          document.body.classList.add('desktop-player-docked');
        } else {
          applyDesktopDockedLayout(false);
          setPlayerBarVisible(false);
        }
      }
    }
    return s;
  } catch (e) {
    statusFailures++;
    setConnectionStatus(
      statusFailures > 2 ? 'Connection lost - retrying...' : 'Retrying connection...',
      true
    );
    updateDiagnosticsIndicator('error');
    return null;
  }
}

// ── Playback error popup ─────────────────────────────────────────────────────
// Tracks the last error shown per domain to avoid repeat popups.
const _pbeLastError = { video: '', music: '', radio: '' };
let _pbeOverlayEl = null;
let _pbePreviousFocus = null;

function _announcePlayerStatus(playerId, state, message) {
  const status = document.getElementById(playerId);
  if (!status || status.dataset.state === state) return;
  const previousState = status.dataset.state;
  status.dataset.state = state;
  if (!message) {
    status.textContent = '';
    return;
  }
  if (!previousState && state.endsWith(':stopped')) return;
  status.textContent = message;
}

// Translate common Windows HRESULT / media error codes to plain English.
// Returns { friendly: string, translated: boolean }
function _friendlyMediaError(msg) {
  if (!msg) return { friendly: msg, translated: false };
  const codes = {
    '0xC00D2EFE': 'The stream ended unexpectedly or the station stopped broadcasting.',
    '0xC00D0035': 'The stream source could not be found. The URL may be invalid or the station is offline.',
    '0xC00D36FA': 'Playback was stopped by the application.',
    '0xC00D36B4': 'The media format is not supported.',
    '0xC00D36C4': 'The byte stream type is unsupported. The station format may not be compatible.',
    '0xC00D4A44': 'The stream could not be opened. The station may be offline or require authentication.',
    '0x80004005': 'An unspecified error occurred while accessing the stream.',
    '0x80070005': 'Access was denied to the media source.',
    '0x800700AA': 'The resource is currently in use by another process.',
    '0xC00D07D2': 'The codec required for this stream is not available.',
    '0xC00D3E8C': 'The network timed out while buffering the stream.',
    '0xC00D4268': 'Could not connect to the server. The station may be offline.',
  };
  const textCodes = {
    'WaveHeaderUnprepared': 'The audio output device encountered an internal buffer error. The audio driver may have reset — try restarting playback.',
    'waveOutWrite':         'The audio output device encountered an internal buffer error. The audio driver may have reset — try restarting playback.',
    'MMSYSERR_INVALHANDLE': 'The audio output device handle became invalid. The audio driver may have reset — try restarting playback.',
    'MMSYSERR_NODRIVER':    'No audio output driver is available. Check that an audio device is connected and enabled.',
    'WAVERR_STILLPLAYING':  'The audio device is still busy with a previous buffer. Try stopping and restarting playback.',
  };

  let out = msg;
  let translated = false;

  // Replace known hex codes and clean up surrounding debris
  for (const [code, human] of Object.entries(codes)) {
    if (out.toUpperCase().includes(code.toUpperCase())) {
      // Remove the code itself, then remove any wrapping parens/brackets that held only the code
      out = out.replace(new RegExp(`\\s*[\\(\\[]?\\s*${code}\\s*[\\)\\]]?\\s*`, 'gi'), ' ');
      // Clean up trailing punctuation and whitespace
      out = out.replace(/[.\s]+$/, '').replace(/^[.\s]+/, '').trim();
      out = (out ? out + '\n\n' : '') + human;
      translated = true;
    }
  }

  // Replace known text error tokens (take priority — replace entire message)
  for (const [token, human] of Object.entries(textCodes)) {
    if (out.includes(token)) {
      out = human;
      translated = true;
      break;
    }
  }

  return { friendly: out, translated };
}

function _buildPbeOverlay() {
  if (_pbeOverlayEl) return _pbeOverlayEl;

  const overlay = document.createElement('div');
  // All critical styles are inline so no CSS class can interfere
  Object.assign(overlay.style, {
    position: 'fixed',
    top: '0',
    left: '0',
    width: '100%',
    height: '100%',
    zIndex: '99999',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    background: 'rgba(0,0,0,0.65)',
  });
  overlay.setAttribute('role', 'alertdialog');
  overlay.setAttribute('aria-modal', 'true');
  overlay.setAttribute('aria-labelledby', 'pbe-title');
  overlay.setAttribute('aria-describedby', 'pbe-message');

  const card = document.createElement('div');
  card.id = 'playback-error-overlay';
  // Use player-card-bg-solid first (opaque), fall back to a hardcoded opaque dark colour.
  // Do NOT use --card-bg which can be semi-transparent in light/silver themes.
  Object.assign(card.style, {
    background: 'var(--player-card-bg-solid, var(--folder-bg, #1a1e2e))',
    color: 'var(--folder-text, #eef)',
    border: '2px solid var(--accent, #e94560)',
    borderRadius: '18px',
    padding: '1.6rem 2rem 1.4rem',
    maxWidth: 'min(520px, 92vw)',
    width: '100%',
    boxShadow: '0 24px 64px rgba(0,0,0,0.75)',
    fontFamily: "'Segoe UI', sans-serif",
  });

  const header = document.createElement('div');
  Object.assign(header.style, { display: 'flex', alignItems: 'center', gap: '.5rem', marginBottom: '.6rem' });

  const icon = document.createElement('span');
  icon.textContent = '⚠';
  icon.setAttribute('aria-hidden', 'true');
  Object.assign(icon.style, { fontSize: '1.3rem', color: 'var(--accent, #e94560)', flexShrink: '0' });

  const domainEl = document.createElement('span');
  domainEl.id = 'pbe-domain';
  Object.assign(domainEl.style, { fontSize: '.78rem', fontWeight: '700', opacity: '.7', letterSpacing: '.05em', textTransform: 'uppercase' });

  const title = document.createElement('h2');
  title.id = 'pbe-title';
  title.textContent = 'Playback stopped';
  Object.assign(title.style, { fontSize: '1.25rem', fontWeight: '800', margin: '0', color: 'var(--title-color, var(--accent, #e94560))' });

  header.appendChild(icon);
  header.appendChild(domainEl);

  const msgEl = document.createElement('p');
  msgEl.id = 'pbe-message';
  Object.assign(msgEl.style, { margin: '.4rem 0 .6rem', fontSize: '.9rem', lineHeight: '1.5', whiteSpace: 'pre-wrap', opacity: '.9' });

  const techEl = document.createElement('p');
  techEl.id = 'pbe-tech';
  Object.assign(techEl.style, { margin: '0 0 1rem', fontSize: '.72rem', lineHeight: '1.4', whiteSpace: 'pre-wrap', opacity: '.5', fontFamily: 'monospace' });

  const actions = document.createElement('div');
  Object.assign(actions.style, { display: 'flex', justifyContent: 'flex-end' });

  const dismissBtn = document.createElement('button');
  dismissBtn.type = 'button';
  dismissBtn.className = 'pbe-close-btn';
  dismissBtn.textContent = 'Dismiss';
  Object.assign(dismissBtn.style, {
    background: 'var(--accent, #e94560)',
    color: '#fff',
    border: 'none',
    borderRadius: '8px',
    padding: '.5rem 1.4rem',
    fontWeight: '700',
    fontSize: '.88rem',
    cursor: 'pointer',
    minHeight: '44px',
  });
  dismissBtn.onclick = closePlaybackErrorPopup;
  actions.appendChild(dismissBtn);

  card.appendChild(header);
  card.appendChild(title);
  card.appendChild(msgEl);
  card.appendChild(techEl);
  card.appendChild(actions);
  overlay.appendChild(card);

  overlay.addEventListener('click', (e) => { if (e.target === overlay) closePlaybackErrorPopup(); });
  overlay.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      e.preventDefault();
      closePlaybackErrorPopup();
    } else if (e.key === 'Tab') {
      e.preventDefault();
      dismissBtn.focus();
    }
  });

  // Store direct references so showPlaybackErrorPopup never needs getElementById
  overlay._domainEl = domainEl;
  overlay._msgEl = msgEl;
  overlay._techEl = techEl;
  overlay._dismissBtn = dismissBtn;

  _pbeOverlayEl = overlay;
  return overlay;
}

function showPlaybackErrorPopup(domain, message) {
  if (!message) return;
  const key = (domain || 'video').toLowerCase();
  // Deduplicate: don't re-show the same error for the same domain until dismissed
  if (_pbeLastError[key] === message) return;
  _pbeLastError[key] = message;

  const overlay = _buildPbeOverlay();
  _pbePreviousFocus = document.activeElement instanceof HTMLElement
    ? document.activeElement
    : null;
  if (overlay._domainEl) overlay._domainEl.textContent = domain || '';
  const { friendly, translated } = _friendlyMediaError(message);
  if (overlay._msgEl) overlay._msgEl.textContent = friendly;
  // Show raw technical detail only when we actually translated the message
  if (overlay._techEl) {
    overlay._techEl.textContent = translated ? message.trim() : '';
    overlay._techEl.style.display = translated ? '' : 'none';
  }

  // Always attach as last child of body so nothing can trap it
  document.body.appendChild(overlay);
  overlay._dismissBtn?.focus({ preventScroll: true });
}

// Called by domain status pollers when playback resumes successfully,
// so the same error can show again if it recurs after recovery.
function clearPlaybackErrorDedup(domain) {
  const key = (domain || 'video').toLowerCase();
  _pbeLastError[key] = '';
}

function closePlaybackErrorPopup() {
  if (_pbeOverlayEl && _pbeOverlayEl.parentNode) {
    _pbeOverlayEl.parentNode.removeChild(_pbeOverlayEl);
  }
  const previousFocus = _pbePreviousFocus;
  _pbePreviousFocus = null;
  if (previousFocus?.isConnected) previousFocus.focus();
  // Do NOT clear _pbeLastError here — keeps the dismissed error suppressed
  // until the domain actually recovers (clearPlaybackErrorDedup is called).
}
