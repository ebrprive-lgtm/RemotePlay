function fmt(sec) {
  const m = Math.floor(sec / 60),
    s = Math.floor(sec % 60);
  return m + ':' + (s < 10 ? '0' : '') + s;
}

function updateResumePrompt(status) {
  const card = document.getElementById('resume-card');
  if (!card) return;
  const position = Number(status?.resumePosition) || 0;
  const path = status?.filePath || '';
  const visible = Boolean(status?.isPlaying && status?.canResume && position > 5 && path);
  card.classList.toggle('visible', visible);
  if (!visible) {
    resumePromptPath = null;
    resumePromptPosition = 0;
    return;
  }
  resumePromptPath = path;
  resumePromptPosition = position;
  document.getElementById('resume-detail').textContent =
    'Continue from ' + fmt(position) + ' or start this video from the beginning.';
}

async function resumeFromSavedPosition() {
  if (!resumePromptPath || resumePromptPosition <= 0) return;
  haptic(12);
  await api('/api/seek?pos=' + resumePromptPosition.toFixed(1));
  updateResumePrompt(null);
  setStatus('Resumed from ' + fmt(resumePromptPosition) + '.');
  await pollStatus();
}

async function startFromBeginning() {
  if (!resumePromptPath) return;
  const path = resumePromptPath;
  haptic(8);
  await api('/api/history/clear?path=' + encodeURIComponent(path));
  await api('/api/seek?pos=0');
  unmarkPlayed(path);
  updateResumePrompt(null);
  setStatus('Starting from beginning.');
  await pollStatus();
}

// Updates the CSS gradient fill on the seek bar to reflect current position.
function updateSeekFill(el) {
  const max = parseFloat(el.max) || 1;
  const val = parseFloat(el.value) || 0;
  const pct = Math.max(0, Math.min(100, (val / max) * 100)).toFixed(3) + '%';
  el.style.setProperty('--seek-pct', pct);
}

async function onSeekDrag() {
  seekDragging = true;
  const el = document.getElementById('progress');
  if (el) updateSeekFill(el);
}
async function onSeekCommit() {
  const pos = parseFloat(document.getElementById('progress').value);
  if (!Number.isNaN(pos)) {
    haptic(8);
    await api('/api/seek?pos=' + pos.toFixed(1));
  }
  seekDragging = false;
}

function updateVolumeIcon(value) {
  const btn = document.getElementById('volume-icon-btn');
  if (btn) btn.classList.toggle('off', (Number(value) || 0) <= 0.001);
}
function updateAudioBoostIcon(value) {
  // legacy no-op — boost state is now reflected by slider colour
}

// Combined volume/boost slider (0–1.0 = volume, 1.0–1.3 = boost zone)
// Boost mapping: slider 1.0 → gain 1.0, slider 1.3 → gain 2.0 (linear)

const _debouncedVideoVolumeApi = debounce((vol, boost) => {
  api('/api/volume?value=' + vol);
  api('/api/audio-boost?value=' + boost);
}, 150);

async function setVideoCombinedSlider(v) {
  const val = parseFloat(v);
  if (isNaN(val)) return;
  const slider = document.getElementById('video-combined-vol');
  const lbl    = document.getElementById('volume-label');
  const totalRange  = 1.3;
  const volBoundary = (1.0 / totalRange * 100).toFixed(2) + '%';  // ~76.92%
  const fillPct     = (val / totalRange * 100).toFixed(2) + '%';
  if (val <= 1.0) {
    if (slider) {
      slider.classList.remove('slider-boosting');
      slider.style.setProperty('--cvs-vol',   fillPct);
      slider.style.setProperty('--cvs-boost', fillPct);
      slider.style.setProperty('--cvs-thumb', 'var(--player-accent,#e94560)');
    }
    const volume = Math.max(0, val);
    if (volume > 0.001) lastVolumeBeforeMute = volume;
    if (lbl) lbl.textContent = Math.round(volume * 100) + '%';
    updateVolumeIcon(volume);
    _debouncedVideoVolumeApi(volume.toFixed(2), '1.00');
  } else {
    if (slider) {
      slider.classList.add('slider-boosting');
      slider.style.setProperty('--cvs-vol',   volBoundary);
      slider.style.setProperty('--cvs-boost', fillPct);
      slider.style.setProperty('--cvs-thumb', '#f97316');
    }
    // vol = 1.0, boost gain maps 1.0–1.3 → 1.0–2.0
    const gain = 1 + (val - 1) * (1 / 0.3);
    const db = Math.round(20 * Math.log10(gain));
    if (lbl) lbl.textContent = '100% +' + db + 'dB';
    updateVolumeIcon(1);
    _debouncedVideoVolumeApi('1.00', gain.toFixed(2));
  }
}
async function setVideoCombinedReset() {
  const slider = document.getElementById('video-combined-vol');
  if (slider) { slider.value = 1; await setVideoCombinedSlider(1); }
}
async function togglePause() {
  haptic(12);
  await api('/api/pause');
}
async function skip(seconds) {
  await api('/api/skip?seconds=' + encodeURIComponent(seconds));
}
async function quickSkip(seconds) {
  if (suppressNextSeekTap) {
    suppressNextSeekTap = false;
    return;
  }
  haptic(8);
  await skip(seconds);
}
function beginSeekHold(event, step) {
  if (event.pointerType === 'mouse' && event.button !== 0) return;
  seekHoldTriggered = false;
  clearTimeout(seekHoldTimer);
  clearInterval(seekHoldInterval);
  seekHoldTimer = setTimeout(() => {
    seekHoldTriggered = true;
    suppressNextSeekTap = true;
    haptic(16);
    skip(step);
    seekHoldInterval = setInterval(() => skip(step), 220);
  }, 350);
}
function endSeekHold() {
  clearTimeout(seekHoldTimer);
  clearInterval(seekHoldInterval);
  seekHoldTimer = null;
  seekHoldInterval = null;
}
async function setVolume(value) {
  const volume = Math.max(0, Math.min(1, parseFloat(value) || 0));
  if (volume > 0.001) lastVolumeBeforeMute = volume;
  document.getElementById('volume-label').textContent = Math.round(volume * 100) + '%';
  updateVolumeIcon(volume);
  // sync combined slider (only if we are in volume zone)
  const cs = document.getElementById('video-combined-vol');
  if (cs && parseFloat(cs.value) <= 1.0) { cs.value = volume.toFixed(2); cs.classList.remove('slider-boosting'); }
  await api('/api/volume?value=' + encodeURIComponent(volume.toFixed(2)));
}
async function toggleVolumeMute() {
  haptic(10);
  const slider = document.getElementById('video-combined-vol');
  if (!slider) return;
  const current = Math.max(0, Math.min(1, parseFloat(slider.value) || 0));
  // Only mute/unmute when in volume zone
  const combined = parseFloat(slider.value);
  if (combined > 1.0) return; // don't mute in boost zone
  const next = current > 0.001 ? 0 : Math.max(0.05, lastVolumeBeforeMute || 0.7);
  slider.value = next.toFixed(2);
  await setVideoCombinedSlider(slider.value);
}
async function setAudioBoost(value) {
  const boostAmount = Math.max(0, Math.min(1, parseFloat(value) || 0));
  if (boostAmount > 0.001) lastBoostBeforeMute = boostAmount;
  updateAudioBoostIcon(boostAmount);
  await api('/api/audio-boost?value=' + encodeURIComponent((1 + boostAmount).toFixed(2)));
}
async function toggleAudioBoostMute() {
  // Legacy — no longer exposed in UI; no-op
}
async function setBrightness(value) {
  const brightness = Math.max(0.3, Math.min(0.7, parseFloat(value) || 0.5));
  document.getElementById('brightness').value = brightness.toFixed(2);
  document.getElementById('brightness-label').textContent = Math.round(brightness * 100) + '%';
  await api('/api/brightness?value=' + encodeURIComponent(brightness.toFixed(2)));
}
async function setSaturation(value) {
  const parsed = parseFloat(value);
  const saturation = Math.max(0, Math.min(2, Number.isFinite(parsed) ? parsed : 1));
  document.getElementById('saturation').value = saturation.toFixed(2);
  document.getElementById('saturation-label').textContent = Math.round(saturation * 100) + '%';
  await api('/api/saturation?value=' + encodeURIComponent(saturation.toFixed(2)));
}
async function setSharpness(value) {
  setSharpnessPreview(value);

  const sharpness = parseFloat(document.getElementById('sharpness').value);
  await api('/api/sharpness?value=' + encodeURIComponent(sharpness.toFixed(2)));
}
function setSharpnessPreview(value) {
  const parsed = parseFloat(value);
  const sharpness = Math.max(0, Math.min(1, Number.isFinite(parsed) ? parsed : 0));
  document.getElementById('sharpness').value = sharpness.toFixed(2);
  document.getElementById('sharpness-label').textContent = Math.round(sharpness * 100) + '%';
}
function onSharpnessPointerDown() {
  sharpnessDragging = true;
}
function onSharpnessPointerUp() {
  sharpnessDragging = false;
}
function onZoomPointerDown() {
  zoomDragging = true;
}
function onZoomPointerUp() {
  zoomDragging = false;
}
function setZoomPreview(value) {
  const parsed = parseFloat(value);
  const zoom = Math.max(0.5, Math.min(2, Number.isFinite(parsed) ? parsed : 1));
  document.getElementById('zoom').value = zoom.toFixed(2);
  document.getElementById('zoom-label').textContent = Math.round(zoom * 100) + '%';
}
async function commitZoom(value) {
  setZoomPreview(value);
  await api(
    '/api/zoom?value=' +
      encodeURIComponent(parseFloat(document.getElementById('zoom').value).toFixed(2))
  );
  zoomDragging = false;
}
function resetBrightnessMid() {
  haptic(8);
  setBrightness(0.5);
}
function resetSaturationMid() {
  haptic(8);
  setSaturation(1);
}
function resetSharpnessDefault() {
  haptic(8);
  setSharpness(0);
}
function resetZoomDefault() {
  haptic(8);
  commitZoom(1);
}
const DEFAULT_PROFILE = { volume: 1, boost: 0, brightness: 0.5, sharpness: 0, saturation: 1, zoom: 1, speed: 1 };
let _profileHoldTimer = null;
let _profileHoldFired = false;
function _getProfiles() {
  try {
    const s = localStorage.getItem('remotePlayProfiles');
    return s ? JSON.parse(s) : {};
  } catch {
    return {};
  }
}
function _saveProfiles(p) {
  try {
    localStorage.setItem('remotePlayProfiles', JSON.stringify(p));
  } catch {}
}
function _getProfile(n) {
  const p = _getProfiles();
  return Object.assign({}, DEFAULT_PROFILE, p[n] || {});
}
function _currentSettings() {
  const cs = document.getElementById('video-combined-vol');
  const combined = cs ? parseFloat(cs.value) : 1;
  const volume = Math.min(1, combined);
  const boost  = combined > 1.0 ? (combined - 1) * (1 / 0.3) : 0;
  return {
    volume,
    boost,
    brightness: parseFloat(document.getElementById('brightness').value) || 0.5,
    sharpness: parseFloat(document.getElementById('sharpness').value) || 0,
    saturation: parseFloat(document.getElementById('saturation').value) || 1,
    zoom: parseFloat(document.getElementById('zoom').value) || 1,
    speed: currentPlaybackSpeed || 1,
  };
}
function profilePointerDown(event, n) {
  _profileHoldFired = false;
  clearTimeout(_profileHoldTimer);
  _profileHoldTimer = setTimeout(() => {
    _profileHoldFired = true;
    saveProfile(n);
  }, 600);
}
function profilePointerUp(event, n) {
  clearTimeout(_profileHoldTimer);
  _profileHoldTimer = null;
}
function profilePointerCancel() {
  clearTimeout(_profileHoldTimer);
  _profileHoldTimer = null;
}
function profileClick(event, n) {
  if (_profileHoldFired) {
    _profileHoldFired = false;
    return;
  }
  applyProfile(n);
}
function saveProfile(n) {
  haptic([40, 60, 40]);
  const profiles = _getProfiles();
  profiles[n] = _currentSettings();
  _saveProfiles(profiles);
  const btn = document.getElementById('profile-btn-' + n);
  if (btn) {
    btn.classList.remove('saving', 'applying');
    void btn.offsetWidth;
    btn.classList.add('saving');
    setTimeout(() => btn.classList.remove('saving'), 900);
  }
  setStatus('Profile ' + n + ' saved.');
}
async function applyProfile(n) {
  const profile = _getProfile(n);
  haptic([18, 30, 18]);
  document
    .querySelectorAll('.profile-btn')
    .forEach((b) => b.classList.toggle('active', b.dataset.profile === String(n)));
  const btn = document.getElementById('profile-btn-' + n);
  if (btn) {
    btn.classList.remove('applying');
    void btn.offsetWidth;
    btn.classList.add('applying');
    setTimeout(() => btn.classList.remove('applying'), 500);
  }
  await setVolume(profile.volume);
  // Set the combined slider: if boost > 0, map to boost zone; otherwise use volume
  const combined = profile.boost > 0
    ? Math.min(1.3, 1.0 + profile.boost * 0.3)
    : Math.min(1.0, profile.volume);
  const cs = document.getElementById('video-combined-vol');
  if (cs) { cs.value = combined.toFixed(2); await setVideoCombinedSlider(combined); }
  else { await setAudioBoost(profile.boost); }
  await setBrightness(profile.brightness);
  await setSharpness(profile.sharpness);
  await setSaturation(profile.saturation);
  await commitZoom(profile.zoom);
  await setPlaybackSpeed(profile.speed);
  setStatus('Profile ' + n + ' applied.');
}
async function applyPlaybackProfile(name) {
  // legacy no-op kept to avoid errors if called from elsewhere
}
function syncSpeedChips(speed) {
  const chips = Array.from(document.querySelectorAll('.speed-chip'));
  let selected = null;
  for (const chip of chips) {
    const value = parseFloat(chip.dataset.speed || '1');
    const isActive = Math.abs(value - speed) < 0.02;
    chip.classList.toggle('active', isActive);
    if (isActive) selected = chip;
  }
  if (!selected) {
    let nearest = null;
    let nearestDelta = Number.POSITIVE_INFINITY;
    for (const chip of chips) {
      const value = parseFloat(chip.dataset.speed || '1');
      const delta = Math.abs(value - speed);
      if (delta < nearestDelta) {
        nearestDelta = delta;
        nearest = chip;
      }
    }
    if (nearest) nearest.classList.add('active');
  }
}
async function setPlaybackSpeed(value) {
  const speed = Math.max(0.5, Math.min(2, parseFloat(value) || 1));
  currentPlaybackSpeed = speed;
  syncSpeedChips(speed);
  haptic(10);
  await api('/api/speed?value=' + encodeURIComponent(speed.toFixed(2)));
}
function updateTrackControls(s) {
  const audioSelect = document.getElementById('audio-track-select');
  const subtitleSelect = document.getElementById('subtitle-track-select');
  const chapterSelect = document.getElementById('chapter-track-select');
  const audioGroup = document.getElementById('audio-track-group');
  const subtitleGroup = document.getElementById('subtitle-track-group');
  const chapterGroup = document.getElementById('chapter-track-group');
  const trackControls = document.getElementById('track-controls');
  const audioTracks = Array.isArray(s.audioTracks) ? s.audioTracks : [];
  const subtitleTracks = Array.isArray(s.subtitleTracks) ? s.subtitleTracks : [];
  const chapters = Array.isArray(s.chapters) ? s.chapters : [];
  const showAudio = audioTracks.length > 1;
  const showSubtitles = subtitleTracks.some((t) => Number(t.id) >= 0);
  const showChapters = chapters.length > 1;
  renderTrackSelect(audioSelect, audioTracks, s.currentAudioTrackId);
  renderTrackSelect(subtitleSelect, subtitleTracks, s.currentSubtitleTrackId);
  if (showChapters) renderTrackSelect(chapterSelect, chapters, s.currentChapter ?? -1);
  audioGroup.style.display = showAudio ? 'flex' : 'none';
  subtitleGroup.style.display = showSubtitles ? 'flex' : 'none';
  if (chapterGroup) chapterGroup.style.display = showChapters ? 'flex' : 'none';
  const showAny = showAudio || showSubtitles || showChapters;
  trackControls.style.display = showAny ? 'flex' : 'none';
  renderEqPresets(s.eqPreset ?? -1);
  const eqWrap = document.getElementById('eq-select-wrap');
  if (eqWrap) eqWrap.style.display = s.isPlaying ? '' : 'none';
  if (s.isPlaying) _drawEqSparkline('eq-sparkline', s.eqPreset ?? -1);
  _syncEqWrapVisibility();
  document.getElementById('options-card').style.display =
    showAny || s.isPlaying ? 'flex' : 'none';
  updateChapterMarkers(s);
  if (s.isPlaying) updateUpNext(s);
  else _clearUpNext();
}
function renderTrackSelect(select, tracks, currentId) {
  const signature = JSON.stringify((tracks || []).map((t) => [t.id, t.name]));
  if (select.dataset.signature !== signature) {
    select.innerHTML = (tracks || [])
      .map(
        (t) =>
          '<option value="' +
          esc(String(t.id)) +
          '">' +
          esc(t.name || 'Track ' + t.id) +
          '</option>'
      )
      .join('');
    select.dataset.signature = signature;
  }
  select.value = String(currentId);
}
function updateAdjacentButtons(s) {
  const previous = document.getElementById('previous-btn');
  const next = document.getElementById('next-btn');
  const navGroup = previous.closest('.transport-nav-group');
  const queue = Array.isArray(s.queue) ? s.queue : [];
  const hasQueue = queue.length > 0;
  navGroup?.classList.toggle('queue-mode', hasQueue);
  previous.classList.toggle('queue-mode', hasQueue);
  next.classList.toggle('queue-mode', hasQueue);
  previous.querySelector('.nav-main').textContent = 'PREV';
  next.querySelector('.nav-main').textContent = hasQueue ? 'Next Queued' : 'NEXT';
  if (hasQueue) {
    previous.querySelector('.nav-title').textContent = '';
    next.querySelector('.nav-title').textContent = shortTitle(queue[0].title || 'Queued video');
    previous.title = 'Previous video unavailable while queue is active';
    next.title = 'Play next queued: ' + (queue[0].title || 'Queued video');
    next.onclick = playQueueStart;
    previous.disabled = true;
    next.disabled = false;
    return;
  }
  next.onclick = () => playAdjacent('next');
  previous.onclick = () => playAdjacent('previous');
  const previousTitle = (s.previousTitle || '').trim();
  const nextTitle = (s.nextTitle || '').trim();
  previous.querySelector('.nav-title').textContent = previousTitle ? shortTitle(previousTitle) : '';
  next.querySelector('.nav-title').textContent = nextTitle ? shortTitle(nextTitle) : '';
  previous.title = previousTitle ? 'Play previous: ' + previousTitle : 'No previous video';
  next.title = nextTitle ? 'Play next: ' + nextTitle : 'No next video';
  previous.disabled = !previousTitle;
  next.disabled = !nextTitle;
}
function updateQueueControls(s) {
  const card = document.getElementById('queue-card');
  const list = document.getElementById('queue-list');
  const queue = Array.isArray(s.queue) ? s.queue : [];
  queuedVideos.clear();
  for (const item of queue) {
    if (item.path) queuedVideos.add(item.path);
  }
  syncQueuedCards();
  if (!queue.length) {
    list.innerHTML = '<div class="queue-empty">Queue is empty</div>';
    return;
  }
  list.innerHTML = queue
    .map((item, index) => {
      const path = item.path || '';
      const title = item.title || path || 'Queued video';
      const thumb = '/api/thumb?path=' + encodeURIComponent(path);
      return (
        '<div class="queue-item" title="' +
        esc(title) +
        '"><div class="queue-thumb" style="background-image:url(' +
        thumb +
        ')"></div><div class="queue-title">' +
        (index + 1) +
        '. ' +
        esc(title) +
        '</div><div class="queue-actions"><button onclick="moveQueueItem(\'' +
        path +
        "','up')\" " +
        (index === 0 ? 'disabled' : '') +
        '>↑</button><button onclick="moveQueueItem(\'' +
        path +
        "','down')\" " +
        (index === queue.length - 1 ? 'disabled' : '') +
        '>↓</button><button onclick="removeQueueItem(\'' +
        path +
        '\')">✕</button></div></div>'
      );
    })
    .join('');
}
function cardIdFor(p) {
  return 'card-' + String(p).replace(/=/g, '_');
}
function continueCardIdFor(p) {
  return 'continue-card-' + String(p).replace(/=/g, '_');
}
function getMovieCards(p) {
  return [
    document.getElementById(cardIdFor(p)),
    document.getElementById(continueCardIdFor(p)),
    ...Array.from(document.querySelectorAll('[data-path="' + cssEscape(p) + '"]')),
  ].filter((card, index, self) => card && self.indexOf(card) === index);
}
function syncQueuedCards() {
  document.querySelectorAll('.movie-card.queued').forEach((card) => {
    const path = card.dataset.path || card.id.substring(card.id.indexOf('-') + 1);
    if (!queuedVideos.has(path)) {
      setQueuedCard(path, false);
    }
  });
  for (const path of queuedVideos) {
    setQueuedCard(path, true);
  }
}
function setQueuedCard(p, isQueued) {
  const cards = getMovieCards(p);
  for (const card of cards) {
    card.dataset.path = p;
    card.classList.toggle('queued', isQueued);
    let badge = card.querySelector('.queue-badge');
    if (isQueued && !badge) {
      badge = document.createElement('div');
      badge.className = 'queue-badge queued-badge';
      badge.textContent = 'Queued';
      card.querySelector('.movie-title')?.insertAdjacentElement('afterend', badge);
    } else if (!isQueued && badge) {
      badge.remove();
    }

    if (typeof syncVideoStateBadges === 'function') syncVideoStateBadges(card);
    if (typeof syncVideoActionButtons === 'function') syncVideoActionButtons(card);

    const queueButton = findQueueButton(card);
    if (queueButton && typeof syncVideoActionButtons !== 'function')
      queueButton.textContent = isQueued ? 'Unqueue' : 'Queue';
  }
}
function findQueueButton(card) {
  return Array.from(card.querySelectorAll('.card-actions button')).find((btn) =>
    String(btn.getAttribute('onclick') || '').includes('queueCardAction')
  );
}
function shortTitle(value) {
  return value.length > 80 ? value.slice(0, 77) + '...' : value;
}
function buildPlayerMeta(s, volume, boostAmount) {
  const watched = s.duration > 0 ? Math.round((s.position / s.duration) * 100) + '% watched' : '';
  const bits = [
    watched,
    currentPlaybackSpeed.toFixed(2).replace(/\.00$/, '') + 'x',
    'Vol ' + Math.round(volume * 100) + '%',
    'Boost ' + Math.round(boostAmount * 100) + '%',
  ];
  return bits.filter(Boolean).join(' • ');
}
async function setAudioTrack(id) {
  haptic(8);
  await api('/api/audio-track?id=' + encodeURIComponent(id));
}
async function setSubtitleTrack(id) {
  haptic(8);
  await api('/api/subtitle-track?id=' + encodeURIComponent(id));
}
async function setChapter(id) {
  haptic(8);
  await api('/api/chapter?id=' + encodeURIComponent(id));
}
async function setEqPreset(id) {
  haptic(8);
  // Route to the domain-specific endpoint so music/radio also hear the change.
  let endpoint = '/api/eq-preset';
  if (typeof currentMode !== 'undefined') {
    if (currentMode === 'music') endpoint = '/api/music/eq-preset';
    else if (currentMode === 'radio') endpoint = '/api/radio/eq-preset';
  }
  await api(endpoint + '?id=' + encodeURIComponent(id));
  _lastEqPreset = Number(id);
  renderEqPresets(_lastEqPreset);
}

// Room reverb preset names (index matches ReverbSampleProvider.PresetNames)
const _reverbPresetNames = ['Off', 'Studio', 'Small Room', 'Medium Room', 'Large Room', 'Hall', 'Cathedral',
  'Arena', 'Cavern', 'Cave', 'Underwater', 'Pipe Chamber'];
let _lastReverbPreset = 0;       // video
let _lastMusicReverbPreset = 0;  // music
let _lastRadioReverbPreset = 0;  // radio
let _lastMusicReverbStrength = 1;
let _lastMusicReverbDryLevel = 1;
let _lastMusicReverbPreDelayMs = 0;
let _lastMusicReverbBypass = false;
let _musicReverbBasePreset = 0;
let _musicReverbIsCustom = false;
let _lastMusicVocalMode = 0;
let _musicReverbStrengthDebounceHandle = 0;
let _musicReverbDryDebounceHandle = 0;
let _musicReverbPreDelayDebounceHandle = 0;
let _musicReverbLocalChangeUntil = 0;

function _populateReverbSelect(id) {
  const sel = document.getElementById(id);
  if (!sel || sel.options.length) return;
  const options = _reverbPresetNames
    .map((name, i) => '<option value="' + i + '">' + esc(name) + '</option>')
    .join('');

  if (id === 'music-reverb-select') {
    sel.innerHTML = options + '<option value="custom">Custom</option>';
    return;
  }

  sel.innerHTML = options;
}

function renderReverbPresets(currentPreset) {
  _lastReverbPreset = currentPreset;
  _populateReverbSelect('reverb-select');
  const sel = document.getElementById('reverb-select');
  if (sel) sel.value = String(currentPreset);
}

function _renderMusicReverbPreset(preset) {
  const incoming = Number(preset);
  if (!Number.isFinite(incoming)) {
    _populateReverbSelect('music-reverb-select');
    _syncMusicReverbPresetSelect();
    _syncMusicReverbStrengthUi();
    _updateMusicEffectsChips();
    return;
  }

  if (_musicReverbIsCustom && incoming === _musicReverbBasePreset) {
    _lastMusicReverbPreset = incoming;
    _populateReverbSelect('music-reverb-select');
    _syncMusicReverbPresetSelect();
    _syncMusicReverbStrengthUi();
    _updateMusicEffectsChips();
    return;
  }

  if (preset == null || preset === _lastMusicReverbPreset) {
    _populateReverbSelect('music-reverb-select');
    _syncMusicReverbPresetSelect();
    _syncMusicReverbStrengthUi();
    _updateMusicEffectsChips();
    return;
  }

  _lastMusicReverbPreset = incoming;
  _musicReverbBasePreset = incoming;
  _musicReverbIsCustom = false;
  _populateReverbSelect('music-reverb-select');
  _syncMusicReverbPresetSelect();
  _applyMusicReverbPresetDefaults(incoming, false);
  _syncMusicReverbHint();
  _syncMusicReverbStrengthUi();
  _updateMusicEffectsChips();
}

function _syncMusicReverbPresetSelect() {
  const sel = document.getElementById('music-reverb-select');
  if (!sel) return;

  const target = _musicReverbIsCustom ? 'custom' : String(_lastMusicReverbPreset);
  if (sel.value !== target) sel.value = target;
}

function _syncMusicReverbStrengthUi() {
  const slider = document.getElementById('music-reverb-strength');
  const dry = document.getElementById('music-reverb-dry');
  const predelay = document.getElementById('music-reverb-predelay');
  const ab = document.getElementById('music-reverb-ab');
  if (!slider && !dry && !predelay && !ab) return;

  const enabled = _lastMusicReverbPreset > 0;
  if (slider) {
    slider.disabled = !enabled;
    slider.classList.toggle('is-disabled', !enabled);
  }
  if (dry) {
    dry.disabled = !enabled;
    dry.classList.toggle('is-disabled', !enabled);
  }
  if (predelay) {
    predelay.disabled = !enabled;
    predelay.classList.toggle('is-disabled', !enabled);
  }
  if (ab) {
    ab.disabled = !enabled;
  }
}

function _normalizeMusicReverbStrength(value) {
  const n = Number(value);
  if (!Number.isFinite(n)) return _lastMusicReverbStrength;

  return Math.max(0, Math.min(2, n));
}

function _renderMusicReverbStrength(value) {
  const strength = _normalizeMusicReverbStrength(value);
  _lastMusicReverbStrength = strength;

  const slider = document.getElementById('music-reverb-strength');
  if (slider && slider.value !== String(strength)) {
    slider.value = String(strength);
  }

  const label = document.getElementById('music-reverb-strength-label');
  if (label) {
    label.textContent = Math.round(strength * 100) + '%';
  }

  _syncMusicReverbHint();
}

function _normalizeMusicReverbDryLevel(value) {
  const n = Number(value);
  if (!Number.isFinite(n)) return _lastMusicReverbDryLevel;

  return Math.max(0, Math.min(1, n));
}

function _normalizeMusicReverbPreDelay(value) {
  const n = Number(value);
  if (!Number.isFinite(n)) return _lastMusicReverbPreDelayMs;

  return Math.max(0, Math.min(120, n));
}

function _noteMusicReverbLocalChange() {
  _musicReverbLocalChangeUntil = Date.now() + 900;
}

function _renderMusicReverbDryLevel(value) {
  const dry = _normalizeMusicReverbDryLevel(value);
  _lastMusicReverbDryLevel = dry;

  const slider = document.getElementById('music-reverb-dry');
  if (slider && slider.value !== String(dry)) {
    slider.value = String(dry);
  }

  const label = document.getElementById('music-reverb-dry-label');
  if (label) {
    label.textContent = Math.round(dry * 100) + '%';
  }
}

function _renderMusicReverbPreDelay(value) {
  const preDelayMs = _normalizeMusicReverbPreDelay(value);
  _lastMusicReverbPreDelayMs = preDelayMs;

  const slider = document.getElementById('music-reverb-predelay');
  if (slider && slider.value !== String(Math.round(preDelayMs))) {
    slider.value = String(Math.round(preDelayMs));
  }

  const label = document.getElementById('music-reverb-predelay-label');
  if (label) {
    label.textContent = Math.round(preDelayMs) + ' ms';
  }
}

function _renderMusicReverbBypass(value) {
  const bypass = !!value;
  _lastMusicReverbBypass = bypass;

  const ab = document.getElementById('music-reverb-ab');
  if (ab) {
    ab.textContent = bypass ? 'A/B (Bypass)' : 'A/B';
    ab.classList.toggle('active', bypass);
  }

  _syncMusicReverbHint();
}

async function _setMusicReverbDryImmediate(value) {
  const dry = _normalizeMusicReverbDryLevel(value);
  await api('/api/music/reverb-dry?value=' + encodeURIComponent(dry.toFixed(2)));
  _renderMusicReverbDryLevel(dry);
  _saveMusicCustomReverbProfile();
}

function setMusicReverbDryLevel(value) {
  if (_lastMusicReverbPreset <= 0) {
    _syncMusicReverbStrengthUi();
    return;
  }

  _noteMusicReverbLocalChange();
  const dry = _normalizeMusicReverbDryLevel(value);
  _markMusicReverbCustom();
  _renderMusicReverbDryLevel(dry);
  _saveMusicCustomReverbProfile();

  if (_musicReverbDryDebounceHandle) clearTimeout(_musicReverbDryDebounceHandle);
  _musicReverbDryDebounceHandle = setTimeout(() => {
    _musicReverbDryDebounceHandle = 0;
    _setMusicReverbDryImmediate(dry).catch(() => {});
  }, 140);
}

async function _setMusicReverbPreDelayImmediate(value) {
  const preDelayMs = _normalizeMusicReverbPreDelay(value);
  await api('/api/music/reverb-predelay?value=' + encodeURIComponent(preDelayMs.toFixed(0)));
  _renderMusicReverbPreDelay(preDelayMs);
  _saveMusicCustomReverbProfile();
}

function setMusicReverbPreDelay(value) {
  if (_lastMusicReverbPreset <= 0) {
    _syncMusicReverbStrengthUi();
    return;
  }

  _noteMusicReverbLocalChange();
  const preDelayMs = _normalizeMusicReverbPreDelay(value);
  _markMusicReverbCustom();
  _renderMusicReverbPreDelay(preDelayMs);
  _saveMusicCustomReverbProfile();

  if (_musicReverbPreDelayDebounceHandle) clearTimeout(_musicReverbPreDelayDebounceHandle);
  _musicReverbPreDelayDebounceHandle = setTimeout(() => {
    _musicReverbPreDelayDebounceHandle = 0;
    _setMusicReverbPreDelayImmediate(preDelayMs).catch(() => {});
  }, 140);
}

async function setMusicReverbBypass(value) {
  const bypass = !!value;
  _noteMusicReverbLocalChange();
  _renderMusicReverbBypass(bypass);
  await api('/api/music/reverb-bypass?value=' + encodeURIComponent(bypass ? '1' : '0'));
}

function toggleMusicReverbAb() {
  if (_lastMusicReverbPreset <= 0) return;

  setMusicReverbBypass(!_lastMusicReverbBypass).catch(() => {});
}

function _syncMusicReverbHint() {
  const hint = document.getElementById('music-reverb-hint');
  if (!hint) return;

  if (_lastMusicReverbPreset <= 0) {
    hint.textContent = 'Enable a Room preset to adjust strength, dry blend, and pre-delay.';
    return;
  }

  if (_lastMusicReverbBypass) {
    hint.textContent = 'A/B is bypassed. Tap A/B again to re-enable Room effect.';
    return;
  }

  const prefix = _musicReverbIsCustom
    ? 'Custom room. '
    : 'Preset defaults. ';

  hint.textContent = prefix + 'Strength ' + Math.round(_lastMusicReverbStrength * 100)
    + '%, Dry ' + Math.round(_lastMusicReverbDryLevel * 100)
    + '%, Pre-delay ' + Math.round(_lastMusicReverbPreDelayMs) + ' ms.';
}

function _defaultMusicReverbProfile(preset) {
  const p = Number(preset);
  switch (p) {
    case 1: return { strength: 0.75, dry: 0.95, preDelayMs: 8 };
    case 2: return { strength: 0.85, dry: 0.93, preDelayMs: 12 };
    case 3: return { strength: 1.00, dry: 0.90, preDelayMs: 18 };
    case 4: return { strength: 1.10, dry: 0.88, preDelayMs: 24 };
    case 5: return { strength: 1.20, dry: 0.84, preDelayMs: 32 };
    case 6: return { strength: 1.30, dry: 0.80, preDelayMs: 45 };
    case 7: return { strength: 1.25, dry: 0.82, preDelayMs: 38 };
    case 8: return { strength: 1.35, dry: 0.78, preDelayMs: 52 };
    case 9: return { strength: 1.30, dry: 0.80, preDelayMs: 48 };
    case 10: return { strength: 0.90, dry: 0.92, preDelayMs: 20 };
    case 11: return { strength: 1.05, dry: 0.90, preDelayMs: 28 };
    default: return { strength: 1.00, dry: 1.00, preDelayMs: 0 };
  }
}

function _loadMusicCustomReverbProfile() {
  try {
    const raw = localStorage.getItem('musicReverbCustomProfile');
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object') return null;

    return {
      strength: _normalizeMusicReverbStrength(parsed.strength),
      dry: _normalizeMusicReverbDryLevel(parsed.dry),
      preDelayMs: _normalizeMusicReverbPreDelay(parsed.preDelayMs)
    };
  } catch (_) {
    return null;
  }
}

function _saveMusicCustomReverbProfile() {
  try {
    const payload = {
      strength: _lastMusicReverbStrength,
      dry: _lastMusicReverbDryLevel,
      preDelayMs: _lastMusicReverbPreDelayMs
    };
    localStorage.setItem('musicReverbCustomProfile', JSON.stringify(payload));
  } catch (_) {
    // best effort
  }
}

function _applyMusicReverbPresetDefaults(preset, sendToServer) {
  const p = Number(preset);
  if (!Number.isFinite(p) || p <= 0) return;

  const defaults = _defaultMusicReverbProfile(p);
  _renderMusicReverbStrength(defaults.strength);
  _renderMusicReverbDryLevel(defaults.dry);
  _renderMusicReverbPreDelay(defaults.preDelayMs);

  if (!sendToServer) return;

  _noteMusicReverbLocalChange();
  _setMusicReverbStrengthImmediate(defaults.strength).catch(() => {});
  _setMusicReverbDryImmediate(defaults.dry).catch(() => {});
  _setMusicReverbPreDelayImmediate(defaults.preDelayMs).catch(() => {});
}

function _markMusicReverbCustom() {
  if (_lastMusicReverbPreset <= 0) return;
  if (_musicReverbIsCustom) return;

  _musicReverbBasePreset = _lastMusicReverbPreset;
  _musicReverbIsCustom = true;
  _syncMusicReverbPresetSelect();
}

async function _setMusicReverbStrengthImmediate(value) {
  const strength = _normalizeMusicReverbStrength(value);
  await api('/api/music/reverb-strength?value=' + encodeURIComponent(strength.toFixed(2)));
  _noteMusicReverbLocalChange();
  _renderMusicReverbStrength(strength);
  _saveMusicCustomReverbProfile();
  _updateMusicEffectsChips();
}

function setMusicReverbStrength(value) {
  if (_lastMusicReverbPreset <= 0) {
    _syncMusicReverbStrengthUi();
    return;
  }

  _noteMusicReverbLocalChange();
  const strength = _normalizeMusicReverbStrength(value);
  _markMusicReverbCustom();
  _renderMusicReverbStrength(strength);
  _saveMusicCustomReverbProfile();
  _updateMusicEffectsChips();

  if (_musicReverbStrengthDebounceHandle) {
    clearTimeout(_musicReverbStrengthDebounceHandle);
  }

  _musicReverbStrengthDebounceHandle = setTimeout(() => {
    _musicReverbStrengthDebounceHandle = 0;
    _setMusicReverbStrengthImmediate(strength).catch(() => {});
  }, 120);
}

function _renderRadioReverbPreset(preset) {
  if (preset == null || preset === _lastRadioReverbPreset) { _populateReverbSelect('radio-reverb-select'); return; }
  _lastRadioReverbPreset = preset;
  _populateReverbSelect('radio-reverb-select');
  const sel = document.getElementById('radio-reverb-select');
  if (sel && sel.value !== String(preset)) sel.value = String(preset);
}

async function setReverbPreset(id, domain) {
  haptic(8);
  if (domain === 'music') {
    if (String(id) === 'custom') {
      const custom = _loadMusicCustomReverbProfile() || {
        strength: _lastMusicReverbStrength,
        dry: _lastMusicReverbDryLevel,
        preDelayMs: _lastMusicReverbPreDelayMs
      };

      _musicReverbIsCustom = true;
      _syncMusicReverbPresetSelect();
      _renderMusicReverbStrength(custom.strength);
      _renderMusicReverbDryLevel(custom.dry);
      _renderMusicReverbPreDelay(custom.preDelayMs);
      _noteMusicReverbLocalChange();
      await _setMusicReverbStrengthImmediate(custom.strength);
      await _setMusicReverbDryImmediate(custom.dry);
      await _setMusicReverbPreDelayImmediate(custom.preDelayMs);
      _syncMusicReverbHint();
      _updateMusicEffectsChips();
      return;
    }

    const presetId = Number(id);
    if (!Number.isFinite(presetId)) return;

    await api('/api/music/reverb-preset?id=' + encodeURIComponent(id));
    _noteMusicReverbLocalChange();
    _lastMusicReverbPreset = presetId;
    _musicReverbBasePreset = presetId;
    _musicReverbIsCustom = false;
    _syncMusicReverbPresetSelect();
    _syncMusicReverbStrengthUi();
    _applyMusicReverbPresetDefaults(_lastMusicReverbPreset, true);
    _syncMusicReverbHint();
    _updateMusicEffectsChips();
  } else if (domain === 'radio') {
    await api('/api/radio/reverb-preset?id=' + encodeURIComponent(id));
    _lastRadioReverbPreset = Number(id);
  } else {
    await api('/api/reverb-preset?id=' + encodeURIComponent(id));
    _lastReverbPreset = Number(id);
    renderReverbPresets(_lastReverbPreset);
  }
}

function _vocalModeLabel(mode) {
  const m = Number(mode);
  return m === 1 ? 'Mild' : m === 2 ? 'Strong' : m === 3 ? 'Karaoke' : 'Off';
}

function _normalizeVocalMode(value) {
  const raw = String(value ?? '').trim().toLowerCase();
  if (raw === '1' || raw === 'mild') return 1;
  if (raw === '2' || raw === 'strong' || raw === 'true' || raw === 'on') return 2;
  if (raw === '3' || raw === 'karaoke') return 3;
  return 0;
}

function _renderMusicVocalRemoval(value) {
  const mode = _normalizeVocalMode(value);
  _lastMusicVocalMode = mode;
  const sel = document.getElementById('music-vocal-remove');
  if (sel && sel.value !== String(mode)) sel.value = String(mode);
  _updateMusicEffectsChips();
}

async function setMusicVocalRemoval(value) {
  haptic(8);
  const mode = _normalizeVocalMode(value);
  await api('/api/music/vocal-removal?value=' + encodeURIComponent(String(mode)));
  _renderMusicVocalRemoval(mode);
}

// VLC built-in equalizer preset names (order matches VLC preset index 0-17)
const _eqPresetNames = [
  'Flat', 'Classical', 'Club', 'Dance', 'Bass Boost', 'Bass + Treble Boost',
  'Treble Boost', 'Headphones', 'Large Hall', 'Live', 'Party', 'Pop',
  'Reggae', 'Rock', 'Ska', 'Soft', 'Soft Rock', 'Techno'
];
// VLC built-in preset gain values (dB, 10 bands: 60Hz 170Hz 310Hz 600Hz 1kHz 3kHz 6kHz 12kHz 14kHz 16kHz)
// Source: vlc/modules/audio_filter/equalizer_presets.h
const _eqPresetGains = [
  [ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],           // 0 Flat (EQ on)
  [ 4, 4, 4, 0, 0, 0, 0, 0,-4,-4],           // 1 Classical
  [ 0, 0, 8, 5, 5, 5, 3, 0, 0, 0],           // 2 Club
  [ 8, 6, 5, 0,-2,-3, 6, 9, 10, 8],          // 3 Dance
  [10, 9, 8, 1,-2,-4,-4,-2, 0, 0],           // 4 Full Bass
  [ 7, 5, 0,-6,-4, 2, 8, 11,12,12],          // 5 Full Bass & Treble
  [-8,-6,-4,-3, 0, 7, 9, 10,11,12],          // 6 Full Treble
  [ 5, 4, 1, 0,-2, 3, 5, 9, 10, 9],          // 7 Headphones
  [12, 8, 5, 0, 0,-4,-7,-8,-7,-7],           // 8 Large Hall
  [-4,-3,-1, 3, 4, 3, 0,-2,-2,-2],           // 9 Live
  [ 5, 5, 0, 0, 0, 0, 5, 5, 5, 5],           // 10 Party
  [-2,-1, 0, 2, 4, 4, 1, 0, 0, 0],           // 11 Pop
  [ 0, 0, 0,-2,-5, 0, 6, 6, 6, 2],           // 12 Reggae
  [ 8, 5,-5,-8,-3, 4, 8, 8, 5, 2],           // 13 Rock
  [-3,-1, 3, 5, 3,-1,-3,-3,-3,-3],           // 14 Ska
  [-2,-2, 0, 2, 3, 3, 2, 0, 0, 0],           // 15 Soft
  [ 4, 4, 2, 0,-4,-3, 0, 2, 8, 9],           // 16 Soft Rock
  [ 8, 5, 0,-6,-4, 4, 9, 9, 8, 7]            // 17 Techno
];
function _drawEqSparkline(canvasId, presetIndex) {
  const cv = document.getElementById(canvasId);
  if (!cv) return;

  const rect = cv.getBoundingClientRect();
  const cssWidth = Math.max(16, Math.round(rect.width || cv.clientWidth || cv.width));
  const cssHeight = Math.max(16, Math.round(rect.height || cv.clientHeight || cv.height));
  const dpr = Math.max(1, window.devicePixelRatio || 1);
  const targetWidth = Math.max(1, Math.round(cssWidth * dpr));
  const targetHeight = Math.max(1, Math.round(cssHeight * dpr));
  if (cv.width !== targetWidth || cv.height !== targetHeight) {
    cv.width = targetWidth;
    cv.height = targetHeight;
  }

  const ctx = cv.getContext('2d');
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.clearRect(0, 0, cv.width, cv.height);
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  const w = cssWidth, h = cssHeight;
  // Resolve CSS variable now (Canvas 2D does not support CSS custom properties in strokeStyle)
  const accentColor = getComputedStyle(document.documentElement).getPropertyValue('--player-accent').trim() || '#e94560';
  if (presetIndex < 0 || presetIndex >= _eqPresetGains.length) {
    // Off (flat) — draw a flat midline
    ctx.strokeStyle = 'rgba(158,162,184,0.45)';
    ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(0, h / 2); ctx.lineTo(w, h / 2); ctx.stroke();
    return;
  }
  const gains = _eqPresetGains[presetIndex];
  const maxDb = 12, n = gains.length;
  const pad = 2;
  ctx.strokeStyle = accentColor;
  ctx.lineWidth = 1.5;
  ctx.lineJoin = 'round';
  ctx.lineCap = 'round';
  ctx.beginPath();
  for (let i = 0; i < n; i++) {
    const x = pad + (i / (n - 1)) * (w - pad * 2);
    const y = pad + ((maxDb - gains[i]) / (maxDb * 2)) * (h - pad * 2);
    i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
  }
  ctx.stroke();
  // zero-db reference line (faint)
  ctx.strokeStyle = 'rgba(158,162,184,0.25)';
  ctx.lineWidth = 0.7;
  ctx.setLineDash([2, 2]);
  const midY = pad + (h - pad * 2) / 2;
  ctx.beginPath(); ctx.moveTo(pad, midY); ctx.lineTo(w - pad, midY); ctx.stroke();
  ctx.setLineDash([]);
}

let _lastEqPreset = -1;

function _eqPresetLabel(preset) {
  const p = Number(preset);
  if (!Number.isFinite(p) || p < 0) return 'Off';

  return _eqPresetNames[p] || 'Custom';
}

function _reverbPresetLabel(preset) {
  const p = Number(preset);
  if (!Number.isFinite(p) || p < 0) return 'Off';

  return _reverbPresetNames[p] || 'Off';
}

function _isNarrowViewport() {
  return window.matchMedia('(max-width: 900px)').matches;
}

function _updateMusicEffectsChipsVisibility() {
  const wrap = document.getElementById('music-effects-status');
  if (!wrap) return;

  const playing = typeof musicIsPlaying !== 'undefined' && !!musicIsPlaying;
  wrap.style.display = (_isNarrowViewport() && playing) ? 'flex' : 'none';
}

function _updateMusicEffectsChips() {
  const eqChip = document.getElementById('music-effect-chip-eq');
  const roomChip = document.getElementById('music-effect-chip-room');
  const vocalChip = document.getElementById('music-effect-chip-vocal');
  if (!eqChip || !roomChip || !vocalChip) return;

  const eqLabel = _eqPresetLabel(_lastEqPreset);
  const roomLabel = _reverbPresetLabel(_lastMusicReverbPreset);
  const roomStrength = Math.round(_lastMusicReverbStrength * 100);
  const vocalLabel = _vocalModeLabel(_lastMusicVocalMode);

  eqChip.textContent = 'EQ: ' + eqLabel;
  roomChip.textContent = _lastMusicReverbPreset > 0
    ? 'Room: ' + roomLabel + ' ' + roomStrength + '%'
    : 'Room: Off';
  vocalChip.textContent = 'Vocal: ' + vocalLabel;

  eqChip.classList.toggle('inactive', _lastEqPreset < 0);
  roomChip.classList.toggle('inactive', !_lastMusicReverbPreset || _lastMusicReverbStrength <= 0.001);
  vocalChip.classList.toggle('inactive', _lastMusicVocalMode === 0);

  _updateMusicEffectsChipsVisibility();
}

async function resetMusicEffects() {
  haptic(8);
  await setEqPreset(-1);
  await setReverbPreset(0, 'music');
  await _setMusicReverbStrengthImmediate(1);
  await _setMusicReverbDryImmediate(1);
  await _setMusicReverbPreDelayImmediate(0);
  await setMusicReverbBypass(false);
  await setMusicVocalRemoval(0);
  _updateMusicEffectsChips();
}

function saveMusicEffectsDefaults() {
  try {
    const defaults = {
      eqPreset: _lastEqPreset,
      reverbPreset: _musicReverbIsCustom ? _musicReverbBasePreset : _lastMusicReverbPreset,
      reverbCustom: _musicReverbIsCustom,
      reverbStrength: _lastMusicReverbStrength,
      reverbDryLevel: _lastMusicReverbDryLevel,
      reverbPreDelayMs: _lastMusicReverbPreDelayMs,
      reverbBypass: _lastMusicReverbBypass,
      vocalMode: _lastMusicVocalMode
    };

    localStorage.setItem('musicEffectsDefaults', JSON.stringify(defaults));
    setStatus('Saved music effect defaults for this device.');
  } catch (_) {
    setStatus('Could not save defaults on this device.');
  }
}

async function applySavedMusicEffectsDefaults() {
  try {
    const raw = localStorage.getItem('musicEffectsDefaults');
    if (!raw) return;
    const saved = JSON.parse(raw);
    if (!saved || typeof saved !== 'object') return;

    if (typeof saved.eqPreset === 'number') await setEqPreset(saved.eqPreset);
    if (typeof saved.reverbPreset === 'number') await setReverbPreset(saved.reverbPreset, 'music');
    if (saved.reverbCustom === true) {
      _musicReverbIsCustom = true;
      _syncMusicReverbPresetSelect();
      if (typeof saved.reverbStrength === 'number') await _setMusicReverbStrengthImmediate(saved.reverbStrength);
      if (typeof saved.reverbDryLevel === 'number') await _setMusicReverbDryImmediate(saved.reverbDryLevel);
      if (typeof saved.reverbPreDelayMs === 'number') await _setMusicReverbPreDelayImmediate(saved.reverbPreDelayMs);
      _saveMusicCustomReverbProfile();
    }
    if (typeof saved.reverbBypass === 'boolean') await setMusicReverbBypass(saved.reverbBypass);
    if (typeof saved.vocalMode === 'number') await setMusicVocalRemoval(saved.vocalMode);
  } catch (_) {
    // ignore malformed local defaults
  }
}

function renderEqPresets(currentPreset) {
  _lastEqPreset = currentPreset;
  const ids = ['eq-select', 'radio-eq-select', 'music-eq-select'];
  const sparkIds = ['eq-sparkline', 'radio-eq-sparkline', 'music-eq-sparkline'];
  ids.forEach((id, idx) => {
    const sel = document.getElementById(id);
    if (!sel) return;
    if (!sel.options.length) {
      const opts = [{ id: -1, name: 'Off' }].concat(
        _eqPresetNames.map((name, i) => ({ id: i, name }))
      );
      sel.innerHTML = opts
        .map((o) => '<option value="' + o.id + '">' + esc(o.name) + '</option>')
        .join('');
    }
    sel.value = String(currentPreset);
    _drawEqSparkline(sparkIds[idx], currentPreset);
  });
  _updateMusicEffectsChips();
}
function _syncEqWrapVisibility() {
  // Ensure all selects are populated (no-op if already done)
  renderEqPresets(_lastEqPreset);
  _populateReverbSelect('music-reverb-select');
  _populateReverbSelect('radio-reverb-select');
  const radioPlaying = window._radioIsPlaying && true;
  const musicPlaying = typeof musicIsPlaying !== 'undefined' && musicIsPlaying;
  const radioWrap = document.getElementById('radio-eq-select-wrap');
  if (radioWrap) radioWrap.style.display = radioPlaying ? '' : 'none';
  const musicWrap = document.getElementById('music-eq-select-wrap');
  if (musicWrap) musicWrap.style.display = musicPlaying ? '' : 'none';
  const radioReverbWrap = document.getElementById('radio-reverb-select-wrap');
  if (radioReverbWrap) radioReverbWrap.style.display = radioPlaying ? '' : 'none';
  const musicReverbWrap = document.getElementById('music-reverb-select-wrap');
  if (musicReverbWrap) musicReverbWrap.style.display = musicPlaying ? '' : 'none';
  if (musicPlaying) {
    _renderMusicReverbStrength(_lastMusicReverbStrength);
    _renderMusicReverbDryLevel(_lastMusicReverbDryLevel);
    _renderMusicReverbPreDelay(_lastMusicReverbPreDelayMs);
    _renderMusicReverbBypass(_lastMusicReverbBypass);
    _syncMusicReverbStrengthUi();
    _syncMusicReverbHint();
  }
  const musicVocalWrap = document.getElementById('music-vocal-remove-wrap');
  if (musicVocalWrap) musicVocalWrap.style.display = musicPlaying ? '' : 'none';
  // Sparklines must be redrawn after the canvas becomes visible
  if (radioPlaying) _drawEqSparkline('radio-eq-sparkline', _lastEqPreset);
  if (musicPlaying) _drawEqSparkline('music-eq-sparkline', _lastEqPreset);
  _updateMusicEffectsChipsVisibility();
}

function _syncMusicReverbFromStatus(status) {
  if (!status) return;

  const now = Date.now();
  if (now < _musicReverbLocalChangeUntil) {
    _syncMusicReverbHint();
    return;
  }

  const statusPreset = Number(status.reverbPreset ?? status.ReverbPreset ?? _lastMusicReverbPreset);
  if (Number.isFinite(statusPreset) && statusPreset >= 0) {
    _lastMusicReverbPreset = statusPreset;
    if (!_musicReverbIsCustom) {
      _musicReverbBasePreset = statusPreset;
    }
  }

  _renderMusicReverbStrength(status.reverbStrength ?? status.ReverbStrength ?? _lastMusicReverbStrength);
  _renderMusicReverbDryLevel(status.reverbDryLevel ?? status.ReverbDryLevel ?? _lastMusicReverbDryLevel);
  _renderMusicReverbPreDelay(status.reverbPreDelayMs ?? status.ReverbPreDelayMs ?? _lastMusicReverbPreDelayMs);
  _renderMusicReverbBypass(status.reverbBypass ?? status.ReverbBypass ?? false);
  _syncMusicReverbPresetSelect();
  _syncMusicReverbStrengthUi();
  _syncMusicReverbHint();
}
async function playAdjacent(direction) {
  haptic(10);
  await api('/api/adjacent?direction=' + encodeURIComponent(direction));
  // Poll until the new file is playing (up to ~2s)
  let tries = 0;
  const poll = setInterval(async () => {
    try {
      const res = await fetch('/api/status');
      if (!res.ok) {
        if (++tries > 8) clearInterval(poll);
        return;
      }
      const s = await res.json();
      if (s.isPlaying && s.filePath && s.filePath !== playingPath) {
        clearInterval(poll);
        const oldPath = playingPath;
        playingPath = s.filePath;
        markPlayed(s.filePath);
        updatePlayingCard(s.filePath, oldPath);
      } else if (++tries > 8) {
        clearInterval(poll);
      }
    } catch (e) {
      if (++tries > 8) clearInterval(poll);
    }
  }, 250);
}
async function rescan() {
  setStatus('Refreshing search index...');
  try {
    const res = await fetch('/api/rescan');
    if (res.ok) updateLibraryStatus((await res.json()).scan);
  } catch (e) {
    setStatus('Command failed: ' + e);
  }
}

function resetCardsScrollTop() {
  const browser = document.getElementById('browser');
  if (browser) browser.scrollTop = 0;
  document.documentElement.scrollTop = 0;
  document.body.scrollTop = 0;
  window.scrollTo(0, 0);
}
async function loadRecent() {
  try {
    const res = await fetch('/api/recent');
    if (!res.ok) return [];
    const data = await res.json();
    return Array.isArray(data.files) ? data.files : [];
  } catch (e) {
    return [];
  }
}
async function loadFavorites() {
  try {
    const res = await fetch('/api/favorites');
    if (!res.ok) return [];
    const data = await res.json();
    const files = Array.isArray(data.files) ? data.files : [];
    favoriteVideos.clear();
    files.forEach((f) => favoriteVideos.add(f.path));
    return files;
  } catch (e) {
    return [];
  }
}
let currentIsLinkedDir = false;
let _videoLinkBase = null;

function _normalizeVideoPathPrefix(path) {
  return (path || '').replace(/[\\/]+$/, '');
}

function _applyVideoVirtualBreadcrumbs(breadcrumbs) {
  if (!Array.isArray(breadcrumbs) || !breadcrumbs.length || !_videoLinkBase) return breadcrumbs;

  const realBasePath = _normalizeVideoPathPrefix(_videoLinkBase.real || '');
  const virtualBase = _videoLinkBase.virtual || '';
  if (!realBasePath || !virtualBase) return breadcrumbs;

  const rootDir = _decodePath(breadcrumbs[0] && breadcrumbs[0].dir ? breadcrumbs[0].dir : '');
  const normalizedRoot = _normalizeVideoPathPrefix(rootDir || '');
  if (!normalizedRoot) return breadcrumbs;

  const currentCrumb = breadcrumbs[breadcrumbs.length - 1];
  const currentRealDir = _normalizeVideoPathPrefix(_decodePath(currentCrumb && currentCrumb.dir ? currentCrumb.dir : '') || '');
  if (!currentRealDir) return breadcrumbs;

  const realBaseLower = realBasePath.toLowerCase();
  const currentLower = currentRealDir.toLowerCase();
  if (!currentLower.startsWith(realBaseLower)) return breadcrumbs;

  const currentVirtualDir = virtualBase + currentRealDir.slice(realBasePath.length);
  const rel = currentVirtualDir.startsWith(normalizedRoot)
    ? currentVirtualDir.slice(normalizedRoot.length)
    : '';
  const sep = rel.indexOf('/') !== -1 ? '/' : '\\';
  const parts = rel.split(sep).filter((p) => p && p.trim());
  if (!parts.length) return breadcrumbs;

  const remapped = [breadcrumbs[0]];
  let virtualCumulative = normalizedRoot;
  for (const part of parts) {
    virtualCumulative += sep + part;
    const normalizedVirtual = _normalizeVideoPathPrefix(virtualCumulative);
    const realForClick = normalizedVirtual.toLowerCase().startsWith(virtualBase.toLowerCase())
      ? realBasePath + normalizedVirtual.slice(virtualBase.length)
      : normalizedVirtual;

    remapped.push({
      name: part,
      dir: _encodePath(realForClick)
    });
  }

  return remapped;
}

async function browse(d, offset = 0, append = false, pushHistory = true, isLinked = false, virtualPath = null) {
  console.log('[VIDEO] browse called: d =', d, ', append =', append);
  // Push the current location onto the back-stack before navigating (not for pagination)
  if (!append && pushHistory && currentDir !== d && currentDir !== null)
    browseHistory.push(currentDir);
  if (!append && d === null) browseHistory = [];
  if (!append) {
    if (isLinked && d && virtualPath) {
      const decodedFolder = _decodePath(d);
      if (decodedFolder) {
        _videoLinkBase = {
          real: _normalizeVideoPathPrefix(decodedFolder),
          virtual: _normalizeVideoPathPrefix(virtualPath)
        };
      }
    } else if (_videoLinkBase && d) {
      const decodedFolder = _decodePath(d);
      if (!decodedFolder || !_normalizeVideoPathPrefix(decodedFolder).toLowerCase().startsWith(_videoLinkBase.real.toLowerCase())) {
        _videoLinkBase = null;
      }
    } else {
      _videoLinkBase = null;
    }

    currentIsLinkedDir = Boolean(_videoLinkBase);
  }
  if (!append) _hideSearchFilterBar();
  setStatus('Loading...');
  currentDir = d;
  console.log('[VIDEO] currentDir set to:', currentDir);
  const videoSearch = document.getElementById('vcb-search');
  if (videoSearch) videoSearch.value = '';
  if (!append) {
    clearPendingThumbnails();
    setBrowseLoading(true);
  }
  try {
    const url =
      (d ? '/api/browse?dir=' + encodeURIComponent(d) : '/api/browse') +
      (offset ? '&offset=' + encodeURIComponent(offset) : '');
    console.log('[VIDEO] Fetching URL:', url);
    const res = await fetch(url);
    if (!res.ok) {
      setBrowseLoading(false);
      setStatus('Server error ' + res.status);
      return;
    }
    const nextData = await res.json();
    if (append && currentData) {
      currentData.files = [...(currentData.files || []), ...(nextData.files || [])];
      currentData.offset = nextData.offset;
      currentData.limit = nextData.limit;
      currentData.totalFiles = nextData.totalFiles;
      currentData.hasMoreFiles = nextData.hasMoreFiles;
    } else currentData = nextData;

    if (!append) {
      const folderPath = d ? _decodePath(d) : null;
      let friendlyPath = null;
      if (nextData.breadcrumbs && nextData.breadcrumbs.length) {
        nextData.breadcrumbs = _applyVideoVirtualBreadcrumbs(nextData.breadcrumbs);
      }
      if (nextData.breadcrumbs && nextData.breadcrumbs.length) {
        console.log('[VIDEO] Breadcrumbs from server:', nextData.breadcrumbs);
        friendlyPath = _buildFriendlyPathFromBreadcrumbs(nextData.breadcrumbs);
        console.log('[VIDEO] Friendly path from breadcrumbs:', friendlyPath);
      } else if (folderPath) {
        friendlyPath = _encodeRouteFolder(folderPath);
        console.log('[VIDEO] Friendly path from folder:', friendlyPath);
      }

      if (friendlyPath || folderPath) {
        // Build URL with friendly names + base64url-encoded path at the end
        let path = '/video/';
        if (friendlyPath) {
          path += friendlyPath;
        }
        if (folderPath) {
          const encoded = _folderPathToBase64Url(folderPath);
          if (encoded) {
            if (friendlyPath) path += '/';
            path += encoded;
          }
        }
        console.log('[VIDEO] Setting URL to:', path);
        if (window.history && typeof window.history.replaceState === 'function') {
          window.history.replaceState(window.history.state, '', path);
        }
      } else {
        if (window.history && typeof window.history.replaceState === 'function') {
          window.history.replaceState(window.history.state, '', '/video');
        }
      }
    }

    render(currentData);
    setBrowseLoading(false); // dismiss overlay as soon as content is rendered
    if (typeof renderPinnedStrip === 'function') renderPinnedStrip();
    if (typeof _updatePinButton === 'function') _updatePinButton();
    if (!append) {
      renderFavorites(await loadFavorites());
      renderRecent(await loadRecent());
    }
    if (!append) resetCardsScrollTop();
  } catch (e) {
    setStatus('Error: ' + e);
  } finally {
    setBrowseLoading(false); // safety: ensure overlay is gone even on error path
  }
}
function setBrowseLoading(on) {
  document.getElementById('browse-loading').classList.toggle('visible', on);
}
function _getDomainSearchInput(domain) {
  if (domain === 'music') return document.getElementById('mcb-search');
  if (domain === 'video') return document.getElementById('vcb-search');
  return null;
}

function setSearchBusy(on, domain = currentMode) {
  const inp = _getDomainSearchInput(domain);
  if (!inp) return;
  inp.classList.toggle('searching', on);
}

// ── Search filter bar ─────────────────────────────────────────────────────
let _searchFilterMode   = 'all'; // 'all' | 'title' | 'artist' | 'album'
let _lastSearchQuery    = '';    // kept in sync with the last query that produced results
let _totalSearchResults = 0;     // total cards before any filter is applied

function _updateSearchCountText(visible, total) {
  const txt = document.getElementById('count-text');
  if (!txt) return;
  if (_searchFilterMode === 'all' || visible === total) {
    txt.textContent = total + ' result' + (total !== 1 ? 's' : '') + ' found';
  } else {
    const modeName = { title: 'title', artist: 'artist', album: 'album' }[_searchFilterMode] || _searchFilterMode;
    txt.textContent = visible + ' of ' + total + ' result' + (total !== 1 ? 's' : '') + ' (filter: ' + modeName + ')';
  }
}

function _showSearchFilterBar(isMusic) {
  const bar = document.getElementById('search-filter-bar');
  if (!bar) return;
  // artist / album chips are only meaningful for music
  bar.querySelectorAll('.sf-music-only').forEach(el => {
    el.style.display = isMusic ? '' : 'none';
  });
  bar.style.display = 'flex';
  // Capture total result count (called right after results are rendered)
  _totalSearchResults = document.querySelectorAll('.music-track-card, .movie-card').length;
}

function _hideSearchFilterBar() {
  const bar = document.getElementById('search-filter-bar');
  if (!bar) return;
  bar.style.display = 'none';
  // reset selection to "All" so next search starts clean
  _searchFilterMode = 'all';
  _totalSearchResults = 0;
  const allRadio = bar.querySelector('input[value="all"]');
  if (allRadio) allRadio.checked = true;
}

function setSearchFilter(mode) {
  _searchFilterMode = mode;
  _applySearchFilter();
}

function _applySearchFilter() {
  const q = _lastSearchQuery.toLowerCase().trim();
  if (!q || _searchFilterMode === 'all') {
    // show everything
    document.querySelectorAll('.music-track-card, .movie-card').forEach(c => c.style.display = '');
    _updateSearchCountText(_totalSearchResults, _totalSearchResults);
    return;
  }
  let visible = 0;
  if (_searchFilterMode === 'title') {
    document.querySelectorAll('.music-track-card').forEach(c => {
      const t = (c.dataset.srTitle || '').toLowerCase();
      const show = t.includes(q);
      c.style.display = show ? '' : 'none';
      if (show) visible++;
    });
    document.querySelectorAll('.movie-card').forEach(c => {
      const t = (c.dataset.srTitle || '').toLowerCase();
      const show = t.includes(q);
      c.style.display = show ? '' : 'none';
      if (show) visible++;
    });
  } else if (_searchFilterMode === 'artist') {
    document.querySelectorAll('.music-track-card').forEach(c => {
      const a = (c.dataset.srArtist || '').toLowerCase();
      const show = a.includes(q);
      c.style.display = show ? '' : 'none';
      if (show) visible++;
    });
  } else if (_searchFilterMode === 'album') {
    document.querySelectorAll('.music-track-card').forEach(c => {
      const a = (c.dataset.srAlbum || '').toLowerCase();
      const show = a.includes(q);
      c.style.display = show ? '' : 'none';
      if (show) visible++;
    });
  }
  _updateSearchCountText(visible, _totalSearchResults);
}
function onDomainSearch(domain) {
  if (domain !== 'music' && domain !== 'video') return;

  const inp = _getDomainSearchInput(domain);
  const q = (inp?.value || '').toLowerCase().trim();
  clearTimeout(searchTimer);

  if (domain === 'music') {
    if (!q) {
      _hideSearchFilterBar();
      setSearchBusy(false, 'music');
      browseMusic(null);
      return;
    }

    setSearchBusy(true, 'music');
    searchTimer = setTimeout(() => searchMusicLibrary(q), 300);
    return;
  }

  if (!q) {
    setSearchBusy(false, 'video');
    _hideSearchFilterBar();
    if (pendingSearchAbort) {
      pendingSearchAbort.abort();
      pendingSearchAbort = null;
    }
    browse(null);
    return;
  }

  clearPendingThumbnails();
  setSearchBusy(true, 'video');
  searchTimer = setTimeout(() => searchLibrary(q), 300);
}

function onSearch() {
  if (currentMode === 'music' || currentMode === 'video') {
    onDomainSearch(currentMode);
  }
}
function clearPendingThumbnails() {
  if (pendingSearchAbort) {
    pendingSearchAbort.abort();
    pendingSearchAbort = null;
  }
  if (thumbnailObserver) {
    thumbnailObserver.disconnect();
    thumbnailObserver = null;
  }
  document.querySelectorAll('.movie-card').forEach((card) => {
    card.style.backgroundImage = 'none';
    card.classList.remove('has-poster');
  });
}

function observeMovieCards() {
  if (thumbnailObserver) {
    thumbnailObserver.disconnect();
    thumbnailObserver = null;
  }
  const cards = Array.from(document.querySelectorAll('.movie-card[data-thumb]'));
  if (!cards.length) return;
  if (!('IntersectionObserver' in window)) {
    cards.forEach((card) => {
      card.style.backgroundImage = 'url(' + card.dataset.thumb + ')';
      card.classList.add('has-poster');
      card.removeAttribute('data-thumb');
    });
    return;
  }

  thumbnailObserver = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        const card = entry.target;
        const thumb = card.dataset.thumb;
        if (thumb) { card.style.backgroundImage = 'url(' + thumb + ')'; card.classList.add('has-poster'); }
        card.removeAttribute('data-thumb');
        thumbnailObserver.unobserve(card);
      }
    },
    { rootMargin: '420px 0px', threshold: 0.01 }
  );
  cards.forEach((card) => thumbnailObserver.observe(card));
}

// ── Pinned folders (feature 11) ─────────────────────────────────────────────
const PINNED_FOLDERS_KEY = 'pinnedFolders';

function _getPinnedFolders() {
  try { return JSON.parse(localStorage.getItem(PINNED_FOLDERS_KEY) || '[]'); } catch { return []; }
}
function _setPinnedFolders(list) {
  try { localStorage.setItem(PINNED_FOLDERS_KEY, JSON.stringify(list)); } catch {}
}

function isFolderPinned(encodedDir) {
  return _getPinnedFolders().some((p) => (typeof p === 'string' ? p : p.path) === encodedDir);
}

function _decodeDirName(encodedDir) {
  try {
    // currentDir values are base64-encoded absolute paths
    return atob(encodedDir);
  } catch { return encodedDir; }
}

function togglePinFolder(encodedDir) {
  let pins = _getPinnedFolders();
  // Normalise legacy string format
  pins = pins.map((p) => (typeof p === 'string' ? { path: p, name: _decodeDirName(p) } : p));
  const idx = pins.findIndex((p) => p.path === encodedDir);
  if (idx >= 0) {
    pins.splice(idx, 1);
  } else {
    const decodedName = _decodeDirName(encodedDir);
    pins.unshift({ path: encodedDir, name: decodedName });
  }
  _setPinnedFolders(pins);
  renderPinnedStrip();
  _updatePinButton();
}

function _unpinFolder(encodedDir) {
  let pins = _getPinnedFolders();
  pins = pins.map((p) => (typeof p === 'string' ? { path: p, name: _decodeDirName(p) } : p));
  pins = pins.filter((p) => p.path !== encodedDir);
  _setPinnedFolders(pins);
  renderPinnedStrip();
  _updatePinButton();
}

function renderPinnedStrip() {
  const strip = document.getElementById('video-pinned-strip');
  if (!strip) return;
  let pins;
  try { pins = JSON.parse(localStorage.getItem(PINNED_FOLDERS_KEY) || '[]'); } catch { pins = []; }
  // Normalise legacy string format
  pins = pins.map((p) => (typeof p === 'string' ? { path: p, name: _decodeDirName(p) } : p));
  if (!pins.length) { strip.style.display = 'none'; return; }
  strip.style.display = '';
  strip.innerHTML =
    pins.map((p) => {
      const parts = _folderParts(p.name);
      const dynAttr = p.isDynamic ? ' data-music-pin-dynamic="1"' : '';
      const nameAttr = p.isDynamic ? ' data-music-pin-name="' + esc(p.name) + '"' : '';
      const icon = p.isDynamic ? '\uD83C\uDFB2' : '\uD83D\uDCC1';
      return (
        '<div class="vr-card pinned-card" role="button" tabindex="0" title="' + esc(p.name) + '"' +
        ' data-pin-path="' + esc(p.path) + '"' + dynAttr + nameAttr +
        ' oncontextmenu="_ctxShow(event,\'music-pinned\',{dir:\'' + esc(p.path) + '\',name:\'' + esc(p.name) + '\'})"' +
        ' onkeydown="activateKeyboardClick(event,this)">' +
        '<div class="vr-thumb vr-thumb-placeholder">' + icon + '</div>' +
        '<div class="vr-label pinned-label-wrap">' +
        (parts.parent ? '<span class="pinned-parent">' + esc(parts.parent) + '</span>' : '') +
        '<span class="pinned-name">' + esc(parts.name) + '</span>' +
        '</div>' +
        '<button class="pinned-card-remove" title="Unpin" data-unpin-path="' + esc(p.path) + '">\u00D7</button>' +
        '</div>'
      );
    }).join('');
  if (strip._pinHandler) strip.removeEventListener('click', strip._pinHandler);
  strip._pinHandler = (e) => {
    const removeBtn = e.target.closest('[data-unpin-path]');
    if (removeBtn) { e.stopPropagation(); _unpinFolder(removeBtn.dataset.unpinPath); return; }
    const card = e.target.closest('[data-pin-path]');
    if (card) browse(card.dataset.pinPath);
  };
  strip.addEventListener('click', strip._pinHandler);
}

function _folderParts(name) {
  try {
    const parts = name.replace(/\\/g, '/').split('/').filter(Boolean);
    return {
      name: parts[parts.length - 1] || name,
      parent: parts.length >= 2 ? parts[parts.length - 2] : null,
    };
  } catch { return { name, parent: null }; }
}

function _shortFolderName(name) {
  return _folderParts(name).name;
}

function _updatePinButton() {
  const btn = document.getElementById('vcb-pin-btn');
  if (!btn) return;
  const pinned = currentDir ? isFolderPinned(currentDir) : false;
  btn.classList.toggle('active', pinned);
  btn.title = pinned ? 'Unpin this folder' : 'Pin this folder';
  btn.innerHTML = pinned ? '📌 Pinned' : '📁 Pin folder';
  btn.disabled = !currentDir;
}

// ── Music pinned folders ─────────────────────────────────────────────────────
const MUSIC_PINNED_FOLDERS_KEY = 'musicPinnedFolders';

function _getMusicPinnedFolders() {
  try { return JSON.parse(localStorage.getItem(MUSIC_PINNED_FOLDERS_KEY) || '[]'); } catch { return []; }
}
function _setMusicPinnedFolders(list) {
  try { localStorage.setItem(MUSIC_PINNED_FOLDERS_KEY, JSON.stringify(list)); } catch {}
}

function isMusicFolderPinned(encodedDir) {
  return _getMusicPinnedFolders().some((p) => (typeof p === 'string' ? p : p.path) === encodedDir);
}

function toggleMusicPinFolder(encodedDir, overrideName, isDynamic) {
  if (!encodedDir) return;
  let pins = _getMusicPinnedFolders();
  pins = pins.map((p) => (typeof p === 'string' ? { path: p, name: _decodeDirName(p) } : p));
  const idx = pins.findIndex((p) => p.path === encodedDir);
  if (idx >= 0) {
    pins.splice(idx, 1);
  } else {
    const decodedName = overrideName || _decodeDirName(encodedDir);
    pins.unshift({ path: encodedDir, name: decodedName, isDynamic: !!isDynamic });
  }
  _setMusicPinnedFolders(pins);
  renderMusicPinnedStrip();
  _updateMusicPinButton();
}

function _unpinMusicFolder(encodedDir) {
  let pins = _getMusicPinnedFolders();
  pins = pins.map((p) => (typeof p === 'string' ? { path: p, name: _decodeDirName(p) } : p));
  pins = pins.filter((p) => p.path !== encodedDir);
  _setMusicPinnedFolders(pins);
  renderMusicPinnedStrip();
  _updateMusicPinButton();
}

function renderMusicPinnedStrip() {
  const strip = document.getElementById('music-pinned-strip');
  if (!strip) return;
  let pins = _getMusicPinnedFolders();
  pins = pins.map((p) => (typeof p === 'string' ? { path: p, name: _decodeDirName(p) } : p));
  if (!pins.length) { strip.style.display = 'none'; return; }
  strip.style.display = '';
  strip.innerHTML =
    pins.map((p) => {
      const parts = _folderParts(p.name);
      return (
        '<div class="vr-card pinned-card" role="button" tabindex="0" title="' + esc(p.name) + '"' +
        ' data-music-pin-path="' + esc(p.path) + '"' +
        ' oncontextmenu="_ctxShow(event,\'music-pinned\',{dir:\'' + esc(p.path) + '\',name:\'' + esc(p.name) + '\'})"' +
        ' onkeydown="activateKeyboardClick(event,this)">' +
        '<div class="vr-thumb vr-thumb-placeholder">📁</div>' +
        '<div class="vr-label pinned-label-wrap">' +
        (parts.parent ? '<span class="pinned-parent">' + esc(parts.parent) + '</span>' : '') +
        '<span class="pinned-name">' + esc(parts.name) + '</span>' +
        '</div>' +
        '<button class="pinned-card-remove" title="Unpin" data-music-unpin-path="' + esc(p.path) + '">×</button>' +
        '</div>'
      );
    }).join('');
  if (strip._pinHandler) strip.removeEventListener('click', strip._pinHandler);
  strip._pinHandler = (e) => {
    const removeBtn = e.target.closest('[data-music-unpin-path]');
    if (removeBtn) { e.stopPropagation(); _unpinMusicFolder(removeBtn.dataset.musicUnpinPath); return; }
    const card = e.target.closest('[data-music-pin-path]');
    if (card && typeof browseMusic === 'function') {
      const pinPath = card.dataset.musicPinPath;
      const pinDynamic = card.dataset.musicPinDynamic === '1';
      if (pinDynamic && typeof _openDynamicFolder === 'function') {
        _openDynamicFolder(pinPath, card.dataset.musicPinName || _decodeDirName(pinPath));
      } else {
        browseMusic(_decodeDirName(pinPath));
      }
    }
  };
  strip.addEventListener('click', strip._pinHandler);
}

function _updateMusicPinButton() {
  const btn = document.getElementById('mcb-pin-btn');
  if (!btn) return;
  const dir = typeof currentMusicFolder !== 'undefined' ? currentMusicFolder : null;
  const effectiveDir = btn.dataset.dynamicPath || dir;
  const pinned = effectiveDir ? isMusicFolderPinned(effectiveDir) : false;
  btn.classList.toggle('active', pinned);
  btn.title = pinned ? 'Unpin this folder' : 'Pin this folder';
  btn.innerHTML = pinned ? '📌 Pinned' : '📁 Pin folder';
  btn.disabled = !dir;
}

// ── Chapter markers on the seek bar ──────────────────────────────────────────

/**
 * Renders tick marks on #chapter-markers that overlay the #progress range input.
 * Each tick is positioned as a percentage of total duration.
 * Clicking a tick jumps to that chapter.
 */
function updateChapterMarkers(s) {
  const container = document.getElementById('chapter-markers');
  if (!container) return;

  const chapters = Array.isArray(s.chapters) ? s.chapters : [];
  const duration = Number(s.duration) || 0;

  // Skip chapter 0/first (that's the start — no marker needed there)
  const markable = duration > 0
    ? chapters.filter((c) => c.startSeconds > 0)
    : [];

  const currentChapter = s.currentChapter ?? -1;
  const signature = markable.map((c) => `${c.id}:${c.startSeconds}`).join(',') + '|' + currentChapter;
  if (container.dataset.signature === signature) return;
  container.dataset.signature = signature;

  container.innerHTML = markable
    .map((c) => {
      const pct = ((c.startSeconds / duration) * 100).toFixed(3);
      const isActive = c.id === currentChapter;
      return (
        `<span class="chapter-pin${isActive ? ' active' : ''}" ` +
        `style="left:${pct}%" ` +
        `data-chapter-id="${c.id}" ` +
        `data-start-seconds="${c.startSeconds}" ` +
        `data-label="${esc(c.name || 'Chapter ' + c.id)}" ` +
        `aria-label="Jump to ${esc(c.name || 'Chapter ' + c.id)}" ` +
        `role="button" tabindex="0"></span>`
      );
    })
    .join('');
}

// Wire up click delegation on chapter-markers (runs once on first call)
(function _initChapterMarkerClicks() {
  function _jumpToPin(pin) {
    const id = parseInt(pin.dataset.chapterId, 10);
    if (Number.isNaN(id)) return;
    const startSec = parseFloat(pin.dataset.startSeconds);
    const progress = document.getElementById('progress');
    if (progress && !Number.isNaN(startSec)) {
      progress.value = startSec;
      updateSeekFill(progress);
    }
    // Immediately update active class without waiting for the next poll
    const container = document.getElementById('chapter-markers');
    if (container) {
      container.querySelectorAll('.chapter-pin').forEach((p) =>
        p.classList.toggle('active', p === pin)
      );
      // Invalidate signature so the next poll re-renders with server-confirmed state
      delete container.dataset.signature;
    }
    haptic(10);
    api('/api/chapter?id=' + id);
  }
  document.addEventListener('click', (e) => {
    const pin = e.target.closest('.chapter-pin[data-chapter-id]');
    if (pin) _jumpToPin(pin);
  });
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Enter' && e.key !== ' ') return;
    const pin = e.target.closest('.chapter-pin[data-chapter-id]');
    if (!pin) return;
    e.preventDefault();
    _jumpToPin(pin);
  });
})();

// ── Up Next card ─────────────────────────────────────────────────────────────

let _upNextPath = null;           // encoded path of next file
let _upNextTitle = null;          // display title
let _upNextCountdownSec = 0;      // seconds remaining before auto-play
let _upNextTimer = null;          // setInterval handle
let _upNextDismissedPath = null;  // encoded path last dismissed (suppress re-show)
let _upNextFetchedForPath = null; // last filePath we fetched next-in-folder for

/** Called from updateTrackControls when s.isPlaying is true. */
async function updateUpNext(s) {
  const duration = Number(s.duration) || 0;
  const position = Number(s.position) || 0;
  const filePath = s.filePath || '';

  // Only show for videos longer than 60 s, and in the last 30 s
  const shouldShow = duration > 60 && position >= duration - 30 && filePath;

  if (!shouldShow) {
    _clearUpNext();
    return;
  }

  // Already dismissed for this file
  if (_upNextDismissedPath && _upNextDismissedPath === filePath) return;

  // Already showing
  const card = document.getElementById('up-next-card');
  if (card && card.classList.contains('visible')) return;

  // Only fetch if the file changed
  if (_upNextFetchedForPath === filePath) return;
  _upNextFetchedForPath = filePath;

  try {
    const res = await fetch('/api/next-in-folder?path=' + encodeURIComponent(filePath));
    if (!res.ok) return;
    const data = await res.json();
    if (!data.found || !data.path) return;

    _upNextPath = data.path;
    _upNextTitle = data.title || 'Next video';
    _showUpNext();
  } catch { /* network error — silently ignore */ }
}

function _showUpNext() {
  const card = document.getElementById('up-next-card');
  if (!card) return;
  const titleEl = document.getElementById('up-next-title');
  const countdownEl = document.getElementById('up-next-countdown');
  if (titleEl) titleEl.textContent = _upNextTitle || '';
  _upNextCountdownSec = 10;
  if (countdownEl) countdownEl.textContent = `Auto-playing in ${_upNextCountdownSec}s`;
  card.classList.add('visible');

  _upNextTimer = setInterval(() => {
    _upNextCountdownSec -= 1;
    if (countdownEl) countdownEl.textContent = `Auto-playing in ${_upNextCountdownSec}s`;
    if (_upNextCountdownSec <= 0) {
      clearInterval(_upNextTimer);
      _upNextTimer = null;
      _triggerUpNextPlay();
    }
  }, 1000);
}

/** User pressed "Play Now" */
async function upNextPlayNow() {
  clearInterval(_upNextTimer);
  _upNextTimer = null;
  haptic(12);
  await _triggerUpNextPlay();
}

/** User pressed "Dismiss" */
function upNextDismiss() {
  _upNextDismissedPath = _upNextFetchedForPath;
  _clearUpNext();
  setStatus('Up Next dismissed.');
}

async function _triggerUpNextPlay() {
  if (!_upNextPath) return;
  const path = _upNextPath;
  _clearUpNext();

  if (typeof closeLocalPlayerForNonLocalPlayback === 'function') {
    await closeLocalPlayerForNonLocalPlayback();
  }

  await api('/api/video/play?path=' + encodeURIComponent(path));
  if (typeof pollStatus === 'function') await pollStatus();
}

function _clearUpNext() {
  clearInterval(_upNextTimer);
  _upNextTimer = null;
  _upNextPath = null;
  _upNextTitle = null;
  _upNextCountdownSec = 0;
  _upNextFetchedForPath = null;
  const card = document.getElementById('up-next-card');
  if (card) card.classList.remove('visible');
  const countdownEl = document.getElementById('up-next-countdown');
  if (countdownEl) countdownEl.textContent = '';
}

