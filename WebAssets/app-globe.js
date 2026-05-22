// ---------------------------------------------------------------------------
// ─── Radio Globe — ray-cast 3D sphere renderer ────────────────────────────
// Land polygons + country border arcs decoded once from /world-50m.json.
// Two equirectangular textures are baked (land fill, border lines) using the
// browser's own Canvas 2D engine for correct winding/concavity handling.
// Each frame ray-casts with a zoom factor that animates from 1→4 so the
// globe starts as a whole-world view then zooms into the station's region.
// -------------------------------------------------------------------------

let _topoLandPolys = null; // [[lon,lat],...] rings for land fill
let _topoCountryLines = null; // [[lon,lat],...] polylines for country borders
let _usStateLines = null; // [[lon,lat],...] polylines for US state borders
let _topoFetching = false;
let _usStateFetching = false;

async function ensureTopoLand() {
  if (_topoLandPolys) return _topoLandPolys;
  if (_topoFetching) return null;
  _topoFetching = true;
  try {
    const [topoData] = await Promise.all([
      fetch('/world-50m.json').then((r) => {
        if (!r.ok) throw new Error('fetch failed');
        return r.json();
      }),
      ensureUsStates(),
    ]);
    const topo = topoData;
    _topoLandPolys = decodeTopoLand(topo);
    _topoCountryLines = decodeCountryLines(topo);
    _globeTextureDirty = true;
  } catch (e) {
    console.warn('Globe TopoJSON load failed', e);
    _topoLandPolys = [];
    _topoCountryLines = [];
    _topoFetching = false;
  }
  return _topoLandPolys;
}

// Decode GeoJSON FeatureCollection of US state polygons into polylines for border rendering.
async function ensureUsStates() {
  if (_usStateLines) return;
  if (_usStateFetching) return;
  _usStateFetching = true;
  try {
    const r = await fetch('/us-states.json');
    if (!r.ok) throw new Error('us-states fetch failed');
    const geojson = await r.json();
    const lines = [];
    for (const feature of geojson.features || []) {
      const geom = feature.geometry;
      if (!geom) continue;
      const polys =
        geom.type === 'Polygon'
          ? [geom.coordinates]
          : geom.type === 'MultiPolygon'
            ? geom.coordinates
            : [];
      for (const poly of polys) {
        for (const ring of poly) {
          if (ring.length >= 2) lines.push(ring); // ring: [[lon,lat],...]
        }
      }
    }
    _usStateLines = lines;
    _globeTextureDirty = true;
  } catch (e) {
    console.warn('US states load failed', e);
    _usStateLines = [];
  }
}

function decodeTopoLand(topo) {
  const { scale, translate } = topo.transform;
  const arcs = topo.arcs.map((arc) => {
    let x = 0,
      y = 0;
    return arc.map((pt) => {
      x += pt[0];
      y += pt[1];
      // Keep raw floating-point coords; normalisation happens in splitRingAtAntimeridian
      return [x * scale[0] + translate[0], y * scale[1] + translate[1]];
    });
  });
  function getArc(i) {
    return i >= 0 ? arcs[i] : arcs[~i].slice().reverse();
  }
  function decodeRing(indices) {
    const ring = [];
    for (const i of indices) {
      const pts = getArc(i);
      for (let j = 0; j < pts.length - 1; j++) ring.push(pts[j]);
    }
    return ring;
  }
  const polys = [];
  function processGeom(geom) {
    if (!geom) return;
    if (geom.type === 'Polygon') {
      for (const ring of geom.arcs) polys.push(decodeRing(ring));
    } else if (geom.type === 'MultiPolygon') {
      for (const poly of geom.arcs) for (const ring of poly) polys.push(decodeRing(ring));
    } else if (geom.type === 'GeometryCollection') {
      for (const g of geom.geometries) processGeom(g);
    }
  }
  processGeom(topo.objects.land);
  return polys;
}

// Decode every raw arc from the TopoJSON as a polyline — these are the
// segments that make up country borders (shared edges between countries).
function decodeCountryLines(topo) {
  const { scale, translate } = topo.transform;
  const lines = [];
  for (const arc of topo.arcs) {
    let x = 0,
      y = 0;
    const line = arc.map((pt) => {
      x += pt[0];
      y += pt[1];
      return [x * scale[0] + translate[0], y * scale[1] + translate[1]];
    });
    if (line.length >= 2) lines.push(line);
  }
  return lines;
}

// ── Equirectangular textures ──────────────────────────────────────────────
const TEX_W = 2048,
  TEX_H = 1024;
let _globeTex = null; // Uint8Array: 1=land
let _globeBorderTex = null; // Uint8Array: 1=country border pixel
let _globeStateTex = null; // Uint8Array: 1=US state border pixel
let _globeTextureDirty = true;

/**
 * Normalise longitude to [-180, 180].
 */
function normLon(lon) {
  return ((((lon + 180) % 360) + 360) % 360) - 180;
}

/**
 * Split a ring of [lon,lat] pairs into sub-rings at the antimeridian.
 *
 * Handles two cases:
 *  a) A decoded TopoJSON ring whose coordinates have drifted outside ±180
 *     (e.g. Russia goes up to lon≈190) — normalise first.
 *  b) A single step that jumps more than 180° (legacy seam crossing).
 *
 * Each output sub-ring is clean: all points inside [-180,180] with no
 * edge that crosses the seam.
 */
function splitRingAtAntimeridian(ring) {
  if (ring.length < 2) return [ring];

  // 1. Normalise all longitudes to [-180,180]
  const norm = ring.map(([lon, lat]) => [normLon(lon), lat]);

  // 2. Walk the normalised ring; wherever a segment crosses the antimeridian
  //    (detected by the sign-of-largest-magnitude heuristic), split it.
  const subRings = [];
  let cur = [norm[0]];

  for (let i = 1; i < norm.length; i++) {
    const [lon0, lat0] = norm[i - 1];
    const [lon1, lat1] = norm[i];
    const dLon = lon1 - lon0;

    if (Math.abs(dLon) > 180) {
      // Interpolate the latitude at the antimeridian crossing
      const sign = dLon > 0 ? -1 : 1;
      const lonA = sign * 180;
      const lonB = -sign * 180;
      const t = Math.abs((lonA - lon0) / dLon);
      const latX = lat0 + (lat1 - lat0) * t;
      cur.push([lonA, latX]);
      subRings.push(cur);
      cur = [
        [lonB, latX],
        [lon1, lat1],
      ];
    } else {
      cur.push([lon1, lat1]);
    }
  }
  if (cur.length > 1) subRings.push(cur);
  return subRings.length ? subRings : [norm];
}

// Draw rings as filled paths with evenodd winding → land texture.
//
// Root cause of the "diagonal artifact through China" that plagued the old version:
//   splitRingAtAntimeridian split Russia's polygon into two sub-rings, then
//   closePath() drew a straight line in equirectangular (lon/lat) space from
//   the antimeridian cut-edge back to the ring's original start point in western
//   Russia.  That closing line slashed diagonally across Central Asia/China in the
//   texture, and fill(evenodd) used it as a polygon boundary — flipping land/ocean
//   on one side and leaving a sharp visible step on the globe.
//
// Fix: use a 3× wide offscreen canvas so every ring can be drawn as a single
// *unwrapped* polygon (consecutive longitudes never jump > 180°). The closing line
// then ends near the ring's own geographic start point, which is safe. Only the
// central strip [TEX_W … 2*TEX_W], representing the standard [−180°…+180°]
// equirectangular extent, is kept as the actual texture.
function buildGlobeTexture(polys) {
  const WIDE = TEX_W * 3;
  const oc = document.createElement('canvas');
  oc.width = WIDE;
  oc.height = TEX_H;
  const ctx = oc.getContext('2d');
  ctx.clearRect(0, 0, WIDE, TEX_H);
  ctx.fillStyle = '#fff';
  ctx.beginPath();
  for (const ring of polys) {
    if (ring.length < 2) continue;
    // Unwrap: shift each point by ±360° so no consecutive gap exceeds 180°.
    // This keeps the polygon in one coherent piece without splitting.
    let prevLon = normLon(ring[0][0]);
    // In the wide canvas, longitude −180° maps to x = TEX_W (start of centre strip).
    ctx.moveTo(((prevLon + 540) / 360) * TEX_W, ((90 - ring[0][1]) / 180) * TEX_H);
    for (let i = 1; i < ring.length; i++) {
      let lon = normLon(ring[i][0]);
      while (lon - prevLon > 180) lon -= 360;
      while (prevLon - lon > 180) lon += 360;
      prevLon = lon;
      ctx.lineTo(((lon + 540) / 360) * TEX_W, ((90 - ring[i][1]) / 180) * TEX_H);
    }
    ctx.closePath();
  }
  ctx.fill('evenodd');
  // Read back only the centre strip — that is the true [−180°…+180°] texture.
  const imgData = ctx.getImageData(TEX_W, 0, TEX_W, TEX_H).data;
  const tex = new Uint8Array(TEX_W * TEX_H);
  for (let i = 0; i < tex.length; i++) tex[i] = imgData[i * 4 + 3] > 128 ? 1 : 0;
  return tex;
}

// Draw arc polylines as 1px strokes → border texture.
// Also uses a 3× wide canvas + unwrap so arcs crossing ±180° don't jump.
function buildBorderTexture(lines) {
  const WIDE = TEX_W * 3;
  const oc = document.createElement('canvas');
  oc.width = WIDE;
  oc.height = TEX_H;
  const ctx = oc.getContext('2d');
  ctx.clearRect(0, 0, WIDE, TEX_H);
  ctx.strokeStyle = '#fff';
  ctx.lineWidth = 1;
  for (const line of lines) {
    if (line.length < 2) continue;
    ctx.beginPath();
    let prevLon = normLon(line[0][0]);
    ctx.moveTo(((prevLon + 540) / 360) * TEX_W, ((90 - line[0][1]) / 180) * TEX_H);
    for (let i = 1; i < line.length; i++) {
      let lon = normLon(line[i][0]);
      while (lon - prevLon > 180) lon -= 360;
      while (prevLon - lon > 180) lon += 360;
      prevLon = lon;
      ctx.lineTo(((lon + 540) / 360) * TEX_W, ((90 - line[i][1]) / 180) * TEX_H);
    }
    ctx.stroke();
  }
  // Read back only the centre strip.
  const imgData = ctx.getImageData(TEX_W, 0, TEX_W, TEX_H).data;
  const tex = new Uint8Array(TEX_W * TEX_H);
  for (let i = 0; i < tex.length; i++) tex[i] = imgData[i * 4 + 3] > 64 ? 1 : 0;
  return tex;
}

/**
 * Bilinear sample a 1-channel Uint8Array texture.
 * Returns a float in [0,1] — fractional coverage suitable for smooth blending.
 */
function bilinearLookup(tex, latRad, lonRad) {
  if (!tex) return 0;
  let lo = (lonRad * 180) / Math.PI;
  lo = ((((lo + 180) % 360) + 360) % 360) - 180;
  const u = ((lo + 180) / 360) * (TEX_W - 1);
  const la = (latRad * 180) / Math.PI;
  const v = ((90 - la) / 180) * (TEX_H - 1);
  const x0 = Math.max(0, Math.floor(u));
  const y0 = Math.max(0, Math.floor(v));
  const x1 = Math.min(TEX_W - 1, x0 + 1);
  const y1 = Math.min(TEX_H - 1, y0 + 1);
  const fx = u - x0,
    fy = v - y0;
  const s00 = tex[y0 * TEX_W + x0];
  const s10 = tex[y0 * TEX_W + x1];
  const s01 = tex[y1 * TEX_W + x0];
  const s11 = tex[y1 * TEX_W + x1];
  return s00 * (1 - fx) * (1 - fy) + s10 * fx * (1 - fy) + s01 * (1 - fx) * fy + s11 * fx * fy;
}

function globeTexLookup(latRad, lonRad) {
  return bilinearLookup(_globeTex, latRad, lonRad);
}

function borderTexLookup(latRad, lonRad) {
  return bilinearLookup(_globeBorderTex, latRad, lonRad);
}

function stateTexLookup(latRad, lonRad) {
  return bilinearLookup(_globeStateTex, latRad, lonRad);
}

// ── Globe animation state ─────────────────────────────────────────────────
let _globeAnimId = null;
let _globeTargetLat = 0,
  _globeTargetLon = 0;
let _globeCurrentLat = 0,
  _globeCurrentLon = 0;
let _globeIdleLon = 0;
let _globeHasStation = false;
let _globeLastTime = 0;
let _globeZoom = 1.0; // current zoom (1=whole globe, 4=zoomed in)
let _globeZoomTarget = 1.0; // animated toward this
let _globePingPhase = 0; // 0..1 drives the station-dot ping ring
// Transition state machine: 'steady' | 'zoomout'
// When a new station arrives while zoomed in, zoom out first then rotate.
let _globeTransitState = 'steady';
let _globePendingLat = 0;
let _globePendingLon = 0;
let _globePendingRegion = ''; // 'europe' | 'usa' | ''
let _globeCurrentRegion = ''; // active region for renderer
const GLOBE_SNAP_SPEED = 2.5;
const GLOBE_ZOOM_TARGET = 4.0;
const GLOBE_ZOOM_SPEED = 1.2; // exponential approach rate

// ── User-controlled zoom (mouse wheel / two-finger touch slide) ───────────
// _globeUserZoom multiplies the effective zoom on top of the station-driven value.
// Clamped to [GLOBE_USER_ZOOM_MIN, GLOBE_USER_ZOOM_MAX] so the globe never collapses
// to a point and never exceeds a useful maximum.
const GLOBE_USER_ZOOM_MIN = 0.5;  // can pull back to see more of the globe
const GLOBE_USER_ZOOM_MAX = 3.0;  // hard cap — never zooms in to a point
let _globeUserZoom = 1.0;         // current user multiplier
let _globeUserZoomTarget = 1.0;   // user smoothly lerps toward this
let _globeZoomEventsAttached = false; // guard: attach listeners only once

function startGlobeAnim(canvas) {
  if (_globeAnimId) return;
  _globeLastTime = performance.now();
  function frame(now) {
    const dt = Math.min((now - _globeLastTime) / 1000, 0.1);
    _globeLastTime = now;
    if (_globeHasStation) {
      if (_globeTransitState === 'zoomout') {
        // Phase 1: zoom back out to globe view before rotating
        _globeZoomTarget = 1.0;
        const kz = 1 - Math.exp(-GLOBE_ZOOM_SPEED * dt);
        _globeZoom += (_globeZoomTarget - _globeZoom) * kz;
        // Phase 2: once nearly at zoom=1, apply pending station and start zoom-in
        if (_globeZoom < 1.12) {
          _globeZoom = 1.0;
          _globeTargetLat = _globePendingLat;
          _globeTargetLon = _globePendingLon;
          _globeZoomTarget = _globePendingRegion ? GLOBE_ZOOM_TARGET : 1.0;
          _globeCurrentRegion = _globePendingRegion;
          _globePingPhase = 0;
          _globeTransitState = 'steady';
        }
        // Keep globe centred on old location during zoom-out
      } else {
        // Normal: rotate toward target then zoom
        const k = 1 - Math.exp(-GLOBE_SNAP_SPEED * dt);
        _globeCurrentLat += (_globeTargetLat - _globeCurrentLat) * k;
        let dLon = _globeTargetLon - _globeCurrentLon;
        if (dLon > Math.PI) dLon -= 2 * Math.PI;
        else if (dLon < -Math.PI) dLon += 2 * Math.PI;
        _globeCurrentLon += dLon * k;
        const kz = 1 - Math.exp(-GLOBE_ZOOM_SPEED * dt);
        _globeZoom += (_globeZoomTarget - _globeZoom) * kz;
      }
    } else {
      _globeIdleLon += ((GLOBE_IDLE_DEG_PER_SEC * Math.PI) / 180) * dt;
      _globeZoom = 1.0;
      _globeZoomTarget = 1.0;
      _globeTransitState = 'steady';
    }
    // Smooth user-zoom toward its target
    const kzu = 1 - Math.exp(-GLOBE_ZOOM_SPEED * 1.5 * dt);
    _globeUserZoom += (_globeUserZoomTarget - _globeUserZoom) * kzu;
    _globePingPhase = (_globePingPhase + dt * 0.7) % 1;
    drawGlobeFrame(canvas);
    _globeAnimId = requestAnimationFrame(frame);
  }
  _globeAnimId = requestAnimationFrame(frame);
}

function stopGlobeAnim() {
  if (_globeAnimId) {
    cancelAnimationFrame(_globeAnimId);
    _globeAnimId = null;
  }
}

// ── Frame renderer ────────────────────────────────────────────────────────
function drawGlobeFrame(canvas) {
  const W = canvas.width,
    H = canvas.height;
  const ctx = canvas.getContext('2d');
  if (!ctx) return;

  if (_globeTextureDirty && _topoLandPolys) {
    _globeTex = buildGlobeTexture(_topoLandPolys);
    _globeBorderTex = _topoCountryLines ? buildBorderTexture(_topoCountryLines) : null;
    _globeStateTex = _usStateLines ? buildBorderTexture(_usStateLines) : null;
    _globeTextureDirty = false;
  }

  const img = ctx.createImageData(W, H);
  const d = img.data;

  const cx = W / 2,
    cy = H / 2;
  const R = W / 2 - 1;

  // Light: upper-left, slightly in front
  const lx = -0.55,
    ly = 0.55,
    lz = 0.63;
  const lLen = Math.sqrt(lx * lx + ly * ly + lz * lz);
  const Lx = lx / lLen,
    Ly = ly / lLen,
    Lz = lz / lLen;
  const hLen = Math.sqrt(Lx * Lx + Ly * Ly + (Lz + 1) * (Lz + 1));
  const Hx = Lx / hLen,
    Hy = Ly / hLen,
    Hz = (Lz + 1) / hLen;

  const viewLat = _globeHasStation ? _globeCurrentLat : 0;
  const viewLon = _globeHasStation ? _globeCurrentLon : _globeIdleLon;
  const cosLat = Math.cos(viewLat),
    sinLat = Math.sin(viewLat);

  // Zoom: shrink the projected (nx,ny) so fewer degrees are visible.
  // At zoom=1 the full hemisphere is visible; at zoom=4 only 1/4 is shown.
  // _globeUserZoom multiplies the station-driven zoom; clamped so globe never collapses.
  const effectiveZoom = _globeZoom * _globeUserZoom;
  const zoom = Math.max(1, effectiveZoom);

  // Grid spacing adapts to zoom: coarse at zoom≈1, fine at zoom≥3
  const gridDeg = zoom < 1.8 ? 30 : zoom < 3 ? 15 : 10;

  // Border opacity: 0 below zoom 1.5, ramps to 1 at zoom 2.5
  const borderAlpha = Math.max(0, Math.min(1, (_globeZoom - 1.5) / 1.0));

  // Smoothly morph border-radius: circle (50%) at zoom=1, square (0%) when fully zoomed
  const brPct = Math.max(0, Math.round(50 * (1 - (_globeZoom - 1) / (GLOBE_ZOOM_TARGET - 1))));
  canvas.style.borderRadius = brPct + '%';

  // Background colour for corners outside the sphere disk
  const BG_R = 3,
    BG_G = 8,
    BG_B = 5;

  for (let py = 0; py < H; py++) {
    const dy = (py - cy) / R;
    for (let px = 0; px < W; px++) {
      const dx = (px - cx) / R;
      const off = (py * W + px) * 4;

      // Project through zoom: a corner pixel maps to a much closer-to-centre
      // sphere point when zoomed in, allowing the full canvas square to be filled.
      const sx = dx / zoom,
        sy = dy / zoom;
      const sz2 = sx * sx + sy * sy;
      if (sz2 > 1) {
        // Outside the zoomed geographic window — dark background
        d[off + 0] = BG_R;
        d[off + 1] = BG_G;
        d[off + 2] = BG_B;
        d[off + 3] = 255;
        continue;
      }

      // Use zoomed sphere coordinates for both normals and geographic projection.
      // This is correct: the sphere normal at the hit point is (sx, -sy, sqrt(1-sz2)).
      const nx = sx;
      const ny = -sy;
      const nz = Math.sqrt(1 - sz2);

      // Alias for clarity (same values)
      const hnx = nx,
        hny = ny,
        hnz = nz;

      // Inverse orthographic projection → geographic lat/lon
      const geoLat = Math.asin(Math.max(-1, Math.min(1, hnz * sinLat + hny * cosLat)));
      const geoLon = viewLon + Math.atan2(hnx, cosLat * hnz - sinLat * hny);

      // Bilinear land coverage in [0,1]; threshold at 0.5 for hard land/ocean boundary
      const landF = globeTexLookup(geoLat, geoLon);
      const isLand = landF > 0.5;
      const borderF = borderAlpha > 0 ? borderTexLookup(geoLat, geoLon) : 0;
      const stateF =
        borderAlpha > 0 && _globeCurrentRegion === 'usa' ? stateTexLookup(geoLat, geoLon) : 0;

      // Base colours — dark navy ocean, muted green land
      let br = isLand ? 42 : 8,
        bg = isLand ? 90 : 22,
        bb = isLand ? 40 : 38;

      // Grid lines
      const gLat = (geoLat * 180) / Math.PI,
        gLon = (geoLon * 180) / Math.PI;
      const modLat = ((gLat % gridDeg) + gridDeg) % gridDeg;
      const modLon = ((gLon % gridDeg) + gridDeg) % gridDeg;
      const gridThr = Math.max(0.5, 0.8 / zoom);
      const onGrid =
        modLat < gridThr ||
        modLat > gridDeg - gridThr ||
        modLon < gridThr ||
        modLon > gridDeg - gridThr;
      if (onGrid) {
        br += 6;
        bg += 8;
        bb += 6;
      }

      // Country borders — blend by fractional coverage for antialiased look
      if (borderF > 0.05) {
        const bStr = borderAlpha * borderF * 0.9;
        br = Math.round(br * (1 - bStr) + (isLand ? 95 : 45) * bStr);
        bg = Math.round(bg * (1 - bStr) + (isLand ? 165 : 80) * bStr);
        bb = Math.round(bb * (1 - bStr) + (isLand ? 78 : 55) * bStr);
      }

      // US state borders — slightly subtler
      if (stateF > 0.05) {
        const sStr = borderAlpha * stateF * 0.7;
        br = Math.round(br * (1 - sStr) + (isLand ? 78 : 38) * sStr);
        bg = Math.round(bg * (1 - sStr) + (isLand ? 138 : 65) * sStr);
        bb = Math.round(bb * (1 - sStr) + (isLand ? 62 : 45) * sStr);
      }

      // Phong lighting — ocean specular with neutral white highlight
      const diff = Math.max(0, nx * Lx + ny * Ly + nz * Lz);
      const nDotH = Math.max(0, nx * Hx + ny * Hy + nz * Hz);
      const specP = isLand ? 60 : 120;
      const specS = isLand ? 0.05 : 0.45;
      const spec = Math.pow(nDotH, specP) * specS;
      const light = 0.18 + 0.8 * diff;

      d[off + 0] = Math.min(255, br * light + spec * 240);
      d[off + 1] = Math.min(255, bg * light + spec * 240);
      d[off + 2] = Math.min(255, bb * light + spec * 240);
      d[off + 3] = 255;
    }
  }

  ctx.putImageData(img, 0, 0);

  // Atmosphere rim — subtle dark vignette only, no blue tint
  const rimOpacity = Math.max(0, 1 - (_globeZoom - 1) / 0.8);
  if (rimOpacity > 0.01) {
    const inner = ctx.createRadialGradient(cx, cy, R * 0.75, cx, cy, R);
    inner.addColorStop(0, 'rgba(0,0,0,0)');
    inner.addColorStop(1, `rgba(0,0,0,${(0.4 * rimOpacity).toFixed(3)})`);
    ctx.fillStyle = inner;
    ctx.beginPath();
    ctx.arc(cx, cy, R, 0, 2 * Math.PI);
    ctx.fill();
  }

  // Clip all 2D overlays to the globe sphere disk so shadows/glows never bleed into corners
  ctx.save();
  ctx.beginPath();
  ctx.arc(cx, cy, R, 0, 2 * Math.PI);
  ctx.clip();

  // Station dot + animated ping ring at canvas centre
  if (_globeHasStation) {
    // Ping ring: expands from radius 5 to 18, fades out
    const pingR = 5 + _globePingPhase * 13;
    const pingOpacity = Math.max(0, 1 - _globePingPhase) * 0.75;
    ctx.save();
    ctx.globalAlpha = pingOpacity;
    ctx.strokeStyle = '#e94560';
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.arc(cx, cy, pingR, 0, 2 * Math.PI);
    ctx.stroke();
    ctx.restore();

    // Solid dot
    ctx.save();
    ctx.shadowColor = '#e94560';
    ctx.shadowBlur = 8;
    ctx.fillStyle = '#e94560';
    ctx.beginPath();
    ctx.arc(cx, cy, 4, 0, 2 * Math.PI);
    ctx.fill();
    ctx.restore();
  }

  // Additional station dots for all visible stations (feature 10)
  _drawGlobeStationDots(canvas);

  // End clip region
  ctx.restore();
}

// ── Public entry point (called from updateRadioBar) ───────────────────────
async function renderRadioGlobeAsync(station) {
  await ensureTopoLand();
  renderRadioGlobe(station);
}

// ── Globe station dots (feature 10/11/12) ─────────────────────────────────
let _globeDotsEnabled = false;
let _globeGeoStations = []; // full country geo-pool for dot rendering
let _globeGeoCountry = ''; // which country the pool was fetched for
let _globeGeoFetching = false;

/** Fetch up to 500 geo-located stations for `country`; fills _globeGeoStations. */
async function _refreshGeoPool(country) {
  if (!country || _globeGeoFetching) return;
  if (country === _globeGeoCountry && _globeGeoStations.length) return; // already loaded
  _globeGeoFetching = true;
  try {
    const params = new URLSearchParams({ country, limit: '500', offset: '0', q: '', tag: '' });
    const r = await fetch('/api/radio/search?' + params);
    if (r.ok) {
      const arr = await r.json();
      _globeGeoStations = arr;
      _globeGeoCountry = country;
    }
  } catch {}
  _globeGeoFetching = false;
}

function radioToggleGlobeDots() {
  _globeDotsEnabled = !_globeDotsEnabled;
  const btn = document.getElementById('radio-globe-dots-btn');
  if (btn) btn.classList.toggle('active', _globeDotsEnabled);
  // Prefetch geo pool for current station's country when enabling
  if (_globeDotsEnabled && _radioCurrentStation) {
    const country =
      _radioCurrentStation.countrycode ||
      _radioCurrentStation.CountryCode ||
      _radioCurrentStation.country ||
      _radioCurrentStation.Country ||
      '';
    _refreshGeoPool(country).catch(() => {});
  }
  // Re-render immediately with current station
  if (_radioCurrentStation) renderRadioGlobeAsync(_radioCurrentStation).catch(() => {});
}

/**
 * Project geographic [lonDeg,latDeg] onto the globe canvas pixel coordinates.
 * This is the exact forward inverse of the pixel-shader's orthographic projection:
 *   shader inverse: geoLat = asin(nz*sinViewLat + ny*cosViewLat)
 *                   geoLon = viewLon + atan2(nx, cosViewLat*nz - sinViewLat*ny)
 * So forward (geo→view) is: rotation around X by +viewLat, then orthographic project.
 */
function _globeProject(lonDeg, latDeg, canvas) {
  const W = canvas.width,
    H = canvas.height;
  const R = W / 2 - 1; // must match drawGlobeFrame exactly
  const cx = W / 2,
    cy = H / 2;
  const relLon = (lonDeg * Math.PI) / 180 - _globeCurrentLon;
  const geoLat = (latDeg * Math.PI) / 180;
  const viewLat = _globeCurrentLat; // already in radians
  // Unit-sphere point in geographic frame
  const x = Math.cos(geoLat) * Math.sin(relLon);
  const y = Math.sin(geoLat);
  const z = Math.cos(geoLat) * Math.cos(relLon);
  // Rotate around X axis by +viewLat (forward transform)
  const cosVL = Math.cos(viewLat);
  const sinVL = Math.sin(viewLat);
  const nx = x;
  const ny = y * cosVL - z * sinVL;
  const nz = y * sinVL + z * cosVL;
  if (nz <= 0) return null; // behind the globe
  const zoom = Math.max(1, _globeZoom * _globeUserZoom);
  return {
    px: cx + nx * zoom * R,
    py: cy - ny * zoom * R,
  };
}

/** Normalise a raw lat/lon value (number or string) to float or null. */
function _parseCoord(v) {
  if (v === null || v === undefined || v === '') return null;
  const n = typeof v === 'number' ? v : parseFloat(v);
  return isNaN(n) ? null : n;
}

/** Draw station dots for all currently loaded stations on the globe canvas. */
function _drawGlobeStationDots(canvas) {
  if (!_globeDotsEnabled) return;
  const ctx = canvas.getContext('2d');
  // Only show dots for stations currently visible in the list + favorites; deduplicate by uuid
  const visArr = typeof _radioVisibleStations !== 'undefined' ? _radioVisibleStations : _radioStations;
  const stations = [...visArr, ..._radioFavorites];
  const seen = new Set();
  for (const s of stations) {
    const uuid = s.stationuuid || s.uuid || s.Uuid || '';
    if (uuid && seen.has(uuid)) continue;
    if (uuid) seen.add(uuid);
    const lat = _parseCoord(s.geo_lat ?? s.GeoLat);
    const lon = _parseCoord(s.geo_long ?? s.GeoLong);
    if (lat === null || lon === null || (lat === 0 && lon === 0)) continue;
    const pt = _globeProject(lon, lat, canvas);
    if (!pt) continue;
    const isPlaying =
      _radioCurrentUrl &&
      (s.url_resolved || s.streamUrl || s.StreamUrl || s.url || s.Url) === _radioCurrentUrl;
    // The currently playing station is already rendered as the centre ping dot at (cx,cy).
    // Drawing it again here creates a double-shadow that looks like a persistent glow artifact.
    if (isPlaying) continue;
    ctx.save();
    ctx.beginPath();
    ctx.arc(pt.px, pt.py, 3, 0, 2 * Math.PI);
    ctx.fillStyle = 'rgba(255,255,255,0.75)';
    ctx.shadowColor = 'rgba(0,0,0,0.6)';
    ctx.shadowBlur = 3;
    ctx.fill();
    ctx.restore();
  }
}

/** Hit-test globe canvas for a mouse position; return station or null. */
function _globeHitTest(canvas, offsetX, offsetY) {
  if (!_globeDotsEnabled) return null;
  const visArr = typeof _radioVisibleStations !== 'undefined' ? _radioVisibleStations : _radioStations;
  const stations = [...visArr, ..._radioFavorites];
  const seen = new Set();
  for (const s of stations) {
    const uuid = s.stationuuid || s.uuid || s.Uuid || '';
    if (uuid && seen.has(uuid)) continue;
    if (uuid) seen.add(uuid);
    const lat = _parseCoord(s.geo_lat ?? s.GeoLat);
    const lon = _parseCoord(s.geo_long ?? s.GeoLong);
    if (lat === null || lon === null || (lat === 0 && lon === 0)) continue;
    const pt = _globeProject(lon, lat, canvas);
    if (!pt) continue;
    const dx = offsetX - pt.px,
      dy = offsetY - pt.py;
    if (dx * dx + dy * dy <= 100) return { station: s, px: pt.px, py: pt.py }; // 10px radius
  }
  return null;
}

// ── Globe zoom event handlers (mouse wheel + two-finger touch vertical slide) ──

function _globeAttachZoomEvents(canvas) {
  if (_globeZoomEventsAttached) return;
  _globeZoomEventsAttached = true;

  // Mouse wheel — each notch zooms ±10 %
  canvas.addEventListener('wheel', (e) => {
    e.preventDefault();
    // normalise delta across browsers (deltaMode 0=px, 1=lines, 2=page)
    let delta = e.deltaY;
    if (e.deltaMode === 1) delta *= 40;
    else if (e.deltaMode === 2) delta *= 400;
    const step = delta > 0 ? -0.12 : 0.12;
    _globeUserZoomTarget = Math.max(GLOBE_USER_ZOOM_MIN,
      Math.min(GLOBE_USER_ZOOM_MAX, _globeUserZoomTarget + step));
  }, { passive: false });

  // Touch: track two-finger vertical drag for zoom.
  // Sliding UP (fingers move up) = zoom in; DOWN = zoom out.
  let _touchStartY = null;

  canvas.addEventListener('touchstart', (e) => {
    if (e.touches.length === 2) {
      _touchStartY = (e.touches[0].clientY + e.touches[1].clientY) / 2;
      e.preventDefault();
    }
  }, { passive: false });

  canvas.addEventListener('touchmove', (e) => {
    if (e.touches.length === 2 && _touchStartY !== null) {
      const currentY = (e.touches[0].clientY + e.touches[1].clientY) / 2;
      const dy = _touchStartY - currentY; // positive = fingers moved up = zoom in
      _touchStartY = currentY;
      const step = (dy / canvas.clientHeight) * 2.5; // scale drag to zoom
      _globeUserZoomTarget = Math.max(GLOBE_USER_ZOOM_MIN,
        Math.min(GLOBE_USER_ZOOM_MAX, _globeUserZoomTarget + step));
      e.preventDefault();
    }
  }, { passive: false });

  canvas.addEventListener('touchend', (e) => {
    if (e.touches.length < 2) _touchStartY = null;
  }, { passive: true });
}

function radioGlobeMouseMove(event) {
  const canvas = document.getElementById('radio-globe-canvas');
  const tooltip = document.getElementById('radio-globe-tooltip');
  if (!canvas || !tooltip) return;
  const hit = _globeHitTest(canvas, event.offsetX, event.offsetY);
  if (!hit) {
    tooltip.style.display = 'none';
    return;
  }
  const s = hit.station;
  const name = s.name || s.Name || '?';
  const bitrate = s.bitrate || s.Bitrate || 0;
  const codec = (s.codec || s.Codec || '').toUpperCase();
  let tip = escHtml(name);
  if (codec || bitrate)
    tip += `<br><small>${escHtml([codec, bitrate ? bitrate + 'k' : ''].filter(Boolean).join(' '))}</small>`;
  tooltip.innerHTML = tip;
  tooltip.style.display = 'block';
  tooltip.style.left = hit.px + 6 + 'px';
  tooltip.style.top = hit.py - 24 + 'px';
}

function radioGlobeMouseLeave() {
  const tooltip = document.getElementById('radio-globe-tooltip');
  if (tooltip) tooltip.style.display = 'none';
}

function radioGlobeClick(event) {
  const canvas = document.getElementById('radio-globe-canvas');
  if (!canvas) return;
  const hit = _globeHitTest(canvas, event.offsetX, event.offsetY);
  if (!hit) return;
  const s = hit.station;
  const url = s.url_resolved || s.streamUrl || s.StreamUrl || '';
  const name = s.name || s.Name || '';
  if (!url) return;
  const country = s.country || s.Country || '';
  const tag = (s.tags || s.Tags || '').split(',').filter(Boolean)[0] || '';
  radioPlayStation(
    encodeURIComponent(url),
    encodeURIComponent(name),
    encodeURIComponent(country),
    encodeURIComponent(tag),
    encodeURIComponent(JSON.stringify(s))
  );
  radioGlobeMouseLeave();
}

function renderRadioGlobe(station) {
  const geoWrap = document.getElementById('radio-bar-geo');
  const canvas = document.getElementById('radio-globe-canvas');
  const label = document.getElementById('radio-geo-label');
  if (!geoWrap || !canvas || !label) return;

  let lat = station && (station.geo_lat ?? station.GeoLat ?? null);
  let lon = station && (station.geo_long ?? station.GeoLong ?? null);
  let hasCoords = typeof lat === 'number' && typeof lon === 'number' && !(lat === 0 && lon === 0);
  const countryName = station && (station.country || station.Country || '');
  const stateName = station && (station.state || station.State || '');
  const countryCode = station && (station.countrycode || station.CountryCode || '');

  if (!hasCoords && countryCode) {
    const c = _countryCentroids[countryCode.toUpperCase()];
    if (c) {
      lat = c[0];
      lon = c[1];
      hasCoords = true;
    }
  }

  if (!station || (!hasCoords && !countryName)) {
    geoWrap.style.display = 'none';
    stopGlobeAnim();
    return;
  }
  geoWrap.style.display = 'flex';

  const labelParts = [];
  if (countryName) labelParts.push('\uD83C\uDF10 ' + countryName);
  if (stateName) labelParts.push('\uD83D\uDCCD ' + stateName);
  const rawLat = station && (station.geo_lat ?? station.GeoLat ?? null);
  const rawLon = station && (station.geo_long ?? station.GeoLong ?? null);
  const hasRawCoords =
    typeof rawLat === 'number' && typeof rawLon === 'number' && !(rawLat === 0 && rawLon === 0);
  if (hasRawCoords) labelParts.push(rawLat.toFixed(2) + '\u00b0, ' + rawLon.toFixed(2) + '\u00b0');
  label.textContent = labelParts.join('\n');

  if (!hasCoords) {
    canvas.style.display = 'none';
    stopGlobeAnim();
    return;
  }
  canvas.style.display = 'block';

  // Wire user-zoom events once
  _globeAttachZoomEvents(canvas);

  // Detect new station BEFORE updating targets
  const newLatRad = (lat * Math.PI) / 180;
  const newLonRad = (lon * Math.PI) / 180;
  const isNewStation =
    !_globeHasStation || _globeTargetLat !== newLatRad || _globeTargetLon !== newLonRad;

  // Europe: lat 35..72°N, lon -25..45°E
  const isEurope = lat >= 35 && lat <= 72 && lon >= -25 && lon <= 45;
  // Contiguous USA + Alaska: lat 18..72, lon -170..-65
  const isUSA = lat >= 18 && lat <= 72 && lon >= -170 && lon <= -65;
  // Region is used only to decide whether two consecutive stations are "close enough"
  // to skip the zoom-out/rotate-in transition. Every station zooms in now.
  const region = isEurope ? 'europe' : isUSA ? 'usa' : `coord:${Math.round(lat / 15)},${Math.round(lon / 15)}`;

  const wasFirstStation = !_globeHasStation;

  if (isNewStation) {
    _globeHasStation = true;
    const sameRegion = region && region === _globeCurrentRegion;
    if (_globeZoom > 1.3 && !sameRegion) {
      // Currently zoomed into a different region — zoom out first
      _globePendingLat = newLatRad;
      _globePendingLon = newLonRad;
      _globePendingRegion = region;
      _globeZoomTarget = 1.0;
      _globeTransitState = 'zoomout';
    } else if (_globeZoom > 1.3 && sameRegion) {
      // Same region, just rotate — keep zoom
      _globeTargetLat = newLatRad;
      _globeTargetLon = newLonRad;
      _globeZoomTarget = GLOBE_ZOOM_TARGET;
      _globePingPhase = 0;
      _globeTransitState = 'steady';
    } else {
      // Not zoomed: animate rotation then zoom in on the station's location
      _globeTargetLat = newLatRad;
      _globeTargetLon = newLonRad;
      if (wasFirstStation) {
        _globeCurrentLat = newLatRad;
        _globeCurrentLon = newLonRad;
      }
      _globeZoom = 1.0;
      _globeZoomTarget = GLOBE_ZOOM_TARGET; // always zoom in, regardless of region
      _globeCurrentRegion = region;
      _globePingPhase = 0;
      _globeTransitState = 'steady';
    }
  } else if (!_globeAnimId) {
    // Re-entering same station (e.g. panel reopened) — just resume
    _globeTargetLat = newLatRad;
    _globeTargetLon = newLonRad;
    _globeHasStation = true;
  }
  startGlobeAnim(canvas);
}
