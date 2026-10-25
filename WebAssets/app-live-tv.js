// ── Live TV ───────────────────────────────────────────────────────────
let _liveTvInited = false;
let _liveTvChannels = [];
let _liveTvPage = 0;
let _liveTvCurrentUrl = '';
const _liveTvPageSize = 240;
let _liveTvFilterQuery = '';
let _liveTvFilterCountry = '';
let _liveTvFilterCategory = '';
let _liveTvFilterLanguage = '';
let _liveTvShowFavoritesOnly = false;
let _liveTvHideFailed = false;
let _liveTvHideFailedExplicit = false;
let _liveTvShowHidden = false;
let _liveTvCountries = [];
let _liveTvCategories = [];
let _liveTvLanguages = [];
const _liveTvFailedKey = 'rp_live_tv_failed_urls';
let _liveTvFailedUrls = new Set();
const _liveTvReliabilityKey = 'rp_live_tv_reliability_v1';
const _liveTvFailureCooldownBaseMs = 2 * 60 * 60 * 1000;
const _liveTvFailureCooldownMaxMs = 24 * 60 * 60 * 1000;
const _liveTvReliabilityMaxEntries = 2500;
let _liveTvReliabilityByUrl = new Map();
const _liveTvFavoritesKey = 'rp_live_tv_favorites';
let _liveTvFavorites = new Set();
const _liveTvHiddenKey = 'rp_live_tv_hidden_urls';
let _liveTvHidden = new Set();
let _liveTvRouteToken = 'browse';
let _liveTvCurrentChannel = null;
let _liveTvAttemptingUrl = '';
let _liveTvPlaybackAttemptGeneration = 0;
let _liveTvCurrentRequestId = '';
let _liveTvPlayRequestController = null;
let _liveTvLastRequestedUrl = '';
let _liveTvLastPlayedUrl = '';
let _liveTvLastFallbackUsed = false;
let _liveTvLastStatusErrorSignature = '';
const _liveTvCatalogCacheKey = 'rp_live_tv_catalog_v1';
const _liveTvCatalogCacheTtlMs = 7 * 24 * 60 * 60 * 1000;
const _liveTvFilterStateKeyPrefix = 'rp_live_tv_filters_v1:';
const _liveTvPlaybackStartTimeoutMs = 15_000;
const _liveTvPlaybackStartBudgetMs = 45_000;
const _liveTvFailureAuditKey = 'rp_live_tv_failure_audit_v1';
const _liveTvFailureAuditMax = 40;
let _liveTvRefreshInFlight = null;
let _liveTvFailureAudit = [];
const _liveTvProbeIntervalMs = 10 * 60 * 1000;
let _liveTvLastProbeByUrl = new Map();
const _LIVE_TV_RECENT_KEY = 'liveTvRecentChannels';
const _LIVE_TV_RECENT_MAX = 8;

const _liveTvFallbackChannels = [
  { name: 'Al Jazeera English', countryCode: 'QA', countryName: 'Qatar', category: 'News', language: 'en', sourceHost: 'live-hls-web-aje.getaj.net', url: 'https://live-hls-web-aje.getaj.net/AJE/index.m3u8' },
  { name: 'France 24 English', countryCode: 'FR', countryName: 'France', category: 'News', language: 'en', sourceHost: 'static.france24.com', url: 'https://static.france24.com/live/F24_EN_HI_HLS/live_tv.m3u8' },
  { name: 'NHK World Japan', countryCode: 'JP', countryName: 'Japan', category: 'News', language: 'en', sourceHost: 'b-nhkwlive-ojp.webcdn.stream.ne.jp', url: 'https://b-nhkwlive-ojp.webcdn.stream.ne.jp/hls/live/2003459-b/nhkwlive-ojp-en/index.m3u8' },
  { name: 'TRT World', countryCode: 'TR', countryName: 'Türkiye', category: 'News', language: 'en', sourceHost: 'tv-trtworld.live.trt.com.tr', url: 'https://tv-trtworld.live.trt.com.tr/master_720.m3u8' },
  { name: 'CGTN English', countryCode: 'CN', countryName: 'China', category: 'News', language: 'en', sourceHost: 'english-livebkali.cgtn.com', url: 'https://english-livebkali.cgtn.com/hls/LSveGEJrlM/playlist.m3u8' },
];

function _liveTvEsc(s) {
  return String(s ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function _liveTvEncodeInlineArgument(value) {
  return encodeURIComponent(String(value ?? '')).replace(/'/g, '%27');
}

function _liveTvLanguageName(codeOrName) {
  const raw = String(codeOrName || '').trim();
  if (!raw) return '';

  const normalized = raw.toLowerCase();
  try {
    if (typeof Intl !== 'undefined' && typeof Intl.DisplayNames === 'function') {
      const dn = new Intl.DisplayNames(['en'], { type: 'language' });
      const direct = dn.of(normalized);
      if (direct && direct.toLowerCase() !== normalized) return direct;
      if (normalized.includes('-')) {
        const base = normalized.split('-')[0];
        const baseName = dn.of(base);
        if (baseName && baseName.toLowerCase() !== base) return baseName;
      }
    }
  } catch {}

  return raw;
}

function _liveTvCanonicalCategory(value) {
  const raw = String(value || '').trim();
  if (!raw) return '';
  return raw
    .split(/\s+/)
    .map((part) => part ? part[0].toUpperCase() + part.slice(1).toLowerCase() : '')
    .join(' ')
    .trim();
}

function _liveTvCanonicalCountryCode(value) {
  const raw = String(value || '').trim().toUpperCase();
  if (!raw) return '';
  if (raw === 'UK') return 'GB';
  return /^[A-Z]{2}$/.test(raw) ? raw : '';
}

function _liveTvDecorateChannel(channel) {
  if (!channel || typeof channel !== 'object') return null;
  const countryName = _liveTvChannelCountryName(channel);
  const category = _liveTvCanonicalCategory(channel.category);
  const language = _liveTvLanguageName(channel.language);

  return {
    ...channel,
    category,
    language,
    countryName: countryName || channel.countryName || '',
    _countryName: countryName,
    _searchText: [
      channel.name,
      countryName,
      category,
      language,
      channel.sourceHost,
    ].filter(Boolean).join(' ').toLowerCase(),
  };
}

function _liveTvStreamPreferenceScore(url) {
  const u = String(url || '').toLowerCase();
  if (u.includes('.m3u8')) return 3;
  if (u.includes('.mpd')) return 2;
  return 1;
}

function _liveTvCatalogDeduplicate(channels) {
  const bestByKey = new Map();

  for (const channel of channels || []) {
    if (!channel || !channel.url) continue;
    const nameKey = String(channel.name || '').trim().toLowerCase();
    const countryKey = String(_liveTvCanonicalCountryCode(channel.countryCode) || '').trim();
    const hostKey = String(channel.sourceHost || _liveTvHostLabel(channel.url) || '').trim().toLowerCase();
    const dedupeKey = `${nameKey}|${countryKey}|${hostKey}`;
    if (!nameKey) continue;

    const existing = bestByKey.get(dedupeKey);
    if (!existing) {
      bestByKey.set(dedupeKey, channel);
      continue;
    }

    const existingScore = _liveTvReliabilityScore(existing.url) + (_isLiveTvChannelFailed(existing.url) ? -0.3 : 0) + (_liveTvStreamPreferenceScore(existing.url) * 0.1);
    const candidateScore = _liveTvReliabilityScore(channel.url) + (_isLiveTvChannelFailed(channel.url) ? -0.3 : 0) + (_liveTvStreamPreferenceScore(channel.url) * 0.1);
    if (candidateScore > existingScore) bestByKey.set(dedupeKey, channel);
  }

  return Array.from(bestByKey.values());
}

function _liveTvNormalizeChannel(raw) {
  if (!raw || typeof raw !== 'object') return null;

  const name = raw.name || raw.Name || '';
  const url = raw.url || raw.Url || '';
  if (!name || !url) return null;

  const categoryRaw = raw.category || raw.Category || (Array.isArray(raw.categories) ? (raw.categories[0] || '') : '');
  const languageRaw = raw.language || raw.Language || (Array.isArray(raw.languages) ? (raw.languages[0] || '') : '');
  const countryCode = _liveTvCanonicalCountryCode(raw.countryCode || raw.CountryCode || raw.country || raw.Country || '');
  const countryName = String(raw.countryName || raw.CountryName || '').trim() || _liveTvCountryName(countryCode);
  const language = _liveTvLanguageName(languageRaw);
  const category = _liveTvCanonicalCategory(categoryRaw);

  return {
    name: String(name).trim(),
    countryCode,
    countryName,
    category,
    language,
    sourceHost: raw.sourceHost || raw.SourceHost || _liveTvHostLabel(url),
    url: String(url).trim(),
    urls: Array.isArray(raw.urls) ? raw.urls.filter((x) => typeof x === 'string' && _liveTvIsPlayableUrl(x)) : undefined,
  };
}

function _liveTvPushRecentChannel(channel, name, url) {
  if (!url) return;
  try {
    const stored = JSON.parse(localStorage.getItem(_LIVE_TV_RECENT_KEY) || '[]');
    const countryName = _liveTvChannelCountryName(channel);
    const entry = {
      name: name || (channel && channel.name) || 'Live TV',
      url,
      countryName: countryName || '',
      category: (channel && channel.category) || '',
      language: (channel && channel.language) || '',
      sourceHost: (channel && channel.sourceHost) || _liveTvHostLabel(url),
      ts: Date.now(),
    };
    const filtered = stored.filter((c) => c.url !== url);
    filtered.unshift(entry);
    localStorage.setItem(_LIVE_TV_RECENT_KEY, JSON.stringify(filtered.slice(0, _LIVE_TV_RECENT_MAX)));
    renderLiveTvRecent();
  } catch {}
}

function _liveTvGetRecentChannels() {
  try {
    return JSON.parse(localStorage.getItem(_LIVE_TV_RECENT_KEY) || '[]');
  } catch {
    return [];
  }
}

function renderLiveTvRecent() {
  const strip = document.getElementById('live-tv-recent-strip');
  if (!strip) return;
  const clearBtn = document.getElementById('live-tv-recent-clear');
  strip.innerHTML = '';
  const items = _liveTvGetRecentChannels();
  if (!items.length) {
    if (clearBtn) clearBtn.style.display = 'none';
    return;
  }
  if (clearBtn) clearBtn.style.display = '';

  strip.innerHTML = items.slice(0, 5).map((c) => {
    const safeName = _liveTvEsc(c.name || 'Live TV');
    const encUrl = _liveTvEncodeInlineArgument(c.url || '');
    const encName = _liveTvEncodeInlineArgument(c.name || 'Live TV');
    const meta = [c.countryName, c.category].filter(Boolean).join(' • ');
    return (
      '<div class="vr-card" role="button" tabindex="0" title="' + safeName + '"'
      + ' onclick="liveTvPlayChannel(decodeURIComponent(\'' + encUrl + '\'),decodeURIComponent(\'' + encName + '\'))"'
      + ' onkeydown="activateKeyboardClick(event,this)">'
      + '<div class="vr-thumb vr-thumb-placeholder">&#128250;</div>'
      + '<div class="vr-label">' + safeName + '</div>'
      + (meta ? '<div class="vr-sub">' + _liveTvEsc(meta) + '</div>' : '')
      + '</div>'
    );
  }).join('');
}

function clearLiveTvRecent() {
  try { localStorage.removeItem(_LIVE_TV_RECENT_KEY); } catch {}
  renderLiveTvRecent();
}

function _loadLiveTvCatalogCache() {
  try {
    const raw = JSON.parse(localStorage.getItem(_liveTvCatalogCacheKey) || 'null');
    if (!raw || !Array.isArray(raw.channels)) return null;
    const savedAt = Number(raw.savedAt || 0);
    if (!Number.isFinite(savedAt) || savedAt <= 0) return null;
    if (Date.now() - savedAt > _liveTvCatalogCacheTtlMs) return null;
    return { savedAt, channels: raw.channels };
  } catch {
    return null;
  }
}

function _saveLiveTvCatalogCache(channels) {
  try {
    localStorage.setItem(
      _liveTvCatalogCacheKey,
      JSON.stringify({ savedAt: Date.now(), channels })
    );
  } catch {
    // Ignore quota/storage failures; runtime will fallback to network each time.
  }
}

function _clearLiveTvCatalogCache() {
  try {
    localStorage.removeItem(_liveTvCatalogCacheKey);
  } catch {}
}

function _loadLiveTvFavorites() {
  try {
    const raw = JSON.parse(localStorage.getItem(_liveTvFavoritesKey) || '[]');
    _liveTvFavorites = new Set(Array.isArray(raw) ? raw.filter((x) => typeof x === 'string' && x) : []);
  } catch {
    _liveTvFavorites = new Set();
  }
}

function _liveTvApplyChannels(channels) {
  _liveTvChannels = Array.isArray(channels)
    ? _liveTvCatalogDeduplicate(channels.map(_liveTvNormalizeChannel).filter(Boolean)).map(_liveTvDecorateChannel).filter(Boolean)
    : [];
  _liveTvPage = 0;
  _liveTvRefreshFilterMeta();
  _liveTvRenderFilters();
  renderLiveTvCards();
}

function _liveTvSetIndexingProgress(text) {
  const scan = document.getElementById('scan-status');
  if (scan) {
    scan.style.display = '';
    scan.textContent = text || '';
  }
}

function _liveTvClearIndexingProgress() {
  const scan = document.getElementById('scan-status');
  if (scan) {
    scan.textContent = '';
    scan.style.display = 'none';
  }
}

function _saveLiveTvFavorites() {
  try {
    localStorage.setItem(_liveTvFavoritesKey, JSON.stringify(Array.from(_liveTvFavorites)));
  } catch {}
}

function _loadLiveTvHidden() {
  try {
    const raw = JSON.parse(localStorage.getItem(_liveTvHiddenKey) || '[]');
    _liveTvHidden = new Set(Array.isArray(raw) ? raw.filter((x) => typeof x === 'string' && x) : []);
  } catch {
    _liveTvHidden = new Set();
  }
}

function _saveLiveTvHidden() {
  try {
    localStorage.setItem(_liveTvHiddenKey, JSON.stringify(Array.from(_liveTvHidden)));
  } catch {}
}

function _isLiveTvHidden(url) {
  const key = _liveTvFavoriteKey(url);
  return !!key && _liveTvHidden.has(key);
}

function liveTvHideChannel(url, event) {
  if (event) {
    event.preventDefault();
    event.stopPropagation();
  }

  const key = _liveTvFavoriteKey(url);
  if (!key) return;
  _liveTvHidden.add(key);
  _saveLiveTvHidden();
  renderLiveTvCards();
}

function liveTvUnhideChannel(url, event) {
  if (event) {
    event.preventDefault();
    event.stopPropagation();
  }

  const key = _liveTvFavoriteKey(url);
  if (!key) return;
  _liveTvHidden.delete(key);
  _saveLiveTvHidden();
  renderLiveTvCards();
}

function _liveTvFavoriteKey(url) {
  return _liveTvFailureKey(url);
}

function _isLiveTvFavorite(url) {
  const key = _liveTvFavoriteKey(url);
  return !!key && _liveTvFavorites.has(key);
}

function liveTvToggleFavorite(url, event) {
  if (event) {
    event.preventDefault();
    event.stopPropagation();
  }
  const key = _liveTvFavoriteKey(url);
  if (!key) return;

  if (_liveTvFavorites.has(key)) _liveTvFavorites.delete(key);
  else _liveTvFavorites.add(key);

  _saveLiveTvFavorites();
  renderLiveTvCards();
}

function liveTvToggleFavoritesFilter() {
  _liveTvShowFavoritesOnly = !_liveTvShowFavoritesOnly;
  _liveTvPage = 0;
  _liveTvSaveFilterState();
  _liveTvRenderFilters();
  renderLiveTvCards();
}

function liveTvToggleHideFailedFilter() {
  _liveTvHideFailed = !_liveTvHideFailed;
  _liveTvHideFailedExplicit = true;
  _liveTvPage = 0;
  _liveTvSaveFilterState();
  _liveTvRenderFilters();
  renderLiveTvCards();
}

function liveTvToggleShowHidden() {
  _liveTvShowHidden = !_liveTvShowHidden;
  _liveTvPage = 0;
  _liveTvRenderFilters();
  renderLiveTvCards();
}

function _liveTvFailureKey(url) {
  const raw = String(url || '').trim();
  if (!raw) return '';
  try {
    const u = new URL(raw);
    const path = (u.pathname || '/').replace(/\/+$/, '') || '/';
    return `${u.protocol}//${u.host}${path}`.toLowerCase();
  } catch {
    return raw.toLowerCase();
  }
}

function _loadFailedLiveTvChannels() {
  try {
    const raw = JSON.parse(localStorage.getItem(_liveTvFailedKey) || '[]');
    _liveTvFailedUrls = new Set(Array.isArray(raw) ? raw.filter((x) => typeof x === 'string' && x) : []);
  } catch {
    _liveTvFailedUrls = new Set();
  }
}

function _loadLiveTvReliability() {
  try {
    const raw = JSON.parse(localStorage.getItem(_liveTvReliabilityKey) || '{}');
    const entries = Object.entries(raw || {});
    _liveTvReliabilityByUrl = new Map(entries.filter(([k, v]) => {
      if (!k || !v || typeof v !== 'object') return false;
      return true;
    }));
  } catch {
    _liveTvReliabilityByUrl = new Map();
  }
}

function _saveLiveTvReliability() {
  try {
    const entries = Array.from(_liveTvReliabilityByUrl.entries())
      .sort((a, b) => {
        const aTime = Number(a[1]?.lastSuccess || a[1]?.lastFailure || 0);
        const bTime = Number(b[1]?.lastSuccess || b[1]?.lastFailure || 0);
        return bTime - aTime;
      })
      .slice(0, _liveTvReliabilityMaxEntries);
    const normalized = Object.fromEntries(entries);
    localStorage.setItem(_liveTvReliabilityKey, JSON.stringify(normalized));
  } catch {}
}

function _liveTvReliability(url) {
  const key = _liveTvFailureKey(url);
  if (!key) return null;
  const current = _liveTvReliabilityByUrl.get(key);
  if (current && typeof current === 'object') {
    return {
      successCount: Math.max(0, Number(current.successCount || 0)),
      failCount: Math.max(0, Number(current.failCount || 0)),
      lastSuccess: Math.max(0, Number(current.lastSuccess || 0)),
      lastFailure: Math.max(0, Number(current.lastFailure || 0)),
      cooldownUntil: Math.max(0, Number(current.cooldownUntil || 0)),
    };
  }

  return {
    successCount: 0,
    failCount: 0,
    lastSuccess: 0,
    lastFailure: 0,
    cooldownUntil: 0,
  };
}

function _liveTvSetReliability(url, value) {
  const key = _liveTvFailureKey(url);
  if (!key || !value) return;
  _liveTvReliabilityByUrl.set(key, value);
  _saveLiveTvReliability();
}

function _liveTvReliabilityScore(url) {
  const info = _liveTvReliability(url);
  if (!info) return 0;

  const success = info.successCount;
  const fail = info.failCount;
  const ratio = (success + 1) / (success + fail + 2);
  const recency = info.lastSuccess > 0 ? 0.2 : 0;
  const penalty = info.lastFailure > info.lastSuccess ? 0.15 : 0;

  return ratio + recency - penalty;
}

function _isLiveTvOnCooldown(url) {
  const info = _liveTvReliability(url);
  if (!info) return false;
  return Number(info.cooldownUntil || 0) > Date.now();
}

function _saveFailedLiveTvChannels() {
  try {
    localStorage.setItem(_liveTvFailedKey, JSON.stringify(Array.from(_liveTvFailedUrls)));
  } catch {}
}

function _markLiveTvChannelFailed(url, reason) {
  const key = _liveTvFailureKey(url);
  if (!key) return;
  _liveTvFailedUrls.add(key);
  const info = _liveTvReliability(url);
  if (info) {
    const failCount = Math.max(1, info.failCount + 1);
    const cooldownMs = Math.min(_liveTvFailureCooldownMaxMs, _liveTvFailureCooldownBaseMs * failCount);
    _liveTvSetReliability(url, {
      ...info,
      failCount,
      lastFailure: Date.now(),
      cooldownUntil: Date.now() + cooldownMs,
    });
  }
  _saveFailedLiveTvChannels();
  _liveTvPushFailureAudit(url, reason);
}

function _loadLiveTvFailureAudit() {
  try {
    const raw = JSON.parse(localStorage.getItem(_liveTvFailureAuditKey) || '[]');
    _liveTvFailureAudit = Array.isArray(raw)
      ? raw.filter((x) => x && typeof x === 'object' && typeof x.url === 'string').slice(0, _liveTvFailureAuditMax)
      : [];
  } catch {
    _liveTvFailureAudit = [];
  }
}

function _saveLiveTvFailureAudit() {
  try {
    localStorage.setItem(_liveTvFailureAuditKey, JSON.stringify(_liveTvFailureAudit.slice(0, _liveTvFailureAuditMax)));
  } catch {}
}

function _liveTvPushFailureAudit(url, reason) {
  const key = _liveTvFailureKey(url);
  if (!key) return;

  const ts = Date.now();
  const channel = _liveTvChannels.find((c) => _liveTvFailureKey(c.url) === key);
  const channelName = channel?.name || '';
  const text = String(reason || '').trim();
  _liveTvFailureAudit = [{
    url: key,
    channelName,
    reason: text,
    ts,
  }, ..._liveTvFailureAudit.filter((x) => x.url !== key)]
    .slice(0, _liveTvFailureAuditMax);
  _saveLiveTvFailureAudit();
}

function _liveTvNoteProbeResult(url, ok) {
  const info = _liveTvReliability(url);
  if (!info) return;

  if (ok) {
    _liveTvSetReliability(url, {
      ...info,
      successCount: info.successCount + 1,
      lastSuccess: Date.now(),
    });
    return;
  }

  _liveTvSetReliability(url, {
    ...info,
    failCount: info.failCount + 1,
    lastFailure: Date.now(),
  });
}

async function _liveTvProbeChannelHealth(channel) {
  if (!channel || !channel.url) return;
  const key = _liveTvFailureKey(channel.url);
  if (!key) return;

  const now = Date.now();
  const lastProbe = Number(_liveTvLastProbeByUrl.get(key) || 0);
  if (now - lastProbe < _liveTvProbeIntervalMs) return;
  _liveTvLastProbeByUrl.set(key, now);

  let probeResult = null;
  const ctrl = typeof AbortController === 'function' ? new AbortController() : null;
  const timeout = setTimeout(() => ctrl?.abort(), 2500);
  try {
    const response = await fetch(channel.url, {
      method: 'HEAD',
      mode: 'no-cors',
      cache: 'no-store',
      signal: ctrl?.signal,
    });
    if (response && response.type !== 'opaque' && response.status !== 0) {
      probeResult = !!response.ok;
    } else if (response) {
      _liveTvEmitTelemetry('health_probe_unknown', {
        host: _liveTvHostLabel(channel.url),
        reason: response.type === 'opaque' ? 'opaque_response' : 'status_unavailable',
        phase: 'health_probe',
      });
    }
  } catch (err) {
    probeResult = null;
    if (err?.name !== 'AbortError') {
      _liveTvEmitTelemetry('health_probe_unknown', {
        host: _liveTvHostLabel(channel.url),
        reason: err?.name || 'network_error',
        phase: 'health_probe',
      });
    }
  } finally {
    clearTimeout(timeout);
  }

  if (probeResult !== null) {
    _liveTvNoteProbeResult(channel.url, probeResult);
  }
}

function _liveTvProbeVisibleChannels(channels) {
  const candidates = (channels || [])
    .filter((c) => c && c.url)
    .filter((c) => !_isLiveTvChannelFailed(c.url))
    .slice(0, 3);

  for (const channel of candidates) {
    _liveTvProbeChannelHealth(channel);
  }
}

function _markLiveTvChannelRecovered(url) {
  const key = _liveTvFailureKey(url);
  if (!key) return;
  const info = _liveTvReliability(url);
  if (info) {
    _liveTvSetReliability(url, {
      ...info,
      successCount: info.successCount + 1,
      lastSuccess: Date.now(),
      cooldownUntil: 0,
    });
  }
  if (_liveTvFailedUrls.delete(key)) _saveFailedLiveTvChannels();
}

function _clearLiveTvChannelFailureState(url) {
  const key = _liveTvFailureKey(url);
  if (!key) return;

  const info = _liveTvReliability(url);
  if (info) {
    _liveTvSetReliability(url, {
      ...info,
      cooldownUntil: 0,
    });
  }

  if (_liveTvFailedUrls.delete(key)) _saveFailedLiveTvChannels();
}

function _isLiveTvChannelFailed(url) {
  const key = _liveTvFailureKey(url);
  return !!key && _liveTvFailedUrls.has(key);
}

function _markLiveTvRelatedChannelsFailed(channel, fallbackName, reason) {
  const nameKey = String(channel?.name || fallbackName || '').trim().toLowerCase();
  if (!nameKey) return;

  const countryKey = _liveTvCountryCode(channel);
  for (const item of _liveTvChannels) {
    const itemName = String(item?.name || '').trim().toLowerCase();
    if (!itemName || itemName !== nameKey) continue;
    if (countryKey && _liveTvCountryCode(item) !== countryKey) continue;
    _markLiveTvChannelFailed(item.url, reason);
  }
}

function _liveTvFailureCategory(rawMessage, statusCode) {
  const msg = String(rawMessage || '').trim().toLowerCase();
  const status = Number(statusCode);
  if (msg.includes('destination is not allowed')) return 'unsafe_destination';
  if (status === 401 || status === 403) return 'provider_rejected';
  if (status === 404 || status === 410) return 'stream_unavailable';
  if (status === 502 || status === 503 || status === 504) return 'provider_unavailable';
  if (msg.includes('timed out')
    || msg.includes('timeout')
    || msg.includes('no playback confirmation')
    || msg.includes('startup exceeded')) return 'startup_timeout';
  if (msg.includes('network') || msg.includes('connection') || msg.includes('resolve')) return 'network_error';
  return 'playback_failed';
}

function _liveTvFriendlyFailureReason(rawMessage, statusCode) {
  const msg = String(rawMessage || '').trim();
  const l = msg.toLowerCase();
  const status = Number(statusCode);
  if (!l) return 'The channel could not be started by the player.';
  if (l.includes('destination is not allowed'))
    return 'This stream URL or redirect points to a private or unsupported destination.';
  if (l.includes('timed out')
    || l.includes('timeout')
    || l.includes('no playback confirmation')
    || l.includes('startup exceeded'))
    return 'The stream timed out before playback started.';
  if (status === 401 || status === 403 || l.includes('forbidden') || l.includes('401') || l.includes('403'))
    return 'This stream is blocked by the provider (authorization or geo restriction).';
  if (status === 404 || status === 410 || l.includes('404') || l.includes('410') || l.includes('not found'))
    return 'This channel stream URL is no longer valid.';
  if (status === 502 || status === 503 || status === 504)
    return 'The stream provider is temporarily unavailable.';
  if (status === 400 || l.includes('bad request'))
    return 'The stream provider rejected the request as invalid.';
  if (l.includes('unsupported') || l.includes('codec'))
    return 'The stream format/codec is not supported by the current VLC playback path.';
  if (l.includes('network') || l.includes('connection') || l.includes('resolve'))
    return 'A network or DNS issue prevented connecting to the stream host.';
  return 'The channel could not be started by VLC on this instance.';
}

function _liveTvApplyNowPlayingInfo() {
  if (!_liveTvCurrentChannel) return;
  const title = document.getElementById('player-title');
  const meta = document.getElementById('player-meta');
  if (title) title.textContent = _liveTvCurrentChannel.name || 'Live TV';
  if (meta) {
    const parts = ['Live TV'];
    if (_liveTvCurrentChannel.countryName) parts.push(_liveTvCurrentChannel.countryName);
    if (_liveTvCurrentChannel.category) parts.push(_liveTvCurrentChannel.category);
    if (_liveTvCurrentChannel.sourceHost) parts.push(_liveTvCurrentChannel.sourceHost);
    if (_liveTvLastFallbackUsed) parts.push('Fallback source');
    meta.textContent = parts.join(' • ');
  }
}

function liveTvGetNowPlayingInfo() {
  return _liveTvCurrentChannel;
}

function liveTvHandleStatusPlaybackError(rawError, statusUrl) {
  const raw = String(rawError || '').trim();
  if (!raw) return false;
  if (_liveTvAttemptingUrl || !_liveTvCurrentUrl) return false;

  const reportedUrl = _liveTvDecodeStatusUrl(statusUrl);
  if (reportedUrl && !_liveTvPlaybackUrlsMatch(_liveTvCurrentUrl, reportedUrl)) {
    return false;
  }

  const currentUrl = reportedUrl || _liveTvCurrentUrl;
  const signature = `${currentUrl}|${raw}`;
  if (_liveTvLastStatusErrorSignature === signature) return true;
  _liveTvLastStatusErrorSignature = signature;

  _markLiveTvChannelFailed(currentUrl, raw);
  renderLiveTvCards();
  return true;
}

function liveTvClearStatusPlaybackError() {
  _liveTvLastStatusErrorSignature = '';
}

function liveTvCancelPendingPlaybackStart(reason = 'user') {
  const hadPendingStart = Boolean(_liveTvAttemptingUrl);
  if (hadPendingStart) {
    _liveTvEmitTelemetry('play_cancelled', {
      host: _liveTvHostLabel(_liveTvAttemptingUrl),
      reason,
      phase: 'cancelled',
    });
  }
  _liveTvPlaybackAttemptGeneration += 1;
  _liveTvPlayRequestController?.abort();
  _liveTvPlayRequestController = null;
  _liveTvAttemptingUrl = '';

  _liveTvCloseQuickActionsModal();
  if (typeof closePlaybackErrorPopup === 'function') closePlaybackErrorPopup();
  if (typeof clearPlaybackErrorDedup === 'function') clearPlaybackErrorDedup('Live TV');
  liveTvClearStatusPlaybackError();
  _liveTvCurrentUrl = '';
  _liveTvLastPlayedUrl = '';
  _liveTvLastFallbackUsed = false;
  _liveTvCurrentChannel = null;
  _liveTvCurrentRequestId = '';
  renderLiveTvCards();
  return hadPendingStart;
}

function _liveTvTokenFromChannelName(name) {
  return String(name || 'channel').trim() || 'channel';
}

function _liveTvSyncRoute(token) {
  _liveTvRouteToken = token || 'browse';
  if (typeof _syncUiRoute === 'function') _syncUiRoute('live-tv', _liveTvRouteToken);
}

function liveTvApplyRouteToken(token) {
  const raw = String(token || '').trim();
  _liveTvRouteToken = raw || 'browse';
  _liveTvLoadFilterState();
}

function _liveTvFilterStateKey() {
  return _liveTvFilterStateKeyPrefix + String(_liveTvRouteToken || 'browse');
}

function _liveTvLoadFilterState() {
  _liveTvFilterQuery = '';
  _liveTvFilterCountry = '';
  _liveTvFilterCategory = '';
  _liveTvFilterLanguage = '';
  _liveTvShowFavoritesOnly = false;
  _liveTvHideFailed = false;
  _liveTvHideFailedExplicit = false;
  _liveTvShowHidden = false;
  _liveTvPage = 0;
  try {
    const raw = JSON.parse(localStorage.getItem(_liveTvFilterStateKey()) || 'null');
    if (!raw || typeof raw !== 'object') return;
    _liveTvFilterQuery = String(raw.query || '').trim();
    _liveTvFilterCountry = String(raw.country || '').trim();
    _liveTvFilterCategory = String(raw.category || '').trim();
    _liveTvFilterLanguage = String(raw.language || '').trim();
    _liveTvShowFavoritesOnly = !!raw.favoritesOnly;
    _liveTvHideFailedExplicit = raw.hideFailedExplicit === true;
    _liveTvHideFailed = _liveTvHideFailedExplicit && raw.hideFailed === true;
  } catch {}
}

function _liveTvSaveFilterState() {
  try {
    localStorage.setItem(_liveTvFilterStateKey(), JSON.stringify({
      query: _liveTvFilterQuery,
      country: _liveTvFilterCountry,
      category: _liveTvFilterCategory,
      language: _liveTvFilterLanguage,
      favoritesOnly: _liveTvShowFavoritesOnly,
      hideFailed: _liveTvHideFailed,
      hideFailedExplicit: _liveTvHideFailedExplicit,
    }));
  } catch {}
}

function _liveTvDelay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function _liveTvIsPlayableUrl(url) {
  return /^https?:\/\//i.test(String(url || '').trim());
}

function _liveTvPlaybackCandidates(url, channel) {
  const seen = new Set();
  const out = [];

  const push = (candidateUrl) => {
    const raw = String(candidateUrl || '').trim();
    if (!_liveTvIsPlayableUrl(raw)) return;
    const key = _liveTvFailureKey(raw);
    if (!key || seen.has(key)) return;
    seen.add(key);
    out.push(raw);
  };

  push(url);

  if (channel && Array.isArray(channel.urls)) {
    for (const alt of channel.urls) push(alt);
  }

  const signatureName = String(channel?.name || '').trim().toLowerCase();
  const signatureCountry = String(_liveTvCountryCode(channel) || '').trim().toUpperCase();
  if (signatureName) {
    const matching = _liveTvChannels
      .filter((c) => String(c.name || '').trim().toLowerCase() === signatureName)
      .filter((c) => !signatureCountry || _liveTvCountryCode(c) === signatureCountry)
      .sort((a, b) => _liveTvReliabilityScore(b.url) - _liveTvReliabilityScore(a.url));
    for (const item of matching) push(item.url);
  }

  const ready = [];
  const cooling = [];
  for (const candidate of out) {
    if (_isLiveTvOnCooldown(candidate)) cooling.push(candidate);
    else ready.push(candidate);
  }
  return ready.concat(cooling).slice(0, 6);
}

async function _liveTvTryStartChannel(url, name, deadline, attemptId, requestController) {
  const qs = new URLSearchParams({ url: url, name: name || 'Live TV' });
  const timeout = setTimeout(() => requestController?.abort(), Math.max(1, deadline - Date.now()));
  let r;
  try {
    r = await fetch('/api/live-tv/play?' + qs, {
      signal: requestController?.signal,
      headers: _liveTvCurrentRequestId
        ? { 'X-RemotePlay-Request-Id': _liveTvCurrentRequestId }
        : {},
    });
  } finally {
    clearTimeout(timeout);
  }
  if (attemptId !== _liveTvPlaybackAttemptGeneration) return false;
  if (!r.ok) {
    const errText = await r.text().catch(() => 'Unable to play Live TV stream.');
    const err = new Error(errText || 'Unable to play Live TV stream.');
    err.status = r.status;
    throw err;
  }

  const waitResult = await _waitForLiveTvPlaybackStart(url, deadline, attemptId);
  if (waitResult.cancelled) return false;
  if (!waitResult.ok) {
    throw new Error(waitResult.error || 'The channel did not start playback.');
  }
  return true;
}

async function _waitForLiveTvPlaybackStart(expectedUrl, deadline, attemptId) {
  let lastErr = '';
  const startedAt = Date.now();
  let sawExpectedUrl = false;
  while (Date.now() < deadline) {
    if (attemptId !== _liveTvPlaybackAttemptGeneration) return { ok: false, cancelled: true, error: '' };
    if (typeof pollStatus !== 'function') {
      return { ok: false, error: 'Playback status reporting is unavailable.' };
    }

    let status;
    try {
      status = await pollStatus();
    } catch (err) {
      lastErr = `Unable to check playback status: ${err && err.message ? err.message : String(err)}`;
      await _liveTvDelay(Math.min(250, Math.max(0, deadline - Date.now())));
      continue;
    }
    if (attemptId !== _liveTvPlaybackAttemptGeneration) return { ok: false, cancelled: true, error: '' };

    const statusUrl = status?.filePath || status?.FilePath || status?.currentFilePath || status?.CurrentFilePath || '';
    if (!_liveTvPlaybackUrlsMatch(expectedUrl, statusUrl)) {
      if (sawExpectedUrl && status) {
        const runtimeErr = String(status.lastError || status.LastError || '').trim();
        if (runtimeErr) return { ok: false, error: runtimeErr };
        return { ok: false, cancelled: true, error: '' };
      }
      await _liveTvDelay(Math.min(250, Math.max(0, deadline - Date.now())));
      continue;
    }

    sawExpectedUrl = true;
    if (status?.isPlaying || status?.IsPlaying) return { ok: true, error: '' };

    const runtimeErr = String(status?.lastError || status?.LastError || '').trim();
    if (runtimeErr) return { ok: false, error: runtimeErr };

    await _liveTvDelay(Math.min(250, Math.max(0, deadline - Date.now())));
  }

  const elapsedSeconds = Math.max(1, Math.ceil((Date.now() - startedAt) / 1000));
  return {
    ok: false,
    error: lastErr || `No playback confirmation for ${_liveTvHostLabel(expectedUrl)} after ${elapsedSeconds} seconds.`,
  };
}

function _liveTvPlaybackUrlsMatch(first, second) {
  const left = _liveTvDecodeStatusUrl(first);
  const right = _liveTvDecodeStatusUrl(second);
  if (!left || !right) return false;
  try {
    const leftUrl = new URL(left);
    const rightUrl = new URL(right);
    leftUrl.hash = '';
    rightUrl.hash = '';
    return leftUrl.href === rightUrl.href;
  } catch {
    return left === right;
  }
}

function _liveTvDecodeStatusUrl(value) {
  const raw = String(value || '').trim();
  if (_liveTvIsPlayableUrl(raw)) return raw;
  try {
    const binary = atob(raw);
    const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
    const decoded = new TextDecoder('utf-8').decode(bytes).trim();
    return _liveTvIsPlayableUrl(decoded) ? decoded : raw;
  } catch {
    return raw;
  }
}

function _liveTvChannelCountryName(channel) {
  if (!channel) return '';
  if (channel.countryName) return channel.countryName;

  const code = channel.countryCode || channel.country || '';
  return _liveTvCountryName(code);
}

function _liveTvCountryName(countryCode) {
  const raw = String(countryCode || '').trim().toUpperCase();
  const code = raw === 'UK' ? 'GB' : raw;
  if (!code) return '';
  try {
    if (typeof Intl !== 'undefined' && typeof Intl.DisplayNames === 'function') {
      const dn = new Intl.DisplayNames(['en'], { type: 'region' });
      const name = dn.of(code);
      if (name) return name;
    }
  } catch {}
  return code;
}

function _liveTvSetCountText(text, canLoadMore = false) {
  const countText = document.getElementById('count-text');
  if (countText) {
    if (canLoadMore) {
      countText.innerHTML = _liveTvEsc(text) + ', <a href="#" class="count-load-more" onclick="event.preventDefault();liveTvLoadMore()">Load more</a>';
    } else {
      countText.textContent = text;
    }
  }

  const cbCount = document.getElementById('ltcb-count');
  if (cbCount) cbCount.textContent = text;
}

function _liveTvRenderBreadcrumb() {
  const bc = document.getElementById('breadcrumb');
  if (!bc) return;

  const root = '<button class="radio-breadcrumb-root breadcrumb-root-btn" type="button" onclick="liveTvResetFromBreadcrumb()" title="Reset all Live TV filters">⌂ Live TV Root</button>';
  const filters = [];

  if (_liveTvFilterCountry) filters.push('Country: ' + _liveTvEsc(_liveTvFilterCountry));
  if (_liveTvFilterCategory) filters.push('Category: ' + _liveTvEsc(_liveTvFilterCategory));
  if (_liveTvFilterLanguage) filters.push('Language: ' + _liveTvEsc(_liveTvFilterLanguage));
  if (_liveTvFilterQuery) filters.push('Search: ' + _liveTvEsc(_liveTvFilterQuery));

  if (!filters.length) {
    bc.innerHTML = root;
    return;
  }

  bc.innerHTML = `${root}<span> &rsaquo; </span><span>${filters.join(', ')}</span>`;
}

function liveTvResetFromBreadcrumb() {
  liveTvResetFilters();
}

function _liveTvSetLoadingState(text) {
  const browser = document.getElementById('live-tv-browser');
  if (!browser) return;

  browser.innerHTML = '<div style="padding:.85rem;color:var(--muted,#9aa8c2)">' + _liveTvEsc(text) + '</div>';
}

function _liveTvEmitTelemetry(eventName, data) {
  try {
    const detail = {
      domain: 'live-tv',
      event: String(eventName || ''),
      ts: Date.now(),
      ...(String(eventName || '').startsWith('play_') && _liveTvCurrentRequestId
        ? { requestId: _liveTvCurrentRequestId }
        : {}),
      ...(data && typeof data === 'object' ? data : {}),
    };
    document.dispatchEvent(new CustomEvent('rp:telemetry', { detail }));

    const timingEvents = new Set([
      'play_start',
      'play_attempt',
      'play_attempt_failed',
      'play_success',
      'play_failed',
    ]);
    const requestId = String(detail.requestId || '');
    if (
      detail.domain !== 'live-tv' ||
      !timingEvents.has(detail.event) ||
      !/^[A-Za-z0-9_-]{1,64}$/.test(requestId)
    ) return;

    const fields = ['requestId=' + requestId, 'event=' + detail.event];
    const allowedPhases = new Set([
      'playback_start',
      'player_handoff',
      'player_startup',
      'startup_confirmed',
      'playback_failed',
    ]);
    if (allowedPhases.has(detail.phase)) fields.push('phase=' + detail.phase);
    const elapsedMs = Number(detail.elapsedMs);
    if (Number.isFinite(elapsedMs) && elapsedMs >= 0)
      fields.push('elapsedMs=' + Math.round(elapsedMs));
    const attempt = Number(detail.attempt);
    if (Number.isInteger(attempt) && attempt > 0 && attempt <= 32)
      fields.push('attempt=' + attempt);
    const statusCode = Number(detail.statusCode);
    if (Number.isInteger(statusCode) && statusCode >= 100 && statusCode <= 599)
      fields.push('statusCode=' + statusCode);
    if (typeof detail.errorType === 'string' && /^[A-Za-z0-9_]{1,32}$/.test(detail.errorType))
      fields.push('errorType=' + detail.errorType);
    void fetch('/api/log', {
      method: 'POST',
      body: '[LIVE-TV-TIMING] ' + fields.join(' '),
      keepalive: true,
    }).catch(() => {});
  } catch {}
}

function _liveTvCloseQuickActionsModal() {
  const existing = document.getElementById('live-tv-quick-actions-modal');
  if (existing) existing.remove();
}

function _liveTvShowQuickActionsModal({ name, raw, friendly, requestedUrl, fallbackUrl }) {
  _liveTvCloseQuickActionsModal();

  const modal = document.createElement('div');
  modal.id = 'live-tv-quick-actions-modal';
  modal.className = 'live-tv-quick-modal';
  modal.innerHTML = ''
    + '<div class="live-tv-quick-modal-card" role="dialog" aria-modal="true" aria-label="Live TV playback error">'
    + '<div class="live-tv-quick-modal-title">Playback stopped</div>'
    + '<div class="live-tv-quick-modal-sub">' + _liveTvEsc(name || 'Live TV') + '</div>'
    + '<div class="live-tv-quick-modal-text">' + _liveTvEsc(friendly || 'Channel failed to start.') + '</div>'
    + '<div class="live-tv-quick-modal-detail">' + _liveTvEsc(raw || '') + '</div>'
    + '<div class="live-tv-quick-modal-actions">'
    + '<button type="button" id="live-tv-qa-fallback" class="btn btn-dim">Try fallback</button>'
    + '<button type="button" id="live-tv-qa-fav" class="btn btn-dim">Toggle favorite</button>'
    + '<button type="button" id="live-tv-qa-hide" class="btn btn-dim">Hide channel</button>'
    + '<button type="button" id="live-tv-qa-close" class="btn">Dismiss</button>'
    + '</div>'
    + '</div>';

  document.body.appendChild(modal);

  const fallbackBtn = modal.querySelector('#live-tv-qa-fallback');
  if (fallbackBtn) {
    if (!fallbackUrl || fallbackUrl === requestedUrl) fallbackBtn.setAttribute('disabled', 'disabled');
    fallbackBtn.addEventListener('click', () => {
      _liveTvCloseQuickActionsModal();
      if (fallbackUrl && fallbackUrl !== requestedUrl) liveTvPlayChannel(fallbackUrl, name || 'Live TV');
    });
  }

  const favBtn = modal.querySelector('#live-tv-qa-fav');
  if (favBtn) {
    favBtn.addEventListener('click', () => {
      liveTvToggleFavorite(requestedUrl);
      _liveTvCloseQuickActionsModal();
    });
  }

  const hideBtn = modal.querySelector('#live-tv-qa-hide');
  if (hideBtn) {
    hideBtn.addEventListener('click', () => {
      liveTvHideChannel(requestedUrl);
      _liveTvCloseQuickActionsModal();
    });
  }

  const closeBtn = modal.querySelector('#live-tv-qa-close');
  if (closeBtn) closeBtn.addEventListener('click', _liveTvCloseQuickActionsModal);
  modal.addEventListener('click', (e) => {
    if (e.target === modal) _liveTvCloseQuickActionsModal();
  });
}

function _liveTvHostLabel(url) {
  try {
    return new URL(url).host || '';
  } catch {
    return '';
  }
}

function _liveTvNewRequestId() {
  try {
    if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function')
      return crypto.randomUUID();
  } catch {}
  return `live-tv-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

function _liveTvCountryCode(channel) {
  const raw = String(channel?.countryCode || channel?.country || '').trim().toUpperCase();
  const code = raw === 'UK' ? 'GB' : raw;
  if (/^[a-z]{2}$/i.test(code)) return code.toUpperCase();
  return '';
}

function _liveTvFlagUrl(channel) {
  const code = _liveTvCountryCode(channel);
  if (!code) return '';
  return `https://flagcdn.com/w640/${code.toLowerCase()}.png`;
}

function _syncLiveTvCommandBar() {
  if (typeof _setViewModeScopeFromSegments === 'function') {
    _setViewModeScopeFromSegments('live-tv', [
      'Live TV Root',
      _liveTvShowFavoritesOnly ? 'Favorites' : 'All',
      _liveTvFilterCountry || 'All countries',
      _liveTvFilterCategory || 'All categories',
      _liveTvFilterLanguage || 'All languages',
      _liveTvFilterQuery ? 'Search: ' + _liveTvFilterQuery : 'All',
    ]);
  }
  const viewBtn = document.getElementById('ltcb-view-btn');
  const isList = _viewMode['live-tv'] === 'list';

  if (viewBtn) {
    viewBtn.textContent = isList ? '▦ Grid' : '☰ List';
    viewBtn.title = isList ? 'Switch to grid view' : 'Switch to list view';
  }

  const right = document.getElementById('ltcb-right');
  if (right) right.style.display = '';
}

function _liveTvBuildFilterList() {
  const toOption = (value, label) => `<option value="${_liveTvEsc(value)}">${_liveTvEsc(label)}</option>`;
  const countries = ['All countries', ..._liveTvCountries].map((name, i) => toOption(i === 0 ? '' : name, name)).join('');
  const categories = ['All Categories', ..._liveTvCategories].map((name, i) => toOption(i === 0 ? '' : name, name)).join('');
  const languages = ['All Languages', ..._liveTvLanguages].map((name, i) => toOption(i === 0 ? '' : name, name)).join('');
  const favoritesPillClass = _liveTvShowFavoritesOnly ? 'vcb-pill active' : 'vcb-pill';
  const hiddenPillClass = _liveTvShowHidden ? 'vcb-pill active' : 'vcb-pill';
  const failedPillClass = _liveTvHideFailed ? 'vcb-pill active' : 'vcb-pill';

  return '<input id="live-tv-search" type="search" placeholder="Search channels" oninput="liveTvOnFilterChange()" aria-label="Search Live TV channels" />'
    + '<button class="' + favoritesPillClass + '" onclick="liveTvToggleFavoritesFilter()" title="Show only favorite channels" aria-pressed="' + (_liveTvShowFavoritesOnly ? 'true' : 'false') + '">♥ Favorites</button>'
    + '<button class="' + hiddenPillClass + '" onclick="liveTvToggleShowHidden()" title="Show channels you previously hid" aria-pressed="' + (_liveTvShowHidden ? 'true' : 'false') + '">Show hidden</button>'
    + '<button class="' + failedPillClass + '" onclick="liveTvToggleHideFailedFilter()" title="Hide channels that failed to start" aria-pressed="' + (_liveTvHideFailed ? 'true' : 'false') + '">Hide failed</button>'
    + '<select id="live-tv-country" onchange="liveTvOnFilterChange()" aria-label="Filter by country" title="Country filter">' + countries + '</select>'
    + '<select id="live-tv-category" onchange="liveTvOnFilterChange()" aria-label="Filter by category" title="Category filter">' + categories + '</select>'
    + '<select id="live-tv-language" onchange="liveTvOnFilterChange()" aria-label="Filter by language" title="Language filter">' + languages + '</select>'
    + '<button class="ltcb-reset cb-reset-btn" onclick="liveTvResetFilters()" title="Reset filters">&#10005; Reset filters</button>';
}

function _liveTvBuildFilterChips() {
  const chips = [];
  const pushChip = (key, label, value) => {
    chips.push('<button class="vcb-pill" onclick="liveTvClearSingleFilter(\'' + key + '\')" title="Clear filter">' + _liveTvEsc(label + ': ' + value) + ' ×</button>');
  };

  if (_liveTvFilterQuery) pushChip('query', 'Search', _liveTvFilterQuery);
  if (_liveTvFilterCountry) pushChip('country', 'Country', _liveTvFilterCountry);
  if (_liveTvFilterCategory) pushChip('category', 'Category', _liveTvFilterCategory);
  if (_liveTvFilterLanguage) pushChip('language', 'Language', _liveTvFilterLanguage);
  if (_liveTvShowFavoritesOnly) chips.push('<button class="vcb-pill active" onclick="liveTvClearSingleFilter(\'favorites\')" title="Show all channels">Favorites only ×</button>');
  if (_liveTvShowHidden) chips.push('<button class="vcb-pill active" onclick="liveTvClearSingleFilter(\'showHidden\')" title="Hide previously hidden channels">Show hidden ×</button>');
  if (_liveTvHideFailed) chips.push('<button class="vcb-pill active" onclick="liveTvClearSingleFilter(\'hideFailed\')" title="Show failed channels">Hide failed ×</button>');

  if (!chips.length) return '';
  return '<div id="live-tv-active-filters" class="ltcb-active-filters" aria-label="Active filters">' + chips.join('') + '</div>';
}

function liveTvClearSingleFilter(key) {
  if (key === 'query') _liveTvFilterQuery = '';
  else if (key === 'country') _liveTvFilterCountry = '';
  else if (key === 'category') _liveTvFilterCategory = '';
  else if (key === 'language') _liveTvFilterLanguage = '';
  else if (key === 'favorites') _liveTvShowFavoritesOnly = false;
  else if (key === 'showHidden') _liveTvShowHidden = false;
  else if (key === 'hideFailed') {
    _liveTvHideFailed = false;
    _liveTvHideFailedExplicit = true;
  }

  _liveTvPage = 0;
  _liveTvSaveFilterState();
  _liveTvRenderFilters();
  renderLiveTvCards();
}

function _liveTvRefreshFilterMeta() {
  const countrySet = new Set();
  const categorySet = new Set();
  const languageSet = new Set();
  for (const ch of _liveTvChannels) {
    const countryName = ch._countryName || _liveTvChannelCountryName(ch);
    if (countryName) countrySet.add(countryName);
    if (ch.category) categorySet.add(ch.category);
    if (ch.language) languageSet.add(ch.language);
  }
  _liveTvCountries = Array.from(countrySet).sort((a, b) => a.localeCompare(b));
  _liveTvCategories = Array.from(categorySet).sort((a, b) => a.localeCompare(b));
  _liveTvLanguages = Array.from(languageSet).sort((a, b) => a.localeCompare(b));
}

function _liveTvRenderFilters() {
  const filters = document.getElementById('ltcb-filters');
  if (!filters) return;
  filters.innerHTML = _liveTvBuildFilterList();

  const q = document.getElementById('live-tv-search');
  const country = document.getElementById('live-tv-country');
  const category = document.getElementById('live-tv-category');
  const language = document.getElementById('live-tv-language');
  if (q) q.value = _liveTvFilterQuery;
  if (country) country.value = _liveTvFilterCountry;
  if (category) category.value = _liveTvFilterCategory;
  if (language) language.value = _liveTvFilterLanguage;
  _liveTvRenderBreadcrumb();
}

function liveTvOnFilterChange() {
  _liveTvFilterQuery = (document.getElementById('live-tv-search') || {}).value?.trim() || '';
  _liveTvFilterCountry = (document.getElementById('live-tv-country') || {}).value || '';
  _liveTvFilterCategory = (document.getElementById('live-tv-category') || {}).value || '';
  _liveTvFilterLanguage = (document.getElementById('live-tv-language') || {}).value || '';
  _liveTvPage = 0;
  _liveTvSaveFilterState();
  _liveTvRenderBreadcrumb();
  renderLiveTvCards();
}

function liveTvResetFilters() {
  _liveTvFilterQuery = '';
  _liveTvFilterCountry = '';
  _liveTvFilterCategory = '';
  _liveTvFilterLanguage = '';
  _liveTvShowFavoritesOnly = false;
  _liveTvHideFailed = false;
  _liveTvHideFailedExplicit = true;
  _liveTvShowHidden = false;
  _liveTvPage = 0;
  _liveTvSaveFilterState();
  _liveTvRenderFilters();
  _liveTvRenderBreadcrumb();
  renderLiveTvCards();
}

function _liveTvGetFilteredChannels() {
  const q = _liveTvFilterQuery.toLowerCase();
  return _liveTvChannels.filter((c) => {
    const countryName = c._countryName || _liveTvChannelCountryName(c);
    if (_isLiveTvHidden(c.url) && !_liveTvShowHidden) return false;
    if (_liveTvShowFavoritesOnly && !_isLiveTvFavorite(c.url)) return false;
    if (_liveTvHideFailed && _isLiveTvChannelFailed(c.url) && _liveTvCurrentUrl !== c.url) return false;
    if (_liveTvFilterCountry && countryName !== _liveTvFilterCountry) return false;
    if (_liveTvFilterCategory && c.category !== _liveTvFilterCategory) return false;
    if (_liveTvFilterLanguage && c.language !== _liveTvFilterLanguage) return false;
    if (!q) return true;

    return (c._searchText || '').includes(q);
  }).sort((a, b) => {
    const af = _isLiveTvFavorite(a.url) ? 1 : 0;
    const bf = _isLiveTvFavorite(b.url) ? 1 : 0;
    if (af !== bf) return bf - af;
    const ac = _isLiveTvOnCooldown(a.url) ? 1 : 0;
    const bc = _isLiveTvOnCooldown(b.url) ? 1 : 0;
    if (ac !== bc) return ac - bc;
    const as = _liveTvReliabilityScore(a.url);
    const bs = _liveTvReliabilityScore(b.url);
    if (as !== bs) return bs - as;
    return a.name.localeCompare(b.name);
  });
}

async function liveTvPlayChannel(url, name) {
  if (!url) return;

  if (_liveTvAttemptingUrl && _liveTvCurrentRequestId) {
    _liveTvEmitTelemetry('play_cancelled', {
      host: _liveTvHostLabel(_liveTvAttemptingUrl),
      reason: 'replaced',
      phase: 'cancelled',
    });
  }
  _liveTvPlayRequestController?.abort();
  _liveTvPlayRequestController = null;
  const attemptId = ++_liveTvPlaybackAttemptGeneration;
  const requestId = _liveTvNewRequestId();
  const playbackStartedAt = Date.now();
  _liveTvCurrentRequestId = requestId;
  _liveTvAttemptingUrl = url;
  if (typeof closeLocalPlayerForNonLocalPlayback === 'function') {
    try {
      await closeLocalPlayerForNonLocalPlayback();
    } catch (err) {
      if (attemptId !== _liveTvPlaybackAttemptGeneration) return;
      _liveTvAttemptingUrl = '';
      throw err;
    }
  }
  if (attemptId !== _liveTvPlaybackAttemptGeneration) return;

  if (typeof setStatus === 'function') setStatus('Starting Live TV: ' + (name || 'Unknown channel'));
  _liveTvLastRequestedUrl = url;
  const requestedChannel = _liveTvChannels.find((c) => c.url === url) || null;
  const candidates = _liveTvPlaybackCandidates(url, requestedChannel);
  const channel = requestedChannel || {
    name: name || 'Live TV',
    countryName: requestedChannel?.countryName || '',
    category: requestedChannel?.category || '',
    sourceHost: requestedChannel?.sourceHost || _liveTvHostLabel(url),
  };
  _liveTvCurrentChannel = channel || {
    name: name || 'Live TV',
    countryName: channel?.countryName || '',
    category: channel?.category || '',
    sourceHost: channel?.sourceHost || _liveTvHostLabel(url),
  };

  const playbackDeadline = Date.now() + _liveTvPlaybackStartBudgetMs;
  let lastError = '';
  let hadFailedAttempt = false;
  try {
    _liveTvEmitTelemetry('play_start', {
      requestId,
      phase: 'playback_start',
      requestedHost: _liveTvHostLabel(url),
      channelName: name || channel?.name || 'Live TV',
    });
    let playedUrl = '';
    for (const candidate of candidates.length ? candidates : [url]) {
      for (let attempt = 0; attempt < 2; attempt++) {
        if (Date.now() >= playbackDeadline) {
          lastError = lastError || `Live TV startup exceeded ${Math.ceil(_liveTvPlaybackStartBudgetMs / 1000)} seconds across stream sources.`;
          break;
        }

        try {
          _liveTvAttemptingUrl = candidate;
          _liveTvEmitTelemetry('play_attempt', {
            requestId,
            phase: 'player_handoff',
            candidateHost: _liveTvHostLabel(candidate),
            attempt: attempt + 1,
            elapsedMs: Date.now() - playbackStartedAt,
          });
          const candidateDeadline = Math.min(playbackDeadline, Date.now() + _liveTvPlaybackStartTimeoutMs);
          const requestController = typeof AbortController === 'function' ? new AbortController() : null;
          _liveTvPlayRequestController = requestController;
          let started;
          try {
            started = await _liveTvTryStartChannel(candidate, name, candidateDeadline, attemptId, requestController);
          } finally {
            if (_liveTvPlayRequestController === requestController) _liveTvPlayRequestController = null;
          }
          if (attemptId !== _liveTvPlaybackAttemptGeneration) return;
          if (!started) {
            liveTvCancelPendingPlaybackStart('server');
            return;
          }
          playedUrl = candidate;
          break;
        } catch (err) {
          if (attemptId !== _liveTvPlaybackAttemptGeneration) return;
          if (Number(err?.status) === 409) {
            liveTvCancelPendingPlaybackStart('server');
            return;
          }
          lastError = err && err.message ? err.message : String(err);
          hadFailedAttempt = true;
          _markLiveTvChannelFailed(candidate, lastError);
          const statusCode = Number(err && err.status);
          _liveTvEmitTelemetry('play_attempt_failed', {
            requestId,
            phase: 'player_startup',
            candidateHost: _liveTvHostLabel(candidate),
            attempt: attempt + 1,
            statusCode: Number.isInteger(statusCode) ? statusCode : undefined,
            errorType: err?.name || 'Error',
            elapsedMs: Date.now() - playbackStartedAt,
          });
          const permanentHttpFailure = Number.isInteger(statusCode)
            && statusCode >= 400
            && statusCode < 500
            && statusCode !== 408
            && statusCode !== 429;
          if (permanentHttpFailure) break;
          if (attempt === 0 && Date.now() < playbackDeadline) await _liveTvDelay(Math.min(350, playbackDeadline - Date.now()));
        }
      }
      if (playedUrl) break;
      if (Date.now() >= playbackDeadline) break;
    }

    if (!playedUrl) throw new Error(lastError || 'Unable to play Live TV stream.');

    const playingChannel = _liveTvChannels.find((c) => c.url === playedUrl) || channel;
    _liveTvCurrentChannel = {
      ...playingChannel,
      name: playingChannel?.name || name || 'Live TV',
      sourceHost: playingChannel?.sourceHost || _liveTvHostLabel(playedUrl),
    };

    _liveTvCurrentUrl = playedUrl;
    _liveTvAttemptingUrl = '';
    _liveTvLastPlayedUrl = playedUrl;
    _liveTvLastFallbackUsed = playedUrl !== url;
    _markLiveTvChannelRecovered(playedUrl);
    _liveTvSyncRoute(_liveTvTokenFromChannelName(name));
    _liveTvApplyNowPlayingInfo();
    _liveTvPushRecentChannel(playingChannel || _liveTvCurrentChannel, name, playedUrl);
    if (typeof setPlayerBarVisible === 'function') setPlayerBarVisible(true);
    if (hadFailedAttempt) {
      renderLiveTvCards();
    } else {
      document.querySelectorAll('.radio-station-card').forEach((el) => {
        el.classList.toggle('playing', el.dataset.url === playedUrl);
        if (el.dataset.url === playedUrl) el.classList.remove('live-tv-failed');
      });
    }

    if (typeof setStatus === 'function') setStatus('Playing Live TV: ' + (name || 'Unknown channel'));
    if (typeof startSse === 'function') startSse();
    if (typeof clearPlaybackErrorDedup === 'function') clearPlaybackErrorDedup('Live TV');
    liveTvClearStatusPlaybackError();
    _liveTvEmitTelemetry('play_success', {
      requestId,
      phase: 'startup_confirmed',
      requestedHost: _liveTvHostLabel(url),
      playedHost: _liveTvHostLabel(playedUrl),
      fallbackUsed: playedUrl !== url,
      channelName: _liveTvCurrentChannel?.name || name || 'Live TV',
      elapsedMs: Date.now() - playbackStartedAt,
    });
  } catch (err) {
    if (attemptId !== _liveTvPlaybackAttemptGeneration) return;
    _liveTvLastPlayedUrl = '';
    _liveTvLastFallbackUsed = false;
    const raw = err && err.message ? err.message : (lastError || String(err));
    const reason = lastError || raw;
    _markLiveTvChannelFailed(url, reason);
    _markLiveTvRelatedChannelsFailed(channel, name, reason);
    renderLiveTvCards();
    const statusCode = Number(err?.status);
    const friendly = _liveTvFriendlyFailureReason(raw, statusCode);
    if (typeof setStatus === 'function') {
      setStatus('Live TV playback failed: ' + (name || 'Channel') + ' — ' + friendly);
    }
    if (typeof showPlaybackErrorPopup === 'function') {
      showPlaybackErrorPopup('Live TV', `${name || 'Channel'} could not play.\n\n${friendly}\n\n${raw}`);
    }
    _liveTvEmitTelemetry('play_failed', {
      requestId,
      phase: 'playback_failed',
      requestedHost: _liveTvHostLabel(url),
      channelName: name || 'Live TV',
      failureCategory: _liveTvFailureCategory(raw, statusCode),
      statusCode: Number.isInteger(statusCode) ? statusCode : undefined,
      errorType: err?.name || 'Error',
      elapsedMs: Date.now() - playbackStartedAt,
    });
    const fallbackUrl = (candidates || []).find((candidateUrl) => candidateUrl !== url) || '';
    _liveTvShowQuickActionsModal({
      name: name || 'Live TV',
      raw,
      friendly,
      requestedUrl: url,
      fallbackUrl,
    });
  } finally {
    if (attemptId === _liveTvPlaybackAttemptGeneration) _liveTvAttemptingUrl = '';
  }
}

function liveTvLoadMore() {
  _liveTvPage += 1;
  _liveTvSyncRoute(_liveTvRouteToken || 'browse');
  renderLiveTvCards();
}

function liveTvRetryChannel(url, name, event) {
  if (event) {
    event.preventDefault();
    event.stopPropagation();
  }

  _clearLiveTvChannelFailureState(url);
  renderLiveTvCards();
  liveTvPlayChannel(url, name || 'Live TV');
}

function _liveTvStatusChips(channel) {
  const failed = _isLiveTvChannelFailed(channel.url);
  const reliability = _liveTvReliability(channel.url);
  const cooldownUntil = Number(reliability?.cooldownUntil || 0);
  const cooldownActive = cooldownUntil > Date.now();
  const recovered = !failed && !cooldownActive && Number(reliability?.failCount || 0) > 0 && Number(reliability?.successCount || 0) > 0;
  const chips = [];

  if (failed) chips.push('<span class="live-tv-status-chip failed" title="Marked failed">Failed</span>');
  if (cooldownActive) {
    const remainingMin = Math.max(1, Math.ceil((cooldownUntil - Date.now()) / 60000));
    chips.push('<span class="live-tv-status-chip cooldown" title="Temporarily deprioritized">Cooldown ' + remainingMin + 'm</span>');
  }
  if (recovered) chips.push('<span class="live-tv-status-chip recovered" title="Recovered after failures">Recovered</span>');

  if (!chips.length) return '';
  return '<div class="live-tv-status-chips" aria-label="Channel status">' + chips.join('') + '</div>';
}

function _liveTvBuildFailureAuditPanel() {
  if (!_liveTvFailureAudit.length) return '';

  const rows = _liveTvFailureAudit.slice(0, 8).map((entry) => {
    const when = new Date(Number(entry.ts || 0));
    const whenLabel = Number.isFinite(when.getTime()) ? when.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '—';
    const channel = _liveTvEsc(entry.channelName || 'Unknown channel');
    const reason = _liveTvEsc(entry.reason || 'Failure');
    return '<div class="live-tv-audit-row"><span class="live-tv-audit-time">' + whenLabel + '</span><span class="live-tv-audit-name">' + channel + '</span><span class="live-tv-audit-reason">' + reason + '</span></div>';
  }).join('');

  return '<details class="live-tv-failure-audit"><summary>Recent stream failures (' + _liveTvFailureAudit.length + ')</summary><div class="live-tv-audit-list">' + rows + '</div></details>';
}

function _liveTvBuildCard(channel, isList) {
  const encUrl = _liveTvEncodeInlineArgument(channel.url);
  const encName = _liveTvEncodeInlineArgument(channel.name);
  const metaParts = [];
  const countryName = _liveTvChannelCountryName(channel);
  if (countryName) metaParts.push(countryName);
  if (channel.category) metaParts.push(channel.category);
  if (channel.language) metaParts.push(channel.language);
  const metaText = metaParts.length ? metaParts.join(' • ') : 'Free channel';
  const host = channel.sourceHost || _liveTvHostLabel(channel.url);
  const playing = _liveTvCurrentUrl && _liveTvCurrentUrl === channel.url;
  const playingFallback = playing && _liveTvLastFallbackUsed;
  const failed = _isLiveTvChannelFailed(channel.url);
  const isFav = _isLiveTvFavorite(channel.url);
  const flagUrl = _liveTvFlagUrl(channel);
  const flagClass = flagUrl ? ' has-flag' : '';
  const flagStyle = flagUrl ? ' style="--lt-flag-url:url(\'' + _liveTvEsc(flagUrl) + '\')"' : '';
  const ariaLabel = _liveTvEsc(`${channel.name || 'Live TV'}${countryName ? `, ${countryName}` : ''}${channel.category ? `, ${channel.category}` : ''}`);
  const statusChips = _liveTvStatusChips(channel);
  const retryButton = failed
    ? '<button class="live-tv-mini-btn live-tv-mini-retry" onclick="liveTvRetryChannel(decodeURIComponent(\'' + encUrl + '\'),decodeURIComponent(\'' + encName + '\'),event)" title="Retry now">Retry now</button>'
    : '';
  const hiddenChannel = _isLiveTvHidden(channel.url);
  const hideButton = hiddenChannel
    ? '<button class="live-tv-mini-btn live-tv-mini-unhide" onclick="liveTvUnhideChannel(decodeURIComponent(\'' + encUrl + '\'),event)" title="Unhide channel" aria-label="Unhide channel">Unhide</button>'
    : '<button class="live-tv-mini-btn live-tv-mini-hide" onclick="liveTvHideChannel(decodeURIComponent(\'' + encUrl + '\'),event)" title="Hide channel" aria-label="Hide channel">🙈 Hide</button>';

  if (isList) {
    return '<div class="radio-station-card live-tv-row' + flagClass + (playing ? ' playing' : '') + (failed ? ' live-tv-failed' : '') + '" data-url="' + _liveTvEsc(channel.url) + '"' + flagStyle + ' role="button" tabindex="0" aria-label="' + ariaLabel + '" onclick="liveTvPlayChannel(decodeURIComponent(\'' + encUrl + '\'), decodeURIComponent(\'' + encName + '\'))" onkeydown="activateKeyboardClick(event,this)">'
      + '<div class="sc-icon">📺</div>'
      + '<div class="station-name sc-name">' + (failed ? '<span class="live-tv-failed-skull" title="Channel marked as not working" aria-label="Channel marked as not working">☠</span>' : '') + _liveTvEsc(channel.name) + '</div>'
      + '<div class="station-meta sc-quality">' + _liveTvEsc(host || 'stream') + (playingFallback ? ' <span class="live-tv-source-note">fallback</span>' : '') + '</div>'
      + '<div class="station-meta sc-country">' + _liveTvEsc(countryName || '—') + '</div>'
      + '<div class="station-meta sc-genre">' + _liveTvEsc(metaText) + statusChips + '</div>'
      + '<div class="sc-fav"><button class="radio-fav-btn live-tv-fav-btn' + (isFav ? ' active' : '') + '" onclick="liveTvToggleFavorite(decodeURIComponent(\'' + encUrl + '\'),event)" title="Toggle favorite" aria-label="Toggle favorite" aria-pressed="' + (isFav ? 'true' : 'false') + '">' + (isFav ? '♥' : '♡') + '</button>' + hideButton + retryButton + '</div>'
      + '</div>';
  }

  return '<div class="radio-station-card live-tv-grid-card' + flagClass + (playing ? ' playing' : '') + (failed ? ' live-tv-failed' : '') + '" data-url="' + _liveTvEsc(channel.url) + '"' + flagStyle + ' role="button" tabindex="0" aria-label="' + ariaLabel + '" onclick="liveTvPlayChannel(decodeURIComponent(\'' + encUrl + '\'), decodeURIComponent(\'' + encName + '\'))" onkeydown="activateKeyboardClick(event,this)">'
    + '<div class="station-name">' + (failed ? '<span class="live-tv-failed-skull" title="Channel marked as not working" aria-label="Channel marked as not working">☠</span>' : '') + _liveTvEsc(channel.name) + '</div>'
    + '<div class="station-meta">' + _liveTvEsc((countryName || 'Unknown country') + (channel.language ? (' • ' + channel.language) : '')) + '</div>'
    + '<div class="station-meta">' + _liveTvEsc(channel.category || 'General') + '</div>'
    + statusChips
    + '<div class="station-meta">Source: ' + _liveTvEsc(host || 'stream') + (playingFallback ? ' <span class="live-tv-source-note">fallback</span>' : '') + '</div>'
    + '<div class="station-meta"><button class="live-tv-fav-btn' + (isFav ? ' active' : '') + '" onclick="liveTvToggleFavorite(decodeURIComponent(\'' + encUrl + '\'),event)" title="Toggle favorite" aria-label="Toggle favorite" aria-pressed="' + (isFav ? 'true' : 'false') + '">' + (isFav ? '♥ Favorite' : '♡ Favorite') + '</button>' + hideButton + retryButton + '</div>'
    + '</div>';
}

function renderLiveTvCards() {
  const browser = document.getElementById('live-tv-browser');
  if (!browser) return;

  _syncLiveTvCommandBar();
  if (!_liveTvCurrentUrl) _liveTvSyncRoute(_liveTvRouteToken || 'browse');
  browser.innerHTML = '';

  if (!_liveTvChannels.length) {
    _liveTvRenderBreadcrumb();
    _liveTvSetCountText('0 free channels');
    _liveTvSetLoadingState('No free live TV channels are currently available.');
    return;
  }

  const filtered = _liveTvGetFilteredChannels();
  const hasActiveFilters = Boolean(
    _liveTvFilterQuery || _liveTvFilterCountry || _liveTvFilterCategory || _liveTvFilterLanguage || _liveTvShowFavoritesOnly || _liveTvShowHidden
  );
  const visibleCount = hasActiveFilters
    ? filtered.length
    : Math.min(filtered.length, (_liveTvPage + 1) * _liveTvPageSize);
  const visible = filtered.slice(0, visibleCount);
  const isList = _viewMode['live-tv'] === 'list';

  let html = '<div class="radio-inner-cards' + (isList ? ' list-view' : '') + '">';
  if (isList) {
    html += '<div class="radio-list-header live-tv-list-header"><span class="sc-icon"></span><span class="sc-sort-btn active">Channel</span><span class="sc-quality">Source</span><span class="sc-country">Country</span><span class="sc-genre">Genre / Tags</span><span class="sc-fav"></span></div>';
  }

  for (const channel of visible) html += _liveTvBuildCard(channel, isList);
  html += '</div>';

  if (!hasActiveFilters && visibleCount < filtered.length) {
    html += '<div style="text-align:center;padding:.6rem 0"><button class="btn btn-dim" onclick="liveTvLoadMore()" style="min-width:120px">Load more…</button></div>';
  }

  html += _liveTvBuildFailureAuditPanel();

  browser.innerHTML = html;
  _liveTvProbeVisibleChannels(visible);

  _liveTvSetCountText(
    visibleCount < filtered.length
      ? `${visibleCount} of ${filtered.length} free channels`
      : `${filtered.length} free channels`,
    visibleCount < filtered.length
  );
  _liveTvRenderBreadcrumb();
}

async function liveTvLoadChannels() {
  const cached = _loadLiveTvCatalogCache();
  const hasCachedChannels = Boolean(cached && Array.isArray(cached.channels) && cached.channels.length);
  const initialChannels = hasCachedChannels ? cached.channels : _liveTvFallbackChannels.slice();
  _liveTvApplyChannels(initialChannels);
  _liveTvSetIndexingProgress(
    hasCachedChannels
      ? 'Refreshing Live TV catalog; showing cached channels…'
      : 'Loading Live TV catalog; showing fallback channels…'
  );
  _liveTvEmitTelemetry('catalog_load_start', { source: 'network' });

  try {
    const res = await fetch('/api/live-tv/catalog');
    if (!res.ok) throw new Error(`Live TV catalog request failed (${res.status}).`);

    const payload = await res.json();
    if (payload && payload.ok === false) {
      throw new Error(payload.error || 'Live TV catalog request failed.');
    }
    let channels = Array.isArray(payload?.channels)
      ? payload.channels.map(_liveTvNormalizeChannel).filter(Boolean)
      : [];
    if (!channels.some((c) => c.category || c.language)) {
      const currentChannels = _liveTvChannels;
      await liveTvReindexNow(true);
      channels = _liveTvChannels !== currentChannels ? _liveTvChannels : initialChannels;
    }
    if (!channels.length) throw new Error('Live TV catalog returned no channels.');

    _liveTvApplyChannels(channels);
    _saveLiveTvCatalogCache(channels);
    if (typeof setStatus === 'function') setStatus(`Live TV loaded: ${channels.length} channels.`);
    _liveTvEmitTelemetry('catalog_load_success', { source: 'network', count: channels.length });

    _liveTvSetIndexingProgress('Live TV channels loaded.');
    setTimeout(_liveTvClearIndexingProgress, 1200);
  } catch (err) {
    const source = hasCachedChannels ? 'cached channels' : 'fallback channels';
    console.warn('[LIVE-TV] catalog refresh failed; keeping the initial channel list.', err);
    _liveTvSetIndexingProgress(`Live TV catalog unavailable — showing ${source}.`);
    if (typeof setStatus === 'function') setStatus(`Live TV catalog refresh failed; showing ${source}.`);
    _liveTvEmitTelemetry('catalog_load_failed', { source: hasCachedChannels ? 'cache' : 'fallback' });
    setTimeout(_liveTvClearIndexingProgress, 2400);
  }
}

async function liveTvReindexNow(background = false) {
  if (_liveTvRefreshInFlight) return _liveTvRefreshInFlight;

  _liveTvRefreshInFlight = (async () => {
    if (!background) _liveTvSetLoadingState('Reindexing Live TV channels…');
    _liveTvSetIndexingProgress(background ? 'Refreshing Live TV index in background…' : 'Refreshing Live TV index…');

    try {
      const res = await fetch('/api/live-tv/reindex', { method: 'POST' });
      if (!res.ok) throw new Error('Live TV reindex request failed');

      const payload = await res.json();
      if (payload && payload.ok === false) {
        throw new Error(payload.error || 'Live TV reindex failed.');
      }
      let channels = Array.isArray(payload?.channels)
        ? payload.channels.map(_liveTvNormalizeChannel).filter(Boolean)
        : [];
      if (!channels.length) throw new Error('Live TV reindex returned no channels.');

      _liveTvApplyChannels(channels);
      _saveLiveTvCatalogCache(channels);
      _liveTvSetIndexingProgress(background ? 'Live TV index refreshed.' : 'Live TV channels loaded.');
      setTimeout(_liveTvClearIndexingProgress, 1600);
      if (typeof setStatus === 'function' && !background) setStatus('Live TV channels reindexed.');
    } catch (err) {
      console.warn('[LIVE-TV] reindex failed.', err);
      if (!_liveTvChannels.length) {
        const cached = _loadLiveTvCatalogCache();
        _liveTvApplyChannels(
          cached && Array.isArray(cached.channels) && cached.channels.length
            ? cached.channels
            : _liveTvFallbackChannels.slice()
        );
      }
      _liveTvSetIndexingProgress('Live TV reindex failed.');
      setTimeout(_liveTvClearIndexingProgress, 2400);
      if (typeof setStatus === 'function') setStatus('Live TV reindex failed. Using available/fallback channels.');
    } finally {
      _liveTvRefreshInFlight = null;
    }
  })();

  return _liveTvRefreshInFlight;
}

function liveTvInit() {
  _syncLiveTvCommandBar();
  _loadFailedLiveTvChannels();
  _loadLiveTvFailureAudit();
  _loadLiveTvReliability();
  _loadLiveTvFavorites();
  _loadLiveTvHidden();
  _liveTvLoadFilterState();
  renderLiveTvRecent();
  _liveTvRenderFilters();
  _liveTvSyncRoute(_liveTvRouteToken || 'browse');

  if (_liveTvInited) {
    renderLiveTvCards();
    return;
  }

  _liveTvInited = true;
  liveTvLoadChannels();
}
