let _radioStatusPollId = null;
let _radioElapsedBase = 0; // server-reported elapsed at last poll
let _radioElapsedPollTime = 0; // performance.now() when that poll landed
let _radioElapsedTick = null;
let _podcastServerElapsedBase = 0;
let _podcastServerElapsedPollTime = 0;
let _podcastServerDuration = 0;
let _podcastServerCanSeek = false;
let _podcastSeekDragging = false;
let _radioRetryCount = 0;
let _radioWaveformId = null;

function _initRadioAudioSliderVisuals() {
  const volume = parseFloat(document.getElementById('radio-bar-volume')?.value);
  const boost = parseFloat(document.getElementById('radio-bar-boost')?.value);
  if (typeof syncRadioAudioControls === 'function') {
    syncRadioAudioControls(
      Number.isFinite(volume) ? volume : 0.8,
      Number.isFinite(boost) ? boost : 1
    );
  }
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', _initRadioAudioSliderVisuals);
} else {
  _initRadioAudioSliderVisuals();
}

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
    if (typeof _localMediaType !== 'undefined' && _localMediaType === 'PodcastServer') {
      const elapsed = _podcastServerElapsedBase
        + Math.max(0, Math.round((performance.now() - _podcastServerElapsedPollTime) / 1000));
      _updatePodcastProgress(elapsed, _podcastServerDuration, _podcastServerCanSeek);
      return;
    }
    const secNow =
      _radioElapsedBase + Math.round((performance.now() - _radioElapsedPollTime) / 1000);
    const el = document.getElementById('radio-bar-elapsed');
    if (el) {
      el.textContent = fmtSec(secNow);
      el.style.display = '';
      const techRow = document.getElementById('radio-bar-tech');
      if (techRow) techRow.hidden = false;
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

function _updatePodcastProgress(currentSeconds, durationSeconds, canSeek) {
  const row = document.getElementById('radio-bar-progress-row');
  const seek = document.getElementById('radio-bar-seek');
  const time = document.getElementById('radio-bar-progress-time');
  const techText = document.getElementById('radio-bar-tech-text');
  const duration = Number.isFinite(durationSeconds) && durationSeconds > 0
    ? durationSeconds
    : 0;
  let current = Number.isFinite(currentSeconds) ? Math.max(0, currentSeconds) : 0;
  if (duration > 0) current = Math.min(current, duration);
  const isSeekable = Boolean(canSeek && duration > 0);

  if (row) row.hidden = duration <= 0;
  if (seek) {
    seek.disabled = !isSeekable;
    seek.max = String(duration);
    if (!_podcastSeekDragging) seek.value = String(current);
    seek.setAttribute('aria-valuetext', `${fmtSec(current)} of ${duration > 0 ? fmtSec(duration) : '0:00'}`);
  }
  if (time) {
    time.textContent = `${fmtSec(current)} / ${duration > 0 ? fmtSec(duration) : '--:--'}`;
  }
  if (techText) {
    techText.textContent = duration > 0 || current <= 0
      ? 'Podcast'
      : `Podcast · ${fmtSec(current)}`;
  }
  if (typeof _syncRadioBarTechRow === 'function') _syncRadioBarTechRow();
}

function _resetPodcastProgress() {
  _podcastSeekDragging = false;
  _podcastServerElapsedBase = 0;
  _podcastServerElapsedPollTime = 0;
  _podcastServerDuration = 0;
  _podcastServerCanSeek = false;

  const row = document.getElementById('radio-bar-progress-row');
  const seek = document.getElementById('radio-bar-seek');
  const time = document.getElementById('radio-bar-progress-time');
  if (row) row.hidden = true;
  if (seek) {
    seek.disabled = true;
    seek.max = '0';
    seek.value = '0';
    seek.setAttribute('aria-valuetext', '0:00 of 0:00');
  }
  if (time) time.textContent = '0:00 / 0:00';
}

function _localPodcastSeekInfo(audio) {
  const duration = audio && Number.isFinite(audio.duration) && audio.duration > 0
    ? audio.duration
    : 0;
  let canSeek = false;
  const ranges = audio?.seekable;
  if (duration > 0 && ranges && ranges.length > 0) {
    const firstStart = ranges.start(0);
    const lastEnd = ranges.end(ranges.length - 1);
    canSeek = Number.isFinite(firstStart)
      && Number.isFinite(lastEnd)
      && firstStart <= 0.5
      && lastEnd >= duration - 1;
  }
  return { duration, canSeek };
}

function previewPodcastSeek(value) {
  const seek = document.getElementById('radio-bar-seek');
  const target = Number(value);
  const duration = seek ? Number(seek.max) : 0;
  if (!seek || seek.disabled || !Number.isFinite(target) || !Number.isFinite(duration)) return;
  _podcastSeekDragging = true;
  _updatePodcastProgress(target, duration, true);
}

async function commitPodcastSeek(value) {
  const seek = document.getElementById('radio-bar-seek');
  const target = Number(value);
  const duration = seek ? Number(seek.max) : 0;
  if (!seek || !Number.isFinite(target) || !Number.isFinite(duration) || duration <= 0) return;

  const seconds = Math.max(0, Math.min(duration, target));
  _podcastSeekDragging = false;
  if (typeof _localMediaType !== 'undefined' && _localMediaType === 'Podcast') {
    const audio = _getLocalAudio();
    const seekInfo = _localPodcastSeekInfo(audio);
    if (!seekInfo.canSeek) return;

    try {
      audio.currentTime = seconds;
      _updatePodcastProgress(seconds, seekInfo.duration, true);
    } catch (error) {
      console.error('Could not seek local podcast playback', error);
      if (typeof setStatus === 'function') setStatus('Could not seek this podcast episode.');
    }
    return;
  }

  if (typeof _localMediaType !== 'undefined' && _localMediaType === 'PodcastServer'
      && _podcastServerCanSeek) {
    _podcastServerElapsedBase = seconds;
    _podcastServerElapsedPollTime = performance.now();
    _updatePodcastProgress(seconds, _podcastServerDuration, true);
    try {
      const response = await fetch(
        '/api/radio/seek?' + new URLSearchParams({ v: String(seconds) }),
        { method: 'POST' }
      );
      if (!response.ok) {
        console.warn('Server rejected podcast seek', response.status);
        if (typeof setStatus === 'function') setStatus('Could not seek this podcast episode.');
      }
    } catch (error) {
      console.error('Could not seek server podcast playback', error);
      if (typeof setStatus === 'function') setStatus('Could not seek this podcast episode.');
    }
  }
}

function _syncPodcastServerProgress(elapsedSeconds, playing, durationSeconds, canSeek) {
  if (typeof _localMediaType === 'undefined' || _localMediaType !== 'PodcastServer') return;

  const now = performance.now();
  const rawElapsed = Number.isFinite(elapsedSeconds) ? Math.max(0, elapsedSeconds) : 0;
  const duration = Number.isFinite(durationSeconds) && durationSeconds > 0
    ? durationSeconds
    : 0;

  if (rawElapsed > 0 || _podcastServerElapsedPollTime <= 0) {
    _podcastServerElapsedBase = rawElapsed;
    _podcastServerElapsedPollTime = now;
  }
  _podcastServerDuration = duration;
  _podcastServerCanSeek = Boolean(canSeek && duration > 0);

  const assumeRunning = playing || (typeof _radioIsPlaying !== 'undefined' && _radioIsPlaying);
  const elapsed = assumeRunning && _podcastServerElapsedPollTime > 0
    ? _podcastServerElapsedBase + Math.max(0, Math.round((now - _podcastServerElapsedPollTime) / 1000))
    : rawElapsed;

  _updatePodcastProgress(elapsed, _podcastServerDuration, _podcastServerCanSeek);
}

// ── Waveform visualizer ───────────────────────────────────────────────
// Only local playback exposes audio samples to the browser; server playback
// has no client-side audio source to analyze.

function _ensureWaveformAnalyser() {
  const audio = document.getElementById('local-audio');
  if (!isPlayLocal() || !audio || !audio.src || typeof _getLocalAudioAnalyser !== 'function') {
    return null;
  }
  return _getLocalAudioAnalyser();
}

function startWaveform() {
  const cv = document.getElementById('radio-waveform');
  if (!cv) return;
  stopWaveform();
  if (window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) return;
  const analyser = _ensureWaveformAnalyser();
  if (!analyser) return;
  cv.classList.add('active');

  const freqData = new Uint8Array(analyser.frequencyBinCount);

  function draw() {
    if (!isPlayLocal()) {
      stopWaveform();
      return;
    }
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

    analyser.getByteFrequencyData(freqData);
    for (let i = 0; i < bars; i++) {
      const startBin = Math.floor(i * freqData.length / bars);
      const endBin = Math.max(startBin + 1, Math.floor((i + 1) * freqData.length / bars));
      let totalEnergy = 0;
      for (let bin = startBin; bin < endBin; bin++) totalEnergy += freqData[bin];
      const energy = totalEnergy / ((endBin - startBin) * 255);
      const h = Math.round(2 + energy * (H - 4));
      const y = Math.round((H - h) / 2);
      ctx.globalAlpha = 0.4 + energy * 0.6;
      ctx.fillRect(x0 + i * (bw + gap), y, bw, h);
    }
    ctx.globalAlpha = 1;
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

const _radioReducedMotionQuery = window.matchMedia?.('(prefers-reduced-motion: reduce)');
_radioReducedMotionQuery?.addEventListener('change', (event) => {
  if (event.matches) stopWaveform();
  else if (_radioIsPlaying) startWaveform();
});

// ── Health dot ───────────────────────────────────────────────────────
function setRadioHealthDot(state) {
  // 'ok'|'warn'|'error'|'off'
  const dot = document.getElementById('radio-health-dot');
  if (!dot) return;
  dot.className = 'radio-health-dot';
  if (state === 'ok') dot.classList.add('ok');
  else if (state === 'error') dot.classList.add('error');
  const status = state === 'ok'
    ? 'connected'
    : state === 'error'
      ? 'error'
      : state === 'off'
        ? 'off'
        : 'idle or reconnecting';
  dot.setAttribute('aria-label', `Stream status: ${status}`);
  // 'warn' = default amber; 'off' = hidden via amber (same visual as warn)
}

async function radioStatusTick() {
  try {
    const podcastServer = typeof _localMediaType !== 'undefined' && _localMediaType === 'PodcastServer';
    const r = await fetch('/api/radio/playback-status');
    if (!r.ok) {
      setRadioHealthDot('error');
      const playerType = podcastServer ? 'Podcast' : 'Radio';
      const reconnectStatus = document.getElementById('radio-bar-reconnect-status');
      if (reconnectStatus) {
        reconnectStatus.hidden = false;
        reconnectStatus.textContent = `${playerType} status unavailable.`;
      }
      const playerBar = document.getElementById('radio-player-bar');
      if (playerBar) playerBar.setAttribute('aria-busy', 'false');
      if (typeof _syncRadioBarTechRow === 'function') _syncRadioBarTechRow();
      _announcePlayerStatus(
        'radio-player-announcement',
        `${playerType.toLowerCase()}:status-error`,
        `${playerType} status is unavailable.`
      );
      return;
    }
    const s = await r.json();
    const playing = s.isPlaying || s.IsPlaying || false;
    const stalled = s.isStalled || s.IsStalled || false;
    const name = s.stationName || s.StationName || _radioCurrentName || '';
    const url = s.stationUrl || s.StationUrl || '';
    const songTitle = s.streamTitle || s.StreamTitle || '';
    const elapsed = s.elapsedSeconds || s.ElapsedSeconds || 0;
    const durationSeconds = Number(s.durationSeconds ?? s.DurationSeconds ?? 0);
    const canSeek = s.canSeek === true || s.CanSeek === true;
    const err = s.error || s.Error || '';
    if (url) _radioCurrentUrl = url;

    _syncPodcastServerProgress(elapsed, playing, durationSeconds, canSeek);
    if (typeof podcastHandleServerPlaybackStatus === 'function') {
      podcastHandleServerPlaybackStatus(playing, stalled, err);
    }

    // Update elapsed clock base
    if (playing && elapsed > 0) {
      _radioElapsedBase = elapsed;
      _radioElapsedPollTime = performance.now();
      if (!_radioElapsedTick) startElapsedTick();
    }

    // Health dot — in local mode, server reports not-playing so drive from local flag
    const effectivePlaying = isPlayLocal() ? _radioIsPlaying : playing;
    if (podcastServer) {
      const playerBar = document.getElementById('radio-player-bar');
      if (playerBar) playerBar.setAttribute('aria-busy', String(Boolean(stalled)));
      const btn = document.getElementById('radio-btn-play');
      if (btn) {
        const action = effectivePlaying ? 'Pause' : 'Play';
        btn.innerHTML = effectivePlaying ? '\u23F8 Pause' : '\u25B6 Play';
        btn.setAttribute('aria-label', `${action} podcast playback`);
        btn.setAttribute('aria-pressed', String(Boolean(effectivePlaying)));
        btn.title = `${action} podcast playback`;
      }
      const reconnectStatus = document.getElementById('radio-bar-reconnect-status');
      if (reconnectStatus) {
        reconnectStatus.hidden = !stalled;
        reconnectStatus.textContent = stalled ? 'Reconnecting…' : '';
      }
      if (stalled) {
        _announcePlayerStatus(
          'radio-player-announcement',
          'podcast:loading',
          'Podcast stream is reconnecting.'
        );
      } else if (!err || effectivePlaying) {
        const playbackState = effectivePlaying ? 'playing' : 'stopped';
        _announcePlayerStatus(
          'radio-player-announcement',
          `podcast:${playbackState}`,
          `Podcast playback ${playbackState}.`
        );
      }
      if (typeof _syncRadioBarTechRow === 'function') _syncRadioBarTechRow();
    }
    if (!effectivePlaying && err) {
      setRadioHealthDot('error');
      _announcePlayerStatus(
        'radio-player-announcement',
        `${podcastServer ? 'podcast' : 'radio'}:error`,
        ''
      );
      // Show themed popup with the server-reported error reason
      const stationLabel = name ? `"${name}"` : 'Station';
      showPlaybackErrorPopup(podcastServer ? 'Podcast' : 'Radio', `${stationLabel} stopped playing.\n\n${err}`);
    } else if (stalled && !isPlayLocal()) {
      setRadioHealthDot('warn');
    } else if (effectivePlaying) {
      setRadioHealthDot('ok');
      // Playback is healthy — allow the error popup to reappear if a new error occurs
      if (typeof clearPlaybackErrorDedup === 'function') {
        clearPlaybackErrorDedup(podcastServer ? 'Podcast' : 'Radio');
      }
    } else {
      setRadioHealthDot('warn');
    }

    // Quality badge stall overlay — show reconnecting state
    if (!isPlayLocal()) {
      const qBadge = document.getElementById('radio-bar-quality-badge');
      if (qBadge) {
        const quality = qBadge.textContent.trim();
        const qualityLabel = quality ? `Stream quality ${quality}` : 'Stream quality';
        qBadge.setAttribute(
          'aria-label',
          stalled ? `${qualityLabel}; reconnecting` : qualityLabel
        );
        if (stalled) {
          qBadge.dataset.stalled = '1';
          qBadge.title = 'Stream stalled — reconnecting…';
          qBadge.classList.add('stalled');
        } else {
          qBadge.dataset.stalled = '';
          qBadge.title = '';
          qBadge.classList.remove('stalled');
        }
      }
      const reconnectStatus = document.getElementById('radio-bar-reconnect-status');
      if (reconnectStatus) {
        if (stalled) {
          if (reconnectStatus.hidden || reconnectStatus.textContent !== 'Reconnecting…') {
            reconnectStatus.hidden = false;
            reconnectStatus.textContent = 'Reconnecting…';
          }
        } else if (!reconnectStatus.hidden) {
          reconnectStatus.hidden = true;
          reconnectStatus.textContent = '';
        }
      }
      if (typeof _syncRadioBarTechRow === 'function') _syncRadioBarTechRow();
    }

    // Sync podcast-server local bar volume/boost from server state.
    if (!isPlayLocal() && typeof _localMediaType !== 'undefined' && _localMediaType === 'PodcastServer') {
      const vol = Math.max(0, Math.min(1, s.volume ?? s.Volume ?? 0.8));
      const boost = Math.max(1, Math.min(3, s.boost ?? s.Boost ?? 1.0));
      if (typeof syncRadioAudioControls === 'function') syncRadioAudioControls(vol, boost);
    }

    // Stream title (server mode only — local streams rarely carry ICY metadata via proxy)
    const songEl = document.getElementById('radio-bar-song');
    const songTextEl = document.getElementById('radio-bar-song-text');
    if (songEl && songTextEl) {
      if (songTitle && playing) {
        songTextEl.textContent = songTitle;
        songTextEl.title = songTitle;
        songEl.style.display = '';
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
              new URLSearchParams({ url: _radioCurrentUrl, name: _radioCurrentName }),
            { method: 'POST' }
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
    if (!isPlayLocal() && !(typeof _localMediaType !== 'undefined' && _localMediaType === 'PodcastServer')) {
      // If we lost the station object (e.g. page reload), try to recover from favorites by URL.
      if (!_radioCurrentStation && url && _radioFavorites && _radioFavorites.length) {
        const match = _radioFavorites.find(
          (f) => (f.url_resolved || f.StreamUrl || f.streamUrl || '') === url
        );
        if (match) _radioCurrentStation = match;
      }
      updateRadioBar(
        name,
        _radioCurrentCountry,
        _radioCurrentTag,
        playing,
        _radioCurrentStation,
        { isStalled: stalled, hasError: Boolean(err && !effectivePlaying) }
      );
    }
    // Sync combined volume/boost slider from server state
    if (!isPlayLocal() && !(typeof _localMediaType !== 'undefined' && _localMediaType === 'PodcastServer')) {
      const vol   = s.volume ?? s.Volume ?? 0.8;
      const boost = s.boost  ?? s.Boost  ?? 1.0;
      if (typeof syncRadioAudioControls === 'function') syncRadioAudioControls(vol, boost);
    }

    // Sync EQ and reverb selectors from server state without resetting user selection
    const radioEqPreset = s.eqPreset ?? s.EqPreset ?? -1;
    if (typeof renderEqPresets === 'function') {
      const sel = document.getElementById('radio-eq-select');
      if (sel && sel.value !== String(radioEqPreset)) renderEqPresets(radioEqPreset);
    }
    const radioReverbPreset = s.reverbPreset ?? s.ReverbPreset ?? 0;
    if (typeof _renderRadioReverbPreset === 'function') {
      _renderRadioReverbPreset(radioReverbPreset);
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
    await radioLoadFavorites();
    // Re-render so removed favorites disappear from favorites tab, and hearts update elsewhere
    if (_radioTab === 'favorites') {
      renderRadioCards(_radioFavorites, false, true);
    } else {
      renderRadioCards(_radioStations, _radioStations.length === _radioPageSize * (_radioPage + 1));
    }
    // Sync the player bar heart button
    if (typeof _radioUpdateBarFavBtn === 'function') _radioUpdateBarFavBtn();
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
function _setLocalAudioBusy(isBusy, refreshPlayerState = true) {
  const mediaType = typeof _localMediaType !== 'undefined' ? _localMediaType : '';
  const isMusic = mediaType === 'Music';
  const isRadioPlayer = mediaType === 'Radio' || mediaType === 'Podcast';
  if (!isMusic && !isRadioPlayer) return;

  const label = mediaType === 'Podcast' ? 'Podcast' : isMusic ? 'Music' : 'Radio';
  const barId = isMusic ? 'music-player-bar' : 'radio-player-bar';
  const statusId = isMusic ? 'music-player-announcement' : 'radio-player-announcement';
  const bar = document.getElementById(barId);
  if (bar) bar.setAttribute('aria-busy', String(isBusy));
  const visibleStatus = document.getElementById(
    isMusic ? 'music-bar-state-status' : 'radio-bar-reconnect-status'
  );
  if (visibleStatus) {
    visibleStatus.hidden = !isBusy;
    visibleStatus.textContent = isBusy ? 'Buffering…' : '';
  }
  if (isRadioPlayer && typeof _syncRadioBarTechRow === 'function') _syncRadioBarTechRow();
  if (isBusy) {
    _announcePlayerStatus(statusId, `${mediaType.toLowerCase()}:loading`, `${label} playback is buffering.`);
  } else if (refreshPlayerState) {
    _localAudioUpdate();
  }
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
        let acState = 'not-created';
        try {
          const graphContext = typeof _localBoostCtx !== 'undefined' ? _localBoostCtx : null;
          if (graphContext) acState = graphContext.state;
        } catch (e) { acState = 'err'; }
        fetch('/api/log', { method:'POST', body:`[LOCAL-AUDIO] event=playing vol=${vol} muted=${muted} readyState=${rs} networkState=${ns} acState=${acState} currentTime=${_localAudio.currentTime.toFixed(2)}` }).catch(()=>{});
        _setLocalAudioBusy(false, false);
      });
      _localAudio.addEventListener('pause',   () => { fetch('/api/log', { method:'POST', body:`[LOCAL-AUDIO] event=pause src=${_localAudio.src}` }).catch(()=>{}); _setLocalAudioBusy(false); });
      _localAudio.addEventListener('ended',   () => {
        fetch('/api/log', { method:'POST', body:`[LOCAL-AUDIO] event=ended` }).catch(()=>{});
        // Auto-advance to the next music track when playing locally; otherwise just stop.
        if (typeof _localMediaType !== 'undefined' && _localMediaType === 'Music'
            && typeof isPlayLocal === 'function' && isPlayLocal()
            && typeof musicNext === 'function') {
          musicNext();
        } else if (typeof _localMediaType !== 'undefined' && _localMediaType === 'Podcast'
            && typeof podcastNextEpisode === 'function') {
          podcastNextEpisode({ autoAdvance: true });
        } else {
          localStop();
        }
      });
      _localAudio.addEventListener('stalled', () => {
        fetch('/api/log', { method:'POST', body:`[LOCAL-AUDIO] event=stalled` }).catch(()=>{});
        _setLocalAudioBusy(true);
      });
      _localAudio.addEventListener('waiting', () => {
        fetch('/api/log', { method:'POST', body:`[LOCAL-AUDIO] event=waiting` }).catch(()=>{});
        _setLocalAudioBusy(true);
      });
      _localAudio.addEventListener('canplay', () => {
        fetch('/api/log', { method:'POST', body:`[LOCAL-AUDIO] event=canplay` }).catch(()=>{});
        _setLocalAudioBusy(false, false);
        _localTimeUpdate();
      });
      _localAudio.addEventListener('durationchange', _localTimeUpdate);
      _localAudio.addEventListener('progress', _localTimeUpdate);
      _localAudio.addEventListener('seeked', _localTimeUpdate);
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
        _setLocalAudioBusy(false);
        const errCode = _localAudio.error ? _localAudio.error.code : '?';
        const errMsg = _localAudio.error ? _localAudio.error.message : 'unknown';
        const detail = `Media error ${errCode}: ${errMsg}`;
        fetch('/api/log', { method: 'POST', body: `Local audio error — ${_localMediaType}: ${detail} (src: ${_localAudio.src || 'none'})` }).catch(() => {});
        const isMusic = _localMediaType === 'Music';
        const isRadioPlayer = _localMediaType === 'Radio' || _localMediaType === 'Podcast';
        if ((isMusic || isRadioPlayer) && typeof _announcePlayerStatus === 'function') {
          const type = isMusic ? 'Music' : _localMediaType;
          _announcePlayerStatus(
            isMusic ? 'music-player-announcement' : 'radio-player-announcement',
            `${type.toLowerCase()}:error`,
            ''
          );
        }
        const nmEl = isMusic
          ? document.getElementById('music-bar-title')
          : document.getElementById('radio-bar-title');
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
  const hasSource = Boolean(a.currentSrc || (a.src && a.src !== window.location.href));
  const playing = !a.paused && !a.ended;
  const rBtn = document.getElementById('radio-btn-play');
  if (rBtn && (typeof _localMediaType !== 'undefined' && (_localMediaType === 'Radio' || _localMediaType === 'Podcast'))) {
    rBtn.innerHTML = a.paused ? '&#9654; Play' : '&#9646;&#9646; Pause';
    rBtn.setAttribute('aria-label', `${a.paused ? 'Play' : 'Pause'} ${_localMediaType === 'Podcast' ? 'podcast' : 'radio'} playback`);
    rBtn.setAttribute('aria-pressed', String(playing));
  }
  // Mirror into music bar when in local music mode
  if (typeof _localMediaType !== 'undefined' && _localMediaType === 'Music') {
    const mBtn = document.getElementById('music-btn-play');
    if (mBtn) {
      mBtn.innerHTML = a.paused ? '&#9654; Play' : '&#9646;&#9646; Pause';
      mBtn.setAttribute('aria-label', `${a.paused ? 'Play' : 'Pause'} music`);
      mBtn.setAttribute('aria-pressed', String(playing));
    }
  }
  if (hasSource) {
    const mediaType = typeof _localMediaType !== 'undefined' ? _localMediaType : '';
    const isMusic = mediaType === 'Music';
    const isRadioPlayer = mediaType === 'Radio' || mediaType === 'Podcast';
    if (isMusic || isRadioPlayer) {
      const label = mediaType === 'Podcast' ? 'Podcast' : isMusic ? 'Music' : 'Radio';
      const statusId = isMusic ? 'music-player-announcement' : 'radio-player-announcement';
      const state = a.ended ? 'stopped' : a.paused ? 'paused' : 'playing';
      if (isMusic) {
        const playerStatus = document.getElementById('music-bar-state-status');
        if (playerStatus) {
          playerStatus.textContent = state === 'stopped' ? 'Stopped' : state === 'paused' ? 'Paused' : 'Playing';
          playerStatus.hidden = false;
        }
      }
      _announcePlayerStatus(
        statusId,
        `${mediaType.toLowerCase()}:${state}`,
        `${label} playback ${state}.`
      );
    }
  }
  if (typeof _syncPodcastPlaybackControls === 'function') _syncPodcastPlaybackControls();
}
function _localTimeUpdate() {
  const a = _getLocalAudio();
  if (!a) return;
  const seek = document.getElementById('local-seek');
  const lbl = document.getElementById('local-time-label');
  const dur = Number.isFinite(a.duration) && a.duration > 0 ? a.duration : 0;
  if (seek && !_localSeekDragging) {
    seek.disabled = false;
    seek.removeAttribute('disabled');
    seek.max = dur || 0;
    seek.value = a.currentTime || 0;
  }
  if (lbl) lbl.textContent = _fmtTime(a.currentTime) + ' / ' + _fmtTime(dur);
  if (typeof _localMediaType !== 'undefined' && _localMediaType === 'Podcast') {
    const seekInfo = _localPodcastSeekInfo(a);
    _updatePodcastProgress(a.currentTime, seekInfo.duration, seekInfo.canSeek);
  } else if (typeof _localMediaType !== 'undefined' && _localMediaType === 'Radio') {
    const elapsed = document.getElementById('radio-bar-elapsed');
    if (elapsed && !a.paused && Number.isFinite(a.currentTime)) {
      elapsed.textContent = fmtSec(a.currentTime);
      elapsed.style.display = '';
      if (typeof _syncRadioBarTechRow === 'function') _syncRadioBarTechRow();
    }
  }
  // Mirror into music bar when in local music mode
  if (typeof _localMediaType !== 'undefined' && _localMediaType === 'Music') {
    const mSeek = document.getElementById('music-seek');
    const musicProgressRow = document.getElementById('music-progress-row');
    const statusDuration = Number(window._lastMusicBarState?.duration);
    const metadataDuration = Number(window._lastMusicBarState?.durationSec);
    const musicDuration = dur > 0
      ? dur
      : Number.isFinite(statusDuration) && statusDuration > 0
        ? statusDuration
        : Number.isFinite(metadataDuration) && metadataDuration > 0
          ? metadataDuration
          : 0;
    const musicPosition = Number.isFinite(a.currentTime) ? Math.max(0, a.currentTime) : 0;
    const displayPosition = _musicSeekDragging && mSeek
      ? Math.max(0, Number(mSeek.value) || 0)
      : musicPosition;
    if (musicProgressRow) musicProgressRow.style.display = musicDuration > 0 ? 'grid' : 'none';
    if (mSeek) {
      mSeek.disabled = musicDuration <= 0;
      mSeek.max = musicDuration;
    }
    if (mSeek && typeof _musicSeekDragging !== 'undefined' && !_musicSeekDragging) {
      mSeek.value = musicDuration > 0 ? Math.min(musicPosition, musicDuration) : 0;
      if (typeof _updateMusicSeekFill !== 'undefined') _updateMusicSeekFill();
    }
    const mLbl = document.getElementById('music-time-label');
    if (mLbl) {
      mLbl.textContent = musicDuration > 0
        ? _fmtTime(Math.min(displayPosition, musicDuration)) + ' / ' + _fmtTime(musicDuration)
        : '';
    }
  }
}
function localSeekDrag() {
  const seekEl = document.getElementById('local-seek');
  if (seekEl) {
    seekEl.disabled = false;
    seekEl.removeAttribute('disabled');
  }

  if (typeof _localMediaType !== 'undefined' && _localMediaType === 'PodcastServer') {
    _localSeekDragging = true;
    const seek = seekEl;
    const lbl = document.getElementById('local-time-label');
    if (seek && lbl) {
      const val = parseFloat(seek.value) || 0;
      const max = parseFloat(seek.max) || 0;
      lbl.textContent = `${fmtSec(val)} / ${max > 0 ? fmtSec(max) : '--:--'}`;
    }
    return;
  }

  _localSeekDragging = true;
  const a = _getLocalAudio(),
    seek = document.getElementById('local-seek');
  const lbl = document.getElementById('local-time-label');
  if (a && seek && lbl) {
    const dur = isFinite(a.duration) ? a.duration : 0;
    lbl.textContent = _fmtTime(parseFloat(seek.value)) + ' / ' + _fmtTime(dur);
  }
}
async function localSeekCommit() {
  if (typeof _localMediaType !== 'undefined' && _localMediaType === 'PodcastServer') {
    const seek = document.getElementById('local-seek');
    const target = seek ? (parseFloat(seek.value) || 0) : 0;
    _localSeekDragging = false;

    try {
      await fetch('/api/radio/seek?' + new URLSearchParams({ v: String(Math.max(0, target)) }), { method: 'POST' });
    } catch {}

    return;
  }

  _localSeekDragging = false;
  const a = _getLocalAudio(),
    seek = document.getElementById('local-seek');
  if (a && seek) a.currentTime = parseFloat(seek.value) || 0;
}
function localVolume(v) {
  if (typeof radioVolume === 'function') radioVolume(v);
}
