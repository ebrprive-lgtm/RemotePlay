let _localMediaType = 'Local';
let _localPlaybackGeneration = 0;
let _localPodcastResumeCleanup = null;

function _clearLocalPlayerMeta() {
  // Legacy local-player-bar metadata was removed. Keep as a no-op for compatibility.
}

function setLocalPodcastMeta(showTitle, artworkUrl) {
  // Legacy local-player-bar metadata was removed. Keep as a no-op for compatibility.
}

function _postLog(msg) {
  fetch('/api/log', { method: 'POST', body: msg }).catch(() => {});
}

function _cancelLocalPodcastResume() {
  const cleanup = _localPodcastResumeCleanup;
  _localPodcastResumeCleanup = null;
  if (cleanup) cleanup();
}

function _prepareLocalPodcastResume(audio, requestedPosition, generation) {
  audio.muted = true;
  let completed = false;
  let timeoutId = null;
  let metadataHandled = false;

  const cleanup = () => {
    if (timeoutId !== null) {
      clearTimeout(timeoutId);
      timeoutId = null;
    }
    audio.removeEventListener('loadedmetadata', onLoadedMetadata);
    audio.removeEventListener('seeked', finish);
    audio.removeEventListener('error', finish);
  };
  const finish = () => {
    if (completed) return;
    completed = true;
    cleanup();
    if (_localPodcastResumeCleanup === finish) _localPodcastResumeCleanup = null;
    if (generation === _localPlaybackGeneration && _localMediaType === 'Podcast') {
      audio.muted = false;
    }
  };
  const onLoadedMetadata = () => {
    metadataHandled = true;
    if (timeoutId !== null) {
      clearTimeout(timeoutId);
      timeoutId = null;
    }
    if (generation !== _localPlaybackGeneration || _localMediaType !== 'Podcast') {
      finish();
      return;
    }
    if (requestedPosition <= 0.5) {
      finish();
      return;
    }

    const duration = audio.duration;
    if (!Number.isFinite(duration) || duration <= 0) {
      console.warn('Could not restore local podcast position because the episode duration is unavailable.');
      if (typeof setStatus === 'function') {
        setStatus('Could not restore the podcast position; playback is starting from the beginning.');
      }
      finish();
      return;
    }

    const target = Math.min(requestedPosition, Math.max(0, duration - 0.25));
    if (Math.abs(audio.currentTime - target) < 0.25) {
      finish();
      return;
    }

    audio.addEventListener('seeked', finish, { once: true });
    try {
      audio.currentTime = target;
    } catch (error) {
      audio.removeEventListener('seeked', finish);
      console.warn('Could not restore local podcast position', error);
      if (typeof setStatus === 'function') {
        setStatus('Could not restore the podcast position; playback is starting from the beginning.');
      }
      finish();
      return;
    }
    if (!audio.seeking) {
      finish();
      return;
    }
    timeoutId = setTimeout(finish, 1200);
  };

  _localPodcastResumeCleanup = finish;
  audio.addEventListener('loadedmetadata', onLoadedMetadata, { once: true });
  audio.addEventListener('error', finish, { once: true });
  timeoutId = setTimeout(() => {
    if (!metadataHandled && requestedPosition > 0.5) {
      console.warn('Timed out while restoring local podcast position.');
      if (typeof setStatus === 'function') {
        setStatus('Could not restore the podcast position; playback is starting from the beginning.');
      }
    }
    finish();
  }, 5000);
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
}

function _stopRemotePlayersForLocalStart() {
  const requests = [
    ['/api/stop'],
    ['/api/music/stop'],
    ['/api/music/queue-next'],
    ['/api/radio/stop', { method: 'POST' }]
  ];
  return Promise.allSettled(requests.map(([url, options]) =>
    fetch(url, options).then((response) => {
      if (!response.ok) throw new Error(`${url} returned ${response.status}.`);
    })
  ));
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

function localPlay(url, name, type, playerMetadata = null, options = {}) {
  _cancelLocalPodcastResume();
  const playbackGeneration = ++_localPlaybackGeneration;
  _localMediaType = type || 'Local';
  _closeNonLocalPlayersForLocalStart(_localMediaType);
  const a = _getLocalAudio();
  if (!a) {
    _postLog(`[LOCAL-PLAY] ERROR: audio element not found — cannot play ${type}: '${name}'`);
    return;
  }
  const playbackUrl = _localMediaType === 'Podcast'
    ? '/api/radio/stream-proxy?url=' + encodeURIComponent(url)
    : url;
  if ((_localMediaType === 'Music' || _localMediaType === 'Podcast')
      && typeof _ensureLocalBoostGraph === 'function') {
    _ensureLocalBoostGraph();
  }
  a.muted = options.startMuted === true;
  let podcastResumePosition = null;
  if (_localMediaType === 'Podcast'
      && playerMetadata
      && Object.prototype.hasOwnProperty.call(playerMetadata, 'startAt')) {
    const startAt = Number(playerMetadata.startAt);
    if (Number.isFinite(startAt) && startAt >= 0) {
      podcastResumePosition = startAt;
      a.muted = true;
    } else {
      console.warn('Ignoring invalid local podcast resume position', playerMetadata.startAt);
      a.muted = false;
    }
  }
  a.src = playbackUrl;
  if (podcastResumePosition !== null) {
    _prepareLocalPodcastResume(a, podcastResumePosition, playbackGeneration);
  }
  const playPromise = a.play();
  _postLog(`[LOCAL-PLAY] localPlay() called — type=${_localMediaType} name='${name}' url=${url}`);
  _postLog(`[LOCAL-PLAY] setting src and calling play() — muted=${a.muted} volume=${a.volume}`);
  const remoteStopPromise = _stopRemotePlayersForLocalStart();
  void remoteStopPromise.then((results) => {
    results.forEach((result) => {
      if (result.status === 'rejected') {
        console.error('Could not stop a server-side player before Local playback.', result.reason);
      }
    });
    if (!options.unmuteAfterServerStop || playbackGeneration !== _localPlaybackGeneration) return;
    if (results[1]?.status !== 'fulfilled') {
      if (typeof setStatus === 'function') {
        setStatus('Server Music could not be stopped; Local Music remains muted to prevent overlapping audio.');
      }
      return;
    }
    a.muted = false;
  });
  if (_localMediaType === 'Podcast' && typeof startWaveform === 'function') startWaveform();
  playPromise.catch((err) => {
    const msg = `[LOCAL-PLAY] play() rejected for '${name}': ${err && err.message ? err.message : err}`;
    _postLog(msg);
    if (_localMediaType === 'Podcast' && typeof stopWaveform === 'function') stopWaveform();
    fetch('/api/local-playing', { method: 'POST', body: 'false' }).catch(() => {});
    const errMsg = err && err.message ? err.message : String(err);
    const statusId = type === 'Music'
      ? 'music-player-announcement'
      : type === 'Radio' || type === 'Podcast'
        ? 'radio-player-announcement'
        : null;
    if (statusId && typeof _announcePlayerStatus === 'function') {
      _announcePlayerStatus(statusId, `${type.toLowerCase()}:error`, '');
    }
    if (typeof showPlaybackErrorPopup === 'function') {
      showPlaybackErrorPopup(type || 'Media', `"${name}" could not play.\n\n${errMsg}`);
    }
  });
  playPromise.then(
    () => {
      if (playbackGeneration === _localPlaybackGeneration) {
        fetch('/api/local-playing', { method: 'POST', body: 'true' }).catch(() => {});
      }
    },
    () => {}
  );
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
      updateRadioBar(resolvedName, country, tag, true, station, playerMetadata);
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
  if (a.paused) {
    if ((_localMediaType === 'Music' || _localMediaType === 'Podcast')
        && typeof _ensureLocalBoostGraph === 'function') {
      _ensureLocalBoostGraph();
    }
    a.play().catch(() => {});
  } else {
    a.pause();
  }
}
async function localStop(keepPlayerVisible = false, options = {}) {
  const preservePodcastQueue = options.preservePodcastQueue === true;
  _cancelLocalPodcastResume();
  if (_localMediaType === 'PodcastServer') {
    if (!preservePodcastQueue && typeof podcastPlaybackStopped === 'function') podcastPlaybackStopped();
    if (typeof radioDismiss === 'function') {
      await radioDismiss({ preservePodcastQueue: true });
    }
    document.body.classList.remove('podcast-player-mode');
    _clearLocalPlayerMeta();
    _localSeekDragging = false;
    _localMediaType = 'Local';
    if (typeof _syncPodcastPlaybackControls === 'function') _syncPodcastPlaybackControls();
    return;
  }

  if (_localMediaType === 'Podcast'
      && !preservePodcastQueue
      && typeof podcastPlaybackStopped === 'function') {
    podcastPlaybackStopped();
  }

  const caller = (new Error()).stack ? (new Error()).stack.split('\n').slice(1,3).join(' | ') : 'unknown';
  const stoppedMediaType = _localMediaType;
  const a = _getLocalAudio();
  const wasActive = a && a.src && !a.paused;
  _postLog(`[LOCAL-STOP] called — wasActive=${wasActive} keepPlayerVisible=${keepPlayerVisible} caller: ${caller}`);
  if (a) {
    a.pause();
    a.src = '';
  }
  const isMusic = stoppedMediaType === 'Music';
  const isRadioPlayer = stoppedMediaType === 'Radio' || stoppedMediaType === 'Podcast';
  if (isMusic || isRadioPlayer) {
    const bar = document.getElementById(isMusic ? 'music-player-bar' : 'radio-player-bar');
    if (bar) bar.setAttribute('aria-busy', 'false');
    const playButton = document.getElementById(isMusic ? 'music-btn-play' : 'radio-btn-play');
    if (playButton) {
      const label = isMusic ? 'Play music' : `Play ${stoppedMediaType.toLowerCase()} playback`;
      playButton.innerHTML = '&#9654; Play';
      playButton.setAttribute('aria-label', label);
      playButton.setAttribute('aria-pressed', 'false');
      playButton.title = label;
    }
    const status = document.getElementById(
      isMusic ? 'music-bar-state-status' : 'radio-bar-reconnect-status'
    );
    if (status) {
      status.hidden = true;
      status.textContent = '';
    }
    if (typeof _announcePlayerStatus === 'function') {
      const label = isMusic ? 'Music' : stoppedMediaType;
      _announcePlayerStatus(
        isMusic ? 'music-player-announcement' : 'radio-player-announcement',
        `${stoppedMediaType.toLowerCase()}:stopped`,
        `${label} playback stopped.`
      );
    }
    if (isRadioPlayer && typeof _syncRadioBarTechRow === 'function') _syncRadioBarTechRow();
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
  if (typeof _syncPodcastPlaybackControls === 'function') _syncPodcastPlaybackControls();
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
