function updateDiagnosticsIndicator(state) {
  const dot = document.getElementById('diag-dot');
  if (!dot) return;
  dot.classList.toggle('ok', state === 'ok');
  dot.classList.toggle('error', state === 'error');
  dot.title =
    state === 'ok'
      ? 'Server connected'
      : state === 'error'
        ? 'Server or playback issue'
        : 'Checking server';
}

async function refreshLibraryStatus() {
  if (currentMode !== 'video') return;
  try {
    const res = await fetch('/api/library-status');
    if (!res.ok) return;
    updateLibraryStatus(await res.json());
  } catch (e) {}
}

let _libStatusPollTimer = null;
let _libStatusWasScanning = false;

function updateLibraryStatus(scan) {
  if (currentMode !== 'video') return;
  const badge = document.getElementById('indexing-badge');
  const el = document.getElementById('scan-status'); // kept for error fallback
  if (!scan) return;
  const indexed = Number(scan.indexedFiles ?? scan.IndexedFiles) || 0;
  const movies = Number(scan.indexedMovies ?? scan.IndexedMovies ?? indexed) || 0;
  const links = Number(scan.indexedLinks ?? scan.IndexedLinks) || 0;
  const isScanning = Boolean(scan.isScanning ?? scan.IsScanning);
  const error = (scan.lastError ?? scan.LastError ?? '').trim();
  if (badge) badge.style.display = isScanning ? '' : 'none';
  if (error) {
    if (el) { el.style.display = ''; el.textContent = 'Library scan failed: ' + error; }
    _stopLibStatusPoll();
    return;
  }
  if (el) el.style.display = 'none';
  if (isScanning) {
    _libStatusWasScanning = true;
    _startLibStatusPoll();
    return;
  }
  // Scan just finished — refresh the current folder so cards appear
  if (_libStatusWasScanning) {
    _libStatusWasScanning = false;
    if (typeof browse !== 'undefined' && typeof currentDir !== 'undefined')
      browse(currentDir, 0, false, false);
  }
  _stopLibStatusPoll();
  // Update count-text with library ready info
  const countText = document.getElementById('count-text');
  if (countText && !countText.textContent) {
    const linkPart = links > 0 ? ' \u2014 ' + links + ' link(s)' : '';
    countText.textContent = 'Library ready: ' + movies + ' movie(s)' + linkPart;
  }
}

function _startLibStatusPoll() {
  if (_libStatusPollTimer) return;
  _libStatusPollTimer = setInterval(async () => {
    if (currentMode !== 'video') { _stopLibStatusPoll(); return; }
    try {
      const res = await fetch('/api/library-status');
      if (!res.ok) return;
      updateLibraryStatus(await res.json());
    } catch (e) {}
  }, 2000);
}

function _stopLibStatusPoll() {
  if (_libStatusPollTimer) {
    clearInterval(_libStatusPollTimer);
    _libStatusPollTimer = null;
  }
}

async function refreshThumbnailStatus() {
  try {
    const res = await fetch('/api/thumbnails/status');
    if (!res.ok) return;
    updateThumbnailStatus(await res.json());
  } catch (e) {}
}
function updateThumbnailStatus(status) {
  const el = document.getElementById('thumb-status');
  if (!el || !status) return;
  const running = Boolean(status.isRunning ?? status.IsRunning);
  const total = Number(status.total ?? status.Total) || 0;
  const processed = Number(status.processed ?? status.Processed) || 0;
  const generated = Number(status.generated ?? status.Generated) || 0;
  const cached = Number(status.cached ?? status.Cached) || 0;
  const current = (status.currentTitle ?? status.CurrentTitle ?? '').trim();
  const error = (status.lastError ?? status.LastError ?? '').trim();
  el.classList.toggle('scanning', running);
  el.classList.toggle('error', Boolean(error && !running));
  if (running) {
    el.innerHTML =
      'Generating thumbnails... ' +
      processed +
      ' / ' +
      total +
      ' (' +
      generated +
      ' new, ' +
      cached +
      ' cached) ' +
      esc(current) +
      ' <button type="button" onclick="cancelThumbnailQueue()">Cancel</button>';
    return;
  }
  if (error) {
    el.textContent = error;
    return;
  }
  el.textContent =
    total > 0 ? 'Thumbnails ready: ' + processed + ' checked, ' + generated + ' generated.' : '';
}
async function startThumbnailQueue() {
  haptic(10);
  const res = await fetch('/api/thumbnails/start');
  if (res.ok) updateThumbnailStatus(await res.json());
}
async function cancelThumbnailQueue() {
  const res = await fetch('/api/thumbnails/cancel');
  if (res.ok) updateThumbnailStatus(await res.json());
}

function setConnectionStatus(message, isError, isConnected) {
  const el = document.getElementById('connection-status');
  if (!el) return;
  el.textContent = message;
  el.classList.toggle('error', Boolean(isError));
  el.classList.toggle('connected', Boolean(isConnected));
}

function openDiagnostics() {
  document.getElementById('diagnostics-overlay').classList.add('open');
  refreshDiagnostics();
}
function closeDiagnostics() {
  document.getElementById('diagnostics-overlay').classList.remove('open');
}

function switchDiagTab(btn, tabName) {
  document.querySelectorAll('.diag-tab').forEach((t) => t.classList.remove('active'));
  document.querySelectorAll('.diag-pane').forEach((p) => p.classList.remove('active'));
  btn.classList.add('active');
  const pane = document.getElementById('diag-pane-' + tabName);
  if (pane) pane.classList.add('active');
}

function diagRow(label, value) {
  return '<dt>' + esc(label) + '</dt><dd>' + esc(String(value ?? 'N/A')) + '</dd>';
}
function diagRowHtml(label, html) {
  return '<dt>' + esc(label) + '</dt><dd>' + html + '</dd>';
}

function renderTrackCard(fields) {
  return (
    '<div class="diag-track-card">' +
    fields
      .map(([k, v]) => {
        if (v === undefined || v === null || v === '') return '';
        return (
          '<div class="diag-track-row"><span class="diag-track-key">' +
          esc(k) +
          '</span><span class="diag-track-val">' +
          esc(String(v)) +
          '</span></div>'
        );
      })
      .filter(Boolean)
      .join('') +
    '</div>'
  );
}

async function refreshDiagnostics() {
  const content = document.getElementById('diagnostics-content');
  if (!content) return;
  content.innerHTML = '<dt>Status</dt><dd>Loading...</dd>';
  ['video', 'audio', 'subtitles'].forEach((t) => {
    const el = document.getElementById('diag-' + t + '-content');
    if (el) el.innerHTML = '<p class="diag-muted">Loading...</p>';
  });
  try {
    const [statusResponse, healthResponse, displayResponse, libraryResponse] = await Promise.all([
      fetch('/api/status'),
      fetch('/api/health'),
      fetch('/api/display-diagnostics'),
      fetch('/api/library-status'),
    ]);
    const status = await statusResponse.json();
    const health = await healthResponse.json();
    const display = await displayResponse.json();
    const library = await libraryResponse.json();
    const ci = display.codecInfo;

    // ── Overview tab ────────────────────────────────────────────
    const rows = [
      ['Playback', status.isPlaying ? 'Playing' : status.queueCount > 0 ? 'Queued' : 'Idle'],
      ['Title', (status.title || '').replace(/^\s*[▶⏸]\s*/, '') || 'N/A'],
      ['Queue', String(status.queueCount || 0) + ' item(s)'],
      [
        'Library',
        library.isScanning
          ? 'Scanning ' + (library.scannedFiles || 0) + ' video(s)'
          : (library.indexedMovies || library.indexedFiles || 0) +
            ' movie(s)' +
            (library.indexedLinks > 0 ? ' \u2014 ' + library.indexedLinks + ' link(s)' : ''),
      ],
      ['Server', String(health.activeScheme || '').toUpperCase() + ' port ' + health.port],
      ['Display', display.targetDisplayName || 'N/A'],
      ['Fullscreen repair', String(Boolean(display.needsFullscreenRepair))],
      ['Zoom', Math.round((Number(status.zoom) || 1) * 100) + '%'],
      ['Brightness', Math.round((Number(status.brightness) || 0) * 100) + '%'],
      ['Last error', status.lastError || health.startupWarning || library.lastError || 'None'],
    ];
    if (ci) {
      rows.push(['─── Media ───', '']);
      rows.push(['File', ci.fileName || 'N/A']);
      rows.push(['Container', ci.containerFormat || 'N/A']);
      rows.push(['Video tracks', String(ci.videoTracks?.length || 0)]);
      rows.push(['Audio tracks', String(ci.audioTracks?.length || 0)]);
      rows.push(['Subtitles', String(ci.subtitleTracks?.length || 0)]);
      if (ci.videoTracks?.length) {
        const vt = ci.videoTracks[0];
        rows.push([
          'Video codec',
          vt.codecDescription +
            (vt.codec && vt.codec !== vt.codecDescription ? ' (' + vt.codec + ')' : ''),
        ]);
        rows.push(['Resolution', vt.width + '×' + vt.height]);
        rows.push(['Frame rate', vt.frameRate]);
      }
      if (ci.audioTracks?.length) {
        const at = ci.audioTracks[0];
        rows.push([
          'Audio codec',
          at.codecDescription +
            (at.codec && at.codec !== at.codecDescription ? ' (' + at.codec + ')' : ''),
        ]);
        rows.push(['Channels', at.channelLayout]);
        rows.push(['Sample rate', at.sampleRate + ' Hz']);
      }
    }
    content.innerHTML = rows
      .map((row) => {
        if (row[0].startsWith('─')) {
          return '<dt class="diag-section-label">' + esc(row[0]) + '</dt><dd></dd>';
        }
        return diagRow(row[0], row[1]);
      })
      .join('');

    // ── Video tab
    const videoEl = document.getElementById('diag-video-content');
    if (videoEl) {
      if (!ci || !ci.videoTracks?.length) {
        videoEl.innerHTML = '<p class="diag-muted">No video tracks — start a video first.</p>';
      } else {
        videoEl.innerHTML = ci.videoTracks
          .map((t, i) => {
            const title =
              '<div class="diag-track-title">&#128250; Video track ' +
              (i + 1) +
              (t.description ? ' · ' + t.description : '') +
              '</div>';
            return (
              title +
              renderTrackCard([
                [
                  'Codec',
                  t.codecDescription +
                    (t.codec && t.codec !== t.codecDescription ? ' (' + t.codec + ')' : ''),
                ],
                ['Resolution', t.width + '×' + t.height],
                ['Frame rate', t.frameRate],
                ['Aspect ratio (SAR)', t.aspectRatio],
                ['Orientation', t.orientation],
                ['Language', t.language || ''],
              ])
            );
          })
          .join('');
      }
    }

    // ── Audio tab ───────────────────────────────────────────────
    const audioEl = document.getElementById('diag-audio-content');
    if (audioEl) {
      const isPlaying = Boolean(status.isPlaying);
      const swActive = Boolean(display.forceSwAudio);
      let audioFixHtml = '';
      if (isPlaying) {
        if (swActive) {
          audioFixHtml =
            '<div class="diag-audio-fix diag-audio-fix--active">&#10003; Software audio decode is active for this file.</div>';
        } else {
          audioFixHtml =
            '<div class="diag-audio-fix"><button class="btn btn-dim" id="btn-fix-audio">&#128267; Fix silent / broken audio</button><span class="diag-muted"> &nbsp;Forces software decode &amp; restarts playback. Saved for this file.</span></div>';
        }
      }
      if (!ci || !ci.audioTracks?.length) {
        audioEl.innerHTML =
          audioFixHtml + '<p class="diag-muted">No audio tracks — start a video first.</p>';
      } else {
        audioEl.innerHTML =
          audioFixHtml +
          ci.audioTracks
            .map((t, i) => {
              const label = t.description || t.language || 'Track ' + (i + 1);
              const title =
                '<div class="diag-track-title">&#127925; Audio track ' +
                (i + 1) +
                ' · ' +
                esc(label) +
                '</div>';
              return (
                title +
                renderTrackCard([
                  [
                    'Codec',
                    t.codecDescription +
                      (t.codec && t.codec !== t.codecDescription ? ' (' + t.codec + ')' : ''),
                  ],
                  ['Channels', t.channelLayout],
                  ['Sample rate', t.sampleRate ? ' ' + t.sampleRate + ' Hz' : ''],
                  ['Language', t.language || ''],
                  ['Description', t.description || ''],
                ])
              );
            })
            .join('');
      }
      const btnFix = audioEl.querySelector('#btn-fix-audio');
      if (btnFix)
        btnFix.addEventListener('click', async () => {
          btnFix.disabled = true;
          btnFix.textContent = 'Applying...';
          try {
            await api('/api/fix-audio');
          } catch (e) {
            btnFix.textContent = 'Error — try again';
          }
          setTimeout(() => refreshDiagnostics(), 1500);
        });
    }

    // ── Subtitles tab
    const subsEl = document.getElementById('diag-subtitles-content');
    if (subsEl) {
      if (!ci || !ci.subtitleTracks?.length) {
        subsEl.innerHTML = '<p class="diag-muted">No subtitle tracks in this file.</p>';
      } else {
        subsEl.innerHTML = ci.subtitleTracks
          .map((t, i) => {
            const label = t.description || t.language || 'Track ' + (i + 1);
            const title =
              '<div class="diag-track-title">&#128221; Subtitle track ' +
              (i + 1) +
              ' · ' +
              esc(label) +
              '</div>';
            return (
              title +
              renderTrackCard([
                [
                  'Codec',
                  t.codecDescription +
                    (t.codec && t.codec !== t.codecDescription ? ' (' + t.codec + ')' : ''),
                ],
                ['Language', t.language || ''],
                ['Encoding', t.encoding || ''],
                ['Description', t.description || ''],
              ])
            );
          })
          .join('');
      }
    }
  } catch (e) {
    console.error('[diag] refreshDiagnostics error:', e);
    if (content) content.innerHTML = '<dt>Error</dt><dd>' + esc(String(e)) + '</dd>';
  }
}
