# Web asset provenance

## Globe map data

The globe renderer in [app-globe.js](../WebAssets/app-globe.js) uses these
server-served geographic assets:

- `WebAssets/world-50m.json` supplies land polygons and country borders. It is
  TopoJSON from [topojson/world-atlas](https://github.com/topojson/world-atlas),
  which packages Natural Earth map data. Its README identifies Natural Earth
  4.1.0 as the current source version; the exact source release for this
  vendored file is not recorded. The repository's
  [license](https://github.com/topojson/world-atlas/blob/master/LICENSE)
  contains Michael Bostock's permissive ISC-style notice; the underlying
  [Natural Earth data](https://www.naturalearthdata.com/about/terms-of-use/) is
  public domain.
- `WebAssets/us-states.json` supplies state polygons for the U.S. border
  overlay. Its Git blob matches
  [PublicaMundi/MappingAPI's `us-states.json`](https://github.com/PublicaMundi/MappingAPI/blob/86ac7bcffa2da7924757ccb2f048aa1412154ad2/data/geojson/us-states.json)
  (blob `1c1ebe55f07425be670a90740c11560fe86acbe6`). The upstream repository has
  no root `LICENSE` file or attribution describing this data, so the license and
  precise source of this GeoJSON derivative are unconfirmed. Do not infer a
  license for the file from the apparent origin of its boundaries; verify
  provenance before replacing or redistributing it separately.
- `WebAssets/world-110m.json` is the lower-resolution world map counterpart. No
  first-party client currently requests it, but
  [WebServer.Handlers.Request.cs](../Services/Web/WebServer.Handlers.Request.cs)
  exposes `/world-110m.json`. It is retained as an externally observable
  compatibility route; remove it only with an explicit endpoint deprecation.

The 50m world map and U.S. state data are loaded lazily by the globe renderer;
the corresponding assets and routes are wired in
[WebServer.cs](../Services/Web/WebServer.cs) and
[WebServer.Handlers.Request.cs](../Services/Web/WebServer.Handlers.Request.cs).
