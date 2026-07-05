let _localMediaType = 'Local';

function _clearLocalPlayerMeta() {
  const ctx = document.getElementById('local-player-context');
  if (ctx) {
    ctx.textContent = '';
    ctx.style.display = 'none';
  }

  const artWrap = document.getElementById('local-player-art-wrap');
  const art = document.getElementById('local-player-art');
  if (art) art.removeAttribute('src');
  if (artWrap) artWrap.style.display = 'none';
}

function setLocalPodcastMeta(showTitle, artworkUrl) {
  const ctx = document.getElementById('local-player-context');
  const label = (showTitle || '').trim();
  if (ctx) {
    ctx.textContent = label;
    ctx.style.display = label ? '' : 'none';
  }

  const artWrap = document.getElementById('local-player-art-wrap');
  const art = document.getElementById('local-player-art');
  const url = (artworkUrl || '').trim();
  if (!artWrap || !art) return;

  if (!url) {
    art.removeAttribute('src');
    artWrap.style.display = 'none';
    return;
  }

  art.src = url;
  artWrap.style.display = '';
}

function _postLog(msg) {
  fetch('/api/log', { method: 'POST', body: msg }).catch(() => {});
}

function _closeNonLocalPlayersForLocalStart() {
  if (typeof stopMusicPlaybackPoll === 'function') stopMusicPlaybackPoll();
  if (typeof stopMusicStatusPoll === 'function') stopMusicStatusPoll();
  if (typeof stopRadioStatusPoll === 'function') stopRadioStatusPoll();
  if (typeof stopPolling === 'function') stopPolling();
  if (typeof stopSse === 'function') stopSse();

  if (typeof _clearMusicPlaybackVisuals === 'function') _clearMusicPlaybackVisuals();
  const musicBar = document.getElementById('music-player-bar');
  if (musicBar) musicBar.style.display = 'none';

  document.body.classList.remove('music-player-docked');
  document.body.classList.remove('radio-player-docked');
  document.body.classList.remove('desktop-player-docked');
  document.body.classList.remove('tablet-docked');
  document.body.classList.remove('phone-playing');

  if (typeof setPlayerBarVisible === 'function') setPlayerBarVisible(false);
  if (typeof updatePlayingCard === 'function') updatePlayingCard(null, playingPath);
  if (typeof playingPath !== 'undefined') playingPath = null;

  Promise.allSettled([
    fetch('/api/stop'),
    fetch('/api/music/stop'),
    fetch('/api/music/queue-next'),
    fetch('/api/radio/stop', { method: 'POST' })
  ]).catch(() => {});
}

async function closeLocalPlayerForNonLocalPlayback() {
  const isDocked = document.body.classList.contains('local-player-docked');
  if (!isDocked && _localMediaType === 'Local') return;

  if (typeof localStop === 'function') {
    await localStop();
    return;
  }

  document.body.classList.remove('local-player-docked');
  _clearLocalPlayerMeta();
  _localMediaType = 'Local';
}

function localPlay(url, name, type) {
  _closeNonLocalPlayersForLocalStart();

  _localMediaType = type || 'Local';
  _postLog(`[LOCAL-PLAY] localPlay() called — type=${_localMediaType} name='${name}' url=${url}`);
  const a = _getLocalAudio();
  if (!a) {
    _postLog(`[LOCAL-PLAY] ERROR: audio element not found — cannot play ${type}: '${name}'`);
    return;
  }
  _postLog(`[LOCAL-PLAY] setting src and calling play() — muted=${a.muted} volume=${a.volume}`);
  a.src = url;
  a.play().catch((err) => {
    const msg = `[LOCAL-PLAY] play() rejected for '${name}': ${err && err.message ? err.message : err}`;
    _postLog(msg);
    fetch('/api/local-playing', { method: 'POST', body: 'false' }).catch(() => {});
    const s = document.getElementById('local-player-state');
    if (s) s.textContent = 'Play error: ' + (err && err.message ? err.message : String(err));
    const errMsg = err && err.message ? err.message : String(err);
    if (typeof showPlaybackErrorPopup === 'function') {
      showPlaybackErrorPopup(type || 'Media', `"${name}" could not play.\n\n${errMsg}`);
    }
  });
  // Notify server AFTER play() — keeps the user-gesture scope clean
  setTimeout(() => fetch('/api/local-playing', { method: 'POST', body: 'true' }).catch(() => {}), 0);
  const nm = document.getElementById('local-player-name');
  if (nm) nm.textContent = name;
  if (_localMediaType !== 'Podcast') _clearLocalPlayerMeta();
  // Only show the generic local-player-bar when no domain-specific player bar exists.
  // Music and Radio already have dedicated player bars with their own controls.
  if (_localMediaType !== 'Music' && _localMediaType !== 'Radio') {
    document.body.classList.add('local-player-docked');
  } else {
    document.body.classList.remove('local-player-docked');
  }
  _localAudioUpdate();
  _postLog(`Playing ${_localMediaType}: '${name}' on Local`);
}
async function localToggle() {
  if (_localMediaType === 'PodcastServer' && typeof radioToggle === 'function') {
    await radioToggle();
    const s = document.getElementById('local-player-state');
    if (s) s.textContent = (typeof _radioIsPlaying !== 'undefined' && _radioIsPlaying) ? 'Playing on server' : 'Paused on server';
    return;
  }

  const a = _getLocalAudio();
  if (!a) return;
  if (a.paused) a.play().catch(() => {});
  else a.pause();
}
async function localStop() {
  if (_localMediaType === 'PodcastServer') {
    if (typeof radioDismiss === 'function') await radioDismiss();
    document.body.classList.remove('local-player-docked');
    _clearLocalPlayerMeta();
    _localSeekDragging = false;
    _localMediaType = 'Local';
    return;
  }

  const caller = (new Error()).stack ? (new Error()).stack.split('\n').slice(1,3).join(' | ') : 'unknown';
  const a = _getLocalAudio();
  const wasActive = a && a.src && !a.paused;
  _postLog(`[LOCAL-STOP] called — wasActive=${wasActive} caller: ${caller}`);
  if (a) {
    a.pause();
    a.src = '';
  }
  fetch('/api/local-playing', { method: 'POST', body: 'false' }).catch(() => {});
  document.body.classList.remove('local-player-docked');
  _localSeekDragging = false;
  _clearLocalPlayerMeta();
  if (wasActive) _postLog(`Stopping ${_localMediaType} on Local`);
  _localMediaType = 'Local';
}

// Plays a short 440 Hz beep via Web Audio API — completely independent of the <audio> element
// and the stream proxy. Used to verify that the browser can produce sound at all.
function localAudioBeepTest() {
  try {
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) { _postLog('[LOCAL-BEEP] AudioContext not supported'); return; }
    const ctx = new AC();
    // Resume in case the context is suspended (autoplay policy)
    ctx.resume().then(() => {
      const osc  = ctx.createOscillator();
      const gain = ctx.createGain();
      osc.type = 'sine';
      osc.frequency.value = 440;
      gain.gain.setValueAtTime(0.4, ctx.currentTime);
      gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + 0.8);
      osc.connect(gain);
      gain.connect(ctx.destination);
      osc.start(ctx.currentTime);
      osc.stop(ctx.currentTime + 0.8);
      osc.onended = () => { ctx.close(); _postLog('[LOCAL-BEEP] beep finished — if you heard it, Web Audio output is working'); };
      _postLog(`[LOCAL-BEEP] beep started — acState=${ctx.state}`);
    }).catch((err) => { _postLog(`[LOCAL-BEEP] resume failed: ${err}`); });
  } catch (err) {
    _postLog(`[LOCAL-BEEP] error: ${err}`);
  }
}

// ===== BOOTSTRAP: Initialize UI based on current URL route (all modules now loaded) =====
function _executeBootstrap() {
  try {
    console.log('[BOOTSTRAP] Starting initialization (all modules loaded)');
    _bootstrapFromRoute();
    refreshLibraryStatus();
    refreshThumbnailStatus();
    setInterval(refreshLibraryStatus, 2500);
    setInterval(refreshThumbnailStatus, 2500);
    updateVolumeIcon(parseFloat(document.getElementById('video-combined-vol')?.value) || 0);
    syncSpeedChips(currentPlaybackSpeed);
    console.log('[BOOTSTRAP] Initialization complete');
  } catch (err) {
    console.error('[BOOTSTRAP] Initialization failed:', err);
  }
}

// Execute bootstrap when DOM is ready
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', _executeBootstrap);
} else {
  _executeBootstrap();
}
