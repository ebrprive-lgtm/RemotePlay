// ── Universal context menu ────────────────────────────────────────────────
(function () {
  let _ctx = null;

  const menu = () => document.getElementById('ctx-menu');

  const visibleActions = {
    'video-folder': ['open', 'pin', 'unpin', 'search', 'copy'],
    'video-file': ['play', 'queue', 'fav', 'watched', 'copy'],
    'music-folder': ['open', 'pin', 'unpin', 'copy'],
    'music-file': ['play', 'queue', 'copy'],
    'music-pinned': ['open', 'unpin', 'copy'],
    'video-recent': ['play', 'queue', 'fav', 'watched', 'copy'],
    'music-recent': ['play', 'queue', 'copy'],
    'radio-station': ['play', 'fav', 'copy'],
    'radio-recent': ['play', 'fav', 'copy'],
    'radio-country': ['open', 'fav', 'copy'],
    'radio-tag': ['open', 'fav', 'copy'],
    'video-pinned': ['open', 'unpin', 'copy'],
  };

  function decodePath(value) {
    if (!value) return '';
    try { return decodeURIComponent(escape(atob(value))); }
    catch { try { return atob(value); } catch { return value; } }
  }

  function setLabel(menuElement, action, text) {
    const button = menuElement.querySelector('[data-action="' + action + '"]');
    if (button) button.textContent = text;
  }

  function show(e, type, ctx) {
    e.preventDefault();
    e.stopPropagation();

    _ctx = { type, ...ctx };
    const menuElement = menu();
    if (!menuElement) return;

    const visible = new Set(visibleActions[type] || []);

    if (type === 'video-folder') {
      const pinned = typeof isFolderPinned === 'function' && isFolderPinned(ctx.dir);
      visible.delete(pinned ? 'pin' : 'unpin');
    }

    if (type === 'music-folder') {
      const pinned = typeof isMusicFolderPinned === 'function' && isMusicFolderPinned(ctx.dir);
      visible.delete(pinned ? 'pin' : 'unpin');
    }

    if (type === 'video-file' || type === 'video-recent') {
      setLabel(menuElement, 'fav', ctx.favorite ? '♥ Unfavourite' : '♥ Favourite');
      setLabel(menuElement, 'watched', ctx.played ? '✔ Mark unwatched' : '✔ Mark watched');
      setLabel(menuElement, 'queue', ctx.queued ? '➕ Remove from queue' : '➕ Add to queue');
    } else if (type === 'music-file' || type === 'music-recent') {
      setLabel(menuElement, 'queue', ctx.queued ? '➕ Remove from queue' : '➕ Add to queue');
    } else if (type === 'radio-station' || type === 'radio-recent' || type === 'radio-country' || type === 'radio-tag') {
      setLabel(menuElement, 'fav', ctx.isFav ? '♥ Unfavourite' : '♥ Favourite');
    }

    menuElement.querySelectorAll('[data-action]').forEach((button) => {
      button.style.display = visible.has(button.dataset.action) ? '' : 'none';
    });

    menuElement.style.display = 'block';
    const menuWidth = menuElement.offsetWidth || 190;
    const menuHeight = menuElement.offsetHeight || 220;
    const x = e.clientX ?? e.touches?.[0]?.clientX ?? 0;
    const y = e.clientY ?? e.touches?.[0]?.clientY ?? 0;
    menuElement.style.left = Math.max(8, Math.min(x, window.innerWidth - menuWidth - 8)) + 'px';
    menuElement.style.top = Math.max(8, Math.min(y, window.innerHeight - menuHeight - 8)) + 'px';
  }

  function hide() {
    const menuElement = menu();
    if (menuElement) menuElement.style.display = 'none';
    _ctx = null;
  }

  document.addEventListener('click', (e) => { if (!e.target.closest('#ctx-menu')) hide(); });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') hide(); });
  document.addEventListener('contextmenu', (e) => {
    if (!e.target.closest('#ctx-menu')) {
      e.preventDefault();
      hide();
    }
  });

  document.addEventListener('DOMContentLoaded', () => {
    const menuElement = menu();
    if (!menuElement) return;
    if (menuElement.parentElement !== document.body) document.body.appendChild(menuElement);

    menuElement.addEventListener('click', async (e) => {
      const button = e.target.closest('[data-action]');
      if (!button || !_ctx) return;

      const action = button.dataset.action;
      const ctx = _ctx;
      hide();

      if (action === 'open') {
        if (ctx.type === 'video-folder') {
          if (ctx.isSearch) searchLibrary(ctx.name || decodePath(ctx.dir));
          else browse(ctx.dir);
        } else if (ctx.type === 'music-folder') {
          browseMusic(ctx.dir);
        } else if (ctx.type === 'music-pinned') {
          browseMusic(ctx.dir);
        } else if (ctx.type === 'video-pinned') {
          browse(ctx.dir);
        } else if (ctx.type === 'radio-country') {
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
        } else if (ctx.type === 'radio-station' && ctx.extra && typeof radioPlayStation === 'function') {
          const station = JSON.parse(ctx.extra);
          radioPlayStation(encodeURIComponent(station.url_resolved || station.url || ''), encodeURIComponent(station.name || ''), encodeURIComponent(station.country || ''), encodeURIComponent((station.tags || '').split(',')[0] || ''), encodeURIComponent(ctx.extra));
        } else if (ctx.type === 'radio-recent' && ctx.extra && typeof radioPlayStation === 'function') {
          const station = JSON.parse(ctx.extra);
          radioPlayStation(encodeURIComponent(station.url), encodeURIComponent(station.name), encodeURIComponent(station.country || ''), encodeURIComponent((station.tags || '').split(',')[0] || ''), encodeURIComponent(ctx.extra));
        }
      } else if (action === 'queue') {
        if ((ctx.type === 'video-file' || ctx.type === 'video-recent') && typeof queueCardAction === 'function') {
          await queueCardAction({ stopPropagation: () => {} }, ctx.path);
        } else if (ctx.type === 'music-file' || ctx.type === 'music-recent') {
          if (!window._musicQueue) window._musicQueue = [];
          const existing = window._musicQueue.findIndex((q) => q.path === ctx.path);
          if (existing >= 0) window._musicQueue.splice(existing, 1);
          else window._musicQueue.push({ path: ctx.path, name: ctx.name });
          if (typeof currentMusicData !== 'undefined' && currentMusicData) renderMusicCards(currentMusicData, Boolean(currentMusicData.query));
          if (typeof _refreshMusicNavLabels === 'function') _refreshMusicNavLabels();
          if (typeof _renderMusicQueuePeek === 'function') _renderMusicQueuePeek();
          if (typeof _updateMusicQueuePendingBar === 'function') _updateMusicQueuePendingBar();
        }
      } else if (action === 'fav') {
        if ((ctx.type === 'video-file' || ctx.type === 'video-recent') && typeof toggleFavoriteCard === 'function') {
          await toggleFavoriteCard({ stopPropagation: () => {} }, ctx.path);
        } else if ((ctx.type === 'radio-station' || ctx.type === 'radio-recent') && ctx.extra && typeof radioToggleFav === 'function') {
          const encoded = ctx.type === 'radio-recent' ? ctx.extra : encodeURIComponent(ctx.extra);
          await radioToggleFav(null, encoded);
        } else if (ctx.type === 'radio-country' && typeof radioToggleCountryFav === 'function') {
          radioToggleCountryFav(ctx.value);
        } else if (ctx.type === 'radio-tag' && typeof radioToggleTagFav === 'function') {
          radioToggleTagFav(ctx.value);
        }
      } else if (action === 'watched') {
        if ((ctx.type === 'video-file' || ctx.type === 'video-recent') && typeof toggleWatchedCard === 'function') {
          await toggleWatchedCard({ stopPropagation: () => {}, currentTarget: null }, ctx.path);
        }
      } else if (action === 'pin') {
        if (ctx.type === 'music-folder' && typeof toggleMusicPinFolder === 'function') toggleMusicPinFolder(ctx.dir);
        else if (typeof togglePinFolder === 'function') togglePinFolder(ctx.dir);
      } else if (action === 'unpin') {
        if (ctx.type === 'music-pinned' && typeof _unpinMusicFolder === 'function') _unpinMusicFolder(ctx.dir);
        else if (ctx.type === 'music-folder' && typeof toggleMusicPinFolder === 'function') toggleMusicPinFolder(ctx.dir);
        else if (ctx.type === 'video-pinned' && typeof _unpinFolder === 'function') _unpinFolder(ctx.dir);
        else if (typeof togglePinFolder === 'function') togglePinFolder(ctx.dir);
      } else if (action === 'search') {
        const decoded = ctx.isSearch ? (ctx.name || decodePath(ctx.dir)) : decodePath(ctx.dir);
        const search = document.getElementById('search');
        if (search) {
          search.value = decoded;
          search.dispatchEvent(new Event('input'));
        }
      } else if (action === 'copy') {
        let raw = ctx.path ? decodePath(ctx.path) : ctx.dir ? decodePath(ctx.dir) : ctx.name || '';
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

  window._ctxShow = show;
  window._showFolderCtxMenu = (e, dir, name, isSearch) => show(e, 'video-folder', { dir, name, isSearch });
  window._showMusicFolderCtxMenu = (e, dir, name) => show(e, 'music-folder', { dir, name });
})();
