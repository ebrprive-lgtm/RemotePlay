let _radioStatusPollId = null;
let _radioElapsedBase = 0; // server-reported elapsed at last poll
let _radioElapsedPollTime = 0; // performance.now() when that poll landed
let _radioElapsedTick = null;
let _radioRetryCount = 0;
let _radioWaveformId = null;
let _radioWaveformPhase = 0;

// ── Stream title history (feature #3) ────────────────────────────────
const TITLE_HISTORY_KEY = 'radioTitleHistory';
const TITLE_HISTORY_MAX = 20;
let _lastLoggedTitle = '';          // avoid duplicate entries on consecutive polls

function _pushTitleHistory(stationName, title) {
  if (!title || title === _lastLoggedTitle) return;
  _lastLoggedTitle = title;
  let history;
  try { history = JSON.parse(localStorage.getItem(TITLE_HISTORY_KEY) || '[]'); } catch { history = []; }
  history.unshift({ station: stationName, title, ts: Date.now() });
  if (history.length > TITLE_HISTORY_MAX) history.length = TITLE_HISTORY_MAX;
  try { localStorage.setItem(TITLE_HISTORY_KEY, JSON.stringify(history)); } catch {}
  _renderTitleHistoryDropdown();
}

function _renderTitleHistoryDropdown() {
  const list = document.getElementById('radio-title-history-list');
  if (!list) return;
  let history;
  try { history = JSON.parse(localStorage.getItem(TITLE_HISTORY_KEY) || '[]'); } catch { history = []; }
  if (!history.length) {
    list.innerHTML = '<li class="radio-title-history-empty">No titles logged yet.</li>';
    return;
  }
  list.innerHTML = history.map(e => {
    const d = new Date(e.ts);
    const time = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    return `<li class="radio-title-history-item" title="${escHtml(e.station)}">
      <span class="radio-title-history-time">${escHtml(time)}</span>
      <span class="radio-title-history-text">${escHtml(e.title)}</span>
    </li>`;
  }).join('');
}

function radioShowTitleHistory() {
  const panel = document.getElementById('radio-title-history-panel');
  if (!panel) return;
  const isVisible = panel.style.display !== 'none';
  if (isVisible) { panel.style.display = 'none'; return; }
  _renderTitleHistoryDropdown();
  panel.style.display = 'block';
}

// Close the history panel when clicking outside it
document.addEventListener('click', (e) => {
  const panel = document.getElementById('radio-title-history-panel');
  const btn = document.getElementById('radio-title-history-btn');
  if (panel && panel.style.display !== 'none' &&
      !panel.contains(e.target) && e.target !== btn) {
    panel.style.display = 'none';
  }
});

function startRadioStatusPoll() {
  stopRadioStatusPoll();
  _radioStatusPollId = setInterval(radioStatusTick, 2500);
}
function stopRadioStatusPoll() {
  if (_radioStatusPollId) {
    clearInterval(_radioStatusPollId);
    _radioStatusPollId = null;
  }
  stopElapsedTick();
  stopWaveform();
}

// ── Elapsed clock ────────────────────────────────────────────────────
function startElapsedTick() {
  stopElapsedTick();
  _radioElapsedTick = setInterval(() => {
    if (!_radioIsPlaying) return;
    const secNow =
      _radioElapsedBase + Math.round((performance.now() - _radioElapsedPollTime) / 1000);
    const el = document.getElementById('radio-bar-elapsed');
    if (el) {
      el.textContent = fmtSec(secNow);
      el.style.display = '';
    }
  }, 1000);
}
function stopElapsedTick() {
  if (_radioElapsedTick) {
    clearInterval(_radioElapsedTick);
    _radioElapsedTick = null;
  }
}
function fmtSec(s) {
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = Math.floor(s % 60);
  if (h > 0) return `${h}:${String(m).padStart(2, '0')}:${String(sec).padStart(2, '0')}`;
  return `${m}:${String(sec).padStart(2, '0')}`;
}

// ── Waveform animator ────────────────────────────────────────────────
// In local mode: real Web Audio AnalyserNode → frequency bars.
// In server mode: simulated energy bars (existing animation).
let _waveformAudioCtx = null;
let _waveformAnalyser = null;
let _waveformSource = null;   // MediaElementAudioSourceNode

function _ensureWaveformAnalyser() {
  const audio = document.getElementById('local-audio');
  if (!audio || audio.paused || !audio.src) return null;
  try {
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return null;
    if (!_waveformAudioCtx) {
      _waveformAudioCtx = new AC();
    }
    if (_waveformAudioCtx.state === 'suspended') _waveformAudioCtx.resume().catch(() => {});
    if (!_waveformSource || _waveformSource._audio !== audio) {
      if (_waveformSource) { try { _waveformSource.disconnect(); } catch {} }
      _waveformSource = _waveformAudioCtx.createMediaElementSource(audio);
      _waveformSource._audio = audio;
      if (!_waveformAnalyser) {
        _waveformAnalyser = _waveformAudioCtx.createAnalyser();
        _waveformAnalyser.fftSize = 64;
        _waveformAnalyser.smoothingTimeConstant = 0.8;
      }
      _waveformSource.connect(_waveformAnalyser);
      _waveformAnalyser.connect(_waveformAudioCtx.destination);
    }
    return _waveformAnalyser;
  } catch { return null; }
}

function startWaveform() {
  const cv = document.getElementById('radio-waveform');
  if (!cv) return;
  cv.classList.add('active');
  stopWaveform();

  const analyser = isPlayLocal() ? _ensureWaveformAnalyser() : null;
  const freqData = analyser ? new Uint8Array(analyser.frequencyBinCount) : null;

  function draw() {
    _radioWaveformPhase += 0.07;
    const ctx = cv.getContext('2d');
    const W = cv.width, H = cv.height;
    ctx.clearRect(0, 0, W, H);
    const bars = 10;
    const bw = 4, gap = 2;
    const totalW = bars * (bw + gap) - gap;
    const x0 = Math.round((W - totalW) / 2);
    const accent =
      getComputedStyle(document.body).getPropertyValue('--player-accent').trim() || '#e94560';
    ctx.fillStyle = accent;

    if (analyser && freqData) {
      // Real frequency data from the local <audio> element
      analyser.getByteFrequencyData(freqData);
      const step = Math.floor(freqData.length / bars);
      for (let i = 0; i < bars; i++) {
        const energy = freqData[i * step] / 255;
        const h = Math.round(3 + energy * (H - 4));
        const y = Math.round((H - h) / 2);
        ctx.globalAlpha = 0.55 + energy * 0.45;
        ctx.fillRect(x0 + i * (bw + gap), y, bw, h);
      }
      ctx.globalAlpha = 1;
    } else {
      // Simulated animation (server-mode / no analyser available)
      for (let i = 0; i < bars; i++) {
        const t = _radioWaveformPhase + i * 0.6;
        const h = Math.round(4 + Math.abs(Math.sin(t)) * (H - 8));
        const y = Math.round((H - h) / 2);
        ctx.fillRect(x0 + i * (bw + gap), y, bw, h);
      }
    }
    _radioWaveformId = requestAnimationFrame(draw);
  }
  draw();
}
function stopWaveform() {
  if (_radioWaveformId) {
    cancelAnimationFrame(_radioWaveformId);
    _radioWaveformId = null;
  }
  const cv = document.getElementById('radio-waveform');
  if (cv) {
    cv.classList.remove('active');
    const ctx = cv.getContext('2d');
    ctx.clearRect(0, 0, cv.width, cv.height);
  }
}

// ── Health dot ───────────────────────────────────────────────────────
function setRadioHealthDot(state) {
  // 'ok'|'warn'|'error'|'off'
  const dot = document.getElementById('radio-health-dot');
  if (!dot) return;
  dot.className = 'radio-health-dot';
  if (state === 'ok') dot.classList.add('ok');
  else if (state === 'error') dot.classList.add('error');
  // 'warn' = default amber; 'off' = hidden via amber (same visual as warn)
}

async function radioStatusTick() {
  try {
    const r = await fetch('/api/radio/playback-status');
    if (!r.ok) {
      setRadioHealthDot('error');
      return;
    }
    const s = await r.json();
    const playing = s.isPlaying || s.IsPlaying || false;
    const stalled = s.isStalled || s.IsStalled || false;
    const name = s.stationName || s.StationName || _radioCurrentName || '';
    const url = s.stationUrl || s.StationUrl || '';
    const songTitle = s.streamTitle || s.StreamTitle || '';
    const elapsed = s.elapsedSeconds || s.ElapsedSeconds || 0;
    const err = s.error || s.Error || '';
    if (url) _radioCurrentUrl = url;

    // Update elapsed clock base
    if (playing && elapsed > 0) {
      _radioElapsedBase = elapsed;
      _radioElapsedPollTime = performance.now();
      if (!_radioElapsedTick) startElapsedTick();
    }

    // Health dot — in local mode, server reports not-playing so drive from local flag
    const effectivePlaying = isPlayLocal() ? _radioIsPlaying : playing;
    if (!effectivePlaying && err) {
      setRadioHealthDot('error');
      // Show themed popup with the server-reported error reason
      const stationLabel = name ? `"${name}"` : 'Station';
      showPlaybackErrorPopup('Radio', `${stationLabel} stopped playing.\n\n${err}`);
    } else if (stalled && !isPlayLocal()) {
      setRadioHealthDot('warn');
    } else if (effectivePlaying) {
      setRadioHealthDot('ok');
      // Playback is healthy — allow the error popup to reappear if a new error occurs
      if (typeof clearPlaybackErrorDedup === 'function') clearPlaybackErrorDedup('Radio');
    } else {
      setRadioHealthDot('warn');
    }

    // Song title marquee (server mode only — local streams rarely carry ICY metadata via proxy)
    const songEl = document.getElementById('radio-bar-song');
    const songTextEl = document.getElementById('radio-bar-song-text');
    if (songEl && songTextEl) {
      if (songTitle && playing) {
        songTextEl.textContent = songTitle;
        songEl.style.display = '';
        // Restart animation so new title is visible from the start
        songTextEl.style.animation = 'none';
        void songTextEl.offsetWidth; // reflow
        songTextEl.style.animation = '';
        _pushTitleHistory(name, songTitle);
      } else if (!isPlayLocal()) {
        songEl.style.display = 'none';
      }
    }

    // Waveform — show in local mode too (driven by _radioIsPlaying local flag)
    if (effectivePlaying && (!stalled || isPlayLocal())) {
      startWaveform();
    } else {
      stopWaveform();
    }

    // Auto-retry on stall (feature 13) — server mode only
    if (!isPlayLocal() && stalled && _radioCurrentUrl && _radioIsPlaying) {
      _radioRetryCount++;
      const delay = Math.min(3000 * _radioRetryCount, 15000);
      setTimeout(async () => {
        if (_radioIsPlaying && _radioCurrentUrl && !isPlayLocal()) {
          await fetch(
            '/api/radio/play?' +
              new URLSearchParams({ url: _radioCurrentUrl, name: _radioCurrentName })
          );
          _radioRetryCount = 0;
        }
      }, delay);
    } else if (!stalled) {
      _radioRetryCount = 0;
    }

    // Notify server audio is alive (so stall detection keeps ticking)
    if (playing) fetch('/api/radio/notify-alive').catch(() => {});

    // In local mode the bar state is managed by radioPlayStation/radioToggle — don't let
    // the server poll overwrite it (server reports not-playing when local is active).
    if (!isPlayLocal()) {
      updateRadioBar(name, _radioCurrentCountry, _radioCurrentTag, playing, _radioCurrentStation);
    }
    // Sync combined volume/boost slider from server state
    if (!isPlayLocal()) {
      const vol   = s.volume ?? s.Volume ?? 0.8;
      const boost = s.boost  ?? s.Boost  ?? 1.0;
      // Map back to combined slider position (0–1.0 = volume, 1.0–1.3 = boost zone)
      const combined = boost > 1.0 ? Math.min(1.3, 1.0 + (boost - 1) * (0.3 / 2)) : Math.min(1.0, vol);
      const cSlider = document.getElementById('radio-bar-combined');
      const cLabel  = document.getElementById('radio-combined-label');
      if (cSlider && Math.abs(parseFloat(cSlider.value) - combined) > 0.015) {
        cSlider.value = combined;
        if (combined > 1.0) {
          cSlider.classList.add('slider-boosting');
          const db = Math.round(20 * Math.log10(boost));
          if (cLabel) cLabel.textContent = '100% +' + db + 'dB';
        } else {
          cSlider.classList.remove('slider-boosting');
          if (cLabel) cLabel.textContent = Math.round(combined * 100) + '%';
        }
      }
    }
  } catch {}
}

async function radioToggleFav(btn, encStation) {
  try {
    const station = JSON.parse(decodeURIComponent(encStation));
    const r = await fetch('/api/radio/favorite', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(station),
    });
    if (!r.ok) return;
    const resp = await r.json();
    const isFav = resp.isFavorite || false;
    await radioLoadFavorites();
    // Re-render so removed favorites disappear from favorites tab, and hearts update elsewhere
    if (_radioTab === 'favorites') {
      renderRadioCards(_radioFavorites, false);
    } else {
      renderRadioCards(_radioStations, _radioStations.length === _radioPageSize * (_radioPage + 1));
    }
  } catch {}
}

// ── Local (tablet-side) playback ─────────────────────────────────────
let _localAudio = null;
let _localSeekDragging = false;
function _fmtTime(s) {
  if (!isFinite(s) || s < 0) return '0:00';
  const m = Math.floor(s / 60),
    sec = Math.floor(s % 60);
  return m + ':' + (sec < 10 ? '0' : '') + sec;
}
function _getLocalAudio() {
  if (!_localAudio) {
    _localAudio = document.getElementById('local-audio');
    if (_localAudio) {
      _localAudio.addEventListener('play',    () => { fetch('/api/log', { method:'POST', body:`[LOCAL-AUDIO] event=play src=${_localAudio.src}` }).catch(()=>{}); _localAudioUpdate(); });
      _localAudio.addEventListener('playing', () => {
        const vol   = _localAudio.volume;
        const muted = _localAudio.muted;
        const rs    = _localAudio.readyState;   // 0-4
        const ns    = _localAudio.networkState; // 0-3
        // Probe AudioContext state — a suspended AC silences output even when pipeline says 'playing'
        let acState = 'n/a';
        try { const AC = window.AudioContext || window.webkitAudioContext; if (AC) { const ctx = new AC(); acState = ctx.state; ctx.close(); } } catch (e) { acState = 'err'; }
        fetch('/api/log', { method:'POST', body:`[LOCAL-AUDIO] event=playing vol=${vol} muted=${muted} readyState=${rs} networkState=${ns} acState=${acState} currentTime=${_localAudio.currentTime.toFixed(2)}` }).catch(()=>{});
      });
      _localAudio.addEventListener('pause',   () => { fetch('/api/log', { method:'POST', body:`[LOCAL-AUDIO] event=pause src=${_localAudio.src}` }).catch(()=>{}); _localAudioUpdate(); });
      _localAudio.addEventListener('ended',   () => {
        fetch('/api/log', { method:'POST', body:`[LOCAL-AUDIO] event=ended` }).catch(()=>{});
        // Auto-advance to the next music track when playing locally; otherwise just stop.
        if (typeof _localMediaType !== 'undefined' && _localMediaType === 'Music'
            && typeof isPlayLocal === 'function' && isPlayLocal()
            && typeof musicNext === 'function') {
          musicNext();
        } else {
          localStop();
        }
      });
      _localAudio.addEventListener('stalled', () => { fetch('/api/log', { method:'POST', body:`[LOCAL-AUDIO] event=stalled` }).catch(()=>{}); });
      _localAudio.addEventListener('waiting', () => { fetch('/api/log', { method:'POST', body:`[LOCAL-AUDIO] event=waiting` }).catch(()=>{}); });
      _localAudio.addEventListener('canplay', () => { fetch('/api/log', { method:'POST', body:`[LOCAL-AUDIO] event=canplay` }).catch(()=>{}); });
      _localAudio.addEventListener('emptied', () => { fetch('/api/log', { method:'POST', body:`[LOCAL-AUDIO] event=emptied — src was cleared` }).catch(()=>{}); });
      // Log the first timeupdate where currentTime advances — proves data is actually decoded and rendered
      let _timeupdateLogged = false;
      _localAudio.addEventListener('timeupdate', () => {
        if (!_timeupdateLogged && _localAudio.currentTime > 0.1) {
          _timeupdateLogged = true;
          fetch('/api/log', { method:'POST', body:`[LOCAL-AUDIO] timeupdate confirmed — currentTime=${_localAudio.currentTime.toFixed(2)}s` }).catch(()=>{});
        }
        _localTimeUpdate();
      });
      _localAudio.addEventListener('loadedmetadata', _localTimeUpdate);
      _localAudio.addEventListener('error', () => {
        // Ignore the benign empty-src error fired when localStop() sets a.src = ''
        if (!_localAudio.src || _localAudio.src === window.location.href) return;
        const s = document.getElementById('local-player-state');
        const errCode = _localAudio.error ? _localAudio.error.code : '?';
        const errMsg = _localAudio.error ? _localAudio.error.message : 'unknown';
        const detail = `Media error ${errCode}: ${errMsg}`;
        if (s) s.textContent = detail;
        fetch('/api/log', { method: 'POST', body: `Local audio error — ${_localMediaType}: ${detail} (src: ${_localAudio.src || 'none'})` }).catch(() => {});
        const nmEl = document.getElementById('local-player-name');
        const nm = nmEl ? nmEl.textContent : '';
        if (typeof showPlaybackErrorPopup === 'function') {
          const label = nm ? `"${nm}"` : 'Stream';
          showPlaybackErrorPopup(_localMediaType || 'Media', `${label} stopped.\n\n${detail}`);
        }
      });
    }
  }
  return _localAudio;
}
function _localAudioUpdate() {
  const a = _getLocalAudio();
  if (!a) return;
  const btn = document.getElementById('local-play-toggle');
  const s = document.getElementById('local-player-state');
  if (btn) btn.innerHTML = a.paused ? '&#9654; Play' : '&#9646;&#9646; Pause';
  if (s) s.textContent = a.paused ? 'Paused' : 'Playing locally';
  // Mirror into music bar when in local music mode
  if (typeof _localMediaType !== 'undefined' && _localMediaType === 'Music') {
    const mBtn = document.getElementById('music-btn-play');
    if (mBtn) mBtn.innerHTML = a.paused ? '&#9654; Play' : '&#9646;&#9646; Pause';
  }
}
function _localTimeUpdate() {
  const a = _getLocalAudio();
  if (!a) return;
  const seek = document.getElementById('local-seek');
  const lbl = document.getElementById('local-time-label');
  const dur = isFinite(a.duration) ? a.duration : 0;
  if (seek && !_localSeekDragging) {
    seek.max = dur || 0;
    seek.value = a.currentTime || 0;
  }
  if (lbl) lbl.textContent = _fmtTime(a.currentTime) + ' / ' + _fmtTime(dur);
  // Mirror into music bar when in local music mode
  if (typeof _localMediaType !== 'undefined' && _localMediaType === 'Music') {
    const mSeek = document.getElementById('music-seek');
    if (mSeek && typeof _musicSeekDragging !== 'undefined' && !_musicSeekDragging) {
      mSeek.max = dur || 0;
      mSeek.value = a.currentTime || 0;
      if (typeof _updateMusicSeekFill !== 'undefined') _updateMusicSeekFill();
    }
    const mLbl = document.getElementById('music-time-label');
    if (mLbl) mLbl.textContent = _fmtTime(a.currentTime) + ' / ' + _fmtTime(dur);
  }
}
function localSeekDrag() {
  _localSeekDragging = true;
  const a = _getLocalAudio(),
    seek = document.getElementById('local-seek');
  const lbl = document.getElementById('local-time-label');
  if (a && seek && lbl) {
    const dur = isFinite(a.duration) ? a.duration : 0;
    lbl.textContent = _fmtTime(parseFloat(seek.value)) + ' / ' + _fmtTime(dur);
  }
}
function localSeekCommit() {
  _localSeekDragging = false;
  const a = _getLocalAudio(),
    seek = document.getElementById('local-seek');
  if (a && seek) a.currentTime = parseFloat(seek.value) || 0;
}
function localVolume(v) {
  const a = _getLocalAudio();
  if (a) a.volume = Math.min(1, Math.max(0, parseFloat(v) || 0));
  const lbl = document.getElementById('local-volume-label');
  if (lbl) lbl.textContent = Math.round((parseFloat(v) || 0) * 100) + '%';
}
