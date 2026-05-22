// ── Radio ────────────────────────────────────────────────────────────
let _radioInited = false;
let _radioStations = [];
let _radioFavorites = [];
let _radioVisibleStations = []; // whichever array is currently rendered in the cards
let _radioFavCountries = []; // country codes marked as favorites (localStorage)
let _radioFavTags = []; // genre tags marked as favorites (localStorage)
let _radioCurrentUrl = '';
let _radioCurrentName = '';
let _radioCurrentCountry = '';
let _radioCurrentTag = '';
let _radioCurrentStation = null; // full station object for player display
let _radioIsPlaying = false;
let _radioStatusPoll = null;
let _radioCountries = [];
let _radioTags = [];
let _radioTab = 'top'; // 'top'|'favorites'
let _radioPage = 0; // current page (0-based), each page = 80 stations
const _radioPageSize = 80;
let _radioFilterQ = '';
let _radioFilterCountry = '';
let _radioFilterTag = '';
let _radioSortBy = 'votes'; // 'votes'|'name'|'bitrate'
let _radioSortDir = 'desc'; // 'asc'|'desc'
let _radioMinBitrate = 0; // 0=all, else minimum kbps

const _radioFavCountriesKey = 'rp_radio_fav_countries';
const _radioFavTagsKey = 'rp_radio_fav_tags';
function radioLoadFavCountries() {
  try {
    _radioFavCountries = JSON.parse(localStorage.getItem(_radioFavCountriesKey) || '[]');
  } catch {
    _radioFavCountries = [];
  }
  try {
    _radioFavTags = JSON.parse(localStorage.getItem(_radioFavTagsKey) || '[]');
  } catch {
    _radioFavTags = [];
  }
}
function radioSaveFavCountries() {
  try {
    localStorage.setItem(_radioFavCountriesKey, JSON.stringify(_radioFavCountries));
  } catch {}
}
function radioSaveFavTags() {
  try {
    localStorage.setItem(_radioFavTagsKey, JSON.stringify(_radioFavTags));
  } catch {}
}
function radioIsCountryFav(code) {
  return _radioFavCountries.includes(code);
}
function radioIsTagFav(tag) {
  return _radioFavTags.includes(tag);
}
function radioToggleCountryFav(code, evt) {
  if (evt) {
    evt.stopPropagation();
  }
  if (!code) return;
  const i = _radioFavCountries.indexOf(code);
  if (i >= 0) _radioFavCountries.splice(i, 1);
  else _radioFavCountries.push(code);
  radioSaveFavCountries();
  _rebuildSelectOptions(
    'radio-country',
    _radioCountries.map((c) => ({ value: c.code, label: c.name })),
    _radioFavCountries,
    'radioToggleCountryFav'
  );
  radioFetch();
}
function radioToggleTagFav(tag, evt) {
  if (evt) {
    evt.stopPropagation();
  }
  if (!tag) return;
  const i = _radioFavTags.indexOf(tag);
  if (i >= 0) _radioFavTags.splice(i, 1);
  else _radioFavTags.push(tag);
  radioSaveFavTags();
  _rebuildSelectOptions(
    'radio-tag',
    _radioTags.map((t) => ({ value: t, label: t })),
    _radioFavTags,
    'radioToggleTagFav'
  );
  radioFetch();
}

// Build a custom-styled select wrapper with heart-toggle buttons per option.
// Renders as a native <select> for value, with an overlay list for the hearts.
// Build a native <select> with favorites grouped at top + divider,
// plus a heart button beside it to toggle the currently selected value as a favorite.
function _buildFavSelect(id, items, favList, toggleFn, allLabel) {
  const favSet = new Set(favList);
  const favItems = [...items]
    .filter((it) => favSet.has(it.value))
    .sort((a, b) => a.label.localeCompare(b.label));
  const restItems = [...items]
    .filter((it) => !favSet.has(it.value))
    .sort((a, b) => a.label.localeCompare(b.label));
  let h = `<div class="radio-fav-select-wrap"><select id="${id}" onchange="${id === 'radio-country' ? 'radioOnCountryChange()' : "_syncFavSelectHeart('" + id + "','" + toggleFn + "')"}">`;
  h += `<option value="">${allLabel}</option>`;
  if (favItems.length && restItems.length) {
    h += '<optgroup label="\u2764 Favorites">';
    for (const it of favItems)
      h += `<option value="${escHtml(it.value)}">${escHtml(it.label)}</option>`;
    h += '</optgroup><optgroup label="\u2500\u2500\u2500\u2500\u2500">';
    for (const it of restItems)
      h += `<option value="${escHtml(it.value)}">${escHtml(it.label)}</option>`;
    h += '</optgroup>';
  } else {
    for (const it of [...favItems, ...restItems])
      h += `<option value="${escHtml(it.value)}">${escHtml(it.label)}</option>`;
  }
  h += '</select>';
  h += `<button class="radio-fav-select-heart-inline" id="${id}-heart" onclick="${toggleFn}(document.getElementById('${id}').value,event)" title="Toggle as favorite">\u2665</button>`;
  h += '</div>';
  return h;
}

function _syncFavSelectHeart(selectId, toggleFn) {
  _radioPage = 0;
  radioFetch();
  const sel = document.getElementById(selectId);
  const btn = document.getElementById(selectId + '-heart');
  if (!sel || !btn) return;
  const isCountry = toggleFn === 'radioToggleCountryFav';
  const isFav = sel.value
    ? isCountry
      ? radioIsCountryFav(sel.value)
      : radioIsTagFav(sel.value)
    : false;
  btn.classList.toggle('active', isFav);
}

async function radioOnCountryChange() {
  const cc = (document.getElementById('radio-country') || {}).value || '';
  const url = '/api/radio/tags' + (cc ? '?country=' + encodeURIComponent(cc) : '');
  try {
    const r = await fetch(url);
    if (r.ok) {
      const newTags = await r.json();
      const prevTag = (document.getElementById('radio-tag') || {}).value || '';
      _radioTags = newTags;
      _rebuildSelectOptions(
        'radio-tag',
        _radioTags.map((t) => ({ value: t, label: t })),
        _radioFavTags,
        'radioToggleTagFav'
      );
      const tagSel = document.getElementById('radio-tag');
      if (tagSel) tagSel.value = newTags.includes(prevTag) ? prevTag : '';
    }
  } catch (e) {
    console.warn('tags refresh failed', e);
  }
  _syncFavSelectHeart('radio-country', 'radioToggleCountryFav');
}

// Rebuild just the options of an existing fav select after a heart toggle.
function _rebuildSelectOptions(id, items, favList, toggleFn) {
  const sel = document.getElementById(id);
  if (!sel) return;
  const curVal = sel.value;
  const favSet = new Set(favList);
  const favItems = [...items]
    .filter((it) => favSet.has(it.value))
    .sort((a, b) => a.label.localeCompare(b.label));
  const restItems = [...items]
    .filter((it) => !favSet.has(it.value))
    .sort((a, b) => a.label.localeCompare(b.label));
  const allLabel = sel.options[0] ? sel.options[0].text : 'All';
  sel.innerHTML = `<option value="">${allLabel}</option>`;
  if (favItems.length && restItems.length) {
    let og = document.createElement('optgroup');
    og.label = '\u2764 Favorites';
    for (const it of favItems) {
      const o = document.createElement('option');
      o.value = it.value;
      o.textContent = it.label;
      og.appendChild(o);
    }
    sel.appendChild(og);
    let og2 = document.createElement('optgroup');
    og2.label = '\u2500\u2500\u2500\u2500\u2500';
    for (const it of restItems) {
      const o = document.createElement('option');
      o.value = it.value;
      o.textContent = it.label;
      og2.appendChild(o);
    }
    sel.appendChild(og2);
  } else {
    const all = [...favItems, ...restItems];
    for (const it of all) {
      const o = document.createElement('option');
      o.value = it.value;
      o.textContent = it.label;
      sel.appendChild(o);
    }
  }
  sel.value = curVal;
  const btn = document.getElementById(id + '-heart');
  if (btn) btn.classList.toggle('active', !!curVal && new Set(favList).has(curVal));
}

async function radioInit() {
  const rb = document.getElementById('radio-browser');
  if (!rb) return;
  rb.innerHTML =
    '<div style="color:var(--muted,#9aa8c2);padding:.5rem">Loading stations\u2026</div>';
  radioLoadFavCountries();
  // Load countries/tags in parallel
  if (!_radioCountries.length || !_radioTags.length) {
    try {
      const [cr, tr] = await Promise.all([
        fetch('/api/radio/countries').then((r) => r.json()),
        fetch('/api/radio/tags').then((r) => r.json()),
      ]);
      _radioCountries = cr || [];
      _radioTags = tr || [];
    } catch {}
  }
  await radioLoadFavorites();
  await radioShowTab(_radioTab);
  startRadioStatusPoll();
  _radioInited = true;
}

async function radioLoadFavorites() {
  try {
    const r = await fetch('/api/radio/favorites');
    _radioFavorites = r.ok ? await r.json() : [];
  } catch {
    _radioFavorites = [];
  }
}

async function radioShowTab(tab) {
  // Persist current filter values before rebuilding the DOM
  const prevQ = _radioFilterQ;
  const prevCountry = _radioFilterCountry;
  const prevTag = _radioFilterTag;
  const sameTab = tab === _radioTab;
  _radioTab = tab;
  _radioPage = 0;
  // Always reset sort to default (votes ↓) when switching to a tab
  _radioSortBy = 'votes';
  _radioSortDir = 'desc';
  const rb = document.getElementById('radio-browser');
  if (!rb) return;
  rb.style.display = '';
  let html = '';
  // Sync header tab buttons active state
  const hts = document.getElementById('header-tab-stations');
  const htf = document.getElementById('header-tab-favorites');
  if (hts) hts.classList.toggle('active', tab === 'top');
  if (htf) htf.classList.toggle('active', tab === 'favorites');
  // Filters (only for top/search) — rendered into sticky header row
  const rfs = document.getElementById('radio-filter-sticky');
  if (tab !== 'favorites') {
    let filterHtml = '';
    filterHtml +=
      '<button id="view-toggle-radio" class="radio-reset-btn view-toggle-btn" onclick="_setViewMode(\'radio\',_viewMode.radio===\'grid\'?\'list\':\'grid\')" title="Toggle view"></button>';
    filterHtml +=
      '<input id="radio-search-box" type="search" placeholder="Station name\u2026" style="background:var(--input-bg);color:var(--input-text);border:1px solid var(--input-border);padding:.55rem .6rem;border-radius:4px;font-size:.9rem;min-width:143px;width:208px;min-height:2.2rem" oninput="radioOnSearchInput()" />';
    filterHtml += _buildFavSelect(
      'radio-country',
      _radioCountries.map((c) => ({ value: c.code, label: c.name })),
      _radioFavCountries,
      'radioToggleCountryFav',
      'All countries'
    );
    filterHtml += _buildFavSelect(
      'radio-tag',
      _radioTags.map((t) => ({ value: t, label: t })),
      _radioFavTags,
      'radioToggleTagFav',
      'All genres'
    );
    filterHtml += `<select id="radio-minbitrate" class="radio-bitrate-select" onchange="radioOnBitrateChange()" title="Min bitrate" aria-label="Minimum bitrate">
              <option value="0"${_radioMinBitrate === 0 ? ' selected' : ''}>Any kbps</option>
              <option value="64"${_radioMinBitrate === 64 ? ' selected' : ''}>64+ kbps</option>
              <option value="128"${_radioMinBitrate === 128 ? ' selected' : ''}>128+ kbps</option>
              <option value="192"${_radioMinBitrate === 192 ? ' selected' : ''}>192+ kbps</option>
              <option value="320"${_radioMinBitrate === 320 ? ' selected' : ''}>320+ kbps</option>
            </select>`;
    filterHtml +=
      '<button class="radio-reset-btn" onclick="radioResetFilters()" title="Reset all filters">&#10005; Reset</button>';
    if (rfs) {
      rfs.innerHTML = filterHtml;
      rfs.style.display = 'flex';
    }
  } else {
    if (rfs) {
      rfs.innerHTML = '';
      rfs.style.display = 'none';
    }
  }
  html += '<div id="radio-cards"></div>';
  rb.innerHTML = html;
  _applyViewToggleBtn('radio');
  // Restore filter state
  if (tab !== 'favorites') {
    const sb = document.getElementById('radio-search-box');
    const sc = document.getElementById('radio-country');
    const st = document.getElementById('radio-tag');
    if (sb) sb.value = prevQ;
    if (sc) sc.value = prevCountry;
    if (st) st.value = prevTag;
    // Sync heart states after restoring selection
    const bhc = document.getElementById('radio-country-heart');
    if (bhc) bhc.classList.toggle('active', !!prevCountry && radioIsCountryFav(prevCountry));
    const bht = document.getElementById('radio-tag-heart');
    if (bht) bht.classList.toggle('active', !!prevTag && radioIsTagFav(prevTag));
  }
  if (tab === 'favorites') {
    renderRadioCards(_radioFavorites, false);
  } else {
    await radioFetch();
  }
}

function radioOnBitrateChange() {
  const sel = document.getElementById('radio-minbitrate');
  if (sel) _radioMinBitrate = parseInt(sel.value) || 0;
  renderRadioCards(_radioStations, _radioStations.length === _radioPageSize * (_radioPage + 1));
}

let _radioSearchTimer = null;
function radioOnSearchInput() {
  clearTimeout(_radioSearchTimer);
  _radioPage = 0;
  _radioSearchTimer = setTimeout(() => radioFetch(), 420);
}

function radioResetFilters() {
  const sb = document.getElementById('radio-search-box');
  const sc = document.getElementById('radio-country');
  const st = document.getElementById('radio-tag');
  const ss = document.getElementById('radio-sort');
  const sb2 = document.getElementById('radio-minbitrate');
  if (sb) sb.value = '';
  if (sc) sc.value = '';
  if (st) st.value = '';
  if (ss) ss.value = 'votes';
  if (sb2) sb2.value = '0';
  _radioFilterQ = '';
  _radioFilterCountry = '';
  _radioFilterTag = '';
  _radioSortBy = 'votes';
  _radioSortDir = 'desc';
  _radioMinBitrate = 0;
  const bhc = document.getElementById('radio-country-heart');
  if (bhc) bhc.classList.remove('active');
  const bht = document.getElementById('radio-tag-heart');
  if (bht) bht.classList.remove('active');
  _radioPage = 0;
  radioFetch();
}

async function radioFetch(append = false) {
  const tab = _radioTab;
  const q = (document.getElementById('radio-search-box') || {}).value || '';
  const country = (document.getElementById('radio-country') || {}).value || '';
  const tag = (document.getElementById('radio-tag') || {}).value || '';
  // Persist filter state so switching tabs and back restores them
  _radioFilterQ = q;
  _radioFilterCountry = country;
  _radioFilterTag = tag;
  if (!append) _radioPage = 0;
  const offset = _radioPage * _radioPageSize;
  const cards = document.getElementById('radio-cards');
  if (cards && !append)
    cards.innerHTML = '<div style="color:var(--muted,#9aa8c2);padding:.4rem">Loading\u2026</div>';
  try {
    let newStations;
    if (!q && !country && !tag && tab === 'top') {
      const r = await fetch(`/api/radio/top?limit=${_radioPageSize}&offset=${offset}`);
      newStations = r.ok ? await r.json() : [];
    } else {
      const params = new URLSearchParams({
        q,
        country,
        tag,
        limit: String(_radioPageSize),
        offset: String(offset),
      });
      const r = await fetch('/api/radio/search?' + params);
      newStations = r.ok ? await r.json() : [];
    }
    if (append) {
      _radioStations = [..._radioStations, ...newStations];
    } else {
      _radioStations = newStations;
    }
    renderRadioCards(_radioStations, newStations.length === _radioPageSize);
  } catch {
    if (cards && !append)
      cards.innerHTML = '<div style="color:#ff7777;padding:.4rem">Failed to load stations.</div>';
  }
}

async function radioLoadMore() {
  _radioPage++;
  await radioFetch(true);
}

function radioIsFav(uuid) {
  return _radioFavorites.some((f) => (f.stationuuid || f.uuid || f.Uuid) === uuid);
}

function _sortStationsAlpha(arr) {
  return [...arr].sort((a, b) => {
    const na = (a.name || a.Name || '').toLowerCase();
    const nb = (b.name || b.Name || '').toLowerCase();
    return na < nb ? -1 : na > nb ? 1 : 0;
  });
}

function _sortAndFilterStations(arr) {
  let list = arr;
  // Bitrate filter
  if (_radioMinBitrate > 0)
    list = list.filter((s) => (s.bitrate || s.Bitrate || 0) >= _radioMinBitrate);
  // Sort — dir=1 keeps the natural comparator direction (b-a = desc for numeric, a<b = asc for strings)
  // _radioSortDir='desc' means highest first → dir=1 for numeric (b-a), dir=-1 for string (flip a<b)
  // _radioSortDir='asc'  means lowest  first → dir=-1 for numeric,          dir=1  for string
  const numDir = _radioSortDir === 'desc' ? 1 : -1;  // numeric: 1=desc (b-a), -1=asc (a-b)
  const strDir = _radioSortDir === 'asc'  ? 1 : -1;  // string:  1=asc  (a<b),  -1=desc
  if (_radioSortBy === 'name') {
    list = [...list].sort((a, b) => {
      const na = (a.name || a.Name || '').toLowerCase();
      const nb = (b.name || b.Name || '').toLowerCase();
      return (na < nb ? -1 : na > nb ? 1 : 0) * strDir;
    });
  } else if (_radioSortBy === 'bitrate') {
    list = [...list].sort((a, b) => ((b.bitrate || b.Bitrate || 0) - (a.bitrate || a.Bitrate || 0)) * numDir);
  } else if (_radioSortBy === 'country') {
    list = [...list].sort((a, b) => {
      const ca = (a.country || a.Country || '').toLowerCase();
      const cb = (b.country || b.Country || '').toLowerCase();
      const cmp = (ca < cb ? -1 : ca > cb ? 1 : 0) * strDir;
      if (cmp !== 0) return cmp;
      // secondary: votes descending
      return (b.votes || b.Votes || 0) - (a.votes || a.Votes || 0);
    });
  } else {
    // votes (default)
    list = [...list].sort((a, b) => ((b.votes || b.Votes || 0) - (a.votes || a.Votes || 0)) * numDir);
  }
  return list;
}

function radioSortByColumn(col) {
  if (_radioSortBy === col) {
    _radioSortDir = _radioSortDir === 'desc' ? 'asc' : 'desc';
  } else {
    _radioSortBy = col;
    // default direction per column
    _radioSortDir = (col === 'name' || col === 'country') ? 'asc' : 'desc';
  }
  renderRadioCards(_radioStations, _radioStations.length === _radioPageSize * (_radioPage + 1));
}

function _buildStationCard(s) {
  const uuid = s.stationuuid || s.uuid || s.Uuid || '';
  const name = s.name || s.Name || '';
  const url = s.streamUrl || s.StreamUrl || s.url_resolved || '';
  const country = s.country || s.Country || '';
  const state = s.state || s.State || '';
  const language = s.language || s.Language || '';
  const tags = (s.tags || s.Tags || '').split(',').filter(Boolean).slice(0, 4).join(', ');
  const bitrate = s.bitrate || s.Bitrate || 0;
  const codec = (s.codec || s.Codec || '').toUpperCase();
  const votes = s.votes || s.Votes || 0;
  const clicks = s.clickcount || s.ClickCount || 0;
  const hls = s.hls || s.Hls || 0;
  const homepage = s.homepage || s.Homepage || '';
  // location line: country + state
  const location = [country, state].filter(Boolean).join(' – ');
  // tech line: codec + bitrate + HLS badge
  const techParts = [codec, bitrate ? bitrate + 'kbps' : '', hls ? 'HLS' : ''].filter(Boolean);
  const tech = techParts.join(' · ');
  // popularity: votes + clicks
  const pop = [
    votes ? '▲ ' + votes.toLocaleString() : '',
    clicks ? '▶ ' + clicks.toLocaleString() : '',
  ]
    .filter(Boolean)
    .join('  ');
  const favIcon = radioIsFav(uuid) ? '&#10084;' : '&#9825;';
  const isPlaying = _radioCurrentUrl && url && _radioCurrentUrl === url;
  const stationJson = encodeURIComponent(
    JSON.stringify({
      stationuuid: uuid,
      name,
      url_resolved: url,
      country,
      countrycode: s.countryCode || s.CountryCode || s.countrycode || '',
      state: s.state || s.State || '',
      language,
      tags: s.tags || s.Tags || '',
      codec,
      bitrate,
      votes,
      clickcount: clicks,
      hls,
      favicon: s.favicon || s.Favicon || '',
      homepage,
      geo_lat: s.geo_lat ?? s.GeoLat ?? null,
      geo_long: s.geo_long ?? s.GeoLong ?? null,
    })
  );
  const encCountry = encodeURIComponent(country);
  const encTagFirst = encodeURIComponent(
    (s.tags || s.Tags || '').split(',').filter(Boolean)[0] || ''
  );
  let h = `<div class="radio-station-card${isPlaying ? ' playing' : ''}" data-url="${escHtml(url)}" onclick="radioPlayStation('${encodeURIComponent(url)}','${encodeURIComponent(name)}','${encCountry}','${encTagFirst}','${stationJson}')">`;
  // sc-icon
  const faviconSrc = s.favicon || s.Favicon || '';
  h += `<span class="sc-icon">${faviconSrc ? `<img src="${escHtml(faviconSrc)}" alt="" onerror="this.style.display='none'" />` : '<span></span>'}</span>`;
  // sc-name
  h += `<span class="sc-name">${escHtml(name)}</span>`;
  // sc-quality
  const badgeParts = [codec, bitrate ? bitrate + 'k' : ''].filter(Boolean);
  h += `<span class="sc-quality">${badgeParts.length ? '<span class="radio-quality-badge">' + escHtml(badgeParts.join(' ')) + '</span>' : ''}</span>`;
  // sc-country
  h += `<span class="sc-country">${escHtml(country)}</span>`;
  // sc-genre (first 2 tags)
  const genreList = (s.tags || s.Tags || '')
    .split(',')
    .map((t) => t.trim())
    .filter(Boolean)
    .slice(0, 2)
    .join(', ');
  h += `<span class="sc-genre">${escHtml(genreList)}</span>`;
  // sc-votes
  h += `<span class="sc-votes">${votes > 0 ? votes.toLocaleString() : ''}</span>`;
  // sc-fav button
  h += `<button class="radio-fav-btn sc-fav${radioIsFav(uuid) ? ' active' : ''}" onclick="event.stopPropagation();radioToggleFav(this,'${stationJson}')" title="Favorite">${favIcon}</button>`;
  // card-only: location + tags subline
  if (location || tags)
    h += `<div class="station-meta card-only">${escHtml([location, tags].filter(Boolean).join(' · '))}</div>`;
  // card-only: tech + detail subline
  const langStr = language ? 'Lang: ' + language : '';
  const detailParts = [tech, langStr, pop].filter(Boolean);
  if (detailParts.length)
    h += `<div class="station-detail card-only">${escHtml(detailParts.join('  ·  '))}</div>`;
  // card-only: homepage link
  if (homepage)
    h += `<div class="card-only"><a href="${escHtml(homepage)}" target="_blank" rel="noopener" class="station-homepage" onclick="event.stopPropagation()">${escHtml(homepage.replace(/^https?:\/\//, '').split('/')[0])}</a></div>`;
  h += '</div>';
  return h;
}

function renderRadioCards(stations, hasMore = false) {
  _radioVisibleStations = stations; // keep dots in sync with whatever is currently shown
  const cards = document.getElementById('radio-cards');
  if (!cards) return;
  if (!stations.length) {
    const countEl = document.getElementById('radio-station-count');
    if (countEl) countEl.textContent = '0 stations';
    cards.innerHTML =
      '<div style="color:var(--muted,#9aa8c2);padding:.5rem">No stations found.</div>';
    return;
  }
  const favCodes = new Set(_radioFavCountries.map((c) => c.toUpperCase()));
  const pinned = _sortStationsAlpha(
    stations.filter((s) => {
      const cc = (s.countryCode || s.CountryCode || '').toUpperCase();
      return favCodes.has(cc);
    })
  );
  const rest = _sortStationsAlpha(
    stations.filter((s) => {
      const cc = (s.countryCode || s.CountryCode || '').toUpperCase();
      return !favCodes.has(cc);
    })
  );
  let html = '';
  const countEl = document.getElementById('radio-station-count');
  if (countEl) {
    if (hasMore) {
      countEl.innerHTML = `${stations.length} loaded \u00b7 <a href="#" class="count-load-more" onclick="event.preventDefault();radioLoadMore()">more available</a>`;
      countEl.title =
        'Radio Browser API does not provide total counts \u2014 use filters to narrow results';
    } else {
      countEl.textContent = `${stations.length} station${stations.length === 1 ? '' : 's'}`;
      countEl.title = '';
    }
  }
  const isList = _viewMode.radio === 'list';
  function _sortHdr(col, label) {
    const active = _radioSortBy === col;
    const arrow = active ? (_radioSortDir === 'desc' ? ' ↓' : ' ↑') : '';
    return `<button class="sc-sort-btn${active ? ' active' : ''}" onclick="radioSortByColumn('${col}')">${label}${arrow}</button>`;
  }
  const listHdr = isList
    ? `<div class="radio-list-header"><span class="sc-icon"></span>${_sortHdr('name','Station')}<span class="sc-quality">Format</span>${_sortHdr('country','Country')}<span class="sc-genre">Genre / Tags</span>${_sortHdr('votes','Votes')}<span class="sc-fav"></span></div>`
    : '';
  if (pinned.length && rest.length) {
    html += '<div class="radio-section-header">&#11088; Favorite countries</div>';
    html += '<div class="radio-inner-cards' + (isList ? ' list-view' : '') + '">';
    html += listHdr;
    for (const s of _sortAndFilterStations(pinned)) html += _buildStationCard(s);
    html += '</div><div class="radio-section-header">All stations</div>';
    html += '<div class="radio-inner-cards' + (isList ? ' list-view' : '') + '">';
    html += listHdr;
    for (const s of _sortAndFilterStations(rest)) html += _buildStationCard(s);
    html += '</div>';
  } else {
    html += '<div class="radio-inner-cards' + (isList ? ' list-view' : '') + '">';
    html += listHdr;
    for (const s of _sortAndFilterStations(stations)) html += _buildStationCard(s);
    html += '</div>';
  }
  if (hasMore) {
    html += `<div style="text-align:center;padding:.6rem 0"><button class="btn btn-dim" onclick="radioLoadMore()" style="min-width:120px">Load more\u2026</button></div>`;
  }
  cards.innerHTML = html;
}

function escHtml(s) {
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

async function radioPlayStation(encUrl, encName, encCountry, encTag, encStation) {
  const url = decodeURIComponent(encUrl);
  const name = decodeURIComponent(encName || '');
  let station = null;
  try {
    station = encStation ? JSON.parse(decodeURIComponent(encStation)) : null;
  } catch {
    station = null;
  }
  // Stop current stream first so the server reinitialises cleanly
  await fetch('/api/radio/stop');
  _radioCurrentUrl = url;
  _radioCurrentName = name;
  _radioCurrentCountry = decodeURIComponent(encCountry || '');
  _radioCurrentTag = decodeURIComponent(encTag || '');
  _radioCurrentStation = station;
  _radioIsPlaying = true;
  _radioRetryCount = 0;
  document.body.classList.add('radio-player-docked');
  updateRadioBar(name, _radioCurrentCountry, _radioCurrentTag, true, _radioCurrentStation);
  // Update playing highlight without full re-render
  document.querySelectorAll('.radio-station-card').forEach((el) => {
    el.classList.toggle('playing', el.dataset.url === url);
  });
  // Prefetch geo pool for the station's country so dots are populated
  if (_globeDotsEnabled && station) {
    const country =
      station.countrycode || station.CountryCode || station.country || station.Country || '';
    _refreshGeoPool(country).catch(() => {});
  }
  // Pre-resolve stream URL via Radio Browser click endpoint (feature 15)
  let playUrl = url;
  try {
    const uuid = (station && (station.stationuuid || station.uuid || station.Uuid)) || '';
    if (uuid) {
      const r = await fetch('/api/radio/resolve?' + new URLSearchParams({ uuid, url }));
      if (r.ok) {
        const j = await r.json();
        if (j.resolvedUrl) playUrl = j.resolvedUrl;
      }
    }
  } catch {}

  if (isPlayLocal()) {
    // Local mode: play stream directly in browser via server-side proxy (avoids CORS on stream URLs).
    // State vars (_radioCurrentUrl, _radioIsPlaying, updateRadioBar) were already set above.
    // Server was already stopped at the top of this function.
    const proxyUrl = '/api/radio/stream-proxy?url=' + encodeURIComponent(playUrl);
    fetch('/api/log', { method: 'POST', body: `[RADIO-LOCAL] isPlayLocal=true, resolved playUrl=${playUrl}, proxyUrl=${proxyUrl}` }).catch(() => {});
    localPlay(proxyUrl, name, 'Radio');
    fetch('/api/log', { method: 'POST', body: `[RADIO-LOCAL] localPlay() returned, _radioIsPlaying=${_radioIsPlaying}` }).catch(() => {});
    startWaveform();
    return;
  }
  fetch('/api/log', { method: 'POST', body: `[RADIO-SERVER] isPlayLocal=false, sending to server url=${playUrl}` }).catch(() => {});
  await fetch('/api/radio/play?' + new URLSearchParams({ url: playUrl, name }));
}

async function radioToggle() {
  if (_radioIsPlaying) {
    if (isPlayLocal()) {
      // Stop local audio and update bar; don't touch server
      localStop();
      _radioIsPlaying = false;
      stopWaveform();
      updateRadioBar(_radioCurrentName, _radioCurrentCountry, _radioCurrentTag, false, _radioCurrentStation);
    } else {
      await radioStop(); // pauses — bar stays open
    }
  } else if (_radioCurrentUrl) {
    if (isPlayLocal()) {
      // Resume locally via proxy
      _radioIsPlaying = true;
      updateRadioBar(_radioCurrentName, _radioCurrentCountry, _radioCurrentTag, true, _radioCurrentStation);
      const proxyUrl = '/api/radio/stream-proxy?url=' + encodeURIComponent(_radioCurrentUrl);
      localPlay(proxyUrl, _radioCurrentName, 'Radio');
      startWaveform();
    } else {
      // Resume: restart the stream at the same URL
      _radioIsPlaying = true;
      updateRadioBar(
        _radioCurrentName,
        _radioCurrentCountry,
        _radioCurrentTag,
        true,
        _radioCurrentStation
      );
      await fetch(
        '/api/radio/play?' + new URLSearchParams({ url: _radioCurrentUrl, name: _radioCurrentName })
      );
    }
  }
}

async function radioStop() {
  await fetch('/api/radio/stop');
  _radioIsPlaying = false;
  // Only stop local audio when in local-play mode; server-mode stop has nothing local to clean up.
  if (isPlayLocal()) localStop();
  // Keep radio-player-docked so the bar stays visible — the user can resume from here.
  updateRadioBar(
    _radioCurrentName,
    _radioCurrentCountry,
    _radioCurrentTag,
    false,
    _radioCurrentStation
  );
}

async function radioDismiss() {
  radioSleepCancel();
  await radioStop();
  _radioCurrentUrl = '';
  _radioCurrentName = '';
  _radioCurrentCountry = '';
  _radioCurrentTag = '';
  _radioCurrentStation = null;
  _radioRetryCount = 0;
  stopElapsedTick();
  stopWaveform();
  setRadioHealthDot('warn');
  const el = document.getElementById('radio-bar-elapsed');
  if (el) el.style.display = 'none';
  const songEl = document.getElementById('radio-bar-song');
  if (songEl) songEl.style.display = 'none';
  document.body.classList.remove('radio-player-docked');
  stopGlobeAnim();
  _globeHasStation = false;
}

function radioVolume(v) {
  const vol = parseFloat(v);
  if (isNaN(vol)) return;
  const lbl = document.getElementById('radio-volume-label');
  if (lbl) lbl.textContent = Math.round(vol * 100) + '%';
  if (isPlayLocal()) {
    // In local mode, drive the <audio> element volume directly
    const a = typeof _getLocalAudio !== 'undefined' ? _getLocalAudio() : null;
    if (a) a.volume = Math.min(1, Math.max(0, vol));
    return;
  }
  fetch('/api/radio/volume?v=' + vol.toFixed(3));
}
function radioVolumeReset() {
  const slider = document.getElementById('radio-bar-volume');
  if (slider) {
    slider.value = 1;
    radioVolume(1);
  }
}

// Persistent Web Audio graph for local boost: AudioContext → MediaElementSourceNode → GainNode → destination
let _localBoostCtx = null;
let _localBoostGain = null;

function _ensureLocalBoostGraph() {
  const a = typeof _getLocalAudio !== 'undefined' ? _getLocalAudio() : null;
  if (!a) return null;
  if (_localBoostGain) return _localBoostGain;
  try {
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return null;
    _localBoostCtx = new AC();
    const src = _localBoostCtx.createMediaElementSource(a);
    _localBoostGain = _localBoostCtx.createGain();
    _localBoostGain.gain.value = 1.0;
    src.connect(_localBoostGain);
    _localBoostGain.connect(_localBoostCtx.destination);
    _localBoostCtx.resume().catch(() => {});
    return _localBoostGain;
  } catch {
    return null;
  }
}

function radioBoost(v) {
  const boost = parseFloat(v);
  if (isNaN(boost)) return;
  const lbl = document.getElementById('radio-boost-label');
  if (lbl) {
    const db = boost > 0 ? Math.round(20 * Math.log10(boost)) : -Infinity;
    lbl.textContent = isFinite(db) ? (db >= 0 ? '+' : '') + db + ' dB' : '—';
  }
  if (isPlayLocal()) {
    // In local mode, drive the Web Audio GainNode so we can boost above 1.0
    const gainNode = _ensureLocalBoostGraph();
    if (gainNode) gainNode.gain.value = Math.max(0, boost);
    return;
  }
  fetch('/api/radio/boost?v=' + boost.toFixed(3));
}
function radioBoostReset() {
  const slider = document.getElementById('radio-bar-boost');
  if (slider) {
    slider.value = 1;
    radioBoost(1);
  }
}

// ── Sleep timer ──────────────────────────────────────────────────────────
const SLEEP_FADE_SECS = 60;      // begin fading this many seconds before stop
let _sleepTimerEnd = 0;
let _sleepTimerInterval = null;
let _sleepFadeActive = false;    // true once the fade-out ramp has started
let _sleepPreFadeVolume = null;  // saved volume so cancel can restore it

function radioSleepSet(minutes) {
  _sleepTimerEnd = Date.now() + minutes * 60 * 1000;
  _sleepFadeActive = false;
  _sleepPreFadeVolume = null;
  clearInterval(_sleepTimerInterval);
  _sleepTimerInterval = setInterval(_sleepTimerTick, 1000);
  _sleepTimerTick();
  const btns = document.getElementById('radio-sleep-btns');
  const cd = document.getElementById('radio-sleep-countdown');
  if (btns) btns.style.display = 'none';
  if (cd) cd.style.display = '';
}
function radioSleepCancel() {
  // Restore volume if we were already fading
  if (_sleepFadeActive && _sleepPreFadeVolume !== null) {
    radioVolume(_sleepPreFadeVolume);
    const vSlider = document.getElementById('radio-bar-volume');
    const vLabel = document.getElementById('radio-volume-label');
    if (vSlider) vSlider.value = _sleepPreFadeVolume;
    if (vLabel) vLabel.textContent = Math.round(_sleepPreFadeVolume * 100) + '%';
  }
  _sleepTimerEnd = 0;
  _sleepFadeActive = false;
  _sleepPreFadeVolume = null;
  clearInterval(_sleepTimerInterval);
  _sleepTimerInterval = null;
  const btns = document.getElementById('radio-sleep-btns');
  const cd = document.getElementById('radio-sleep-countdown');
  if (btns) btns.style.display = '';
  if (cd) cd.style.display = 'none';
}
function _sleepTimerTick() {
  const rem = Math.max(0, _sleepTimerEnd - Date.now());
  const remain = document.getElementById('radio-sleep-remain');
  if (remain) {
    const m = Math.floor(rem / 60000);
    const s = Math.floor((rem % 60000) / 1000);
    remain.textContent = 'Stops in ' + m + ':' + (s < 10 ? '0' : '') + s;
  }

  // Begin fade-out when SLEEP_FADE_SECS remain
  const fadeSecs = SLEEP_FADE_SECS * 1000;
  if (!_sleepFadeActive && rem > 0 && rem <= fadeSecs) {
    _sleepFadeActive = true;
    // Capture current volume once
    const vSlider = document.getElementById('radio-bar-volume');
    _sleepPreFadeVolume = vSlider ? parseFloat(vSlider.value) : 0.8;
  }
  if (_sleepFadeActive && rem > 0) {
    // Linearly interpolate volume from saved → 0 over SLEEP_FADE_SECS seconds
    const t = Math.min(1, 1 - rem / fadeSecs);
    const newVol = Math.max(0, _sleepPreFadeVolume * (1 - t));
    radioVolume(newVol);
    const vSlider = document.getElementById('radio-bar-volume');
    const vLabel = document.getElementById('radio-volume-label');
    if (vSlider) vSlider.value = newVol;
    if (vLabel) vLabel.textContent = Math.round(newVol * 100) + '%';
  }

  if (rem <= 0) {
    radioSleepCancel();
    radioDismiss();
  }
}

function updateRadioBar(title, country, tag, playing, station) {
  _radioIsPlaying = playing;
  const t = document.getElementById('radio-bar-title');
  const m = document.getElementById('radio-bar-meta');
  const techText = document.getElementById('radio-bar-tech-text');
  const qBadge = document.getElementById('radio-bar-quality-badge');
  const tagsEl = document.getElementById('radio-bar-tags');
  const popEl = document.getElementById('radio-bar-pop');
  const hpEl = document.getElementById('radio-bar-homepage');
  const favWrap = document.getElementById('radio-bar-favicon-wrap');
  const favImg = document.getElementById('radio-bar-favicon');
  const btn = document.getElementById('radio-btn-play');
  if (t) t.textContent = title || '\u2014';
  if (m) {
    const parts = [country, tag].filter(Boolean);
    m.textContent = parts.length ? parts.join(' \u00b7 ') : '';
  }
  // Extra detail from full station object
  if (station) {
    const codec = (station.codec || station.Codec || '').toUpperCase();
    const bitrate = station.bitrate || station.Bitrate || 0;
    const hls = station.hls || station.Hls || 0;
    const language = station.language || station.Language || '';
    const tags = (station.tags || station.Tags || '')
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean)
      .slice(0, 5)
      .join(', ');
    const votes = station.votes || station.Votes || 0;
    const clicks = station.clickcount || station.ClickCount || 0;
    const homepage = station.homepage || station.Homepage || '';
    const favicon = station.favicon || station.Favicon || '';
    // Quality badge: codec + bitrate
    if (qBadge) {
      const badgeParts = [codec, bitrate ? bitrate + 'k' : ''].filter(Boolean);
      if (badgeParts.length) {
        qBadge.textContent = badgeParts.join(' ');
        qBadge.style.display = '';
      } else {
        qBadge.style.display = 'none';
      }
    }
    const techParts = [hls ? 'HLS' : '', language ? '\uD83D\uDDE3\uFE0F ' + language : ''].filter(
      Boolean
    );
    if (techText) techText.textContent = techParts.join(' · ');
    if (tagsEl) tagsEl.textContent = tags;
    const popParts = [
      votes ? '\u25B2 ' + votes.toLocaleString() : '',
      clicks ? '\u25B6 ' + clicks.toLocaleString() : '',
    ].filter(Boolean);
    if (popEl) popEl.textContent = popParts.join('  ');
    if (hpEl) {
      if (homepage) {
        const label = homepage.replace(/^https?:\/\//, '').split('/')[0];
        hpEl.innerHTML = `<a href="${escHtml(homepage)}" target="_blank" rel="noopener" class="station-homepage">${escHtml(label)}</a>`;
      } else {
        hpEl.textContent = '';
      }
    }
    if (favImg && favWrap) {
      if (favicon) {
        favWrap.style.display = 'none';
        favImg.onload = () => {
          favWrap.style.display = '';
        };
        favImg.onerror = () => {
          favWrap.style.display = 'none';
        };
        favImg.src = favicon;
      } else {
        favWrap.style.display = 'none';
      }
    }
    renderRadioGlobeAsync(station).catch(() => {});
  } else {
    if (qBadge) qBadge.style.display = 'none';
    if (techText) techText.textContent = '';
    if (tagsEl) tagsEl.textContent = '';
    if (popEl) popEl.textContent = '';
    if (hpEl) hpEl.textContent = '';
    if (favWrap) favWrap.style.display = 'none';
    renderRadioGlobeAsync(null).catch(() => {});
  }
  if (!playing) {
    stopWaveform();
    stopElapsedTick();
    const el = document.getElementById('radio-bar-elapsed');
    if (el) el.style.display = 'none';
  }
  if (btn) btn.innerHTML = playing ? '\u23F8 Pause' : '\u25B6 Play';
}

async function radioPlayHere() {
  if (!_radioCurrentUrl) return;
  const url = _radioCurrentUrl;
  const name = _radioCurrentName || 'Radio';
  // Stop server radio so we don't hear two streams
  await radioStop();
  localPlay(url, name, 'Radio');
}

// Country centroid lookup [lat, lon] keyed by ISO 3166-1 alpha-2 code.
// Used as a fallback when a station has no exact geo_lat/geo_long.
const _countryCentroids = {
  AD: [42.55, 1.57],
  AE: [23.42, 53.85],
  AF: [33.94, 67.71],
  AG: [17.06, -61.8],
  AL: [41.15, 20.17],
  AM: [40.07, 45.04],
  AO: [-11.2, 17.87],
  AR: [-38.42, -63.62],
  AT: [47.52, 14.55],
  AU: [-25.27, 133.78],
  AZ: [40.14, 47.58],
  BA: [43.92, 17.68],
  BB: [13.19, -59.54],
  BD: [23.68, 90.36],
  BE: [50.5, 4.47],
  BF: [12.36, -1.56],
  BG: [42.73, 25.49],
  BH: [26.02, 50.55],
  BI: [-3.37, 29.92],
  BJ: [9.31, 2.32],
  BN: [4.54, 114.73],
  BO: [-16.29, -63.59],
  BR: [-14.24, -51.93],
  BS: [25.03, -77.4],
  BT: [27.51, 90.43],
  BW: [-22.33, 24.68],
  BY: [53.71, 27.95],
  BZ: [17.19, -88.5],
  CA: [56.13, -106.35],
  CD: [-4.04, 21.76],
  CF: [6.61, 20.94],
  CG: [-0.23, 15.83],
  CH: [46.82, 8.23],
  CI: [7.54, -5.55],
  CL: [-35.68, -71.54],
  CM: [3.85, 11.5],
  CN: [35.86, 104.2],
  CO: [4.57, -74.3],
  CR: [9.75, -83.75],
  CU: [21.52, -77.78],
  CV: [16.54, -23.04],
  CY: [35.13, 33.43],
  CZ: [49.82, 15.47],
  DE: [51.17, 10.45],
  DJ: [11.83, 42.59],
  DK: [56.26, 9.5],
  DM: [15.41, -61.37],
  DO: [18.74, -70.16],
  DZ: [28.03, 1.66],
  EC: [-1.83, -78.18],
  EE: [58.6, 25.01],
  EG: [26.82, 30.8],
  EH: [24.22, -12.89],
  ER: [15.18, 39.78],
  ES: [40.46, -3.75],
  ET: [9.15, 40.49],
  FI: [61.92, 25.75],
  FJ: [-16.58, 179.41],
  FK: [-51.8, -59.52],
  FR: [46.23, 2.21],
  GA: [-0.8, 11.61],
  GB: [55.38, -3.44],
  GD: [12.11, -61.68],
  GE: [42.32, 43.36],
  GH: [7.95, -1.02],
  GM: [13.44, -15.31],
  GN: [9.95, -11.81],
  GQ: [1.65, 10.27],
  GR: [39.07, 21.82],
  GT: [15.78, -90.23],
  GW: [11.8, -15.18],
  GY: [4.86, -58.93],
  HN: [15.2, -86.24],
  HR: [45.1, 15.2],
  HT: [18.97, -72.29],
  HU: [47.16, 19.5],
  ID: [-0.79, 113.92],
  IE: [53.41, -8.24],
  IL: [31.05, 34.85],
  IN: [20.59, 78.96],
  IQ: [33.22, 43.68],
  IR: [32.43, 53.69],
  IS: [64.96, -19.02],
  IT: [41.87, 12.57],
  JM: [18.11, -77.3],
  JO: [30.59, 36.24],
  JP: [36.2, 138.25],
  KE: [-0.02, 37.91],
  KG: [41.2, 74.77],
  KH: [12.57, 104.99],
  KI: [-3.37, -168.73],
  KM: [-11.64, 43.33],
  KN: [17.36, -62.78],
  KP: [40.34, 127.51],
  KR: [35.91, 127.77],
  KW: [29.31, 47.48],
  KZ: [48.02, 66.92],
  LA: [19.86, 102.5],
  LB: [33.85, 35.86],
  LC: [13.91, -60.98],
  LI: [47.14, 9.55],
  LK: [7.87, 80.77],
  LR: [6.43, -9.43],
  LS: [-29.61, 28.23],
  LT: [55.17, 23.88],
  LU: [49.82, 6.13],
  LV: [56.88, 24.6],
  LY: [26.34, 17.23],
  MA: [31.79, -7.09],
  MC: [43.75, 7.4],
  MD: [47.41, 28.37],
  ME: [42.71, 19.37],
  MG: [-18.77, 46.87],
  MH: [7.13, 171.18],
  MK: [41.61, 21.75],
  ML: [17.57, -3.99],
  MM: [16.87, 96.08],
  MN: [46.86, 103.85],
  MR: [21.01, -10.94],
  MT: [35.94, 14.38],
  MU: [-20.35, 57.55],
  MV: [3.2, 73.22],
  MW: [-13.25, 34.3],
  MX: [23.63, -102.55],
  MY: [4.21, 108.03],
  MZ: [-18.67, 35.53],
  NA: [-22.96, 18.49],
  NE: [17.61, 8.08],
  NG: [9.08, 8.68],
  NI: [12.87, -85.21],
  NL: [52.13, 5.29],
  NO: [60.47, 8.47],
  NP: [28.39, 84.12],
  NR: [-0.52, 166.93],
  NZ: [-40.9, 174.89],
  OM: [21.51, 55.92],
  PA: [8.54, -80.78],
  PE: [-9.19, -75.02],
  PG: [-6.31, 143.96],
  PH: [12.88, 121.77],
  PK: [30.38, 69.35],
  PL: [51.92, 19.15],
  PT: [39.4, -8.22],
  PW: [7.51, 134.58],
  PY: [-23.44, -58.44],
  QA: [25.35, 51.18],
  RO: [45.94, 24.97],
  RS: [44.02, 21.01],
  RU: [61.52, 105.32],
  RW: [-1.94, 29.87],
  SA: [23.89, 45.08],
  SB: [-9.64, 160.16],
  SC: [-4.68, 55.49],
  SD: [12.86, 30.22],
  SE: [60.13, 18.64],
  SG: [1.35, 103.82],
  SI: [46.15, 14.99],
  SK: [48.67, 19.7],
  SL: [8.46, -11.78],
  SM: [43.94, 12.46],
  SN: [14.5, -14.45],
  SO: [5.15, 46.2],
  SR: [3.92, -56.03],
  SS: [6.88, 31.57],
  ST: [0.19, 6.61],
  SV: [13.79, -88.9],
  SY: [34.8, 38.99],
  SZ: [-26.52, 31.47],
  TD: [15.45, 18.73],
  TG: [8.62, 0.82],
  TH: [15.87, 100.99],
  TJ: [38.86, 71.28],
  TL: [-8.87, 125.73],
  TM: [38.97, 59.56],
  TN: [33.89, 9.54],
  TO: [-21.18, -175.2],
  TR: [38.96, 35.24],
  TT: [10.69, -61.22],
  TV: [-7.11, 177.65],
  TW: [23.7, 120.96],
  TZ: [-6.37, 34.89],
  UA: [48.38, 31.17],
  UG: [1.37, 32.29],
  US: [37.09, -95.71],
  UY: [-32.52, -55.77],
  UZ: [41.38, 64.59],
  VA: [41.9, 12.45],
  VC: [12.98, -61.29],
  VE: [6.42, -66.59],
  VN: [14.06, 108.28],
  VU: [-15.38, 166.96],
  WS: [-13.76, -172.1],
  XK: [42.6, 20.9],
  YE: [15.55, 48.52],
  ZA: [-30.56, 22.94],
  ZM: [-13.13, 27.85],
  ZW: [-19.02, 29.15],
};
