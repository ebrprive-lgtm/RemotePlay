let _localMediaType = 'Local';
function _postLog(msg) {
  fetch('/api/log', { method: 'POST', body: msg }).catch(() => {});
}
function localPlay(url, name, type) {
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
  document.body.classList.add('local-player-docked');
  _localAudioUpdate();
  _postLog(`Playing ${_localMediaType}: '${name}' on Local`);
}
function localToggle() {
  const a = _getLocalAudio();
  if (!a) return;
  if (a.paused) a.play().catch(() => {});
  else a.pause();
}
function localStop() {
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
