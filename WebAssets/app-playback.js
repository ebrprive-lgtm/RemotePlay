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

async function onSeekDrag() {
  seekDragging = true;
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
function onZoomPointerDown() {
  zoomDragging = true;
}
function onZoomPointerUp() {
  zoomDragging = false;
}
function setZoomPreview(value) {
  const parsed = parseFloat(value);
  const zoom = Math.max(1, Math.min(2, Number.isFinite(parsed) ? parsed : 1));
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
function resetZoomDefault() {
  haptic(8);
  commitZoom(1);
}
const DEFAULT_PROFILE = { volume: 1, boost: 0, brightness: 0.5, saturation: 1, zoom: 1, speed: 1 };
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
  const audioGroup = document.getElementById('audio-track-group');
  const subtitleGroup = document.getElementById('subtitle-track-group');
  const trackControls = document.getElementById('track-controls');
  const audioTracks = Array.isArray(s.audioTracks) ? s.audioTracks : [];
  const subtitleTracks = Array.isArray(s.subtitleTracks) ? s.subtitleTracks : [];
  const showAudio = audioTracks.length > 1;
  const showSubtitles = subtitleTracks.some((t) => Number(t.id) >= 0);
  renderTrackSelect(audioSelect, audioTracks, s.currentAudioTrackId);
  renderTrackSelect(subtitleSelect, subtitleTracks, s.currentSubtitleTrackId);
  audioGroup.style.display = showAudio ? 'flex' : 'none';
  subtitleGroup.style.display = showSubtitles ? 'flex' : 'none';
  trackControls.style.display = showAudio || showSubtitles ? 'flex' : 'none';
  document.getElementById('options-card').style.display =
    showAudio || showSubtitles ? 'flex' : 'none';
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

    const queueButton = findQueueButton(card);
    if (queueButton) queueButton.textContent = isQueued ? 'Unqueue' : 'Queue';
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
async function browse(d, offset = 0, append = false, pushHistory = true, isLinked = false) {
  // Push the current location onto the back-stack before navigating (not for pagination)
  if (!append && pushHistory && currentDir !== d && currentDir !== null)
    browseHistory.push(currentDir);
  if (!append && d === null) browseHistory = [];
  if (!append) currentIsLinkedDir = isLinked;
  setStatus('Loading...');
  currentDir = d;
  document.getElementById('search').value = '';
  if (!append) {
    clearPendingThumbnails();
    setBrowseLoading(true);
  }
  try {
    const url =
      (d ? '/api/browse?dir=' + encodeURIComponent(d) : '/api/browse') +
      (offset ? '&offset=' + encodeURIComponent(offset) : '');
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
    render(currentData);
    setBrowseLoading(false); // dismiss overlay as soon as content is rendered
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
function setSearchBusy(on) {
  const inp = document.getElementById('search');
  const sp = document.getElementById('search-spinner');
  inp.classList.toggle('searching', on);
  sp.classList.toggle('visible', on);
}
function onSearch() {
  const q = document.getElementById('search').value.toLowerCase().trim();
  clearTimeout(searchTimer);
  if (currentMode === 'music') {
    if (!q) {
      if (currentMusicData) renderMusic(currentMusicData);
      else browseMusic(null);
      return;
    }
    searchTimer = setTimeout(() => searchMusicLibrary(q), 300);
    return;
  }
  if (currentMode === 'radio') return;
  if (!q) {
    setSearchBusy(false);
    if (currentData) render(currentData);
    return;
  }
  clearPendingThumbnails();
  setSearchBusy(true);
  searchTimer = setTimeout(() => searchLibrary(q), 300);
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
