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
let seekHoldTimer = null,
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
  if (section === 'video' && currentData) render(currentData, Boolean(currentData.query));
  else if (section === 'music' && currentMusicData)
    renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
  else if (section === 'radio')
    renderRadioCards(_radioStations, _radioStations.length === _radioPageSize * (_radioPage + 1));
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
      fetch('/api/music/stop').then(() => {
        if (typeof musicIsPlaying !== 'undefined') musicIsPlaying = false;
        if (typeof stopMusicPlaybackPoll !== 'undefined') stopMusicPlaybackPoll();
        // Keep the music bar visible — localPlay manages its own player bar
        localPlay('/api/music/stream?path=' + encodeURIComponent(musicCurrentPath), title, 'Music');
        // Seek the local audio element to the saved position BEFORE it becomes audible.
        // Mute briefly, then seek on canplay and unmute so there's no flash of pos-0 audio.
        if (resumePos > 0.5) {
          const a = typeof _getLocalAudio !== 'undefined' ? _getLocalAudio() : null;
          if (a) {
            a.muted = true;
            const seekWhenReady = () => {
              a.currentTime = resumePos;
              a.removeEventListener('canplay', seekWhenReady);
              // Unmute after the seek is committed — use seeked event for accuracy
              const onSeeked = () => { a.muted = false; a.removeEventListener('seeked', onSeeked); };
              a.addEventListener('seeked', onSeeked);
              // Fallback: unmute after 400 ms in case seeked never fires
              setTimeout(() => { a.muted = false; a.removeEventListener('seeked', onSeeked); }, 400);
            };
            if (a.readyState >= 2) {
              seekWhenReady();
            } else {
              a.addEventListener('canplay', seekWhenReady);
            }
          }
        }
      }).catch(() => {});
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
    // Tear down the Web Audio boost graph so the <audio> element is released for normal use
    if (typeof _localBoostCtx !== 'undefined' && _localBoostCtx) {
      try { _localBoostCtx.close(); } catch {}
      _localBoostCtx = null;
      _localBoostGain = null;
    }
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
    'theme-high-contrast'
  );
  if (theme && theme !== 'default') document.body.classList.add('theme-' + theme);
  const selector = document.getElementById('theme-select');
  if (selector) selector.value = theme || 'default';
  if (save) localStorage.setItem('remotePlayTheme', theme || 'default');
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
  const slider = document.getElementById('volume');
  if (!slider) return;
  const current = Math.max(0, Math.min(1, Number(slider.value) || 0));
  const next = Math.max(0, Math.min(1, current + delta));
  slider.value = next.toFixed(2);
  setVolume(slider.value);
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
      document.getElementById('volume').value = volume;
      document.getElementById('volume-label').textContent = Math.round(volume * 100) + '%';
      if (volume > 0.001) lastVolumeBeforeMute = volume;
      updateVolumeIcon(volume);
      const boostAmount = Math.max(0, Math.min(1, (Number(s.audioBoost) || 1) - 1));
      document.getElementById('audio-boost').value = boostAmount;
      document.getElementById('audio-boost-label').textContent =
        Math.round(boostAmount * 100) + '%';
      if (boostAmount > 0.001) lastBoostBeforeMute = boostAmount;
      updateAudioBoostIcon(boostAmount);
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
        err.style.display = 'block';
        err.textContent = 'Playback error: ' + s.lastError;
      } else {
        err.style.display = 'none';
        err.textContent = '';
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
