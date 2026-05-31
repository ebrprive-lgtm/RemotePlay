using System.IO;
using System.Net;
using System.Text.Json;

namespace RemotePlay;

// Music library browsing, search, metadata, album art, and playlist handlers.

internal sealed partial class WebServer
{
    private void HandleMusicBrowse(HttpListenerContext ctx)
    {
        if (_musicIndex.Length == 0 && !_isMusicIndexing)
            StartMusicIndexRefresh();

        var folderParam = (ctx.Request.QueryString["folder"] ?? string.Empty).Trim();

        // If the user navigates to a specific folder that has no entries in the index yet,
        // scan it immediately on this thread so results are available right now â€” regardless
        // of whether the background BFS is still running or has already completed.
        if (!string.IsNullOrEmpty(folderParam) && Directory.Exists(folderParam))
        {
            // Check whether this folder already has entries in the index.
            bool alreadyIndexed;
            lock (_musicIndexGate)
                alreadyIndexed = _musicIndex.Any(f =>
                    string.Equals(Path.GetDirectoryName(f.FullPath), folderParam, StringComparison.OrdinalIgnoreCase));

            if (!alreadyIndexed)
            {
                // Quick synchronous scan of just this one directory.
                var instant = new List<MusicFile>();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(folderParam, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        if (!_musicExtensions.Contains(Path.GetExtension(f))) continue;
                        instant.Add(new MusicFile(Name: Path.GetFileNameWithoutExtension(f), FullPath: f));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning("MusicScanner", $"Instant browse scan failed for '{folderParam}': {ex.Message}");
                }

                if (instant.Count > 0)
                {
                    // Merge into the live index; background scan will skip this dir via _scanned.
                    lock (_musicIndexGate)
                    {
                        var merged = _musicIndex.Concat(instant).ToArray();
                        Array.Sort(merged, (a, b) =>
                            string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));
                        _musicIndex = merged;
                    }
                    // Tell the background job not to re-scan this folder.
                    MusicScanJob? activeJob;
                    lock (_musicIndexGate)
                        activeJob = _activeMusicScanJob;
                    activeJob?.MarkScanned(folderParam);

                    Logger.Detail("MusicScanner", $"Instant browse scan: {instant.Count} track(s) from '{folderParam}'");
                }
            }
            else
            {
                // Folder is already indexed â€” still tell the background job to skip it
                // and promote it so BFS moves on faster.
                MusicScanJob? activeJob;
                lock (_musicIndexGate)
                    activeJob = _activeMusicScanJob;
                activeJob?.Prioritize(folderParam);
            }
        }

        var offset = ReadNonNegativeInt(ctx.Request.QueryString["offset"]);
        var limit = ReadPositiveInt(ctx.Request.QueryString["limit"], _config.EffectiveLibraryPageSize, 1000);

        var allTracks = _musicIndex;
        var musicRoot = _config.ResolvedMusicPath;

        // Files are only returned when a specific subfolder path is requested,
        // and only direct children of that folder (not nested subdirectories).
        var filteredAll = string.IsNullOrEmpty(folderParam)
            ? []
            : allTracks.Where(f =>
                string.Equals(Path.GetDirectoryName(f.FullPath), folderParam, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var page = filteredAll.Skip(offset).Take(limit).ToArray();

        // At root: show direct subdirectories of the music root.
        // Inside a subfolder: no nested folders shown (flat file list).
        object[] folders;
        if (string.IsNullOrEmpty(folderParam))
        {
            try
            {
                folders = Directory.Exists(musicRoot)
                    ? Directory.GetDirectories(musicRoot)
                        .Where(d => !_hiddenFolderNames.Contains(Path.GetFileName(d)))
                        .OrderBy(d => string.Equals(Path.GetFileName(d), "All", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(d => d, _naturalComparer)
                        .Select(d => (object)new { name = Path.GetFileName(d), folder = d, isAll = string.Equals(Path.GetFileName(d), "All", StringComparison.OrdinalIgnoreCase) })
                        .ToArray()
                    : [];
            }
            catch
            {
                folders = [];
            }
        }
        else
        {
            // Show direct sub-directories of the requested folder
            try
            {
                folders = Directory.Exists(folderParam)
                    ? Directory.GetDirectories(folderParam)
                        .Where(d => !_hiddenFolderNames.Contains(Path.GetFileName(d)))
                        .OrderBy(d => string.Equals(Path.GetFileName(d), "All", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(d => d, _naturalComparer)
                        .Select(d => (object)new { name = Path.GetFileName(d), folder = d, isAll = string.Equals(Path.GetFileName(d), "All", StringComparison.OrdinalIgnoreCase) })
                        .ToArray()
                    : [];
            }
            catch
            {
                folders = [];
            }
        }

        var files = page
            .Select(f => (object)BuildMusicFileObj(f))
            .ToArray();

        var scanDir = string.IsNullOrEmpty(folderParam) ? musicRoot : folderParam;

        // Discover .rpDynamic pseudo-folders in the current directory and append them to the folders list
        if (Directory.Exists(scanDir))
        {
            try
            {
                var dynamicFiles = Directory.EnumerateFiles(scanDir, "*.rpDynamic", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, _naturalComparer)
                    .Select(f =>
                    {
                        var name = Path.GetFileNameWithoutExtension(f);
                        int trackCount = 0;
                        string mode = "sample";
                        string sort = "random";
                        string[] include = [];
                        string[] exclude = [];
                        string genre = string.Empty;
                        int? yearFrom = null;
                        int? yearTo = null;
                        string? lastExpanded = null;
                        try
                        {
                            var json = File.ReadAllText(f);
                            using var doc = JsonDocument.Parse(json);
                            // Prefer lastCount (actual tracks found last expand) over count (configured limit)
                            if (doc.RootElement.TryGetProperty("lastCount", out var lcProp))
                                trackCount = lcProp.GetInt32();
                            else if (doc.RootElement.TryGetProperty("count", out var countProp))
                                trackCount = countProp.GetInt32();
                            if (doc.RootElement.TryGetProperty("mode", out var modeProp))
                                mode = modeProp.GetString() ?? mode;
                            if (doc.RootElement.TryGetProperty("sort", out var sortProp))
                                sort = sortProp.GetString() ?? sort;
                            if (doc.RootElement.TryGetProperty("name", out var nameProp))
                                name = nameProp.GetString() ?? name;
                            if (doc.RootElement.TryGetProperty("include", out var incP) && incP.ValueKind == JsonValueKind.Array)
                                include = [.. incP.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(s => s.Length > 0)];
                            if (doc.RootElement.TryGetProperty("exclude", out var exP) && exP.ValueKind == JsonValueKind.Array)
                                exclude = [.. exP.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(s => s.Length > 0)];
                            if (doc.RootElement.TryGetProperty("genre", out var gp)) genre = gp.GetString() ?? string.Empty;
                            if (doc.RootElement.TryGetProperty("yearFrom", out var yfp) && yfp.ValueKind == JsonValueKind.Number) yearFrom = yfp.GetInt32();
                            if (doc.RootElement.TryGetProperty("yearTo",   out var ytp) && ytp.ValueKind == JsonValueKind.Number) yearTo   = ytp.GetInt32();
                            if (doc.RootElement.TryGetProperty("lastExpanded", out var lep)) lastExpanded = lep.GetString();
                        }
                        catch { }
                        return (object)new { name, folder = WebPathHelpers.EncodePath(f), isDynamic = true, dynamicPath = WebPathHelpers.EncodePath(f), trackCount, mode, sort, include, exclude, genre, yearFrom, yearTo, lastExpanded };
                    })
                    .ToArray();

                if (dynamicFiles.Length > 0)
                    folders = [.. folders, .. dynamicFiles];
            }
            catch (Exception ex)
            {
                Logger.Warning("MusicBrowse", $"Cannot scan .rpDynamic files in '{scanDir}': {ex.Message}");
            }
        }

        object[] playlists = [];
        if (Directory.Exists(scanDir))
        {
            try
            {
                playlists = Directory.EnumerateFiles(scanDir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f =>
                    {
                        var ext = Path.GetExtension(f);
                        return string.Equals(ext, ".m3u", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(ext, ".m3u8", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderBy(f => f, _naturalComparer)
                    .Select(f =>
                    {
                        int trackCount = 0;
                        string? artist = null;
                        try
                        {
                            var lines = File.ReadLines(f).Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#')).ToList();
                            trackCount = lines.Count;
                            // Peek at the first track to extract the artist tag
                            if (lines.Count > 0)
                            {
                                var firstEntry = lines[0].Trim();
                                var firstPath = Path.IsPathRooted(firstEntry)
                                    ? firstEntry
                                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(f)!, firstEntry));
                                if (File.Exists(firstPath))
                                {
                                    try
                                    {
                                        using var tagFile = TagLib.File.Create(firstPath);
                                        var performers = tagFile.Tag.AlbumArtists.Length > 0
                                            ? tagFile.Tag.AlbumArtists
                                            : tagFile.Tag.Performers;
                                        artist = performers.Length > 0 ? performers[0] : null;
                                    }
                                    catch { /* tag read failed, leave artist null */ }
                                }
                            }
                        }
                        catch { /* ignore unreadable playlists */ }
                        return (object)new { name = Path.GetFileNameWithoutExtension(f), path = WebPathHelpers.EncodePath(f), trackCount, artist };
                    })
                    .ToArray();
            }
            catch (Exception ex)
            {
                Logger.Warning("MusicBrowse", $"Cannot scan playlists in '{scanDir}': {ex.Message}");
            }
        }

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new
        {
            folders,
            playlists,
            files,
            total = files.Length,
            totalInFolder = filteredAll.Length,
            offset,
            limit,
            hasMore = offset + files.Length < filteredAll.Length,
            indexedFiles = allTracks.Length,
            indexing = _isMusicIndexing,
            enriching = _isMusicEnriching,
            lastError = _lastMusicScanError,
            lastRefreshUtc = _lastMusicIndexRefreshUtc,
            musicRoot = musicRoot,
            folder = string.IsNullOrEmpty(folderParam) ? musicRoot : folderParam
        }));
    }

    private void HandleMusicPlaylist(HttpListenerContext ctx)
    {
        var encodedPath = (ctx.Request.QueryString["path"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }
        var filePath = WebPathHelpers.DecodePath(encodedPath);
        if (!File.Exists(filePath))
        {
            TrySendResponse(ctx, 404, "text/plain", "Playlist not found");
            return;
        }
        var dir = Path.GetDirectoryName(filePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(filePath);
        var tracks = new List<object>();
        try
        {
            foreach (var raw in File.ReadLines(filePath))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                    continue;
                // Resolve relative or absolute path; normalise any forward-slash separators
                var normalised = line.Replace('/', Path.DirectorySeparatorChar);
                var resolved = Path.IsPathRooted(normalised)
                    ? normalised
                    : Path.GetFullPath(Path.Combine(dir, normalised));
                if (File.Exists(resolved))
                    tracks.Add(new { path = WebPathHelpers.EncodePath(resolved), name = Path.GetFileNameWithoutExtension(resolved) });
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("MusicPlaylist", $"Error reading playlist '{filePath}': {ex.Message}");
        }
        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { name, tracks }));
    }

    // â”€â”€ rpDynamic folder handlers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>PUT /api/music/dynamic â€” create a new .rpDynamic file. Body JSON: { dir, name, count }</summary>
    private void HandleMusicDynamicCreate(HttpListenerContext ctx)
    {
        if (!string.Equals(ctx.Request.HttpMethod, "PUT", StringComparison.OrdinalIgnoreCase))
        {
            TrySendResponse(ctx, 405, "text/plain", "Method not allowed");
            return;
        }
        using var reader = new System.IO.StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8, leaveOpen: true);
        var body = reader.ReadToEnd();
        JsonElement root;
        try { using var doc = JsonDocument.Parse(body); root = doc.RootElement.Clone(); }
        catch { TrySendResponse(ctx, 400, "text/plain", "Invalid JSON"); return; }

        var dir = root.TryGetProperty("dir", out var dp) ? dp.GetString() ?? string.Empty : string.Empty;
        var name = root.TryGetProperty("name", out var np) ? (np.GetString() ?? "My Dynamic Folder").Trim() : "My Dynamic Folder";
        var count = root.TryGetProperty("count", out var cp) ? cp.GetInt32() : 20;
        var sort = root.TryGetProperty("sort", out var sp) ? sp.GetString() ?? "random" : "random";
        var mode = root.TryGetProperty("mode", out var mp) ? mp.GetString() ?? "sample" : "sample";
        var recursive = !root.TryGetProperty("recursive", out var rp) || rp.GetBoolean();
        var include = root.TryGetProperty("include", out var ip) && ip.ValueKind == JsonValueKind.Array
            ? [.. ip.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(s => s.Length > 0)]
            : (string[])[];

        if (!Directory.Exists(dir))
        {
            TrySendResponse(ctx, 400, "text/plain", "Directory not found");
            return;
        }

        // Sanitise filename
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(name)) name = "My Dynamic Folder";

        var filePath = Path.Combine(dir, name + ".rpDynamic");
        var settings = new { name, count, sort, mode, recursive, include };
        try
        {
            File.WriteAllText(filePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            TrySendResponse(ctx, 500, "text/plain", $"Cannot create file: {ex.Message}");
            return;
        }
        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true, path = WebPathHelpers.EncodePath(filePath) }));
    }

    /// <summary>GET /api/music/dynamic?path=... â€” read settings from a .rpDynamic file.</summary>
    private void HandleMusicDynamicGet(HttpListenerContext ctx)
    {
        var encodedPath = (ctx.Request.QueryString["path"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(encodedPath)) { TrySendResponse(ctx, 400, "text/plain", "Missing path"); return; }
        var filePath = WebPathHelpers.DecodePath(encodedPath);
        if (!File.Exists(filePath)) { TrySendResponse(ctx, 404, "text/plain", "Not found"); return; }
        try
        {
            var json = File.ReadAllText(filePath);
            TrySendResponse(ctx, 200, "application/json", json);
        }
        catch (Exception ex) { TrySendResponse(ctx, 500, "text/plain", ex.Message); }
    }

    /// <summary>POST /api/music/dynamic?path=... â€” save updated settings to a .rpDynamic file. Body JSON: { name, count }</summary>
    private void HandleMusicDynamicSave(HttpListenerContext ctx)
    {
        if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            TrySendResponse(ctx, 405, "text/plain", "Method not allowed");
            return;
        }
        var encodedPath = (ctx.Request.QueryString["path"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(encodedPath)) { TrySendResponse(ctx, 400, "text/plain", "Missing path"); return; }
        var filePath = WebPathHelpers.DecodePath(encodedPath);
        if (!File.Exists(filePath)) { TrySendResponse(ctx, 404, "text/plain", "Not found"); return; }

        using var reader = new System.IO.StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8, leaveOpen: true);
        var body = reader.ReadToEnd();
        JsonElement root;
        try { using var doc = JsonDocument.Parse(body); root = doc.RootElement.Clone(); }
        catch { TrySendResponse(ctx, 400, "text/plain", "Invalid JSON"); return; }

        var newName = root.TryGetProperty("name", out var np) ? (np.GetString() ?? string.Empty).Trim() : string.Empty;
        var count = root.TryGetProperty("count", out var cp) ? cp.GetInt32() : 20;
        var sort = root.TryGetProperty("sort", out var sp) ? sp.GetString() ?? "random" : "random";
        var mode = root.TryGetProperty("mode", out var mp) ? mp.GetString() ?? "sample" : "sample";
        var recursive = !root.TryGetProperty("recursive", out var rp) || rp.GetBoolean();
        var include = root.TryGetProperty("include", out var ip) && ip.ValueKind == JsonValueKind.Array
            ? [.. ip.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(s => s.Length > 0)]
            : (string[])[];
        var exclude = root.TryGetProperty("exclude", out var ep) && ep.ValueKind == JsonValueKind.Array
            ? [.. ep.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(s => s.Length > 0)]
            : (string[])[];
        var genre     = root.TryGetProperty("genre",    out var gp) ? gp.GetString() ?? string.Empty : string.Empty;
        var yearFrom  = root.TryGetProperty("yearFrom", out var yfp) && yfp.ValueKind == JsonValueKind.Number ? (int?)yfp.GetInt32() : null;
        var yearTo    = root.TryGetProperty("yearTo",   out var ytp) && ytp.ValueKind == JsonValueKind.Number ? (int?)ytp.GetInt32() : null;
        var autoPlay  = root.TryGetProperty("autoPlay", out var app) && app.GetBoolean();

        // Preserve existing lastExpanded when saving (don't overwrite with null)
        string? lastExpanded = null;
        try
        {
            var existing = File.ReadAllText(filePath);
            using var existDoc = JsonDocument.Parse(existing);
            if (existDoc.RootElement.TryGetProperty("lastExpanded", out var lep))
                lastExpanded = lep.GetString();
        }
        catch { }

        // If name changed, rename the file
        var dir = Path.GetDirectoryName(filePath) ?? string.Empty;
        var currentName = Path.GetFileNameWithoutExtension(filePath);
        var targetPath = filePath;
        if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName, currentName, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var c in Path.GetInvalidFileNameChars()) newName = newName.Replace(c, '_');
            targetPath = Path.Combine(dir, newName + ".rpDynamic");
            try { File.Move(filePath, targetPath, overwrite: false); }
            catch (Exception ex) { TrySendResponse(ctx, 500, "text/plain", $"Cannot rename: {ex.Message}"); return; }
        }

        var effectiveName = string.IsNullOrWhiteSpace(newName) ? currentName : newName;
        var settings = new
        {
            name = effectiveName, count, sort, mode, recursive, include, exclude,
            genre, yearFrom, yearTo, autoPlay,
            lastExpanded
        };
        try
        {
            File.WriteAllText(targetPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) { TrySendResponse(ctx, 500, "text/plain", ex.Message); return; }
        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true, path = WebPathHelpers.EncodePath(targetPath) }));
    }

    /// <summary>DELETE /api/music/dynamic?path=... â€” permanently delete a .rpDynamic file.</summary>
    private void HandleMusicDynamicDelete(HttpListenerContext ctx)
    {
        var encodedPath = (ctx.Request.QueryString["path"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(encodedPath)) { TrySendResponse(ctx, 400, "text/plain", "Missing path"); return; }
        var filePath = WebPathHelpers.DecodePath(encodedPath);
        if (!filePath.EndsWith(".rpDynamic", StringComparison.OrdinalIgnoreCase))
        { TrySendResponse(ctx, 400, "text/plain", "Not a dynamic folder file"); return; }
        if (!File.Exists(filePath)) { TrySendResponse(ctx, 404, "text/plain", "Not found"); return; }
        try { File.Delete(filePath); }
        catch (Exception ex) { TrySendResponse(ctx, 500, "text/plain", ex.Message); return; }
        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true }));
    }

    private static readonly string[] _musicAudioExtensions = [".mp3", ".flac", ".ogg", ".aac", ".m4a", ".wav", ".wma", ".opus", ".ape"];

    /// <summary>GET /api/music/dynamic/expand?path=... â€” resolve the track list for a .rpDynamic folder.</summary>
    private void HandleMusicDynamicExpand(HttpListenerContext ctx)
    {
        var encodedPath = (ctx.Request.QueryString["path"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(encodedPath)) { TrySendResponse(ctx, 400, "text/plain", "Missing path"); return; }
        var filePath = WebPathHelpers.DecodePath(encodedPath);
        if (!File.Exists(filePath)) { TrySendResponse(ctx, 404, "text/plain", "Not found"); return; }

        string settingsJson;
        try { settingsJson = File.ReadAllText(filePath); }
        catch (Exception ex) { TrySendResponse(ctx, 500, "text/plain", ex.Message); return; }

        int count = 20;
        string displayName = Path.GetFileNameWithoutExtension(filePath);
        string sort = "random";      // random | newest | oldest | alphabetical
        string mode = "sample";      // sample | all
        bool recursive = true;
        string[] include = [];
        string[] exclude = [];
        string genre = string.Empty;
        int? yearFrom = null;
        int? yearTo = null;

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("count",     out var cp)) count       = cp.GetInt32();
            if (root.TryGetProperty("name",      out var np)) displayName = np.GetString() ?? displayName;
            if (root.TryGetProperty("sort",      out var sp)) sort        = sp.GetString() ?? sort;
            if (root.TryGetProperty("mode",      out var mp)) mode        = mp.GetString() ?? mode;
            if (root.TryGetProperty("recursive", out var rp)) recursive   = rp.GetBoolean();
            if (root.TryGetProperty("include",   out var ip) && ip.ValueKind == JsonValueKind.Array)
                include = [.. ip.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(s => s.Length > 0)];
            if (root.TryGetProperty("exclude",   out var exP) && exP.ValueKind == JsonValueKind.Array)
                exclude = [.. exP.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(s => s.Length > 0)];
            if (root.TryGetProperty("genre",    out var gp)) genre    = gp.GetString() ?? string.Empty;
            if (root.TryGetProperty("yearFrom", out var yfp) && yfp.ValueKind == JsonValueKind.Number) yearFrom = yfp.GetInt32();
            if (root.TryGetProperty("yearTo",   out var ytp) && ytp.ValueKind == JsonValueKind.Number) yearTo   = ytp.GetInt32();
        }
        catch { }

        var baseDir = Path.GetDirectoryName(filePath) ?? string.Empty;
        var baseDirSlash = baseDir.EndsWith(Path.DirectorySeparatorChar) ? baseDir : baseDir + Path.DirectorySeparatorChar;
        bool needsTagFilter = !string.IsNullOrWhiteSpace(genre) || yearFrom.HasValue || yearTo.HasValue;

        Logger.Info("DynamicExpand", $"Expanding '{displayName}' | baseDir='{baseDir}' sort={sort} mode={mode} count={count} recursive={recursive} include=[{string.Join(", ", include)}] exclude=[{string.Join(", ", exclude)}] genre='{genre}' yearFrom={yearFrom} yearTo={yearTo}");

        // â”€â”€ Phase 1: candidate selection from in-memory index (zero filesystem I/O) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Take a snapshot of the index so we work on a consistent array without holding a lock.
        // If the index is empty and no scan is running, kick one off so future calls are fast.
        if (_musicIndex.Length == 0 && !_isMusicIndexing)
            StartMusicIndexRefresh();
        var index = _musicIndex;
        if (index.Length == 0)
            Logger.Warning("DynamicExpand", "Music index is empty â€” results may be incomplete. Index scan is in progress.");

        // Collect M3U-sourced candidates from the in-memory M3U index (zero filesystem I/O).
        var m3uSnapshot = _m3uIndex;
        var trackAlbumHint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var m3uCandidates = new List<string>();
        int m3uFileCount = 0;

        Logger.Info("DynamicExpand", $"M3U index contains {m3uSnapshot.Count} playlist(s) total. baseDirSlash='{baseDirSlash}'");
        if (m3uSnapshot.Count > 0)
        {
            // Log up to 5 sample keys so we can see what paths are stored
            int sampleCount = 0;
            foreach (var k in m3uSnapshot.Keys)
            {
                Logger.Info("DynamicExpand", $"  M3U index sample: '{k}'");
                if (++sampleCount >= 5) break;
            }
        }

        foreach (var kv in m3uSnapshot)
        {
            var m3uPath = kv.Key;
            var entry   = kv.Value;

            // Must be under baseDir
            if (!m3uPath.StartsWith(baseDirSlash, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info("DynamicExpand", $"  SKIP (not under baseDir): '{m3uPath}'");
                continue;
            }
            if (!recursive)
            {
                // Non-recursive: the M3U itself must be directly in baseDir (no subdirectory)
                var rel = m3uPath[baseDirSlash.Length..];
                if (rel.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0) continue;
            }

            // Apply include/exclude filters to the M3U's parent folder path (not the filename).
            // The user's filter terms describe folder names, not playlist filenames.
            var m3uDir  = Path.GetDirectoryName(m3uPath) ?? string.Empty;
            var m3uDirRel = m3uDir.Length > baseDirSlash.Length ? m3uDir[baseDirSlash.Length..] : string.Empty;
            Logger.Info("DynamicExpand", $"  Checking M3U: '{m3uPath}' | m3uDirRel='{m3uDirRel}' | tracks={entry.TrackPaths.Length}");
            if (include.Length > 0 && !include.Any(p => m3uDirRel.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                Logger.Info("DynamicExpand", $"    SKIP (include filter '{string.Join(",", include)}' not matched by m3uDirRel='{m3uDirRel}')");
                continue;
            }
            if (exclude.Length > 0 &&  exclude.Any(p => m3uDirRel.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;

            int added = 0;
            foreach (var track in entry.TrackPaths)
            {
                if (!seen.Add(track)) continue;
                m3uCandidates.Add(track);
                trackAlbumHint[track] = entry.AlbumHint;
                added++;
            }
            if (added > 0) m3uFileCount++;
        }

        // Index-based candidates: filter by path only (no I/O)
        // Note: include/exclude filters apply to M3U selection above. Tracks discovered
        // directly from the audio index are scoped by baseDir/recursive only â€” the
        // include filter controls which M3U playlists contribute tracks, not the
        // physical location of the audio files themselves.
        var indexCandidates = new List<string>();
        foreach (var mf in index)
        {
            var f = mf.FullPath;
            // Must be under baseDir (respect recursive flag)
            if (!f.StartsWith(baseDirSlash, StringComparison.OrdinalIgnoreCase)) continue;
            if (!recursive && f.IndexOf(Path.DirectorySeparatorChar, baseDirSlash.Length) >= 0) continue;
            if (!_musicAudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
            if (!seen.Add(f)) continue; // already from an M3U

            // When an include filter is active, only include direct-index tracks when they
            // live inside a folder whose path matches the filter â€” otherwise the user would
            // get unrelated tracks mixed in alongside their M3U-scoped results.
            if (include.Length > 0)
            {
                var dir = Path.GetDirectoryName(f) ?? string.Empty;
                var dirRel = dir.Length > baseDirSlash.Length ? dir[baseDirSlash.Length..] : string.Empty;
                if (!include.Any(p => dirRel.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
            }
            if (exclude.Length > 0)
            {
                var dir = Path.GetDirectoryName(f) ?? string.Empty;
                var dirRel = dir.Length > baseDirSlash.Length ? dir[baseDirSlash.Length..] : string.Empty;
                if (exclude.Any(p => dirRel.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
            }

            indexCandidates.Add(f);
        }

        Logger.Info("DynamicExpand", $"Candidates: {indexCandidates.Count} from index + {m3uCandidates.Count} from {m3uFileCount} M3U(s)");

        // â”€â”€ Phase 2: order, lazy-filter, pick â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var rng = new Random();

        IEnumerable<string> OrderCandidates(List<string> src) => sort switch
        {
            "newest"       => src.OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc),
            "oldest"       => src.OrderBy(f => new FileInfo(f).LastWriteTimeUtc),
            "alphabetical" => src.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase),
            _              => src.OrderBy(_ => rng.Next()),   // random: shuffle in-place via LINQ key
        };

        // For random mode we want a real Fisher-Yates shuffle so we avoid enumerating the full list
        // (LINQ OrderBy on a random key materialises everything). Shuffle each bucket separately.
        static void FisherYates(List<string> list, Random r)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = r.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        IEnumerable<string> orderedCandidates;
        if (sort == "random")
        {
            FisherYates(indexCandidates, rng);
            FisherYates(m3uCandidates, rng);
            // Interleave: yield index and m3u candidates alternately so both sources are represented
            orderedCandidates = indexCandidates.Concat(m3uCandidates);
        }
        else
        {
            orderedCandidates = OrderCandidates(indexCandidates).Concat(OrderCandidates(m3uCandidates));
        }

        // Build a fast pathâ†’MusicFile lookup for in-memory genre/year checks (covers index candidates;
        // M3U-sourced candidates will fall back to a tag read only if not found in the index).
        Dictionary<string, MusicFile>? indexLookup = null;
        if (needsTagFilter)
        {
            indexLookup = new Dictionary<string, MusicFile>(index.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var mf in index)
                indexLookup.TryAdd(mf.FullPath, mf);
        }

        int need = mode == "all" ? int.MaxValue : Math.Max(1, count);
        var picked = new List<string>(Math.Min(need, 200));

        foreach (var f in orderedCandidates)
        {
            if (picked.Count >= need) break;

            // Genre/year filter: use cached metadata from the index when available, otherwise read tags once.
            if (needsTagFilter)
            {
                string? trackGenre = null;
                uint trackYear = 0;

                if (indexLookup!.TryGetValue(f, out var cachedMf) && (cachedMf.Genre != null || cachedMf.Year > 0))
                {
                    // Data already in memory â€” zero I/O
                    trackGenre = cachedMf.Genre;
                    trackYear = cachedMf.Year;
                }
                else
                {
                    // Fall back to TagLib only for M3U-sourced files not yet in the index
                    try
                    {
                        using var tagFile = TagLib.File.Create(f);
                        trackGenre = tagFile.Tag.FirstGenre;
                        trackYear = tagFile.Tag.Year;
                    }
                    catch { /* unreadable tags â€” include the track */ }
                }

                if (!string.IsNullOrWhiteSpace(genre) && (trackGenre == null || !trackGenre.Contains(genre, StringComparison.OrdinalIgnoreCase))) continue;
                if (yearFrom.HasValue && trackYear > 0 && trackYear < (uint)yearFrom.Value) continue;
                if (yearTo.HasValue   && trackYear > 0 && trackYear > (uint)yearTo.Value)   continue;
            }

            picked.Add(f);
        }

        Logger.Info("DynamicExpand", $"Picked {picked.Count} track(s) (mode={mode}, sort={sort}):");
        foreach (var p in picked)
            Logger.Info("DynamicExpand", $"  [{(trackAlbumHint.ContainsKey(p) ? "M3U" : "DIRECT")}] {Path.GetFileName(p)}");

        var tracks = picked.Select(f =>
        {
            var hint = trackAlbumHint.TryGetValue(f, out var h) ? h : null;
            return (object)BuildMusicFileObj(new MusicFile(Path.GetFileNameWithoutExtension(f), f), hint);
        }).ToArray();

        // Write lastExpanded timestamp and lastCount back to the .rpDynamic file (best-effort)
        try
        {
            using var doc2 = JsonDocument.Parse(settingsJson);
            var dict = doc2.RootElement.EnumerateObject()
                .Where(prop => prop.Name != "lastExpanded" && prop.Name != "lastCount")
                .ToDictionary(prop => prop.Name, prop => (object?)prop.Value.Clone());
            dict["lastExpanded"] = DateTime.UtcNow.ToString("o");
            dict["lastCount"] = tracks.Length;
            File.WriteAllText(filePath, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch { /* best-effort */ }

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { name = displayName, tracks, total = tracks.Length }));
    }

    private void HandleMusicSearch(HttpListenerContext ctx)
    {
        var query = (ctx.Request.QueryString["q"] ?? string.Empty).Trim();
        if (_musicIndex.Length == 0 && !_isMusicIndexing)
            StartMusicIndexRefresh();

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var offset = ReadNonNegativeInt(ctx.Request.QueryString["offset"]);
        var limit = ReadPositiveInt(ctx.Request.QueryString["limit"], _config.EffectiveLibraryPageSize, 1000);

        var matching = terms.Length == 0
            ? []
            : _musicIndex
                .Where(f => terms.All(t =>
                    f.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                    (Path.GetFileName(Path.GetDirectoryName(f.FullPath)) ?? string.Empty).Contains(t, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => f.Name)
                .ToArray();

        var files = matching
            .Skip(offset)
            .Take(limit)
            .Select(f => (object)BuildMusicFileObj(f))
            .ToArray();

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new
        {
            files,
            total = matching.Length,
            offset,
            limit,
            hasMore = offset + files.Length < matching.Length,
            indexedFiles = _musicIndex.Length,
            indexing = _isMusicIndexing,
            enriching = _isMusicEnriching
        }));
    }


    private static object BuildMusicFileObj(MusicFile f, string? albumHint = null)
    {
        var dir    = Path.GetDirectoryName(f.FullPath) ?? string.Empty;
        var ext    = Path.GetExtension(f.FullPath).TrimStart('.').ToLowerInvariant();

        // Fallback values from path structure; albumHint (e.g. from M3U filename) takes
        // priority over the raw folder name when no ID3 album tag is present.
        string album    = albumHint ?? Path.GetFileName(dir);
        string artist   = Path.GetFileName(Path.GetDirectoryName(dir)) ?? string.Empty;
        string tagTitle = string.Empty;
        int?   trackNum = null;
        int?   durationSec = null;
        string genre = string.Empty;
        int?   year  = null;
        bool   hasCover = false;

        // Try to read embedded ID3/Vorbis/etc. tags
        try
        {
            using var tfile = TagLib.File.Create(f.FullPath);
            var tag = tfile.Tag;

            if (!string.IsNullOrWhiteSpace(tag.Title))   tagTitle = tag.Title;
            if (!string.IsNullOrWhiteSpace(tag.Album))   album  = tag.Album;
            var performers = tag.AlbumArtists.Length > 0 ? tag.AlbumArtists : tag.Performers;
            if (performers.Length > 0 && !string.IsNullOrWhiteSpace(performers[0]))
                artist = performers[0];
            if (tag.Track > 0)  trackNum = (int)tag.Track;
            if (tag.Year > 0)   year     = (int)tag.Year;
            if (tag.Genres.Length > 0 && !string.IsNullOrWhiteSpace(tag.Genres[0]))
                genre = tag.Genres[0];
            hasCover = tfile.Tag.Pictures.Length > 0
                    || FolderHasCoverImage(dir);
            durationSec = (int)Math.Round(tfile.Properties.Duration.TotalSeconds);
        }
        catch
        {
            // TagLib can't read this file â€” fall back to path-based values
            var tnMatch = System.Text.RegularExpressions.Regex.Match(f.Name, @"^(\d{1,3})[.\s\-]");
            if (tnMatch.Success && int.TryParse(tnMatch.Groups[1].Value, out var tn))
                trackNum = tn;
            hasCover = FolderHasCoverImage(dir);
        }

        return new
        {
            name        = f.Name,
            tagTitle,
            path        = WebPathHelpers.EncodePath(f.FullPath),
            folder      = Path.GetFileName(dir),
            ext,
            album,
            artist,
            trackNum,
            durationSec,
            genre,
            year,
            hasCover
        };
    }

    private static readonly string[] _coverNames =
        ["cover.jpg", "cover.png", "folder.jpg", "folder.png", "front.jpg", "front.png", "album.jpg", "album.png"];

    private static bool FolderHasCoverImage(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        foreach (var name in _coverNames)
            if (System.IO.File.Exists(Path.Combine(dir, name))) return true;
        return false;
    }

    private void HandleMusicCover(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        { TrySendResponse(ctx, 400, "text/plain", "missing path"); return; }

        string fullPath;
        try   { fullPath = WebPathHelpers.DecodePath(encodedPath); }
        catch { TrySendResponse(ctx, 400, "text/plain", "bad path"); return; }

        var musicRoot = _config.ResolvedMusicPath;
        if (!WebPathHelpers.IsUnderRoot(fullPath, musicRoot))
        { TrySendResponse(ctx, 403, "text/plain", "forbidden"); return; }

        // 1. Try embedded picture
        try
        {
            using var tfile = TagLib.File.Create(fullPath);
            var pic = tfile.Tag.Pictures.FirstOrDefault();
            if (pic is not null)
            {
                ctx.Response.ContentType = string.IsNullOrEmpty(pic.MimeType) ? "image/jpeg" : pic.MimeType;
                ctx.Response.ContentLength64 = pic.Data.Data.Length;
                ctx.Response.OutputStream.Write(pic.Data.Data, 0, pic.Data.Data.Length);
                ctx.Response.OutputStream.Close();
                return;
            }
        }
        catch { /* fall through to folder art */ }

        // 2. Try folder cover image
        var dir = Path.GetDirectoryName(fullPath) ?? string.Empty;
        foreach (var name in _coverNames)
        {
            var candidate = Path.Combine(dir, name);
            if (!System.IO.File.Exists(candidate)) continue;
            var mime = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            try
            {
                var bytes = System.IO.File.ReadAllBytes(candidate);
                ctx.Response.ContentType = mime;
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
                return;
            }
            catch { }
        }

        TrySendResponse(ctx, 404, "text/plain", "no cover");
    }

    // â”€â”€ Album Art Cache â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // In-memory: null = confirmed-miss, byte[] = image bytes (already on disk too)
    // Disk:      %AppData%\RemotePlay\AlbumArtCache\<safeKey>.jpg
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private static readonly Dictionary<string, byte[]?> _albumArtCache = new(StringComparer.OrdinalIgnoreCase);
    // On first use, purge stale zero-byte miss-sentinels written by older logic (e.g. before release-group fallback)
    private static int _albumArtMissPurged = 0;
    private static void PurgeStaleAlbumArtMisses()
    {
        if (Interlocked.Exchange(ref _albumArtMissPurged, 1) != 0) return;
        try
        {
            var dir = Path.Combine(AppPaths.UserDataDirectory, "AlbumArtCache");
            if (!Directory.Exists(dir)) return;
            int count = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*.jpg"))
            {
                if (new FileInfo(f).Length == 0) { File.Delete(f); count++; }
            }
            if (count > 0) Logger.Detail("AlbumArt", $"Purged {count} stale miss-sentinel(s) from disk cache");
        }
        catch (Exception ex)
        {
            Logger.Warning("AlbumArt", $"Miss-sentinel purge failed: {ex.Message}");
        }
    }
    private static readonly SemaphoreSlim _albumArtLock = new(1, 1);
    // In-flight deduplication: if N requests arrive for the same key simultaneously,
    // only one fetch runs; the rest await the same Task.
    private static readonly Dictionary<string, Task<byte[]?>> _albumArtInflight = new(StringComparer.OrdinalIgnoreCase);
    // MusicBrainz rate-limit: max 1 request per second
    private static readonly SemaphoreSlim _mbRateLimit = new(1, 1);
    private static long _mbLastRequestTick = 0;
    private static readonly System.Net.Http.HttpClient _albumArtHttpClient = CreateAlbumArtHttpClient();

    private static string AlbumArtCacheDir =>
        Path.Combine(AppPaths.UserDataDirectory, "AlbumArtCache");

    private static System.Net.Http.HttpClient CreateAlbumArtHttpClient()
    {
        var client = new System.Net.Http.HttpClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePlay/1.0 (album-art-lookup)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>Returns a filesystem-safe filename for a cache key.</summary>
    private static string AlbumArtCacheFile(string cacheKey)
    {
        // Replace characters that are invalid in filenames
        var safe = string.Concat(cacheKey.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(AlbumArtCacheDir, safe + ".jpg");
    }

    // â”€â”€ Album art export / import â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Accepts a JPEG body and stores it in the local AlbumArtCache under the provided key.
    /// Called by a remote server's export push. No auth required beyond network access.
    /// Query params: key (the cache key, e.g. "artist|album")
    /// </summary>
    // -----------------------------------------------------------------------
    // Lyric offsets persistence (GET = load, POST = save)
    // -----------------------------------------------------------------------

    /// <summary>
    /// GET  /api/music/lyrics/offsets  â€” returns the stored offset map as JSON.
    /// POST /api/music/lyrics/offsets  â€” accepts a JSON object and writes it to disk.
    /// </summary>
    private static async Task HandleMusicLyricsOffsetsAsync(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            var path = AppPaths.LyricOffsetsFile;
            if (!File.Exists(path))
            {
                TrySendResponse(ctx, 200, "application/json", "{}");
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                TrySendResponse(ctx, 200, "application/json", json);
            }
            catch (Exception ex)
            {
                Logger.Warning("Lyrics", $"Failed reading offsets file: {ex.Message}");
                TrySendResponse(ctx, 500, "application/json", "{\"error\":\"read failed\"}");
            }

            return;
        }

        if (ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var sr = new System.IO.StreamReader(ctx.Request.InputStream);
                var body = await sr.ReadToEndAsync().ConfigureAwait(false);

                // Validate it is parseable JSON before writing
                using var _ = JsonDocument.Parse(body);

                await File.WriteAllTextAsync(AppPaths.LyricOffsetsFile, body).ConfigureAwait(false);
                TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
            }
            catch (JsonException)
            {
                TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid JSON\"}");
            }
            catch (Exception ex)
            {
                Logger.Warning("Lyrics", $"Failed writing offsets file: {ex.Message}");
                TrySendResponse(ctx, 500, "application/json", "{\"error\":\"write failed\"}");
            }

            return;
        }

        TrySendResponse(ctx, 405, "application/json", "{\"error\":\"method not allowed\"}");
    }

    /// <summary>
    /// Server-side relay: reads offsets from the request body and POSTs them to targetUrl/api/music/lyrics/offsets.
    /// Body (JSON): { "targetUrl": "http://host:port", "offsets": {...} }
    /// This avoids browser-to-peer cross-origin issues by routing the request through the local server.
    /// </summary>
    private static async Task HandleMusicLyricsOffsetsExportAsync(HttpListenerContext ctx)
    {
        if (!ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            TrySendResponse(ctx, 405, "application/json", "{\"error\":\"method not allowed\"}");
            return;
        }

        string targetUrl;
        JsonElement offsets;
        try
        {
            using var sr = new System.IO.StreamReader(ctx.Request.InputStream);
            var body = await sr.ReadToEndAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            targetUrl = (doc.RootElement.TryGetProperty("targetUrl", out var tu) ? tu.GetString() : null) ?? string.Empty;
            offsets = doc.RootElement.TryGetProperty("offsets", out var o) ? o.Clone() : default;
        }
        catch
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid JSON body\"}");
            return;
        }

        targetUrl = targetUrl.TrimEnd('/');
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri)
            || (targetUri.Scheme != "http" && targetUri.Scheme != "https"))
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid targetUrl\"}");
            return;
        }

        if (offsets.ValueKind != JsonValueKind.Object)
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"offsets must be a JSON object\"}");
            return;
        }

        var count = 0;
        foreach (var _ in offsets.EnumerateObject()) count++;
        if (count == 0)
        {
            TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true, pushed = 0 }));
            return;
        }

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePlay/1.0 (offsets-export)");
            var json = offsets.GetRawText();
            using var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"{targetUrl}/api/music/lyrics/offsets", content).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                Logger.Info("Lyrics", $"Offsets export: pushed {count} offset(s) to {targetUrl}");
                TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true, pushed = count }));
            }
            else
            {
                var errBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Logger.Warning("Lyrics", $"Offsets export: peer returned HTTP {(int)resp.StatusCode}: {errBody.Trim()}");
                TrySendResponse(ctx, 200, "application/json",
                    JsonSerializer.Serialize(new { ok = false, error = $"Peer returned HTTP {(int)resp.StatusCode}" }));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Lyrics", $"Offsets export failed: {ex.Message}");
            TrySendResponse(ctx, 500, "application/json", JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
        }
    }

    // -----------------------------------------------------------------------
    // Lyric cache file export / import (peer-sync)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Pushes every .json file in the lyrics cache to the target server's
    /// /api/music/lyrics/import endpoint, skipping files that are byte-for-byte identical
    /// (compared by file size; the receiver checks the ETag header).
    /// Body (JSON): { "targetUrl": "http://host:port" }
    /// </summary>
    private static async Task HandleMusicLyricsExportAsync(HttpListenerContext ctx)
    {
        string targetUrl;
        try
        {
            using var sr = new System.IO.StreamReader(ctx.Request.InputStream);
            var body = await sr.ReadToEndAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            targetUrl = (doc.RootElement.TryGetProperty("targetUrl", out var el)
                ? el.GetString() : null) ?? string.Empty;
        }
        catch
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid JSON body\"}");
            return;
        }

        targetUrl = targetUrl.TrimEnd('/');
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri)
            || (targetUri.Scheme != "http" && targetUri.Scheme != "https"))
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid targetUrl\"}");
            return;
        }

        var cacheDir = AppPaths.LyricsCacheDirectory;
        if (!Directory.Exists(cacheDir))
        {
            TrySendResponse(ctx, 200, "application/json",
                JsonSerializer.Serialize(new { ok = true, sent = 0, skipped = 0, failed = 0, total = 0 }));
            return;
        }

        var files = Directory.EnumerateFiles(cacheDir, "*.json")
            .Where(f => new FileInfo(f).Length > 0)
            .ToArray();

        Logger.Info("Lyrics", $"Export: pushing {files.Length} cached lyric files to {targetUrl}");

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePlay/1.0 (lyrics-export)");

        int sent = 0, skipped = 0, failed = 0;
        var importBase = $"{targetUrl}/api/music/lyrics/import";
        var failReasons = new System.Collections.Generic.List<string>();
        const int InterRequestDelayMs = 30;
        const int MaxRetries = 3;

        // First, ask the target which files it already has and their sizes so we can skip identical ones.
        System.Collections.Generic.Dictionary<string, long>? remoteIndex = null;
        try
        {
            var idxRes = await http.GetAsync($"{targetUrl}/api/music/lyrics/import?index=1").ConfigureAwait(false);
            if (idxRes.IsSuccessStatusCode)
            {
                var idxJson = await idxRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var idxDoc = JsonDocument.Parse(idxJson);
                remoteIndex = new System.Collections.Generic.Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in idxDoc.RootElement.EnumerateObject())
                    remoteIndex[prop.Name] = prop.Value.GetInt64();
            }
        }
        catch { /* remote may not support the index endpoint yet; proceed without skipping */ }

        foreach (var file in files)
        {
            var key = Path.GetFileNameWithoutExtension(file);
            try
            {
                var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);

                // Skip if the remote already has a file with the same size (byte-identical proxy).
                if (remoteIndex is not null
                    && remoteIndex.TryGetValue(key, out var remoteSize)
                    && remoteSize == bytes.LongLength)
                {
                    skipped++;
                    continue;
                }

                var url = $"{importBase}?key={Uri.EscapeDataString(key)}";

                System.Net.Http.HttpResponseMessage resp;
                int retryDelayMs = 2000;
                int attempt = 0;
                while (true)
                {
                    using var content = new System.Net.Http.ByteArrayContent(bytes);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    resp = await http.PostAsync(url, content).ConfigureAwait(false);

                    if ((int)resp.StatusCode != 429 || attempt >= MaxRetries)
                        break;

                    if (resp.Headers.RetryAfter?.Delta is { } delta)
                        retryDelayMs = (int)delta.TotalMilliseconds + 200;

                    Logger.Warning("Lyrics", $"Export: 429 for '{key}', retrying in {retryDelayMs}ms (attempt {attempt + 1}/{MaxRetries})");
                    await Task.Delay(retryDelayMs).ConfigureAwait(false);
                    retryDelayMs = Math.Min(retryDelayMs * 2, 30_000);
                    attempt++;
                }

                if (resp.IsSuccessStatusCode)
                {
                    sent++;
                }
                else
                {
                    var reason = $"HTTP {(int)resp.StatusCode} for '{key}': {(await resp.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim()}";
                    Logger.Warning("Lyrics", $"Export: {reason}");
                    if (failReasons.Count < 10) failReasons.Add(reason);
                    failed++;
                }
            }
            catch (Exception ex)
            {
                var reason = $"Exception for '{key}': {ex.Message}";
                Logger.Warning("Lyrics", $"Export: {reason}");
                if (failReasons.Count < 10) failReasons.Add(reason);
                failed++;
            }

            await Task.Delay(InterRequestDelayMs).ConfigureAwait(false);
        }

        Logger.Info("Lyrics", $"Export complete: sent={sent} skipped={skipped} failed={failed} total={files.Length}");
        TrySendResponse(ctx, 200, "application/json",
            JsonSerializer.Serialize(new { ok = true, sent, skipped, failed, total = files.Length, failSamples = failReasons }));
    }

    /// <summary>
    /// Receives a lyrics cache file from another instance.
    /// GET  ?index=1  â€” returns a JSON object {filename: fileSize} for diff-checking.
    /// POST ?key=...  â€” writes the body as a .json file in the lyrics cache directory.
    /// </summary>
    private static async Task HandleMusicLyricsImportAsync(HttpListenerContext ctx)
    {
        // Index request: return a map of filename â†’ file size so exporter can skip identical items.
        if (ctx.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase)
            && ctx.Request.QueryString["index"] == "1")
        {
            var cacheDir = AppPaths.LyricsCacheDirectory;
            var index = new System.Collections.Generic.Dictionary<string, long>();
            if (Directory.Exists(cacheDir))
            {
                foreach (var f in Directory.EnumerateFiles(cacheDir, "*.json"))
                    index[Path.GetFileNameWithoutExtension(f)] = new FileInfo(f).Length;
            }

            TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(index));
            return;
        }

        var key = (ctx.Request.QueryString["key"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(key))
        { TrySendResponse(ctx, 400, "application/json", "{\"error\":\"missing key\"}"); return; }

        if (ctx.Request.ContentLength64 <= 0 || ctx.Request.ContentLength64 > 2 * 1024 * 1024)
        { TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid content length\"}"); return; }

        try
        {
            using var ms = new System.IO.MemoryStream();
            await ctx.Request.InputStream.CopyToAsync(ms).ConfigureAwait(false);
            var bytes = ms.ToArray();

            if (bytes.Length == 0)
            { TrySendResponse(ctx, 400, "application/json", "{\"error\":\"empty body\"}"); return; }

            // Strip UTF-8 BOM if present (older cache files may have been written with BOM)
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                bytes = bytes[3..];

            // Basic validation: must be JSON
            using var _ = JsonDocument.Parse(bytes);

            Directory.CreateDirectory(AppPaths.LyricsCacheDirectory);
            var destFile = Path.Combine(AppPaths.LyricsCacheDirectory, key + ".json");
            await File.WriteAllBytesAsync(destFile, bytes).ConfigureAwait(false);

            Logger.Detail("Lyrics", $"Import: stored '{key}' ({bytes.Length} bytes)");
            TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
        }
        catch (JsonException)
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"not valid JSON\"}");
        }
        catch (Exception ex)
        {
            Logger.Warning("Lyrics", $"Import failed for key '{key}': {ex.Message}");
            TrySendResponse(ctx, 500, "application/json", "{\"error\":\"write failed\"}");
        }
    }

    private static async Task HandleMusicAlbumArtImportAsync(HttpListenerContext ctx)
    {
        var key = (ctx.Request.QueryString["key"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(key))
        { TrySendResponse(ctx, 400, "application/json", "{\"error\":\"missing key\"}"); return; }

        if (ctx.Request.ContentLength64 <= 0 || ctx.Request.ContentLength64 > 5 * 1024 * 1024)
        { TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid content length\"}"); return; }

        try
        {
            using var ms = new System.IO.MemoryStream();
            await ctx.Request.InputStream.CopyToAsync(ms).ConfigureAwait(false);
            var bytes = ms.ToArray();

            if (bytes.Length == 0)
            { TrySendResponse(ctx, 400, "application/json", "{\"error\":\"empty body\"}"); return; }

            // Validate it looks like a JPEG (FF D8 FF)
            if (bytes.Length < 3 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF)
            { TrySendResponse(ctx, 400, "application/json", "{\"error\":\"not a JPEG\"}"); return; }

            Directory.CreateDirectory(AlbumArtCacheDir);
            var destFile = AlbumArtCacheFile(key);
            await File.WriteAllBytesAsync(destFile, bytes).ConfigureAwait(false);

            // Update in-memory cache so it's immediately available
            lock (_albumArtCache) _albumArtCache[key] = bytes;

            Logger.Detail("AlbumArt", $"Import: stored '{key}' ({bytes.Length} bytes)");
            TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
        }
        catch (Exception ex)
        {
            Logger.Warning("AlbumArt", $"Import failed for key '{key}': {ex.Message}");
            TrySendResponse(ctx, 500, "application/json", "{\"error\":\"write failed\"}");
        }
    }

    /// <summary>
    /// Iterates AlbumArtCache on disk and pushes every non-empty JPEG to the target server's
    /// /api/music/album-art/import endpoint. Returns a JSON summary with totals.
    /// Body (JSON): { "targetUrl": "http://host:port" }
    /// </summary>
    private static async Task HandleMusicAlbumArtExportAsync(HttpListenerContext ctx)
    {
        string targetUrl;
        try
        {
            using var sr = new System.IO.StreamReader(ctx.Request.InputStream);
            var body = await sr.ReadToEndAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            targetUrl = (doc.RootElement.TryGetProperty("targetUrl", out var el)
                ? el.GetString() : null) ?? string.Empty;
        }
        catch
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid JSON body\"}");
            return;
        }

        targetUrl = targetUrl.TrimEnd('/');
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri)
            || (targetUri.Scheme != "http" && targetUri.Scheme != "https"))
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid targetUrl\"}");
            return;
        }

        if (!Directory.Exists(AlbumArtCacheDir))
        {
            TrySendResponse(ctx, 200, "application/json",
                JsonSerializer.Serialize(new { ok = true, sent = 0, skipped = 0, failed = 0, total = 0 }));
            return;
        }

        var files = Directory.EnumerateFiles(AlbumArtCacheDir, "*.jpg")
            .Where(f => new FileInfo(f).Length > 0)
            .ToArray();

        Logger.Info("AlbumArt", $"Export: pushing {files.Length} cached images to {targetUrl}");

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePlay/1.0 (album-art-export)");

        int sent = 0, skipped = 0, failed = 0;
        var importBase = $"{targetUrl}/api/music/album-art/import";
        var failReasons = new System.Collections.Generic.List<string>();
        // Throttle to ~20 req/s to stay well under the target's default 300-req/10-s rate limit.
        const int InterRequestDelayMs = 50;
        const int MaxRetries          = 3;

        foreach (var file in files)
        {
            // Derive the original cache key from the filename (strip .jpg extension)
            var fileName = Path.GetFileNameWithoutExtension(file);
            try
            {
                var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                if (bytes.Length < 3 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF)
                { skipped++; continue; }

                var url = $"{importBase}?key={Uri.EscapeDataString(fileName)}";

                System.Net.Http.HttpResponseMessage resp;
                int retryDelayMs = 2000;
                int attempt = 0;
                while (true)
                {
                    using var content = new System.Net.Http.ByteArrayContent(bytes);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                    resp = await http.PostAsync(url, content).ConfigureAwait(false);

                    if ((int)resp.StatusCode != 429 || attempt >= MaxRetries)
                        break;

                    // Honour Retry-After if present, otherwise use exponential back-off.
                    if (resp.Headers.RetryAfter?.Delta is { } delta)
                        retryDelayMs = (int)delta.TotalMilliseconds + 200;

                    Logger.Warning("AlbumArt", $"Export: 429 for '{fileName}', retrying in {retryDelayMs}ms (attempt {attempt + 1}/{MaxRetries})");
                    await Task.Delay(retryDelayMs).ConfigureAwait(false);
                    retryDelayMs = Math.Min(retryDelayMs * 2, 30_000);
                    attempt++;
                }

                if (resp.IsSuccessStatusCode)
                {
                    sent++;
                }
                else
                {
                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var reason = $"HTTP {(int)resp.StatusCode} for '{fileName}': {body.Trim()}";
                    Logger.Warning("AlbumArt", $"Export: {reason}");
                    if (failReasons.Count < 10) failReasons.Add(reason);
                    failed++;
                }
            }
            catch (Exception ex)
            {
                var reason = $"Exception for '{fileName}': {ex.Message}";
                Logger.Warning("AlbumArt", $"Export: {reason}");
                if (failReasons.Count < 10) failReasons.Add(reason);
                failed++;
            }

            await Task.Delay(InterRequestDelayMs).ConfigureAwait(false);
        }

        Logger.Info("AlbumArt", $"Export complete: sent={sent} skipped={skipped} failed={failed} total={files.Length}");
        TrySendResponse(ctx, 200, "application/json",
            JsonSerializer.Serialize(new { ok = true, sent, skipped, failed, total = files.Length, failSamples = failReasons }));
    }

    /// <summary>Proxy-fetches album art from MusicBrainz/CAA. Cached in memory + on disk.</summary>
    private async Task HandleMusicAlbumArtAsync(HttpListenerContext ctx)
    {
        // One-time purge of zero-byte miss-sentinels written before the release-group fallback was added
        PurgeStaleAlbumArtMisses();

        var album  = (ctx.Request.QueryString["album"]  ?? string.Empty).Trim();
        var artist = (ctx.Request.QueryString["artist"] ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(album))
        { TrySendResponse(ctx, 400, "text/plain", "missing album"); return; }

        // Cap the wait time so a navigating-away client doesn't hold the queue open indefinitely.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = cts.Token;

        var cacheKey  = $"{artist}|{album}".ToLowerInvariant();
        var cacheFile = AlbumArtCacheFile(cacheKey);

        // 1. Fast path â€” in-memory cache
        await _albumArtLock.WaitAsync(ct).ConfigureAwait(false);
        bool hasCached = _albumArtCache.TryGetValue(cacheKey, out var cached);
        _albumArtLock.Release();

        if (hasCached)
        {
            if (cached is null) { TrySendResponse(ctx, 404, "text/plain", "not found"); return; }
            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.ContentLength64 = cached.Length;
            try
            {
                await ctx.Response.OutputStream.WriteAsync(cached, ct).ConfigureAwait(false);
                ctx.Response.OutputStream.Close();
            }
            catch (OperationCanceledException) { }
            catch (System.Net.HttpListenerException) { }  // client disconnected
            return;
        }

        // 2. Disk cache â€” load without going to the network
        if (File.Exists(cacheFile))
        {
            try
            {
                var diskBytes = await File.ReadAllBytesAsync(cacheFile, ct).ConfigureAwait(false);
                if (diskBytes.Length > 0)
                {
                    await _albumArtLock.WaitAsync(ct).ConfigureAwait(false);
                    _albumArtCache[cacheKey] = diskBytes;
                    _albumArtLock.Release();
                    Logger.Detail("AlbumArt", $"Served from disk cache: '{cacheKey}'");
                    ctx.Response.ContentType = "image/jpeg";
                    ctx.Response.ContentLength64 = diskBytes.Length;
                    try
                    {
                        await ctx.Response.OutputStream.WriteAsync(diskBytes, ct).ConfigureAwait(false);
                        ctx.Response.OutputStream.Close();
                    }
                    catch (OperationCanceledException) { }
                    catch (System.Net.HttpListenerException) { }  // client disconnected
                    return;
                }
                // Zero-byte file = persisted miss â€” don't hit network again
                await _albumArtLock.WaitAsync(ct).ConfigureAwait(false);
                _albumArtCache[cacheKey] = null;
                _albumArtLock.Release();
                TrySendResponse(ctx, 404, "text/plain", "not found");
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Logger.Warning("AlbumArt", $"Failed to read disk cache '{cacheFile}': {ex.Message}");
            }
        }

        // 3. Fetch from MusicBrainz / Cover Art Archive.
        //    In-flight deduplication: if another request is already fetching this key, await its
        //    Task instead of firing a second network fetch.  This is the primary fix for the hang
        //    when many playlist cards load at the same time â€” only one outbound chain runs per key.
        Task<byte[]?> fetchTask;
        bool isOwner = false;
        await _albumArtLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_albumArtInflight.TryGetValue(cacheKey, out var existing))
            {
                fetchTask = existing;   // join the already-running fetch
            }
            else
            {
                fetchTask = FetchAndCacheAlbumArtAsync(artist, album, cacheKey, cacheFile);
                _albumArtInflight[cacheKey] = fetchTask;
                isOwner = true;
            }
        }
        finally
        {
            _albumArtLock.Release();
        }

        byte[]? imageBytes;
        try
        {
            imageBytes = await fetchTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;  // client navigated away â€” drop silently
        }
        finally
        {
            // Owner cleans up the inflight entry once the task finishes
            if (isOwner)
            {
                await _albumArtLock.WaitAsync().ConfigureAwait(false);
                _albumArtInflight.Remove(cacheKey);
                _albumArtLock.Release();
            }
        }

        if (imageBytes is null) { TrySendResponse(ctx, 404, "text/plain", "not found"); return; }

        ctx.Response.ContentType = "image/jpeg";
        ctx.Response.ContentLength64 = imageBytes.Length;
        try
        {
            await ctx.Response.OutputStream.WriteAsync(imageBytes, ct).ConfigureAwait(false);
            ctx.Response.OutputStream.Close();
        }
        catch (OperationCanceledException) { }
        catch (System.Net.HttpListenerException) { }  // client disconnected
    }

    /// <summary>
    /// Performs the actual MusicBrainz/CAA network fetch and persists the result to disk and
    /// in-memory cache.  Extracted so multiple concurrent requests for the same key can share
    /// a single Task via <c>_albumArtInflight</c>.
    /// </summary>
    private static async Task<byte[]?> FetchAndCacheAlbumArtAsync(
        string artist, string album, string cacheKey, string cacheFile)
    {
        byte[]? imageBytes = null;
        bool fetchWasTransient = false;
        try
        {
            imageBytes = await FetchAlbumArtFromMusicBrainzAsync(artist, album).ConfigureAwait(false);
        }
        catch (AlbumArtTransientException ex)
        {
            Logger.Warning("AlbumArt", $"Fetch failed (transient) for '{artist} - {album}': {ex.Message}");
            fetchWasTransient = true;
        }
        catch (Exception ex)
        {
            Logger.Warning("AlbumArt", $"Fetch failed for '{artist} - {album}': {ex.Message}");
        }

        // Persist to disk
        //   image found        â†’ write bytes (positive hit)
        //   permanent miss     â†’ write 0-byte sentinel so we don't retry
        //   transient failure  â†’ don't write; allow retry on next request
        if (imageBytes is not null)
        {
            try
            {
                Directory.CreateDirectory(AlbumArtCacheDir);
                await File.WriteAllBytesAsync(cacheFile, imageBytes).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warning("AlbumArt", $"Failed to write disk cache '{cacheFile}': {ex.Message}");
            }
        }
        else if (!fetchWasTransient)
        {
            try
            {
                Directory.CreateDirectory(AlbumArtCacheDir);
                await File.WriteAllBytesAsync(cacheFile, []).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warning("AlbumArt", $"Failed to write miss sentinel '{cacheFile}': {ex.Message}");
            }
        }

        // Update in-memory cache (don't cache transient failures)
        if (!fetchWasTransient)
        {
            await _albumArtLock.WaitAsync().ConfigureAwait(false);
            _albumArtCache[cacheKey] = imageBytes;
            _albumArtLock.Release();
        }

        return imageBytes;
    }

    /// <summary>Thrown when the failure is transient (rate-limit/timeout) â€” must not be persisted as a miss sentinel.</summary>
    private sealed class AlbumArtTransientException(string message) : Exception(message) { }

    /// <summary>Enforces MusicBrainz 1-request-per-second policy before each HTTP call.</summary>
    private static async Task ThrottleMusicBrainzAsync(CancellationToken ct = default)
    {
        await _mbRateLimit.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now   = Environment.TickCount64;
            var delta = now - Interlocked.Read(ref _mbLastRequestTick);
            if (delta >= 0 && delta < 1100)
                await Task.Delay((int)(1100 - delta), ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _mbLastRequestTick, Environment.TickCount64);
        }
        finally
        {
            _mbRateLimit.Release();
        }
    }

    private static async Task<byte[]?> FetchAlbumArtFromMusicBrainzAsync(string artist, string album,
        CancellationToken ct = default)
    {
        // Step 1: Search MusicBrainz for matching releases (up to 5), including release-group ids
        var searchTerms = string.IsNullOrWhiteSpace(artist)
            ? Uri.EscapeDataString($"release:\"{album}\"")
            : Uri.EscapeDataString($"release:\"{album}\" AND artist:\"{artist}\"");

        var searchUrl = $"https://musicbrainz.org/ws/2/release/?query={searchTerms}&limit=5&fmt=json&inc=release-groups";
        Logger.Detail("AlbumArt", $"MusicBrainz search: {searchUrl}");

        await ThrottleMusicBrainzAsync(ct).ConfigureAwait(false);

        System.Net.Http.HttpResponseMessage searchResponse;
        try
        {
            searchResponse = await _albumArtHttpClient.GetAsync(searchUrl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new AlbumArtTransientException($"MB search request failed: {ex.Message}");
        }

        if ((int)searchResponse.StatusCode == 503 || (int)searchResponse.StatusCode == 429)
            throw new AlbumArtTransientException($"MB search returned {(int)searchResponse.StatusCode}");

        if (!searchResponse.IsSuccessStatusCode)
        {
            Logger.Detail("AlbumArt", $"MB search non-success {(int)searchResponse.StatusCode} for '{artist} - {album}'");
            return null;
        }

        var searchJson = await searchResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var (releaseIds, releaseGroupIds) = ExtractMusicBrainzIds(searchJson);

        if (releaseIds.Count == 0 && releaseGroupIds.Count == 0)
        {
            Logger.Detail("AlbumArt", $"No MBID found for '{artist} - {album}'");
            return null;
        }
        Logger.Detail("AlbumArt", $"Found {releaseIds.Count} release(s) + {releaseGroupIds.Count} release-group(s) for '{artist} - {album}'");

        // Step 2a: Try each release MBID against Cover Art Archive
        var result = await TryCaaUrlsAsync(
            releaseIds.Select(id => $"https://coverartarchive.org/release/{id}/front-500"),
            artist, album, ct).ConfigureAwait(false);
        if (result is not null) return result;

        // Step 2b: Fall back to release-group CAA endpoint (covers the common case where
        //          individual releases have no art but the release group does)
        result = await TryCaaUrlsAsync(
            releaseGroupIds.Select(id => $"https://coverartarchive.org/release-group/{id}/front-500"),
            artist, album, ct).ConfigureAwait(false);
        if (result is not null) return result;

        // Step 3: Recording search â€” finds the parent album when the title is a single track,
        //         not an album/EP name.  E.g. "Three Drinks Behind" by George Strait exists as
        //         a single release with no CAA art, but the same recording appears on the album
        //         "Cowboys and Dreamers" which does have CAA art.
        result = await FetchAlbumArtViaRecordingSearchAsync(artist, album, ct).ConfigureAwait(false);
        if (result is not null) return result;

        Logger.Detail("AlbumArt", $"No CAA cover found for '{artist} - {album}'");
        return null;
    }

    /// <summary>
    /// Searches MusicBrainz by recording (track) name, collects the release-groups of every
    /// release the recording appears on, then tries Cover Art Archive on those groups.
    /// Results are ordered so Album release-groups are tried before EP, then Single.
    /// </summary>
    private static async Task<byte[]?> FetchAlbumArtViaRecordingSearchAsync(string artist, string trackName,
        CancellationToken ct = default)
    {
        var searchTerms = string.IsNullOrWhiteSpace(artist)
            ? Uri.EscapeDataString($"recording:\"{trackName}\"")
            : Uri.EscapeDataString($"recording:\"{trackName}\" AND artist:\"{artist}\"");

        var searchUrl = $"https://musicbrainz.org/ws/2/recording/?query={searchTerms}&limit=10&fmt=json&inc=releases+release-groups";
        Logger.Detail("AlbumArt", $"MB recording search: {searchUrl}");

        await ThrottleMusicBrainzAsync(ct).ConfigureAwait(false);

        System.Net.Http.HttpResponseMessage searchResponse;
        try
        {
            searchResponse = await _albumArtHttpClient.GetAsync(searchUrl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new AlbumArtTransientException($"MB recording search failed: {ex.Message}");
        }

        if ((int)searchResponse.StatusCode == 503 || (int)searchResponse.StatusCode == 429)
            throw new AlbumArtTransientException($"MB recording search returned {(int)searchResponse.StatusCode}");

        if (!searchResponse.IsSuccessStatusCode)
        {
            Logger.Detail("AlbumArt", $"MB recording search non-success {(int)searchResponse.StatusCode} for '{artist} - {trackName}'");
            return null;
        }

        var json = await searchResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var releaseGroupIds = ExtractReleaseGroupsFromRecordingSearch(json);

        if (releaseGroupIds.Count == 0)
        {
            Logger.Detail("AlbumArt", $"No release-groups found via recording search for '{artist} - {trackName}'");
            return null;
        }

        Logger.Detail("AlbumArt", $"Recording search found {releaseGroupIds.Count} release-group(s) for '{artist} - {trackName}'");

        return await TryCaaUrlsAsync(
            releaseGroupIds.Select(id => $"https://coverartarchive.org/release-group/{id}/front-500"),
            artist, trackName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses a MusicBrainz recording-search JSON response and returns release-group IDs
    /// ordered by primary-type preference: Album first, then EP, then others (e.g. Single).
    /// Duplicate release-group IDs are removed.
    /// </summary>
    private static List<string> ExtractReleaseGroupsFromRecordingSearch(string json)
    {
        // MB JSON structure:
        // { "recordings": [ { "releases": [ { "id": "...", "release-group": { "id": "...", "primary-type": "Album" } } ] } ] }
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var albums  = new List<string>();
        var eps     = new List<string>();
        var others  = new List<string>();

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("recordings", out var recordings)) return albums;

            foreach (var recording in recordings.EnumerateArray())
            {
                if (!recording.TryGetProperty("releases", out var releases)) continue;
                foreach (var release in releases.EnumerateArray())
                {
                    if (!release.TryGetProperty("release-group", out var rg)) continue;
                    if (!rg.TryGetProperty("id", out var idProp)) continue;
                    var rgId = idProp.GetString() ?? string.Empty;
                    if (string.IsNullOrEmpty(rgId) || !seen.Add(rgId)) continue;

                    var primaryType = rg.TryGetProperty("primary-type", out var pt)
                        ? (pt.GetString() ?? string.Empty)
                        : string.Empty;

                    if (string.Equals(primaryType, "Album", StringComparison.OrdinalIgnoreCase))
                        albums.Add(rgId);
                    else if (string.Equals(primaryType, "EP", StringComparison.OrdinalIgnoreCase))
                        eps.Add(rgId);
                    else
                        others.Add(rgId);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("AlbumArt", $"Failed to parse MB recording JSON: {ex.Message}");
        }

        // Prefer Album > EP > Single/other
        albums.AddRange(eps);
        albums.AddRange(others);
        return albums;
    }

    /// <summary>Tries a sequence of CAA URLs, returning the first successful image bytes.</summary>
    private static async Task<byte[]?> TryCaaUrlsAsync(IEnumerable<string> urls, string artist, string album,
        CancellationToken ct = default)
    {
        foreach (var coverUrl in urls)
        {
            Logger.Detail("AlbumArt", $"CAA fetch: {coverUrl}");

            System.Net.Http.HttpResponseMessage coverResponse;
            try
            {
                coverResponse = await _albumArtHttpClient.GetAsync(coverUrl, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new AlbumArtTransientException($"CAA request failed: {ex.Message}");
            }

            if ((int)coverResponse.StatusCode == 503 || (int)coverResponse.StatusCode == 429)
                throw new AlbumArtTransientException($"CAA returned {(int)coverResponse.StatusCode}");

            if (coverResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Logger.Detail("AlbumArt", $"CAA 404 for {coverUrl}, trying next");
                continue;
            }

            if (!coverResponse.IsSuccessStatusCode)
            {
                Logger.Detail("AlbumArt", $"CAA {(int)coverResponse.StatusCode} for {coverUrl}, trying next");
                continue;
            }

            var imageBytes = await coverResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (imageBytes.Length > 0)
            {
                Logger.Detail("AlbumArt", $"Got {imageBytes.Length} bytes for '{artist} - {album}' via {coverUrl}");
                return imageBytes;
            }
        }
        return null;
    }

    /// <summary>
    /// Parses a MusicBrainz release-search JSON response and returns
    /// (releaseIds, releaseGroupIds) â€” both in result order, deduplicated.
    /// </summary>
    private static (List<string> ReleaseIds, List<string> ReleaseGroupIds) ExtractMusicBrainzIds(string json)
    {
        // MB JSON structure (simplified):
        // { "releases": [ { "id": "<release-mbid>", "release-group": { "id": "<rg-mbid>" }, ... } ] }
        var releaseIds      = new List<string>();
        var releaseGroupIds = new List<string>();
        var seenRg          = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("releases", out var releases)) return (releaseIds, releaseGroupIds);

            foreach (var release in releases.EnumerateArray())
            {
                if (release.TryGetProperty("id", out var idProp))
                    releaseIds.Add(idProp.GetString() ?? string.Empty);

                if (release.TryGetProperty("release-group", out var rg) &&
                    rg.TryGetProperty("id", out var rgId))
                {
                    var rgIdStr = rgId.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(rgIdStr) && seenRg.Add(rgIdStr))
                        releaseGroupIds.Add(rgIdStr);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("AlbumArt", $"Failed to parse MB JSON: {ex.Message}");
        }

        releaseIds.RemoveAll(string.IsNullOrEmpty);
        return (releaseIds, releaseGroupIds);
    }

    private object GetMusicScanStatus()
    {
        var pb = _callbacks.GetMusicStatus();
        return new
        {
            sessionId     = _serverSessionId,
            isScanning    = _isMusicIndexing,
            indexedFiles  = _isMusicIndexing ? _musicScanProgress : _musicIndex.Length,
            currentScanFolder = _musicScanFolder,
            musicRoot     = _config.ResolvedMusicPath,
            lastRefreshUtc = _lastMusicIndexRefreshUtc,
            lastError     = _lastMusicScanError,
            // playback fields
            isPlaying     = pb.IsPlaying,
            isPaused      = pb.IsPaused,
            currentPath   = pb.CurrentPath is not null ? WebPathHelpers.EncodePath(pb.CurrentPath) : null,
            title         = pb.Title,
            artist        = pb.Artist,
            album         = pb.Album,
            position      = pb.Position,
            duration      = pb.Duration,
            playbackError = pb.LastError,
            eqPreset      = pb.EqPreset,
            reverbPreset  = pb.ReverbPreset
        };
    }

}
