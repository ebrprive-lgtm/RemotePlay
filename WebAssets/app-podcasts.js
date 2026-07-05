// ── Podcasts ─────────────────────────────────────────────────────────
let _podcastsInited = false;
let _podcastShows = [];
let _podcastEpisodes = [];
let _podcastSelectedShow = null;
let _podcastDirectory = [];
let _podcastDirectoryLoading = false;
let _podcastLastSearch = { query: '', topic: '', country: '' };
let _podcastShowsOffset = 0;
let _podcastShowsHasMore = false;
let _podcastShowsTotal = null;
const _podcastShowsPageSize = 50;
let _podcastEpisodesOffset = 0;
let _podcastEpisodesHasMore = false;
let _podcastEpisodesTotal = null;
const _podcastEpisodesPageSize = 80;
let _podcastCurrentEpisodeIndex = -1;
let _podcastCountryNames = new Map([['US', 'United States']]);
let _podcastFavoriteShowFeeds = [];
let _podcastFavoritesOnly = false;
let _podcastFavCountries = [];
let _podcastFavTopics = [];
let _podcastSearchDebounceTimer = null;
let _podcastFavoriteShowMap = new Map();

const _podcastFavoriteShowsKey = 'rp_podcast_favorite_shows';
const _podcastFavoriteShowMapKey = 'rp_podcast_favorite_show_map';
const _podcastFavoriteCountriesKey = 'rp_podcast_favorite_countries';
const _podcastFavoriteTopicsKey = 'rp_podcast_favorite_topics';

const _podcastQuickTopics = ['News', 'Technology', 'Business', 'History', 'Science', 'Comedy', 'True Crime', 'Health'];
const _podcastAllTopics = [
  'News',
  'Technology',
  'Business',
  'History',
  'Science',
  'Comedy',
  'True Crime',
  'Health',
  'Sports',
  'Society & Culture',
  'Education',
  'Arts',
  'TV & Film',
  'Music',
  'Leisure',
  'Kids & Family',
  'Religion & Spirituality',
  'Government',
  'Fiction',
  'Documentary'
];

function podcastsInit() {
  const root = document.getElementById('podcasts-browser');
  if (!root) return;

  _podcastFavoritesOnly = true;
  _syncPodcastRoute(_podcastSelectedShow?.feedUrl || '', _podcastSelectedShow?.title || '');

  _syncPodcastCommandBar();
  _syncPodcastPlaybackControls();

  if (!_podcastsInited) {
    _podcastsInited = true;
    _podcastLoadFavoriteShows();
    _podcastLoadFilterFavorites();
    _wirePodcastControls();
    _loadPodcastTopics();

    if (_podcastFavoritesOnly) _renderPodcastShows();
    else _renderPodcastDirectoryLoading();

    _loadPodcastCountries().finally(() => {
      _loadPodcastDirectory();
    });
    return;
  }

  if (_podcastSelectedShow) _renderPodcastEpisodes();
  else if (_podcastFavoritesOnly) _renderPodcastShows();
  else if (_podcastShows.length) _renderPodcastShows();
  else _renderPodcastDirectory();
}

function _podcastNormalizeFeedUrl(url) {
  return String(url || '').trim().replace(/\/$/, '').toLowerCase();
}

function _podcastNormalizeFilterValue(value) {
  return String(value || '').trim();
}

function _podcastLoadFavoriteShows() {
  try {
    const raw = JSON.parse(localStorage.getItem(_podcastFavoriteShowsKey) || '[]');
    _podcastFavoriteShowFeeds = Array.isArray(raw)
      ? raw.map((v) => _podcastNormalizeFeedUrl(v)).filter(Boolean)
      : [];
  } catch {
    _podcastFavoriteShowFeeds = [];
  }

  try {
    const rawMap = JSON.parse(localStorage.getItem(_podcastFavoriteShowMapKey) || '{}');
    const entries = Object.entries(rawMap || {});
    _podcastFavoriteShowMap = new Map(entries);
  } catch {
    _podcastFavoriteShowMap = new Map();
  }
}

function _podcastSaveFavoriteShows() {
  try {
    localStorage.setItem(_podcastFavoriteShowsKey, JSON.stringify(_podcastFavoriteShowFeeds));
  } catch {}

  try {
    localStorage.setItem(_podcastFavoriteShowMapKey, JSON.stringify(Object.fromEntries(_podcastFavoriteShowMap.entries())));
  } catch {}
}

function _podcastSnapshotShow(show) {
  return {
    title: show?.title || 'Untitled Podcast',
    author: show?.author || '',
    feedUrl: show?.feedUrl || '',
    artworkUrl: show?.artworkUrl || '',
    genre: show?.genre || '',
    country: show?.country || '',
  };
}

function _podcastRememberShow(show) {
  const key = _podcastNormalizeFeedUrl(show?.feedUrl || '');
  if (!key) return;
  _podcastFavoriteShowMap.set(key, _podcastSnapshotShow(show));
}

function _podcastGetFavoriteShows() {
  const seen = new Set();
  const list = [];

  for (const feed of _podcastFavoriteShowFeeds) {
    const key = _podcastNormalizeFeedUrl(feed);
    if (!key || seen.has(key)) continue;

    const show = _podcastFavoriteShowMap.get(key) || {
      title: 'Favorite Podcast',
      author: '',
      feedUrl: key,
      artworkUrl: '',
      genre: '',
      country: '',
    };

    seen.add(key);
    list.push(_podcastSnapshotShow(show));
  }

  return list;
}

function _podcastLoadFilterFavorites() {
  try {
    const countries = JSON.parse(localStorage.getItem(_podcastFavoriteCountriesKey) || '[]');
    _podcastFavCountries = Array.isArray(countries)
      ? countries.map((v) => _podcastNormalizeFilterValue(v).toUpperCase()).filter(Boolean)
      : [];
  } catch {
    _podcastFavCountries = [];
  }

  try {
    const topics = JSON.parse(localStorage.getItem(_podcastFavoriteTopicsKey) || '[]');
    _podcastFavTopics = Array.isArray(topics)
      ? topics.map((v) => _podcastNormalizeFilterValue(v)).filter(Boolean)
      : [];
  } catch {
    _podcastFavTopics = [];
  }
}

function _podcastSaveFilterFavorites() {
  try {
    localStorage.setItem(_podcastFavoriteCountriesKey, JSON.stringify(_podcastFavCountries));
  } catch {}

  try {
    localStorage.setItem(_podcastFavoriteTopicsKey, JSON.stringify(_podcastFavTopics));
  } catch {}
}

function podcastIsCountryFavorite(code) {
  const key = _podcastNormalizeFilterValue(code).toUpperCase();
  return key ? _podcastFavCountries.includes(key) : false;
}

function podcastIsTopicFavorite(topic) {
  const key = _podcastNormalizeFilterValue(topic);
  return key ? _podcastFavTopics.includes(key) : false;
}

function podcastToggleCountryFavorite(code, evt) {
  if (evt) {
    evt.preventDefault();
    evt.stopPropagation();
  }

  const key = _podcastNormalizeFilterValue(code).toUpperCase();
  if (!key) return;

  const index = _podcastFavCountries.indexOf(key);
  if (index >= 0) _podcastFavCountries.splice(index, 1);
  else _podcastFavCountries.push(key);

  _podcastSaveFilterFavorites();
  _loadPodcastCountries();
}

function podcastToggleTopicFavorite(topic, evt) {
  if (evt) {
    evt.preventDefault();
    evt.stopPropagation();
  }

  const key = _podcastNormalizeFilterValue(topic);
  if (!key) return;

  const index = _podcastFavTopics.indexOf(key);
  if (index >= 0) _podcastFavTopics.splice(index, 1);
  else _podcastFavTopics.push(key);

  _podcastSaveFilterFavorites();
  _loadPodcastTopics();
}

function podcastsToggleFavoritesFilter() {
  _podcastFavoritesOnly = !_podcastFavoritesOnly;
  _syncPodcastRoute(_podcastSelectedShow?.feedUrl || '', _podcastSelectedShow?.title || '');
  _syncPodcastCommandBar();

  if (_podcastSelectedShow) {
    _renderPodcastEpisodes();
    return;
  }

  if (_podcastFavoritesOnly) {
    _renderPodcastShows();
    return;
  }

  _podcastShows = [];
  _podcastShowsOffset = 0;
  _podcastShowsHasMore = false;
  _podcastShowsTotal = null;
  podcastsSearch();
}

function podcastIsShowFavorite(feedUrl) {
  const key = _podcastNormalizeFeedUrl(feedUrl);
  return key ? _podcastFavoriteShowFeeds.includes(key) : false;
}

function podcastToggleShowFavorite(feedUrl, evt) {
  if (evt) {
    evt.preventDefault();
    evt.stopPropagation();
  }

  const key = _podcastNormalizeFeedUrl(feedUrl);
  if (!key) return;

  const index = _podcastFavoriteShowFeeds.indexOf(key);
  if (index >= 0) {
    _podcastFavoriteShowFeeds.splice(index, 1);
  } else {
    _podcastFavoriteShowFeeds.push(key);

    const fromShows = _podcastShows.find((s) => _podcastNormalizeFeedUrl(s.feedUrl || '') === key);
    if (fromShows) _podcastRememberShow(fromShows);
  }
  _podcastSaveFavoriteShows();
  _renderPodcastsCurrentView();
}

function _syncPodcastRoute(feedUrl, showTitle) {
  if (!window.history || typeof window.history.replaceState !== 'function') return;

  let route = '/podcasts';
  const query = new URLSearchParams();

  if (_podcastFavoritesOnly) query.set('fav', '1');

  if (feedUrl) {
    query.set('feed', feedUrl);
    if (showTitle) query.set('show', showTitle);
  }

  if (query.size) route += '?' + query.toString();

  const currentRoute = window.location.pathname + (window.location.search || '');
  if (currentRoute === route) return;
  try {
    window.history.replaceState(window.history.state, '', route);
  } catch {
    // Some feed URLs are too long for browser history state URL updates.
    // Keep playback working even if the route cannot be updated.
  }
}

async function podcastApplyRouteFromUrl() {
  const params = new URLSearchParams(window.location.search || '');

  const fav = (params.get('fav') || '').trim().toLowerCase();
  if (fav === '1' || fav === 'true' || fav === 'yes' || fav === 'on') {
    _podcastFavoritesOnly = true;
  }

  const feedUrl = (params.get('feed') || '').trim();
  if (!feedUrl) {
    _renderPodcastsCurrentView();
    return;
  }

  const showTitle = (params.get('show') || 'Podcast').trim();
  await podcastOpenShow(feedUrl, showTitle || 'Podcast');
}

function _renderPodcastsCurrentView() {
  _syncPodcastCommandBar();
  if (_podcastSelectedShow) _renderPodcastEpisodes();
  else if (_podcastFavoritesOnly) _renderPodcastShows();
  else if (_podcastShows.length) _renderPodcastShows();
  else _renderPodcastDirectory();
}

function _syncPodcastCommandBar() {
  if (typeof _setViewModeScopeFromSegments === 'function') {
    const segments = ['Podcasts'];
    const topic = (_podcastLastSearch.topic || '').trim();
    const query = (_podcastLastSearch.query || '').trim();
    const country = (_podcastLastSearch.country || '').trim();
    if (_podcastSelectedShow?.title) {
      segments.push(_podcastSelectedShow.title);
    } else if (_podcastFavoritesOnly) {
      segments.push('Favorites');
    } else if (!_podcastShows.length && !topic && !query && !country) {
      segments.push('Directory');
    } else {
      if (topic) segments.push(topic);
      else if (query) segments.push('Search: ' + query);
      else segments.push('Top Podcasts');
      if (country) segments.push(country);
    }
    _setViewModeScopeFromSegments('podcasts', segments);
  }
  if (typeof _applyViewToggleBtn === 'function') _applyViewToggleBtn('podcasts');

  const videoToggle = document.getElementById('view-toggle-video');
  if (videoToggle) videoToggle.style.display = 'none';
  const musicToggle = document.getElementById('view-toggle-music');
  if (musicToggle) musicToggle.style.display = 'none';

  const btn = document.getElementById('pcb-view-btn');
  if (btn) {
    const isList = _viewMode.podcasts === 'list';
    btn.textContent = isList ? '⊞ Grid' : '☰ List';
    btn.title = isList ? 'Switch to grid view' : 'Switch to list view';
  }

  const favBtn = document.getElementById('pcb-fav-pill');
  if (favBtn) {
    favBtn.classList.toggle('active', _podcastFavoritesOnly);
    favBtn.innerHTML = _podcastFavoritesOnly ? '&#10084; Favorites' : '&#9825; Favorites';
    favBtn.title = _podcastFavoritesOnly ? 'Show all podcasts' : 'Show favorite podcasts only';
  }

  const pcbCount = document.getElementById('pcb-count');
  if (pcbCount) {
    pcbCount.innerHTML = '';
    pcbCount.style.display = 'none';
  }

  const pcbRight = document.getElementById('pcb-right');
  if (pcbRight) pcbRight.style.display = 'none';
}

function _renderPodcastHeader({
  breadcrumb,
  countText,
  showBack = false,
  backHandler = null,
}) {
  const bc = document.getElementById('breadcrumb');
  const count = document.getElementById('count-text');
  const back = document.getElementById('back-button');

  if (bc) bc.innerHTML = breadcrumb || '';
  if (count) count.innerHTML = countText || '';
  if (back) {
    back.style.display = showBack ? 'block' : 'none';
    back.onclick = showBack && typeof backHandler === 'function' ? backHandler : () => {};
  }

  const pcbCount = document.getElementById('pcb-count');
  if (pcbCount) {
    pcbCount.innerHTML = '';
    pcbCount.style.display = 'none';
  }
}

function _wirePodcastControls() {
  const search = document.getElementById('pcb-search');
  const topic = document.getElementById('pcb-topic');
  const country = document.getElementById('pcb-country');

  const queueSearch = (immediate = false) => {
    if (_podcastFavoritesOnly) {
      _renderPodcastShows();
      return;
    }

    if (_podcastSearchDebounceTimer) {
      clearTimeout(_podcastSearchDebounceTimer);
      _podcastSearchDebounceTimer = null;
    }
    if (immediate) {
      podcastsSearch();
      return;
    }
    _podcastSearchDebounceTimer = setTimeout(() => {
      _podcastSearchDebounceTimer = null;
      podcastsSearch();
    }, 260);
  };

  if (search) {
    search.addEventListener('input', () => queueSearch(false));
    search.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter') return;
      e.preventDefault();
      queueSearch(true);
    });
  }
  if (topic) {
    topic.addEventListener('change', () => {
      const heart = document.getElementById('pcb-topic-heart');
      if (heart) {
        heart.classList.toggle('active', !!topic.value && podcastIsTopicFavorite(topic.value));
      }
      queueSearch(false);
    });
  }
  if (country) {
    country.addEventListener('change', () => {
      const heart = document.getElementById('pcb-country-heart');
      if (heart) {
        heart.classList.toggle('active', !!country.value && podcastIsCountryFavorite(country.value));
      }
      queueSearch(false);
    });
  }
}

function _loadPodcastTopics() {
  const select = document.getElementById('pcb-topic');
  const heart = document.getElementById('pcb-topic-heart');
  if (!select) return;

  const selected = (select.value || '').trim();
  const uniqueTopics = [...new Set(_podcastAllTopics.map((t) => (t || '').trim()).filter(Boolean))]
    .sort((a, b) => a.localeCompare(b));

  const favorites = uniqueTopics.filter((topic) => podcastIsTopicFavorite(topic));
  const others = uniqueTopics.filter((topic) => !podcastIsTopicFavorite(topic));

  const options = ['<option value="">All topics</option>'];
  if (favorites.length && others.length) {
    options.push('<optgroup label="❤ Favorites">');
    favorites.forEach((topic) => {
      options.push(`<option value="${esc(topic)}">${esc(topic)}</option>`);
    });
    options.push('</optgroup><optgroup label="─────">');
    others.forEach((topic) => {
      options.push(`<option value="${esc(topic)}">${esc(topic)}</option>`);
    });
    options.push('</optgroup>');
  } else {
    [...favorites, ...others].forEach((topic) => {
      options.push(`<option value="${esc(topic)}">${esc(topic)}</option>`);
    });
  }

  select.innerHTML = options.join('');
  if (selected && uniqueTopics.includes(selected)) select.value = selected;
  if (heart) {
    heart.classList.toggle('active', !!select.value && podcastIsTopicFavorite(select.value));
  }
}

async function _loadPodcastCountries() {
  const select = document.getElementById('pcb-country');
  const heart = document.getElementById('pcb-country-heart');
  if (!select) return;

  _podcastCountryNames = new Map([['US', 'United States']]);
  select.innerHTML = '<option value="">All countries</option><option value="US">United States</option>';
  select.value = '';

  try {
    const r = await fetch('/api/podcasts/countries');
    if (!r.ok) return;
    const countries = await r.json();
    if (!Array.isArray(countries) || countries.length === 0) return;

    const selected = select.value || '';
    const options = ['<option value="">All countries</option>'];
    const entries = [];
    countries.forEach((c) => {
      const code = (c.code || '').trim();
      const name = (c.name || '').trim();
      if (!code || !name) return;
      _podcastCountryNames.set(code.toUpperCase(), name);
      entries.push({ code: code.toUpperCase(), name });
    });

    const favorites = entries.filter((c) => podcastIsCountryFavorite(c.code));
    const others = entries.filter((c) => !podcastIsCountryFavorite(c.code));
    if (favorites.length && others.length) {
      options.push('<optgroup label="❤ Favorites">');
      favorites.forEach((c) => {
        options.push(`<option value="${esc(c.code)}">${esc(c.name)}</option>`);
      });
      options.push('</optgroup><optgroup label="─────">');
      others.forEach((c) => {
        options.push(`<option value="${esc(c.code)}">${esc(c.name)}</option>`);
      });
      options.push('</optgroup>');
    } else {
      [...favorites, ...others].forEach((c) => {
        options.push(`<option value="${esc(c.code)}">${esc(c.name)}</option>`);
      });
    }

    select.innerHTML = options.join('');
    select.value = selected;
  } catch {
    // Keep fallback options when country list cannot be loaded.
  }

  if (heart) {
    heart.classList.toggle('active', !!select.value && podcastIsCountryFavorite(select.value));
  }
}

async function podcastsSearch() {
  const root = document.getElementById('podcasts-browser');
  if (!root) return;

  if (_podcastFavoritesOnly) {
    _renderPodcastShows();
    return;
  }

  const search = (document.getElementById('pcb-search')?.value || '').trim();
  const topic = (document.getElementById('pcb-topic')?.value || '').trim();
  const country = (document.getElementById('pcb-country')?.value || '').trim();
  _podcastLastSearch = { query: search, topic, country };
  const hasFilters = Boolean(search || topic || country);
  _podcastShowsOffset = 0;
  _podcastShowsHasMore = false;
  _podcastShowsTotal = null;

  root.innerHTML = '<div style="padding:.8rem;color:var(--muted,#9aa8c2)">Searching podcasts…</div>';

  try {
    await _loadPodcastShowsPage({ append: false });
    _podcastEpisodes = [];
    _podcastSelectedShow = null;
    if (_podcastShows.length > 0 || hasFilters) _renderPodcastShows();
    else _renderPodcastDirectory();
    if (typeof setStatus === 'function') {
      setStatus(_podcastShows.length > 0 ? `Found ${_podcastShows.length} podcast show(s).` : 'No podcasts found for this search.');
    }
  } catch (error) {
    const details = error?.message ? ` (${error.message})` : '';
    console.error('Podcast search failed', {
      error,
      query: _podcastLastSearch,
      offset: _podcastShowsOffset,
      append: false,
    });
    root.innerHTML = `<div style="padding:.8rem;color:#ff8f8f">Podcast search failed${esc(details)}.</div>`;
    if (typeof setStatus === 'function') setStatus('Podcast search failed.');
  }
}

async function _loadPodcastShowsPage({ append }) {
  const search = (_podcastLastSearch.query || '').trim();
  const topic = (_podcastLastSearch.topic || '').trim();
  const country = (_podcastLastSearch.country || '').trim();

  const requestPage = async ({ includeTerm }) => {
    const query = new URLSearchParams();
    if (includeTerm && search) query.set('q', search);
    if (includeTerm && topic) query.set('topic', topic);
    if (country) query.set('country', country);
    query.set('limit', String(_podcastShowsPageSize));
    query.set('offset', String(_podcastShowsOffset));

    const url = '/api/podcasts/search?' + query.toString();
    const r = await fetch(url);
    if (!r.ok) {
      let body = '';
      try {
        body = await r.text();
      } catch {
        body = '';
      }
      throw new Error(`podcast search failed (${r.status}) url=${url}${body ? ` body=${body}` : ''}`);
    }

    return r.json();
  };

  let data;
  const topicOnlySearch = Boolean(topic && !search);
  try {
    data = await requestPage({ includeTerm: true });
  } catch (error) {
    console.warn('Primary podcast search request failed, trying fallback request without term', {
      error,
      query: _podcastLastSearch,
      offset: _podcastShowsOffset,
    });
    const term = `${search} ${topic}`.trim().toLowerCase();
    if (!term) throw error;

    try {
      const fallbackData = await requestPage({ includeTerm: false });
      const fallbackShows = Array.isArray(fallbackData.shows) ? fallbackData.shows : [];
      const filtered = fallbackShows.filter((s) => {
        const haystack = `${s?.title || ''} ${s?.author || ''} ${s?.genre || ''}`.toLowerCase();
        return haystack.includes(term);
      });

      data = {
        shows: filtered,
        hasMore: false,
        total: filtered.length,
      };
    } catch {
      throw error;
    }
  }

  if (topicOnlySearch && (!Array.isArray(data?.shows) || data.shows.length === 0)) {
    try {
      const fallbackData = await requestPage({ includeTerm: false });
      const fallbackShows = Array.isArray(fallbackData.shows) ? fallbackData.shows : [];
      const term = topic.toLowerCase();
      const filtered = fallbackShows.filter((s) => {
        const haystack = `${s?.title || ''} ${s?.author || ''} ${s?.genre || ''}`.toLowerCase();
        return haystack.includes(term);
      });

      data = {
        shows: filtered,
        hasMore: false,
        total: filtered.length,
      };
    } catch {
      // Keep original empty response if fallback enrichment fails.
    }
  }

  const page = Array.isArray(data.shows) ? data.shows : [];
  const hasMore = Boolean(data.hasMore);
  const total = _toOptionalNumber(data.total);

  if (append) {
    const seen = new Set(_podcastShows.map((s) => ((s.feedUrl || s.title || '').trim().toLowerCase())));
    for (const show of page) {
      const key = ((show.feedUrl || show.title || '').trim().toLowerCase());
      if (!key || seen.has(key)) continue;
      seen.add(key);
      _podcastShows.push(show);
    }
  } else {
    const unique = [];
    const seen = new Set();
    for (const show of page) {
      const key = ((show.feedUrl || show.title || '').trim().toLowerCase());
      if (!key || seen.has(key)) continue;
      seen.add(key);
      unique.push(show);
    }
    _podcastShows = unique;
  }

  _podcastShowsOffset += _podcastShowsPageSize;
  _podcastShowsHasMore = hasMore;
  _podcastShowsTotal = total;
}

async function podcastLoadMoreShows() {
  if (!_podcastShowsHasMore) return;

  try {
    await _loadPodcastShowsPage({ append: true });
    _renderPodcastShows();
  } catch (error) {
    console.error('Failed to load more podcasts', error);
    if (typeof setStatus === 'function') setStatus('Failed to load more podcasts.');
  }
}

async function podcastOpenShow(feedUrl, showTitle, artworkUrl) {
  if (!feedUrl || currentMode !== 'podcasts') return;

  const root = document.getElementById('podcasts-browser');
  if (!root) return;

  root.innerHTML = '<div style="padding:.8rem;color:var(--muted,#9aa8c2)">Loading episodes…</div>';

  try {
    const knownArt = (artworkUrl || '').trim()
      || ((_podcastShows.find((s) => (s.feedUrl || '').trim() === feedUrl)?.artworkUrl || '').trim());
    _podcastSelectedShow = { title: showTitle || 'Podcast', feedUrl, artworkUrl: knownArt };
    _podcastEpisodes = [];
    _podcastCurrentEpisodeIndex = -1;
    _podcastEpisodesOffset = 0;
    _podcastEpisodesHasMore = false;
    _podcastEpisodesTotal = null;
    await _loadPodcastEpisodesPage({ append: false });
    _syncPodcastRoute(feedUrl, showTitle || 'Podcast');
    _renderPodcastEpisodes();
  } catch (error) {
    console.error('Failed to load podcast episodes', {
      error,
      feedUrl,
      showTitle,
      selectedShow: _podcastSelectedShow,
    });

    const detail = error?.message ? ` (${error.message})` : '';
    root.innerHTML = `<div style="padding:.8rem;color:#ff8f8f">Failed to load episodes${esc(detail)}.</div>`;
    if (typeof setStatus === 'function') setStatus('Failed to load podcast episodes.' + (error?.message ? ` ${error.message}` : ''));
  }
}

async function _loadPodcastEpisodesPage({ append }) {
  if (!_podcastSelectedShow?.feedUrl) return;

  const query = new URLSearchParams({
    url: _podcastSelectedShow.feedUrl,
    limit: String(_podcastEpisodesPageSize),
    offset: String(_podcastEpisodesOffset),
  });
  const r = await fetch('/api/podcasts/feed?' + query.toString());
  if (!r.ok) {
    let body = '';
    try {
      body = (await r.text()).trim();
    } catch {
      body = '';
    }
    throw new Error(`podcast feed failed (${r.status})${body ? ` body=${body}` : ''}`);
  }

  const data = await r.json();
  const page = Array.isArray(data.episodes) ? data.episodes : [];
  const hasMore = Boolean(data.hasMore);
  const total = _toOptionalNumber(data.total);

  if (append) _podcastEpisodes.push(...page);
  else _podcastEpisodes = page;

  _podcastEpisodesOffset += page.length;
  _podcastEpisodesHasMore = hasMore;
  _podcastEpisodesTotal = total;
}

async function podcastLoadMoreEpisodes() {
  if (!_podcastEpisodesHasMore) return;

  try {
    await _loadPodcastEpisodesPage({ append: true });
    _renderPodcastEpisodes();
  } catch {
    if (typeof setStatus === 'function') setStatus('Failed to load more episodes.');
  }
}

function podcastBackToShows() {
  _podcastSelectedShow = null;
  _podcastEpisodes = [];
  _podcastCurrentEpisodeIndex = -1;
  _syncPodcastPlaybackControls();
  _syncPodcastRoute(null, null);
  if (_podcastFavoritesOnly) _renderPodcastShows();
  else if (_podcastShows.length) _renderPodcastShows();
  else _renderPodcastDirectory();
}

async function podcastPlayEpisode(episodeIndex) {
  const index = Number(episodeIndex);
  if (!Number.isInteger(index) || index < 0 || index >= _podcastEpisodes.length) return false;

  const episode = _podcastEpisodes[index] || null;
  const audioUrl = (episode?.audioUrl || '').trim();
  const title = (episode?.title || '').trim() || 'Podcast episode';
  if (!audioUrl) return false;

  try {
    _podcastCurrentEpisodeIndex = index;
    const playLocal = typeof isPlayLocal === 'function' && isPlayLocal();

    // Podcasts share the radio player bar (both local and server playback).
    // Ensure any stale video-player UI is removed before we start the episode.
    if (typeof setPlayerBarVisible === 'function') setPlayerBarVisible(false);
    if (typeof applyDesktopDockedLayout === 'function') applyDesktopDockedLayout(false);
    document.body.classList.remove('phone-playing');
    if (typeof updatePlayingCard === 'function') updatePlayingCard(null, playingPath);
    if (typeof playingPath !== 'undefined') playingPath = null;

    if (playLocal) {
      await fetch('/api/radio/stop', { method: 'POST' }).catch(() => {});
      if (typeof stopRadioStatusPoll === 'function') stopRadioStatusPoll();
      document.body.classList.remove('radio-player-docked');

      if (typeof localPlay !== 'function') {
        if (typeof setStatus === 'function') setStatus('Podcast playback is unavailable in this browser.');
        return false;
      }

      if (typeof _radioCurrentUrl !== 'undefined') _radioCurrentUrl = audioUrl;
      if (typeof _radioCurrentName !== 'undefined') _radioCurrentName = title;
      if (typeof _radioCurrentCountry !== 'undefined') _radioCurrentCountry = '';
      if (typeof _radioCurrentTag !== 'undefined') _radioCurrentTag = 'Podcast';
      if (typeof _radioCurrentStation !== 'undefined') _radioCurrentStation = null;
      if (typeof _radioIsPlaying !== 'undefined') _radioIsPlaying = true;

      localPlay(audioUrl, title, 'Podcast');
    } else {
      if (typeof localStop === 'function') localStop();

      const query = new URLSearchParams({ url: audioUrl, title });
      const r = await fetch('/api/podcasts/play?' + query.toString(), { method: 'POST' });
      if (!r.ok) {
        if (typeof setStatus === 'function') setStatus('Podcast playback failed.');
        return false;
      }

      if (typeof _radioCurrentUrl !== 'undefined') _radioCurrentUrl = audioUrl;
      if (typeof _radioCurrentName !== 'undefined') _radioCurrentName = title;
      if (typeof _radioCurrentCountry !== 'undefined') _radioCurrentCountry = '';
      if (typeof _radioCurrentTag !== 'undefined') _radioCurrentTag = 'Podcast';
      if (typeof _radioCurrentStation !== 'undefined') _radioCurrentStation = null;
      if (typeof _radioIsPlaying !== 'undefined') _radioIsPlaying = true;

      if (typeof _localMediaType !== 'undefined') _localMediaType = 'PodcastServer';
      document.body.classList.add('radio-player-docked');
      document.body.classList.add('podcast-player-mode');
      if (typeof updateRadioBar === 'function') updateRadioBar(title, '', 'Podcast', true, null);

      if (typeof startRadioStatusPoll === 'function') startRadioStatusPoll();
    }

    _syncPodcastPlaybackControls();

    if (typeof setStatus === 'function') setStatus('Playing podcast episode…');
    return true;
  } catch {
    if (typeof setStatus === 'function') setStatus('Podcast playback failed.');
    return false;
  }
}

async function podcastPrevEpisode() {
  if (_podcastCurrentEpisodeIndex <= 0) {
    _syncPodcastPlaybackControls();
    return false;
  }

  return podcastPlayEpisode(_podcastCurrentEpisodeIndex - 1);
}

async function podcastNextEpisode(options) {
  const autoAdvance = Boolean(options && options.autoAdvance);
  const nextIndex = _podcastCurrentEpisodeIndex + 1;
  if (nextIndex < 0 || nextIndex >= _podcastEpisodes.length) {
    _syncPodcastPlaybackControls();
    if (autoAdvance && typeof localStop === 'function') localStop();
    return false;
  }

  return podcastPlayEpisode(nextIndex);
}

function _syncPodcastPlaybackControls() {
  const prevBtn = document.getElementById('local-podcast-prev');
  const nextBtn = document.getElementById('local-podcast-next');
  const indexLabel = document.getElementById('local-podcast-index');
  const isPodcast = typeof _localMediaType !== 'undefined' && (_localMediaType === 'Podcast' || _localMediaType === 'PodcastServer');
  const total = _podcastEpisodes.length;
  const active = _podcastCurrentEpisodeIndex >= 0 && _podcastCurrentEpisodeIndex < total;

  if (prevBtn) prevBtn.style.display = isPodcast && total > 0 ? '' : 'none';
  if (nextBtn) nextBtn.style.display = isPodcast && total > 0 ? '' : 'none';
  if (indexLabel) indexLabel.style.display = isPodcast && total > 0 ? '' : 'none';

  if (prevBtn) prevBtn.disabled = !active || _podcastCurrentEpisodeIndex <= 0;
  if (nextBtn) nextBtn.disabled = !active || _podcastCurrentEpisodeIndex >= total - 1;

  if (indexLabel) {
    indexLabel.textContent = active ? `Episode ${_podcastCurrentEpisodeIndex + 1} / ${total}` : '';
  }
}

function podcastPickTopic(topic) {
  const topicInput = document.getElementById('pcb-topic');
  if (topicInput) topicInput.value = topic || '';
  podcastsSearch();
}

function podcastsResetFilters() {
  const search = document.getElementById('pcb-search');
  const topic = document.getElementById('pcb-topic');
  const country = document.getElementById('pcb-country');

  if (search) search.value = '';
  if (topic) topic.value = '';
  if (country) country.value = 'US';

  _podcastLastSearch = { query: '', topic: '', country: country?.value || '' };
  _podcastShows = [];
  _podcastShowsOffset = 0;
  _podcastShowsHasMore = false;
  _podcastShowsTotal = null;
  _podcastSelectedShow = null;
  _podcastEpisodes = [];
  _podcastCurrentEpisodeIndex = -1;
  _syncPodcastPlaybackControls();

  _renderPodcastDirectoryLoading();
  _loadPodcastDirectory();
}

async function _loadPodcastDirectory() {
  if (_podcastDirectoryLoading) return;
  _podcastDirectoryLoading = true;

  const country = (document.getElementById('pcb-country')?.value || '').trim();
  const topicLimit = 12;

  try {
    const sections = [];

    const topShows = await _fetchPodcastShows({ country, limit: 40 });
    if (topShows.length > 0) {
      sections.push({ topic: 'Top Podcasts', shows: topShows.slice(0, 12) });
    }

    // Fetch a few topic sections sequentially to reduce upstream throttling.
    for (const topic of _podcastQuickTopics.slice(0, 4)) {
      const shows = await _fetchPodcastShows({ country, topic, limit: topicLimit });
      if (shows.length > 0) {
        sections.push({ topic, shows: shows.slice(0, 8) });
      }
    }

    _podcastDirectory = sections;
  } finally {
    _podcastDirectoryLoading = false;
  }

  if (_podcastSelectedShow) return;

  if (_podcastFavoritesOnly) {
    _renderPodcastShows();
    return;
  }

  if (_podcastShows.length === 0) _renderPodcastDirectory();
}

async function _fetchPodcastShows({ country, topic = '', limit = 12 }) {
  const query = new URLSearchParams();
  if (country) query.set('country', country);
  if (topic) query.set('topic', topic);
  query.set('limit', String(limit));

  try {
    const r = await fetch('/api/podcasts/search?' + query.toString());
    if (!r.ok) return [];
    const data = await r.json();
    return Array.isArray(data.shows) ? data.shows : [];
  } catch {
    return [];
  }
}

function _renderPodcastDirectoryLoading() {
  const root = document.getElementById('podcasts-browser');
  if (!root) return;

  root.innerHTML = '<div style="padding:.8rem;color:var(--muted,#9aa8c2)">Loading podcast topics…</div>';
}

function _renderPodcastDirectory() {
  const root = document.getElementById('podcasts-browser');
  if (!root) return;

  _syncPodcastCommandBar();

  if (_podcastDirectoryLoading) {
    _renderPodcastHeader({
      breadcrumb: '<span class="crumb-current">Podcasts</span>',
      countText: 'Loading topics…',
      showBack: false,
    });
    _renderPodcastDirectoryLoading();
    return;
  }

  const totalShows = _podcastDirectory.reduce((sum, section) => sum + (section.shows?.length || 0), 0);
  _renderPodcastHeader({
    breadcrumb: '<span class="crumb-current">Podcasts</span><span> &rsaquo; </span><span class="crumb-current">Directory</span>',
    countText: `${_podcastDirectory.length} topic section${_podcastDirectory.length === 1 ? '' : 's'}, ${totalShows} show${totalShows === 1 ? '' : 's'}`,
    showBack: false,
  });

  const topicChips = _podcastQuickTopics
    .map((topic) => `<button class="btn btn-dim" type="button" onclick="podcastPickTopic('${jsStr(topic)}')">${esc(topic)}</button>`)
    .join('');

  if (_podcastDirectory.length === 0) {
    root.innerHTML = `
      <section class="card" style="padding:.8rem">
        <h3 style="margin:.1rem 0 .5rem">Browse by topic</h3>
        <div style="display:flex;gap:.4rem;flex-wrap:wrap">${topicChips}</div>
        <div style="margin-top:.7rem;color:var(--muted,#9aa8c2)">No topic directory entries were loaded. Try selecting a topic above.</div>
      </section>`;
    return;
  }

  const sections = _podcastDirectory
    .map((section) => {
      const isList = _viewMode.podcasts === 'list';
      const cards = section.shows
        .filter((show) => !_podcastFavoritesOnly || podcastIsShowFavorite(show.feedUrl || ''))
        .map((show) => {
          _podcastRememberShow(show);
          const title = show.title || 'Untitled Podcast';
          const author = show.author || '';
          const art = show.artworkUrl || '';
          const isFav = podcastIsShowFavorite(show.feedUrl || '');
          return `
            <article class="movie-card" style="padding:.55rem">
              <div style="display:flex;gap:.55rem;align-items:flex-start">
                <div onclick="podcastOpenShow('${jsStr(show.feedUrl || '')}','${jsStr(title)}','${jsStr(art)}')" style="cursor:pointer;width:${isList ? '44px' : '58px'};height:${isList ? '44px' : '58px'};flex:0 0 auto;border-radius:.4rem;overflow:hidden;background:#1b1b2c;display:flex;align-items:center;justify-content:center">
                  ${art ? `<img src="${esc(art)}" alt="" style="width:100%;height:100%;display:block;object-fit:cover;object-position:center;filter:${isList ? 'brightness(.88) saturate(.9)' : 'brightness(.78) saturate(.85)'}" loading="lazy"/>` : '<span style="opacity:.6">🎙️</span>'}
                </div>
                <div style="min-width:0;flex:1;display:grid;gap:.22rem">
                  <div style="display:flex;align-items:center;gap:.35rem;justify-content:space-between;min-width:0">
                    <button type="button" onclick="podcastOpenShow('${jsStr(show.feedUrl || '')}','${jsStr(title)}','${jsStr(art)}')" style="all:unset;cursor:pointer;font-weight:650;line-height:1.2;text-decoration:underline;text-decoration-color:transparent;min-width:0" onmouseover="this.style.textDecorationColor='currentColor'" onmouseout="this.style.textDecorationColor='transparent'">${esc(title)}</button>
                    <button type="button" class="radio-fav-btn${isFav ? ' active' : ''}" onclick="podcastToggleShowFavorite('${jsStr(show.feedUrl || '')}',event)" title="${isFav ? 'Remove from favorites' : 'Add to favorites'}" aria-label="${isFav ? 'Remove from favorites' : 'Add to favorites'}">${isFav ? '&#10084;' : '&#9825;'}</button>
                  </div>
                  ${author ? `<div style="font-size:.84rem;color:var(--muted,#9aa8c2)">${esc(author)}</div>` : ''}
                </div>
              </div>
            </article>`;
        })
        .join('');

      if (!cards) return '';

      return `
        <section class="card" style="padding:.65rem">
          <div style="display:flex;justify-content:space-between;align-items:center;gap:.6rem;margin-bottom:.45rem">
            <h3 style="margin:0">${esc(section.topic)}</h3>
            <button class="btn btn-dim" type="button" onclick="podcastPickTopic('${jsStr(section.topic)}')">View more</button>
          </div>
          <div class="movie-grid${_viewMode.podcasts === 'list' ? ' podcast-directory-list' : ''}" style="gap:.45rem">${cards}</div>
        </section>`;
    })
    .join('');

  root.innerHTML = `
    <section class="card" style="padding:.8rem;margin-bottom:.65rem">
      <h3 style="margin:.1rem 0 .5rem">Browse by topic</h3>
      <div style="display:flex;gap:.4rem;flex-wrap:wrap">${topicChips}</div>
    </section>
    <div style="display:grid;gap:.65rem">${sections}</div>`;
}

function _renderPodcastShows() {
  const root = document.getElementById('podcasts-browser');
  if (!root) return;

  _syncPodcastCommandBar();

  const crumbs = ['<a onclick="_podcastShows=[];_renderPodcastDirectory();">Podcasts</a>'];
  const query = (_podcastLastSearch.query || '').trim();
  const topic = (_podcastLastSearch.topic || '').trim();
  const country = (_podcastLastSearch.country || '').trim();
  if (_podcastFavoritesOnly) {
    crumbs.push('<span class="crumb-current">Favorites</span>');
  } else {
    if (topic) crumbs.push(`<span class="crumb-current">${esc(topic)}</span>`);
    else if (query) crumbs.push(`<span class="crumb-current">Search: ${esc(query)}</span>`);
    else crumbs.push('<span class="crumb-current">Top Podcasts</span>');
    if (country) crumbs.push(`<span class="crumb-current">${esc(country)}</span>`);
  }

  _renderPodcastHeader({
    breadcrumb: crumbs.join('<span> &rsaquo; </span>'),
    countText: _buildPodcastCountHtml(_podcastShows.length, _podcastShowsTotal, _podcastShowsHasMore, 'podcastLoadMoreShows()'),
    showBack: !_podcastFavoritesOnly && Boolean(topic || query || country),
    backHandler: () => {
      _podcastShows = [];
      _renderPodcastDirectory();
    },
  });

  if (!_podcastFavoritesOnly) {
    _podcastShows.forEach((s) => _podcastRememberShow(s));
  }

  const visibleShows = _podcastFavoritesOnly
    ? _podcastGetFavoriteShows()
    : _podcastShows;

  if (!visibleShows.length) {
    root.innerHTML = _podcastFavoritesOnly
      ? '<div style="padding:.8rem;color:var(--muted,#9aa8c2)">No favorite podcasts yet.</div>'
      : '<div style="padding:.8rem;color:var(--muted,#9aa8c2)">No podcasts found. Try a different topic or country.</div>';
    return;
  }

  const isList = _viewMode.podcasts === 'list';
  const listHeader = isList
    ? `<div class="podcast-list-header"><span>Podcast</span><span>Country</span><span>Topics</span></div>`
    : '';

  const cards = visibleShows
    .map((show) => {
      const title = show.title || 'Untitled Podcast';
      const author = show.author || '';
      const genre = show.genre || '';
      const country = (show.country || '').trim().toUpperCase();
      const countryName = _podcastCountryName(country);
      const countryFlag = _podcastCountryFlag(country);
      const topics = _podcastTopics(show);
      const art = show.artworkUrl || '';
      const isFav = podcastIsShowFavorite(show.feedUrl || '');

      if (isList) {
        return `
      <article class="podcast-list-row" style="cursor:pointer" onclick="podcastOpenShow('${jsStr(show.feedUrl || '')}','${jsStr(title)}','${jsStr(art)}')">
        <div class="podcast-list-row-main">
          <div class="podcast-list-primary">
            <div class="podcast-list-art">
              ${art ? `<img src="${esc(art)}" alt="" class="podcast-list-art-img" loading="lazy"/>` : '<span style="opacity:.6">🎙️</span>'}
            </div>
            <div class="podcast-list-text">
              <div style="display:flex;align-items:center;gap:.4rem;justify-content:space-between;min-width:0">
                <div class="podcast-list-title">${esc(title)}</div>
                <button type="button" class="radio-fav-btn${isFav ? ' active' : ''}" onclick="podcastToggleShowFavorite('${jsStr(show.feedUrl || '')}',event)" title="${isFav ? 'Remove from favorites' : 'Add to favorites'}" aria-label="${isFav ? 'Remove from favorites' : 'Add to favorites'}">${isFav ? '&#10084;' : '&#9825;'}</button>
              </div>
              ${author ? `<div class="podcast-list-author">${esc(author)}</div>` : ''}
            </div>
          </div>
          <div class="podcast-list-country">${country ? `${countryFlag ? `${esc(countryFlag)} ` : ''}${esc(countryName || country)}` : '—'}</div>
          <div class="podcast-list-topics">${topics.map((t) => `<span class="meta-pill">${esc(t)}</span>`).join('')}</div>
        </div>
      </article>`;
      }

      return `
      <article class="movie-card" style="padding:.65rem;cursor:pointer" onclick="podcastOpenShow('${jsStr(show.feedUrl || '')}','${jsStr(title)}','${jsStr(art)}')">
        <div style="display:grid;gap:.5rem">
          <div style="position:relative;width:100%;aspect-ratio:1/1;border-radius:.5rem;overflow:hidden;background:#1b1b2c;display:flex;align-items:center;justify-content:center">
            ${art
              ? `<img src="${esc(art)}" alt="" style="width:100%;height:100%;display:block;object-fit:cover;object-position:center;filter:brightness(.74) saturate(.85)" loading="lazy"/><div style="position:absolute;inset:0;background:linear-gradient(135deg, rgba(233,69,96,.22), rgba(37,99,235,.18));mix-blend-mode:screen;pointer-events:none"></div>`
              : '<span style="opacity:.6;font-size:1.8rem">🎙️</span>'}
            <button type="button" class="radio-fav-btn${isFav ? ' active' : ''}" onclick="podcastToggleShowFavorite('${jsStr(show.feedUrl || '')}',event)" title="${isFav ? 'Remove from favorites' : 'Add to favorites'}" aria-label="${isFav ? 'Remove from favorites' : 'Add to favorites'}" style="position:absolute;top:.35rem;right:.35rem;background:rgba(10,10,20,.6);border-radius:999px;padding:.12rem .34rem">${isFav ? '&#10084;' : '&#9825;'}</button>
          </div>
          <div style="min-width:0;display:grid;gap:.25rem">
            <div style="font-weight:700;line-height:1.25;text-decoration:underline;text-decoration-color:transparent">${esc(title)}</div>
            ${author ? `<div style="color:var(--muted,#9aa8c2)">${esc(author)}</div>` : ''}
            <div style="display:flex;gap:.35rem;flex-wrap:wrap">
              ${genre ? `<span class="meta-pill">${esc(genre)}</span>` : ''}
              ${country ? `<span class="meta-pill">${esc(country)}</span>` : ''}
            </div>
          </div>
        </div>
      </article>`;
    })
    .join('');

  const loadMore = _podcastShowsHasMore
    ? `<div style="display:flex;justify-content:center;margin-top:.65rem"><button class="btn btn-dim" type="button" onclick="podcastLoadMoreShows()">Load more</button></div>`
    : '';

  if (isList) {
    root.innerHTML = `<div class="podcast-list-shell">${listHeader}<div class="movie-grid list-view podcast-list-view" style="gap:.6rem">${cards}</div></div>${loadMore}`;
  } else {
    root.innerHTML = `<div class="movie-grid" style="gap:.6rem">${cards}</div>${loadMore}`;
  }
}

function _renderPodcastEpisodes() {
  const root = document.getElementById('podcasts-browser');
  if (!root) return;

  _syncPodcastCommandBar();

  const showTitle = _podcastSelectedShow?.title || 'Podcast';
  const episodes = _podcastEpisodes;

  _renderPodcastHeader({
    breadcrumb: `<a onclick=\"podcastBackToShows()\">Podcasts</a><span> &rsaquo; </span><span class=\"crumb-current\">${esc(showTitle)}</span>`,
    countText: _buildPodcastCountHtml(episodes.length, _podcastEpisodesTotal, _podcastEpisodesHasMore, 'podcastLoadMoreEpisodes()'),
    showBack: true,
    backHandler: podcastBackToShows,
  });

  const header = `
    <div class="podcast-episodes-sticky-header">
      <button class="btn btn-dim podcast-episodes-back-btn" type="button" onclick="podcastBackToShows()">← Back</button>
      <div class="podcast-episodes-title">${esc(showTitle)}</div>
      <div class="podcast-episodes-count">${episodes.length} episode${episodes.length === 1 ? '' : 's'}</div>
    </div>`;

  if (!episodes.length) {
    root.innerHTML = header + '<div style="padding:.4rem;color:var(--muted,#9aa8c2)">No playable episodes were found in this feed.</div>';
    return;
  }

  const cards = episodes
    .map((ep, epIndex) => {
      const isList = _viewMode.podcasts === 'list';
      const title = ep.title || 'Episode';
      const published = _formatPodcastDate(ep.publishedUtc);
      const duration = _formatPodcastDuration(ep.durationSeconds);
      const listMeta = [published, duration ? `Duration: ${duration}` : ''].filter(Boolean).join(' · ');
      const desc = _podcastPlainText(ep.description || '');
      const safeDesc = desc.length > 240 ? desc.slice(0, 240) + '…' : desc;
      return `
      <article class="movie-card podcast-episode-row" style="padding:.65rem;cursor:pointer" onclick="podcastPlayEpisode(${epIndex})">
        <div style="display:grid;gap:.35rem">
          <div style="display:flex;gap:.5rem;align-items:flex-start;justify-content:space-between">
            <div style="font-weight:700;line-height:1.25;min-width:0">${esc(title)}${isList && listMeta ? `<div style=\"margin-top:.12rem;font-size:.72rem;color:var(--muted,#9aa8c2);font-weight:500\">${esc(listMeta)}</div>` : ''}</div>
          </div>
          <div style="display:flex;gap:.35rem;flex-wrap:wrap;color:var(--muted,#9aa8c2)">
            ${!isList && published ? `<span>${esc(published)}</span>` : ''}
            ${!isList && duration ? `<span>• Duration: ${esc(duration)}</span>` : ''}
          </div>
          ${safeDesc ? `<div style="color:var(--muted,#9aa8c2);line-height:1.4">${esc(safeDesc)}</div>` : ''}
        </div>
      </article>`;
    })
    .join('');

  const loadMore = _podcastEpisodesHasMore
    ? `<div style="display:flex;justify-content:center;margin-top:.65rem"><button class="btn btn-dim" type="button" onclick="podcastLoadMoreEpisodes()">Load more</button></div>`
    : '';

  const listClass = _viewMode.podcasts === 'list' ? ' list-view podcast-episodes-list-view' : '';
  root.innerHTML = `${header}<div class="movie-grid${listClass}" style="gap:.6rem">${cards}</div>${loadMore}`;
  _syncPodcastPlaybackControls();
}

function _podcastPlainText(value) {
  const raw = String(value || '').trim();
  if (!raw) return '';

  try {
    const temp = document.createElement('div');
    temp.innerHTML = raw;
    return (temp.textContent || temp.innerText || '').trim();
  } catch {
    return raw.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();
  }
}

function _podcastTopics(show) {
  const raw = (show?.genre || '').trim();
  if (!raw) return ['General'];

  const topics = raw
    .split(/[|,/;>]+/g)
    .map((v) => (v || '').trim())
    .filter(Boolean);

  if (topics.length === 0) return ['General'];

  return [...new Set(topics)].slice(0, 4);
}

function _podcastCountryFlag(code) {
  const country = (code || '').trim().toUpperCase();
  if (!/^[A-Z]{2}$/.test(country)) return '';

  const base = 127397;
  return String.fromCodePoint(country.charCodeAt(0) + base, country.charCodeAt(1) + base);
}

function _podcastCountryName(code) {
  const country = (code || '').trim().toUpperCase();
  if (!country) return '';

  return _podcastCountryNames.get(country) || '';
}

function _buildPodcastCountHtml(loadedCount, totalCount, hasMore, loadMoreHandler) {
  const loaded = Number(loadedCount) || 0;
  const total = _toOptionalNumber(totalCount);
  const noun = 'item' + (loaded === 1 ? '' : 's');

  let label = total !== null
    ? `${loaded} of ${total} ${noun}`
    : `${loaded} ${noun}`;

  if (hasMore) {
    label += `, <a href="#" class="count-load-more" onclick="event.preventDefault();${loadMoreHandler}">Load more</a>`;
  }

  return label;
}

function _toOptionalNumber(value) {
  if (value === null || value === undefined || value === '') return null;

  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function _formatPodcastDate(value) {
  if (!value) return '';

  try {
    const d = new Date(value);
    if (Number.isNaN(d.getTime())) return '';
    return d.toLocaleDateString();
  } catch {
    return '';
  }
}

function _formatPodcastDuration(seconds) {
  const value = Number(seconds || 0);
  if (!Number.isFinite(value) || value <= 0) return '';

  const total = Math.floor(value);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;

  if (h > 0) return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
  return `${m}:${String(s).padStart(2, '0')}`;
}
