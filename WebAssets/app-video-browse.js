
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
    if (typeof _lastSearchQuery !== 'undefined') _lastSearchQuery = q;
    if (typeof _showSearchFilterBar === 'function') _showSearchFilterBar(false);
    if (typeof _applySearchFilter === 'function') _applySearchFilter();
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
  const sortEl = document.getElementById('vcb-sort');
  if (sortEl) sortEl.style.display = isList ? 'none' : '';
  if (currentData) {
    const cnt = document.getElementById('vcb-count');
    if (cnt) cnt.style.display = (currentData.files?.length || 0) > 0 ? '' : 'none';
  }
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
  // Show video command bar (hides the old count-line toggle)
  _applyVideoCommandBar(true);
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
  const sortElR = document.getElementById('vcb-sort');
  if (sortElR) sortElR.style.display = _viewMode.video === 'list' ? 'none' : '';
  const cntEl = document.getElementById('vcb-count');
  if (cntEl) cntEl.style.display = data.files.length > 0 ? '' : 'none';
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
  const isList = _viewMode.video === 'list';
  grid.className = 'movie-grid' + (isList ? ' list-view' : '');

  const HEADER = isList
    ? '<div class="vlc-header-row">' +
        '<div class="vlc-h vlc-h-title sortable" onclick="setVideoSort(\'name\')" title="Sort by title">Title</div>' +
        '<div class="vlc-h vlc-h-progress sortable" onclick="setVideoSort(\'progress\')" title="Sort by progress">Progress</div>' +
        '<div class="vlc-h vlc-h-year sortable" onclick="setVideoSort(\'year\')" title="Sort by year">Year</div>' +
        '<div class="vlc-h vlc-h-cb"></div>' +
        '<div class="vlc-h vlc-h-ext">Format</div>' +
        '<div class="vlc-h vlc-h-duration sortable" onclick="setVideoSort(\'duration\')" title="Sort by duration">Duration</div>' +
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
      const titleCol = '<div class="vlc-title"><span class="vlc-title-text">' + esc(displayName) + linkBadgeInline + '</span>' + inlineActions + '</div>';
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
        '" data-sr-title="' + esc((displayName || '').toLowerCase()) +
        '" role="button" tabindex="0" aria-label="Play ' + esc(displayName) +
        '" data-thumb="' + esc(thumbUrl) + '"' +
        action +
        ' onkeydown="activateKeyboardClick(event,this)"' +
        ' oncontextmenu="_ctxShow(event,\'video-file\',{path:\'' + esc(f.path) + '\',name:\'' + esc(displayName) + '\',played:' + played + ',queued:' + queued + ',favorite:' + favorite + '})"' +
        ' onpointerdown="beginCardHold(event,\'' + f.path + '\')"' +
        ' onpointerup="endCardHold()" onpointercancel="endCardHold()" onpointerleave="endCardHold()">'
        // List-mode columns (CSS grid)
        titleCol + progCol + yearCol + cbCol + extCol + durCol + actionsCol +
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

// ── Universal context menu ────────────────────────────────────────────────────
(function () {
  /*
   * _ctx holds the current item info:
   *   type   : 'video-folder' | 'music-folder' | 'video-recent' | 'music-recent' | 'radio-recent' | 'video-pinned'
   *   dir    : base64-encoded folder path  (folder types + video-pinned)
   *   path   : base64-encoded file path    (recent types)
   *   name   : display name
   *   extra  : arbitrary extra data (radio station JSON, etc.)
   */
  let _ctx = null;

  const menu = () => document.getElementById('ctx-menu');

  // Actions visible per type
  const _VISIBLE = {
    'video-folder':   ['open', 'pin', 'unpin', 'search', 'copy'],
    'video-file':     ['play', 'queue', 'fav', 'watched', 'copy'],
    'music-folder':   ['open', 'copy'],
    'music-file':     ['play', 'queue', 'copy'],
    'video-recent':   ['play', 'queue', 'fav', 'watched', 'copy'],
    'music-recent':   ['play', 'queue', 'copy'],
    'radio-station':  ['play', 'fav', 'copy'],
    'radio-recent':   ['play', 'fav', 'copy'],
    'radio-country':  ['open', 'fav', 'copy'],
    'radio-tag':      ['open', 'fav', 'copy'],
    'video-pinned':   ['open', 'unpin', 'copy'],
  };

  function _show(e, type, ctx) {
    e.preventDefault();
    e.stopPropagation();
    _ctx = { type, ...ctx };
    const m = menu();
    if (!m) return;

    const visible = new Set(_VISIBLE[type] || []);

    // Dynamic pin/unpin for video folders
    if (type === 'video-folder') {
      const pinned = typeof isFolderPinned === 'function' && isFolderPinned(ctx.dir);
      visible.delete(pinned ? 'pin' : 'unpin');
    }

    // Dynamic labels based on current item state
    if (type === 'video-file' || type === 'video-recent') {
      const favBtn = m.querySelector('[data-action="fav"]');
      if (favBtn) favBtn.textContent = ctx.favorite ? '♥ Unfavourite' : '♥ Favourite';
      const watchedBtn = m.querySelector('[data-action="watched"]');
      if (watchedBtn) watchedBtn.textContent = ctx.played ? '✔ Mark unwatched' : '✔ Mark watched';
      const queueBtn = m.querySelector('[data-action="queue"]');
      if (queueBtn) queueBtn.textContent = ctx.queued ? '➕ Remove from queue' : '➕ Add to queue';
    } else if (type === 'music-file' || type === 'music-recent') {
      const queueBtn = m.querySelector('[data-action="queue"]');
      if (queueBtn) queueBtn.textContent = ctx.queued ? '➕ Remove from queue' : '➕ Add to queue';
    } else if (type === 'radio-station' || type === 'radio-recent' || type === 'radio-country' || type === 'radio-tag') {
      const favBtn = m.querySelector('[data-action="fav"]');
      if (favBtn) favBtn.textContent = ctx.isFav ? '♥ Unfavourite' : '♥ Favourite';
    }

    m.querySelectorAll('[data-action]').forEach((btn) => {
      btn.style.display = visible.has(btn.dataset.action) ? '' : 'none';
    });

    // Position menu near cursor, keeping inside viewport
    m.style.display = 'block';
    const vw = window.innerWidth, vh = window.innerHeight;
    const mw = m.offsetWidth || 180, mh = m.offsetHeight || 200;
    m.style.left = Math.min(e.clientX, vw - mw - 8) + 'px';
    m.style.top  = Math.min(e.clientY, vh - mh - 8) + 'px';
  }

  function _hide() {
    const m = menu();
    if (m) m.style.display = 'none';
    _ctx = null;
  }

  // ── Global dismiss ────────────────────────────────────────────────────────
  document.addEventListener('click',       (e) => { if (!e.target.closest('#ctx-menu')) _hide(); });
  document.addEventListener('keydown',     (e) => { if (e.key === 'Escape') _hide(); });
  // Suppress native context menu everywhere; only show ours when triggered via _show
  document.addEventListener('contextmenu', (e) => {
    if (!e.target.closest('#ctx-menu')) { e.preventDefault(); _hide(); }
  });

  // ── Action dispatch ───────────────────────────────────────────────────────
  document.addEventListener('DOMContentLoaded', () => {
    const m = menu();
    if (!m) return;
    // Re-parent to <body> so position:fixed isn't trapped by any overflow/stacking context
    if (m.parentElement !== document.body) document.body.appendChild(m);
    m.addEventListener('click', async (e) => {
      const btn = e.target.closest('[data-action]');
      if (!btn || !_ctx) return;
      const action = btn.dataset.action;
      const ctx = _ctx;
      _hide();

      const decodePath = (b64) => { try { return atob(b64); } catch { return b64; } };

      if (action === 'open') {
        if (ctx.type === 'video-folder') {
          if (ctx.isSearch) searchLibrary(ctx.name || decodePath(ctx.dir));
          else browse(ctx.dir);
        }
        else if (ctx.type === 'music-folder') browseMusic(ctx.dir);
        else if (ctx.type === 'video-pinned') browse(ctx.dir);
        else if (ctx.type === 'radio-country') {
          if (currentMode !== 'radio') switchMode('radio', true);
          const country = document.getElementById('radio-country');
          if (country) country.value = ctx.value || '';
          if (typeof radioOnCountryChange === 'function') await radioOnCountryChange();
        } else if (ctx.type === 'radio-tag') {
          if (currentMode !== 'radio') switchMode('radio', true);
          const tag = document.getElementById('radio-tag');
          if (tag) tag.value = ctx.value || '';
          if (typeof _syncFavSelectHeart === 'function') _syncFavSelectHeart('radio-tag', 'radioToggleTagFav');
        }
      } else if (action === 'play') {
        if (ctx.type === 'video-file') {
          if (typeof onCardClick === 'function') onCardClick({ stopPropagation: () => {} }, ctx.path);
          else if (typeof play === 'function') play(ctx.path, ctx.name);
        } else if (ctx.type === 'video-recent') {
          const path = decodePath(ctx.path);
          const lastSlash = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
          const rawFolder = lastSlash > 0 ? path.substring(0, lastSlash) : null;
          const folderArg = rawFolder ? btoa(unescape(encodeURIComponent(rawFolder))) : null;
          if (currentMode !== 'video') switchMode('video', true);
          await browse(folderArg);
          await play(ctx.path, ctx.name);
        } else if (ctx.type === 'music-file' || ctx.type === 'music-recent') {
          if (currentMode !== 'music') switchMode('music', true);
          await playMusic(ctx.path, ctx.name);
        } else if (ctx.type === 'radio-station') {
          if (ctx.extra && typeof radioPlayStation === 'function') {
            const s = JSON.parse(ctx.extra);
            radioPlayStation(encodeURIComponent(s.url_resolved || s.url || ''),
              encodeURIComponent(s.name || ''), encodeURIComponent(s.country || ''),
              encodeURIComponent((s.tags || '').split(',')[0] || ''),
              encodeURIComponent(ctx.extra));
          }
        } else if (ctx.type === 'radio-recent') {
          if (ctx.extra) {
            const s = JSON.parse(ctx.extra);
            if (typeof radioPlayStation === 'function')
              radioPlayStation(encodeURIComponent(s.url), encodeURIComponent(s.name),
                encodeURIComponent(s.country || ''), encodeURIComponent((s.tags || '').split(',')[0] || ''),
                encodeURIComponent(ctx.extra));
          }
        }
      } else if (action === 'queue') {
        if ((ctx.type === 'video-recent' || ctx.type === 'video-file') && typeof queueCardAction === 'function') {
          await queueCardAction({ stopPropagation: () => {} }, ctx.path);
        } else if (ctx.type === 'music-file' || ctx.type === 'music-recent') {
          if (!window._musicQueue) window._musicQueue = [];
          const existing = window._musicQueue.findIndex((q) => q.path === ctx.path);
          if (existing >= 0) window._musicQueue.splice(existing, 1);
          else window._musicQueue.push({ path: ctx.path, name: ctx.name });
          if (typeof renderMusicCards === 'function' && typeof currentMusicData !== 'undefined' && currentMusicData)
            renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
          if (typeof _refreshMusicNavLabels === 'function') _refreshMusicNavLabels();
          if (typeof _renderMusicQueuePeek === 'function') _renderMusicQueuePeek();
          if (typeof _updateMusicQueuePendingBar === 'function') _updateMusicQueuePendingBar();
        }
      } else if (action === 'fav') {
        if ((ctx.type === 'video-recent' || ctx.type === 'video-file') && typeof toggleFavoriteCard === 'function')
          await toggleFavoriteCard({ stopPropagation: () => {} }, ctx.path);
        else if ((ctx.type === 'radio-station' || ctx.type === 'radio-recent') && ctx.extra && typeof radioToggleFav === 'function') {
          // radio-recent extra is already encodeURIComponent'd; radio-station extra is raw JSON
          const enc = ctx.type === 'radio-recent' ? ctx.extra : encodeURIComponent(ctx.extra);
          await radioToggleFav(null, enc);
        } else if (ctx.type === 'radio-country' && typeof radioToggleCountryFav === 'function') {
          radioToggleCountryFav(ctx.value);
        } else if (ctx.type === 'radio-tag' && typeof radioToggleTagFav === 'function') {
          radioToggleTagFav(ctx.value);
        }
      } else if (action === 'watched') {
        if (ctx.type === 'video-file' && typeof toggleWatchedCard === 'function')
          await toggleWatchedCard({ stopPropagation: () => {} }, ctx.path);
      } else if (action === 'pin') {
        if (typeof togglePinFolder === 'function') togglePinFolder(ctx.dir);
      } else if (action === 'unpin') {
        if (ctx.type === 'video-pinned') {
          if (typeof _unpinFolder === 'function') _unpinFolder(ctx.dir);
        } else {
          if (typeof togglePinFolder === 'function') togglePinFolder(ctx.dir);
        }
      } else if (action === 'search') {
        const decoded = ctx.isSearch ? (ctx.name || decodePath(ctx.dir)) : decodePath(ctx.dir);
        const srch = document.getElementById('search');
        if (srch) { srch.value = decoded; srch.dispatchEvent(new Event('input')); }
      } else if (action === 'copy') {
        let raw = ctx.path ? decodePath(ctx.path)
                : ctx.dir  ? decodePath(ctx.dir)
                : ctx.name || '';
        if ((ctx.type === 'radio-station' || ctx.type === 'radio-recent') && ctx.extra) {
          try {
            const parsed = JSON.parse(ctx.type === 'radio-recent' ? decodeURIComponent(ctx.extra) : ctx.extra);
            raw = parsed.url_resolved || parsed.url || raw;
          } catch {}
        } else if (ctx.type === 'radio-country' || ctx.type === 'radio-tag') {
          raw = ctx.value || ctx.name || '';
        }
        navigator.clipboard?.writeText(raw).catch(() => {});
      }
    });
  });

  // ── Public API (called from inline oncontextmenu handlers) ────────────────
  window._ctxShow = _show;
  // Legacy alias kept for any existing inline handlers
  window._showFolderCtxMenu = (e, dir, name, isSearch) =>
    _show(e, 'video-folder', { dir, name, isSearch });
})();
