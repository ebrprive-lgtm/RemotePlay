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

// Decode a server-side Base64-encoded file path back to the raw FS path string.
function _decodePath(encoded) {
  try {
    return decodeURIComponent(escape(atob(encoded)));
  } catch {
    return null;
  }
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
  try {
    return btoa(unescape(encodeURIComponent(folder)));
  } catch {
    return null;
  }
}

  seekHoldInterval = null,
  seekHoldTriggered = false,
  suppressNextSeekTap = false;
let currentPlaybackSpeed = 1;
let statusFailures = 0;
let cardHoldTimer = null,
  cardHoldOpened = false;
let zoomDragging = false;
let pendingSearchAbort = null;
let thumbnailObserver = null;

// View mode: 'grid' or 'list' per section, persisted in localStorage
const _viewMode = {
  video: localStorage.getItem('remotePlayViewVideo') || 'grid',
  music: localStorage.getItem('remotePlayViewMusic') || 'list',
  radio: localStorage.getItem('remotePlayViewRadio') || 'grid',
};
function _setViewMode(section, mode) {
  _viewMode[section] = mode;
  localStorage.setItem('remotePlayView' + section.charAt(0).toUpperCase() + section.slice(1), mode);
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
      const handoffName    = _radioCurrentName;
      const handoffCountry = typeof _radioCurrentCountry !== 'undefined' ? _radioCurrentCountry : '';
      const handoffTag     = typeof _radioCurrentTag     !== 'undefined' ? _radioCurrentTag     : '';
      const handoffStation = typeof _radioCurrentStation !== 'undefined' ? _radioCurrentStation : null;
      const proxyUrl = '/api/radio/stream-proxy?url=' + encodeURIComponent(_radioCurrentUrl);
      // Update UI state immediately (synchronous, in user-gesture scope)
      _radioIsPlaying = true;
      if (typeof updateRadioBar !== 'undefined')
        updateRadioBar(handoffName, handoffCountry, handoffTag, true, handoffStation);
      // Start local audio NOW — any fetch() before this would consume the user-gesture token
      // and Chromium would silently block audio output.
      localPlay(proxyUrl, handoffName || 'Radio', 'Radio');
      // Notify server and log AFTER localPlay() — deferred so we never race the gesture
      setTimeout(() => {
        fetch('/api/local-playing', { method: 'POST', body: 'true' }).catch(() => {});
        fetch('/api/radio/stop').catch(() => {});
        fetch('/api/log', { method: 'POST', body: 'Switched playback mode to Local' }).catch(() => {});
      }, 0);
    } else {
      // No active playback to hand off — just log the mode switch
      setTimeout(() => {
        fetch('/api/local-playing', { method: 'POST', body: 'true' }).catch(() => {});
        fetch('/api/log', { method: 'POST', body: 'Switched playback mode to Local' }).catch(() => {});
      }, 0);
    }
    // If music is playing on the server, hand it off to local immediately
    if (typeof musicIsPlaying !== 'undefined' && musicIsPlaying && typeof musicCurrentPath !== 'undefined' && musicCurrentPath) {
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
    }
  } else {
    fetch('/api/log', { method: 'POST', body: 'Switched playback mode to Server' }).catch(() => {});
    fetch('/api/local-playing', { method: 'POST', body: 'false' }).catch(() => {});
    // Capture position and state BEFORE localStop() wipes the audio element (sets src='')
    const a = typeof _getLocalAudio !== 'undefined' ? _getLocalAudio() : null;
    const localType = typeof _localMediaType !== 'undefined' ? _localMediaType : '';
    const wasPlaying = a && a.src && !a.paused;
    const localResumePos = (a && localType === 'Music') ? (a.currentTime || 0) : 0;
    // Use localStop() so the "Stopping X on Local" log line is emitted correctly
    if (typeof localStop !== 'undefined') localStop();
    // Leave the Web Audio boost graph running — localStop() already cleared a.src so the
    // MediaElementSource produces silence. Suspending risks a resume-race where the context
    // is still in the suspended→running transition when the next localPlay() calls a.play(),
    // which Chrome rejects with MEDIA_ERR_SRC_NOT_SUPPORTED.
    if (wasPlaying) {
      if (localType === 'Radio' && typeof _radioCurrentUrl !== 'undefined' && _radioCurrentUrl) {
        // Resume radio on server
        fetch('/api/radio/play?' + new URLSearchParams({ url: _radioCurrentUrl, name: _radioCurrentName || '' }))
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
          if (typeof startMusicPlaybackPoll !== 'undefined') startMusicPlaybackPoll();
        }).catch(() => {});
      }
    }
  }
}
// Apply saved state on load
document.addEventListener('DOMContentLoaded', () => {
  // Always start in server mode — local is a session gesture, not a persisted preference.
  localStorage.setItem('remotePlayLocal', 'false');
  setPlayLocal(false);

  // On load, switch to whichever domain is currently playing on this server instance.
  // This means navigating to an instance always lands on the active player, not the default tab.
  (async () => {
    try {
      const [videoRes, musicRes, radioRes] = await Promise.all([
        fetch('/api/status').then(r => r.ok ? r.json() : null).catch(() => null),
        fetch('/api/music/status').then(r => r.ok ? r.json() : null).catch(() => null),
        fetch('/api/radio/playback-status').then(r => r.ok ? r.json() : null).catch(() => null),
      ]);
      let targetMode = null;
      if (musicRes && (musicRes.isPlaying || musicRes.isPaused)) targetMode = 'music';
      else if (radioRes && radioRes.isPlaying) targetMode = 'radio';
      else if (videoRes && (videoRes.isPlaying || videoRes.isPaused)) targetMode = 'video';

      if (!targetMode || targetMode === 'video') return; // video is already default; nothing to do

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
          if (typeof startRadioStatusPoll === 'function') startRadioStatusPoll();
        }
      }

      _applyActiveMode();
      // Re-apply after the initial video browse response has rendered (race fix)
      setTimeout(_applyActiveMode, 900);
    } catch {}
  })();

  // Initialize the combined volume/boost slider gradient to its default value.
  if (typeof musicCombinedSlider === 'function') musicCombinedSlider(0.8);
  if (typeof setVideoCombinedSlider === 'function') setVideoCombinedSlider(1.0);

  // Keep --header-h in sync so sticky elements below the header don't overlap it.
  const hdr = document.querySelector('header');
  if (hdr && typeof ResizeObserver !== 'undefined') {
    const ro = new ResizeObserver(entries => {
      const h = entries[0].borderBoxSize?.[0]?.blockSize ?? entries[0].contentRect.height;
      document.documentElement.style.setProperty('--header-h', h + 'px');
    });
    ro.observe(hdr);
  }
});
function setTheme(theme, save = true) {
  document.body.classList.remove(
    'theme-amoled',
    'theme-blue',
    'theme-sunset',
    'theme-neon',
    'theme-forest',
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
    'theme-high-contrast',
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

async function pollStatus() {
  try {
    const res = await fetch('/api/status');
    if (!res.ok) {
      setConnectionStatus('Retrying connection...', true);
      statusFailures++;
      return;
    }
    const s = await res.json();
    statusFailures = 0;
    setConnectionStatus('Connected', false, true);
    updateDiagnosticsIndicator(s.lastError ? 'error' : 'ok');
    const bar = document.getElementById('now-playing-bar');
    updateQueueControls(s);
    const hasQueue = Array.isArray(s.queue) && s.queue.length > 0;
    lastQueue = hasQueue ? s.queue : [];
    updateStopBtn(s.isPlaying, hasQueue);
    updateAdjacentButtons(s);
    if (s.isPlaying || hasQueue) {
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
      document.getElementById('player-title').textContent = (s.title || 'Now playing').replace(
        /^\s*[▶⏸]\s*/,
        ''
      );
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
      document.getElementById('brightness').value = brightness;
      document.getElementById('brightness-label').textContent = Math.round(brightness * 100) + '%';
      const rawSaturation = Number(s.saturation);
      const saturation = Math.max(
        0,
        Math.min(2, Number.isFinite(rawSaturation) ? rawSaturation : 1)
      );
      document.getElementById('saturation').value = saturation;
      document.getElementById('saturation-label').textContent = Math.round(saturation * 100) + '%';
      const rawZoom = Number(s.zoom);
      const zoom = Math.max(1, Math.min(2, Number.isFinite(rawZoom) ? rawZoom : 1));
      if (!zoomDragging) {
        document.getElementById('zoom').value = zoom;
        document.getElementById('zoom-label').textContent = Math.round(zoom * 100) + '%';
      }
      const err = document.getElementById('error');
      if (s.lastError) {
        err.style.display = 'none';
        showPlaybackErrorPopup('Video', s.lastError);
      } else {
        err.style.display = 'none';
        err.textContent = '';
        // Video is playing without error — allow the popup to reappear if a new error occurs
        clearPlaybackErrorDedup('Video');
      }
      updateResumePrompt(s);
      const progress = document.getElementById('progress');
      if (s.duration > 0) {
        progress.max = s.duration;
        if (!seekDragging) progress.value = s.position;
      }
      document.getElementById('time-label').textContent = fmt(s.position) + ' / ' + fmt(s.duration);
      if (s.duration > 0) updateCardProgress(s.filePath, s.position, s.duration);
      currentPlaybackSpeed = Math.max(0.5, Math.min(2, Number(s.playbackSpeed) || 1));
      syncSpeedChips(currentPlaybackSpeed);
      updateTrackControls(s);
      document.getElementById('player-meta').textContent = buildPlayerMeta(s, volume, boostAmount);
      requestWakeLock();
      if (isPhoneRemoteOnly) applyPhonePlaybackState(true);
      else applyDesktopDockedLayout(true);
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
      if (hasQueue) {
        document.body.classList.add('desktop-player-docked');
      } else {
        applyDesktopDockedLayout(false);
        setPlayerBarVisible(false);
      }
    }
  } catch (e) {
    statusFailures++;
    setConnectionStatus(
      statusFailures > 2 ? 'Connection lost - retrying...' : 'Retrying connection...',
      true
    );
    updateDiagnosticsIndicator('error');
  }
}

// ── Playback error popup ─────────────────────────────────────────────────────
// Tracks the last error shown per domain to avoid repeat popups.
const _pbeLastError = { video: '', music: '', radio: '' };
let _pbeOverlayEl = null;

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
  overlay.setAttribute('role', 'dialog');
  overlay.setAttribute('aria-modal', 'true');

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
  Object.assign(icon.style, { fontSize: '1.3rem', color: 'var(--accent, #e94560)', flexShrink: '0' });

  const domainEl = document.createElement('span');
  domainEl.id = 'pbe-domain';
  Object.assign(domainEl.style, { fontSize: '.78rem', fontWeight: '700', opacity: '.7', letterSpacing: '.05em', textTransform: 'uppercase' });

  const title = document.createElement('h2');
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
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closePlaybackErrorPopup(); });

  // Store direct references so showPlaybackErrorPopup never needs getElementById
  overlay._domainEl = domainEl;
  overlay._msgEl = msgEl;
  overlay._techEl = techEl;

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
  // Do NOT clear _pbeLastError here — keeps the dismissed error suppressed
  // until the domain actually recovers (clearPlaybackErrorDedup is called).
}
