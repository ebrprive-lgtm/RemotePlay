let _localMediaType = 'Local';

function _clearLocalPlayerMeta() {
  // Legacy local-player-bar metadata was removed. Keep as a no-op for compatibility.
}

function setLocalPodcastMeta(showTitle, artworkUrl) {
  // Legacy local-player-bar metadata was removed. Keep as a no-op for compatibility.
}

function _postLog(msg) {
  fetch('/api/log', { method: 'POST', body: msg }).catch(() => {});
}

function _closeNonLocalPlayersForLocalStart(localType) {
  const isMusic = localType === 'Music';
  const isRadio = localType === 'Radio';

  if (typeof stopMusicPlaybackPoll === 'function') stopMusicPlaybackPoll();
  if (typeof stopMusicStatusPoll === 'function') stopMusicStatusPoll();
  if (typeof stopRadioStatusPoll === 'function') stopRadioStatusPoll();
  if (typeof stopPolling === 'function') stopPolling();
  if (typeof stopSse === 'function') stopSse();

  if (!isMusic) {
    if (typeof _clearMusicPlaybackVisuals === 'function') _clearMusicPlaybackVisuals();
    const musicBar = document.getElementById('music-player-bar');
    if (musicBar) musicBar.style.display = 'none';
  }

  if (!isMusic) document.body.classList.remove('music-player-docked');
  if (!isRadio) document.body.classList.remove('radio-player-docked');
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
  if (_localMediaType === 'Local') return;

  if (typeof localStop === 'function') {
    await localStop();
    return;
  }

  document.body.classList.remove('music-player-docked');
  document.body.classList.remove('music-local-mode');
  document.body.classList.remove('radio-player-docked');
  document.body.classList.remove('podcast-player-mode');
  _clearLocalPlayerMeta();
  _localMediaType = 'Local';
}

function localPlay(url, name, type) {
  _localMediaType = type || 'Local';
  _closeNonLocalPlayersForLocalStart(_localMediaType);
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
    const errMsg = err && err.message ? err.message : String(err);
    if (typeof showPlaybackErrorPopup === 'function') {
      showPlaybackErrorPopup(type || 'Media', `"${name}" could not play.\n\n${errMsg}`);
    }
  });
  // Notify server AFTER play() — keeps the user-gesture scope clean
  setTimeout(() => fetch('/api/local-playing', { method: 'POST', body: 'true' }).catch(() => {}), 0);
  if (_localMediaType !== 'Podcast') _clearLocalPlayerMeta();

  // Keep the same domain player UI for local playback.
  if (_localMediaType === 'Music') {
    const musicBar = document.getElementById('music-player-bar');
    if (musicBar) musicBar.style.display = 'flex';
    document.body.classList.add('music-player-docked');
    document.body.classList.add('music-local-mode');
    document.body.classList.remove('radio-player-docked');
    document.body.classList.remove('podcast-player-mode');
  } else {
    const isPodcast = _localMediaType === 'Podcast' || _localMediaType === 'PodcastServer';
    const radioBar = document.getElementById('radio-player-bar');
    if (radioBar) radioBar.style.display = 'flex';
    document.body.classList.add('radio-player-docked');
    document.body.classList.remove('music-player-docked');
    document.body.classList.toggle('podcast-player-mode', isPodcast);
    if (typeof updateRadioBar === 'function') {
      const resolvedName = name || (isPodcast ? 'Podcast episode' : 'Radio');
      const country = isPodcast ? '' : (typeof _radioCurrentCountry !== 'undefined' ? _radioCurrentCountry : '');
      const tag = isPodcast ? 'Podcast' : (typeof _radioCurrentTag !== 'undefined' ? _radioCurrentTag : '');
      const station = isPodcast ? null : (typeof _radioCurrentStation !== 'undefined' ? _radioCurrentStation : null);
      updateRadioBar(resolvedName, country, tag, true, station);
    }
  }
  _localAudioUpdate();
  _postLog(`Playing ${_localMediaType}: '${name}' on Local`);
}
async function localToggle() {
  if (_localMediaType === 'PodcastServer' && typeof radioToggle === 'function') {
    await radioToggle();
    return;
  }

  const a = _getLocalAudio();
  if (!a) return;
  if (a.paused) a.play().catch(() => {});
  else a.pause();
}
async function localStop(keepPlayerVisible = false) {
  if (_localMediaType === 'PodcastServer') {
    if (typeof radioDismiss === 'function') await radioDismiss();
    document.body.classList.remove('podcast-player-mode');
    _clearLocalPlayerMeta();
    _localSeekDragging = false;
    _localMediaType = 'Local';
    return;
  }

  const caller = (new Error()).stack ? (new Error()).stack.split('\n').slice(1,3).join(' | ') : 'unknown';
  const a = _getLocalAudio();
  const wasActive = a && a.src && !a.paused;
  _postLog(`[LOCAL-STOP] called — wasActive=${wasActive} keepPlayerVisible=${keepPlayerVisible} caller: ${caller}`);
  if (a) {
    a.pause();
    a.src = '';
  }
  fetch('/api/local-playing', { method: 'POST', body: 'false' }).catch(() => {});
  if (_localMediaType === 'Music') {
    // Only hide the player if not keeping it visible for handoff
    if (!keepPlayerVisible) {
      document.body.classList.remove('music-player-docked');
    }
    document.body.classList.remove('music-local-mode');
  } else if (_localMediaType === 'Podcast') {
    if (!keepPlayerVisible) {
      document.body.classList.remove('radio-player-docked');
    }
    document.body.classList.remove('podcast-player-mode');
  } else if (_localMediaType === 'Radio') {
    // Radio handoff - keep player visible if requested
    if (!keepPlayerVisible) {
      document.body.classList.remove('radio-player-docked');
    }
  }
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
