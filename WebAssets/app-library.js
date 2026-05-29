let currentMode = localStorage.getItem('remotePlayMode') || 'video';
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
let _musicLastSelectedIdx = -1;
// ── Video multi-select state ──────────────────────────────────────────────
let _videoSelected = new Set(); // selected video paths

function _videoSelectionToggle(path) {
  if (_videoSelected.has(path)) _videoSelected.delete(path);
  else _videoSelected.add(path);
  // Update the card's selected class and checkbox state
  const card = document.getElementById(cardIdFor(path));
  if (card) {
    card.classList.toggle('selected', _videoSelected.has(path));
    const cb = card.querySelector('.vlc-select-cb');
    if (cb) cb.checked = _videoSelected.has(path);
  }
  _renderVideoSelection();
}

function _videoSelectionClear() {
  _videoSelected.forEach(path => {
    const card = document.getElementById(cardIdFor(path));
    if (card) {
      card.classList.remove('selected');
      const cb = card.querySelector('.vlc-select-cb');
      if (cb) cb.checked = false;
    }
  });
  _videoSelected.clear();
  _renderVideoSelection();
}

function _renderVideoSelection() {
  const normal = document.getElementById('vcb-normal');
  const sel    = document.getElementById('vcb-selection');
  const lbl    = document.getElementById('video-selection-label');
  const count  = _videoSelected.size;
  if (normal) normal.style.display = count > 0 ? 'none' : '';
  if (sel)    sel.style.display    = count > 0 ? ''     : 'none';
  if (lbl)    lbl.textContent = count > 0 ? `${count} video${count !== 1 ? 's' : ''} selected` : '';
}

async function _videoSelectionPlay() {
  if (!_videoSelected.size) return;
  const paths = Array.from(_videoSelected);
  const first = paths[0];
  const rest  = paths.slice(1);
  // Queue the rest before playing the first
  for (const p of rest) {
    if (queuedVideos.has(p)) continue;
    await api('/api/queue/add?path=' + encodeURIComponent(p));
    queuedVideos.add(p);
    setQueuedCard(p, true);
  }
  _videoSelectionClear();
  play(first);
}

async function _videoSelectionQueue() {
  if (!_videoSelected.size) return;
  const paths = Array.from(_videoSelected);
  const first = paths[0];
  const rest  = paths.slice(1);
  // Queue the rest, then play the first
  for (const p of rest) {
    if (queuedVideos.has(p)) continue;
    await api('/api/queue/add?path=' + encodeURIComponent(p));
    queuedVideos.add(p);
    setQueuedCard(p, true);
  }
  updateQueueControls({
    queue: Array.from(queuedVideos).map((path) => ({
      path,
      title: path.split(/[\\/]/).pop() || path,
    })),
  });
  _videoSelectionClear();
  play(first);
}

// ── Music playback ─────────────────────────────────────────────────────
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
    // Record play in history so recent cards update
    fetch('/api/music/play?path=' + encodeURIComponent(path) + '&recordOnly=1').catch(() => {});
    setTimeout(() => loadMusicRecent().then((files) => renderMusicRecent(files)), 300);
    return;
  }

  await fetch('/api/music/play?path=' + encodeURIComponent(path));
  musicIsPlaying = true;
  setTimeout(() => loadMusicRecent().then((files) => renderMusicRecent(files)), 300);
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
  _onMusicTrackChangedLyrics(null, '', '');
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

const _debouncedMusicVolumeApi = debounce((v) => fetch('/api/music/volume?v=' + v), 150);
const _debouncedMusicBoostApi  = debounce((v) => fetch('/api/music/boost?v='  + v), 150);

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
  _debouncedMusicVolumeApi(vol.toFixed(3));
}
function musicVolumeReset() {
  const slider = document.getElementById('music-bar-volume');
  if (slider) { slider.value = 1; musicVolume(1); }
}

// Combined volume/boost slider (0–1.0 = volume, 1.0–1.3 = boost zone)
function musicCombinedSlider(v) {
  const val = parseFloat(v);
  if (isNaN(val)) return;
  const slider = document.getElementById('music-bar-combined');
  const lbl    = document.getElementById('music-combined-label');
  // Slider range is 0–1.3; convert value to a percentage of total track width
  const totalRange = 1.3;
  const volBoundary = (1.0 / totalRange * 100).toFixed(2) + '%';  // ~76.92%
  const fillPct = (val / totalRange * 100).toFixed(2) + '%';
  if (val <= 1.0) {
    // Volume zone: accent color fills up to value; no boost segment
    if (slider) {
      slider.style.setProperty('--cvs-vol',   fillPct);
      slider.style.setProperty('--cvs-boost', fillPct);
      slider.style.setProperty('--cvs-thumb', 'var(--player-accent,#e94560)');
    }
    if (lbl) lbl.textContent = Math.round(val * 100) + '%';
    musicVolume(val);
    musicBoost(1.0);
  } else {
    // Boost zone: accent fills to 100% boundary; orange fills from there to value
    const gain = 1 + (val - 1) * (2 / 0.3);
    if (slider) {
      slider.style.setProperty('--cvs-vol',   volBoundary);
      slider.style.setProperty('--cvs-boost', fillPct);
      slider.style.setProperty('--cvs-thumb', '#f97316');
    }
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
    lbl.textContent = isFinite(db) ? (db >= 0 ? '+' : '') + db + ' dB' : '—';
  }
  if (isPlayLocal()) {
    const gainNode = typeof _ensureLocalBoostGraph !== 'undefined' ? _ensureLocalBoostGraph() : null;
    if (gainNode) gainNode.gain.value = Math.max(0, boost);
    return;
  }
  _debouncedMusicBoostApi(boost.toFixed(3));
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
  if (title) title.textContent = s.title || '—';

  // Artist + album
  const artistEl = document.getElementById('music-bar-artist');
  if (artistEl) artistEl.textContent = s.artist || '';
  const albumEl = document.getElementById('music-bar-album');
  if (albumEl) albumEl.textContent = s.album || '';

  // Genre · year muted line
  const gyEl = document.getElementById('music-bar-genreyear');
  if (gyEl) {
    const parts = [s.genre, s.year].filter(Boolean);
    gyEl.textContent = parts.join(' · ');
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

  // Lyrics — trigger fetch when the track changes
  const lyricsPath = s.currentPath || musicCurrentPath;
  if (lyricsPath && lyricsPath !== _musicLyricsCurrentPath) {
    _onMusicTrackChangedLyrics(lyricsPath, s.artist || '', s.title || '');
  }

  if (s.lastError && !active) bar.style.display = 'none';
  if (typeof _syncEqWrapVisibility === 'function') _syncEqWrapVisibility();
  if (typeof renderEqPresets === 'function') {
    const eqP = s.eqPreset ?? s.EqPreset ?? -1;
    if (_lastEqPreset !== eqP) renderEqPresets(eqP);
  }
  if (typeof _renderMusicReverbPreset === 'function')
    _renderMusicReverbPreset(s.reverbPreset ?? s.ReverbPreset);
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

// -- Lyrics -------------------------------------------------------------------
const _musicLyricsCache = new Map(); // path -> { found, lyrics, geniusUrl } | 'loading'
let _musicLyricsCurrentPath = null;
let _musicLyricsPanelOpen = false; // persists across track changes

async function _fetchMusicLyrics(path, artist, title) {
  if (_musicLyricsCache.has(path)) return;
  _musicLyricsCache.set(path, 'loading');
  let cleanTitle = title || '';
  if (!cleanTitle || cleanTitle.includes('/') || cleanTitle.includes('\\')) {
    cleanTitle = (path.split(/[\/\\]/).pop() || '').replace(/\.[^.]+$/, '');
  }
  cleanTitle = cleanTitle.replace(/^\d{1,3}[\s._-]+/, '').trim();
  if (!cleanTitle) { _musicLyricsCache.set(path, { found: false }); if (path === _musicLyricsCurrentPath) _applyMusicLyricsState(path); return; }
  try {
    const params = new URLSearchParams({ title: cleanTitle });
    if (artist) params.set('artist', artist);
    const res  = await fetch('/api/music/lyrics?' + params.toString());
    const data = await res.json();
    _musicLyricsCache.set(path, data);
  } catch (_e) {
    _musicLyricsCache.set(path, { found: false });
  }
  if (path === _musicLyricsCurrentPath) _applyMusicLyricsState(path);
}

function _applyMusicLyricsState(path) {
  const btn   = document.getElementById('music-btn-lyrics');
  const panel = document.getElementById('music-lyrics-panel');
  const body  = document.getElementById('music-lyrics-body');
  const src   = document.getElementById('music-lyrics-source');
  if (!btn) return;

  if (!path) {
    btn.style.display = 'none';
    _closeLyricsPanel();
    return;
  }

  btn.style.display = '';
  const entry = _musicLyricsCache.get(path);

  if (!entry || entry === 'loading') {
    btn.textContent = '\u23F3 Lyrics';
    btn.disabled = true;
    // Panel stays open but shows loading state
    if (_musicLyricsPanelOpen && panel) {
      _openLyricsPanel();
      if (body) body.textContent = 'Loading lyrics\u2026';
      if (src)  src.innerHTML = '';
    }
    return;
  }

  btn.disabled = false;

  if (!entry.found) {
    btn.textContent = '\uD83C\uDFA4 No lyrics';
    btn.classList.add('lyrics-not-found');
    btn.classList.remove('active');
    // No lyrics — hide panel visually but keep intent so next track with lyrics reopens it
    _closeLyricsPanelVisual();
    return;
  }

  btn.textContent = '\uD83C\uDFA4 Lyrics';
  btn.classList.remove('lyrics-not-found');
  // Restore open state
  if (_musicLyricsPanelOpen) {
    _openLyricsPanel();
    if (body) body.textContent = entry.lyrics || '';
    if (src)  src.innerHTML = entry.geniusUrl
      ? '<a class="lyrics-genius-link" href="' + entry.geniusUrl + '" target="_blank" rel="noopener">View on Genius \u2197</a>'
      : '';
  }
}

function _openLyricsPanel() {
  const panel = document.getElementById('music-lyrics-panel');
  const btn   = document.getElementById('music-btn-lyrics');
  if (panel) { panel.classList.add('open'); panel.style.display = 'flex'; }
  if (btn)   btn.classList.add('active');
  _musicLyricsPanelOpen = true;
}

function _closeLyricsPanel() {
  const panel = document.getElementById('music-lyrics-panel');
  const btn   = document.getElementById('music-btn-lyrics');
  if (panel) { panel.classList.remove('open'); panel.style.display = 'none'; }
  if (btn)   { btn.classList.remove('active'); }
  _musicLyricsPanelOpen = false;
}

// Hides the panel visually but preserves the user's intent to keep it open.
// Used when the current track has no lyrics — next track with lyrics will reopen automatically.
function _closeLyricsPanelVisual() {
  const panel = document.getElementById('music-lyrics-panel');
  const btn   = document.getElementById('music-btn-lyrics');
  if (panel) { panel.classList.remove('open'); panel.style.display = 'none'; }
  if (btn)   { btn.classList.remove('active'); }
  // _musicLyricsPanelOpen intentionally NOT reset
}

function toggleMusicLyricsPanel() {
  if (_musicLyricsPanelOpen) {
    _closeLyricsPanel();
  } else {
    _musicLyricsPanelOpen = true;
    // Populate panel immediately if we already have data
    if (_musicLyricsCurrentPath) {
      const entry = _musicLyricsCache.get(_musicLyricsCurrentPath);
      const body  = document.getElementById('music-lyrics-body');
      const src   = document.getElementById('music-lyrics-source');
      if (!entry || entry === 'loading') {
        _openLyricsPanel();
        if (body) body.textContent = 'Loading lyrics\u2026';
        if (src)  src.innerHTML = '';
      } else if (!entry.found) {
        _closeLyricsPanelVisual();
      } else {
        _openLyricsPanel();
        if (body) body.textContent = entry.lyrics || '';
        if (src)  src.innerHTML = entry.geniusUrl
          ? '<a class="lyrics-genius-link" href="' + entry.geniusUrl + '" target="_blank" rel="noopener">View on Genius \u2197</a>'
          : '';
      }
    } else {
      _openLyricsPanel();
    }
  }
}

function _onMusicTrackChangedLyrics(path, artist, title) {
  _musicLyricsCurrentPath = path;
  const btn = document.getElementById('music-btn-lyrics');

  if (!path) {
    if (btn) { btn.style.display = 'none'; btn.disabled = false; btn.classList.remove('active', 'lyrics-not-found'); }
    _closeLyricsPanel();
    return;
  }

  // Show button immediately; keep active state if panel was open
  if (btn) {
    btn.style.display = '';
    btn.disabled = false;
    btn.classList.remove('lyrics-not-found');
    btn.classList.toggle('active', _musicLyricsPanelOpen);
  }

  const cached = _musicLyricsCache.get(path);
  if (cached && cached !== 'loading') {
    _applyMusicLyricsState(path);
  } else {
    if (btn) { btn.textContent = '\u23F3 Lyrics'; btn.disabled = true; }
    if (_musicLyricsPanelOpen) {
      const body = document.getElementById('music-lyrics-body');
      const src  = document.getElementById('music-lyrics-source');
      _openLyricsPanel();
      if (body) body.textContent = 'Loading lyrics\u2026';
      if (src)  src.innerHTML = '';
    }
    _fetchMusicLyrics(path, artist, title);
  }
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
  _renderMusicQueuePeek();
}
function _musicNextTrack() {
  if (!musicTrackList.length && !(window._musicQueue && window._musicQueue.length)) return null;
  if (_musicRepeat === 'one' && musicTrackIndex >= 0) return musicTrackList[musicTrackIndex] || null;
  // Consume from the explicit queue first
  if (window._musicQueue && window._musicQueue.length) {
    const next = window._musicQueue.shift();
    // Refresh queue count badge
    if (currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
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
  if (currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
  _renderMusicQueuePeek();
}
function _musicQueueClear() {
  window._musicQueue = [];
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
      + `<button class="music-queue-clear-btn" data-action="clear-all" title="Clear queue">✕ Clear all</button>`
      + `</div>`;
    html += queueItems.map((item, i) => {
      const safeTitle = (item.name || item.path || '').replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;');
      const displayName = (item.name || item.path || '').replace(/&/g, '&amp;').replace(/</g, '&lt;');
      return `<div class="music-queue-peek-item music-queue-peek-queued" data-qi="${i}" title="${safeTitle}">`
        + `<span class="music-queue-peek-num">${i + 1}</span>`
        + `<span class="music-queue-peek-name">${displayName}</span>`
        + `<button class="music-queue-remove-btn" data-action="remove" data-qi="${i}" title="Remove from queue">✕</button>`
        + `</div>`;
    }).join('');
  }

  // --- Up Next section --- only shown when autoplay is on AND no explicit queue items
  if (upNextItems.length && _musicAutoPlay && !queueItems.length) {
    if (queueItems.length) html += `<div class="music-queue-section-header"><span>Up Next</span></div>`;
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

  // Delegated event handling — no inline onclick
  list.onclick = (e) => {
    const btn = e.target.closest('[data-action]');
    if (btn) {
      e.stopPropagation();
      const action = btn.dataset.action;
      if (action === 'clear-all') { _musicQueueClear(); return; }
      if (action === 'remove') { _musicQueueRemove(parseInt(btn.dataset.qi, 10)); return; }
    }
    // Click on queued row — discard everything before it, play it
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
    // with no active playback – sync local state and do NOT auto-advance.
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

    // Surface server-side music playback errors (skip in local mode — local errors are handled by app-local.js)
    const musicErr = (s.lastError || s.LastError || '').trim();
    if (musicErr && !isPlayLocal() && typeof showPlaybackErrorPopup === 'function') {
      const trackName = s.title || s.Title || (musicCurrentPath ? musicCurrentPath.replace(/.*[/\\]/, '') : '');
      const label = trackName ? `"${trackName}"` : 'Track';
      showPlaybackErrorPopup('Music', `${label} could not play.\n\n${musicErr}`);
    }

    // auto-advance when track ends naturally and the server didn't already handle it
    // (server handles it when _nextTrackPath was set; this covers the case where
    //  the browser is still open but autoplay wasn't yet registered)
    // Skip in local mode — the ended event on the <audio> element drives advancement there.
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

function switchMode(mode, skipInitialBrowse = false) {
  // Stop whichever player is active before switching away from it.
  const leavingVideo = currentMode === 'video' && mode !== 'video';
  const leavingMusic = currentMode === 'music' && mode !== 'music';
  const leavingRadio = currentMode === 'radio' && mode !== 'radio';
  if (leavingVideo && playingPath && typeof stop !== 'undefined') stop();
  if (leavingMusic && musicIsPlaying && typeof musicStop !== 'undefined') musicStop();
  if (leavingRadio && typeof _radioIsPlaying !== 'undefined' && _radioIsPlaying && typeof radioStop !== 'undefined') radioStop();
  if (typeof _hideSearchFilterBar === 'function') _hideSearchFilterBar();

  currentMode = mode;
  localStorage.setItem('remotePlayMode', mode);
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
  const vClear = document.getElementById('video-recent-clear');
  const vPinned = document.getElementById('video-pinned-strip');
  const mStrip = document.getElementById('music-recent-strip');
  const mClear = document.getElementById('music-recent-clear');
  const mPinned = document.getElementById('music-pinned-strip');
  const rStrip = document.getElementById('radio-recent-strip');
  if (navRow) navRow.style.display = '';
  if (vStrip) vStrip.style.display = mode === 'video' ? '' : 'none';
  if (vClear) vClear.style.display = mode === 'video' && vStrip && vStrip.children.length ? '' : 'none';
  if (vPinned) vPinned.style.display = mode === 'video' ? '' : 'none';
  if (mStrip) mStrip.style.display = mode === 'music' ? '' : 'none';
  if (mClear) mClear.style.display = mode === 'music' && mStrip && mStrip.children.length ? '' : 'none';
  if (mPinned) mPinned.style.display = mode === 'music' ? '' : 'none';
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
    else if (skipInitialBrowse) setMusicHeaderForMode('video');
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
    if (typeof renderMusicPinnedStrip === 'function') renderMusicPinnedStrip();
    if (typeof _updateMusicPinButton === 'function') _updateMusicPinButton();
    if (!currentMusicData && !skipInitialBrowse) browseMusic(null);
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
        if (searching) {
          browseMusic(currentMusicFolder);
          return;
        }
        const prev = musicBrowseHistory.length > 0 ? musicBrowseHistory.pop() : null;
        // Navigate directly without re-pushing to history
        currentMusicFolder = prev;
        const mb = document.getElementById('music-browser');
        if (mb)
          mb.innerHTML =
            '<div style="padding:.75rem;color:var(--muted,#9aa8c2)">Loading\u2026</div>';
        const url = '/api/music/browse' + (prev ? '?folder=' + encodeURIComponent(prev) : '');
        fetch(url)
          .then((r) => (r.ok ? r.json() : null))
          .then((data) => {
            if (!data) return;
            currentMusicData = data;
            currentMusicData.folder = prev;
            if (currentMode === 'music') renderMusicHeader(currentMusicData, false);
            renderMusicCards(currentMusicData);
          })
          .catch(() => {});
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
        // Only re-browse on scan complete if we're at root, or the current folder
        // has no tracks yet (avoid wiping an already-populated folder view).
        const currentHasTracks = currentMusicData && currentMusicData.files && currentMusicData.files.length > 0;
        if (!currentMusicFolder || !currentHasTracks) browseMusic(currentMusicFolder);
        else renderMusicHeader(currentMusicData, false); // just refresh the header badge
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
        // Re-browse every ~10s during scan so tracks appear progressively,
        // but only if the current folder view has no tracks yet (avoid wiping
        // a folder that was already populated by the instant scan).
        pollCount++;
        const currentHasTracks = currentMusicData && currentMusicData.files && currentMusicData.files.length > 0;
        if (pollCount % 5 === 0 && !currentHasTracks) browseMusic(currentMusicFolder, 0, false);
      }
    } catch (e) {}
  }, 2000);
}

function stopMusicStatusPoll() {
  if (musicStatusPollTimer) {
    clearInterval(musicStatusPollTimer);
    musicStatusPollTimer = null;
  }
  _stopMusicFolderFastPoll();
}

// ── Music keyboard navigation ──────────────────────────────────────────────
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

// ── Music context menu — delegates to universal #ctx-menu ──────────────────
let _musicCtxLongPressTimer = null;

function _musicShowContextMenu(e, path, name) {
  if (typeof _ctxShow === 'function') {
    const isQueued = window._musicQueue ? window._musicQueue.some((q) => q.path === path) : false;
    _ctxShow(e, 'music-file', { path, name, queued: isQueued });
  }
}

function _musicDismissContextMenu() {
  // no-op: universal menu handles its own dismissal
}

function _musicCtxPlay(path, name) { playMusic(path, name); }

function _musicCtxQueue(path, name) {
  if (!window._musicQueue) window._musicQueue = [];
  const existing = window._musicQueue.findIndex((q) => q.path === path);
  if (existing >= 0) window._musicQueue.splice(existing, 1);
  else window._musicQueue.push({ path, name });
  if (currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
  if (typeof _refreshMusicNavLabels === 'function') _refreshMusicNavLabels();
  _renderMusicQueuePeek();
  if (typeof _updateMusicQueuePendingBar === 'function') _updateMusicQueuePendingBar();
}

function _musicCtxCopy(path) {
  try { navigator.clipboard.writeText(path); } catch (_) {}
}

// ── Multi-select ────────────────────────────────────────────────────────
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
  _renderMusicQueuePeek();
}
function _musicSelectionPlay() {
  if (!_musicSelected.size) return;
  if (currentMusicData && currentMusicData.files) {
    const sorted = _musicSortFiles(currentMusicData.files);
    const selected = sorted.filter((f) => _musicSelected.has(f.path));
    if (!selected.length) return;
    // Play the first selected track; queue the rest
    const [first, ...rest] = selected;
    window._musicQueue = rest.map((f) => ({ path: f.path, name: f.name }));
    playMusic(first.path, first.name);
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
    bar.textContent = `\u23F3 ${count} track${count !== 1 ? 's' : ''} queued — start a song to begin playback`;
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

let _musicFolderFastPollTimer = null;

function _startMusicFolderFastPoll(folder) {
  _stopMusicFolderFastPoll();
  _musicFolderFastPollTimer = setInterval(async () => {
    if (currentMode !== 'music' || currentMusicFolder !== folder) {
      _stopMusicFolderFastPoll();
      return;
    }
    try {
      const url = '/api/music/browse' + (folder ? '?folder=' + encodeURIComponent(folder) : '');
      const res = await fetch(url);
      if (!res.ok) return;
      const data = await res.json();
      // Stop fast-polling once tracks are available or indexing has finished
      if ((data.files && data.files.length > 0) || !data.indexing) {
        _stopMusicFolderFastPoll();
        currentMusicData = data;
        currentMusicData.folder = folder;
        if (currentMode === 'music') renderMusicHeader(currentMusicData, false);
        renderMusicCards(currentMusicData);
      }
    } catch (e) {}
  }, 500);
}

function _stopMusicFolderFastPoll() {
  if (_musicFolderFastPollTimer) {
    clearInterval(_musicFolderFastPollTimer);
    _musicFolderFastPollTimer = null;
  }
}

async function browseMusic(folder, offset = 0, append = false) {
  if (!append) {
    _musicSelectionClear();
    _stopMusicFolderFastPoll();
    // Hide search filter bar whenever we navigate away from search results
    if (typeof _hideSearchFilterBar === 'function') _hideSearchFilterBar();
    // Push current folder to history before navigating
    if (currentMusicFolder !== folder) {
      if (currentMusicFolder !== null) musicBrowseHistory.push(currentMusicFolder);
      // Going to root resets history
      if (folder === null || folder === undefined) musicBrowseHistory = [];
    }
    currentMusicFolder = folder;
  }
  const mb = document.getElementById('music-browser');
  if (!mb) return;
  if (!append)
    mb.innerHTML = '<div style="padding:.75rem;color:var(--muted,#9aa8c2)">Loading…</div>';
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
    // If we just navigated into a folder and it's still indexing (no tracks yet),
    // start a fast re-poll so results appear as soon as the priority scan finishes.
    if (folder && !append && data.indexing && !(data.files && data.files.length > 0) && !(data.folders && data.folders.length > 0)) {
      _startMusicFolderFastPoll(folder);
    } else {
      _stopMusicFolderFastPoll();
    }
    // Always refresh the recently played strip on non-append navigations
    if (!append) {
      const mStrip = document.getElementById('music-recent-strip');
      if (mStrip) mStrip.style.display = '';
      loadMusicRecent().then((files) => renderMusicRecent(files));
      if (typeof renderMusicPinnedStrip === 'function') renderMusicPinnedStrip();
      if (typeof _updateMusicPinButton === 'function') _updateMusicPinButton();
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
    if (typeof _lastSearchQuery !== 'undefined') _lastSearchQuery = q;
    if (typeof _showSearchFilterBar === 'function') _showSearchFilterBar(true);
    if (typeof _applySearchFilter === 'function') _applySearchFilter();
  } catch (e) {
    setSearchBusy(false);
  }
}

// ── Music card metadata helpers ────────────────────────────────────────────

/**
 * Strip a leading track-number prefix from a filename (no extension).
 *   "01 - Song Title" → "Song Title"
 *   "01. Song Title"  → "Song Title"
 *   "Song Title"      → "Song Title"
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

// ── Music list sort state ──────────────────────────────────────────────────
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
        '<div class="folder-row" role="button" tabindex="0" onkeydown="activateKeyboardClick(event,this)"' +
        ' oncontextmenu="_ctxShow(event,\'music-folder\',{dir:btoa(unescape(encodeURIComponent(\'' + jsStr(f.folder) + '\'))),name:\'' + jsStr(f.name) + '\'})"' +
        ' onclick="browseMusic(\'' +
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

    // ── Sync command bar ──────────────────────────────────────────────────
    const playAllVisible = !!data.folder;
    _syncMusicCommandBar(playAllVisible);

    if (isList) {
      const _si = (col) => _musicSort.col === col ? (_musicSort.dir === 'asc' ? ' ▲' : ' ▼') : '';
      html += '<div class="music-list-header">'
        + '<span title="Art / Track #">#</span>'
        + `<span class="sortable" onclick="musicSortBy('name')" title="Sort by title">Title${_si('name')}</span>`
        + `<span class="sortable" onclick="musicSortBy('ext')" title="Sort by format">Fmt${_si('ext')}</span>`
        + `<span class="sortable" onclick="musicSortBy('artist')" title="Sort by artist">Artist${_si('artist')}</span>`
        + `<span class="sortable" onclick="musicSortBy('album')" title="Sort by album">Album${_si('album')}</span>`
        + `<span class="sortable mtc-dur-hdr" onclick="musicSortBy('durationSec')" title="Sort by duration">Time${_si('durationSec')}</span>`
        + '<span></span>'
        + '</div>';
    }
    html += '<div class="music-grid' + (isList ? ' list-view' : '') + '">';
    sortedFiles.forEach((f, idx) => {
      const isPlaying  = musicCurrentPath && f.path === musicCurrentPath;
      const isSelected = _musicSelected.has(f.path);
      const isQueued   = window._musicQueue && window._musicQueue.some(q => q.path === f.path);
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

      // Merged genre · year single line
      const genreYear = [genre, year].filter(Boolean).join(' · ');

      let card = `<div class="music-track-card${isPlaying ? ' playing' : ''}${isSelected ? ' selected' : ''}${isQueued ? ' queued' : ''}${isList && idx % 2 === 1 ? ' alt-row' : ''}" data-path="${esc(f.path)}" data-idx="${idx}" data-sr-title="${esc(title)}" data-sr-artist="${esc(artist)}" data-sr-album="${esc(album)}" title="${esc(title)}">`;

      // ── Checkbox (for multi-select) ────────────────────────────────────
      card += `<input type="checkbox" class="mtc-checkbox" data-path="${esc(f.path)}" data-idx="${idx}"${isSelected ? ' checked' : ''} tabindex="-1" aria-label="Select ${esc(title)}" />`;

      // ── Col 1: thumbnail / playing bars / music-note placeholder / track number
      // Always render both cover/placeholder AND bars so toggling .playing on the card
      // is enough — no stale HTML left on the wrong row when the track changes.
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

      // ── Col 2: title ───────────────────────────────────────────────────
      card += `<span class="mtc-title">${esc(title)}${isList && isQueued ? ' <span class="mtc-queued-badge">Q</span>' : ''}</span>`;

      // ── Col 3: format badge + duration (together in top-right) ─────────
      const durHtml = duration ? `<span class="mtc-badge-dur">${esc(duration)}</span>` : '';
      card += `<span class="mtc-badge-group">${badge ? `<span class="mtc-badge">${esc(badge)}</span>` : ''} ${durHtml}</span>`;

      // ── Col 4: artist ─────────────────────────────────────────────────
      card += `<span class="mtc-artist">${esc(artist)}</span>`;

      // ── Col 5: album ──────────────────────────────────────────────────
      card += `<span class="mtc-album">${esc(album)}</span>`;

      // ── Col 6 (list only): duration ───────────────────────────────────
      if (isList) {
        card += `<span class="mtc-duration">${esc(duration)}</span>`;
      }

      // ── Grid-only: merged genre · year line ───────────────────────────
      if (!isList && genreYear) {
        card += `<div class="mtc-genre-year card-only">${esc(genreYear)}</div>`;
      }

      card += '</div>';
      html += card;
    });
    html += '</div>';
    if (data.hasMore) {
      const shown = data.files.length;
      const total = data.totalInFolder || shown;
      html +=
        '<button class="load-more-btn" onclick="loadMoreMusic()">Load more tracks (' +
        shown + ' of ' + total + ' loaded)</button>';
    }
  } else if (data.folder && data.indexing && !(data.folders && data.folders.length > 0)) {
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
      // Card click: if checkbox clicked → toggle selection; otherwise play
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

// kept for compatibility — routes to the card renderer
function renderMusic(data, searching) {
  renderMusicCards(data, searching);
}

function playMusicAll() {
  if (!currentMusicData || !currentMusicData.files || !currentMusicData.files.length) return;
  const sorted = _musicSortFiles(currentMusicData.files);
  if (!sorted.length) return;
  const first = sorted[0];
  playMusic(first.path, first.name);
  // queue remaining — stored for auto-advance
  window._musicQueue = sorted.slice(1);
  // refresh to show queue count badge
  if (currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
}

async function searchLibrary(q, offset = 0, append = false) {
  setStatus('Searching library...');
  if (!append) clearPendingThumbnails();
  pendingSearchAbort = new AbortController();
  try {
    const res = await fetch(
      '/api/search?q=' +
        encodeURIComponent(q) +
        (offset ? '&offset=' + encodeURIComponent(offset) : ''),
      { signal: pendingSearchAbort.signal }
    );
    if (!res.ok) {
      setSearchBusy(false);
      setStatus('Search error ' + res.status);
      return;
    }
    const data = await res.json();
    setSearchBusy(false);
    const rendered = {
      folders: data.folders || [],
      files: data.files,
      current: 'Search',
      currentFull: 'Search',
      parent: null,
      isRoot: false,
      totalFiles: data.totalFiles,
      offset: data.offset,
      limit: data.limit,
      hasMoreFiles: data.hasMoreFiles,
      query: q,
    };
    if (append && currentData) {
      rendered.files = [...(currentData.files || []), ...(data.files || [])];
    }
    currentData = rendered;
    render(rendered, true);
    updateLibraryStatus({
      isScanning: data.indexing,
      indexedFiles: data.indexedFiles,
      scannedFiles: data.indexedFiles,
      completedUtc: data.lastRefreshUtc,
      lastError: '',
    });
    const note = data.indexing ? ' (indexing...)' : '';
    setStatus(
      (data.folders?.length || 0) +
        ' folder(s) and ' +
        data.files.length +
        ' result(s) from ' +
        data.indexedFiles +
        ' indexed video(s)' +
        note
    );
    pendingSearchAbort = null;
  } catch (e) {
    setSearchBusy(false);
    if (e.name !== 'AbortError') setStatus('Search failed: ' + e);
    pendingSearchAbort = null;
  }
}
// ── Video command-bar filter / sort state ────────────────────────────
let _videoFilter = 'all'; // 'all' | 'unwatched' | 'watched' | 'fav' | 'progress'
let _videoSort   = 'name'; // 'name' | 'folder' | 'duration' | 'size' | 'progress'
let _currentRawFiles = []; // last unfiltered file list from server

function setVideoFilter(f) {
  _videoFilter = f;
  document.querySelectorAll('#vcb-filters .vcb-pill').forEach(b => b.classList.toggle('active', b.dataset.vf === f));
  _rerenderVideoFiles();
}
function setVideoSort(s) {
  _videoSort = s;
  const sel = document.getElementById('vcb-sort');
  if (sel && sel.value !== s) sel.value = s;
  _rerenderVideoFiles();
}
function _applyVideoCommandBar(show) {
  const bar = document.getElementById('video-command-bar');
  const cl  = document.getElementById('count-line');
  const vtv = document.getElementById('view-toggle-video');
  // Bar lives inside the header now; show whenever we are in video mode
  if (bar) bar.style.display = show ? 'flex' : 'none';
  if (cl)  { cl.style.display = show ? '' : ''; cl.classList.toggle('vcb-active', show); }
  if (vtv) vtv.style.display = 'none';
  if (show) {
    const sel = document.getElementById('vcb-sort');
    if (sel) sel.value = _videoSort;
    document.querySelectorAll('#vcb-filters .vcb-pill').forEach(b => b.classList.toggle('active', b.dataset.vf === _videoFilter));
    _applyVcbViewBtn();
  }
}
/** Show or hide a command-bar right section (sort + count) based on whether files are present. */
function _setCbRightVisible(id, visible) {
  const el = document.getElementById(id);
  if (el) el.style.display = visible ? '' : 'none';
}
function _applyVcbViewBtn() {
  const btn = document.getElementById('vcb-view-btn');
  if (!btn) return;
  const isList = _viewMode.video === 'list';
  btn.textContent = isList ? '⊞ Grid' : '☰ List';
  btn.title = isList ? 'Switch to grid view' : 'Switch to list view';
  // Re-sync sort-block visibility based on new view mode
  if (currentData) _setCbRightVisible('vcb-right', (currentData.files?.length || 0) > 0 && !isList);
}
function _sortedFiles(files) {
  const arr = files.slice();
  switch (_videoSort) {
    case 'folder':   arr.sort((a,b) => (a.folder||'').localeCompare(b.folder||'') || (a.displayName||a.name||'').localeCompare(b.displayName||b.name||'')); break;
    case 'duration': arr.sort((a,b) => (Number(b.duration)||0) - (Number(a.duration)||0)); break;
    case 'year':     arr.sort((a,b) => { const ya = _parseYear(a.name||''), yb = _parseYear(b.name||''); return (yb||'0').localeCompare(ya||'0') || (a.displayName||a.name||'').localeCompare(b.displayName||b.name||''); }); break;
    case 'progress': arr.sort((a,b) => (Number(b.progress)||0) - (Number(a.progress)||0)); break;
    default:         arr.sort((a,b) => (a.displayName||a.name||'').localeCompare(b.displayName||b.name||'')); break;
  }
  return arr;
}
function _filteredFiles(files) {
  switch (_videoFilter) {
    case 'unwatched': return files.filter(f => !playedVideos.has(f.path) && (Number(f.progress)||0) < 0.95);
    case 'watched':   return files.filter(f => playedVideos.has(f.path) || (Number(f.progress)||0) >= 0.95);
    case 'fav':       return files.filter(f => favoriteVideos.has(f.path) || Boolean(f.favorite));
    case 'progress':  return files.filter(f => { const p = Number(f.progress)||0; return p > 0.02 && p < 0.95; });
    default:          return files;
  }
}
function _rerenderVideoFiles() {
  if (!_currentRawFiles.length) return;
  const filtered = _filteredFiles(_currentRawFiles);
  const sorted   = _sortedFiles(filtered);
  const cnt = document.getElementById('vcb-count');
  if (cnt) cnt.textContent = sorted.length + ' / ' + _currentRawFiles.length + ' video(s)';
  _renderVideoFileRows(sorted);
}
function _formatDuration(secs) {
  if (!secs) return '';
  const s = Math.round(secs);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  return h > 0
    ? h + ':' + String(m).padStart(2,'0') + ':' + String(sec).padStart(2,'0')
    : m + ':' + String(sec).padStart(2,'0');
}
function _formatSize(bytes) {
  if (!bytes) return '';
  if (bytes >= 1073741824) return (bytes / 1073741824).toFixed(1) + ' GB';
  if (bytes >= 1048576)    return (bytes / 1048576).toFixed(0) + ' MB';
  return (bytes / 1024).toFixed(0) + ' KB';
}

/** Extract a 4-digit year (1900-2099) from a filename, checking the most common
 *  tagging conventions: (YYYY), [YYYY], .YYYY., or a bare 4-digit token. */
function _parseYear(name) {
  const m = name.match(/[(\[]((?:19|20)\d{2})[)\]]/) ||
            name.match(/\.((19|20)\d{2})\./) ||
            name.match(/\b((?:19|20)\d{2})\b/);
  return m ? m[1] : '';
}

function render(data, searching) {
  const bc = document.getElementById('breadcrumb');
  const countLine = document.getElementById('count-line');
  const back = document.getElementById('back-button');
  // Recent strip stays populated at all times — do not clear on navigation
  const canGoBack =
    !searching && (browseHistory.length > 0 || (data.parent != null && !data.isRoot));
  back.style.display = canGoBack ? 'block' : 'none';
  back.dataset.dir = !data.isRoot && data.parent != null && !searching ? data.parent : '';
  bc.innerHTML = '';
  if (searching) {
    bc.innerHTML +=
      '<a onclick="browse(null)">&#8962; Root</a><span> &rsaquo; </span><span class="crumb-current">Search results</span>';
  } else if (data.breadcrumbs && data.breadcrumbs.length) {
    bc.innerHTML += data.breadcrumbs
      .map((c, i) => {
        const sep = i > 0 ? '<span> &rsaquo; </span>' : '';
        const label = i === 0 ? '&#8962; Root' : esc(c.name);
        const isLast = i === data.breadcrumbs.length - 1;
        const cls = isLast ? ' class="crumb-current"' : '';
        const linkBadge =
          isLast && currentIsLinkedDir
            ? ' <span class="folder-link-badge" title="Navigated via library link">\uD83D\uDD17</span>'
            : '';
        return (
          sep +
          '<a' +
          cls +
          ' onclick="browse(\'' +
          c.dir +
          '\')">' +
          (isLast ? label + linkBadge : label) +
          '</a>'
        );
      })
      .join('');
  }
  if (searching) {
    const txt = document.getElementById('count-text');
    if (txt)
      txt.textContent =
        (data.folders?.length || 0) + ' folder(s) and ' + data.files.length + ' result(s)';
  } else {
    const txt = document.getElementById('count-text');
    if (txt)
      txt.textContent = data.folders.length + ' folder(s), ' + data.files.length + ' video(s)';
  }
  document.getElementById('view-toggle-music') &&
    (document.getElementById('view-toggle-music').style.display = 'none');
  // Show video command bar only when still in video mode — avoids overriding tab state
  // when an async browse response arrives after the user has switched to music/radio.
  if (currentMode === 'video') _applyVideoCommandBar(true);
  _applyViewToggleBtn('video');
  let html = '';
  if (data.folders.length) {
    if (searching) {
      const folderLabel =
        data.folders.length > 5
          ? 'Folders (' + data.folders.length + ', scroll for more)'
          : 'Folders';
      html += '<div class="section-label">' + folderLabel + '</div>';
      html += '<div class="folder-list search-folders">';
      html += data.folders
        .map(
          (f) =>
            '<div class="folder-row" role="button" tabindex="0" onkeydown="activateKeyboardClick(event,this)" oncontextmenu="_showFolderCtxMenu(event,null,\'' + esc(f.folder || f.name) + '\',true)" onclick="this.classList.add(\'folder-row-active\');searchLibrary(\'' +
            esc(f.folder || f.name) +
            '\')">' +
            '<span class="folder-icon">&#128193;</span><span class="folder-name">' +
            esc(f.name || f.folder) +
            '</span></div>'
        )
        .join('');
      html += '</div>';
    } else {
      html += '<div class="folder-list">';
      html += data.folders
        .map(
          (f) =>
            '<div class="folder-row" role="button" tabindex="0" onkeydown="activateKeyboardClick(event,this)" oncontextmenu="_showFolderCtxMenu(event,\'' + f.dir + '\',\'' + esc(f.name) + '\',false)" onclick="this.classList.add(\'folder-row-active\');browse(\'' +
            f.dir +
            "',0,false,true," +
            (f.isLink ? 'true' : 'false') +
            ')">' +
            '<span class="folder-icon">&#128193;</span><span class="folder-name">' +
            esc(f.name) +
            '</span>' +
            (f.isLink ? '<span class="folder-link-badge" title="Library link">🔗</span>' : '') +
            '</div>'
        )
        .join('');
      html += '</div>';
    }
  }
  _setCbRightVisible('vcb-right', data.files.length > 0 && _viewMode.video !== 'list');
  if (data.files.length) {
    // Store raw files for filter/sort; render via helper
    _currentRawFiles = data.files;
    const filtered = _filteredFiles(data.files);
    const sorted   = _sortedFiles(filtered);
    const cnt = document.getElementById('vcb-count');
    if (cnt) cnt.textContent = sorted.length + ' / ' + data.files.length + ' video(s)';
    html += '<div class="section-label" style="display:none">Videos</div>';
    html += '<div id="video-list-grid" class="movie-grid' + (_viewMode.video === 'list' ? ' list-view' : '') + '"></div>';
    // We'll populate the grid after inserting the HTML skeleton
  }
  if (!data.folders.length && !data.files.length)
    html = '<div id="empty">No subfolders or video files here.</div>';
  document.getElementById('browser').innerHTML = html;
  if (data.files.length) {
    const sorted2 = _sortedFiles(_filteredFiles(data.files));
    _renderVideoFileRows(sorted2);
  }
  if (data.hasMoreFiles) {
    const shown = (data.files || []).length;
    document
      .getElementById('browser')
      .insertAdjacentHTML(
        'beforeend',
        '<button class="load-more-btn" onclick="loadMoreFiles()">Load more videos (' +
          shown +
          ' of ' +
          (data.totalFiles || shown) +
          ')</button>'
      );
  }
  observeMovieCards();
  return; // early return — rest of old render body is replaced
}

function _renderVideoFileRows(files) {
  const grid = document.getElementById('video-list-grid');
  if (!grid) return;
  // Clear any stale selection when the file list is replaced
  _videoSelectionClear();
  const isList = _viewMode.video === 'list';
  grid.className = 'movie-grid' + (isList ? ' list-view' : '');

  const HEADER = isList
    ? '<div class="vlc-header-row">' +
        '<div class="vlc-h vlc-h-title sortable" onclick="setVideoSort(\'name\')" title="Sort by title">Title</div>' +
        '<div class="vlc-h vlc-h-progress sortable" onclick="setVideoSort(\'progress\')" title="Sort by progress">Progress</div>' +
        '<div class="vlc-h vlc-h-year sortable" onclick="setVideoSort(\'year\')" title="Sort by year">Year</div>' +
        '<div class="vlc-h vlc-h-ext">Format</div>' +
        '<div class="vlc-h vlc-h-duration sortable" onclick="setVideoSort(\'duration\')" title="Sort by duration">Duration</div>' +
        '<div class="vlc-h vlc-h-cb"></div>' +
      '</div>'
    : '';

  let html = HEADER;
  html += files
    .map((f, rowIndex) => {
      if (f.watched && !playedVideos.has(f.path)) {
        playedVideos.add(f.path);
        savePlayedVideos();
      }
      const played    = playedVideos.has(f.path);
      const queued    = queuedVideos.has(f.path);
      if (f.favorite) favoriteVideos.add(f.path);
      const favorite  = favoriteVideos.has(f.path) || Boolean(f.favorite);
      const thumbUrl  = '/api/thumb?path=' + encodeURIComponent(f.path);
      const altRow    = rowIndex % 2 === 1 ? ' alt-row' : '';
      const isPlaying = f.path === playingPath;
      const displayName = f.displayName || f.name;
      const progress  = Number(f.progress) || 0;
      const progressPct = Math.max(0, Math.min(99, Math.round(progress * 100)));
      const cardClass = 'movie-card ' +
        (isPlaying ? 'playing ' : '') +
        (queued    ? 'queued '  : '') +
        (played    ? 'played '  : '') +
        (favorite  ? 'favorite ': '') +
        altRow;
      const action = isPlaying ? '' : ' onclick="onCardClick(event,\'' + f.path + '\')"';
      // ── column: watched pip ────────────────────────────────────────────────
      // watched state is indicated by the left border colour on the row (class 'played')
      // ── column: title (with inline actions on right) ──────────────────────
      const linkBadgeInline = f.isLink ? ' <span class="folder-link-badge" title="Library link">🔗</span>' : '';
      const inlineActions = isPlaying
        ? '<div class="vlc-inline-actions"><button class="stop-btn" onclick="stopPlayingCard(event,\'' + f.path + '\')">&#9632; Stop</button></div>'
        : '<div class="vlc-inline-actions">' +
            '<button class="primary-action" onclick="playCardAction(event,\'' + f.path + "','" + esc(f.name) + '\')">&#9654; Play</button>' +
            '<button class="muted-action"   onclick="queueCardAction(event,\'' + f.path + '\')">' + (queued ? '&#8722; Unqueue' : '+ Queue') + '</button>' +
            '<button class="favorite-action" onclick="toggleFavoriteCard(event,\'' + f.path + '\')">' + (favorite ? '&#9733; Unfav' : '&#9734; Fav') + '</button>' +
            '<button class="muted-action"   onclick="toggleWatchedCard(event,\'' + f.path + '\')">' + (played ? '&#128064; Unwatch' : '&#10003; Watched') + '</button>' +
          '</div>';
      const titleCol = '<div class="vlc-title"><span class="vlc-title-text">' + esc(displayName) + linkBadgeInline + '</span>' + (queued ? '<span class="vlc-queued-badge">Q</span>' : '') + inlineActions + '</div>';
      // ── column: folder ───────────────────────────────────────────────────
      const folderCol = '<div class="vlc-folder">' + esc(f.folder || '') + '</div>';
      // ── column: year (parsed from filename) ──────────────────────────────
      const year = _parseYear(f.name || '');
      const yearCol = '<div class="vlc-year">' + (year || '') + '</div>';
      // ── column: multi-select checkbox (list mode only) ──────────────────
      const cbCol = '<div class="vlc-cb"><input type="checkbox" class="vlc-select-cb" data-path="' + esc(f.path) + '" onclick="event.stopPropagation()" title="Select" aria-label="Select ' + esc(displayName) + '"/></div>';
      // ── column: format (file extension badge) ────────────────────────────
      const extText = f.ext || '';
      const extCol = '<div class="vlc-ext">' + (extText ? '<span class="vlc-ext-badge">' + esc(extText) + '</span>' : '') + '</div>';
      // ── column: duration ─────────────────────────────────────────────────
      const durCol = '<div class="vlc-duration">' + _formatDuration(Number(f.duration) || 0) + '</div>';
      // ── column: size ─────────────────────────────────────────────────────
      const sizeCol = '<div class="vlc-size">' + _formatSize(Number(f.sizeBytes) || 0) + '</div>';
      // ── column: progress (percentage text only) ───────────────────────────
      const progCol = '<div class="vlc-progress">' + (progressPct > 0 ? progressPct + '%' : '') + '</div>';
      // ── (actions now inline in titleCol — no separate actionsCol) ─────────
      const actionsCol = '';
      // ── grid-mode card overlay actions (hidden in list mode) ─────────────
      const cardActionsGrid = isPlaying ? '' :
        '<div class="card-actions">' +
          '<button class="primary-action" onclick="playCardAction(event,\'' + f.path + "','" + esc(f.name) + '\')">Play</button>' +
          '<button class="muted-action"   onclick="queueCardAction(event,\'' + f.path + '\')">' + (queued ? 'Unqueue' : 'Queue') + '</button>' +
          '<button class="favorite-action" onclick="toggleFavoriteCard(event,\'' + f.path + '\')">'
            + (favorite ? '&#10084;&#xFE0E; Unfavorite' : '&#9825; Favorite') + '</button>' +
          '<button class="muted-action"   onclick="toggleWatchedCard(event,\'' + f.path + '\')">' + (played ? 'Unwatch' : 'Watched') + '</button>' +
        '</div>';
      const progressOverlay = progressPct > 0
        ? '<div class="progress-overlay"><div class="progress-fill" style="width:' + progressPct + '%"></div></div>'
        : '';
      const gridBadges = (queued  ? '<div class="queue-badge queued-badge">Queued</div>'   : '') +
                         (favorite ? '<div class="favorite-badge queued-badge">Favorite</div>' : '');

      return '<div class="' + cardClass +
        '" id="' + cardIdFor(f.path) +
        '" data-path="' + esc(f.path) +
        '" role="button" tabindex="0" aria-label="Play ' + esc(displayName) +
        '" data-thumb="' + esc(thumbUrl) + '"' +
        action +
        ' onkeydown="activateKeyboardClick(event,this)"' +
        ' oncontextmenu="_ctxShow(event,\'video-file\',{path:\'' + esc(f.path) + '\',name:\'' + esc(displayName) + '\',played:' + played + ',queued:' + queued + ',favorite:' + favorite + '})"' +
        ' onpointerdown="beginCardHold(event,\'' + f.path + '\')"' +
        ' onpointerup="endCardHold()" onpointercancel="endCardHold()" onpointerleave="endCardHold()">' +
        // List-mode columns (CSS grid)
        titleCol + progCol + yearCol + extCol + durCol + actionsCol + cbCol +
        // Grid-mode inner card (hidden in list mode)
        '<div class="movie-card-inner">' +
          '<div class="movie-title">' + esc(displayName) + '</div>' +
          gridBadges +
          cardActionsGrid +
        '</div>' +
        progressOverlay +
        '</div>';
    })
    .join('');
  grid.innerHTML = html;
  // Attach checkbox change handlers for multi-select
  grid.querySelectorAll('.vlc-select-cb').forEach(cb => {
    cb.addEventListener('change', (e) => {
      e.stopPropagation();
      const path = cb.dataset.path;
      if (path) _videoSelectionToggle(path);
    });
    // Prevent the click from bubbling to the card row
    cb.addEventListener('click', e => e.stopPropagation());
  });
  observeMovieCards();
  _scheduleDurationPatch(files);
}
// Waits a short moment for the server's background probes to complete, then
// fetches any durations that were missing on initial render and patches them in.
let _durationPatchTimer = null;
function _scheduleDurationPatch(files) {
  if (_durationPatchTimer) clearTimeout(_durationPatchTimer);
  const missing = files.filter(f => !(Number(f.duration) > 0)).map(f => f.path);
  if (!missing.length) return;
  _durationPatchTimer = setTimeout(async () => {
    _durationPatchTimer = null;
    try {
      const res = await fetch('/api/durations', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(missing)
      });
      if (!res.ok) return;
      const map = await res.json(); // { encodedPath: durationSeconds, ... }
      for (const [encodedPath, dur] of Object.entries(map)) {
        if (!(dur > 0)) continue;
        // Update the raw data so re-renders have the value
        const raw = _currentRawFiles.find(f => f.path === encodedPath);
        if (raw) raw.duration = dur;
        // Patch the live DOM cell
        const card = document.getElementById(cardIdFor(encodedPath));
        if (card) {
          const cell = card.querySelector('.vlc-duration');
          if (cell) cell.textContent = _formatDuration(dur);
        }
      }
    } catch (_) { /* ignore */ }
  }, 2500);
}
function loadMoreFiles() {
  if (!currentData) return;
  const nextOffset = (currentData.files || []).length;
  if (currentData.query) searchLibrary(currentData.query, nextOffset, true);
  else browse(currentDir, nextOffset, true);
}
async function loadMusicRecent() {
  try {
    const res = await fetch('/api/music/recent');
    if (!res.ok) return [];
    const data = await res.json();
    return Array.isArray(data.files) ? data.files : [];
  } catch (e) {
    return [];
  }
}
function renderMusicRecent(files) {
  const strip = document.getElementById('music-recent-strip');
  const clearBtn = document.getElementById('music-recent-clear');
  if (!strip) return;
  strip.innerHTML = '';
  if (!files.length) {
    strip.style.display = 'none';
    if (clearBtn) clearBtn.style.display = 'none';
    return;
  }
  strip.style.display = '';
  if (clearBtn) clearBtn.style.display = '';
  strip.innerHTML = files.slice(0, 7).map((f) => {
    const title   = f.tagTitle ? f.tagTitle : _titleFromName(f.name);
    const pct     = Math.max(0, Math.min(99, Math.round((Number(f.progress) || 0) * 100)));
    const isCont  = pct > 0 && pct < 95;
    const hasCover = !!f.hasCover;
    const thumb   = hasCover ? '/api/music/cover?path=' + encodeURIComponent(f.path) : '';
    return (
      '<div class="vr-card" role="button" tabindex="0"' +
      ' data-recent-music-path="' + esc(f.path) + '"' +
      ' data-recent-music-title="' + esc(title) + '"' +
      ' title="' + esc(title) + '"' +
      ' oncontextmenu="_ctxShow(event,\'music-recent\',{path:\'' + esc(f.path) + '\',name:\'' + esc(title) + '\'})"' +
      ' onkeydown="activateKeyboardClick(event,this)">' +
      (hasCover
        ? '<div class="vr-thumb" style="background-image:url(\'' + thumb + '\')"></div>'
        : '<div class="vr-thumb vr-thumb-placeholder">&#9835;</div>') +
      '<div class="vr-label">' + esc(title) + '</div>' +
      (isCont ? '<div class="vr-progress"><div class="vr-progress-fill" style="width:' + pct + '%"></div></div>' : '') +
      '</div>'
    );
  }).join('');
  // Wire clicks via delegation — navigate to folder then play
  if (strip._recentMusicHandler) strip.removeEventListener('click', strip._recentMusicHandler);
  strip._recentMusicHandler = async (e) => {
    const card = e.target.closest('[data-recent-music-path]');
    if (!card) return;
    const encodedPath = card.dataset.recentMusicPath;
    const title = card.dataset.recentMusicTitle;
    const path = decodeURIComponent(escape(atob(encodedPath)));
    const lastSlash = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
    const folder = lastSlash > 0 ? path.substring(0, lastSlash) : null;
    if (currentMode !== 'music') switchMode('music', true);
    await browseMusic(folder);
    await playMusic(encodedPath, title);
  };
  strip.addEventListener('click', strip._recentMusicHandler);
}
async function clearRecentMusic() {
  await fetch('/api/music/recent/clear', { method: 'POST' });
  renderMusicRecent([]);
}
async function clearRecentVideos() {
  await fetch('/api/recent/clear', { method: 'POST' });
  renderRecent([]);
}
function renderRecent(files) {
  const strip = document.getElementById('video-recent-strip');
  if (!strip) return;
  const clearBtn = document.getElementById('video-recent-clear');
  strip.innerHTML = '';
  if (!files.length) {
    strip.style.display = 'none';
    if (clearBtn) clearBtn.style.display = 'none';
    return;
  }
  strip.style.display = '';
  if (clearBtn) clearBtn.style.display = '';
  strip.innerHTML = files.slice(0, 5).map((f) => {
    const isCont  = Number(f.progress) > 0 && Number(f.progress) < 0.95;
    const pct     = Math.max(0, Math.min(99, Math.round((Number(f.progress) || 0) * 100)));
    const title   = f.displayName || f.name;
    const thumb   = '/api/thumb?path=' + encodeURIComponent(f.path);
    return (
      '<div class="vr-card" role="button" tabindex="0"' +
      ' data-recent-video-path="' + esc(f.path) + '"' +
      ' data-recent-video-title="' + esc(title) + '"' +
      ' title="' + esc(title) + '"' +
      ' oncontextmenu="_ctxShow(event,\'video-recent\',{path:\'' + esc(f.path) + '\',name:\'' + esc(title) + '\'})"' +
      ' onkeydown="activateKeyboardClick(event,this)">' +
      '<div class="vr-thumb" style="background-image:url(\'' + thumb + '\')"></div>' +
      '<div class="vr-label">' + esc(title) + '</div>' +
      (isCont ? '<div class="vr-progress"><div class="vr-progress-fill" style="width:' + pct + '%"></div></div>' : '') +
      '</div>'
    );
  }).join('');
  // Wire clicks via delegation — navigate to folder then play
  if (strip._recentVideoHandler) strip.removeEventListener('click', strip._recentVideoHandler);
  strip._recentVideoHandler = async (e) => {
    const card = e.target.closest('[data-recent-video-path]');
    if (!card) return;
    const encodedPath = card.dataset.recentVideoPath;
    const path = decodeURIComponent(escape(atob(encodedPath)));
    const lastSlash = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
    const rawFolder = lastSlash > 0 ? path.substring(0, lastSlash) : null;
    // browse() expects Base64-encoded path (server calls DecodePath on it)
    const folderArg = rawFolder ? btoa(unescape(encodeURIComponent(rawFolder))) : null;
    if (currentMode !== 'video') switchMode('video', true);
    await browse(folderArg);
    await play(encodedPath, card.dataset.recentVideoTitle);
  };
  strip.addEventListener('click', strip._recentVideoHandler);
}
function renderFavorites(files) {
  if (!files.length) return;
  const browser = document.getElementById('browser');
  const html =
    '<div class="section-label favorites-label">Favorites</div><div class="continue-grid recent-grid favorites-grid">' +
    files
      .slice(0, 8)
      .map((f) => renderProgressCard(f, false, true))
      .join('') +
    '</div>';
  browser.innerHTML = html + browser.innerHTML;
}
function renderProgressCard(f, isContinue, isFavoriteSection = false) {
  const thumbUrl = '/api/thumb?path=' + encodeURIComponent(f.path);
  const bg = 'data-thumb="' + esc(thumbUrl) + '"';
  const pct = Math.max(0, Math.min(99, Math.round((Number(f.progress) || 0) * 100)));
  const displayName = f.displayName || f.name;
  const queued = queuedVideos.has(f.path);
  const favorite = favoriteVideos.has(f.path) || Boolean(f.favorite);
  if (favorite) favoriteVideos.add(f.path);
  if (f.watched && !playedVideos.has(f.path)) {
    playedVideos.add(f.path);
    savePlayedVideos();
  }
  return (
    '<div class="movie-card ' +
    (isContinue ? 'continue-card ' : '') +
    (isFavoriteSection ? 'favorite-section-card ' : '') +
    (queued ? 'queued ' : '') +
    (favorite ? 'favorite ' : '') +
    'played" id="' +
    continueCardIdFor(f.path) +
    '" data-path="' +
    esc(f.path) +
    '" role="button" tabindex="0" aria-label="Play ' +
    esc(displayName) +
    '" ' +
    bg +
    ' onclick="onCardClick(event,\'' +
    f.path +
    '\')" onkeydown="activateKeyboardClick(event,this)" onpointerdown="beginCardHold(event,\'' +
    f.path +
    '\')" onpointerup="endCardHold()" onpointercancel="endCardHold()" onpointerleave="endCardHold()">' +
    '<div class="movie-card-inner"><div class="movie-title">' +
    esc(displayName) +
    '</div>' +
    (isContinue ? '<div class="continue-badge queued-badge">▶ Continue</div>' : '') +
    (queued ? '<div class="queue-badge queued-badge">Queued</div>' : '') +
    (favorite ? '<div class="favorite-badge queued-badge">Favorite</div>' : '') +
    '<div class="movie-meta">' +
    (pct > 0 ? pct + '% • ' : '') +
    esc(f.folder || '') +
    '</div>' +
    '<div class="card-actions"><button class="primary-action" onclick="playCardAction(event,\'' +
    f.path +
    "','" +
    esc(displayName) +
    '\')">Play</button><button class="muted-action" onclick="queueCardAction(event,\'' +
    f.path +
    '\')">' +
    (queued ? 'Unqueue' : 'Queue') +
    '</button><button class="favorite-action" onclick="toggleFavoriteCard(event,\'' +
    f.path +
    '\')">' +
    (favorite ? 'Unfavorite' : 'Favorite') +
    '</button><button class="muted-action" onclick="toggleWatchedCard(event,\'' +
    f.path +
    '\')">Unwatch</button></div>' +
    '</div>' +
    '<div class="progress-label">' +
    pct +
    '%</div><div class="progress-overlay"><div class="progress-fill" style="width:' +
    pct +
    '%"></div></div>' +
    '</div>'
  );
}
function activateKeyboardClick(event, element) {
  if (event.key !== 'Enter' && event.key !== ' ') return;
  event.preventDefault();
  element.click();
}
function toggleRecentSection(label, storageKey) {
  const grid = label.nextElementSibling;
  const chevron = label.querySelector('.recent-chevron');
  const open = grid.style.display === 'none';
  grid.style.display = open ? '' : 'none';
  if (chevron) chevron.textContent = open ? '▼' : '▶';
  if (storageKey) localStorage.setItem(storageKey, open ? '1' : '0');
}
function goBack() {
  currentIsLinkedDir = false;
  if (browseHistory.length > 0) {
    const prev = browseHistory.pop();
    browse(prev, 0, false, false);
  } else {
    const d = document.getElementById('back-button').dataset.dir;
    if (d) browse(d, 0, false, false);
  }
}
function onCardClick(event, p) {
  if (cardHoldOpened) {
    cardHoldOpened = false;
    event.preventDefault();
    event.stopPropagation();
    return;
  }
  // If at least one video is selected, row click toggles selection instead of playing
  if (_videoSelected.size > 0) {
    _videoSelectionToggle(p);
    return;
  }
  play(p);
}
function beginCardHold(event, p) {
  if (!window.matchMedia('(pointer:coarse)').matches) return;
  clearTimeout(cardHoldTimer);
  cardHoldOpened = false;
  const targetCard = event.currentTarget;
  cardHoldTimer = setTimeout(() => {
    document
      .querySelectorAll('.movie-card.actions-open')
      .forEach((card) => card.classList.remove('actions-open'));
    if (targetCard) {
      targetCard.classList.add('actions-open');
      cardHoldOpened = true;
      haptic(18);
    }
  }, 520);
}
function endCardHold() {
  clearTimeout(cardHoldTimer);
  cardHoldTimer = null;
}
async function play(p, name) {
  const title = name || '';
  playingPath = p;
  setStatus(title ? 'Playing: ' + title : 'Playing...');
  setPlayerPoster(p);
  markPlayed(p);
  updatePlayingCard(p);
  if (queuedVideos.has(p)) {
    await api('/api/queue/remove?path=' + encodeURIComponent(p));
    queuedVideos.delete(p);
    setQueuedCard(p, false);
  }
  await api('/api/play?path=' + encodeURIComponent(p));
  setTimeout(() => loadRecent().then((files) => renderRecent(files)), 300);
  startPolling();
  await pollStatus();
}
function setPlayerPoster(p) {
  const bar = document.getElementById('now-playing-bar');
  if (!bar || !p) return;
  const thumb = '/api/thumb?path=' + encodeURIComponent(p);
  bar.style.background =
    'linear-gradient(135deg,rgba(13,13,26,.92),rgba(21,21,38,.88)),url(' + thumb + ') center/cover';
  const poster = document.getElementById('player-poster');
  if (poster) poster.style.backgroundImage = 'url(' + thumb + ')';
}
function closeCardActions(event) {
  const card = event?.target?.closest('.movie-card');
  if (card) card.classList.remove('actions-open');
}
async function playCardAction(event, p, name) {
  event.stopPropagation();
  closeCardActions(event);
  await play(p, name);
}
async function startOverCard(event, p, name) {
  event.stopPropagation();
  closeCardActions(event);
  await play(p, name);
  await api('/api/seek?pos=0');
}
async function queueCardAction(event, p) {
  event.stopPropagation();
  closeCardActions(event);
  haptic(10);
  if (queuedVideos.has(p)) {
    await removeQueueItem(p);
    return;
  }

  await api('/api/queue/add?path=' + encodeURIComponent(p));
  queuedVideos.add(p);
  setQueuedCard(p, true);
  updateQueueControls({
    queue: Array.from(queuedVideos).map((path) => ({
      path: path,
      title: path.split(/[\\/]/).pop() || path,
    })),
  });
  await pollStatus();
  setStatus('Added to queue.');
}
async function removeQueueItem(p) {
  haptic(8);
  await api('/api/queue/remove?path=' + encodeURIComponent(p));
  queuedVideos.delete(p);
  setQueuedCard(p, false);
  await pollStatus();
  setStatus('Removed from queue.');
}
async function moveQueueItem(p, direction) {
  haptic(6);
  await api(
    '/api/queue/move?path=' + encodeURIComponent(p) + '&direction=' + encodeURIComponent(direction)
  );
  await pollStatus();
}
async function clearQueue() {
  haptic(10);
  await api('/api/queue/clear');
  queuedVideos.clear();
  syncQueuedCards();
  updateQueueControls({ queue: [] });
  setStatus('Queue cleared.');
}
async function toggleFavoriteCard(event, p) {
  event.stopPropagation();
  closeCardActions(event);
  const favorite = !favoriteVideos.has(p);
  await api('/api/favorite?path=' + encodeURIComponent(p) + '&value=' + favorite);
  setFavoriteCard(p, favorite);
  setStatus(favorite ? 'Added to favorites.' : 'Removed from favorites.');
}
function setFavoriteCard(p, favorite) {
  if (favorite) favoriteVideos.add(p);
  else favoriteVideos.delete(p);
  document.querySelectorAll('[data-path="' + cssEscape(p) + '"]').forEach((card) => {
    card.classList.toggle('favorite', favorite);
    const inner = card.querySelector('.movie-card-inner');
    const existingBadge = card.querySelector('.favorite-badge');
    if (favorite && !existingBadge && inner) {
      const badge = document.createElement('div');
      badge.className = 'favorite-badge queued-badge';
      badge.textContent = 'Favorite';
      const stopButton = inner.querySelector('.stop-btn');
      inner.insertBefore(badge, stopButton || inner.querySelector('.card-actions'));
    } else if (!favorite && existingBadge) {
      existingBadge.remove();
    }
    card.querySelectorAll('.card-actions button').forEach((btn) => {
      const text = btn.textContent.trim();
      if (text === 'Favorite' || text === 'Unfavorite')
        btn.textContent = favorite ? 'Unfavorite' : 'Favorite';
    });
    if (!favorite && card.classList.contains('favorite-section-card')) {
      const grid = card.closest('.favorites-grid');
      const label = grid?.previousElementSibling;
      card.remove();
      if (grid && !grid.querySelector('.favorite-section-card')) {
        grid.remove();
        if (label?.classList.contains('favorites-label')) label.remove();
      }
    }
  });
}
async function toggleWatchedCard(event, p) {
  event.stopPropagation();
  closeCardActions(event);
  const target = event.currentTarget;
  const shouldClear =
    playedVideos.has(p) ||
    target?.textContent?.trim() === 'Unwatch' ||
    Boolean(target?.closest('.continue-card'));
  if (shouldClear) {
    await api('/api/history/clear?path=' + encodeURIComponent(p));
    unmarkPlayed(p);
    setStatus('Resume position cleared.');
  } else {
    markPlayed(p);
  }
}
async function stopPlayingCard(event, p) {
  event.stopPropagation();
  if (p !== playingPath) return;
  await stop();
}
function updateStopBtn(isPlaying, hasQueue) {
  const btn = document.getElementById('stop-btn');
  if (!btn) return;
  btn.className = 'btn btn-stop';
  btn.innerHTML = '&#9632; STOP';
  btn.onclick = stop;
  btn.disabled = !isPlaying;
}
async function playQueueStart() {
  if (!lastQueue.length) return;
  const first = lastQueue[0];
  setStatus('Playing next queued: ' + (first.title || 'Queued video'));
  startPolling();
  await api('/api/adjacent?direction=next');
  await pollStatus();
}
async function stop() {
  const stoppedPath = playingPath;
  playingPath = null;
  updatePlayingCard(null, stoppedPath);
  document.getElementById('now-playing-bar').style.background =
    'linear-gradient(135deg,#0d0d1a,#151526)';
  if (isPhoneRemoteOnly) applyPhonePlaybackState(false);
  else {
    applyDesktopDockedLayout(false);
    setPlayerBarVisible(false);
  }
  setStatus('Stopped.');
  await api('/api/stop');
}
async function api(url) {
  try {
    await fetch(url);
  } catch (e) {
    setStatus('Command failed: ' + e);
  }
}
function loadPlayedVideos() {
  const m = document.cookie.match(/(?:^|; )playedVideos=([^;]*)/);
  if (!m) return [];
  try {
    return JSON.parse(decodeURIComponent(m[1]));
  } catch (e) {
    return [];
  }
}
function savePlayedVideos() {
  const values = Array.from(playedVideos).slice(-1000);
  document.cookie =
    'playedVideos=' +
    encodeURIComponent(JSON.stringify(values)) +
    '; max-age=31536000; path=/; SameSite=Lax';
}
function markPlayed(p) {
  playedVideos.add(p);
  savePlayedVideos();
  api('/api/history/watched/set?path=' + encodeURIComponent(p) + '&watched=true');
  for (const card of getMovieCards(p)) {
    card.classList.add('played');
    const watchedButton = findWatchedButton(card);
    if (watchedButton) watchedButton.textContent = 'Unwatch';
  }
}
function unmarkPlayed(p) {
  playedVideos.delete(p);
  savePlayedVideos();
  api('/api/history/watched/set?path=' + encodeURIComponent(p) + '&watched=false');
  for (const card of getMovieCards(p)) {
    card.classList.remove('played');
    const watchedButton = findWatchedButton(card);
    if (watchedButton) watchedButton.textContent = 'Watched';
    if (card.id.startsWith('continue-card-')) {
      const grid = card.closest('.continue-grid');
      const label = grid?.previousElementSibling;
      card.remove();
      if (grid && !grid.querySelector('.movie-card')) {
        grid.remove();
        label?.remove();
      }
    }
  }
}
function findWatchedButton(card) {
  return Array.from(card.querySelectorAll('.card-actions button')).find((btn) =>
    String(btn.getAttribute('onclick') || '').includes('toggleWatchedCard')
  );
}
function updateCardProgress(filePath, position, duration) {
  if (!filePath || !(duration > 0)) return;
  const pct = Math.max(0, Math.min(99, Math.round((position / duration) * 100)));
  for (const card of getMovieCards(filePath)) {
    let overlay = card.querySelector('.progress-overlay');
    let label = card.querySelector('.progress-label');
    if (pct > 0) {
      if (!overlay) {
        overlay = document.createElement('div');
        overlay.className = 'progress-overlay';
        const fill = document.createElement('div');
        fill.className = 'progress-fill';
        overlay.appendChild(fill);
        card.appendChild(overlay);
      }
      overlay.querySelector('.progress-fill').style.width = pct + '%';
      if (!label) {
        label = document.createElement('div');
        label.className = 'progress-label';
        card.appendChild(label);
      }
      label.textContent = pct + '%';
    } else {
      overlay?.remove();
      label?.remove();
    }
  }
}
function updatePlayingCard(newPath, oldPath) {
  const paths = [oldPath, playingPath, newPath].filter(Boolean);
  document.querySelectorAll('.movie-card.playing').forEach((c) => {
    if (c.dataset.path) paths.push(c.dataset.path);
  });
  for (const p of new Set(paths)) {
    for (const card of getMovieCards(p)) {
      const isActive = p === newPath;
      card.classList.toggle('playing', isActive);
      card.classList.remove('actions-open');
      card.classList.toggle('played', !isActive && playedVideos.has(p));
      card.onclick = isActive ? null : (event) => onCardClick(event, p);
      // Grid-mode overlay actions
      const actions = card.querySelector('.card-actions');
      if (actions) actions.style.display = isActive ? 'none' : '';
      // Grid-mode stop button (inside .movie-card-inner)
      const existingStopInner = card.querySelector('.movie-card-inner .stop-btn');
      if (isActive && !existingStopInner) {
        const btn = document.createElement('button');
        btn.className = 'stop-btn';
        btn.innerHTML = '&#9632; STOP';
        btn.onclick = (e) => stopPlayingCard(e, p);
        card.querySelector('.movie-card-inner')?.appendChild(btn);
      } else if (!isActive && existingStopInner) {
        existingStopInner.remove();
      }
      // List-mode inline actions (inside title column): swap to stop button when playing
      const vlcInlineActions = card.querySelector('.vlc-inline-actions');
      if (vlcInlineActions) {
        const ep = p.replace(/'/g, "\\'");
        if (isActive) {
          vlcInlineActions.innerHTML = '<button class="stop-btn" onclick="stopPlayingCard(event,\'' + ep + '\')">&#9632; Stop</button>';
        } else {
          const isQueued  = card.classList.contains('queued');
          const isFav     = card.classList.contains('favorite');
          const isWatched = card.classList.contains('played');
          const fname = esc(card.getAttribute('aria-label') || '').replace(/^Play /, '');
          vlcInlineActions.innerHTML =
            '<button class="primary-action"  onclick="playCardAction(event,\'' + ep + "','" + fname + '\')">&#9654; Play</button>' +
            '<button class="muted-action"    onclick="queueCardAction(event,\'' + ep + '\')">' + (isQueued ? '&#8722; Unqueue' : '+ Queue') + '</button>' +
            '<button class="favorite-action" onclick="toggleFavoriteCard(event,\'' + ep + '\')">' + (isFav ? '&#9733; Unfav' : '&#9734; Fav') + '</button>' +
            '<button class="muted-action"    onclick="toggleWatchedCard(event,\'' + ep + '\')">' + (isWatched ? '&#128064; Unwatch' : '&#10003; Watched') + '</button>';
        }
      }
      // List-mode actions column: rebuild when stopping so the four buttons reappear
      const vlcActions = card.querySelector('.vlc-actions');
      if (vlcActions) {
        if (isActive) {
          vlcActions.innerHTML = '<button class="stop-btn" onclick="stopPlayingCard(event,\'' + p + '\')">&#9632;</button>';
          vlcActions.classList.remove('card-list-actions');
        } else {
          const isQueued   = card.classList.contains('queued');
          const isFav      = card.classList.contains('favorite');
          const isWatched  = card.classList.contains('played');
          const ep = p.replace(/'/g, "\\'");
          const fname = esc(card.getAttribute('aria-label') || '').replace(/^Play /, '');
          vlcActions.classList.add('card-list-actions');
          vlcActions.innerHTML =
            '<button class="primary-action"  onclick="playCardAction(event,\'' + ep + "','" + fname + '\')">&#9654;</button>' +
            '<button class="muted-action"    onclick="queueCardAction(event,\'' + ep + '\')">' + (isQueued ? '&#8722;Q' : '+Q') + '</button>' +
            '<button class="favorite-action" onclick="toggleFavoriteCard(event,\'' + ep + '\')">' + (isFav ? '&#9733;' : '&#9734;') + '</button>' +
            '<button class="muted-action"    onclick="toggleWatchedCard(event,\'' + ep + '\')">' + (isWatched ? '&#128064;' : '&#10003;') + '</button>';
        }
      }
    }
  }
}
function setStatus(m) {
  document.getElementById('status').textContent = m;
}
function dismissInstallHint() {
  localStorage.setItem('remotePlayInstallHintDismissed', '1');
  installHint.style.display = 'none';
}
function showUpdateAvailable() {
  let banner = document.getElementById('update-hint');
  if (!banner) {
    banner = document.createElement('div');
    banner.id = 'update-hint';
    banner.innerHTML =
      '<span>RemotePlay update available.</span><button type="button">Reload</button>';
    document.body.prepend(banner);
    banner.querySelector('button').onclick = () => location.reload();
  }
  banner.style.display = 'flex';
}
function esc(s) {
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}
// Escape a value for embedding inside a single-quoted JS string literal in an HTML attribute.
function jsStr(s) {
  return String(s).replace(/\\/g, '\\\\').replace(/'/g, "\\'");
}
function cssEscape(s) {
  return window.CSS && CSS.escape
    ? CSS.escape(String(s))
    : String(s).replace(/\\/g, '\\\\').replace(/"/g, '\\"');
}
let wakeLock = null;
async function requestWakeLock() {
  if (wakeLock || !('wakeLock' in navigator)) return;
  try {
    wakeLock = await navigator.wakeLock.request('screen');
    wakeLock.addEventListener('release', () => (wakeLock = null));
  } catch (e) {}
}
async function releaseWakeLock() {
  if (!wakeLock) return;
  try {
    await wakeLock.release();
  } catch (e) {}
  wakeLock = null;
}
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible' && playingPath) requestWakeLock();
  if (document.visibilityState === 'visible') _onPageVisible();
});
window.addEventListener('pageshow', (e) => {
  // bfcache restore (common on mobile Safari/Chrome after tab switch or screen-off)
  if (e.persisted) _onPageVisible();
});

// Called when the page/tab becomes visible again (tablet wake, app-switch back, etc.).
// Re-syncs server playback state so music that ended while the screen was off gets
// advanced and the poll is restarted if needed.
async function _onPageVisible() {
  if (isPlayLocal()) return; // local audio is handled by the browser itself
  try {
    const res = await fetch('/api/music/status');
    if (!res.ok) return;
    const s = await res.json();

    const playing = s.isPlaying || s.isPaused;
    musicIsPlaying = playing;

    if (playing) {
      // Music is still going on the server — make sure the poll is running and
      // re-register the lookahead so auto-advance will keep working.
      musicCurrentPath = s.currentPath || musicCurrentPath;
      if (!musicPlaybackPollTimer) startMusicPlaybackPoll();
      _queueNextTrackOnServer();
      return;
    }

    // Music stopped while the screen was off.
    if (!musicCurrentPath) return; // nothing was playing from this client's perspective

    // If auto-advance is enabled, kick off the next track now.
    if (_musicAutoPlay || _musicShuffle || _musicRepeat !== 'none') {
      const nextT = _musicNextTrack();
      if (nextT) {
        await playMusic(nextT.path, nextT.name);
        return;
      }
    }

    // Nothing left to advance — update the bar to reflect stopped state.
    musicCurrentPath = null;
    updateMusicBar(s);
    stopMusicPlaybackPoll();
  } catch {}
}
if ('serviceWorker' in navigator) {
  window.addEventListener('load', () =>
    navigator.serviceWorker
      .register('/service-worker.js')
      .then((registration) => {
        registration.addEventListener('updatefound', () => {
          const worker = registration.installing;
          if (!worker) return;
          worker.addEventListener('statechange', () => {
            if (worker.state === 'installed' && navigator.serviceWorker.controller)
              showUpdateAvailable();
          });
        });
      })
      .catch(() => {})
  );
}
applyPhoneLayout();
phoneLayoutQuery.addEventListener('change', () => {
  const wasPhone = isPhoneRemoteOnly;
  applyPhoneLayout();
  if (wasPhone && !isPhoneRemoteOnly && !currentData) browse(null);
});
window.addEventListener('orientationchange', () => setTimeout(applyPhoneLayout, 80));
window.addEventListener('resize', () => {
  if (document.body.classList.contains('desktop-player-docked')) applyDesktopDockedLayout(true);
});
document.addEventListener('keydown', (e) => {
  if (!playingPath || e.repeat) return;
  const target = e.target;
  if (
    target &&
    (target.tagName === 'INPUT' ||
      target.tagName === 'TEXTAREA' ||
      target.contentEditable === 'true')
  )
    return;
  if (e.key === 'ArrowLeft') {
    e.preventDefault();
    haptic(8);
    skip(-10);
  } else if (e.key === 'ArrowRight') {
    e.preventDefault();
    haptic(8);
    skip(10);
  }
});
browse(null);
refreshLibraryStatus();
refreshThumbnailStatus();
setInterval(refreshLibraryStatus, 2500);
setInterval(refreshThumbnailStatus, 2500);
updateVolumeIcon(parseFloat(document.getElementById('video-combined-vol')?.value) || 0);
// updateAudioBoostIcon is a no-op — boost state now reflected by combined slider colour
syncSpeedChips(currentPlaybackSpeed);
const nowPlayingBar = document.getElementById('now-playing-bar');
if (nowPlayingBar) {
  nowPlayingBar.addEventListener('touchstart', onPlayerGestureStart, { passive: true });
  nowPlayingBar.addEventListener('touchend', onPlayerGestureEnd, { passive: true });
  nowPlayingBar.addEventListener(
    'touchcancel',
    () => {
      gestureStart = null;
    },
    { passive: true }
  );
}
startPolling();
refreshPeers();
setInterval(refreshPeers, 8000);
refreshVersion();
setInterval(refreshVersion, 30000);
document.addEventListener('click', (e) => {
  const dd = document.getElementById('peers-dropdown');
  if (dd.classList.contains('open') && !e.target.closest('#peers-wrap')) closePeers();
});
document.addEventListener('pointerdown', (e) => {
  if (!e.target.closest('.movie-card'))
    document
      .querySelectorAll('.movie-card.actions-open')
      .forEach((c) => c.classList.remove('actions-open'));
});

// Diagnostics panel wiring
(function wireDiagnostics() {
  const overlay = document.getElementById('diagnostics-overlay');
  const tabs = document.getElementById('diag-tabs');
  const btnRefresh = document.getElementById('btn-diag-refresh');
  const btnClose = document.getElementById('btn-diag-close');
  const btnRescan = document.getElementById('btn-rescan');
  const btnThumbs = document.getElementById('btn-thumbs');
  if (overlay)
    overlay.addEventListener('click', (e) => {
      if (e.target === overlay) closeDiagnostics();
    });
  if (tabs)
    tabs.addEventListener('click', (e) => {
      const btn = e.target.closest('.diag-tab');
      if (!btn) return;
      switchDiagTab(btn, btn.dataset.tab);
    });
  if (btnRefresh) btnRefresh.addEventListener('click', () => refreshDiagnostics());
  if (btnClose) btnClose.addEventListener('click', () => closeDiagnostics());
  if (btnRescan) btnRescan.addEventListener('click', () => rescan());
  if (btnThumbs) btnThumbs.addEventListener('click', () => startThumbnailQueue());
})();

async function refreshPeers() {
  try {
    const res = await fetch('/api/peers');
    if (!res.ok) return;
    const peers = await res.json();
    const dot = document.getElementById('peers-dot');
    const others = peers.filter((p) => !p.isSelf);
    const count = others.length;
    dot.className = count > 0 ? 'multi' : '';
    const label = count > 0 ? `Instances (${count})` : 'Instances';
    const labelEl = document.querySelector('#peers-btn .im-label');
    if (labelEl) labelEl.textContent = label;
    document.getElementById('peers-btn').title =
      count > 0 ? `${count} other instance(s) on the network` : 'No other instances found';
    renderPeersDropdown(peers);
  } catch {}
}

function renderPeersDropdown(peers) {
  const dd = document.getElementById('peers-dropdown');
  dd.innerHTML = '';
  const header = document.createElement('div');
  header.id = 'peers-header';
  header.textContent = 'RemotePlay instances';
  dd.appendChild(header);
  const self = peers.find((p) => p.isSelf);
  const others = peers.filter((p) => !p.isSelf);
  if (self) {
    const el = document.createElement('div');
    el.className = 'peer-item peer-self';
    el.innerHTML = `<span class="peer-dot"></span><span class="peer-info"><div class="peer-name">${esc(self.name)}</div><div class="peer-addr">${esc(self.host)}:${self.port}</div></span><span class="peer-badge">this</span>`;
    dd.appendChild(el);
  }
  if (others.length === 0) {
    const el = document.createElement('div');
    el.className = 'peer-no-others';
    el.textContent = 'No other instances discovered yet.';
    dd.appendChild(el);
  } else {
    others.forEach((p) => {
      const row = document.createElement('div');
      row.className = 'peer-item-row';
      const a = document.createElement('a');
      a.className = 'peer-item';
      a.href = p.url;
      a.title = `Switch to ${esc(p.name)} — ${p.url}`;
      a.innerHTML = `<span class="peer-dot"></span><span class="peer-info"><div class="peer-name">${esc(p.name)}</div><div class="peer-addr">${esc(p.host)}:${p.port}</div></span>`;
      row.appendChild(a);
      const syncBtn = document.createElement('button');
      syncBtn.className = 'peer-sync-btn';
      syncBtn.title = `Sync current playback to ${esc(p.name)}`;
      syncBtn.textContent = '⇒ Sync';
      syncBtn.onclick = (e) => { e.preventDefault(); e.stopPropagation(); syncToPeer(p.url); };
      row.appendChild(syncBtn);
      dd.appendChild(row);
    });
  }
}

function togglePeers() {
  const dd = document.getElementById('peers-dropdown');
  const btn = document.getElementById('peers-btn');
  const isOpen = dd.classList.toggle('open');
  btn.setAttribute('aria-expanded', String(isOpen));
  if (isOpen) refreshPeers();
}

function closePeers() {
  document.getElementById('peers-dropdown').classList.remove('open');
  document.getElementById('peers-btn').setAttribute('aria-expanded', 'false');
}

async function syncToPeer(peerUrl) {
  closePeers();
  try {
    const res = await fetch('/api/status');
    if (!res.ok) return;
    const s = await res.json();
    if (!s.isPlaying || !s.filePath) {
      alert('Nothing is currently playing to sync.');
      return;
    }
    const base = peerUrl.replace(/\/$/, '');
    // Play the same file on the peer
    await fetch(`${base}/api/play?` + new URLSearchParams({ path: s.filePath }));
    // Allow the peer a moment to start, then seek to match our position
    await new Promise((r) => setTimeout(r, 800));
    await fetch(`${base}/api/seek?` + new URLSearchParams({ position: String(Math.round(s.position)) }));
  } catch (err) {
    alert('Sync failed: ' + err.message);
  }
}

let _loadedVersion = null;
async function refreshVersion() {
  try {
    const res = await fetch('/api/version');
    if (!res.ok) return;
    const v = await res.json();
    const chip = document.getElementById('version-chip');
    if (chip && v.version) chip.textContent = 'v' + v.version;
    const banner = document.getElementById('update-banner');
    if (banner) banner.style.display = v.isUpdating ? 'block' : 'none';
    if (v.version) {
      if (_loadedVersion === null) {
        _loadedVersion = v.version;
      } else if (_loadedVersion !== v.version) {
        // Server was updated while this page was open — force a full reload to pick up new assets
        if ('serviceWorker' in navigator) {
          const reg = await navigator.serviceWorker.getRegistration();
          if (reg) await reg.update();
        }
        location.reload(true);
        return;
      }
    }
  } catch {}
}
