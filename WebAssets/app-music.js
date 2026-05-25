let currentMode = 'video';
let musicBrowseHistory = [];
let currentMusicFolder = null;
let currentMusicData = null;
let musicStatusPollTimer = null;
let musicPlaybackPollTimer = null;
let musicCurrentPath = null;
let musicIsPlaying = false;
let musicCurrentVolume = 0.8;
// Track list for prev/next
let musicTrackList = [];
let musicTrackIndex = -1;
// Playback options
let _musicAutoPlay = true;
let _musicShuffle = false;
let _musicRepeat = 'none'; // 'none' | 'all' | 'one'
let _musicShuffleOrder = []; // shuffled indices into musicTrackList
let _musicShufflePos = -1;
// Multi-select
let _musicSelected = new Set(); // selected track paths
let _musicLastSelectedIdx = -1; // for shift-click range

// â”€â”€ Music playback â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
let _musicTrackListFolder = null; // folder the current musicTrackList was built from
async function playMusic(path, name) {
  musicCurrentPath = path;
  // Update the .playing highlight immediately on click
  document.querySelectorAll('.music-track-card').forEach((card) => {
    card.classList.toggle('playing', card.dataset.path === path);
  });
  // Build/update track list from current browse data for prev/next.
  // Only rebuild shuffle order when the folder (track list) changes.
  if (currentMusicData && currentMusicData.files && currentMusicData.files.length) {
    const newFolder = currentMusicData.folder || currentMusicFolder || null;
    const folderChanged = newFolder !== _musicTrackListFolder;
    _musicTrackListFolder = newFolder;
    musicTrackList = currentMusicData.files.map((f) => ({
      path: f.path,
      name: f.name || pathToName(f.path),
      artist: f.artist || '',
      album: f.album || '',
      genre: f.genre || '',
      year: f.year || null,
      ext: f.ext || '',
      durationSec: f.durationSec || 0,
      hasCover: !!f.hasCover,
    }));
    musicTrackIndex = musicTrackList.findIndex((t) => t.path === path);
    if (_musicShuffle && folderChanged) _buildShuffleOrder();
    else if (_musicShuffle && _musicShuffleOrder.length) {
      // update shuffle position to match the new track
      const pos = _musicShuffleOrder.indexOf(musicTrackIndex);
      if (pos >= 0) _musicShufflePos = pos;
    }
  }
  const displayName =
    name ||
    (musicTrackIndex >= 0 ? musicTrackList[musicTrackIndex].name : null) ||
    pathToName(path);

  if (isPlayLocal()) {
    // Local mode: stop server, play in browser via local audio
    await fetch('/api/music/stop');
    musicIsPlaying = false;
    stopMusicPlaybackPoll();
    // Show music bar with local data (localPlay will also show the local docked bar)
    const trackMeta = musicTrackList.find(t => t.path === path) || {};
    updateMusicBar({
      isPlaying: true,
      isPaused: false,
      title: displayName,
      position: 0,
      duration: 0,
      artist: trackMeta.artist || '',
      album: trackMeta.album || '',
      genre: trackMeta.genre || '',
      year: trackMeta.year || null,
      ext: trackMeta.ext || '',
      durationSec: trackMeta.durationSec || 0,
      hasCover: !!trackMeta.hasCover,
      coverPath: path,
    });
    localPlay('/api/music/stream?path=' + encodeURIComponent(path), displayName, 'Music');
    return;
  }

  await fetch('/api/music/play?path=' + encodeURIComponent(path));
  musicIsPlaying = true;
  // Immediately tell the server which track comes next so playback continues
  // even when this browser tab is closed before the current song ends.
  _queueNextTrackOnServer();
  const trackMetaSrv = musicTrackList.find(t => t.path === path) || {};
  updateMusicBar({
    isPlaying: true,
    isPaused: false,
    title: displayName,
    position: 0,
    duration: 0,
    artist: trackMetaSrv.artist || '',
    album: trackMetaSrv.album || '',
    genre: trackMetaSrv.genre || '',
    year: trackMetaSrv.year || null,
    ext: trackMetaSrv.ext || '',
    durationSec: trackMetaSrv.durationSec || 0,
    hasCover: !!trackMetaSrv.hasCover,
    coverPath: path,
  });
  startMusicPlaybackPoll();
}

async function musicToggle() {
  if (isPlayLocal() && typeof _localMediaType !== 'undefined' && _localMediaType === 'Music') {
    localToggle();
    return;
  }
  await fetch('/api/music/pause');
  // state updated by next poll tick
}

async function musicStop() {
  await fetch('/api/music/stop');
  musicIsPlaying = false;
  musicCurrentPath = null;
  const bar = document.getElementById('music-player-bar');
  if (bar) bar.style.display = 'none';
  document.body.classList.remove('music-player-docked');
  stopMusicPlaybackPoll();
  localStop(); // also silence local browser audio
}

async function musicPrev() {
  if (musicTrackList.length === 0) return;
  let t;
  if (_musicShuffle && _musicShuffleOrder.length) {
    _musicShufflePos = Math.max(0, _musicShufflePos - 1);
    t = musicTrackList[_musicShuffleOrder[_musicShufflePos]];
  } else {
    const idx = Math.max(0, musicTrackIndex - 1);
    t = musicTrackList[idx];
  }
  if (t) await playMusic(t.path, t.name);
}

async function musicNext() {
  if (musicTrackList.length === 0) return;
  const t = _musicNextTrack();
  if (t) await playMusic(t.path, t.name);
}

function musicVolume(v) {
  const vol = parseFloat(v);
  if (isNaN(vol)) return;
  musicCurrentVolume = vol;
  const lbl = document.getElementById('music-volume-label');
  if (lbl) lbl.textContent = Math.round(vol * 100) + '%';
  if (isPlayLocal()) {
    const a = typeof _getLocalAudio !== 'undefined' ? _getLocalAudio() : null;
    if (a) a.volume = Math.min(1, Math.max(0, vol));
    return;
  }
  fetch('/api/music/volume?v=' + vol.toFixed(3));
}
function musicVolumeReset() {
  const slider = document.getElementById('music-bar-volume');
  if (slider) { slider.value = 1; musicVolume(1); }
}

// Combined volume/boost slider (0â€“1.0 = volume, 1.0â€“1.3 = boost zone)
function musicCombinedSlider(v) {
  const val = parseFloat(v);
  if (isNaN(val)) return;
  const slider = document.getElementById('music-bar-combined');
  const lbl    = document.getElementById('music-combined-label');
  if (val <= 1.0) {
    // Volume zone
    if (slider) slider.classList.remove('slider-boosting');
    if (lbl) lbl.textContent = Math.round(val * 100) + '%';
    musicVolume(val);
    musicBoost(1.0);
  } else {
    // Boost zone: map 1.0â€“1.3 â†’ gain 1â€“3
    const gain = 1 + (val - 1) * (2 / 0.3);
    if (slider) slider.classList.add('slider-boosting');
    const db = Math.round(20 * Math.log10(gain));
    if (lbl) lbl.textContent = '100% +' + db + 'dB';
    musicVolume(1.0);
    musicBoost(gain);
  }
}
function musicCombinedReset() {
  const slider = document.getElementById('music-bar-combined');
  if (slider) { slider.value = 0.8; musicCombinedSlider(0.8); }
}

function musicBoost(v) {
  const boost = parseFloat(v);
  if (isNaN(boost)) return;
  const lbl = document.getElementById('music-boost-label');
  if (lbl) {
    const db = boost > 0 ? Math.round(20 * Math.log10(boost)) : -Infinity;
    lbl.textContent = isFinite(db) ? (db >= 0 ? '+' : '') + db + ' dB' : 'â€”';
  }
  if (isPlayLocal()) {
    const gainNode = typeof _ensureLocalBoostGraph !== 'undefined' ? _ensureLocalBoostGraph() : null;
    if (gainNode) gainNode.gain.value = Math.max(0, boost);
    return;
  }
  fetch('/api/music/boost?v=' + boost.toFixed(3));
}
function musicBoostReset() {
  const slider = document.getElementById('music-bar-boost');
  if (slider) { slider.value = 1; musicBoost(1); }
}

function pathToName(p) {
  if (!p) return '';
  const parts = p.replace(/\\/g, '/').split('/');
  const file = parts[parts.length - 1] || '';
  return file.replace(/\.[^.]+$/, '');
}

function updateMusicBar(s) {
  const bar = document.getElementById('music-player-bar');
  if (!bar) return;
  const active = s.isPlaying || s.isPaused;
  bar.style.display = active ? 'flex' : 'none';
  const isDocked = active && window.matchMedia('(min-width:900px)').matches;
  document.body.classList.toggle('music-player-docked', isDocked);

  // Title
  const title = document.getElementById('music-bar-title');
  if (title) title.textContent = s.title || 'â€”';

  // Artist + album
  const artistEl = document.getElementById('music-bar-artist');
  if (artistEl) artistEl.textContent = s.artist || '';
  const albumEl = document.getElementById('music-bar-album');
  if (albumEl) albumEl.textContent = s.album || '';

  // Genre Â· year muted line
  const gyEl = document.getElementById('music-bar-genreyear');
  if (gyEl) {
    const parts = [s.genre, s.year].filter(Boolean);
    gyEl.textContent = parts.join(' Â· ');
    gyEl.style.display = parts.length ? '' : 'none';
  }

  // Format + duration badges
  const badgeEl = document.getElementById('music-bar-badge');
  if (badgeEl) {
    const fmt = s.ext ? s.ext.toUpperCase() : '';
    const dur = s.durationSec != null && s.durationSec > 0 ? fmtSec(s.durationSec) : '';
    badgeEl.innerHTML = (fmt ? `<span class="music-bar-fmt-badge">${fmt}</span>` : '')
      + (dur ? `<span class="music-bar-dur">${dur}</span>` : '');
  }

  // Track position (#N of M)
  const trackPosEl = document.getElementById('music-bar-trackpos');
  if (trackPosEl) {
    if (musicTrackIndex >= 0 && musicTrackList.length > 0) {
      trackPosEl.textContent = `#${musicTrackIndex + 1} of ${musicTrackList.length}`;
      trackPosEl.style.display = '';
    } else {
      trackPosEl.style.display = 'none';
    }
  }

  // Cover art + animated bars overlay
  const artEl = document.getElementById('music-bar-art');
  const artBars = document.getElementById('music-bar-art-bars');
  if (artEl) {
    const cp = s.coverPath || s.currentPath || musicCurrentPath;
    if (s.hasCover && cp) {
      // Only refresh img if cover path changed to avoid flicker
      const existing = artEl.querySelector('.music-bar-art-img');
      const newSrc = '/api/music/cover?path=' + encodeURIComponent(cp);
      if (!existing || existing.getAttribute('data-cp') !== cp) {
        const img = document.createElement('img');
        img.src = newSrc;
        img.setAttribute('data-cp', cp);
        img.className = 'music-bar-art-img';
        img.onerror = () => { artEl.classList.add('music-bar-art-empty'); img.remove(); };
        // Remove previous img / text
        artEl.querySelectorAll('.music-bar-art-img').forEach(el => el.remove());
        artEl.classList.remove('music-bar-art-empty');
        artEl.appendChild(img);
      }
    } else {
      artEl.querySelectorAll('.music-bar-art-img').forEach(el => el.remove());
      artEl.classList.add('music-bar-art-empty');
    }
    // Keep bars overlay as last child
    if (artBars) {
      artEl.appendChild(artBars);
      artBars.style.display = s.isPlaying ? 'flex' : 'none';
    }
  }

  // Prev / Next nav titles (shuffle-aware) + queue peek
  _refreshMusicNavLabels();

  // Seek bar + filled-track CSS var
  const seek = document.getElementById('music-seek');
  if (seek && !_musicSeekDragging) {
    seek.max = s.duration > 0 ? s.duration : 0;
    seek.value = s.position || 0;
  }
  _updateMusicSeekFill();

  // Time label
  const timeEl = document.getElementById('music-time-label');
  if (timeEl)
    timeEl.textContent =
      s.duration > 0 ? fmtSec(s.position) + ' / ' + fmtSec(s.duration) : '0:00 / 0:00';

  // Play button label
  const btn = document.getElementById('music-btn-play');
  if (btn) btn.innerHTML = s.isPaused ? '&#9654; Play' : '&#9646;&#9646; Pause';

  if (s.lastError && !active) bar.style.display = 'none';
}

async function musicPlayHere() {
  if (!musicCurrentPath) return;
  const path = musicCurrentPath;
  const title = document.getElementById('music-bar-title')?.textContent || 'Track';
  // Stop server playback so we don't hear two versions
  await fetch('/api/music/stop');
  musicIsPlaying = false;
  stopMusicPlaybackPoll();
  const bar = document.getElementById('music-player-bar');
  if (bar) bar.style.display = 'none';
  document.body.classList.remove('music-player-docked');
  localPlay('/api/music/stream?path=' + encodeURIComponent(path), title, 'Music');
}

let _musicSeekDragging = false;
function onMusicSeekDrag() {
  _musicSeekDragging = true;
  _updateMusicSeekFill();
}
async function onMusicSeekCommit() {
  if (!_musicSeekDragging) return; // ignore programmatic value changes
  _musicSeekDragging = false;
  const seek = document.getElementById('music-seek');
  if (!seek) return;
  const pos = parseFloat(seek.value);
  if (Number.isNaN(pos)) return;
  // Local mode: seek the browser audio directly
  if (isPlayLocal() && typeof _localMediaType !== 'undefined' && _localMediaType === 'Music') {
    const a = typeof _getLocalAudio !== 'undefined' ? _getLocalAudio() : null;
    if (a) a.currentTime = pos;
    return;
  }
  try {
    await fetch('/api/music/seek?pos=' + pos.toFixed(2), { method: 'POST' });
  } catch (e) {}
}

function toggleMusicAutoPlay() {
  _musicAutoPlay = !_musicAutoPlay;
  const btn = document.getElementById('music-btn-autoplay');
  if (btn) btn.classList.toggle('active', _musicAutoPlay);
}
function _buildShuffleOrder() {
  const n = musicTrackList.length;
  const arr = Array.from({ length: n }, (_, i) => i);
  for (let i = n - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [arr[i], arr[j]] = [arr[j], arr[i]];
  }
  // Put current track first so shuffle starts from the current song
  const cur = arr.indexOf(musicTrackIndex);
  if (cur > 0) {
    const tmp = arr[0];
    arr[0] = arr[cur];
    arr[cur] = tmp;
  }
  _musicShuffleOrder = arr;
  _musicShufflePos = 0;
}
function toggleMusicShuffle() {
  if (window._musicQueue && window._musicQueue.length) return; // disabled in queue mode
  _musicShuffle = !_musicShuffle;
  const btn = document.getElementById('music-btn-shuffle');
  if (btn) btn.classList.toggle('active', _musicShuffle);
  if (_musicShuffle) {
    // Load ALL tracks in the current folder before building shuffle order so
    // the shuffle pool is the full folder, not just the first page.
    _loadAllMusicTracksForShuffle().then(() => {
      _buildShuffleOrder();
      // Refresh nav-label display without touching server state
      _refreshMusicNavLabels();
    });
  } else {
    _refreshMusicNavLabels();
  }
}
// Silently fetches remaining pages until hasMore is false; merges into musicTrackList.
async function _loadAllMusicTracksForShuffle() {
  if (!currentMusicData) return;
  // If we already have all tracks (or it's a search result) nothing to do
  while (currentMusicData.hasMore) {
    const offset = currentMusicData.files ? currentMusicData.files.length : 0;
    try {
      const folder = currentMusicData.folder || currentMusicFolder || null;
      const url =
        '/api/music/browse' +
        (folder ? '?folder=' + encodeURIComponent(folder) : '?') +
        '&offset=' +
        encodeURIComponent(offset);
      const res = await fetch(url);
      if (!res.ok) break;
      const page = await res.json();
      currentMusicData.files = [...(currentMusicData.files || []), ...(page.files || [])];
      currentMusicData.offset = page.offset;
      currentMusicData.hasMore = page.hasMore;
    } catch (e) {
      break;
    }
  }
  // Rebuild the in-memory track list from the now-complete file list
  musicTrackList = currentMusicData.files.map((f) => ({
    path: f.path,
    name: f.name || pathToName(f.path),
    artist: f.artist || '',
    album: f.album || '',
    genre: f.genre || '',
    year: f.year || null,
    ext: f.ext || '',
    durationSec: f.durationSec || 0,
    hasCover: !!f.hasCover,
  }));
  musicTrackIndex = musicTrackList.findIndex((t) => t.path === (musicCurrentPath || ''));
  // Re-render cards so load-more button disappears
  renderMusicCards(currentMusicData);
}
// Refresh the Prev/Next label display without mutating positions
function _refreshMusicNavLabels() {
  const prevTitleEl = document.getElementById('music-prev-title');
  const prevTrack = _musicPeekPrev();
  if (prevTitleEl) prevTitleEl.textContent = prevTrack ? prevTrack.name : '';
  const nextTitleEl = document.getElementById('music-next-title');
  const nextTrack = _musicPeekNext();
  if (nextTitleEl) nextTitleEl.textContent = nextTrack ? nextTrack.name : '';

  // Queue mode: when explicit queue has items, replace Prev/Next with single "Play Next in Queue"
  // and disable Shuffle / Repeat since they don't apply to an explicit queue.
  const queueLen = window._musicQueue ? window._musicQueue.length : 0;
  const inQueueMode = queueLen > 0;

  const navNormal   = document.getElementById('music-nav-normal');
  const queueNextBtn = document.getElementById('music-btn-queue-next');
  const shuffleBtn  = document.getElementById('music-btn-shuffle');
  const repeatBtn   = document.getElementById('music-btn-repeat');

  if (navNormal)    navNormal.style.display   = inQueueMode ? 'none' : '';
  if (queueNextBtn) {
    queueNextBtn.style.display = inQueueMode ? '' : 'none';
    // Show first item in queue as subtitle
    const queueNextTitleEl = document.getElementById('music-queue-next-title');
    if (queueNextTitleEl) queueNextTitleEl.textContent = queueLen > 0 ? window._musicQueue[0].name : '';
  }
  if (shuffleBtn) {
    shuffleBtn.disabled = inQueueMode;
    shuffleBtn.style.opacity = inQueueMode ? '0.35' : '';
    shuffleBtn.title = inQueueMode ? 'Shuffle disabled while queue is active' : 'Shuffle songs in current folder';
  }
  if (repeatBtn) {
    repeatBtn.disabled = inQueueMode;
    repeatBtn.style.opacity = inQueueMode ? '0.35' : '';
    repeatBtn.title = inQueueMode ? 'Repeat disabled while queue is active' : 'Repeat: Off / Repeat All / Repeat One';
  }

  _renderMusicQueuePeek();
}
function _musicNextTrack() {
  if (!musicTrackList.length && !(window._musicQueue && window._musicQueue.length)) return null;
  if (_musicRepeat === 'one' && musicTrackIndex >= 0) return musicTrackList[musicTrackIndex] || null;
  // Consume from the explicit queue first
  if (window._musicQueue && window._musicQueue.length) {
    const next = window._musicQueue.shift();
    // Refresh queue count badge and nav labels
    if (currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
    _refreshMusicNavLabels();
    return next || null;
  }
  if (_musicShuffle) {
    if (!_musicShuffleOrder.length) _buildShuffleOrder();
    _musicShufflePos = (_musicShufflePos + 1) % _musicShuffleOrder.length;
    const idx = _musicShuffleOrder[_musicShufflePos];
    return musicTrackList[idx] || null;
  }
  const next = musicTrackIndex + 1;
  if (next < musicTrackList.length) return musicTrackList[next];
  return _musicRepeat === 'all' ? musicTrackList[0] : null;
}
// Peek at the previous/next track WITHOUT advancing position (for label display)
function _musicPeekPrev() {
  if (!musicTrackList.length) return null;
  if (_musicShuffle && _musicShuffleOrder.length) {
    const pos = _musicShufflePos - 1;
    if (pos < 0) return null;
    return musicTrackList[_musicShuffleOrder[pos]] || null;
  }
  const idx = musicTrackIndex - 1;
  return idx >= 0 ? musicTrackList[idx] : null;
}
function _musicPeekNext() {
  if (!musicTrackList.length) return null;
  if (_musicShuffle && _musicShuffleOrder.length) {
    const pos = (_musicShufflePos + 1) % _musicShuffleOrder.length;
    // Don't wrap-peek if we're at the last unique position
    if (_musicShufflePos === _musicShuffleOrder.length - 1) return null;
    return musicTrackList[_musicShuffleOrder[pos]] || null;
  }
  const idx = musicTrackIndex + 1;
  return idx < musicTrackList.length ? musicTrackList[idx] : null;
}
function fmtSec(sec) {
  const s = Math.floor(sec);
  return Math.floor(s / 60) + ':' + (s % 60).toString().padStart(2, '0');
}

function _updateMusicSeekFill() {
  const seek = document.getElementById('music-seek');
  if (!seek) return;
  const max = parseFloat(seek.max) || 0;
  const val = parseFloat(seek.value) || 0;
  const pct = max > 0 ? ((val / max) * 100).toFixed(2) + '%' : '0%';
  seek.style.setProperty('--music-seek-pct', pct);
}

function _musicQueueRemove(idx) {
  if (!window._musicQueue || idx < 0 || idx >= window._musicQueue.length) return;
  window._musicQueue.splice(idx, 1);
  _refreshMusicNavLabels();
  if (currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
  _renderMusicQueuePeek();
}
function _musicQueueClear() {
  window._musicQueue = [];
  _refreshMusicNavLabels();
  if (currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
  _renderMusicQueuePeek();
}
function _renderMusicQueuePeek() {
  _updateMusicQueuePendingBar();
  const card = document.getElementById('music-queue-peek-card');
  const list = document.getElementById('music-queue-peek-list');
  if (!card || !list) return;

  const queueItems = window._musicQueue || [];
  const peekCount = 3;
  const upNextItems = [];
  for (let i = 1; i <= peekCount; i++) {
    let t = null, listIdx = -1;
    if (_musicShuffle && _musicShuffleOrder.length) {
      const pos = (_musicShufflePos + i);
      if (pos < _musicShuffleOrder.length) { listIdx = _musicShuffleOrder[pos]; t = musicTrackList[listIdx] || null; }
    } else {
      listIdx = musicTrackIndex + i;
      if (listIdx < musicTrackList.length) t = musicTrackList[listIdx] || null;
      else if (_musicRepeat === 'all' && musicTrackList.length) { listIdx = listIdx % musicTrackList.length; t = musicTrackList[listIdx] || null; }
    }
    if (t) upNextItems.push({ t, listIdx });
  }

  if (!queueItems.length && !upNextItems.length) { card.style.display = 'none'; return; }
  card.style.display = '';

  // Store for delegated click handler
  list._queuePeekItems = upNextItems;
  list._queueItems = queueItems.slice();

  let html = '';

  // --- Queued section ---
  if (queueItems.length) {
    html += `<div class="music-queue-section-header">`
      + `<span>Queue (${queueItems.length})</span>`
      + `<button class="music-queue-clear-btn" data-action="clear-all" title="Clear queue">âœ• Clear all</button>`
      + `</div>`;
    html += queueItems.map((item, i) => {
      const safeTitle = (item.name || item.path || '').replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;');
      const displayName = (item.name || item.path || '').replace(/&/g, '&amp;').replace(/</g, '&lt;');
      return `<div class="music-queue-peek-item music-queue-peek-queued" data-qi="${i}" title="${safeTitle}">`
        + `<span class="music-queue-peek-num">${i + 1}</span>`
        + `<span class="music-queue-peek-name">${displayName}</span>`
        + `<button class="music-queue-remove-btn" data-action="remove" data-qi="${i}" title="Remove from queue">âœ•</button>`
        + `</div>`;
    }).join('');
  }

  // --- Up Next section --- only shown when autoplay is on AND no explicit queue items
  if (upNextItems.length && _musicAutoPlay && !queueItems.length) {
    html += `<div class="music-queue-section-header"><span>Up Next</span></div>`;
    html += upNextItems.map(({ t, listIdx }, i) => {
      const num = listIdx >= 0 ? listIdx + 1 : '?';
      const dur = t.durationSec ? fmtSec(t.durationSec) : '';
      const safeTitle = t.name.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;');
      return `<div class="music-queue-peek-item" data-qi-next="${i}" title="${safeTitle}">`
        + `<span class="music-queue-peek-num">${num}</span>`
        + `<span class="music-queue-peek-name">${t.name.replace(/&/g, '&amp;').replace(/</g, '&lt;')}</span>`
        + (dur ? `<span class="music-queue-peek-dur">${dur}</span>` : '')
        + `</div>`;
    }).join('');
  }

  list.innerHTML = html;

  // Delegated event handling â€” no inline onclick
  list.onclick = (e) => {
    const btn = e.target.closest('[data-action]');
    if (btn) {
      e.stopPropagation();
      const action = btn.dataset.action;
      if (action === 'clear-all') { _musicQueueClear(); return; }
      if (action === 'remove') { _musicQueueRemove(parseInt(btn.dataset.qi, 10)); return; }
    }
    // Click on queued row â€” discard everything before it, play it
    const qRow = e.target.closest('[data-qi]');
    if (qRow && !qRow.dataset.qiNext) {
      const idx = parseInt(qRow.dataset.qi, 10);
      const item = (list._queueItems || [])[idx];
      if (item) {
        // Remove this item and all that preceded it (they are skipped)
        window._musicQueue.splice(0, idx + 1);
        if (currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
        playMusic(item.path, item.name);
      }
      return;
    }
    // Click on up-next row
    const nextRow = e.target.closest('[data-qi-next]');
    if (nextRow) {
      const entry = (list._queuePeekItems || [])[parseInt(nextRow.dataset.qiNext, 10)];
      if (entry) playMusic(entry.t.path, entry.t.name);
    }
  };
}

function toggleMusicRepeat() {
  if (window._musicQueue && window._musicQueue.length) return; // disabled in queue mode
  const modes = ['none', 'all', 'one'];
  _musicRepeat = modes[(modes.indexOf(_musicRepeat) + 1) % modes.length];
  const btn = document.getElementById('music-btn-repeat');
  if (btn) {
    btn.classList.toggle('active', _musicRepeat !== 'none');
    if (_musicRepeat === 'none') btn.innerHTML = '&#128257; Repeat';
    else if (_musicRepeat === 'all') btn.innerHTML = '&#128257; Repeat All';
    else btn.innerHTML = '&#128257; Repeat One';
  }
  _refreshMusicNavLabels();
}

// Tell the server which track to auto-advance to when the current one ends.
// Called every time a track starts playing so the server always has the right
// lookahead even if the browser is closed before the song finishes.
function _queueNextTrackOnServer() {
  const nextT = _musicPeekNext();
  const url = nextT
    ? '/api/music/queue-next?path=' + encodeURIComponent(nextT.path)
    : '/api/music/queue-next'; // empty path clears the queued track
  fetch(url).catch(() => {});
}

let _musicKnownSessionId = null; // last server session ID seen; null = unknown

function startMusicPlaybackPoll() {
  if (musicPlaybackPollTimer) return;
  musicPlaybackPollTimer = setInterval(async () => {
    let s;
    try {
      const res = await fetch('/api/music/status');
      if (!res.ok) return;
      s = await res.json();
    } catch (e) {
      return;
    }

    // Detect server restart via the per-process session ID.
    // If the ID changed (or we didn't have one yet) the server is a fresh process
    // with no active playback â€“ sync local state and do NOT auto-advance.
    if (s.sessionId && s.sessionId !== _musicKnownSessionId) {
      _musicKnownSessionId = s.sessionId;
      musicIsPlaying = s.isPlaying || s.isPaused;
      musicCurrentPath = s.currentPath || null;
      updateMusicBar(s);
      if (!musicIsPlaying) stopMusicPlaybackPoll();
      return;
    }

    const wasPlaying = musicIsPlaying;
    musicIsPlaying = s.isPlaying || s.isPaused;

    // Detect a server-initiated track advance (the server auto-played the next
    // track while the browser was open). Sync browser state and re-register the
    // new lookahead so the chain keeps going.
    if (s.currentPath && s.currentPath !== musicCurrentPath && s.isPlaying) {
      musicCurrentPath = s.currentPath;
      const advancedIdx = musicTrackList.findIndex(t => t.path === s.currentPath);
      if (advancedIdx >= 0) {
        musicTrackIndex = advancedIdx;
        if (_musicShuffle && _musicShuffleOrder.length) {
          const pos = _musicShuffleOrder.indexOf(advancedIdx);
          if (pos >= 0) _musicShufflePos = pos;
        }
      }
      _refreshMusicNavLabels();
      _queueNextTrackOnServer();
    }

    // Enrich server status with track-list metadata for the player bar
    const trackMeta = musicCurrentPath ? musicTrackList.find(t => t.path === musicCurrentPath) : null;
    if (trackMeta) {
      s.artist    = s.artist    || trackMeta.artist    || '';
      s.album     = s.album     || trackMeta.album     || '';
      s.genre     = s.genre     || trackMeta.genre     || '';
      s.year      = s.year      || trackMeta.year      || null;
      s.ext       = s.ext       || trackMeta.ext       || '';
      s.durationSec = s.durationSec || trackMeta.durationSec || 0;
      s.hasCover  = s.hasCover  ?? trackMeta.hasCover;
      s.coverPath = musicCurrentPath;
    }
    updateMusicBar(s);

    // Surface server-side music playback errors (skip in local mode â€” local errors are handled by app-local.js)
    const musicErr = (s.lastError || s.LastError || '').trim();
    if (musicErr && !isPlayLocal() && typeof showPlaybackErrorPopup === 'function') {
      const trackName = s.title || s.Title || (musicCurrentPath ? musicCurrentPath.replace(/.*[/\\]/, '') : '');
      const label = trackName ? `"${trackName}"` : 'Track';
      showPlaybackErrorPopup('Music', `${label} could not play.\n\n${musicErr}`);
    }

    // auto-advance when track ends naturally and the server didn't already handle it
    // (server handles it when _nextTrackPath was set; this covers the case where
    //  the browser is still open but autoplay wasn't yet registered)
    // Skip in local mode â€” the ended event on the <audio> element drives advancement there.
    if (!isPlayLocal() && !s.isPlaying && !s.isPaused && wasPlaying && musicCurrentPath && !s.currentPath) {
      musicCurrentPath = null;
      if (_musicAutoPlay || _musicShuffle || _musicRepeat !== 'none') {
        const nextT = _musicNextTrack();
        if (nextT) {
          await playMusic(nextT.path, nextT.name);
          // playMusic restarts the poll; skip the stop-check below
          return;
        }
      }
    }

    // Stop polling only when truly idle (nothing playing and we didn't just advance)
    if (!s.isPlaying && !s.isPaused && !s.currentPath && !musicCurrentPath) stopMusicPlaybackPoll();
  }, 1500);
}

function stopMusicPlaybackPoll() {
  if (musicPlaybackPollTimer) {
    clearInterval(musicPlaybackPollTimer);
    musicPlaybackPollTimer = null;
  }
}

function switchMode(mode) {
  // Stop whichever player is active before switching away from it.
  const leavingVideo = currentMode === 'video' && mode !== 'video';
  const leavingMusic = currentMode === 'music' && mode !== 'music';
  const leavingRadio = currentMode === 'radio' && mode !== 'radio';
  if (leavingVideo && playingPath && typeof stop !== 'undefined') stop();
  if (leavingMusic && musicIsPlaying && typeof musicStop !== 'undefined') musicStop();
  if (leavingRadio && typeof _radioIsPlaying !== 'undefined' && _radioIsPlaying && typeof radioStop !== 'undefined') radioStop();

  currentMode = mode;
  // Remove music dock when leaving music mode
  if (mode !== 'music') document.body.classList.remove('music-player-docked');
  if (mode !== 'radio' && !_radioIsPlaying) document.body.classList.remove('radio-player-docked');
  document.body.classList.toggle('radio-mode', mode === 'radio');
  ['video', 'music', 'radio'].forEach((m) => {
    const tab = document.getElementById('tab-' + m);
    const browser = document.getElementById(m === 'video' ? 'browser' : m + '-browser');
    const active = m === mode;
    if (tab) tab.classList.toggle('active', active);
    if (browser) browser.style.display = active ? '' : 'none';
  });
  // Show/hide the radio sub-tab bar and count in the header
  const radioTabHdr = document.getElementById('radio-tab-bar-header');
  const radioCountHdr = document.getElementById('radio-station-count');
  if (radioTabHdr) radioTabHdr.style.display = mode === 'radio' ? 'flex' : 'none';
  if (radioCountHdr) radioCountHdr.style.display = mode === 'radio' ? '' : 'none';
  const searchEl = document.getElementById('search');
  const searchRow = document.getElementById('search-row');
  // Always show the nav row; just switch which recent strip is active
  const navRow = document.getElementById('browse-nav-row');
  const vStrip = document.getElementById('video-recent-strip');
  const mStrip = document.getElementById('music-recent-strip');
  const rStrip = document.getElementById('radio-recent-strip');
  if (navRow) navRow.style.display = '';
  if (vStrip) vStrip.style.display = mode === 'video' ? '' : 'none';
  if (mStrip) mStrip.style.display = mode === 'music' ? '' : 'none';
  if (rStrip) rStrip.style.display = mode === 'radio' ? '' : 'none';
  if (mode === 'video') {
    if (searchRow) searchRow.style.display = '';
    searchEl.placeholder = 'Search entire library...';
    stopMusicStatusPoll();
    stopRadioStatusPoll();
    const back = document.getElementById('back-button');
    if (back) back.onclick = goBack;
    _applyVideoCommandBar(true); // always show bar when entering video mode
    const mcb = document.getElementById('music-command-bar');
    if (mcb) mcb.style.display = 'none';
    const rcb = document.getElementById('radio-command-bar');
    if (rcb) rcb.style.display = 'none';
    refreshLibraryStatus();
    if (currentData) render(currentData);
    else browse(null);
  } else if (mode === 'music') {
    if (typeof _applyVideoCommandBar === 'function') _applyVideoCommandBar(false);
    const rcb = document.getElementById('radio-command-bar');
    if (rcb) rcb.style.display = 'none';
    const mcb = document.getElementById('music-command-bar');
    if (mcb) { mcb.style.display = 'flex'; _syncMusicCommandBar(); }
    if (searchRow) searchRow.style.display = '';
    searchEl.placeholder = 'Search music library...';
    stopRadioStatusPoll();
    document.body.classList.remove('radio-player-docked');
    // clear any video scan-status left-overs
    const el = document.getElementById('scan-status');
    if (el) {
      el.textContent = '';
      el.className = '';
    }
    // restore music bar if already playing
    if (musicIsPlaying) startMusicPlaybackPoll();
    _ensureMusicKeyboardNav();
    if (!currentMusicData) browseMusic(null);
    else renderMusicHeader(currentMusicData, false);
  } else if (mode === 'radio') {
    if (typeof _applyVideoCommandBar === 'function') _applyVideoCommandBar(false);
    const mcb = document.getElementById('music-command-bar');
    if (mcb) mcb.style.display = 'none';
    const rcb = document.getElementById('radio-command-bar');
    if (rcb) rcb.style.display = 'flex';
    if (searchRow) searchRow.style.display = 'none';
    stopMusicStatusPoll();
    setMusicHeaderForMode('radio');
    radioInit();
  }
}

function setMusicHeaderForMode(mode) {
  const bc = document.getElementById('breadcrumb');
  const countLine = document.getElementById('count-line');
  const scanStatus = document.getElementById('scan-status');
  const back = document.getElementById('back-button');
  if (back) back.style.display = 'none';
  if (bc) bc.innerHTML = '';
  if (countLine) countLine.textContent = '';
  if (scanStatus) {
    scanStatus.textContent = '';
    scanStatus.className = '';
  }
}

function renderMusicHeader(data, searching) {
  const bc = document.getElementById('breadcrumb');
  const countLine = document.getElementById('count-line');
  const scanStatus = document.getElementById('scan-status');
  const back = document.getElementById('back-button');

  // Breadcrumb
  if (bc) {
    bc.innerHTML = '';
    if (searching) {
      bc.innerHTML =
        '<a onclick="browseMusic(null)">&#128193; Music Root</a><span> &rsaquo; </span><span class="crumb-current">Search results</span>';
    } else {
      const root = data.musicRoot || '';
      const folder = data.folder || root;
      let html = '<a onclick="browseMusic(null)">&#128193; Music Root</a>';
      if (folder && root && folder.toLowerCase() !== root.toLowerCase()) {
        // Build segments relative to music root
        const rel = folder.startsWith(root) ? folder.slice(root.length) : '';
        const sep = rel.indexOf('/') !== -1 ? '/' : '\\';
        const parts = rel.split(sep).filter((p) => p.length > 0);
        let cumulative = root;
        parts.forEach((part, i) => {
          cumulative += sep + part;
          const captured = cumulative;
          if (i === parts.length - 1) {
            html += '<span> &rsaquo; </span><span class="crumb-current">' + esc(part) + '</span>';
          } else {
            html +=
              '<span> &rsaquo; </span><a onclick="browseMusic(\'' +
              jsStr(captured) +
              '\')">' +
              esc(part) +
              '</a>';
          }
        });
      }
      bc.innerHTML = html;
    }
  }

  // Back button
  if (back) {
    const canGoBack =
      searching ||
      musicBrowseHistory.length > 0 ||
      (data.folder != null && data.folder !== (data.musicRoot || ''));
    back.style.display = canGoBack ? 'block' : 'none';
    back.dataset.dir = '';
    back.onclick = () => {
      if (currentMode === 'music') {
        if (searching) { browseMusic(currentMusicFolder); return; }
        const prev = musicBrowseHistory.length > 0 ? musicBrowseHistory.pop() : null;
        browseMusic(prev);
      } else goBack();
    };
  }

  // Count line
  if (countLine) {
    document.getElementById('view-toggle-video') &&
      (document.getElementById('view-toggle-video').style.display = 'none');
    document.getElementById('view-toggle-music') &&
      (document.getElementById('view-toggle-music').style.display = 'none');
    _applyViewToggleBtn('music');
    const txt = document.getElementById('count-text');
    let countText = '';
    if (searching) {
      const fc = data.folders?.length || 0;
      const tc = data.files?.length || 0;
      countText = fc + ' folder(s) and ' + tc + ' result(s)';
    } else {
      const fc = data.folders?.length || 0;
      const loaded = data.files?.length || 0;
      const total = data.totalInFolder || loaded;
      countText = total > loaded
        ? fc + ' folder(s), ' + loaded + ' of ' + total + ' track(s) loaded'
        : fc + ' folder(s), ' + total + ' track(s)';
    }
    if (txt) txt.textContent = countText;
    const mcbCount = document.getElementById('mcb-count');
    if (mcbCount) mcbCount.textContent = countText;
    _setCbRightVisible('mcb-right', (data.files?.length || 0) > 0 && _viewMode.music !== 'list');
  }

  // Scan status line
  if (scanStatus && currentMode === 'music') {
    scanStatus.classList.remove('scanning', 'error', 'global-scan-status');
    const total = Number(data.indexedFiles) || 0;
    const isScanning = Boolean(data.indexing);
    const err = (data.lastError || '').trim();
    const root = (data.musicRoot || '').trim();
    if (err) {
      scanStatus.classList.add('error');
      scanStatus.textContent = 'Music scan failed: ' + err + (root ? ' (path: ' + root + ')' : '');
    } else if (isScanning) {
      scanStatus.classList.add('scanning', 'global-scan-status');
      scanStatus.textContent =
        'Scanning music library\u2026 ' + total + ' track(s) indexed so far.';
      startMusicStatusPoll();
    } else if (total > 0) {
      scanStatus.textContent =
        'Music Library Ready: ' + total + ' song(s)' + (root ? ' \u2014 ' + root : '');
    } else {
      scanStatus.textContent =
        'Music library empty or path not configured' + (root ? ' \u2014 path: ' + root : '');
    }
  }
}

function _syncMusicCommandBar(playAllVisible) {
  const mcb = document.getElementById('music-command-bar');
  if (!mcb) return;
  // Sync view button label
  const viewBtn = document.getElementById('mcb-view-btn');
  const isList = _viewMode.music === 'list';
  if (viewBtn) {
    viewBtn.textContent = isList ? '\u2261 List' : '\u25a6 Grid';
    viewBtn.title = isList ? 'Switch to grid view' : 'Switch to list view';
  }
  // Sync sort select
  const sortSel = document.getElementById('mcb-sort');
  if (sortSel) sortSel.value = _musicSort.col;
  // Re-sync sort-block visibility based on view mode
  if (currentMusicData) _setCbRightVisible('mcb-right', (currentMusicData.files?.length || 0) > 0 && !isList);
  // Show/hide Play All
  const playAllBtn = document.getElementById('mcb-play-all');
  if (playAllBtn && playAllVisible != null) {
    playAllBtn.style.display = playAllVisible ? '' : 'none';
    if (playAllVisible) {
      const queueCount = window._musicQueue ? window._musicQueue.length : 0;
      const badge = queueCount > 0 ? ` <span class="music-queue-badge">${queueCount} queued</span>` : '';
      playAllBtn.innerHTML = '\u25b6 Play All' + badge;
    }
  }
}

function startMusicStatusPoll() {
  if (musicStatusPollTimer) return;
  let pollCount = 0;
  musicStatusPollTimer = setInterval(async () => {
    try {
      const res = await fetch('/api/music/status');
      if (!res.ok) return;
      const s = await res.json();
      if (currentMode !== 'music') return stopMusicStatusPoll();
      if (!s.isScanning) {
        stopMusicStatusPoll();
        browseMusic(currentMusicFolder, 0, false, true);
      } else {
        // Update live count directly in the scan status element
        const scanStatus = document.getElementById('scan-status');
        if (scanStatus && currentMode === 'music') {
          scanStatus.classList.add('scanning', 'global-scan-status');
          const folder = (s.currentScanFolder || '').replace(/.*[/\\]/, '');
          const folderHint = folder ? ' \u2014 ' + folder : '';
          scanStatus.textContent =
            'Scanning music library\u2026 ' +
            (s.indexedFiles || 0) +
            ' track(s) indexed' +
            folderHint;
        }
        if (currentMusicData) {
          currentMusicData.indexing = true;
          currentMusicData.indexedFiles = s.indexedFiles || 0;
        }
        // Re-browse every ~10s during scan so tracks appear progressively
        pollCount++;
        if (pollCount % 5 === 0) browseMusic(currentMusicFolder, 0, false, true);
      }
    } catch (e) {}
  }, 2000);
}

function stopMusicStatusPoll() {
  if (musicStatusPollTimer) {
    clearInterval(musicStatusPollTimer);
    musicStatusPollTimer = null;
  }
}

// â”€â”€ Music keyboard navigation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
let _musicKbdBound = false;
function _ensureMusicKeyboardNav() {
  if (_musicKbdBound) return;
  _musicKbdBound = true;
  document.addEventListener('keydown', (e) => {
    if (currentMode !== 'music') return;
    // Ignore when focus is inside an input/textarea
    const tag = document.activeElement?.tagName;
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;

    const cards = Array.from(document.querySelectorAll('.music-track-card'));
    if (!cards.length) return;

    const cur = document.querySelector('.music-track-card.kb-focus');
    let idx = cur ? cards.indexOf(cur) : -1;

    if (e.key === 'ArrowDown') {
      e.preventDefault();
      idx = Math.min(idx + 1, cards.length - 1);
      _musicKbdFocus(cards, idx);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      idx = Math.max(idx - 1, 0);
      _musicKbdFocus(cards, idx);
    } else if ((e.key === 'Enter' || e.key === ' ') && idx >= 0) {
      e.preventDefault();
      cards[idx].click();
    }
  });
}

function _musicKbdFocus(cards, idx) {
  cards.forEach((c, i) => c.classList.toggle('kb-focus', i === idx));
  cards[idx]?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
}

// â”€â”€ Music context menu â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
let _musicCtxMenu = null;
let _musicCtxLongPressTimer = null;

function _musicShowContextMenu(e, path, name) {
  e.preventDefault();
  _musicDismissContextMenu();
  const menu = document.createElement('div');
  menu.className = 'music-ctx-menu';
  menu.innerHTML =
    `<button onclick="_musicCtxPlay('${jsStr(path)}','${jsStr(name)}')">&#9654; Play</button>` +
    `<button onclick="_musicCtxQueue('${jsStr(path)}','${jsStr(name)}')">&#43; Add to Queue</button>` +
    `<button onclick="_musicCtxCopy('${jsStr(path)}')">&#128203; Copy path</button>`;
  document.body.appendChild(menu);
  _musicCtxMenu = menu;
  // Position near pointer
  const x = e.clientX ?? (e.touches?.[0]?.clientX ?? 0);
  const y = e.clientY ?? (e.touches?.[0]?.clientY ?? 0);
  menu.style.left = Math.min(x, window.innerWidth - 180) + 'px';
  menu.style.top  = Math.min(y, window.innerHeight - 100) + 'px';
  // Dismiss on outside click
  setTimeout(() => document.addEventListener('pointerdown', _musicDismissContextMenu, { once: true }), 50);
}

function _musicDismissContextMenu() {
  if (_musicCtxMenu) { _musicCtxMenu.remove(); _musicCtxMenu = null; }
}

function _musicCtxPlay(path, name) {
  _musicDismissContextMenu();
  playMusic(path, name);
}

function _musicCtxQueue(path, name) {
  _musicDismissContextMenu();
  if (!window._musicQueue) window._musicQueue = [];
  window._musicQueue.push({ path, name });
  if (currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
  _renderMusicQueuePeek();
}

function _musicCtxCopy(path) {
  _musicDismissContextMenu();
  try { navigator.clipboard.writeText(path); } catch (_) {}
}

// â”€â”€ Multi-select â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
function _musicSelectionToggle(path, idx, shiftHeld) {
  if (shiftHeld && _musicLastSelectedIdx >= 0 && currentMusicData && currentMusicData.files) {
    const sorted = _musicSortFiles(currentMusicData.files);
    const from = Math.min(_musicLastSelectedIdx, idx);
    const to   = Math.max(_musicLastSelectedIdx, idx);
    for (let i = from; i <= to; i++) {
      if (sorted[i]) _musicSelected.add(sorted[i].path);
    }
  } else {
    if (_musicSelected.has(path)) { _musicSelected.delete(path); }
    else { _musicSelected.add(path); _musicLastSelectedIdx = idx; }
  }
  if (shiftHeld) _musicLastSelectedIdx = idx;
  _renderMusicSelection();
}
function _renderMusicSelection() {
  const normal = document.getElementById('mcb-normal');
  const sel    = document.getElementById('mcb-selection');
  const lbl    = document.getElementById('music-selection-label');
  const count  = _musicSelected.size;
  if (normal) normal.style.display = count > 0 ? 'none' : '';
  if (sel)    sel.style.display    = count > 0 ? ''     : 'none';
  if (lbl)    lbl.textContent = count > 0 ? `${count} track${count !== 1 ? 's' : ''} selected` : '';
  // Sync checkboxes in the DOM
  document.querySelectorAll('.music-track-card').forEach((el) => {
    const cb = el.querySelector('.mtc-checkbox');
    if (cb) cb.checked = _musicSelected.has(el.dataset.path || '');
    el.classList.toggle('selected', _musicSelected.has(el.dataset.path || ''));
  });
}
function _musicSelectionClear() {
  _musicSelected.clear();
  _musicLastSelectedIdx = -1;
  _renderMusicSelection();
}
function _musicSelectionAddToQueue() {
  if (!_musicSelected.size) return;
  if (!window._musicQueue) window._musicQueue = [];
  if (currentMusicData && currentMusicData.files) {
    const sorted = _musicSortFiles(currentMusicData.files);
    sorted.forEach((f) => {
      if (_musicSelected.has(f.path)) window._musicQueue.push({ path: f.path, name: f.name });
    });
  }
  _musicSelectionClear();
  if (currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
  _refreshMusicNavLabels();
}
function _musicSelectionPlay()
  if (!_musicSelected.size) return;
  if (currentMusicData && currentMusicData.files) {
    const sorted = _musicSortFiles(currentMusicData.files);
    const selected = sorted.filter((f) => _musicSelected.has(f.path));
    if (!selected.length) return;
    // Play the first selected track; queue the rest
    const [first, ...rest] = selected;
    window._musicQueue = rest.map((f) => ({ path: f.path, name: f.name }));
    playMusic(first.path, first.name);
    _refreshMusicNavLabels();
  }
  _musicSelectionClear();
}
// Update the pending-queue bar (visible before playback starts)
function _updateMusicQueuePendingBar() {
  const bar = document.getElementById('music-queue-pending-bar');
  if (!bar) return;
  const playerBar = document.getElementById('music-player-bar');
  const playerVisible = playerBar && playerBar.style.display !== 'none';
  const count = window._musicQueue ? window._musicQueue.length : 0;
  if (!playerVisible && count > 0) {
    bar.style.display = '';
    bar.textContent = `\u23F3 ${count} track${count !== 1 ? 's' : ''} queued â€” start a song to begin playback`;
  } else {
    bar.style.display = 'none';
  }
}

function _musicCardContextBind(el, path, name) {
  el.addEventListener('contextmenu', (e) => _musicShowContextMenu(e, path, name));
  el.addEventListener('touchstart', (e) => {
    _musicCtxLongPressTimer = setTimeout(() => _musicShowContextMenu(e, path, name), 600);
  }, { passive: true });
  el.addEventListener('touchend', () => {
    clearTimeout(_musicCtxLongPressTimer);
  });
  el.addEventListener('touchmove', () => {
    clearTimeout(_musicCtxLongPressTimer);
  }, { passive: true });
}

async function browseMusic(folder, offset = 0, append = false, silentRefresh = false) {
  if (!append) {
    if (!silentRefresh) {
      _musicSelectionClear();
      // Push current folder to history before navigating
      if (currentMusicFolder !== folder) {
        if (currentMusicFolder !== null) musicBrowseHistory.push(currentMusicFolder);
        // Going to root resets history
        if (folder === null || folder === undefined) musicBrowseHistory = [];
      }
    }
    currentMusicFolder = folder;
  }
  const mb = document.getElementById('music-browser');
  if (!mb) return;
  if (!append && !silentRefresh)
    mb.innerHTML = '<div style="padding:.75rem;color:var(--muted,#9aa8c2)">Loading\u2026</div>';
  try {
    const url =
      '/api/music/browse' +
      (folder ? '?folder=' + encodeURIComponent(folder) : '') +
      (offset ? '&offset=' + encodeURIComponent(offset) : '');
    const res = await fetch(url);
    if (!res.ok) {
      mb.innerHTML = '<div style="padding:.75rem">Error ' + res.status + '</div>';
      return;
    }
    const data = await res.json();
    if (append && currentMusicData) {
      currentMusicData.files = [...(currentMusicData.files || []), ...(data.files || [])];
      currentMusicData.offset = data.offset;
      currentMusicData.hasMore = data.hasMore;
      // totalInFolder comes from the first page; don't overwrite it
      if (data.totalInFolder) currentMusicData.totalInFolder = data.totalInFolder;
    } else {
      currentMusicData = data;
      currentMusicData.folder = folder;
    }
    if (currentMode === 'music') renderMusicHeader(currentMusicData, false);
    renderMusicCards(currentMusicData);
    // Show recently played strip on the music root, hide in subfolders
    const mStrip = document.getElementById('music-recent-strip');
    if (!folder && !append) {
      if (mStrip) mStrip.style.display = '';
      loadMusicRecent().then((files) => { if (files.length) renderMusicRecent(files); });
    } else if (!append && mStrip) {
      mStrip.style.display = 'none';
    }
  } catch (e) {
    mb.innerHTML = '<div style="padding:.75rem">Error: ' + e + '</div>';
  }
}

async function searchMusicLibrary(q, offset = 0, append = false) {
  const mb = document.getElementById('music-browser');
  if (!mb) return;
  try {
    const res = await fetch(
      '/api/music/search?q=' +
        encodeURIComponent(q) +
        (offset ? '&offset=' + encodeURIComponent(offset) : '')
    );
    if (!res.ok) {
      setSearchBusy(false);
      return;
    }
    const data = await res.json();
    if (append && currentMusicData) {
      currentMusicData.files = [...(currentMusicData.files || []), ...(data.files || [])];
      currentMusicData.hasMore = data.hasMore;
    } else currentMusicData = { ...data, folders: [], query: q };
    setSearchBusy(false);
    if (currentMode === 'music') renderMusicHeader(currentMusicData, true);
    renderMusicCards(currentMusicData, true);
  } catch (e) {
    setSearchBusy(false);
  }
}

// â”€â”€ Music card metadata helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/**
 * Strip a leading track-number prefix from a filename (no extension).
 *   "01 - Song Title" â†’ "Song Title"
 *   "01. Song Title"  â†’ "Song Title"
 *   "Song Title"      â†’ "Song Title"
 */
function _titleFromName(name) {
  // Strip leading track number (e.g. "03 - " or "03.")
  let s = (name || '').replace(/\.[^.]+$/, ''); // remove extension
  s = s.replace(/^\d{1,3}[.\s\-]+\s*/, '');     // strip leading track #
  // Replace underscores and hyphens used as word separators with spaces
  s = s.replace(/[_]+/g, ' ').replace(/\s*-\s*/g, ' - ');
  // Title-case: capitalise first letter of each word
  s = s.replace(/\b\w/g, c => c.toUpperCase()).trim();
  return s || name;
}

/** Human-readable format badge label */
function _fmtBadge(ext) {
  const map = { mp3: 'MP3', flac: 'FLAC', aac: 'AAC', m4a: 'M4A', ogg: 'OGG', wma: 'WMA', wav: 'WAV', opus: 'OPUS', ape: 'APE', alac: 'ALAC' };
  return map[(ext || '').toLowerCase()] || (ext ? ext.toUpperCase() : '');
}

/** Format seconds as m:ss or h:mm:ss */
function _fmtDuration(sec) {
  if (sec == null || sec < 0) return '';
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  const s = sec % 60;
  if (h > 0) return `${h}:${String(m).padStart(2,'0')}:${String(s).padStart(2,'0')}`;
  return `${m}:${String(s).padStart(2,'0')}`;
}

// â”€â”€ Music list sort state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
let _musicSort = (() => {
  try {
    const saved = localStorage.getItem('remotePlayMusicSort');
    if (saved) return JSON.parse(saved);
  } catch (_) {}
  return { col: 'trackNum', dir: 'asc' };
})();

function _musicSortFiles(files) {
  if (!files || !files.length) return files;
  const { col, dir } = _musicSort;
  const mul = dir === 'asc' ? 1 : -1;
  return [...files].sort((a, b) => {
    let va = a[col], vb = b[col];
    // trackNum: nulls last
    if (col === 'trackNum') {
      if (va == null && vb == null) return 0;
      if (va == null) return 1;
      if (vb == null) return -1;
      return (va - vb) * mul;
    }
    va = String(va ?? '').toLowerCase();
    vb = String(vb ?? '').toLowerCase();
    return va < vb ? -mul : va > vb ? mul : 0;
  });
}

function musicSortBy(col) {
  // When called from the <select>, toggling direction only makes sense for column-header clicks;
  // a fresh select choice always resets to ascending.
  const fromSelect = document.getElementById('mcb-sort') === document.activeElement;
  if (!fromSelect && _musicSort.col === col) {
    _musicSort.dir = _musicSort.dir === 'asc' ? 'desc' : 'asc';
  } else {
    _musicSort = { col, dir: 'asc' };
  }
  try { localStorage.setItem('remotePlayMusicSort', JSON.stringify(_musicSort)); } catch (_) {}
  const sel = document.getElementById('mcb-sort');
  if (sel) sel.value = _musicSort.col;
  if (currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
}

function renderMusicCards(data, searching) {
  const mb = document.getElementById('music-browser');
  if (!mb) return;
  let html = '';
  if (!searching && data.folders && data.folders.length) {
    html += '<div class="folder-list">';
    data.folders.forEach((f) => {
      html +=
        '<div class="folder-row" role="button" tabindex="0" onkeydown="activateKeyboardClick(event,this)" onclick="browseMusic(\'' +
        jsStr(f.folder) +
        '\')">' +
        '<span class="folder-icon">&#128193;</span><span class="folder-name">' +
        esc(f.name) +
        '</span></div>';
    });
    html += '</div>';
  }
  if (data.files && data.files.length) {
    const isList = _viewMode.music === 'list';
    const sortedFiles = _musicSortFiles(data.files);

    // â”€â”€ Sync command bar â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    const playAllVisible = !!data.folder;
    _syncMusicCommandBar(playAllVisible);

    if (isList) {
      const _si = (col) => _musicSort.col === col ? (_musicSort.dir === 'asc' ? ' â–²' : ' â–¼') : '';
      html += '<div class="music-list-header">'
        + '<span title="Art / Track #">#</span>'
        + `<span class="sortable" onclick="musicSortBy('name')" title="Sort by title">Title${_si('name')}</span>`
        + '<span title="Select"></span>'
        + `<span class="sortable" onclick="musicSortBy('ext')" title="Sort by format">Fmt${_si('ext')}</span>`
        + `<span class="sortable" onclick="musicSortBy('artist')" title="Sort by artist">Artist${_si('artist')}</span>`
        + `<span class="sortable" onclick="musicSortBy('album')" title="Sort by album">Album${_si('album')}</span>`
        + `<span class="sortable mtc-dur-hdr" onclick="musicSortBy('durationSec')" title="Sort by duration">Time${_si('durationSec')}</span>`
        + '</div>';
    }
    html += '<div class="music-grid' + (isList ? ' list-view' : '') + '">';
    sortedFiles.forEach((f, idx) => {
      const isPlaying = musicCurrentPath && f.path === musicCurrentPath;
      const isSelected = _musicSelected.has(f.path);
      // Prefer embedded ID3 title; fall back to cleaned filename
      const title      = f.tagTitle ? f.tagTitle : _titleFromName(f.name);
      const badge      = _fmtBadge(f.ext);
      const artist     = f.artist || '';
      const album      = f.album  || '';
      const trackNum   = f.trackNum;
      const duration   = f.durationSec != null ? _fmtDuration(f.durationSec) : '';
      const genre      = f.genre || '';
      const year       = f.year  || null;
      const hasCover   = !!f.hasCover;

      // Merged genre Â· year single line
      const genreYear = [genre, year].filter(Boolean).join(' Â· ');

      let card = `<div class="music-track-card${isPlaying ? ' playing' : ''}${isSelected ? ' selected' : ''}${isList && idx % 2 === 1 ? ' alt-row' : ''}" data-path="${esc(f.path)}" data-idx="${idx}" title="${esc(title)}">`;

      // â”€â”€ Checkbox (for multi-select) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
      card += `<input type="checkbox" class="mtc-checkbox" data-path="${esc(f.path)}" data-idx="${idx}"${isSelected ? ' checked' : ''} tabindex="-1" aria-label="Select ${esc(title)}" />`;

      // â”€â”€ Col 1: thumbnail / playing bars / music-note placeholder / track number
      // Always render both cover/placeholder AND bars so toggling .playing on the card
      // is enough â€” no stale HTML left on the wrong row when the track changes.
      if (hasCover) {
        card += `<span class="mtc-play-indicator mtc-thumb-wrap">`
          + `<img class="mtc-thumb" src="/api/music/cover?path=${encodeURIComponent(f.path)}" loading="lazy" onerror="this.parentElement.classList.add('mtc-no-cover');this.remove()" />`
          + `<span class="mtc-bars mtc-bars-overlay"><span></span><span></span><span></span></span>`
          + `</span>`;
      } else {
        card += `<span class="mtc-play-indicator mtc-no-cover">`
          + (trackNum != null ? `<span class="mtc-tracknum mtc-tracknum-default">${trackNum}</span>` : '')
          + `<span class="mtc-bars"><span></span><span></span><span></span></span>`
          + `</span>`;
      }

      // â”€â”€ Col 2: title â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
      card += `<span class="mtc-title">${esc(title)}</span>`;

      // â”€â”€ Col 3: format badge + duration (together in top-right) â”€â”€â”€â”€â”€â”€â”€â”€â”€
      const durHtml = duration ? `<span class="mtc-badge-dur">${esc(duration)}</span>` : '';
      card += `<span class="mtc-badge-group">${badge ? `<span class="mtc-badge">${esc(badge)}</span>` : ''} ${durHtml}</span>`;

      // â”€â”€ Col 4: artist â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
      card += `<span class="mtc-artist">${esc(artist)}</span>`;

      // â”€â”€ Col 5: album â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
      card += `<span class="mtc-album">${esc(album)}</span>`;

      // â”€â”€ Col 6 (list only): duration â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
      if (isList) {
        card += `<span class="mtc-duration">${esc(duration)}</span>`;
      }

      // â”€â”€ Grid-only: merged genre Â· year line â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
      if (!isList && genreYear) {
        card += `<div class="mtc-genre-year card-only">${esc(genreYear)}</div>`;
      }

      card += '</div>';
      html += card;
    });
    html += '</div>';
    if (data.hasMore) {
      const shown = data.files.length;
      const total = totalInFolder;
      html +=
        '<button class="load-more-btn" onclick="loadMoreMusic()">Load more tracks (' +
        shown + ' of ' + total + ' loaded)</button>';
    }
  } else if (data.folder && data.indexing) {
    html +=
      '<div style="padding:1rem;color:var(--muted,#9aa8c2)">&#128197; Still indexing \u2014 tracks will appear here when the scan reaches this folder.</div>';
  } else if (data.folder && !data.indexing) {
    html += '<div style="padding:1rem;color:var(--muted,#9aa8c2)">No tracks found.</div>';
  }
  mb.innerHTML =
    html ||
    '<div style="padding:1rem;color:var(--muted,#9aa8c2)">Select a folder to browse tracks.</div>';

  // Attach context-menu bindings and click handlers (can't use inline attrs with complex args)
  if (data.files) {
    const sortedForCtx = _musicSortFiles(data.files);
    document.querySelectorAll('.music-track-card').forEach((el, i) => {
      const f = sortedForCtx[i];
      if (!f) return;
      _musicCardContextBind(el, f.path, f.name);
      // Card click: if checkbox clicked â†’ toggle selection; otherwise play
      el.addEventListener('click', (e) => {
        const cb = e.target.closest('.mtc-checkbox');
        if (cb) {
          e.stopPropagation();
          _musicSelectionToggle(f.path, i, e.shiftKey);
          return;
        }
        // Shift+click on the row itself also selects
        if (e.shiftKey || _musicSelected.size > 0) {
          _musicSelectionToggle(f.path, i, e.shiftKey);
          return;
        }
        playMusic(f.path, f.name);
      });
    });
  }
}

function loadMoreMusic() {
  if (!currentMusicData) return;
  const offset = (currentMusicData.files || []).length;
  if (currentMusicData.query !== undefined)
    searchMusicLibrary(currentMusicData.query || '', offset, true);
  else browseMusic(currentMusicData.folder || currentMusicFolder, offset, true);
}

// kept for compatibility â€” routes to the card renderer
function renderMusic(data, searching) {
  renderMusicCards(data, searching);
}

function playMusicAll() {
  if (!currentMusicData || !currentMusicData.files || !currentMusicData.files.length) return;
  const sorted = _musicSortFiles(currentMusicData.files);
  if (!sorted.length) return;
  const first = sorted[0];
  playMusic(first.path, first.name);
  // queue remaining â€” stored for auto-advance
  window._musicQueue = sorted.slice(1);
  // refresh to show queue count badge and nav labels
  if (currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
  _refreshMusicNavLabels();
}
