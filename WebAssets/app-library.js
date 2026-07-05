// Initialize to 'video'; will be updated by _bootstrapFromRoute() if URL specifies otherwise
let currentMode = 'video';
let musicBrowseHistory = [];
let currentMusicFolder = null;
let _musicLinkBase = null; // {real: string, virtual: string} | null — virtual path base for rplink folder navigation
let currentMusicData = null;
let musicStatusPollTimer = null;
let musicPlaybackPollTimer = null;
let musicCurrentPath = null;
let musicIsPlaying = false;
let musicCurrentVolume = 0.8;
// Clear the dynamic-session-browsed flag on every fresh page load so that after
// a server restart the music tab always starts at root, not at the last dynamic folder.
try { sessionStorage.removeItem('rpdyn_browsed'); } catch {}
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

function _countWord(n, singular, plural) {
  return n === 1 ? `1 ${singular}` : `${n} ${plural}`;
}

function _countWordNoZero(n, singular, plural) {
  if (n <= 0) return `no ${plural}`;
  return _countWord(n, singular, plural);
}

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
  await _videoSelectionPlayFromSelection(false);
}

async function _videoSelectionQueue() {
  await _videoSelectionPlayFromSelection(true);
}

function _buildQueuedVideoControlsPayload() {
  return {
    queue: Array.from(queuedVideos).map((path) => ({
      path,
      title: path.split(/[\\/]/).pop() || path,
    })),
  };
}

async function _videoSelectionPlayFromSelection(syncQueueControls) {
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

  if (syncQueueControls) updateQueueControls(_buildQueuedVideoControlsPayload());

  _videoSelectionClear();
  play(first);
}

// ── Music playback ─────────────────────────────────────────────────────
let _musicTrackListFolder = null; // folder the current musicTrackList was built from
// Persistent queue cursor: -1 = not in queue mode; >= 0 = index into _musicQueue
window._musicQueue    = window._musicQueue    || [];
window._musicQueuePos = (window._musicQueuePos != null) ? window._musicQueuePos : -1;
// Album art URL for the currently-active playlist (blob URL or null)
window._playlistArtUrl = window._playlistArtUrl || null;
// Player-bar art miss caches (avoid retry spam on every status poll)
window._musicLocalCoverMiss = window._musicLocalCoverMiss || new Set();
window._musicAlbumArtMiss = window._musicAlbumArtMiss || new Set();
async function playMusic(path, name) {
  // Safety: while a video is actively playing and the user is not in Music mode,
  // ignore background music auto-start attempts (queue/poll race).
  if (playingPath && currentMode !== 'music') {
    stopMusicPlaybackPoll();
    return;
  }

  await _stopAllPlaybackForNewStart();

  musicCurrentPath = path;
  // If this path is not in the current queue, this is a manual play — reset queue cursor.
  if (window._musicQueue && window._musicQueue.length > 0) {
    const pathKey = _normalizeMusicPath(path);
    const qIdx = window._musicQueue.findIndex((q) => _normalizeMusicPath(q.path) === pathKey);
    if (qIdx < 0) {
      // Playing something outside the queue — clear queue entirely
      window._musicQueue = [];
      window._musicQueuePos = -1;
      window._playlistArtUrl = null;
    } else {
      // Jumping to a specific queue item — cursor is already set by the caller,
      // but ensure it matches the found index for any direct playMusic() call.
      window._musicQueuePos = qIdx;
    }
  }
  // Update the .playing highlight immediately on click
  document.querySelectorAll('.music-track-card').forEach((card) => {
    card.classList.toggle('playing', card.dataset.path === path);
  });
  // Build/update track list from current browse data for prev/next.
  // Skip when a playlist queue is driving playback — rebuilding musicTrackList
  // from the browse folder would corrupt the queue-next lookahead.
  const _hasActiveQueue = window._musicQueue && window._musicQueue.length > 0;
  if (!_hasActiveQueue && currentMusicData && currentMusicData.files && currentMusicData.files.length) {
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
    const trackMeta = _findTrackMetaByPath(path) || {};
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
    // Only record individual tracks when NOT in playlist-queue mode
    if (!(window._musicQueue && window._musicQueue.length > 0)) {
      fetch('/api/music/play?path=' + encodeURIComponent(path) + '&recordOnly=1').catch(() => {});
      setTimeout(() => loadMusicRecent().then((files) => renderMusicRecent(files)), 300);
    }
    // In local mode the stream is handled by the browser; no server-side play call needed for queue tracks
    requestWakeLock();
    return;
  }

  const _inQueue = window._musicQueue && window._musicQueue.length > 0;
  if (typeof closeLocalPlayerForNonLocalPlayback === 'function') {
    await closeLocalPlayerForNonLocalPlayback();
  }

  await fetch('/api/music/play?path=' + encodeURIComponent(path) + (_inQueue ? '&skipRecord=1' : ''));
  musicIsPlaying = true;
  // Only refresh recent strip when NOT in playlist-queue mode (playlist was recorded once on launch)
  if (!_inQueue) {
    setTimeout(() => loadMusicRecent().then((files) => renderMusicRecent(files)), 300);
  }
  // Immediately tell the server which track comes next so playback continues
  // even when this browser tab is closed before the current song ends.
  _queueNextTrackOnServer();
  const trackMetaSrv = _findTrackMetaByPath(path) || {};
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
  requestWakeLock();
}

function openArtistWiki() {
  const artist = (document.getElementById('music-bar-artist') || {}).textContent || '';
  if (!artist.trim()) return;
  const panel      = document.getElementById('music-wiki-panel');
  const backdrop   = document.getElementById('music-wiki-backdrop');
  const titleEl    = document.getElementById('music-wiki-title');
  const bodyEl     = document.getElementById('music-wiki-body');
  const linkEl     = document.getElementById('music-wiki-open-link');
  if (!panel || !bodyEl) return;

  if (titleEl) titleEl.textContent = artist.trim();
  if (bodyEl)  bodyEl.innerHTML = '<div class="wiki-loading">Loading\u2026</div>';
  panel.style.display = '';
  if (backdrop) backdrop.style.display = '';
  panel.classList.add('open');

  fetch('/api/music/wiki?artist=' + encodeURIComponent(artist.trim()))
    .then(r => r.ok ? r.json() : Promise.reject(r.status))
    .then(data => {
      if (linkEl) { linkEl.href = data.pageUrl; }
      if (bodyEl) {
        const imgHtml = data.imageUrl
          ? `<img class="wiki-main-image" src="${data.imageUrl}" alt="" />`
          : '';
        bodyEl.innerHTML = imgHtml + (data.html || '<em>No content.</em>');
      }
      // Disable all links — remove href so nothing navigates away
      bodyEl.querySelectorAll('a').forEach(a => {
        a.removeAttribute('href');
        a.removeAttribute('target');
        a.style.cursor = 'default';
        a.style.pointerEvents = 'none';
        a.style.color = 'inherit';
        a.style.textDecoration = 'none';
      });
    })
    .catch(() => {
      if (bodyEl) bodyEl.innerHTML = '<em>Could not load Wikipedia content.</em>';
    });
}

function closeWikiPanel() {
  const panel    = document.getElementById('music-wiki-panel');
  const backdrop = document.getElementById('music-wiki-backdrop');
  if (!panel) return;
  panel.classList.remove('open');
  panel.addEventListener('transitionend', () => {
    panel.style.display = 'none';
    if (backdrop) backdrop.style.display = 'none';
  }, { once: true });
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
  _clearMusicPlaybackVisuals();
  const bar = document.getElementById('music-player-bar');
  if (bar) bar.style.display = 'none';
  document.body.classList.remove('music-player-docked');
  stopMusicPlaybackPoll();
  if (!playingPath) releaseWakeLock();
  localStop(); // also silence local browser audio
}

function _clearMusicPlaybackVisuals() {
  document.querySelectorAll('.music-track-card.playing').forEach((card) => {
    card.classList.remove('playing');
  });

  if (typeof _renderMusicQueuePeek === 'function') _renderMusicQueuePeek();
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
  if (musicTrackList.length === 0 && !(window._musicQueue && window._musicQueue.length)) return;
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

function _normalizeMusicPath(path) {
  let s = String(path || '').trim();
  if (!s) return '';
  try { s = decodeURIComponent(s); } catch (_) {}
  s = s.split('?')[0].split('#')[0];
  s = s.replace(/^file:\/\//i, '');
  s = s.replace(/\\/g, '/').replace(/\/+/g, '/');
  return s.toLowerCase();
}

function _trackBaseName(value) {
  const raw = String(value || '').trim();
  if (!raw) return '';
  const last = raw.replace(/\\/g, '/').split('/').pop() || raw;
  return last.replace(/\.[^.]+$/, '').toLowerCase();
}

function _allTrackMetaSources() {
  const all = [];
  if (Array.isArray(musicTrackList) && musicTrackList.length) all.push(...musicTrackList);
  if (Array.isArray(window._musicQueue) && window._musicQueue.length) all.push(...window._musicQueue);
  if (currentMusicData && Array.isArray(currentMusicData.files) && currentMusicData.files.length) all.push(...currentMusicData.files);
  return all;
}

function _findTrackMetaByPath(path) {
  const key = _normalizeMusicPath(path);
  if (!key) return null;

  const all = _allTrackMetaSources();
  const exact = all.find(t => _normalizeMusicPath(t.path) === key);
  if (exact) return exact;

  const base = _trackBaseName(key);
  if (!base) return null;
  return all.find(t => _trackBaseName(t.path) === base) || null;
}

function _findTrackMetaForStatus(s) {
  const status = s || {};
  const byPath = _findTrackMetaByPath(status.coverPath || status.currentPath || musicCurrentPath || '');
  if (byPath) return byPath;

  const all = _allTrackMetaSources();
  const titleKey = _trackBaseName(status.title || '');
  if (!titleKey || !all.length) return null;

  const artistKey = String(status.artist || '').trim().toLowerCase();
  let best = null;
  let bestScore = -1;

  for (const t of all) {
    const pathName = _trackBaseName(t.path || '');
    const nameKey = _trackBaseName(t.name || '');
    if (pathName !== titleKey && nameKey !== titleKey) continue;

    let score = 1;
    const a = String(t.artist || '').trim().toLowerCase();
    if (artistKey && a === artistKey) score += 2;
    if (t.album && !['all', 'all songs', 'albums', 'singles'].includes(String(t.album).trim().toLowerCase())) score += 1;

    if (score > bestScore) {
      bestScore = score;
      best = t;
    }
  }

  return best;
}

function updateMusicBar(s) {
  window._lastMusicBarState = s;
  const bar = document.getElementById('music-player-bar');
  if (!bar) return;
  const active = (s.isPlaying || s.isPaused) && currentMode === 'music';
  bar.style.display = active ? 'flex' : 'none';
  const isDocked = active && window.matchMedia('(min-width:900px)').matches;
  document.body.classList.toggle('music-player-docked', isDocked);

  const isGenericAlbum = (v) => {
    const n = String(v || '').trim().toLowerCase();
    return n === 'all' || n === 'all songs' || n === 'albums' || n === 'singles';
  };
  const lookupPath = s.coverPath || s.currentPath || musicCurrentPath || '';
  let barTrackMeta = _findTrackMetaForStatus({ ...s, currentPath: lookupPath });

  if (barTrackMeta) {
    s.artist = s.artist || barTrackMeta.artist || '';
    if (!s.album || isGenericAlbum(s.album))
      s.album = barTrackMeta.album || s.album || '';
    s.genre = s.genre || barTrackMeta.genre || '';
    s.year = s.year || barTrackMeta.year || null;
    s.ext = s.ext || barTrackMeta.ext || '';
    s.durationSec = s.durationSec || barTrackMeta.durationSec || 0;
    s.hasCover = s.hasCover ?? barTrackMeta.hasCover;
  }

  // Title
  const title = document.getElementById('music-bar-title');
  if (title) title.textContent = s.title || '—';

  // Artist + album
  const artistEl = document.getElementById('music-bar-artist');
  if (artistEl) artistEl.textContent = s.artist || '';
  const albumEl = document.getElementById('music-bar-album');
  if (albumEl) albumEl.textContent = s.album || '';

  // Genre muted line
  const gyEl = document.getElementById('music-bar-genreyear');
  if (gyEl) {
    gyEl.textContent = s.genre || '';
    gyEl.style.display = s.genre ? '' : 'none';
  }

  // Format + length + year badges
  const badgeEl = document.getElementById('music-bar-badge');
  if (badgeEl) {
    const fmt = s.ext ? s.ext.toUpperCase() : '';
    const dur = s.durationSec != null && s.durationSec > 0 ? fmtSec(s.durationSec) : '';
    const year = s.year ? String(s.year) : '';
    badgeEl.innerHTML = (fmt ? `<span class="music-bar-fmt-badge">${fmt}</span>` : '')
      + (dur ? `<span class="music-bar-dur">Length: ${dur}</span>` : '')
      + (year ? `<span class="music-bar-year">Year: ${esc(year)}</span>` : '');
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
    let cp = s.coverPath || s.currentPath || musicCurrentPath;
    const plArt = window._playlistArtUrl || null;
    const artist = s.artist || '';
    const album  = s.album  || '';
    const title  = s.title  || '';
    const cpKey = _normalizeMusicPath(cp);
    if (cpKey && window._musicLocalCoverMiss.has(cpKey)) cp = null;
    if (cp) {
      // Always try local cover; fall back to online album art; fall back to empty
      const existing = artEl.querySelector('.music-bar-art-img');
      const newSrc = '/api/music/cover?path=' + encodeURIComponent(cp);
      if (!existing || existing.getAttribute('data-cp') !== cp) {
        const img = document.createElement('img');
        img.src = newSrc;
        img.setAttribute('data-cp', cp);
        img.className = 'music-bar-art-img';
        img.onload = () => { if (cpKey) window._musicLocalCoverMiss.delete(cpKey); };
        img.onerror = () => {
          if (cpKey) window._musicLocalCoverMiss.add(cpKey);
          // Stage 2: try online album art — use album if available, fall back to track title
          // (artist required for title fallback to avoid wrong-artist art)
          const searchTerm = album || (artist ? title : '');
          if (searchTerm) {
            const onlineKey = (String(artist || '').trim().toLowerCase() + '|' + String(searchTerm || '').trim().toLowerCase());
            if (window._musicAlbumArtMiss.has(onlineKey)) {
              if (plArt) {
                const pimg = document.createElement('img');
                pimg.src = plArt;
                pimg.setAttribute('data-pl-art', plArt);
                pimg.className = 'music-bar-art-img music-bar-art-playlist';
                pimg.onerror = () => { artEl.classList.add('music-bar-art-empty'); pimg.remove(); };
                artEl.querySelectorAll('.music-bar-art-img').forEach(el => el.remove());
                artEl.classList.remove('music-bar-art-empty');
                artEl.appendChild(pimg);
              } else {
                artEl.classList.add('music-bar-art-empty');
                img.remove();
              }
              return;
            }
            const onlineUrl = '/api/music/album-art?album=' + encodeURIComponent(searchTerm)
              + (artist ? '&artist=' + encodeURIComponent(artist) : '');
            img.onerror = () => {
              window._musicAlbumArtMiss.add(onlineKey);
              if (plArt) {
                const pimg = document.createElement('img');
                pimg.src = plArt;
                pimg.setAttribute('data-pl-art', plArt);
                pimg.className = 'music-bar-art-img music-bar-art-playlist';
                pimg.onerror = () => { artEl.classList.add('music-bar-art-empty'); pimg.remove(); };
                artEl.querySelectorAll('.music-bar-art-img').forEach(el => el.remove());
                artEl.classList.remove('music-bar-art-empty');
                artEl.appendChild(pimg);
              } else {
                artEl.classList.add('music-bar-art-empty');
                img.remove();
              }
            };
            img.src = onlineUrl;
          } else if (plArt) {
            const pimg = document.createElement('img');
            pimg.src = plArt;
            pimg.setAttribute('data-pl-art', plArt);
            pimg.className = 'music-bar-art-img music-bar-art-playlist';
            pimg.onerror = () => { artEl.classList.add('music-bar-art-empty'); pimg.remove(); };
            artEl.querySelectorAll('.music-bar-art-img').forEach(el => el.remove());
            artEl.classList.remove('music-bar-art-empty');
            artEl.appendChild(pimg);
          } else {
            artEl.classList.add('music-bar-art-empty');
            img.remove();
          }
        };
        artEl.querySelectorAll('.music-bar-art-img').forEach(el => el.remove());
        artEl.classList.remove('music-bar-art-empty');
        artEl.appendChild(img);
      }
    } else {
      // No local cover path: still try online album art when album/artist metadata is known.
      const searchTerm = album || (artist ? title : '');
      if (searchTerm) {
        const onlineKey = (String(artist || '').trim().toLowerCase() + '|' + String(searchTerm || '').trim().toLowerCase());
        if (window._musicAlbumArtMiss.has(onlineKey)) {
          if (plArt) {
            const existing = artEl.querySelector('.music-bar-art-img');
            if (!existing || existing.getAttribute('data-pl-art') !== plArt) {
              const pimg = document.createElement('img');
              pimg.src = plArt;
              pimg.setAttribute('data-pl-art', plArt);
              pimg.className = 'music-bar-art-img music-bar-art-playlist';
              pimg.onerror = () => { artEl.classList.add('music-bar-art-empty'); pimg.remove(); };
              artEl.querySelectorAll('.music-bar-art-img').forEach(el => el.remove());
              artEl.classList.remove('music-bar-art-empty');
              artEl.appendChild(pimg);
            }
          } else {
            artEl.querySelectorAll('.music-bar-art-img').forEach(el => el.remove());
            artEl.classList.add('music-bar-art-empty');
          }
        } else {
          const onlineUrl = '/api/music/album-art?album=' + encodeURIComponent(searchTerm)
            + (artist ? '&artist=' + encodeURIComponent(artist) : '');
          const existing = artEl.querySelector('.music-bar-art-img');
          if (!existing || existing.getAttribute('data-online-art') !== onlineUrl) {
            const img = document.createElement('img');
            img.src = onlineUrl;
            img.setAttribute('data-online-art', onlineUrl);
            img.className = 'music-bar-art-img';
            img.onerror = () => {
              window._musicAlbumArtMiss.add(onlineKey);
              if (plArt) {
              const pimg = document.createElement('img');
              pimg.src = plArt;
              pimg.setAttribute('data-pl-art', plArt);
              pimg.className = 'music-bar-art-img music-bar-art-playlist';
              pimg.onerror = () => { artEl.classList.add('music-bar-art-empty'); pimg.remove(); };
              artEl.querySelectorAll('.music-bar-art-img').forEach(el => el.remove());
              artEl.classList.remove('music-bar-art-empty');
              artEl.appendChild(pimg);
            } else {
              artEl.classList.add('music-bar-art-empty');
              img.remove();
            }
          };
                img.onload = () => { window._musicAlbumArtMiss.delete(onlineKey); };
                artEl.querySelectorAll('.music-bar-art-img').forEach(el => el.remove());
                artEl.classList.remove('music-bar-art-empty');
                artEl.appendChild(img);
              }
            }
          } else if (plArt) {
        // No metadata-based art — use playlist album art in the player bar
        const existing = artEl.querySelector('.music-bar-art-img');
        if (!existing || existing.getAttribute('data-pl-art') !== plArt) {
          const img = document.createElement('img');
          img.src = plArt;
          img.setAttribute('data-pl-art', plArt);
          img.className = 'music-bar-art-img music-bar-art-playlist';
          img.onerror = () => { artEl.classList.add('music-bar-art-empty'); img.remove(); };
          artEl.querySelectorAll('.music-bar-art-img').forEach(el => el.remove());
          artEl.classList.remove('music-bar-art-empty');
          artEl.appendChild(img);
        }
      } else {
        artEl.querySelectorAll('.music-bar-art-img').forEach(el => el.remove());
        artEl.classList.add('music-bar-art-empty');
      }
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
    seek.max = s.duration > 0 ? s.duration : (s.durationSec > 0 ? s.durationSec : 0);
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
  // Advance synced-lyrics highlight
  if (typeof _lrcTick === 'function') _lrcTick(s.position || 0);

  if (s.lastError && !active) bar.style.display = 'none';
  if (typeof _syncEqWrapVisibility === 'function') _syncEqWrapVisibility();
  if (typeof renderEqPresets === 'function') {
    const eqP = s.eqPreset ?? s.EqPreset ?? -1;
    if (_lastEqPreset !== eqP) renderEqPresets(eqP);
  }
  if (typeof _renderMusicReverbPreset === 'function')
    _renderMusicReverbPreset(s.reverbPreset ?? s.ReverbPreset);
  if (typeof _syncMusicReverbFromStatus === 'function')
    _syncMusicReverbFromStatus(s);
  if (typeof _renderMusicVocalRemoval === 'function')
    _renderMusicVocalRemoval(s.vocalMode ?? s.VocalMode ?? (s.vocalRemoval ?? s.VocalRemoval ?? false));
}

async function musicPlayHere() {
  if (!musicCurrentPath) return;
  const path = musicCurrentPath;
  const title = document.getElementById('music-bar-title')?.textContent || 'Track';
  // Stop server playback so we don't hear two versions
  await fetch('/api/music/stop');
  musicIsPlaying = false;
  stopMusicPlaybackPoll();
  localPlay('/api/music/stream?path=' + encodeURIComponent(path), title, 'Music');
}

// -- Lyrics -------------------------------------------------------------------
const _musicLyricsCache = new Map(); // path -> { found, lyrics, syncedLyrics, source, geniusUrl } | 'loading'
let _musicLyricsCurrentPath = null;
let _musicLyricsPanelOpen = false; // persists across track changes
let _lyricsFontSizeStep = 0;       // integer offset from default (-3..+5)
let _lyricsSyncedMode = true;      // true = prefer synced LRC, false = force plain

const _LYRICS_FONT_STEPS = ['.68rem','.73rem','.78rem','.82rem','.88rem','.95rem','1.02rem','1.1rem','1.18rem'];
const _LYRICS_FONT_DEFAULT_IDX = 3; // .82rem

function _getLyricsFontIdx() {
  return Math.max(0, Math.min(_LYRICS_FONT_STEPS.length - 1, _LYRICS_FONT_DEFAULT_IDX + _lyricsFontSizeStep));
}

/** Change font size by delta (+1 or -1) and persist in localStorage. */
function changeLyricsFontSize(delta) {
  _lyricsFontSizeStep = Math.max(-3, Math.min(5, _lyricsFontSizeStep + delta));
  try { localStorage.setItem('lyricsFontStep', String(_lyricsFontSizeStep)); } catch (_) {}
  _applyLyricsFontSize();
}

function _applyLyricsFontSize() {
  const size = _LYRICS_FONT_STEPS[_getLyricsFontIdx()];
  document.documentElement.style.setProperty('--lyrics-font-size', size);
}

/** Toggle between synced LRC mode and plain-text mode. */
function toggleLyricsSyncedMode() {
  _lyricsSyncedMode = !_lyricsSyncedMode;
  const btn = document.getElementById('lyrics-btn-synced-toggle');
  if (btn) btn.classList.toggle('active', _lyricsSyncedMode);
  _updateSyncOffsetDisplay();
  if (_musicLyricsCurrentPath && _musicLyricsPanelOpen) {
    const entry = _musicLyricsCache.get(_musicLyricsCurrentPath);
    const body  = document.getElementById('music-lyrics-body');
    if (entry && entry !== 'loading' && entry.found && body)
      _renderLyricsBody(body, entry);
  }
}

/**
 * Adjusts the sync offset by `delta` seconds (±1 normally, ±5 with Shift).
 * @param {MouseEvent} e
 * @param {number} delta  base step (+1 or -1)
 */
function adjustLyricsSyncOffset(e, delta) {
  const step = (e && e.shiftKey) ? delta * 5 : delta;
  _lrcSyncOffset = Math.round((_lrcSyncOffset + step) * 10) / 10;
  _lrcCurrentIdx = -1;  // force re-render on next tick
  _updateSyncOffsetDisplay();
  _lyricOffsetSave(_lrcOffsetCurrentKey, _lrcSyncOffset);
}

/** Shows / hides the sync-offset group and refreshes the offset readout. */
function _updateSyncOffsetDisplay() {
  const group = document.getElementById('lyrics-sync-offset-group');
  const display = document.getElementById('lyrics-sync-offset-display');
  const visible = _lyricsSyncedMode && !!_lrcLines;
  if (group) group.style.display = visible ? '' : 'none';
  if (display) {
    const v = _lrcSyncOffset;
    display.textContent = (v === 0) ? '0s' : ((v > 0 ? '+' : '') + v.toFixed(v % 1 === 0 ? 0 : 1) + 's');
    display.classList.toggle('lyrics-sync-offset-nonzero', v !== 0);
  }
}

/** Copy current lyrics to clipboard. */
async function copyLyricsToClipboard() {
  if (!_musicLyricsCurrentPath) return;
  const entry = _musicLyricsCache.get(_musicLyricsCurrentPath);
  if (!entry || entry === 'loading' || !entry.found) return;
  const text = entry.lyrics || (entry.syncedLyrics
    ? _parseLrc(entry.syncedLyrics).map(l => l.text).filter(Boolean).join('\n')
    : '');
  if (!text) return;
  try {
    await navigator.clipboard.writeText(text);
    const btn = document.getElementById('lyrics-btn-copy');
    if (btn) { const orig = btn.textContent; btn.textContent = '✓'; setTimeout(() => { btn.textContent = orig; }, 1200); }
  } catch (_) {}
}

/** Clears all server-side and client-side lyrics cache so stale "no lyrics" results are discarded. */
async function clearLyricsCache() {
  const btn = document.getElementById('lyrics-btn-clear-cache');
  if (btn) { btn.disabled = true; btn.textContent = '…'; }
  try {
    await fetch('/api/music/lyrics/clear-cache', { method: 'POST' });
    _musicLyricsCache.clear();
    _lrcLines = null;
    _lrcCurrentIdx = -1;
    if (_musicLyricsCurrentPath) {
      const body = document.getElementById('music-lyrics-body');
      if (body) body.textContent = 'Loading lyrics\u2026';
      const src = document.getElementById('music-lyrics-source');
      if (src) src.innerHTML = '';
      const artist = (document.getElementById('music-artist-label') || {}).textContent || '';
      const title  = (document.getElementById('music-lyrics-title') || {}).textContent || '';
      await _fetchMusicLyrics(_musicLyricsCurrentPath, artist, title);
    }
  } catch (e) {
    console.warn('clearLyricsCache error', e);
  } finally {
    if (btn) { btn.disabled = false; btn.textContent = '\uD83D\uDDD1'; }
  }
}

/** Re-fetch lyrics with the user-supplied title override (search-again input). */
async function retryLyricsSearch() {
  const input = document.getElementById('music-lyrics-search-input');
  if (!input || !_musicLyricsCurrentPath) return;
  const newTitle = input.value.trim();
  if (!newTitle) return;
  // Bust the cache for this path so it re-fetches
  _musicLyricsCache.delete(_musicLyricsCurrentPath);
  _lrcLines = null;
  _lrcCurrentIdx = -1;
  const path   = _musicLyricsCurrentPath;
  const artist = (document.getElementById('music-artist-label') || {}).textContent || '';
  // Update search title display immediately
  const titleEl = document.getElementById('music-lyrics-title');
  if (titleEl) titleEl.textContent = newTitle;
  if (_musicLyricsPanelOpen) {
    const body = document.getElementById('music-lyrics-body');
    if (body) body.textContent = 'Loading lyrics\u2026';
    const src = document.getElementById('music-lyrics-source');
    if (src) src.innerHTML = '';
  }
  await _fetchMusicLyrics(path, artist, newTitle);
}

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
      if (path)   params.set('path', path);
      const res  = await fetch('/api/music/lyrics?' + params.toString());
    const data = await res.json();
    _musicLyricsCache.set(path, { ...data, _searchTitle: cleanTitle });
  } catch (_e) {
    _musicLyricsCache.set(path, { found: false, _searchTitle: cleanTitle });
  }
  if (path === _musicLyricsCurrentPath) _applyMusicLyricsState(path);
}

function _applyMusicLyricsState(path) {
  const btn        = document.getElementById('music-btn-lyrics');
  const panel      = document.getElementById('music-lyrics-panel');
  const body       = document.getElementById('music-lyrics-body');
  const src        = document.getElementById('music-lyrics-source');
  const titleEl    = document.getElementById('music-lyrics-title');
  if (!btn) return;

  if (!path) {
    btn.style.display = 'none';
    if (titleEl) titleEl.textContent = '';
    _closeLyricsPanel();
    return;
  }

  btn.style.display = '';
  const entry = _musicLyricsCache.get(path);

  // Show the title that was searched for (or a fallback from the path)
  const searchTitle = (entry && entry !== 'loading' && entry._searchTitle)
    ? entry._searchTitle
    : ((path.split(/[\/\\]/).pop() || '').replace(/\.[^.]+$/, '').replace(/^\d{1,3}[\s._-]+/, '').trim());
  if (titleEl) titleEl.textContent = searchTitle || '';

  // Populate search input with current search title (pre-filled for easy retry)
  const searchInput = document.getElementById('music-lyrics-search-input');
  if (searchInput && !searchInput.value) searchInput.value = searchTitle || '';

  if (!entry || entry === 'loading') {
    btn.textContent = '\u23F3 Lyrics';
    btn.disabled = true;
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
    _closeLyricsPanelVisual();
    return;
  }

  btn.textContent = '\uD83C\uDFA4 Lyrics';
  btn.classList.remove('lyrics-not-found');

  // Show/hide synced toggle based on whether both modes are available
  const syncToggle = document.getElementById('lyrics-btn-synced-toggle');
  if (syncToggle) {
    const hasBoth = !!(entry.syncedLyrics && entry.lyrics);
    syncToggle.style.display = hasBoth ? '' : 'none';
    syncToggle.classList.toggle('active', _lyricsSyncedMode);
    syncToggle.textContent = _lyricsSyncedMode ? '\u25B6 Sync' : '\u25A4 Plain';
  }

  if (_musicLyricsPanelOpen) {
    _openLyricsPanel();
    _renderLyricsBody(body, entry);
    if (src) src.innerHTML = _buildLyricsSourceHtml(entry);
  }
}

function _openLyricsPanel() {
  const panel = document.getElementById('music-lyrics-panel');
  const btn   = document.getElementById('music-btn-lyrics');
  if (panel) { panel.classList.add('open'); panel.style.display = 'flex'; }
  if (btn)   btn.classList.add('active');
  _musicLyricsPanelOpen = true;
  _applyLyricsFontSize();
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
        if (body) _renderLyricsBody(body, entry);
        if (src)  src.innerHTML = _buildLyricsSourceHtml(entry);
      }
    } else {
      _openLyricsPanel();
    }
  }
}

// -- LRC synced-lyrics helpers ------------------------------------------------

/** Parses a standard LRC string into [{time, text}] sorted by time. */
function _parseLrc(lrc) {
  const lines = [];
  for (const raw of lrc.split('\n')) {
    const m = raw.match(/^\[(\d{2}):(\d{2}(?:\.\d+)?)\](.*)$/);
    if (!m) continue;
    const t = parseInt(m[1], 10) * 60 + parseFloat(m[2]);
    lines.push({ time: t, text: m[3].trim() });
  }
  return lines.sort((a, b) => a.time - b.time);
}

/**
 * Parses LRC extended format with per-word timestamps.
 * e.g. "[00:01.00]<00:01.00>word <00:01.40>word2"
 * Returns [{time, words:[{time,text}]}] or null if no word tags found.
 */
function _parseLrcExtended(lrc) {
  const lines = [];
  let hasWordTags = false;
  for (const raw of lrc.split('\n')) {
    const lineM = raw.match(/^\[(\d{2}):(\d{2}(?:\.\d+)?)\](.*)/);
    if (!lineM) continue;
    const lineTime = parseInt(lineM[1], 10) * 60 + parseFloat(lineM[2]);
    const rest = lineM[3];
    const words = [];
    const wordRe = /<(\d{2}):(\d{2}(?:\.\d+)?)>([^<]*)/g;
    let wm;
    while ((wm = wordRe.exec(rest)) !== null) {
      hasWordTags = true;
      words.push({ time: parseInt(wm[1], 10) * 60 + parseFloat(wm[2]), text: wm[3] });
    }
    if (words.length > 0)
      lines.push({ time: lineTime, words });
    else
      lines.push({ time: lineTime, words: null, text: rest.replace(/<[^>]+>/g, '').trim() });
  }
  return hasWordTags ? lines.sort((a, b) => a.time - b.time) : null;
}

/** Renders lyrics into the body element.
 *  If synced mode and syncedLyrics present: LRC lines with click-to-seek.
 *  If synced mode and extended word tags found: per-word karaoke spans.
 *  Otherwise: plain text. */
function _renderLyricsBody(body, entry) {
  if (!body) return;
  if (_lyricsSyncedMode && entry && entry.syncedLyrics) {
    // Try extended karaoke parsing first
    const extLines = _parseLrcExtended(entry.syncedLyrics);
    if (extLines) {
      body.innerHTML = extLines.map((l, i) => {
        const wordsHtml = l.words
          ? l.words.map(w => `<span class="lrc-word" data-time="${w.time}">${w.text}</span>`).join('')
          : (l.text || '&nbsp;');
        return `<div class="lrc-line" data-idx="${i}" data-time="${l.time}">${wordsHtml}</div>`;
      }).join('');
      _lrcLines = extLines.map(l => ({
        time: l.time,
        text: l.words ? l.words.map(w => w.text).join('') : (l.text || ''),
        words: l.words || null
      }));
    } else {
      const lines = _parseLrc(entry.syncedLyrics);
      body.innerHTML = lines.map((l, i) =>
        `<div class="lrc-line" data-idx="${i}" data-time="${l.time}">${l.text || '&nbsp;'}</div>`
      ).join('');
      _lrcLines = lines;
    }
    _lrcCurrentIdx = -1;

    // Click-to-seek: clicking a lyric line seeks the track
    body.querySelectorAll('.lrc-line').forEach(el => {
      el.addEventListener('click', () => {
        const t = parseFloat(el.dataset.time);
        if (!isNaN(t)) _seekToLrcTime(t);
      });
    });
    // Show sync offset controls now that synced lyrics are rendered
    _updateSyncOffsetDisplay();
  } else {
    body.textContent = (entry && entry.lyrics) || '';
    _lrcLines = null;
    _lrcCurrentIdx = -1;
  }
  }

  /** Seeks the current music track to the given time (seconds). */
function _seekToLrcTime(t) {
  if (typeof isPlayLocal !== 'undefined' && isPlayLocal() &&
      typeof _localMediaType !== 'undefined' && _localMediaType === 'Music') {
    const a = typeof _getLocalAudio !== 'undefined' ? _getLocalAudio() : null;
    if (a) { a.currentTime = t; return; }
  }
  fetch('/api/music/seek?pos=' + t.toFixed(2), { method: 'POST' }).catch(() => {});
}

/** Builds source attribution HTML. */
function _buildLyricsSourceHtml(entry) {
  if (!entry) return '';
  if (entry.source === 'lrclib') return '<span class="lyrics-source-label">Lyrics via LRCLib</span>';
  if (entry.geniusUrl)
    return `<a class="lyrics-genius-link" href="${entry.geniusUrl}" target="_blank" rel="noopener">View on Genius \u2197</a>`;
  return '';
}

// Synced LRC tick state
let _lrcLines = null;        // parsed [{time,text,words?}] for current track, or null
let _lrcCurrentIdx = -1;     // last highlighted index
let _lrcSyncOffset = 0;      // user-applied sync correction in seconds (+ve = lyrics earlier, -ve = later)

// Per-track lyric offset persistence -----------------------------------------
// Offsets are stored in localStorage as a JSON map {[key]: seconds} AND
// mirrored to the server so other clients can load them.
const _LYRIC_OFFSETS_LS_KEY = 'remotePlayLyricOffsets';
let _lrcOffsetCurrentKey = null;  // key for the currently playing track

function _lyricOffsetKey(artist, title) {
  if (!artist && !title) return null;
  return ((artist || '') + '|' + (title || '')).toLowerCase();
}

function _lyricOffsetsLoad() {
  try {
    const raw = localStorage.getItem(_LYRIC_OFFSETS_LS_KEY);
    return raw ? JSON.parse(raw) : {};
  } catch (_) { return {}; }
}

function _lyricOffsetGet(key) {
  if (!key) return 0;
  const map = _lyricOffsetsLoad();
  return typeof map[key] === 'number' ? map[key] : 0;
}

function _lyricOffsetSave(key, offsetSeconds) {
  if (!key) return;
  const map = _lyricOffsetsLoad();
  if (offsetSeconds === 0) {
    delete map[key];
  } else {
    map[key] = offsetSeconds;
  }
  try { localStorage.setItem(_LYRIC_OFFSETS_LS_KEY, JSON.stringify(map)); } catch (_) {}
  // Mirror to server (fire-and-forget) so other clients/peers can sync
  _lyricOffsetsPushToServer(map);
}

function _lyricOffsetsPushToServer(map) {
  try {
    fetch('/api/music/lyrics/offsets', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(map),
    }).catch(() => {});
  } catch (_) {}
}

// On first load, merge server-stored offsets into localStorage so a fresh
// browser or a newly connected client picks up offsets saved by another tab.
async function _lyricOffsetsPullFromServer() {
  try {
    const res = await fetch('/api/music/lyrics/offsets');
    if (!res.ok) return;
    const serverMap = await res.json();
    if (typeof serverMap !== 'object' || serverMap === null) return;
    const localMap = _lyricOffsetsLoad();
    let changed = false;
    for (const [k, v] of Object.entries(serverMap)) {
      if (typeof v === 'number' && localMap[k] !== v) {
        localMap[k] = v;
        changed = true;
      }
    }
    if (changed) {
      try { localStorage.setItem(_LYRIC_OFFSETS_LS_KEY, JSON.stringify(localMap)); } catch (_) {}
      // If the currently-playing track is affected, update the live offset
      if (_lrcOffsetCurrentKey && localMap[_lrcOffsetCurrentKey] !== undefined) {
        _lrcSyncOffset = localMap[_lrcOffsetCurrentKey];
        _updateSyncOffsetDisplay();
      }
    }
  } catch (_) {}
}
// Initial pull on page load
_lyricOffsetsPullFromServer();
// Periodic pull every 30 s — keeps all open tabs/clients in sync automatically
setInterval(_lyricOffsetsPullFromServer, 30_000);

/** Derive a clean song title the same way _fetchMusicLyrics does. */
function _cleanTrackTitle(track) {
  let t = track.name || '';
  if (!t || t.includes('/') || t.includes('\\')) {
    t = (track.path.split(/[\/\\]/).pop() || '').replace(/\.[^.]+$/, '');
  }
  return t.replace(/^\d{1,3}[\s._-]+/, '').trim();
}

/** Silently pre-fetches lyrics for ALL remaining queue items so they are
 *  already cached before each track starts playing (no hourglass delay).
 *  Items are staggered 1.2 s apart so the current track's own fetch has priority.
 *  Already-cached tracks are skipped immediately. */
function _prefetchNextTrackLyrics() {
  // Collect remaining queue items after the current position
  const candidates = [];

  if (window._musicQueue && window._musicQueue.length > 0 && window._musicQueuePos >= 0) {
    // Queue mode: prefetch every item after the current position
    for (let i = window._musicQueuePos + 1; i < window._musicQueue.length; i++) {
      candidates.push(window._musicQueue[i]);
    }
  } else {
    // Folder/tracklist mode: only prefetch the immediate next track
    const next = _musicPeekNext();
    if (next) candidates.push(next);
  }

  if (candidates.length === 0) return;

  candidates.forEach((track, idx) => {
    const delayMs = 800 + idx * 1200; // stagger: 0.8s, 2.0s, 3.2s, …
    setTimeout(() => {
      if (!track || !track.path) return;
      if (_musicLyricsCache.has(track.path)) return; // already cached
      const artist = track.artist || '';
      const cleanTitle = _cleanTrackTitle(track);
      if (!cleanTitle) return;
      _fetchMusicLyrics(track.path, artist, cleanTitle);
    }, delayMs);
  });
}

/** Called from updateMusicBar with current position (seconds). Highlights the active LRC line. */
function _lrcTick(posSeconds) {
  if (!_lrcLines) return;
  const body = document.getElementById('music-lyrics-body');

  // Apply user sync correction: positive offset means lyrics timestamps are shifted earlier
  const adjPos = posSeconds + _lrcSyncOffset;

  // Find the last line whose time <= adjPos
  let idx = -1;
  for (let i = 0; i < _lrcLines.length; i++) {
    if (_lrcLines[i].time <= adjPos) idx = i;
    else break;
  }
  if (idx === _lrcCurrentIdx) {
    // Still same line — update per-word karaoke highlighting if applicable
    if (_musicLyricsPanelOpen && body) _tickKaraokeWords(body, adjPos, idx);
    return;
  }
  _lrcCurrentIdx = idx;


  if (_musicLyricsPanelOpen && body) {
    const els = body.querySelectorAll('.lrc-line');
    els.forEach((el, i) => {
      el.classList.toggle('lrc-line-active', i === idx);
      el.classList.toggle('lrc-line-past', i < idx);
      // future lines: neither past nor active — reset
      if (i > idx) { el.classList.remove('lrc-line-past'); }
    });

    // Scroll so the active line sits in the lower third (2 lines of look-ahead visible above)
    if (idx >= 0 && els[idx]) {
      const lookAheadIdx = Math.max(0, idx - 2);
      if (els[lookAheadIdx]) {
        els[lookAheadIdx].scrollIntoView({ block: 'start', behavior: 'smooth' });
      } else {
        els[idx].scrollIntoView({ block: 'center', behavior: 'smooth' });
      }
    }

    _tickKaraokeWords(body, adjPos, idx);
  }
}

/** Highlights per-word karaoke spans within the active line. */
function _tickKaraokeWords(body, posSeconds, lineIdx) {
  if (lineIdx < 0 || !_lrcLines[lineIdx] || !_lrcLines[lineIdx].words) return;
  const lineEl = body.querySelector(`.lrc-line[data-idx="${lineIdx}"]`);
  if (!lineEl) return;
  lineEl.querySelectorAll('.lrc-word').forEach(span => {
    const wt = parseFloat(span.dataset.time);
    span.classList.toggle('lrc-word-active', !isNaN(wt) && posSeconds >= wt);
  });
}

// -------------------------------------------------------------------------------

function _onMusicTrackChangedLyrics(path, artist, title) {
  _lrcLines = null;
  _lrcCurrentIdx = -1;
  // Restore any previously saved offset for this track (default 0 for new tracks)
  _lrcOffsetCurrentKey = _lyricOffsetKey(artist, title);
  _lrcSyncOffset = _lyricOffsetGet(_lrcOffsetCurrentKey);
  _musicLyricsCurrentPath = path;
  const btn = document.getElementById('music-btn-lyrics');

  // Reset search input for new track
  const searchInput = document.getElementById('music-lyrics-search-input');
  if (searchInput) searchInput.value = '';

  // Scroll lyrics body back to top for the new track
  const lyricsBody = document.getElementById('music-lyrics-body');
  if (lyricsBody) lyricsBody.scrollTop = 0;

  // Hide synced toggle and sync-offset controls until we know synced lyrics are available
  const syncToggle = document.getElementById('lyrics-btn-synced-toggle');
  if (syncToggle) syncToggle.style.display = 'none';
  _updateSyncOffsetDisplay();

  if (!path) {
    if (btn) { btn.style.display = 'none'; btn.disabled = false; btn.classList.remove('active', 'lyrics-not-found'); }
    _closeLyricsPanel();
    return;
  }

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

  // Pre-fetch lyrics for the next track in the background so they are ready when needed
  _prefetchNextTrackLyrics();
}

// Restore persisted font size preference on load
try {
  const saved = localStorage.getItem('lyricsFontStep');
  if (saved !== null) { _lyricsFontSizeStep = parseInt(saved, 10) || 0; }
} catch (_) {}


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
async function _setMusicShuffleEnabled(enabled) {
  const next = !!enabled;
  if (_musicShuffle === next) {
    if (_musicShuffle && !_musicShuffleOrder.length) _buildShuffleOrder();
    _refreshMusicNavLabels();
    return;
  }

  _musicShuffle = next;
  window._musicNextShuffleQueuePick = null; // recompute for new shuffle state
  const btn = document.getElementById('music-btn-shuffle');
  if (btn) btn.classList.toggle('active', _musicShuffle);

  if (_musicShuffle) {
    // Load ALL tracks in the current folder before building shuffle order so
    // the shuffle pool is the full folder, not just the first page.
    await _loadAllMusicTracksForShuffle();
    _buildShuffleOrder();
  }

  // Refresh nav-label display without touching server state
  _refreshMusicNavLabels();
}

function toggleMusicShuffle() {
  _setMusicShuffleEnabled(!_musicShuffle).catch(() => {});
}
async function resetM3uCache() {
  const btn = document.getElementById('mcb-reset-m3u-btn');
  if (btn) { btn.disabled = true; btn.textContent = '⏳ Resetting…'; }
  try {
    const res = await fetch('/api/music/reset-m3u-cache', { method: 'POST' });
    if (!res.ok) { setStatus('Reset M3U cache failed: ' + res.status); return; }
    setStatus('M3U cache reset — reloading…');
    await browseMusic(currentMusicFolder);
  } catch (e) {
    setStatus('Reset M3U cache error: ' + e);
  } finally {
    if (btn) { btn.disabled = false; btn.textContent = '↻ Reset M3u Cache'; }
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
  // Repeat-one: restart the current queue item (if queue active) or current track-list item
  if (_musicRepeat === 'one') {
    if (window._musicQueue && window._musicQueue.length && window._musicQueuePos >= 0) {
      return window._musicQueue[window._musicQueuePos] || null;
    }
    if (musicTrackIndex >= 0) return musicTrackList[musicTrackIndex] || null;
  }
  // Advance through the explicit queue (cursor-based, non-destructive)
  if (window._musicQueue && window._musicQueue.length) {
    // Shuffle mode: pick a random queue index that hasn't been played yet
    if (_musicShuffle) {
      const visited = window._musicQueueVisited || (window._musicQueueVisited = new Set());
      visited.add(window._musicQueuePos);
      const unvisited = window._musicQueue.map((_, i) => i).filter(i => !visited.has(i));
      if (unvisited.length > 0) {
        // Use the pre-cached pick from _musicPeekNext so what was shown in the label is what plays.
        let pick = window._musicNextShuffleQueuePick;
        if (pick == null || !unvisited.includes(pick)) {
          pick = unvisited[Math.floor(Math.random() * unvisited.length)];
        }
        window._musicNextShuffleQueuePick = null; // consumed; recomputed on next peek
        window._musicQueuePos = pick;
        _updateMusicQueuePendingBar();
        return window._musicQueue[pick];
      }
      // All visited — wrap if repeat=all
      if (_musicRepeat === 'all') {
        window._musicQueueVisited = new Set();
        window._musicNextShuffleQueuePick = null;
        const pick = Math.floor(Math.random() * window._musicQueue.length);
        window._musicQueuePos = pick;
        _updateMusicQueuePendingBar();
        return window._musicQueue[pick];
      }
      window._musicNextShuffleQueuePick = null;
      window._musicQueuePos = window._musicQueue.length;
      _updateMusicQueuePendingBar();
      return null;
    }
    const nextPos = window._musicQueuePos + 1;
    if (nextPos < window._musicQueue.length) {
      window._musicQueuePos = nextPos;
      _updateMusicQueuePendingBar();
      return window._musicQueue[nextPos];
    }
    // Queue exhausted — wrap if repeat=all, otherwise fall through to track list or stop
    if (_musicRepeat === 'all') {
      window._musicQueuePos = 0;
      _updateMusicQueuePendingBar();
      return window._musicQueue[0];
    }
    window._musicQueuePos = window._musicQueue.length; // mark past end
    _updateMusicQueuePendingBar();
    if (!musicTrackList.length) return null;
    // continue to track-list logic below
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
  // Queue takes priority — peek the next item without consuming it
  if (window._musicQueue && window._musicQueue.length > 0) {
    // Repeat-one: the 'next' is the same current item (it will restart)
    if (_musicRepeat === 'one' && window._musicQueuePos >= 0) return window._musicQueue[window._musicQueuePos] || null;
    if (_musicShuffle) {
      // Mirror shuffle+queue logic from _musicNextTrack: pick a random unvisited index.
      // Cache the pick so the label and actual playback agree on the same track.
      const visited = new Set(window._musicQueueVisited || []);
      if (window._musicQueuePos >= 0) visited.add(window._musicQueuePos);
      const unvisited = window._musicQueue.map((_, i) => i).filter(i => !visited.has(i));
      if (unvisited.length > 0) {
        if (window._musicNextShuffleQueuePick != null && unvisited.includes(window._musicNextShuffleQueuePick)) {
          return window._musicQueue[window._musicNextShuffleQueuePick] || null;
        }
        const pick = unvisited[Math.floor(Math.random() * unvisited.length)];
        window._musicNextShuffleQueuePick = pick;
        return window._musicQueue[pick] || null;
      }
      if (_musicRepeat === 'all') return window._musicQueue[0] || null;
      return null;
    }
    const nextPos = window._musicQueuePos + 1;
    if (nextPos < window._musicQueue.length) return window._musicQueue[nextPos];
    // When repeat=all, the next after the last is the first
    if (_musicRepeat === 'all') return window._musicQueue[0];
    return null; // queue is exhausted
  }
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

function _musicResetQueueTraversalState() {
  window._musicNextShuffleQueuePick = null;
  const queue = window._musicQueue || [];
  const cur = window._musicQueuePos;
  window._musicQueueVisited = (cur >= 0 && cur < queue.length) ? new Set([cur]) : new Set();
}

function _musicQueueRemove(idx) {
  if (!window._musicQueue || idx < 0 || idx >= window._musicQueue.length) return;
  // If removing the currently-playing item or one before it, adjust the cursor
  if (idx <= window._musicQueuePos) {
    window._musicQueuePos = Math.max(-1, window._musicQueuePos - 1);
  }
  window._musicQueue.splice(idx, 1);
  _musicResetQueueTraversalState();
  if (currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
  _renderMusicQueuePeek();
}
function _musicQueueClear() {
  window._musicQueue = [];
  window._musicQueuePos = -1;
  _musicResetQueueTraversalState();
  if (currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
  _renderMusicQueuePeek();
}
function _renderMusicQueuePeek() {
  _updateMusicQueuePendingBar();
  const card = document.getElementById('music-queue-peek-card');
  const list = document.getElementById('music-queue-peek-list');
  if (!card || !list) return;

  const queueItems = window._musicQueue || [];
  const queuePos   = (window._musicQueuePos != null) ? window._musicQueuePos : -1;

  // Up Next from track list (only when no explicit queue is active)
  const upNextItems = [];
  if (!queueItems.length) {
    const peekCount = 3;
    for (let i = 1; i <= peekCount; i++) {
      let t = null, listIdx = -1;
      if (_musicShuffle && _musicShuffleOrder.length) {
        const p = (_musicShufflePos + i);
        if (p < _musicShuffleOrder.length) { listIdx = _musicShuffleOrder[p]; t = musicTrackList[listIdx] || null; }
      } else {
        listIdx = musicTrackIndex + i;
        if (listIdx < musicTrackList.length) t = musicTrackList[listIdx] || null;
        else if (_musicRepeat === 'all' && musicTrackList.length) { listIdx = listIdx % musicTrackList.length; t = musicTrackList[listIdx] || null; }
      }
      if (t) upNextItems.push({ t, listIdx });
    }
  }

  if (!queueItems.length && !upNextItems.length) { card.style.display = 'none'; return; }
  card.style.display = '';

  list._queuePeekItems = upNextItems;
  list._queueItems = queueItems; // live reference

  let html = '';

  if (queueItems.length) {
    const esc = (s) => (s || '').replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;');
    html += '<div class="music-queue-section-header">'
      + '<span>Queue (' + queueItems.length + ')</span>'
      + '<button class="music-queue-clear-btn" data-action="clear-all" title="Clear queue">&#10005; Clear all</button>'
      + '</div>';
    html += queueItems.map(function(item, i) {
      const isPlaying = (i === queuePos);
      const isPlayed  = (i < queuePos);
      let cls = 'music-queue-peek-item music-queue-peek-queued';
      if (isPlaying) cls += ' music-queue-peek-playing';
      else if (isPlayed) cls += ' music-queue-peek-played';
      const nowIcon = isPlaying ? '<span class="music-queue-now-icon">&#9654;</span>' : '';
      return '<div class="' + cls + '" data-qi="' + i + '" title="' + esc(item.name || item.path) + '">'
        + '<span class="music-queue-peek-num">' + (i + 1) + '</span>'
        + '<span class="music-queue-peek-name">' + nowIcon + esc(item.name || item.path) + '</span>'
        + '<button class="music-queue-remove-btn" data-action="remove" data-qi="' + i + '" title="Remove from queue">&#10005;</button>'
        + '</div>';
    }).join('');
  }

  if (upNextItems.length && _musicAutoPlay && !queueItems.length) {
    html += '<div class="music-queue-section-header"><span>Up Next</span></div>';
    html += upNextItems.map(function(obj, i) {
      const t = obj.t; const listIdx = obj.listIdx;
      const num = listIdx >= 0 ? listIdx + 1 : '?';
      const dur = t.durationSec ? fmtSec(t.durationSec) : '';
      const esc = (s) => (s || '').replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;');
      return '<div class="music-queue-peek-item" data-qi-next="' + i + '" title="' + esc(t.name) + '">'
        + '<span class="music-queue-peek-num">' + num + '</span>'
        + '<span class="music-queue-peek-name">' + esc(t.name) + '</span>'
        + (dur ? '<span class="music-queue-peek-dur">' + dur + '</span>' : '')
        + '</div>';
    }).join('');
  }

  list.innerHTML = html;

  const playingEl = list.querySelector('.music-queue-peek-playing');
  if (playingEl && list.scrollHeight > list.clientHeight) {
    const elTop = playingEl.offsetTop;
    const elBottom = elTop + playingEl.offsetHeight;
    if (elTop < list.scrollTop) {
      list.scrollTop = elTop;
    } else if (elBottom > list.scrollTop + list.clientHeight) {
      list.scrollTop = elBottom - list.clientHeight;
    }
  }

  list.onclick = (e) => {
    const btn = e.target.closest('[data-action]');
    if (btn) {
      e.stopPropagation();
      const action = btn.dataset.action;
      if (action === 'clear-all') { _musicQueueClear(); return; }
      if (action === 'remove')    { _musicQueueRemove(parseInt(btn.dataset.qi, 10)); return; }
    }
    const qRow = e.target.closest('[data-qi]');
    if (qRow && !qRow.dataset.qiNext) {
      const idx = parseInt(qRow.dataset.qi, 10);
      const item = (window._musicQueue || [])[idx];
      if (item) {
        window._musicQueuePos = idx;
        if (currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
        playMusic(item.path, item.name);
      }
      return;
    }
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
    if (currentMode !== 'music') {
      stopMusicPlaybackPoll();
      return;
    }

    let s;
    try {
      const res = await fetch('/api/music/status');
      if (!res.ok) return;
      s = await res.json();
    } catch (e) {
      return;
    }

    // Guard against in-flight race: mode may have changed while awaiting status.
    if (currentMode !== 'music') {
      stopMusicPlaybackPoll();
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
      if (musicIsPlaying && typeof applySavedMusicEffectsDefaults === 'function') {
        applySavedMusicEffectsDefaults().catch(() => {});
      }
      if (!musicIsPlaying) stopMusicPlaybackPoll();
      return;
    }

    const wasPlaying = musicIsPlaying;
    musicIsPlaying = s.isPlaying || s.isPaused;
    if (!wasPlaying && musicIsPlaying && typeof applySavedMusicEffectsDefaults === 'function') {
      applySavedMusicEffectsDefaults().catch(() => {});
    }

    // Detect a server-initiated track advance (the server auto-played the next
    // track while the browser was open). Sync browser state and re-register the
    // new lookahead so the chain keeps going.
    if (s.currentPath && s.currentPath !== musicCurrentPath && s.isPlaying) {
      musicCurrentPath = s.currentPath;
      // Keep queue cursor synchronized with the server-advanced track.
      // In shuffle mode this may jump to any unvisited queue index, not just +1.
      if (window._musicQueue && window._musicQueue.length > 0) {
        const currentKey = _normalizeMusicPath(s.currentPath);
        const byPath = window._musicQueue.findIndex((q) => _normalizeMusicPath(q.path) === currentKey);
        if (byPath >= 0) {
          window._musicQueuePos = byPath;
          if (_musicShuffle) {
            const visited = window._musicQueueVisited || (window._musicQueueVisited = new Set());
            visited.add(byPath);
            window._musicNextShuffleQueuePick = null;
          }
          _updateMusicQueuePendingBar();
          _renderMusicQueuePeek();
        }
      }
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

    // Enrich server status with track-list metadata for the player bar.
    // Look in musicTrackList first, then fall back to the active queue entry.
    const trackMeta = _findTrackMetaForStatus({ ...s, currentPath: musicCurrentPath || s.currentPath || '' });
    if (trackMeta) {
      const genericAlbum = (v) => {
        const n = String(v || '').trim().toLowerCase();
        return n === 'all' || n === 'all songs' || n === 'albums' || n === 'singles';
      };

      s.artist    = s.artist    || trackMeta.artist    || '';
      if (!s.album || genericAlbum(s.album)) {
        s.album = trackMeta.album || s.album || '';
      }
      s.genre     = s.genre     || trackMeta.genre     || '';
      s.year      = s.year      || trackMeta.year      || null;
      s.ext       = s.ext       || trackMeta.ext       || '';
      s.durationSec = s.durationSec || trackMeta.durationSec || 0;
      s.hasCover  = s.hasCover  ?? trackMeta.hasCover;
      s.coverPath = musicCurrentPath;
    } else if (musicCurrentPath) {
      // Even without local track metadata, ensure coverPath is set for art lookup
      s.coverPath = s.coverPath || musicCurrentPath;
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
    if (currentMode === 'music' && !isPlayLocal() && !s.isPlaying && !s.isPaused && wasPlaying && musicCurrentPath && !s.currentPath) {
      musicCurrentPath = null;
      const hasQueue = window._musicQueue && window._musicQueue.length > 0;
      if (hasQueue || _musicAutoPlay || _musicShuffle || _musicRepeat !== 'none') {
        const nextT = _musicNextTrack();
        if (nextT) {
          await playMusic(nextT.path, nextT.name);
          // playMusic restarts the poll; skip the stop-check below
          return;
        }
      }
    }

    // Stop polling only when truly idle (nothing playing and we didn't just advance)
    if (!s.isPlaying && !s.isPaused && !s.currentPath && !musicCurrentPath) {
      stopMusicPlaybackPoll();
      if (!playingPath) releaseWakeLock();
    }
  }, 1500);
}

function stopMusicPlaybackPoll() {
  if (musicPlaybackPollTimer) {
    clearInterval(musicPlaybackPollTimer);
    musicPlaybackPollTimer = null;
  }
}

async function _stopAllPlaybackForNewStart() {
  if (typeof closeLocalPlayerForNonLocalPlayback === 'function') {
    await closeLocalPlayerForNonLocalPlayback();
  }

  stopMusicPlaybackPoll();
  if (typeof stopMusicStatusPoll === 'function') stopMusicStatusPoll();
  if (typeof stopRadioStatusPoll === 'function') stopRadioStatusPoll();

  musicIsPlaying = false;
  musicCurrentPath = null;
  if (typeof _onMusicTrackChangedLyrics === 'function') _onMusicTrackChangedLyrics(null, '', '');
  _clearMusicPlaybackVisuals();

  const musicBar = document.getElementById('music-player-bar');
  if (musicBar) musicBar.style.display = 'none';
  document.body.classList.remove('music-player-docked');
  document.body.classList.remove('radio-player-docked');
  if (typeof setPlayerBarVisible === 'function') setPlayerBarVisible(false);

  await Promise.allSettled([
    fetch('/api/stop'),
    fetch('/api/music/stop'),
    fetch('/api/music/queue-next'),
    fetch('/api/radio/stop', { method: 'POST' })
  ]);
}

function switchMode(mode, skipInitialBrowse = false) {
  console.log('[BOOTSTRAP] switchMode called: mode =', mode, ', skipInitialBrowse =', skipInitialBrowse);
  // Domain switching is navigation-only; keep active playback running.
  const leavingVideo = currentMode === 'video' && mode !== 'video';
  const leavingMusic = currentMode === 'music' && mode !== 'music';
  const leavingPodcasts = currentMode === 'podcasts' && mode !== 'podcasts';
  const leavingRadio = currentMode === 'radio' && mode !== 'radio';
  console.log('[BOOTSTRAP] Leaving: video =', leavingVideo, ', music =', leavingMusic, ', podcasts =', leavingPodcasts, ', radio =', leavingRadio);

  if (leavingMusic) {
    _cancelPendingPlaylistArtLoads();
    stopMusicPlaybackPoll();
  }
  if (typeof _hideSearchFilterBar === 'function') _hideSearchFilterBar();

  currentMode = mode;
  console.log('[BOOTSTRAP] currentMode updated to:', currentMode);
  localStorage.setItem('remotePlayMode', mode);
  if (typeof _syncUiRoute === 'function') _syncUiRoute(mode);
  // Remove music dock when leaving music mode
  if (mode !== 'music') document.body.classList.remove('music-player-docked');
  if (mode !== 'music') {
    const musicBar = document.getElementById('music-player-bar');
    if (musicBar) musicBar.style.display = 'none';
  }
  if (mode !== 'radio' && !window._radioIsPlaying) document.body.classList.remove('radio-player-docked');
  document.body.classList.toggle('radio-mode', mode === 'radio');
  ['video', 'music', 'podcasts', 'radio', 'live-tv'].forEach((m) => {
    const tab = document.getElementById('tab-' + m);
    const browser = document.getElementById(m === 'video' ? 'browser' : m + '-browser');
    const active = m === mode;
    if (tab) tab.classList.toggle('active', active);
    if (browser) browser.style.display = active ? '' : 'none';
  });
  // Radio secondary tab header removed; command-bar toggle is used instead.
  const videoSearch = document.getElementById('vcb-search');
  const musicSearch = document.getElementById('mcb-search');
  const podcastsViewToggle = document.getElementById('view-toggle-podcasts');
  if (podcastsViewToggle) podcastsViewToggle.style.display = 'none';
  // Always show the nav row; just switch which recent strip is active
  const navRow = document.getElementById('browse-nav-row');
  const vStrip = document.getElementById('video-recent-strip');
  const vClear = document.getElementById('video-recent-clear');
  const vPinned = document.getElementById('video-pinned-strip');
  const mStrip = document.getElementById('music-recent-strip');
  const mClear = document.getElementById('music-recent-clear');
  const mPinned = document.getElementById('music-pinned-strip');
  const rStrip = document.getElementById('radio-recent-strip');
  const rClear = document.getElementById('radio-recent-clear');
  const ltStrip = document.getElementById('live-tv-recent-strip');
  const ltClear = document.getElementById('live-tv-recent-clear');
  // navRow and video/music strips are managed by the status poll (_applyVideoLibraryAvailable /
  // _applyMusicLibraryAvailable) to avoid showing chrome when paths are invalid.
  // Hide them now; the status poll will restore them if paths are valid.
  if (navRow) navRow.style.display = mode === 'radio' || mode === 'live-tv' ? '' : 'none';
  if (vStrip) vStrip.style.display = 'none';
  if (vClear) vClear.style.display = 'none';
  if (vPinned) vPinned.style.display = 'none';
  if (mStrip) mStrip.style.display = 'none';
  if (mClear) mClear.style.display = 'none';
  if (mPinned) mPinned.style.display = 'none';
  if (rStrip) rStrip.style.display = mode === 'radio' ? '' : 'none';
  if (rClear) rClear.style.display = 'none';
  if (ltStrip) ltStrip.style.display = mode === 'live-tv' ? '' : 'none';
  if (ltClear) ltClear.style.display = 'none';
  if (mode === 'video') {
    if (musicSearch) musicSearch.value = '';
    stopMusicStatusPoll();
    stopRadioStatusPoll();
    const back = document.getElementById('back-button');
    if (back) back.onclick = goBack;
    // Hide all video chrome up-front; refreshLibraryStatus will reveal it only when paths are valid.
    _applyVideoCommandBar(false);
    const mcb = document.getElementById('music-command-bar');
    if (mcb) mcb.style.display = 'none';
    const rcb = document.getElementById('radio-command-bar');
    if (rcb) rcb.style.display = 'none';
    const ltcb = document.getElementById('live-tv-command-bar');
    if (ltcb) ltcb.style.display = 'none';
    const pcb = document.getElementById('podcasts-command-bar');
    if (pcb) pcb.style.display = 'none';
    refreshLibraryStatus();
    if (currentData) render(currentData);
    else if (skipInitialBrowse) setMusicHeaderForMode('video');
    else browse(null);
  } else if (mode === 'music') {
    if (typeof _applyVideoCommandBar === 'function') _applyVideoCommandBar(false);
    // Hide the video player bar — it must not show alongside the music player bar.
    if (typeof setPlayerBarVisible === 'function') setPlayerBarVisible(false);
    const rcb = document.getElementById('radio-command-bar');
    if (rcb) rcb.style.display = 'none';
    const ltcb = document.getElementById('live-tv-command-bar');
    if (ltcb) ltcb.style.display = 'none';
    const pcb = document.getElementById('podcasts-command-bar');
    if (pcb) pcb.style.display = 'none';
    const mcb = document.getElementById('music-command-bar');
    // Hide up-front; refreshMusicLibraryStatus will reveal it only when paths are valid.
    if (mcb) { mcb.style.display = 'none'; _syncMusicCommandBar(); }
    if (videoSearch) videoSearch.value = '';
    stopRadioStatusPoll();
    document.body.classList.remove('radio-player-docked');
    // clear any video scan-status left-overs
    const el = document.getElementById('scan-status');
    if (el) {
      el.textContent = '';
      el.className = '';
    }
    const badge = document.getElementById('indexing-badge');
    if (badge) badge.style.display = 'none';
    // Always poll once when entering music mode so page refresh can recover
    // currently playing/paused server state and restore the player bar.
    startMusicPlaybackPoll();
    _ensureMusicKeyboardNav();
    // Check whether music paths are valid; hides command bar / strips if not.
    if (typeof refreshMusicLibraryStatus === 'function') refreshMusicLibraryStatus();
    if (typeof renderMusicPinnedStrip === 'function') renderMusicPinnedStrip();
    if (typeof _updateMusicPinButton === 'function') _updateMusicPinButton();
    if (!currentMusicData && !skipInitialBrowse) {
      // Only restore a dynamic session if the user already browsed music this page lifetime.
      // On a fresh load (server restart / new tab) always go to root instead.
      const _sess = (() => { try { return JSON.parse(sessionStorage.getItem('rpdyn_session') || 'null'); } catch { return null; } })();
      const _browsedThisLoad = sessionStorage.getItem('rpdyn_browsed') === '1';
      if (_sess && _sess.encodedPath && _browsedThisLoad) _openDynamicFolder(_sess.encodedPath, _sess.name || '');
      else browseMusic(null);
    }
    else if (skipInitialBrowse && !currentMusicData) {
      // Bootstrap without data - load root
      browseMusic(null);
    }
    else if (currentMusicData) {
      renderMusicHeader(currentMusicData, false);
    }
  } else if (mode === 'podcasts') {
    if (typeof _applyVideoCommandBar === 'function') _applyVideoCommandBar(false);
    const mcb = document.getElementById('music-command-bar');
    if (mcb) mcb.style.display = 'none';
    const rcb = document.getElementById('radio-command-bar');
    if (rcb) rcb.style.display = 'none';
    const ltcb = document.getElementById('live-tv-command-bar');
    if (ltcb) ltcb.style.display = 'none';
    const pcb = document.getElementById('podcasts-command-bar');
    if (pcb) pcb.style.display = 'flex';
    if (videoSearch) videoSearch.value = '';
    if (musicSearch) musicSearch.value = '';
    const badge = document.getElementById('indexing-badge');
    if (badge) badge.style.display = 'none';
    stopMusicStatusPoll();
    stopRadioStatusPoll();
    setMusicHeaderForMode('podcasts');
    if (typeof podcastsInit === 'function') podcastsInit();
  } else if (mode === 'radio') {
    if (typeof _applyVideoCommandBar === 'function') _applyVideoCommandBar(false);
    const mcb = document.getElementById('music-command-bar');
    if (mcb) mcb.style.display = 'none';
    const pcb = document.getElementById('podcasts-command-bar');
    if (pcb) pcb.style.display = 'none';
    const rcb = document.getElementById('radio-command-bar');
    if (rcb) rcb.style.display = 'flex';
    const ltcb = document.getElementById('live-tv-command-bar');
    if (ltcb) ltcb.style.display = 'none';
    if (videoSearch) videoSearch.value = '';
    if (musicSearch) musicSearch.value = '';
    const badge = document.getElementById('indexing-badge');
    if (badge) badge.style.display = 'none';
    stopMusicStatusPoll();
    setMusicHeaderForMode('radio');
    radioInit();
  } else if (mode === 'live-tv') {
    if (typeof _applyVideoCommandBar === 'function') _applyVideoCommandBar(false);
    const mcb = document.getElementById('music-command-bar');
    if (mcb) mcb.style.display = 'none';
    const pcb = document.getElementById('podcasts-command-bar');
    if (pcb) pcb.style.display = 'none';
    const rcb = document.getElementById('radio-command-bar');
    if (rcb) rcb.style.display = 'none';
    const ltcb = document.getElementById('live-tv-command-bar');
    if (ltcb) {
      ltcb.style.display = 'flex';
      if (typeof _syncLiveTvCommandBar === 'function') _syncLiveTvCommandBar();
    }
    if (videoSearch) videoSearch.value = '';
    if (musicSearch) musicSearch.value = '';
    const badge = document.getElementById('indexing-badge');
    if (badge) badge.style.display = 'none';
    stopMusicStatusPoll();
    stopRadioStatusPoll();
    document.body.classList.remove('radio-player-docked');
    setMusicHeaderForMode('live-tv');
    if (typeof liveTvInit === 'function') liveTvInit();
  }
}

function setMusicHeaderForMode(mode) {
  const bc = document.getElementById('breadcrumb');
  const countText = document.getElementById('count-text');
  const scanStatus = document.getElementById('scan-status');
  const back = document.getElementById('back-button');
  if (back) back.style.display = 'none';
  if (bc) bc.innerHTML = '';
  if (countText) countText.textContent = '';
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
        '<a onclick="browseMusic(null)">&#8962; Music Root</a><span> &rsaquo; </span><span class="crumb-current">Search results</span>';
    } else {
      const root = data.musicRoot || '';
      const folder = data.folder || root;
      // When inside a linked folder tree, display the virtual path instead of the real resolved path.
      // Segments before the virtual link point are identical to real paths; segments at or after it
      // are mapped back to real paths for onclick handlers so navigation still works.
      const displayFolder = _musicLinkBase && folder && folder.toLowerCase().startsWith(_musicLinkBase.real.toLowerCase())
        ? _musicLinkBase.virtual + folder.slice(_musicLinkBase.real.length)
        : folder;
      let html = '<a onclick="browseMusic(null)">&#8962; Music Root</a>';
      if (displayFolder && root && displayFolder.toLowerCase() !== root.toLowerCase()) {
        // Build segments relative to music root
        const rel = displayFolder.startsWith(root) ? displayFolder.slice(root.length) : '';
        const sep = rel.indexOf('/') !== -1 ? '/' : '\\';
        const parts = rel.split(sep).filter((p) => p.length > 0);
        let displayCumulative = root;
        parts.forEach((part, i) => {
          displayCumulative += sep + part;
          // Map the display path back to the real path for the onclick handler.
          // Segments at or inside the virtual link point substitute the virtual prefix with the real one.
          const realForClick = _musicLinkBase && displayCumulative.toLowerCase().startsWith(_musicLinkBase.virtual.toLowerCase())
            ? _musicLinkBase.real + displayCumulative.slice(_musicLinkBase.virtual.length)
            : displayCumulative;
          if (i === parts.length - 1) {
            html += '<span> &rsaquo; </span><span class="crumb-current">' + esc(part) + '</span>';
          } else {
            html +=
              '<span> &rsaquo; </span><a onclick="browseMusic(\'' +
              jsStr(realForClick) +
              '\')">' +
              esc(part) +
              '</a>';
          }
        });
      }
      bc.innerHTML = html;
    }
  }

  if (typeof _setViewModeScope === 'function') {
    if (searching) {
      _setViewModeScope('music', 2, 'Search results');
    } else {
      const root = data.musicRoot || '';
      const folder = data.folder || root;
      const displayFolder = _musicLinkBase && folder && folder.toLowerCase().startsWith(_musicLinkBase.real.toLowerCase())
        ? _musicLinkBase.virtual + folder.slice(_musicLinkBase.real.length)
        : folder;
      const rel = displayFolder && root && displayFolder.toLowerCase() !== root.toLowerCase()
        ? (displayFolder.startsWith(root) ? displayFolder.slice(root.length) : '')
        : '';
      const sep = rel.indexOf('/') !== -1 ? '/' : '\\';
      const parts = rel.split(sep).filter((p) => p.length > 0);
      _setViewModeScope('music', parts.length + 1, parts.length ? parts[parts.length - 1] : 'Music Root');
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
        // Keep the virtual link base only when going back into the same linked tree
        if (_musicLinkBase && prev && prev.toLowerCase().startsWith(_musicLinkBase.real.toLowerCase())) {
          // Still inside the linked folder tree — keep existing base
        } else {
          _musicLinkBase = null;
        }
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
  document.getElementById('view-toggle-podcasts') &&
    (document.getElementById('view-toggle-podcasts').style.display = 'none');
    _applyViewToggleBtn('music');
    const txt = document.getElementById('count-text');
    let countText = '';
    if (searching) {
      const fc = data.folders?.length || 0;
      const tc = data.files?.length || 0;
      countText = `${_countWord(fc, 'folder', 'folders')} and ${_countWordNoZero(tc, 'result', 'results')}`;
    } else {
      const fc = data.folders?.length || 0;
      const loaded = data.files?.length || 0;
      const total = data.totalInFolder || loaded;
      countText = total > loaded
        ? `${_countWord(fc, 'folder', 'folders')}, ${_countWordNoZero(loaded, 'track', 'tracks')} of ${_countWord(total, 'track', 'tracks')} loaded`
        : `${_countWord(fc, 'folder', 'folders')}, ${_countWordNoZero(total, 'track', 'tracks')}`;
    }
    if (txt) {
      if (data.hasMore) {
        txt.innerHTML = countText + ', <a href="#" class="count-load-more" onclick="event.preventDefault();loadMoreMusic()">Load more</a>';
      } else {
        txt.textContent = countText;
      }
    }
    const mcbCount = document.getElementById('mcb-count');
    if (mcbCount) mcbCount.textContent = countText;
    _setCbRightVisible('mcb-right', (data.files?.length || 0) > 0 && _viewMode.music !== 'list');
  }

  // Scan status line
  if (scanStatus && currentMode === 'music') {
    scanStatus.classList.remove('scanning', 'error', 'global-scan-status');
    const total = Number(data.indexedFiles) || 0;
    const isScanning = Boolean(data.indexing);
    const isEnriching = Boolean(data.enriching);
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
    } else if (isEnriching) {
      scanStatus.classList.add('scanning');
      scanStatus.textContent =
        'Music Library Ready: ' + total + ' song(s)' + (root ? ' \u2014 enriching tags\u2026' : '');
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
    viewBtn.textContent = isList ? '\u25a6 Grid' : '\u2630 List';
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
        // Never re-browse when a dynamic folder is open or loading — the sentinel
        // value starts with \0 and browseMusic would navigate away from the track list.
        const _isDynView = currentMusicFolder && currentMusicFolder.startsWith('\0dynamic:');
        if (!_isDynView) {
          // Only re-browse on scan complete if we're at root, or the current folder
          // has no content yet (avoid wiping an already-populated folder view).
          const currentHasContent = currentMusicData && (
            (currentMusicData.files && currentMusicData.files.length > 0) ||
            (currentMusicData.folders && currentMusicData.folders.length > 0) ||
            (currentMusicData.playlists && currentMusicData.playlists.length > 0)
          );
          if (!currentMusicFolder || !currentHasContent) browseMusic(currentMusicFolder);
          else renderMusicHeader(currentMusicData, false); // just refresh the header badge
        }
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
        // but only if the current folder view has no visible content yet
        // (avoid wiping a folder that already has cards).
        // Never re-browse when a dynamic folder is open — it would navigate away.
        pollCount++;
        const _isDynView2 = currentMusicFolder && currentMusicFolder.startsWith('\0dynamic:');
        const currentHasContent = currentMusicData && (
          (currentMusicData.files && currentMusicData.files.length > 0) ||
          (currentMusicData.folders && currentMusicData.folders.length > 0) ||
          (currentMusicData.playlists && currentMusicData.playlists.length > 0)
        );
        if (pollCount % 5 === 0 && !currentHasContent && !_isDynView2) browseMusic(currentMusicFolder, null, 0, false);
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
  if (existing >= 0) {
    // Use the shared remove helper so cursor stays consistent
    _musicQueueRemove(existing);
    return; // _musicQueueRemove already re-renders
  } else {
    window._musicQueue.push({ path, name });
    _musicResetQueueTraversalState();
  }
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

function _musicGetSelectedSortedFiles() {
  if (!currentMusicData || !currentMusicData.files) return [];
  const sorted = _musicSortFiles(currentMusicData.files);
  return sorted.filter((f) => _musicSelected.has(f.path));
}

function _musicSelectionAddToQueue() {
  if (!_musicSelected.size) return;
  if (!window._musicQueue) window._musicQueue = [];
  const selected = _musicGetSelectedSortedFiles();
  if (selected.length) {
    selected.forEach((f) => {
      window._musicQueue.push({ path: f.path, name: f.name });
    });
    _musicResetQueueTraversalState();
  }
  _musicSelectionClear();
  if (currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
  _renderMusicQueuePeek();
}
function _musicSelectionPlay() {
  if (!_musicSelected.size) return;
  const selected = _musicGetSelectedSortedFiles();
  if (!selected.length) return;

  // Put the full selection into the persistent queue and start at index 0
  window._musicQueue = selected.map((f) => ({ path: f.path, name: f.name }));
  window._musicQueuePos = 0;
  _musicResetQueueTraversalState();
  playMusic(selected[0].path, selected[0].name);

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
  // Show the yellow queue-next button (replacing Prev/Next) when queue has items
  const navNormal      = document.getElementById('music-nav-normal');
  const btnQueueNext   = document.getElementById('music-btn-queue-next');
  const queueNextTitle = document.getElementById('music-queue-next-title');
  if (navNormal && btnQueueNext) {
    const hasQueue = count > 0;
    navNormal.style.display    = hasQueue ? 'none' : '';
    btnQueueNext.style.display = hasQueue ? ''     : 'none';
    if (queueNextTitle) {
      // Use the same peek logic as the normal Next label so shuffle+queue shows the true upcoming track.
      const peek = hasQueue ? _musicPeekNext() : null;
      queueNextTitle.textContent = peek ? (peek.name || '') : '';
    }
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

async function browseMusic(folder, virtualPath = null, offset = 0, append = false) {
  console.log('[MUSIC] browseMusic called: folder =', folder, ', append =', append);
  if (!append) {
    _cancelPendingPlaylistArtLoads();
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
    console.log('[MUSIC] currentMusicFolder set to:', currentMusicFolder);
    // Track virtual path base for rplink folder navigation
    if (virtualPath) {
      _musicLinkBase = { real: folder.replace(/[\/\\]+$/, ''), virtual: virtualPath };
    } else if (_musicLinkBase && folder && folder.toLowerCase().startsWith(_musicLinkBase.real.toLowerCase())) {
      // Still inside the linked folder tree — keep the existing base
    } else {
      _musicLinkBase = null;
    }
    // Always show the dynamic-folder button when browsing real folders; hide reroll
    const _dynBtn = document.getElementById('mcb-dynamic-btn');
    if (_dynBtn) _dynBtn.style.display = '';
    const _rerollBtn = document.getElementById('mcb-reroll-btn');
    if (_rerollBtn) _rerollBtn.style.display = 'none';
    const _pinBtn = document.getElementById('mcb-pin-btn');
    if (_pinBtn) _pinBtn.style.display = '';
    const _minusBtn = document.getElementById('mcb-dyn-minus');
    if (_minusBtn) _minusBtn.style.display = 'none';
    const _plusBtn = document.getElementById('mcb-dyn-plus');
    if (_plusBtn) _plusBtn.style.display = 'none';
    const _fromLabel = document.getElementById('mcb-dyn-from');
    if (_fromLabel) _fromLabel.style.display = 'none';
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
    // If every configured music path is invalid, show empty state and hide chrome.
    if (data.allMusicPathsInvalid) {
      if (typeof _applyMusicLibraryAvailable === 'function') _applyMusicLibraryAvailable(false);
      mb.innerHTML =
        '<div id="empty" class="empty-invalid-path">'
        + '<span class="empty-invalid-icon">&#127925;</span>'
        + '<span class="empty-invalid-title">No music library configured</span>'
        + '<span class="empty-invalid-sub">The configured music path is invalid or does not exist.<br>'
        + 'Go to <a onclick="location.href=\'/settings\'">Settings</a> to update the library path.</span>'
        + '</div>';
      return;
    }
    if (!append) {
      // Build a friendly URL path from breadcrumbs or folder path
      let friendlyPath = null;
      if (data.breadcrumbs && data.breadcrumbs.length) {
        console.log('[MUSIC] Breadcrumbs from server:', data.breadcrumbs);
        friendlyPath = _buildFriendlyPathFromBreadcrumbs(data.breadcrumbs);
        console.log('[MUSIC] Friendly path from breadcrumbs:', friendlyPath);
      } else if (folder) {
        friendlyPath = _encodeRouteFolder(folder);
        console.log('[MUSIC] Friendly path from folder:', friendlyPath);
      }

      if (friendlyPath || folder) {
        // Build URL with friendly names + base64url-encoded path at the end
        let path = '/music/';
        if (friendlyPath) {
          path += friendlyPath;
        }
        if (folder) {
          const encoded = _folderPathToBase64Url(folder);
          if (encoded) {
            if (friendlyPath) path += '/';
            path += encoded;
          }
        }
        console.log('[MUSIC] Setting URL to:', path);
        if (window.history && typeof window.history.replaceState === 'function') {
          window.history.replaceState(window.history.state, '', path);
        }
      } else {
        if (window.history && typeof window.history.replaceState === 'function') {
          window.history.replaceState(window.history.state, '', '/music');
        }
      }
    }

    if (currentMode === 'music') renderMusicHeader(currentMusicData, false);
    renderMusicCards(currentMusicData);
    // Mark that the user has browsed music at least once this page lifetime,
    // so dynamic-session restore is allowed on subsequent tab switches.
    try { sessionStorage.setItem('rpdyn_browsed', '1'); } catch {}
    // If we just navigated into a folder and it's still indexing (no tracks yet),
    // start a fast re-poll so results appear as soon as the priority scan finishes.
    if (folder && !append && data.indexing
      && !(data.files && data.files.length > 0)
      && !(data.folders && data.folders.length > 0)
      && !(data.playlists && data.playlists.length > 0)) {
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
  if (!append) _cancelPendingPlaylistArtLoads();
  try {
    const res = await fetch(
      '/api/music/search?q=' +
        encodeURIComponent(q) +
        (offset ? '&offset=' + encodeURIComponent(offset) : '')
    );
    if (!res.ok) {
      setSearchBusy(false, 'music');
      return;
    }
    const data = await res.json();
    if (append && currentMusicData) {
      currentMusicData.files = [...(currentMusicData.files || []), ...(data.files || [])];
      currentMusicData.hasMore = data.hasMore;
    } else currentMusicData = { ...data, folders: [], query: q };
    setSearchBusy(false, 'music');
    if (currentMode === 'music') renderMusicHeader(currentMusicData, true);
    renderMusicCards(currentMusicData, true);
    if (typeof _lastSearchQuery !== 'undefined') _lastSearchQuery = q;
    if (typeof _showSearchFilterBar === 'function') _showSearchFilterBar(true);
    if (typeof _applySearchFilter === 'function') _applySearchFilter();
  } catch (e) {
    setSearchBusy(false, 'music');
  }
}

// ── Music card metadata helpers ────────────────────────────────────────────

/**
 * Strip a leading track-number prefix from a filename (no extension).
 *   "01 - Song Title" → "Song Title"
 *   "01. Song Title"  → "Song Title"
 *   "Song Title"      → "Song Title"
 */
/** Degrade a grid or list track-card thumbnail to the no-cover appearance. */
function _mtcNoCover(img) {
  const wrap = img.parentElement;
  if (wrap) {
    wrap.classList.remove('mtc-thumb-wrap');
    wrap.classList.add('mtc-no-cover');
    // Reveal track-number span if present (list mode)
    const tn = wrap.querySelector('.mtc-tracknum-default');
    if (tn) tn.style.display = '';
  }
  img.remove();
}

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

// Parse a playlist filename: if it matches YYYY_RestOfName, return { displayName, year };
// otherwise return { displayName: name, year: null }.
function _parsePlName(name) {
  const m = /^(\d{4})_(.+)$/.exec(name);
  const raw = m ? m[2] : name;
  let year = m ? m[1] : null;
  // If no underscore-year prefix, check for "YYYY - Album" (dash-separated year).
  // Without this, "1992 - Dit ben ik" would be mis-parsed as artist="1992".
  if (!year) {
    const mDashYear = /^(\d{4})\s+[-\u2013\u2014]\s+(.+)$/.exec(raw);
    if (mDashYear) {
      return { displayName: mDashYear[2].trim(), year: mDashYear[1], artist: null };
    }
  }
  // Try to split "Artist - Album" or "Artist – Album"
  const sep = /^(.+?)\s+[-\u2013\u2014]\s+(.+)$/.exec(raw);
  const artist = sep ? sep[1].trim() : null;
  const displayName = sep ? sep[2].trim() : raw;
  return { displayName, year, artist };
}

/**
 * Attempts to fetch album art from the backend proxy (MusicBrainz/CAA) and
 * applies it as the background of the playlist card's art area.
 * Falls back silently to the existing gradient if art is not found.
 */
function _applyPlaylistArtEl(el) {
  if (!el) return;
  const serverUrl = el.dataset.plarturl || '';
  if (!serverUrl) return;

  if (el.classList.contains('music-playlist-card')) {
    // Keep gradient placeholder visible until the remote art URL has actually loaded.
    const probe = new Image();
    probe.onload = () => {
      el.style.backgroundImage = `url(${serverUrl})`;
      el.style.backgroundSize = 'cover';
      el.style.backgroundPosition = 'center';
      const note = el.querySelector('.mpl-card-note');
      if (note) note.style.opacity = '0';
      el.classList.add('has-album-art');
      el._albumArtUrl = serverUrl;
    };
    probe.onerror = () => {
      // Keep the existing gradient + note placeholder when art cannot be fetched.
      el.classList.remove('has-album-art');
      el.style.backgroundImage = '';
      const note = el.querySelector('.mpl-card-note');
      if (note) note.style.opacity = '';
    };
    probe.src = serverUrl;
    return;
  }

  if (el.classList.contains('music-playlist-list-row') || el.classList.contains('music-playlist-row')) {
    const thumbEl = el.querySelector('.mpl-list-thumb');
    if (!thumbEl) return;
    const probe = new Image();
    probe.onload = () => {
      thumbEl.style.backgroundImage = `url(${serverUrl})`;
      thumbEl.style.backgroundSize = 'cover';
      thumbEl.style.backgroundPosition = 'center';
      thumbEl.textContent = '';
      el._albumArtUrl = serverUrl;
    };
    probe.onerror = () => { /* keep the ♩ placeholder */ };
    probe.src = serverUrl;
  }
}

function _cancelPendingPlaylistArtLoads() {
  if (!window._artLoadAbortCtrl) return;
  try { window._artLoadAbortCtrl.abort(); } catch (_) {}
  window._artLoadAbortCtrl = null;
}

function renderMusicCards(data, searching) {
  const mb = document.getElementById('music-browser');
  if (!mb) return;
  const isGrid = _viewMode.music !== 'list';
  const isList = !isGrid;
  let html = '';

  function _dynFilterTags(f) {
    let t = '';
    if (Array.isArray(f.include) && f.include.length)
      t += f.include.map(x => '<span class="folder-dyn-tag folder-dyn-tag-include">' + esc(x) + '</span>').join('');
    if (Array.isArray(f.exclude) && f.exclude.length)
      t += f.exclude.map(x => '<span class="folder-dyn-tag folder-dyn-tag-exclude">\u2212' + esc(x) + '</span>').join('');
    if (f.genre) t += '<span class="folder-dyn-tag folder-dyn-tag-genre">\uD83C\uDFB5 ' + esc(f.genre) + '</span>';
    if (f.yearFrom || f.yearTo)
      t += '<span class="folder-dyn-tag folder-dyn-tag-year">\uD83D\uDCC5 ' + esc(String(f.yearFrom || '?')) + '\u2013' + esc(String(f.yearTo || '?')) + '</span>';
    if (f.minDuration != null || f.maxDuration != null) {
      const minTxt = f.minDuration != null ? _fmtDuration(f.minDuration) : '0:00';
      const maxTxt = f.maxDuration != null ? _fmtDuration(f.maxDuration) : '∞';
      t += '<span class="folder-dyn-tag folder-dyn-tag-time">⏱ ' + esc(minTxt + '–' + maxTxt) + '</span>';
    }
    if (f.mode === 'maxTotalDuration' && f.maxTotalDuration != null)
      t += '<span class="folder-dyn-tag folder-dyn-tag-total">🧮 ≤ ' + esc(_fmtDuration(f.maxTotalDuration)) + '</span>';
    if (f.recursive === false)
      t += '<span class="folder-dyn-tag folder-dyn-tag-scope">📁 Flat</span>';
    return t;
  }
  function _dynLastSeen(f) {
    if (!f.lastExpanded) return '';
    try {
      const d = new Date(f.lastExpanded);
      const dd = Math.floor((Date.now() - d) / 86400000);
      if (dd === 0) return 'Today';
      if (dd === 1) return 'Yesterday';
      if (dd < 7) return dd + 'd ago';
      return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    } catch(e) { return ''; }
  }

  function _sortedMusicFoldersForListRows(folders) {
    if (!Array.isArray(folders) || !folders.length) return [];

    return folders
      .slice()
      .sort((a, b) => {
        if (a.isDynamic && !b.isDynamic) return -1;
        if (!a.isDynamic && b.isDynamic) return 1;
        return 0;
      })
      .map((f) => Object.assign({}, f, {
        isFolderRow: true,
        rowType: f.isDynamic ? 'dynamic' : 'folder'
      }));
  }

  function _folderLeafName(pathValue, fallbackName) {
    const raw = String(pathValue || '').trim();
    if (!raw) return String(fallbackName || '').trim();
    const parts = raw.split(/[\\/]+/).filter(Boolean);
    return parts.length ? parts[parts.length - 1] : String(fallbackName || '').trim();
  }

  function _musicFolderTypeBadgeInfo(folderPath, folderName, isAll) {
    const leaf = (isAll ? 'ALL' : _folderLeafName(folderPath, folderName)).trim().toUpperCase();
    if (leaf === 'ALL') return { icon: '&#10024;', cls: 'type-all', label: 'All' };
    if (leaf === 'BYTAG') return { icon: '&#127991;&#65039;', cls: 'type-bytag', label: 'ByTag' };
    if (leaf === 'BYSET') return { icon: '&#129513;', cls: 'type-byset', label: 'BySet' };
    if (leaf === 'ALBUMS' || leaf === 'ALBUM') return { icon: '&#128191;', cls: 'type-album', label: 'Albums' };
    if (leaf === 'SINGLES' || leaf === 'SINGLE') return { icon: '&#9835;', cls: 'type-single', label: 'Singles' };
    if (leaf === 'COMPILATIONS') return { icon: '&#128450;&#65039;', cls: 'type-compilations', label: 'Compilations' };
    if (leaf === 'LIVE') return { icon: '&#127908;', cls: 'type-live', label: 'Live' };
    return null;
  }

  function _musicFolderIconHtml(folderPath, folderName, isAll, className) {
    const badge = _musicFolderTypeBadgeInfo(folderPath, folderName, isAll);
    const cls = className ? (' ' + className) : '';
    const isHero = String(className || '').includes('rp-folder-icon--hero');
    return '<span class="rp-folder-icon' + cls + '" aria-hidden="true">'
      + '<span class="rp-folder-base">&#128193;</span>'
      + (badge
        ? (isHero
          ? '<span class="rp-folder-symbol ' + badge.cls + '" title="' + esc(badge.label) + '">' + badge.icon + '</span>'
          : '<span class="rp-folder-badge ' + badge.cls + '" title="' + esc(badge.label) + '">' + badge.icon + '</span>')
        : '')
      + '</span>';
  }

  function _musicFolderListRowHtml(f, idx) {
    const isAll = f.isAll || f.name === 'All';
    const isLink = !!f.isLink;
    const isDynamic = !!f.isDynamic;
    const folderName = f.name || 'Folder';
    const folderNameJs = jsStr(folderName);
    const folderPath = f.folder || '';
    const folderPathJs = jsStr(folderPath);
    const dynamicPath = f.dynamicPath || folderPath;
    const dynamicPathJs = jsStr(dynamicPath);
    const browseAction = isDynamic
      ? "_openDynamicFolder('" + dynamicPathJs + "','" + folderNameJs + "')"
      : "browseMusic('" + folderPathJs + "'" + (isLink && f.virtualPath ? ", '" + jsStr(f.virtualPath) + "'" : '') + ")";
    const contextMenuAction = isDynamic
      ? "_ctxShow(event,'music-dynamic',{dir:'" + dynamicPathJs + "',name:'" + folderNameJs + "',dynamicPath:'" + dynamicPathJs + "'})"
      : "_ctxShow(event,'music-folder',{dir:btoa(unescape(encodeURIComponent('" + folderPathJs + "'))),name:'" + folderNameJs + "'})";
    const folderTypeLabel = isDynamic ? 'dynamic folder' : (isLink ? 'linked folder' : 'folder');
    const typeBadge = isDynamic ? 'DYNAMIC' : 'FOLDER';
    const infoLeft = isDynamic
      ? ((f.sort === 'alphabetical' ? '\u21d3 A\u2013Z' : f.sort === 'oldest' ? '\u21d3 Oldest' : f.sort === 'newest' ? '\u21d3 Recent' : '\uD83C\uDFB2') + ' · '
        + (f.mode === 'all' ? 'All' : f.mode === 'top' ? 'Top' : 'Random'))
      : '';
    const infoRight = f.trackCount
      ? (String(f.trackCount) + ' track' + (f.trackCount !== 1 ? 's' : ''))
      : (f.playlistCount > 0 ? _countWord(f.playlistCount, 'playlist', 'playlists') : (isLink ? 'Linked folder' : 'Folder'));

    return '<div class="music-track-card music-folder-row'
      + (isAll ? ' music-folder-row-all' : '')
      + (isLink ? ' music-folder-row-link' : '')
      + (isDynamic ? ' music-folder-row-dynamic' : '')
      + (idx % 2 === 1 ? ' alt-row' : '')
      + '" role="button" tabindex="0" aria-label="Open ' + esc(folderTypeLabel) + ' ' + esc(folderName) + '"'
      + ' onclick="' + browseAction + '" onkeydown="activateKeyboardClick(event,this)" oncontextmenu="' + contextMenuAction + '">'
      + '<span class="mtc-play-indicator music-folder-row-icon">' + (isDynamic ? '&#127922;' : _musicFolderIconHtml(folderPath, folderName, isAll)) + '</span>'
      + '<span class="mtc-title">' + esc(folderName)
      + (isLink ? ' <span class="music-folder-link-pill" title="Library link">🔗</span>' : '')
      + '</span>'
      + '<span class="mtc-badge-group"><span class="mtc-badge">' + esc(typeBadge) + '</span></span>'
      + '<span class="mtc-artist">' + esc(infoLeft) + '</span>'
      + '<span class="mtc-year"></span>'
      + '<span class="mtc-album">' + esc(infoRight) + '</span>'
      + '<span class="mtc-duration"></span>'
      + '<span class="mtc-checkbox music-folder-row-cb" aria-hidden="true" style="visibility:hidden"></span>'
      + '</div>';
  }

  function _musicPlaylistListRowHtml(pl, idx) {
    const count = pl.trackCount > 0 ? pl.trackCount + ' track' + (pl.trackCount !== 1 ? 's' : '') : '';
    const totalDur = pl.totalDurationSec > 0 ? _fmtDuration(pl.totalDurationSec) : '';
    const parsed = _parsePlName(pl.name);
    const displayName = parsed.displayName;
    const year = parsed.year;
    const parsedArtist = parsed.artist;
    const artist = (pl.artist && pl.artist.trim()) || parsedArtist || '';

    return '<div class="music-track-card music-playlist-row'
      + (idx % 2 === 1 ? ' alt-row' : '')
      + '" role="button" tabindex="0" title="' + esc(displayName) + '"'
      + ' data-plpath="' + esc(pl.path) + '" data-plalbum="' + esc(pl.albumForArt || displayName) + '"'
      + (artist ? ' data-plartist="' + esc(artist) + '"' : '')
      + (pl.albumArtUrl ? ' data-plarturl="' + esc(pl.albumArtUrl) + '"' : '')
      + '>'
      + '<span class="mtc-play-indicator music-playlist-row-icon"><span class="mpl-list-thumb">&#9835;</span></span>'
      + '<span class="mtc-title">' + esc(displayName) + '</span>'
      + '<span class="mtc-badge-group"><span class="mtc-badge">PLAYLIST</span></span>'
      + '<span class="mtc-artist">' + esc(artist) + '</span>'
      + '<span class="mtc-year">' + (year ? esc(year) : '') + '</span>'
      + '<span class="mtc-album">' + esc(count) + '</span>'
      + '<span class="mtc-duration">' + esc(totalDur) + '</span>'
      + '<span class="mtc-checkbox music-playlist-row-cb" aria-hidden="true" style="visibility:hidden"></span>'
      + '</div>';
  }

  if (isGrid && !searching) {
    let gridHtml = '';

    if (data.folders && data.folders.length) {
      data.folders.filter(f => f.isDynamic).forEach(function(f) {
        const _dp = jsStr(f.dynamicPath || f.folder);
        const filterTags = _dynFilterTags(f);
        const sortIcon = f.sort === 'alphabetical' ? '\u21d3 A\u2013Z' : f.sort === 'oldest' ? '\u21d3 Oldest' : f.sort === 'newest' ? '\u21d3 Recent' : '\uD83C\uDFB2';
        const modeLabel = f.mode === 'all' ? 'All' : f.mode === 'top' ? 'Top' : 'Random';
        gridHtml += '<div class="mfc-card mfc-dyn-card folder-row-dynamic" role="button" tabindex="0" onkeydown="activateKeyboardClick(event,this)"'
          + ' oncontextmenu="_ctxShow(event,\'music-dynamic\',{dir:\'' + _dp + '\',name:\'' + jsStr(f.name) + '\',dynamicPath:\'' + _dp + '\'})"'
          + ' onclick="_openDynamicFolder(\'' + _dp + '\',\'' + jsStr(f.name) + '\')">'
          + '<button class="mfc-dyn-edit-btn" title="Edit" onclick="event.stopPropagation();_editDynamicFolder(\'' + _dp + '\')">✏️</button>'
          + '<div class="mfc-card-hero">'
          + '<div class="mfc-dyn-top-row">'
          + '<span class="mfc-dyn-badge">DYNAMIC</span>'
          + '</div>'
          + '<span class="mfc-card-icon">🎲</span>'
          + (f.trackCount ? '<span class="mfc-dyn-count">' + f.trackCount + ' songs</span>' : '')
          + '</div>'
          + '<div class="mfc-card-footer mfc-dyn-footer">'
          + '<div class="mfc-dyn-side">'
          + '<span class="mfc-dyn-side-mode">' + esc(sortIcon + ' · ' + modeLabel) + '</span>'
          + (filterTags ? '<div class="mfc-dyn-tags">' + filterTags + '</div>' : '')
          + '</div>'
          + '<div class="mfc-dyn-main">'
          + '<span class="mfc-card-name" title="' + esc(f.name) + '">' + esc(f.name) + '</span>'
          + '</div>'
          + '</div>'
          + '</div>';
      });
      data.folders.filter(f => !f.isDynamic).forEach(function(f) {
        const isAll = f.isAll || f.name === 'All';
        const isLink = !!f.isLink;
        const trackCountRaw = Number(f.trackCount);
        const playlistCountRaw = Number(f.playlistCount);
        const hasTrackCount = Number.isFinite(trackCountRaw) && trackCountRaw > 0;
        const hasPlaylistCount = !hasTrackCount && Number.isFinite(playlistCountRaw) && playlistCountRaw > 0;
        gridHtml += '<div class="mfc-card mfc-folder-card' + (isAll ? ' folder-row-all' : '') + (isLink ? ' folder-row-link' : '') + '" role="button" tabindex="0" onkeydown="activateKeyboardClick(event,this)"'
          + ' oncontextmenu="_ctxShow(event,\'music-folder\',{dir:btoa(unescape(encodeURIComponent(\'' + jsStr(f.folder) + '\'))),name:\'' + jsStr(f.name) + '\'})"'
          + ' onclick="browseMusic(\'' + jsStr(f.folder) + '\'' + (isLink && f.virtualPath ? ',\'' + jsStr(f.virtualPath) + '\'' : '') + ')">'
          + '<div class="mfc-card-hero">'
          + _musicFolderIconHtml(f.folder, f.name, isAll, 'mfc-card-icon rp-folder-icon--hero')
          + '</div>'
          + '<div class="mfc-card-footer">'
          + '<span class="mfc-card-name">' + esc(f.name) + '</span>'
          + (hasTrackCount ? '<span class="mfc-card-sub">' + esc(_countWord(trackCountRaw, 'track', 'tracks')) + '</span>' : '')
          + (hasPlaylistCount ? '<span class="mfc-card-sub">' + esc(_countWord(playlistCountRaw, 'playlist', 'playlists')) + '</span>' : '')
          + (isLink ? '<span class="folder-link-badge" title="Library link">🔗</span>' : '')
          + '</div>'
          + '</div>';
      });
    }

    if (data.playlists && data.playlists.length) {
      data.playlists.forEach(function(pl, idx) {
        const count = pl.trackCount > 0 ? pl.trackCount : null;
        const totalDur = pl.totalDurationSec > 0 ? _fmtDuration(pl.totalDurationSec) : '';
        const parsed = _parsePlName(pl.name);
        const displayName = parsed.displayName; const year = parsed.year; const parsedArtist = parsed.artist;
        const artist = (pl.artist && pl.artist.trim()) || parsedArtist || '';
        const gradIdx = idx % 8;
        gridHtml += '<div class="music-playlist-card mpl-grad-' + gradIdx + '" role="button" tabindex="0" title="' + esc(displayName) + '"'
          + ' onkeydown="activateKeyboardClick(event,this)" data-plpath="' + esc(pl.path) + '" data-plalbum="' + esc(pl.albumForArt || displayName) + '"' + (artist ? ' data-plartist="' + esc(artist) + '"' : '') + (pl.albumArtUrl ? ' data-plarturl="' + esc(pl.albumArtUrl) + '"' : '') + '>'
          + '<div class="mpl-card-top-pills">'
          + (year ? '<span class="mpl-card-year">' + esc(year) + '</span>' : '<span></span>')
          + '<span class="mpl-badge-footer">PLAYLIST</span>'
          + '</div>'
          + '<div class="mpl-card-art"><span class="mpl-card-note">&#9835;</span></div>'
          + '<div class="mpl-card-footer"><span class="mpl-card-name">' + esc(displayName) + '</span></div>'
          + ((count || totalDur) ? '<span class="mpl-card-count">' + (count ? count + ' tracks' : '') + (count && totalDur ? ' · ' : '') + esc(totalDur) + '</span>' : '')
          + '</div>';
      });
    }

    if (gridHtml.length > 0 || (data.files && data.files.length)) {
      html += '<div class="music-grid">' + gridHtml;
    }
  } else {
    if (!isList && !searching && data.folders && data.folders.length) {
      html += '<div class="folder-list">';
      const _sortedFolders = data.folders.slice().sort(function(a, b) {
        if (a.isDynamic && !b.isDynamic) return -1;
        if (!a.isDynamic && b.isDynamic) return 1;
        return 0;
      });
      _sortedFolders.forEach(function(f) {
        const isAll = f.isAll || f.name === 'All';
        if (f.isDynamic) {
          const _dp = jsStr(f.dynamicPath || f.folder);
          const filterTags = _dynFilterTags(f);
          const lastSeen = _dynLastSeen(f);
          const sortIcon = f.sort === 'alphabetical' ? '\u21d3 A\u2013Z' : f.sort === 'oldest' ? '\u21d3 Oldest' : f.sort === 'newest' ? '\u21d3 Recent' : '\uD83C\uDFB2';
          const modeLabel = f.mode === 'all' ? 'All' : f.mode === 'top' ? 'Top' : 'Random';
          html += '<div class="folder-row folder-row-dynamic" role="button" tabindex="0" onkeydown="activateKeyboardClick(event,this)"'
            + ' oncontextmenu="_ctxShow(event,\'music-dynamic\',{dir:\'' + _dp + '\',name:\'' + jsStr(f.name) + '\',dynamicPath:\'' + _dp + '\'})"'
            + ' onclick="_openDynamicFolder(\'' + _dp + '\',\'' + jsStr(f.name) + '\')">'
            + '<span class="folder-icon">&#127922;</span>'
            + '<div class="folder-dyn-body">'
            + '<div class="folder-dyn-top">'
            + '<span class="folder-name">' + esc(f.name) + '</span>'
            + (f.trackCount ? '<span class="folder-dyn-count">' + f.trackCount + ' songs</span>' : '')
            + '<span class="folder-dyn-meta">' + sortIcon + ' \u00b7 ' + modeLabel + '</span>'
            + (lastSeen ? '<span class="folder-dyn-last">\uD83D\uDD52 ' + lastSeen + '</span>' : '')
            + '</div>'
            + (filterTags ? '<div class="folder-dyn-tags">' + filterTags + '</div>' : '')
            + '</div>'
            + '<button class="folder-dyn-edit-btn" title="Edit" onclick="event.stopPropagation();_editDynamicFolder(\'' + _dp + '\')">\u270F\uFE0F</button>'
            + '</div>';
        } else {
          const isLink = !!f.isLink;
          html += '<div class="folder-row' + (isAll ? ' folder-row-all' : '') + (isLink ? ' folder-row-link' : '') + '" role="button" tabindex="0" onkeydown="activateKeyboardClick(event,this)"'
            + ' oncontextmenu="_ctxShow(event,\'music-folder\',{dir:btoa(unescape(encodeURIComponent(\'' + jsStr(f.folder) + '\'))),name:\'' + jsStr(f.name) + '\'})"'
            + ' onclick="browseMusic(\'' + jsStr(f.folder) + '\'' + (isLink && f.virtualPath ? ',\'' + jsStr(f.virtualPath) + '\'' : '') + ')">'
            + _musicFolderIconHtml(f.folder, f.name, isAll, 'folder-icon') + '<span class="folder-name">'
            + esc(f.name) + '</span>'
            + (isLink ? '<span class="folder-link-badge" title="Library link">&#128279;</span>' : '')
            + '</div>';
        }
      });
      html += '</div>';
    }
    if (!isList && !searching && data.playlists && data.playlists.length) {
      html += '<div class="music-playlist-list">';
      html += '<div class="music-playlist-list-header"><span></span><span>Name</span><span>Year</span><span>Type</span><span class="mpl-list-count">Tracks</span></div>';
      data.playlists.forEach(function(pl, idx) {
        const count = pl.trackCount > 0 ? pl.trackCount + ' track' + (pl.trackCount !== 1 ? 's' : '') : '';
        const parsed = _parsePlName(pl.name);
        const displayName = parsed.displayName; const year = parsed.year; const parsedArtist = parsed.artist;
        const artist = (pl.artist && pl.artist.trim()) || parsedArtist || '';
        html += '<div class="music-playlist-list-row' + (idx % 2 === 1 ? ' alt-row' : '') + (year ? ' has-year' : '') + '" role="button" tabindex="0" title="' + esc(displayName) + '"'
          + ' onkeydown="activateKeyboardClick(event,this)" data-plpath="' + esc(pl.path) + '" data-plalbum="' + esc(pl.albumForArt || displayName) + '"' + (artist ? ' data-plartist="' + esc(artist) + '"' : '') + (pl.albumArtUrl ? ' data-plarturl="' + esc(pl.albumArtUrl) + '"' : '') + '>'
          + '<span class="mpl-list-thumb">&#9835;</span>'
          + '<span class="mpl-list-name">' + esc(displayName) + '</span>'
          + (year ? '<span class="mpl-list-year">' + esc(year) + '</span>' : '<span></span>')
          + '<span class="mpl-badge">PLAYLIST</span>'
          + (count ? '<span class="mpl-list-count">' + esc(count) + '</span>' : '<span></span>')
          + '</div>';
      });
      html += '</div>';
    }
  }
  const listPlaylists = isList && !searching && Array.isArray(data.playlists)
    ? data.playlists.map((pl) => Object.assign({}, pl, { isPlaylistRow: true }))
    : [];
  const listFolders = isList && !searching ? _sortedMusicFoldersForListRows(data.folders) : [];
  const hasTrackFiles = Array.isArray(data.files) && data.files.length > 0;
  if (hasTrackFiles || (isList && (listPlaylists.length > 0 || listFolders.length > 0))) {
    const sortedFiles = hasTrackFiles ? _musicSortFiles(data.files) : [];
    const rows = isList ? [...listFolders, ...listPlaylists, ...sortedFiles] : sortedFiles;

    // ── Sync command bar ──────────────────────────────────────────────────
    const playAllVisible = !!data.folder && hasTrackFiles;
    _syncMusicCommandBar(playAllVisible);

    if (isList) {
      const _si = (col) => _musicSort.col === col ? (_musicSort.dir === 'asc' ? ' ▲' : ' ▼') : '';
      const foldersOnly = !hasTrackFiles && listPlaylists.length === 0;
      html += `<div class="music-list-header${foldersOnly ? ' folders-only' : ''}">`
        + '<span class="mlh-icon" title="Art / icon"></span>'
        + `<span class="sortable" onclick="musicSortBy('name')" title="Sort by title">Title${_si('name')}</span>`
        + `<span class="sortable" onclick="musicSortBy('ext')" title="Sort by format">Fmt${_si('ext')}</span>`
        + `<span class="sortable" onclick="musicSortBy('artist')" title="Sort by artist">Artist${_si('artist')}</span>`
        + `<span class="sortable mlh-year" onclick="musicSortBy('year')" title="Sort by year">Year${_si('year')}</span>`
        + `<span class="sortable" onclick="musicSortBy('album')" title="Sort by album">Album${_si('album')}</span>`
        + `<span class="sortable mtc-dur-hdr" onclick="musicSortBy('durationSec')" title="Sort by duration">Time${_si('durationSec')}</span>`
        + '<span></span>'
        + '</div>';
    }
    if (isList) { html += `<div class="music-grid list-view${(!hasTrackFiles && listPlaylists.length === 0) ? ' folders-only' : ''}">`; } else if (!isGrid || searching) { html += '<div class="music-grid">'; }
    rows.forEach((f, idx) => {
      if (f.isPlaylistRow) {
        html += _musicPlaylistListRowHtml(f, idx);
        return;
      }

      if (f.isFolderRow) {
        html += _musicFolderListRowHtml(f, idx);
        return;
      }

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

      // Always attempt cover art — the server caches results so repeat requests are instant.
      // hasCover is kept as a hint to skip the slow online-art fallback for confirmed no-cover tracks.
      const gridCoverUrl = !isList ? `/api/music/cover?path=${encodeURIComponent(f.path)}` : null;

      let card = `<div class="music-track-card${isPlaying ? ' playing' : ''}${isSelected ? ' selected' : ''}${isQueued ? ' queued' : ''}${isList && idx % 2 === 1 ? ' alt-row' : ''}" data-path="${esc(f.path)}" data-idx="${idx}" data-sr-title="${esc(title)}" data-sr-artist="${esc(artist)}" data-sr-album="${esc(album)}" title="${esc(title)}"${gridCoverUrl ? ` data-cover-url="${gridCoverUrl}"` : ''}>`;

      // ── Checkbox (for multi-select) ────────────────────────────────────
      card += `<input type="checkbox" class="mtc-checkbox" data-path="${esc(f.path)}" data-idx="${idx}"${isSelected ? ' checked' : ''} tabindex="-1" aria-label="Select ${esc(title)}" />`;

      // ── Col 1: thumbnail / playing bars / music-note placeholder / track number
      // Always render both cover/placeholder AND bars so toggling .playing on the card
      // is enough — no stale HTML left on the wrong row when the track changes.
      if (!isList) {
        // Grid mode: always try local cover art; post-render pass adds online-art fallback
        card += `<span class="mtc-play-indicator mtc-thumb-wrap">`
          + `<img class="mtc-thumb" src="${gridCoverUrl}" loading="lazy" data-artist="${esc(artist)}" data-album="${esc(album)}" data-title="${esc(title)}"${hasCover ? '' : ' data-no-cover="1"'} />`
          + `<span class="mtc-bars mtc-bars-overlay"><span></span><span></span><span></span></span>`
          + `</span>`;
      } else {
        // List mode: always try local cover art; post-render pass adds online-art fallback
        card += `<span class="mtc-play-indicator mtc-thumb-wrap">`
          + `<img class="mtc-thumb" src="/api/music/cover?path=${encodeURIComponent(f.path)}" loading="lazy" data-artist="${esc(artist)}" data-album="${esc(album)}" data-title="${esc(title)}"${hasCover ? '' : ' data-no-cover="1"'} />`
          + (trackNum != null ? `<span class="mtc-tracknum mtc-tracknum-default">${trackNum}</span>` : '')
          + `<span class="mtc-bars mtc-bars-overlay"><span></span><span></span><span></span></span>`
          + `</span>`;
      }

      // ── Col 2: title ───────────────────────────────────────────────────
      card += `<span class="mtc-title">${esc(title)}${isList && isQueued ? ' <span class="mtc-queued-badge">Q</span>' : ''}</span>`;

      // ── Col 3: format badge + duration (together in top-right) ─────────
      const durHtml = duration ? `<span class="mtc-badge-dur">${esc(duration)}</span>` : '';
      card += `<span class="mtc-badge-group">${badge ? `<span class="mtc-badge">${esc(badge)}</span>` : ''} ${durHtml}</span>`;

      // ── Col 4: artist ─────────────────────────────────────────────────
      card += `<span class="mtc-artist">${esc(artist)}</span>`;

      // ── Col 5 (list only): year ──────────────────────────────────────
      if (isList) {
        card += `<span class="mtc-year">${year != null ? esc(String(year)) : ''}</span>`;
      }

      // ── Col 6: album ──────────────────────────────────────────────────
      card += `<span class="mtc-album">${esc(album)}</span>`;

      // ── Col 7 (list only): duration ───────────────────────────────────
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
    if (hasTrackFiles && data.hasMore) {
      const shown = data.files.length;
      const total = data.totalInFolder || shown;
      html +=
        '<button class="load-more-btn" onclick="loadMoreMusic()">Load more tracks (' +
        shown + ' of ' + total + ' loaded)</button>';
    }
  } else if (data.folder && data.indexing && !(data.folders && data.folders.length > 0) && !(data.playlists && data.playlists.length > 0)) {
    html +=
      '<div style="padding:1rem;color:var(--muted,#9aa8c2)">&#128197; Still indexing \u2014 tracks will appear here when the scan reaches this folder.</div>';
  } else if (data.folder && !data.indexing && !(data.playlists && data.playlists.length) && !(data.folders && data.folders.length)) {
    html += '<div style="padding:1rem;color:var(--muted,#9aa8c2)">No content.</div>';
  }
  // Close the unified grid opened for folders/playlists in grid mode when there are no tracks
  if (isGrid && !searching && !(data.files && data.files.length)) { html += '</div>'; }
  mb.innerHTML =
    html ||
    '<div style="padding:1rem;color:var(--muted,#9aa8c2)">Select a folder to browse tracks.</div>';

  // Grid-only: apply resolved cover image as hero background so card mode shows art reliably.
  if (isGrid) {
    mb.querySelectorAll('.music-track-card[data-cover-url]').forEach((card) => {
      const hero = card.querySelector('.mtc-play-indicator');
      const img = card.querySelector('.mtc-thumb');
      if (!hero || !img) return;

      const applyHeroBackground = () => {
        const src = img.currentSrc || img.src;
        if (!src) return;
        hero.style.backgroundImage = `url("${src}")`;
        hero.style.backgroundSize = 'cover';
        hero.style.backgroundPosition = 'center';
        hero.style.backgroundRepeat = 'no-repeat';
      };

      if (img.complete && img.naturalWidth > 0) applyHeroBackground();
      img.addEventListener('load', applyHeroBackground);
      img.addEventListener('error', () => {
        hero.style.backgroundImage = '';
      });
    });
  }

  // Grid cover art: wire up two-stage fallback (local → online album art → no-cover placeholder)
  // data-no-cover="1" means the index confirmed no embedded/folder art exists — skip straight to
  // the online fallback if there is an album name or artist+title to search; otherwise show placeholder.
  mb.querySelectorAll('.music-track-card .mtc-thumb[data-album]').forEach(img => {
    if (img.dataset.noCover && !img.dataset.album && !img.dataset.title) { _mtcNoCover(img); return; }

    const fallbackToOnline = () => {
      const artist = img.dataset.artist || '';
      const album  = img.dataset.album  || '';
      const title  = img.dataset.title  || '';
      // Use album if available; fall back to track title — but only when artist is known
      // (artist is required to avoid showing art for a different artist with the same title)
      const searchTerm = album || (artist ? title : '');
      if (!searchTerm) { _mtcNoCover(img); return; }

      const onlineUrl = '/api/music/album-art?album=' + encodeURIComponent(searchTerm)
        + (artist ? '&artist=' + encodeURIComponent(artist) : '');
      img.addEventListener('error', () => _mtcNoCover(img), { once: true });
      img.src = onlineUrl;
    };

    img.addEventListener('error', fallbackToOnline, { once: true });

    // Handle race: local /api/music/cover can fail before listener attachment on fast 404 paths.
    if (img.complete && img.naturalWidth === 0) {
      fallbackToOnline();
    }
  });

  // Attach click/contextmenu handlers for dynamic folders (rendered with data-* attributes)
  document.querySelectorAll('.folder-row-dynamic[data-dynpath]').forEach((el) => {
    const dp = el.dataset.dynpath;
    const dn = el.dataset.dynname;
    el.addEventListener('click', () => _openDynamicFolder(dp, dn));
    el.addEventListener('contextmenu', (e) => _ctxShow(e, 'music-dynamic', { dir: dp, name: dn, dynamicPath: dp }));
  });

  // Attach click handlers for playlist cards and list rows
  document.querySelectorAll('.music-playlist-card, .music-playlist-row').forEach((el) => {
    el.addEventListener('click', () => {
      const path = el.dataset.plpath;
      const name = el.querySelector('.mpl-name, .mpl-list-name, .mtc-title')?.textContent || path;
      playMusicPlaylist(path, name, el);
    });
  });

  // Playlist art is now resolved server-side; apply precomputed art URLs without per-card fetches.
  document.querySelectorAll('.music-playlist-card').forEach((el) => {
    _applyPlaylistArtEl(el);
  });
  document.querySelectorAll('.music-playlist-row').forEach((el) => {
    _applyPlaylistArtEl(el);
  });

  // Attach context-menu bindings
  if (data.files) {
    const sortedForCtx = _musicSortFiles(data.files);
    document.querySelectorAll('.music-track-card:not(.music-folder-row):not(.music-playlist-row)').forEach((el, i) => {
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

async function playMusicPlaylist(path, name, cardEl) {
  try {
    const res = await fetch('/api/music/playlist?path=' + encodeURIComponent(path));
    if (!res.ok) { setStatus('Playlist error ' + res.status); return; }
    const data = await res.json();
    const tracks = data.tracks;
    if (!tracks || !tracks.length) { setStatus('Playlist is empty.'); return; }

    // Resolve album art URL before starting playback so the player bar shows it immediately.
    // Browse-grid cards have _albumArtUrl already set; recent-strip cards don't, so fetch now.
    window._playlistArtUrl = (cardEl && (cardEl._albumArtUrl || cardEl.dataset.plarturl)) || null;

    // Record the playlist itself in history (not the individual tracks)
    fetch('/api/music/play?path=' + encodeURIComponent(path) + '&recordOnly=1').catch(() => {});
    setTimeout(() => loadMusicRecent().then((files) => renderMusicRecent(files)), 400);
    // Load all tracks into a fresh persistent queue; cursor starts at 0
    window._musicQueue = tracks.map((t) => ({ path: t.path, name: t.name }));
    window._musicQueuePos = 0;
    window._musicQueueVisited = new Set([0]);
    window._musicNextShuffleQueuePick = null;
    _updateMusicQueuePendingBar();
    playMusic(tracks[0].path, tracks[0].name || name);
  } catch (e) {
    setStatus('Playlist error: ' + e.message);
  }
}

function loadMoreMusic() {
  if (!currentMusicData) return;
  const offset = (currentMusicData.files || []).length;
  if (currentMusicData.query !== undefined)
    searchMusicLibrary(currentMusicData.query || '', offset, true);
  else browseMusic(currentMusicData.folder || currentMusicFolder, null, offset, true);
}

// kept for compatibility — routes to the card renderer
function renderMusic(data, searching) {
  renderMusicCards(data, searching);
}

function playMusicAll() {
  if (!currentMusicData || !currentMusicData.files || !currentMusicData.files.length) return;
  const sorted = _musicSortFiles(currentMusicData.files);
  if (!sorted.length) return;
  // Put all tracks into the persistent queue with cursor at 0
  window._musicQueue = sorted.map((f) => ({ path: f.path, name: f.name }));
  window._musicQueuePos = 0;
  window._musicQueueVisited = new Set([0]);
  window._musicNextShuffleQueuePick = null;
  _updateMusicQueuePendingBar();
  playMusic(sorted[0].path, sorted[0].name);
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
      setSearchBusy(false, 'video');
      setStatus('Search error ' + res.status);
      return;
    }
    const data = await res.json();
    setSearchBusy(false, 'video');
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
    setSearchBusy(false, 'video');
    if (e.name !== 'AbortError') setStatus('Search failed: ' + e);
    pendingSearchAbort = null;
  }
}
// ── Video command-bar filter / sort state ────────────────────────────
let _videoFilter = 'all'; // 'all' | 'unwatched' | 'watched' | 'fav' | 'progress'
let _videoSort    = 'name'; // 'name' | 'folder' | 'duration' | 'size' | 'progress'
let _videoSortDir = 'asc';  // 'asc' | 'desc'
let _currentRawFiles = []; // last unfiltered file list from server
let _currentFolderRows = []; // folder/link rows shown above videos in list view

function _parseLeadingYearTitle(name) {
  const rawName = (name || '').trim();
  const m = rawName.match(/^((?:19|20)\d{2})\s*[-–—]\s*(.+)$/);
  if (!m) return { title: rawName || 'Folder', year: '' };

  const title = (m[2] || '').trim();
  return { title: title || rawName || 'Folder', year: title ? m[1] : '' };
}

function _toVideoFolderRow(folder, searching, idx) {
  const rawName = folder.name || folder.folder || 'Folder';
  const parsed = _parseLeadingYearTitle(rawName);
  return {
    isFolderRow: true,
    isLink: !!folder.isLink,
    virtualPath: folder.virtualPath || '',
    displayName: rawName,
    folderDisplayName: parsed.title,
    folderDisplayYear: parsed.year,
    folderDir: folder.dir || '',
    searchFolder: folder.folder || folder.name || '',
    searching: Boolean(searching),
    rowKey: 'folder:' + (folder.dir || folder.folder || folder.name || String(idx))
  };
}

function _mapVideoFoldersToListRows(folders, searching) {
  if (!Array.isArray(folders) || !folders.length) return [];
  return folders.map((f, idx) => _toVideoFolderRow(f, searching, idx));
}

function _composeVideoListRows(sortedFiles) {
  // Keep folders and folder links at the top regardless of active video sort.
  return [..._currentFolderRows, ...sortedFiles];
}

function _videoFolderCardHtml(f) {
  const folderName = f.folderDisplayName || f.displayName || 'Folder';
  const folderYear = f.folderDisplayYear || '';
  const folderTypeText = f.isLink ? 'linked folder' : 'folder';
  const folderNameJs = jsStr(folderName);
  const folderDirJs = jsStr(f.folderDir || '');
  const searchFolderJs = jsStr(f.searchFolder || folderName);
  const virtualPathJs = jsStr(f.virtualPath || '');
  const folderCtx = f.searching
    ? '_showFolderCtxMenu(event,null,\'' + searchFolderJs + '\',true)'
    : '_showFolderCtxMenu(event,\'' + folderDirJs + '\',\'' + folderNameJs + '\',false)';
  const action = f.searching
    ? 'searchLibrary(\'' + searchFolderJs + '\')'
    : 'browse(\'' + folderDirJs + '\',0,false,true,' + (f.isLink ? 'true' : 'false') + ',\'' + virtualPathJs + '\')';
  const folderInfoParts = [];
  if (folderYear) folderInfoParts.push('Year ' + folderYear);
  folderInfoParts.push(f.isLink ? 'Linked folder' : 'Folder');
  const folderInfo = folderInfoParts.join(' · ');
  const linkBadge = f.isLink ? '<span class="link-badge" title="Library link">🔗</span>' : '';

  return '<div class="movie-card video-folder-card' + (f.isLink ? ' video-folder-row-link' : '') + '" id="' + cardIdFor(f.rowKey) + '" data-path="' + esc(f.rowKey) + '" role="button" tabindex="0" aria-label="Open ' + folderTypeText + ' ' + esc(folderName) + '" onclick="' + action + '" onkeydown="activateKeyboardClick(event,this)" oncontextmenu="' + folderCtx + '">' +
    '<div class="movie-card-inner">' +
      '<div class="movie-title"><span class="vlc-folder-emoji">&#128193;</span> ' + esc(folderName) + '</div>' +
      linkBadge +
      '<div class="movie-meta">' + esc(folderInfo) + '</div>' +
    '</div>' +
    '</div>';
}

function setVideoFilter(f) {
  _videoFilter = f;
  document.querySelectorAll('#vcb-filters .vcb-pill').forEach(b => b.classList.toggle('active', b.dataset.vf === f));
  _rerenderVideoFiles();
  _videoRenderBreadcrumbTokens(Boolean(currentData?.query), currentData);
}
function setVideoSort(s) {
  if (_videoSort === s) {
    _videoSortDir = _videoSortDir === 'asc' ? 'desc' : 'asc';
  } else {
    _videoSort    = s;
    _videoSortDir = 'asc';
  }
  const sel = document.getElementById('vcb-sort');
  if (sel && sel.value !== _videoSort) sel.value = _videoSort;
  _rerenderVideoFiles();
  _videoRenderBreadcrumbTokens(Boolean(currentData?.query), currentData);
}
function _videoFilterLabel(filter) {
  return {
    all: 'All',
    unwatched: 'Unwatched',
    watched: 'Watched',
    fav: 'Favorites',
    progress: 'In Progress',
  }[filter] || filter;
}
function _videoSortLabel(sort, dir) {
  const name = {
    name: 'Name',
    folder: 'Folder',
    duration: 'Duration',
    year: 'Year',
    progress: 'Progress',
  }[sort] || sort;
  return `${name} ${dir === 'desc' ? '↓' : '↑'}`;
}
function _videoRenderBreadcrumbTokens(searching, data) {
  if (currentMode !== 'video') return;
  const bc = document.getElementById('breadcrumb');
  if (!bc || !data) return;

  const tokens = [];
  const q = (data.query || '').trim();
  if (q) tokens.push({ key: 'search', label: 'Search', value: q });
  if (_videoFilter !== 'all') tokens.push({ key: 'filter', label: 'Filter', value: _videoFilterLabel(_videoFilter) });
  if (_videoSort !== 'name' || _videoSortDir !== 'asc') {
    tokens.push({ key: 'sort', label: 'Sort', value: _videoSortLabel(_videoSort, _videoSortDir) });
  }

  if (!tokens.length) return;

  const details = tokens
    .map((token) => `${token.label}: <button class="radio-crumb-value" type="button" onclick="videoClearBreadcrumbToken('${token.key}')" title="Reset ${token.label}">${esc(token.value)}</button>`)
    .join('<span class="radio-crumb-sep">, </span>');

  bc.innerHTML += `<span> &rsaquo; </span>${details}`;
}

function videoClearBreadcrumbToken(key) {
  if (key === 'search') {
    const search = document.getElementById('vcb-search');
    if (search) search.value = '';
    if (pendingSearchAbort) {
      pendingSearchAbort.abort();
      pendingSearchAbort = null;
    }
    browse(null);
    return;
  }

  if (key === 'filter') {
    setVideoFilter('all');
    return;
  }

  if (key === 'sort') {
    _videoSort = 'name';
    _videoSortDir = 'asc';
    const sel = document.getElementById('vcb-sort');
    if (sel) sel.value = _videoSort;
    _rerenderVideoFiles();
    _videoRenderBreadcrumbTokens(Boolean(currentData?.query), currentData);
  }
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
  const asc = _videoSortDir !== 'desc';
  switch (_videoSort) {
    case 'folder':   arr.sort((a,b) => { const r = (a.folder||'').localeCompare(b.folder||'') || (a.displayName||a.name||'').localeCompare(b.displayName||b.name||''); return asc ? r : -r; }); break;
    case 'duration': arr.sort((a,b) => { const r = (Number(a.duration)||0) - (Number(b.duration)||0); return asc ? -r : r; }); break;
    case 'year':     arr.sort((a,b) => { const ya = _parseYear(a.name||''), yb = _parseYear(b.name||''); const r = (yb||'0').localeCompare(ya||'0') || (a.displayName||a.name||'').localeCompare(b.displayName||b.name||''); return asc ? r : -r; }); break;
    case 'progress': arr.sort((a,b) => { const r = (Number(a.progress)||0) - (Number(b.progress)||0); return asc ? -r : r; }); break;
    default:         arr.sort((a,b) => { const r = (a.displayName||a.name||'').localeCompare(b.displayName||b.name||''); return asc ? r : -r; }); break;
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
  if (!_currentRawFiles.length && !_currentFolderRows.length) return;
  const filtered = _filteredFiles(_currentRawFiles);
  const sorted   = _sortedFiles(filtered);
  const listRows = _composeVideoListRows(sorted);
  const cnt = document.getElementById('vcb-count');
  if (cnt) cnt.textContent = sorted.length + ' / ' + _currentRawFiles.length + ' video(s)';
  _renderVideoFileRows(listRows);
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
      '<a onclick="browse(null)">&#8962; Video Root</a><span> &rsaquo; </span><span class="crumb-current">Search results</span>';
  } else if (data.breadcrumbs && data.breadcrumbs.length) {
    bc.innerHTML += data.breadcrumbs
      .map((c, i) => {
        const sep = i > 0 ? '<span> &rsaquo; </span>' : '';
        const label = i === 0 ? '&#8962; Video Root' : esc(c.name);
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
  if (typeof _setViewModeScope === 'function') {
    if (searching) {
      _setViewModeScope('video', 2, 'Search results');
    } else if (data.breadcrumbs && data.breadcrumbs.length) {
      const last = data.breadcrumbs[data.breadcrumbs.length - 1];
      _setViewModeScope('video', data.breadcrumbs.length, (last && last.name) || 'Video Root');
    } else {
      _setViewModeScope('video', 1, 'Video Root');
    }
  }
  _videoRenderBreadcrumbTokens(searching, data);
  const videoFolderCount = data.folders?.length || 0;
  const videoFileCount = data.files?.length || 0;
  const videoCountText = searching
    ? `${_countWord(videoFolderCount, 'folder', 'folders')} and ${_countWordNoZero(videoFileCount, 'result', 'results')}`
    : `${_countWord(videoFolderCount, 'folder', 'folders')}, ${_countWordNoZero(videoFileCount, 'video', 'videos')}`;
  if (searching) {
    const txt = document.getElementById('count-text');
    if (txt) {
      if (data.hasMoreFiles) {
        txt.innerHTML = videoCountText + ', <a href="#" class="count-load-more" onclick="event.preventDefault();loadMoreFiles()">Load more</a>';
      } else {
        txt.textContent = videoCountText;
      }
    }
  } else {
    const txt = document.getElementById('count-text');
    if (txt) {
      if (data.hasMoreFiles) {
        txt.innerHTML = videoCountText + ', <a href="#" class="count-load-more" onclick="event.preventDefault();loadMoreFiles()">Load more</a>';
      } else {
        txt.textContent = videoCountText;
      }
    }
  }
  document.getElementById('view-toggle-music') &&
    (document.getElementById('view-toggle-music').style.display = 'none');
  // Show video command bar only when still in video mode — avoids overriding tab state
  // when an async browse response arrives after the user has switched to music/radio.
  if (currentMode === 'video') _applyVideoCommandBar(true);
  _applyViewToggleBtn('video');
  let html = '';
  _currentFolderRows = _mapVideoFoldersToListRows(data.folders, searching);

  _setCbRightVisible('vcb-right', data.files.length > 0 && _viewMode.video !== 'list');
  if (data.files.length || _currentFolderRows.length) {
    // Store raw files for filter/sort; render via helper
    _currentRawFiles = data.files || [];
    const filtered = _filteredFiles(_currentRawFiles);
    const sorted   = _sortedFiles(filtered);
    const cnt = document.getElementById('vcb-count');
    if (cnt) cnt.textContent = sorted.length + ' / ' + data.files.length + ' video(s)';
    html += '<div class="section-label" style="display:none">Videos</div>';
    html += '<div id="video-list-grid" class="movie-grid' + (_viewMode.video === 'list' ? ' list-view' : '') + '"></div>';
    // We'll populate the grid after inserting the HTML skeleton
  }
  if (!data.folders.length && !data.files.length) {
    if (data.allPathsInvalid) {
      html = '<div id="empty" class="empty-invalid-path">'
           + '<span class="empty-invalid-icon">&#128193;</span>'
           + '<span class="empty-invalid-title">No video library configured</span>'
           + '<span class="empty-invalid-sub">The configured video path is invalid or does not exist.<br>'
           + 'Go to <a onclick="location.href=\'/settings\'">Settings</a> to update the library path.</span>'
           + '</div>';
    } else {
      html = '<div id="empty">No subfolders or video files here.</div>';
    }
  }
  document.getElementById('browser').innerHTML = html;
  if (data.files.length || _currentFolderRows.length) {
    const sorted2 = _sortedFiles(_filteredFiles(data.files || []));
    const listRows2 = _composeVideoListRows(sorted2);
    _renderVideoFileRows(listRows2);
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

  const HEADER = isList && files.length
    ? '<div class="vlc-header-row">' +
        `<div class="vlc-h vlc-h-title sortable${_videoSort==='name'?' sort-active':''}" onclick="setVideoSort('name')" title="Sort by title">Title${_videoSort==='name'?(_videoSortDir==='asc'?' ▲':' ▼'):''}</div>` +
        `<div class="vlc-h vlc-h-progress sortable${_videoSort==='progress'?' sort-active':''}" onclick="setVideoSort('progress')" title="Sort by progress">Progress${_videoSort==='progress'?(_videoSortDir==='asc'?' ▲':' ▼'):''}</div>` +
        `<div class="vlc-h vlc-h-year sortable${_videoSort==='year'?' sort-active':''}" onclick="setVideoSort('year')" title="Sort by year">Year${_videoSort==='year'?(_videoSortDir==='asc'?' ▲':' ▼'):''}</div>` +
        '<div class="vlc-h vlc-h-ext">Format</div>' +
        `<div class="vlc-h vlc-h-duration sortable${_videoSort==='duration'?' sort-active':''}" onclick="setVideoSort('duration')" title="Sort by duration">Duration${_videoSort==='duration'?(_videoSortDir==='asc'?' ▲':' ▼'):''}</div>` +
        '<div class="vlc-h vlc-h-cb"></div>' +
      '</div>'
    : '';

  let html = HEADER;
  html += files
    .map((f, rowIndex) => {
      if (f.isFolderRow) {
        if (!isList) return _videoFolderCardHtml(f);

        const folderName = f.folderDisplayName || f.displayName || 'Folder';
        const folderYear = f.folderDisplayYear || '';
        const folderTypeText = f.isLink ? 'linked folder' : 'folder';
        const folderNameJs = jsStr(folderName);
        const folderDirJs = jsStr(f.folderDir || '');
        const searchFolderJs = jsStr(f.searchFolder || folderName);
        const virtualPathJs = jsStr(f.virtualPath || '');
        const linkBadgeInline = f.isLink ? ' <span class="folder-link-badge" title="Library link">🔗</span>' : '';
        const titleCol = '<div class="vlc-title"><span class="vlc-title-text"><span class="vlc-folder-emoji">&#128193;</span>' + esc(folderName) + linkBadgeInline + '</span></div>';
        const folderCtx = f.searching
          ? '_showFolderCtxMenu(event,null,\'' + searchFolderJs + '\',true)'
          : '_showFolderCtxMenu(event,\'' + folderDirJs + '\',\'' + folderNameJs + '\',false)';
        const action = f.searching
          ? 'searchLibrary(\'' + searchFolderJs + '\')'
          : 'browse(\'' + folderDirJs + '\',0,false,true,' + (f.isLink ? 'true' : 'false') + ',\'' + virtualPathJs + '\')';

        return '<div class="movie-card video-folder-row' + (f.isLink ? ' video-folder-row-link' : '') + (rowIndex % 2 === 1 ? ' alt-row' : '') + '" id="' + cardIdFor(f.rowKey) + '" data-path="' + esc(f.rowKey) + '" role="button" tabindex="0" aria-label="Open ' + folderTypeText + ' ' + esc(folderName) + '" onclick="' + action + '" onkeydown="activateKeyboardClick(event,this)" oncontextmenu="' + folderCtx + '">' +
          titleCol + '<div class="vlc-progress"></div><div class="vlc-year">' + esc(folderYear) + '</div><div class="vlc-ext"></div><div class="vlc-duration"></div><div class="vlc-cb"></div>' +
          '</div>';
      }

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
      const stateBadges = isList
        ? '<span class="vlc-state-badges">' +
            (played ? '<span class="vlc-state-badge watched">Watched</span>' : '') +
            (favorite ? '<span class="vlc-state-badge favorite">Favorite</span>' : '') +
            (queued ? '<span class="vlc-state-badge queued">Queued</span>' : '') +
          '</span>'
        : '';
      const titleCol = '<div class="vlc-title"><span class="vlc-title-text">' + esc(displayName) + linkBadgeInline + '</span>' + stateBadges + inlineActions + '</div>';
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
  _scheduleDurationPatch(files.filter(f => !f.isFolderRow));
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
    if (f.isPlaylist) {
      const { displayName, artist: parsedArtist } = _parsePlName(f.name || f.tagTitle || f.folder || 'Playlist');
      const albumForArt  = displayName;
      const artistForArt = (f.artist && f.artist.trim()) || parsedArtist || '';
      return (
        '<div class="vr-card vr-card-playlist" role="button" tabindex="0"' +
        ' data-recent-playlist-path="' + esc(f.path) + '"' +
        ' data-recent-playlist-name="' + esc(displayName) + '"' +
        ' data-plalbum="' + esc(albumForArt) + '"' +
        ' data-plartist="' + esc(artistForArt) + '"' +
        ' title="' + esc(displayName) + '"' +
        ' onkeydown="activateKeyboardClick(event,this)">' +
        '<div class="vr-thumb vr-thumb-placeholder vr-thumb-playlist">&#9835;</div>' +
        '<div class="vr-label">' + esc(displayName) + '</div>' +
        '<div class="vr-badge-overlay">PLAYLIST</div>' +
        '</div>'
      );
    }
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
  // Wire clicks via delegation — navigate to folder then play, or relaunch playlist
  if (strip._recentMusicHandler) strip.removeEventListener('click', strip._recentMusicHandler);
  strip._recentMusicHandler = async (e) => {
    // Playlist card
    const plCard = e.target.closest('[data-recent-playlist-path]');
    if (plCard) {
      const encodedPath = plCard.dataset.recentPlaylistPath;
      const name = plCard.dataset.recentPlaylistName;
      if (currentMode !== 'music') switchMode('music', true);
      playMusicPlaylist(encodedPath, name, plCard);
      return;
    }
    // Regular track card
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
  await _stopAllPlaybackForNewStart();

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
  await api('/api/video/play?path=' + encodeURIComponent(p));
  setTimeout(() => loadRecent().then((files) => renderRecent(files)), 300);
  startSse();
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
    syncVideoStateBadges(card);
    syncVideoActionButtons(card);
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
  const card = target?.closest('.movie-card');
  const shouldClear =
    playedVideos.has(p) ||
    Boolean(card?.classList.contains('played')) ||
    Boolean(target?.closest('.continue-card'));
  if (shouldClear) {
    await api('/api/history/clear?path=' + encodeURIComponent(p));
    unmarkPlayed(p);
    setStatus('Resume position cleared.');
  } else {
    markPlayed(p);
  }

  if (card) syncVideoActionButtons(card);
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
  startSse();
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
    syncVideoStateBadges(card);
    syncVideoActionButtons(card);
  }
}
function unmarkPlayed(p) {
  playedVideos.delete(p);
  savePlayedVideos();
  api('/api/history/watched/set?path=' + encodeURIComponent(p) + '&watched=false');
  for (const card of getMovieCards(p)) {
    card.classList.remove('played');
    syncVideoStateBadges(card);
    syncVideoActionButtons(card);
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
function syncVideoActionButtons(card) {
  const isQueued = card.classList.contains('queued');
  const isFavorite = card.classList.contains('favorite');
  const isWatched = card.classList.contains('played');

  card.querySelectorAll('button').forEach((button) => {
    const onclick = String(button.getAttribute('onclick') || '');
    const inline = Boolean(button.closest('.vlc-inline-actions'));

    if (onclick.includes('queueCardAction')) {
      button.textContent = inline
        ? (isQueued ? '− Unqueue' : '+ Queue')
        : (isQueued ? 'Unqueue' : 'Queue');
      return;
    }

    if (onclick.includes('toggleFavoriteCard')) {
      button.textContent = inline
        ? (isFavorite ? '★ Unfav' : '☆ Fav')
        : (isFavorite ? 'Unfavorite' : 'Favorite');
      return;
    }

    if (onclick.includes('toggleWatchedCard')) {
      button.textContent = inline
        ? (isWatched ? '👁 Unwatch' : '✓ Watched')
        : (isWatched ? 'Unwatch' : 'Watched');
    }
  });
}
function syncVideoStateBadges(card) {
  const title = card.querySelector('.vlc-title');
  if (!title) return;

  const watched = card.classList.contains('played');
  const favorite = card.classList.contains('favorite');
  const queued = card.classList.contains('queued');
  let badges = title.querySelector('.vlc-state-badges');

  if (!watched && !favorite && !queued) {
    badges?.remove();
    return;
  }

  if (!badges) {
    badges = document.createElement('span');
    badges.className = 'vlc-state-badges';
    const inlineActions = title.querySelector('.vlc-inline-actions');
    if (inlineActions) title.insertBefore(badges, inlineActions);
    else title.appendChild(badges);
  }

  badges.innerHTML =
    (watched ? '<span class="vlc-state-badge watched">Watched</span>' : '') +
    (favorite ? '<span class="vlc-state-badge favorite">Favorite</span>' : '') +
    (queued ? '<span class="vlc-state-badge queued">Queued</span>' : '');
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
      syncVideoStateBadges(card);
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
  if (document.visibilityState === 'visible' && (playingPath || musicIsPlaying)) requestWakeLock();
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
  if (currentMode !== 'music') return;

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
function _syncMusicDockedOnResize() {
  if (currentMode !== 'music') return;
  const bar = document.getElementById('music-player-bar');
  if (!bar || bar.style.display === 'none') return;
  const isDocked = !isPhoneRemoteOnly && window.matchMedia('(min-width:900px)').matches;
  document.body.classList.toggle('music-player-docked', isDocked);
}
window.addEventListener('orientationchange', () => setTimeout(() => {
  applyPhoneLayout();
  _syncMusicDockedOnResize();
}, 80));
window.addEventListener('resize', () => {
  if (document.body.classList.contains('desktop-player-docked')) applyDesktopDockedLayout(true);
  _syncMusicDockedOnResize();
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
function _bootstrapFromRoute() {
  console.log('[BOOTSTRAP] _bootstrapFromRoute started');
  const route = _parseInitialUiRoute();
  console.log('[BOOTSTRAP] route parsed:', route);

  if (!route.hasExplicitRoute) {
    // No URL route specified; restore from localStorage or default to video
    const savedMode = localStorage.getItem('remotePlayMode') || 'video';
    console.log('[BOOTSTRAP] No explicit URL route; restored mode from localStorage:', savedMode);

    if (savedMode === 'music') {
      console.log('[BOOTSTRAP] Switching to music mode');
      switchMode('music', true);
      browseMusic(null);
      return;
    }
    if (savedMode === 'podcasts') {
      console.log('[BOOTSTRAP] Switching to podcasts mode');
      switchMode('podcasts', true);
      return;
    }
    if (savedMode === 'radio') {
      console.log('[BOOTSTRAP] Switching to radio mode');
      switchMode('radio', true);
      return;
    }
    if (savedMode === 'live-tv') {
      console.log('[BOOTSTRAP] Switching to live-tv mode');
      switchMode('live-tv', true);
      return;
    }
    // Default to video
    console.log('[BOOTSTRAP] Defaulting to video mode');
    browse(null);
    return;
  }

  // URL route detected; switch to the specified mode and start from root
  console.log('[BOOTSTRAP] Explicit URL route detected: mode =', route.mode);

  if (route.mode === 'radio') {
    console.log('[BOOTSTRAP] Switching to radio mode (from URL)');
    switchMode('radio', true);
    return;
  }

  if (route.mode === 'live-tv') {
    console.log('[BOOTSTRAP] Switching to live-tv mode (from URL)');
    switchMode('live-tv', true);
    if (route.routeToken && typeof liveTvApplyRouteToken === 'function') {
      liveTvApplyRouteToken(route.routeToken);
    }
    return;
  }

  if (route.mode === 'music') {
    console.log('[BOOTSTRAP] Switching to music mode (from URL)');
    switchMode('music', true);
    // Use folderPath from sessionStorage if available, otherwise start at root
    const folderToLoad = route.folderPath || null;
    console.log('[BOOTSTRAP] Loading music folder:', folderToLoad);
    browseMusic(folderToLoad);
    return;
  }

  if (route.mode === 'podcasts') {
    console.log('[BOOTSTRAP] Switching to podcasts mode (from URL)');
    switchMode('podcasts', true);
    if (typeof podcastApplyRouteFromUrl === 'function') {
      podcastApplyRouteFromUrl().catch(() => {});
    }
    return;
  }

  console.log('[BOOTSTRAP] Switching to video mode (from URL)');
  switchMode('video', true);
  // Use folderPath from sessionStorage if available, otherwise start at root
  const videoFolderToLoad = route.folderPath ? _encodePath(route.folderPath) : null;
  console.log('[BOOTSTRAP] Loading video folder:', videoFolderToLoad);
  browse(videoFolderToLoad);
}

// Bootstrap is now called at the end of app-local.js after all modules are loaded
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
startSse();
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
    const [peersRes, dlnaRes] = await Promise.all([
      fetch('/api/peers'),
      fetch('/api/dlna/renderers'),
    ]);
    const peers = peersRes.ok ? await peersRes.json() : [];
    const renderers = dlnaRes.ok ? await dlnaRes.json() : [];
    const dot = document.getElementById('peers-dot');
    const others = peers.filter((p) => !p.isSelf);
    const totalOthers = others.length + renderers.length;
    dot.className = totalOthers > 0 ? 'multi' : '';
    const label = totalOthers > 0 ? `Instances (${totalOthers})` : 'Instances';
    const labelEl = document.querySelector('#peers-btn .im-label');
    if (labelEl) labelEl.textContent = label;
    document.getElementById('peers-btn').title =
      totalOthers > 0 ? `${totalOthers} other instance(s)/renderer(s) on the network` : 'No other instances found';
    renderPeersDropdown(peers, renderers);
  } catch {}
}

function renderPeersDropdown(peers, renderers) {
  renderers = renderers || [];
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

  // ── DLNA renderers section ──────────────────────────────────────────────────
  if (renderers.length > 0) {
    const dlnaHeader = document.createElement('div');
    dlnaHeader.className = 'peers-section-header';
    dlnaHeader.textContent = '📺 DLNA renderers';
    dd.appendChild(dlnaHeader);
    renderers.forEach((r) => {
      const row = document.createElement('div');
      row.className = 'peer-item-row';
      const info = document.createElement('div');
      info.className = 'peer-item peer-dlna';
      info.innerHTML = `<span class="peer-dot peer-dot-dlna"></span><span class="peer-info"><div class="peer-name">${esc(r.name)}</div><div class="peer-addr">${esc(r.host)}</div></span>`;
      row.appendChild(info);
      const playBtn = document.createElement('button');
      playBtn.className = 'peer-sync-btn';
      playBtn.title = `Cast current video to ${esc(r.name)}`;
      playBtn.textContent = '📺 Cast';
      playBtn.onclick = (e) => { e.preventDefault(); e.stopPropagation(); playOnDlna(r.controlUrl, r.name); };
      row.appendChild(playBtn);
      dd.appendChild(row);
    });
  }
}

function togglePeers() {
  const dd = document.getElementById('peers-dropdown');
  const btn = document.getElementById('peers-btn');
  const isOpen = dd.classList.toggle('open');
  btn.setAttribute('aria-expanded', String(isOpen));
  if (isOpen) {
    // Close settings panel if open
    const panel = document.getElementById('settings-panel');
    if (panel && panel.style.display !== 'none') {
      panel.style.display = 'none';
      document.getElementById('settings-btn')?.setAttribute('aria-expanded', 'false');
    }
    refreshPeers();
  }
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
    await fetch(`${base}/api/video/play?` + new URLSearchParams({ path: s.filePath }));
    // Allow the peer a moment to start, then seek to match our position
    await new Promise((r) => setTimeout(r, 800));
    await fetch(`${base}/api/seek?` + new URLSearchParams({ position: String(Math.round(s.position)) }));
  } catch (err) {
    alert('Sync failed: ' + err.message);
  }
}

async function playOnDlna(controlUrl, rendererName) {
  closePeers();
  try {
    const statusRes = await fetch('/api/status');
    if (!statusRes.ok) return;
    const s = await statusRes.json();
    if (!s.isPlaying || !s.filePath) {
      alert('Nothing is currently playing to cast.');
      return;
    }
    // Build an HTTP URL for the media file that the DLNA renderer can reach.
    const mediaUrl = `${location.protocol}//${location.host}/api/play?path=${encodeURIComponent(s.filePath)}`;
    const res = await fetch('/api/dlna/play', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ controlUrl, mediaUrl }),
    });
    const data = await res.json();
    if (!data.ok) {
      alert(`Cast to ${rendererName} failed: ${data.error || 'unknown error'}`);
    }
  } catch (err) {
    alert('Cast failed: ' + err.message);
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

// ── rpDynamic folder helpers ──────────────────────────────────────────────────

// State for the settings dialog
let _rpdynMode = 'create'; // 'create' | 'edit'
let _rpdynEditPath = null;  // encoded path of the .rpDynamic file being edited
let _rpdynTargetDir = null; // directory where the new file will be created

/** Called by the Expert Mode checkbox onchange — POSTs the new value to the server so it persists. */
function _toggleExpertMode() {
  const chk = document.getElementById('expert-mode-chk');
  const on = chk ? chk.checked : false;
  _setExpertMode(on);
  fetch('/api/expert-mode', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ expertMode: on })
  }).catch(() => {});
}
/** Called by the Debug Mode checkbox onchange — POSTs the new value to the server so it persists. */
function _toggleDebugMode() {
  const chk = document.getElementById('debug-mode-chk');
  const on = chk ? chk.checked : false;
  _setDebugMode(on);
  fetch('/api/debug-mode', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ debugMode: on })
  }).catch(() => {});
}
function _initExpertMode() {
  _refreshExpertMode();
  _refreshDebugMode();
}
_initExpertMode();

/** Toggle the Settings panel open/closed */
function toggleSettingsPanel() {
  const panel = document.getElementById('settings-panel');
  const btn = document.getElementById('settings-btn');
  if (!panel) return;
  const open = panel.style.display === 'none';
  panel.style.display = open ? '' : 'none';
  if (btn) btn.setAttribute('aria-expanded', open ? 'true' : 'false');
  if (open) {
    // Close peers dropdown if open
    closePeers();
    // Close when clicking outside
    const close = (e) => {
      if (!document.getElementById('settings-wrap')?.contains(e.target)) {
        panel.style.display = 'none';
        if (btn) btn.setAttribute('aria-expanded', 'false');
        document.removeEventListener('click', close, true);
      }
    };
    setTimeout(() => document.addEventListener('click', close, true), 0);
  }
}

function _rpdynParseDurationInput(value) {
  const raw = (value || '').trim();
  if (!raw) return null;

  if (/^\d+$/.test(raw)) return parseInt(raw, 10);

  const parts = raw.split(':').map(p => p.trim());
  if (parts.length !== 2 && parts.length !== 3) return Number.NaN;
  if (parts.some(p => !/^\d+$/.test(p))) return Number.NaN;

  if (parts.length === 2) {
    const mm = parseInt(parts[0], 10);
    const ss = parseInt(parts[1], 10);
    if (ss >= 60) return Number.NaN;
    return mm * 60 + ss;
  }

  const hh = parseInt(parts[0], 10);
  const mm = parseInt(parts[1], 10);
  const ss = parseInt(parts[2], 10);
  if (mm >= 60 || ss >= 60) return Number.NaN;
  return hh * 3600 + mm * 60 + ss;
}

function _rpdynFormatDurationInput(seconds) {
  const sec = Number(seconds);
  if (!Number.isFinite(sec) || sec < 0) return '';
  return fmtSec(sec);
}

function _rpdynNormalizeDurationField(id) {
  const el = document.getElementById(id);
  if (!el) return;
  const parsed = _rpdynParseDurationInput(el.value);
  if (parsed === null) {
    el.value = '';
    return;
  }
  if (!Number.isNaN(parsed)) {
    el.value = _rpdynFormatDurationInput(parsed);
  }
}

/** Called when clicking the Shift+button — opens the creation dialog */
function _createDynamicFolder() {
  _rpdynMode = 'create';
  _rpdynEditPath = null;
  _rpdynTargetDir = (currentMusicData && currentMusicData.folder) || currentMusicFolder || null;
  if (!_rpdynTargetDir) { alert('Navigate into a music folder first.'); return; }
  const dlg = document.getElementById('rpdynamic-dialog');
  if (!dlg) return;
  document.getElementById('rpdynamic-title').textContent = '\uD83C\uDFB2 New Dynamic Folder';
  document.getElementById('rpdyn-name').value = '';
  document.getElementById('rpdyn-sort').value = 'random';
  document.getElementById('rpdyn-mode').value = 'sample';
  document.getElementById('rpdyn-count').value = '20';
  document.getElementById('rpdyn-recursive').checked = true;
  document.getElementById('rpdyn-include').value = '';
  document.getElementById('rpdyn-exclude').value = '';
  document.getElementById('rpdyn-genre').value = '';
  document.getElementById('rpdyn-year-from').value = '';
  document.getElementById('rpdyn-year-to').value = '';
  document.getElementById('rpdyn-min-dur').value = '';
  document.getElementById('rpdyn-max-dur').value = '';
  document.getElementById('rpdyn-max-total-dur').value = '1:00:00';
  document.getElementById('rpdyn-autoplay').checked = false;
  _rpdynModeChange();
  dlg.style.display = 'flex';
  document.getElementById('rpdyn-name').focus();
}

/** Show/hide the count row depending on mode */
function _rpdynModeChange() {
  const modeEl = document.getElementById('rpdyn-mode');
  const countRow = document.getElementById('rpdyn-count-row');
  const maxTotalRow = document.getElementById('rpdyn-max-total-row');
  const mode = modeEl ? modeEl.value : 'sample';

  if (countRow) countRow.style.display = (mode === 'all' || mode === 'maxTotalDuration') ? 'none' : '';
  if (maxTotalRow) maxTotalRow.style.display = mode === 'maxTotalDuration' ? '' : 'none';
}

/** Open the edit dialog for an existing dynamic folder */
async function _editDynamicFolder(encodedPath) {
  _rpdynMode = 'edit';
  _rpdynEditPath = encodedPath;
  _rpdynTargetDir = null;
  const dlg = document.getElementById('rpdynamic-dialog');
  if (!dlg) return;
  document.getElementById('rpdynamic-title').textContent = '\uD83C\uDFB2 Edit Dynamic Folder';
  // Set defaults first
  document.getElementById('rpdyn-name').value = '';
  document.getElementById('rpdyn-sort').value = 'random';
  document.getElementById('rpdyn-mode').value = 'sample';
  document.getElementById('rpdyn-count').value = '20';
  document.getElementById('rpdyn-recursive').checked = true;
  document.getElementById('rpdyn-include').value = '';
  document.getElementById('rpdyn-exclude').value = '';
  document.getElementById('rpdyn-genre').value = '';
  document.getElementById('rpdyn-year-from').value = '';
  document.getElementById('rpdyn-year-to').value = '';
  document.getElementById('rpdyn-min-dur').value = '';
  document.getElementById('rpdyn-max-dur').value = '';
  document.getElementById('rpdyn-max-total-dur').value = '1:00:00';
  document.getElementById('rpdyn-autoplay').checked = false;
  try {
    const res = await fetch('/api/music/dynamic?path=' + encodeURIComponent(encodedPath));
    if (res.ok) {
      const s = await res.json();
      if (s.name)      document.getElementById('rpdyn-name').value      = s.name;
      if (s.sort)      document.getElementById('rpdyn-sort').value      = s.sort;
      if (s.mode)      document.getElementById('rpdyn-mode').value      = s.mode;
      if (s.count != null) document.getElementById('rpdyn-count').value = s.count;
      if (s.recursive != null) document.getElementById('rpdyn-recursive').checked = !!s.recursive;
      if (Array.isArray(s.include) && s.include.length)
        document.getElementById('rpdyn-include').value = s.include.join(', ');
      if (Array.isArray(s.exclude) && s.exclude.length)
        document.getElementById('rpdyn-exclude').value = s.exclude.join(', ');
      if (s.genre) document.getElementById('rpdyn-genre').value = s.genre;
      if (s.yearFrom != null) document.getElementById('rpdyn-year-from').value = s.yearFrom;
      if (s.yearTo   != null) document.getElementById('rpdyn-year-to').value   = s.yearTo;
      if (s.minDuration != null) document.getElementById('rpdyn-min-dur').value = _rpdynFormatDurationInput(s.minDuration);
      if (s.maxDuration != null) document.getElementById('rpdyn-max-dur').value = _rpdynFormatDurationInput(s.maxDuration);
      if (s.maxTotalDuration != null) document.getElementById('rpdyn-max-total-dur').value = _rpdynFormatDurationInput(s.maxTotalDuration);
      if (s.autoPlay != null) document.getElementById('rpdyn-autoplay').checked = !!s.autoPlay;
    }
  } catch (e) { /* keep defaults */ }
  _rpdynModeChange();
  dlg.style.display = 'flex';
  document.getElementById('rpdyn-name').focus();
}

/** Close the settings dialog without saving */
function _closeDynamicDialog() {
  const dlg = document.getElementById('rpdynamic-dialog');
  if (dlg) dlg.style.display = 'none';
  _rpdynEditPath = null;
  _rpdynTargetDir = null;
}

/** Save button — creates or updates the .rpDynamic file */
async function _saveDynamicFolder() {
  const name      = (document.getElementById('rpdyn-name').value.trim())   || 'My Dynamic Folder';
  const sort      = document.getElementById('rpdyn-sort').value             || 'random';
  const mode      = document.getElementById('rpdyn-mode').value             || 'sample';
  const count     = parseInt(document.getElementById('rpdyn-count').value, 10) || 20;
  const recursive = document.getElementById('rpdyn-recursive').checked;
  const includeRaw = document.getElementById('rpdyn-include').value.trim();
  const include   = includeRaw ? includeRaw.split(',').map(s => s.trim()).filter(Boolean) : [];
  const excludeRaw = document.getElementById('rpdyn-exclude').value.trim();
  const exclude   = excludeRaw ? excludeRaw.split(',').map(s => s.trim()).filter(Boolean) : [];
  const genre     = document.getElementById('rpdyn-genre').value.trim();
  const yearFromVal = document.getElementById('rpdyn-year-from').value.trim();
  const yearToVal   = document.getElementById('rpdyn-year-to').value.trim();
  const yearFrom  = yearFromVal ? parseInt(yearFromVal, 10) : null;
  const yearTo    = yearToVal   ? parseInt(yearToVal,   10) : null;
  const minDurVal = document.getElementById('rpdyn-min-dur').value.trim();
  const maxDurVal = document.getElementById('rpdyn-max-dur').value.trim();
  const minDuration = _rpdynParseDurationInput(minDurVal);
  const maxDuration = _rpdynParseDurationInput(maxDurVal);
  const maxTotalDurVal = document.getElementById('rpdyn-max-total-dur').value.trim();
  const maxTotalDuration = _rpdynParseDurationInput(maxTotalDurVal);
  const autoPlay  = document.getElementById('rpdyn-autoplay').checked;

  if (Number.isNaN(minDuration) || Number.isNaN(maxDuration) || Number.isNaN(maxTotalDuration)) {
    alert('Invalid duration. Use seconds, mm:ss, or hh:mm:ss.');
    return;
  }

  const payload = { name, sort, mode, count, recursive, include, exclude, genre, yearFrom, yearTo, minDuration, maxDuration, maxTotalDuration, autoPlay };

  try {
    let res;
    if (_rpdynMode === 'create') {
      res = await fetch('/api/music/dynamic', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ dir: _rpdynTargetDir, ...payload })
      });
    } else {
      res = await fetch('/api/music/dynamic?path=' + encodeURIComponent(_rpdynEditPath), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
    }
    if (!res.ok) { alert('Could not save: ' + await res.text()); return; }
    const saved = await res.json();
    _closeDynamicDialog();
    if (typeof browseMusic === 'function') await browseMusic(currentMusicFolder);
  } catch (e) {
    alert('Error saving dynamic folder: ' + e);
  }
}

/** Re-roll the current dynamic folder — fetch a fresh random set and re-render */
async function _rerollDynamicFolder() {
  const sess = _dynSession;
  if (!sess) return;
  sessionStorage.removeItem('rpdyn_session');
  await _openDynamicFolder(sess.encodedPath, sess.name);
}

/** Adjust the track count of the current dynamic folder by delta and re-roll */
async function _adjustDynCount(delta) {
  const sess = _dynSession;
  if (!sess) return;
  try {
    const res = await fetch('/api/music/dynamic?path=' + encodeURIComponent(sess.encodedPath));
    if (!res.ok) return;
    const s = await res.json();
    const newCount = Math.max(1, (s.count || 20) + delta);
    await fetch('/api/music/dynamic?path=' + encodeURIComponent(sess.encodedPath), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ...s, count: newCount })
    });
    await _openDynamicFolder(sess.encodedPath, sess.name);
  } catch { /* ignore */ }
}

/** Persist / clear the active dynamic session state */
let _dynSession = null;
function _saveDynSession(data) {
  _dynSession = data;
  if (data) sessionStorage.setItem('rpdyn_session', JSON.stringify(data));
  else sessionStorage.removeItem('rpdyn_session');
}

/**
 * Spread same-artist tracks so no two consecutive tracks share the same artist.
 * Uses a greedy interleave approach.
 */
function _dynSpreadArtists(tracks) {
  if (!tracks || tracks.length <= 1) return tracks || [];
  // Group by artist (case-insensitive)
  const groups = new Map();
  for (const t of tracks) {
    const key = (t.artist || '').trim().toLowerCase() || '\x00';
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(t);
  }
  // Priority queue: always pick the group with most remaining tracks whose artist != last picked
  const buckets = [...groups.values()].sort((a, b) => b.length - a.length);
  const result = [];
  let lastArtist = null;
  while (result.length < tracks.length) {
    // Find first bucket where artist differs from last (or all same — just pick largest)
    let chosen = null;
    for (const b of buckets) {
      if (!b.length) continue;
      if ((b[0].artist || '').trim().toLowerCase() !== lastArtist) { chosen = b; break; }
    }
    if (!chosen) {
      // fallback: pick largest non-empty bucket
      chosen = buckets.find(b => b.length > 0);
    }
    if (!chosen) break;
    const t = chosen.shift();
    result.push(t);
    lastArtist = (t.artist || '').trim().toLowerCase();
    // Re-sort buckets by remaining size
    buckets.sort((a, b) => b.length - a.length);
  }
  return result;
}

/** Open a dynamic folder — expand it into a playlist-style track list and start playing */
async function _openDynamicFolder(encodedPath, name) {
  const mb = document.getElementById('music-browser');
  if (mb) mb.innerHTML = '<div style="padding:.75rem;color:var(--muted,#9aa8c2)">\uD83C\uDFB2 Loading dynamic folder\u2026</div>';
  // Set the sentinel immediately so the music scan-status poller never fires
  // browseMusic() and navigates away while the async expand request is in flight.
  const prevFolder = currentMusicFolder && !currentMusicFolder.startsWith('\0dynamic:')
    ? currentMusicFolder : (currentMusicData && currentMusicData._prevFolder) || null;
  currentMusicFolder = '\0dynamic:' + encodedPath;
  try {
    const res = await fetch('/api/music/dynamic/expand?path=' + encodeURIComponent(encodedPath));
    if (!res.ok) { if (mb) mb.innerHTML = '<div style="padding:.75rem">Error ' + res.status + '</div>'; return; }
    const data = await res.json();
    const tracks = _dynSpreadArtists(data.tracks || []);
    const musicRoot = (currentMusicData && currentMusicData.musicRoot) || null;
    const dynamicName = data.name || name;

    const virtualData = {
      folder: prevFolder,        // real parent path → breadcrumb gets full segment chain
      musicRoot,
      folders: [],
      files: tracks,
      totalFiles: tracks.length,
      _isDynamic: true,
      _dynamicName: dynamicName,
      _prevFolder: prevFolder,
    };
    currentMusicData = virtualData;
    currentMusicFolder = '\0dynamic:' + encodedPath; // sentinel (not a real path)

    // Hide the "Create dynamic folder" button — can't nest dynamic folders
    const _dynBtn = document.getElementById('mcb-dynamic-btn');
    if (_dynBtn) _dynBtn.style.display = 'none';

    // Show pin button wired to the .rpDynamic file path (not the virtual sentinel)
    const _pinBtn = document.getElementById('mcb-pin-btn');
    if (_pinBtn) {
      _pinBtn.style.display = '';
      _pinBtn.dataset.dynamicPath = encodedPath;
      _pinBtn.onclick = () => toggleMusicPinFolder(encodedPath, data.name || dynamicName, true);
    }

    // Show the re-roll button (shuffle again)
    const rerollBtn = document.getElementById('mcb-reroll-btn');
    if (rerollBtn) rerollBtn.style.display = '';

    // Show ±5 count adjustment buttons
    const minusBtn = document.getElementById('mcb-dyn-minus');
    const plusBtn  = document.getElementById('mcb-dyn-plus');
    if (minusBtn) minusBtn.style.display = '';
    if (plusBtn)  plusBtn.style.display  = '';

    // Show "Now playing from" label
    const fromLabel = document.getElementById('mcb-dyn-from');
    if (fromLabel) {
      fromLabel.textContent = '\uD83C\uDFB2 ' + (data.name || name);
      fromLabel.style.display = '';
    }

    // Let renderMusicHeader build the parent breadcrumb segments normally …
    if (typeof renderMusicHeader === 'function') renderMusicHeader(virtualData, false);

    // … then append the dynamic folder name as a non-clickable final crumb.
    const bc = document.getElementById('breadcrumb');
    if (bc) {
      bc.innerHTML += '<span> &rsaquo; </span><span class="crumb-current">\uD83C\uDFB2 ' + esc(dynamicName) + '</span>';

      // The last segment that renderMusicHeader made crumb-current is now a middle
      // segment — make it clickable instead.
      const currentSpans = bc.querySelectorAll('.crumb-current');
      if (currentSpans.length > 1) {
        for (let i = 0; i < currentSpans.length - 1; i++) {
          const span = currentSpans[i];
          const a = document.createElement('a');
          a.textContent = span.textContent;
          a.setAttribute('onclick', 'browseMusic(' + (prevFolder ? "'" + jsStr(prevFolder) + "'" : 'null') + ')');
          span.replaceWith(a);
        }
      }
    }
    if (typeof _setViewModeScope === 'function') {
      const level = bc ? Math.max(1, bc.querySelectorAll('a, .crumb-current').length) : 2;
      _setViewModeScope('music', level, 'Dynamic ' + dynamicName);
    }

    // Wire back button to return to the real previous folder
    const back = document.getElementById('back-button');
    if (back) {
      back.onclick = () => {
        _saveDynSession(null);
        const reroll = document.getElementById('mcb-reroll-btn');
        if (reroll) reroll.style.display = 'none';
        const dynBtn = document.getElementById('mcb-dynamic-btn');
        if (dynBtn) dynBtn.style.display = '';
        const pinBtn = document.getElementById('mcb-pin-btn');
        if (pinBtn) {
          pinBtn.style.display = '';
          delete pinBtn.dataset.dynamicPath;
          pinBtn.onclick = () => toggleMusicPinFolder(typeof currentMusicFolder !== 'undefined' ? currentMusicFolder : null);
        }
        const minusBtn = document.getElementById('mcb-dyn-minus');
        if (minusBtn) minusBtn.style.display = 'none';
        const plusBtn = document.getElementById('mcb-dyn-plus');
        if (plusBtn)  plusBtn.style.display  = 'none';
        const fromLabel = document.getElementById('mcb-dyn-from');
        if (fromLabel) fromLabel.style.display = 'none';
        browseMusic(prevFolder);
      };
      back.style.display = '';
    }

    // Empty result — show message in the browser area but keep header/breadcrumb/back intact
    if (!tracks.length) {
      if (mb) mb.innerHTML = '<div style="padding:1.5rem 1rem;text-align:center;color:var(--muted,#9aa8c2)">'
        + '<div style="font-size:2rem;margin-bottom:.5rem">🎲</div>'
        + '<div style="font-size:1rem;font-weight:600;color:var(--text,#dce3f0)">No Tracks Found</div>'
        + '<div style="margin-top:.35rem;font-size:.85rem">The filters for this dynamic folder matched no tracks.<br>Try relaxing the include / exclude rules, genre, or year range.</div>'
        + '</div>';
      return;
    }

    // Persist session so the re-roll button can repeat the expand
    _saveDynSession({ encodedPath, name });

    if (typeof renderMusicCards === 'function') renderMusicCards(virtualData);

    // Auto-play if enabled in settings
    try {
      const settRes = await fetch('/api/music/dynamic?path=' + encodeURIComponent(encodedPath));
      if (settRes.ok) {
        const sett = await settRes.json();
        const sort = (sett && typeof sett.sort === 'string') ? sett.sort.toLowerCase() : '';
        const dynamicWantsShuffle = sort === 'random' || sort === 'shuffle';
        if (sett.autoPlay && typeof playMusicAll === 'function') {
          if (dynamicWantsShuffle) await _setMusicShuffleEnabled(true);
          playMusicAll();
        }
      }
    } catch { /* best-effort */ }
  } catch (e) {
    if (mb) mb.innerHTML = '<div style="padding:.75rem">Error: ' + e + '</div>';
  }
}

// Keyboard shortcut: Enter / Space in the dialog submits
document.addEventListener('keydown', (e) => {
  const dlg = document.getElementById('rpdynamic-dialog');
  if (!dlg || dlg.style.display === 'none') {
    // R key rerolls when in a dynamic folder and no input is focused
    if (e.key === 'r' || e.key === 'R') {
      const active = document.activeElement;
      const isInput = active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA' || active.tagName === 'SELECT');
      if (!isInput && _dynSession) { e.preventDefault(); _rerollDynamicFolder(); }
    }
    return;
  }
  if (e.key === 'Enter') { e.preventDefault(); _saveDynamicFolder(); }
  if (e.key === 'Escape') { e.preventDefault(); _closeDynamicDialog(); }
});

// ── Sync-status pill ────────────────────────────────────────────────────────
// The settings page broadcasts 'sync-start' / 'sync-done' on a shared channel
// so the syncing pill on the main UI lights up even when sync runs in another tab.
(function () {
  function _setSyncBadge(visible) {
    const b = document.getElementById('syncing-badge');
    if (b) b.style.display = visible ? '' : 'none';
  }
  try {
    const ch = new BroadcastChannel('remoteplay-sync');
    ch.onmessage = function (e) {
      if (e.data === 'sync-start') _setSyncBadge(true);
      else if (e.data === 'sync-done') _setSyncBadge(false);
    };
  } catch (_) { /* BroadcastChannel not supported – pill just won't appear */ }
})();
