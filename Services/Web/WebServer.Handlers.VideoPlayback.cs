using System.IO;
using System.Net;
using System.Text.Json;

namespace RemotePlay;

// Video library and playback handlers: search, browse, play, queue, history, favorites, durations, peers.

internal sealed partial class WebServer
{

    private void HandleSearch(HttpListenerContext ctx)
    {
        var query = (ctx.Request.QueryString["q"] ?? string.Empty).Trim();
        if (_libraryIndex.Length == 0)
            StartLibraryIndexRefresh(force: false);

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var history = GetHistoryForIp(GetClientIp(ctx));
        var resumeMap = GetMergedProgressMap(history);
        var watchedSet = GetMergedWatchedSet(history);
        var naturalComparer = _naturalComparer;

        var offset = ReadNonNegativeInt(ctx.Request.QueryString["offset"]);
        var limit = ReadPositiveInt(ctx.Request.QueryString["limit"], _config.EffectiveLibraryPageSize, 1000);

        var matchingFiles = terms.Length == 0
            ? []
            : _libraryIndex
                .Where(f => terms.All(t => f.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => f.Name)
                .ToArray();

        var files = matchingFiles
            .Skip(offset)
            .Take(limit)
            .Select(f => BuildCardFile(f.Name, f.FilePath, resumeMap, f.FolderName, IsFavorite(f.FilePath), f.IsLink, f.LinkSourcePath, watchedSet))
            .ToArray<object>();

        // Find unique folders that match the search terms
        var folders = terms.Length == 0
            ? Array.Empty<object>()
            : _libraryIndex
                .Where(f => terms.All(t => f.FolderName.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(f => f.FolderName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, naturalComparer)
                .Select(g => new
                {
                    name = g.Key,
                    folder = g.Key
                })
                .ToArray<object>();

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new
        {
            folders,
            files,
            total = folders.Length + files.Length,
            totalFiles = matchingFiles.Length,
            offset,
            limit,
            hasMoreFiles = offset + files.Length < matchingFiles.Length,
            indexedFiles = _libraryIndex.Length,
            indexing = _isIndexing,
            lastRefreshUtc = _lastIndexRefreshUtc
        }));
    }

    private void HandleRecent(HttpListenerContext ctx)
    {
        var root = _config.ResolvedMoviesPath;
        var files = GetHistoryForIp(GetClientIp(ctx)).GetRecent(_config.PlaybackHistoryLimit)
            .Where(item => WebPathHelpers.IsUnderRoot(item.FilePath, root))
            .Select(item => new
            {
                name = Path.GetFileNameWithoutExtension(item.FilePath),
                displayName = CleanDisplayTitle(Path.GetFileNameWithoutExtension(item.FilePath)),
                path = WebPathHelpers.EncodePath(item.FilePath),
                favorite = IsFavorite(item.FilePath),
                position = Math.Round(item.PositionSeconds, 1),
                duration = Math.Round(item.DurationSeconds, 1),
                progress = item.DurationSeconds > 0 ? Math.Round(item.PositionSeconds / item.DurationSeconds, 3) : 0,
                resume = FormatTime(item.PositionSeconds),
                folder = Path.GetFileName(Path.GetDirectoryName(item.FilePath)) ?? string.Empty
            })
            .ToArray();

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { files }));
    }

    private void HandleMusicRecent(HttpListenerContext ctx)
    {
        var root = _config.ResolvedMusicPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            TrySendResponse(ctx, 200, "application/json", "{\"files\":[]}");
            return;
        }

        var musicExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".flac", ".aac", ".m4a", ".ogg", ".wma", ".wav", ".opus", ".ape", ".alac" };

        var indexLookup = _musicIndex.ToDictionary(f => f.FullPath, StringComparer.OrdinalIgnoreCase);

        var files = GetHistoryForIp(GetClientIp(ctx)).GetRecent(_config.PlaybackHistoryLimit + 14)
            .Where(item => WebPathHelpers.IsUnderRoot(item.FilePath, root)
                        && (musicExts.Contains(Path.GetExtension(item.FilePath))
                            || string.Equals(Path.GetExtension(item.FilePath), ".m3u", StringComparison.OrdinalIgnoreCase)))
            .Take(7)
            .Select(item =>
            {
                var ext = Path.GetExtension(item.FilePath);
                if (string.Equals(ext, ".m3u", StringComparison.OrdinalIgnoreCase))
                    return BuildPlaylistRecentObj(item.FilePath);
                if (!indexLookup.TryGetValue(item.FilePath, out var musicFile))
                    musicFile = new MusicFile(Path.GetFileName(item.FilePath), item.FilePath);
                return BuildMusicRecentObj(musicFile, item.PositionSeconds, item.DurationSeconds);
            })
            .ToArray();

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { files }));
    }

    private static object BuildMusicRecentObj(MusicFile f, double positionSeconds, double durationSeconds)
    {
        var dir     = Path.GetDirectoryName(f.FullPath) ?? string.Empty;
        var ext     = Path.GetExtension(f.FullPath).TrimStart('.').ToLowerInvariant();
        string album    = Path.GetFileName(dir);
        string artist   = Path.GetFileName(Path.GetDirectoryName(dir)) ?? string.Empty;
        string tagTitle = string.Empty;
        int?   durationSec = durationSeconds > 0 ? (int)Math.Round(durationSeconds) : null;
        string genre    = string.Empty;
        int?   year     = null;
        bool   hasCover = false;

        try
        {
            using var tfile = TagLib.File.Create(f.FullPath);
            var tag = tfile.Tag;
            if (!string.IsNullOrWhiteSpace(tag.Title))  tagTitle = tag.Title;
            if (!string.IsNullOrWhiteSpace(tag.Album))  album    = tag.Album;
            var performers = tag.AlbumArtists.Length > 0 ? tag.AlbumArtists : tag.Performers;
            if (performers.Length > 0 && !string.IsNullOrWhiteSpace(performers[0]))
                artist = performers[0];
            if (tag.Year > 0)   year = (int)tag.Year;
            if (tag.Genres.Length > 0 && !string.IsNullOrWhiteSpace(tag.Genres[0]))
                genre = tag.Genres[0];
            hasCover = tfile.Tag.Pictures.Length > 0 || FolderHasCoverImage(dir);
            durationSec = (int)Math.Round(tfile.Properties.Duration.TotalSeconds);
        }
        catch { /* fall back to path-based values */ }

        return new
        {
            name        = f.Name,
            tagTitle,
            path        = WebPathHelpers.EncodePath(f.FullPath),
            folder      = Path.GetFileName(dir),
            ext,
            album,
            artist,
            genre,
            year,
            durationSec,
            hasCover,
            position    = Math.Round(positionSeconds, 1),
            duration    = Math.Round(durationSeconds, 1),
            progress    = durationSeconds > 0 ? Math.Round(positionSeconds / durationSeconds, 3) : 0
        };
    }

    private static object BuildPlaylistRecentObj(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        var dir  = Path.GetDirectoryName(filePath) ?? string.Empty;
        string artist = string.Empty;
        try
        {
            var lines = File.ReadLines(filePath)
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
                .Take(1).ToList();
            if (lines.Count > 0)
            {
                var entry = lines[0].Trim();
                var firstPath = Path.IsPathRooted(entry)
                    ? entry
                    : Path.GetFullPath(Path.Combine(dir, entry));
                if (File.Exists(firstPath))
                {
                    using var tagFile = TagLib.File.Create(firstPath);
                    var performers = tagFile.Tag.AlbumArtists.Length > 0
                        ? tagFile.Tag.AlbumArtists
                        : tagFile.Tag.Performers;
                    artist = performers.Length > 0 ? performers[0] : string.Empty;
                }
            }
        }
        catch { /* leave artist empty */ }

        return new
        {
            name,
            tagTitle    = name,
            path        = WebPathHelpers.EncodePath(filePath),
            folder      = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? string.Empty,
            ext         = "m3u",
            album       = name,
            artist,
            genre       = string.Empty,
            year        = (int?)null,
            durationSec = (int?)null,
            hasCover    = false,
            position    = 0.0,
            duration    = 0.0,
            progress    = 0.0,
            isPlaylist  = true
        };
    }

    private void HandleFavorites(HttpListenerContext ctx)
    {
        var root = _config.ResolvedMoviesPath;
        var history = GetHistoryForIp(GetClientIp(ctx));
        var resumeMap = GetMergedProgressMap(history);
        var watchedSet = GetMergedWatchedSet(history);
        var files = GetFavoritePaths()
            .Where(path => WebPathHelpers.IsUnderRoot(path, root) && File.Exists(path))
            .OrderBy(path => Path.GetFileNameWithoutExtension(path), _naturalComparer)
            .Select(path => BuildCardFile(Path.GetFileNameWithoutExtension(path), path, resumeMap, Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty, isFavorite: true, watchedSet: watchedSet))
            .ToArray();

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { files }));
    }

    private void HandleFavorite(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        var filePath = WebPathHelpers.DecodePath(encodedPath);
        if (!WebPathHelpers.IsUnderRoot(filePath, _config.ResolvedMoviesPath) || !File.Exists(filePath))
        {
            TrySendResponse(ctx, 403, "text/plain", "Forbidden");
            return;
        }

        var favorite = bool.TryParse(ctx.Request.QueryString["value"], out var parsed)
            ? parsed
            : !IsFavorite(filePath);
        SetFavorite(filePath, favorite);
        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true, favorite }));
    }

    private void HandleHistoryWatchedSet(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        var filePath = WebPathHelpers.DecodePath(encodedPath);
        if (!WebPathHelpers.IsUnderRoot(filePath, _config.ResolvedMoviesPath))
        {
            TrySendResponse(ctx, 403, "text/plain", "Forbidden");
            return;
        }

        var watchedParam = ctx.Request.QueryString["watched"] ?? "true";
        var watched = !string.Equals(watchedParam, "false", StringComparison.OrdinalIgnoreCase);
        GetHistoryForIp(GetClientIp(ctx)).MarkWatched(filePath, watched);
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    private void HandleRecentClear(HttpListenerContext ctx)
    {
        GetHistoryForIp(GetClientIp(ctx)).ClearAll();
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    private void HandleMusicRecentClear(HttpListenerContext ctx)
    {
        var root = _config.ResolvedMusicPath;
        if (!string.IsNullOrWhiteSpace(root))
        {
            var musicExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".mp3", ".flac", ".aac", ".m4a", ".ogg", ".wma", ".wav", ".opus", ".ape", ".alac", ".m3u" };
            var history = GetHistoryForIp(GetClientIp(ctx));
            var toClear = history.GetRecent(int.MaxValue)
                .Where(item => WebPathHelpers.IsUnderRoot(item.FilePath, root)
                            && musicExts.Contains(Path.GetExtension(item.FilePath)))
                .Select(item => item.FilePath)
                .ToArray();
            foreach (var path in toClear)
                history.Clear(path);
        }
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    private void HandleHistoryClear(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        var filePath = WebPathHelpers.DecodePath(encodedPath);
        if (!WebPathHelpers.IsUnderRoot(filePath, _config.ResolvedMoviesPath))
        {
            TrySendResponse(ctx, 403, "text/plain", "Forbidden");
            return;
        }

        GetHistoryForIp(GetClientIp(ctx)).Clear(filePath);
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    /// <summary>Deletes a <c>.rplink</c> file.
    /// path of the <c>.rplink</c> file itself (returned as <c>linkPath</c> from browse/search).</summary>
    private void HandleDeleteLink(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        var rplinkPath = WebPathHelpers.DecodePath(encodedPath);
        if (!RplinkHelper.IsRplinkFile(rplinkPath)
            || !WebPathHelpers.IsUnderRoot(rplinkPath, _config.ResolvedMoviesPath))
        {
            TrySendResponse(ctx, 403, "text/plain", "Forbidden");
            return;
        }

        try
        {
            File.Delete(rplinkPath);
            StartLibraryIndexRefresh(force: true);
            TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true }));
        }
        catch (Exception ex)
        {
            Logger.Error("HandleDeleteLink", "Failed to delete .rplink file", ex);
            TrySendResponse(ctx, 500, "text/plain", "Failed to delete link: " + ex.Message);
        }
    }

    private void HandleBrowse(HttpListenerContext ctx)
    {
        var root = _config.ResolvedMoviesPath;
        var dirParam = ctx.Request.QueryString["dir"];

        string targetDir;
        if (string.IsNullOrWhiteSpace(dirParam))
        {
            targetDir = root;
        }
        else
        {
            targetDir = WebPathHelpers.DecodePath(dirParam);
            // Security: ensure path stays within the configured root
            if (!WebPathHelpers.IsUnderRoot(targetDir, root))
            {
                TrySendResponse(ctx, 403, "text/plain", "Forbidden");
                return;
            }
        }

        if (!Directory.Exists(targetDir))
        {
            try { Directory.CreateDirectory(targetDir); } catch { }
            // When browsing root and the primary path doesn't exist, check whether any
            // configured movie root is valid so the client can show the right empty state.
            bool allPathsInvalid = string.IsNullOrWhiteSpace(dirParam) &&
                !_config.AllResolvedMoviesPaths.Any(Directory.Exists);
            TrySendResponse(ctx, 200, "application/json",
                JsonSerializer.Serialize(new { folders = Array.Empty<object>(), files = Array.Empty<object>(), current = targetDir, isRoot = true, breadcrumbs = Array.Empty<object>(), allPathsInvalid }));
            return;
        }

        var naturalComparer = _naturalComparer;

        // Regular sub-folders
        var regularFolders = Directory.EnumerateDirectories(targetDir)
            .Where(d => !_hiddenFolderNames.Contains(Path.GetFileName(d)))
            .Select(d => (name: Path.GetFileName(d), dir: WebPathHelpers.EncodePath(d), isLink: false));

        // Folder-link .rplink entries (target is a directory)
        var folderLinkFolders = Directory.EnumerateFiles(targetDir, "*" + RplinkHelper.Extension)
            .Where(RplinkHelper.IsTargetFolder)
            .Select(f =>
            {
                var target = RplinkHelper.TryReadTarget(f);
                return (name: Path.GetFileNameWithoutExtension(f),
                        dir: target is not null ? WebPathHelpers.EncodePath(target) : string.Empty,
                        isLink: true);
            })
            .Where(x => !string.IsNullOrEmpty(x.dir));

        var folders = regularFolders.Concat(folderLinkFolders)
            .OrderBy(f => f.name, naturalComparer)
            .Select(f => new { f.name, f.dir, f.isLink })
            .ToArray();

        var history = GetHistoryForIp(GetClientIp(ctx));
        var resumeMap = GetMergedProgressMap(history);
        var watchedSet = GetMergedWatchedSet(history);
        var offset = ReadNonNegativeInt(ctx.Request.QueryString["offset"]);
        var limit = ReadPositiveInt(ctx.Request.QueryString["limit"], _config.EffectiveLibraryPageSize, 1000);

        // Collect regular video files
        var videoFiles = Directory.EnumerateFiles(targetDir)
            .Where(f => WebPathHelpers.IsVideoFile(f, _videoExtensions))
            .Select(f => (name: Path.GetFileNameWithoutExtension(f), filePath: f, isLink: false, linkSourcePath: (string?)null));

        // Collect resolved .rplink files (file targets only â€” folder targets appear as folder rows above)
        var linkFiles = Directory.EnumerateFiles(targetDir)
            .Where(RplinkHelper.IsRplinkFile)
            .Where(f => !RplinkHelper.IsTargetFolder(f))
            .Select(f => (name: Path.GetFileNameWithoutExtension(f), target: RplinkHelper.TryReadTarget(f), rplinkPath: f))
            .Where(t => t.target is not null)
            .Select(t => (name: t.name, filePath: t.target!, isLink: true, linkSourcePath: (string?)t.rplinkPath));

        var matchingFiles = videoFiles.Concat(linkFiles)
            .OrderBy(f => f.name, naturalComparer)
            .ToArray();

        var files = matchingFiles
            .Skip(offset)
            .Take(limit)
            .Select(f => BuildCardFile(f.name, f.filePath, resumeMap, isFavorite: IsFavorite(f.filePath), isLink: f.isLink, linkSourcePath: f.linkSourcePath, watchedSet: watchedSet))
            .ToArray();

        // Parent dir (null if we're already at root)
        string? parentEncoded = null;
        var parent = Path.GetDirectoryName(targetDir);
        if (parent is not null && WebPathHelpers.IsUnderRoot(parent, root))
            parentEncoded = WebPathHelpers.EncodePath(parent);

        var result = JsonSerializer.Serialize(new
        {
            folders,
            files,
            current = Path.GetFileName(targetDir),
            currentFull = targetDir,
            parent = parentEncoded,
            breadcrumbs = BuildBreadcrumbs(root, targetDir),
            isRoot = string.Equals(targetDir, root, StringComparison.OrdinalIgnoreCase),
            totalFiles = matchingFiles.Length,
            offset,
            limit,
            hasMoreFiles = offset + files.Length < matchingFiles.Length
        });

        TrySendResponse(ctx, 200, "application/json", result);
    }

    private static int ReadNonNegativeInt(string? value) => MediaControlValueParser.ReadNonNegativeInt(value);

    private static int ReadPositiveInt(string? value, int defaultValue, int maxValue) => MediaControlValueParser.ReadPositiveInt(value, defaultValue, maxValue);

    private void HandlePlay(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        var filePath = WebPathHelpers.DecodePath(encodedPath);
        if (!File.Exists(filePath))
        {
            TrySendResponse(ctx, 404, "text/plain", "File not found");
            return;
        }

        _callbacks.StopMusic();
        _callbacks.RadioStop();
        _callbacks.Play(filePath);
        Logger.Info("Playback", $"Playing Video: '{Path.GetFileName(filePath)}' on Server");
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    // Sets the browser-local-playing flag so the WPF window can update its idle overlay.
    private void HandleLocalPlayingAsync(HttpListenerContext ctx)
    {
        using var reader = new System.IO.StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8);
        var body = reader.ReadToEnd().Trim();
        _browserLocalPlaying = body.Equals("true", StringComparison.OrdinalIgnoreCase);
        TrySendResponse(ctx, 200, "text/plain", "ok");
    }

    // Accepts a log message posted from the browser-side JavaScript (e.g. local playback events).
    private void HandleClientLog(HttpListenerContext ctx)
    {
        using var reader = new System.IO.StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8);
        var body = reader.ReadToEnd().Trim();
        if (!string.IsNullOrWhiteSpace(body))
        {
            // Diagnostic trace prefixes are Detail-level; everything else is Info
            if (body.StartsWith("[LOCAL-", StringComparison.Ordinal) ||
                body.StartsWith("[RADIO-LOCAL]", StringComparison.Ordinal) ||
                body.StartsWith("[RADIO-SERVER]", StringComparison.Ordinal) ||
                body.StartsWith("[PROXY]", StringComparison.Ordinal))
                Logger.Detail("Playback", body);
            else
                Logger.Info("Playback", body);
        }
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    private void HandleQueueAdd(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        var filePath = WebPathHelpers.DecodePath(encodedPath);
        if (!File.Exists(filePath))
        {
            TrySendResponse(ctx, 404, "text/plain", "File not found");
            return;
        }

        _callbacks.Enqueue(filePath);
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    private void HandleQueueRemove(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        _callbacks.RemoveFromQueue(WebPathHelpers.DecodePath(encodedPath));
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    private void HandleQueueMove(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        var directionParam = ctx.Request.QueryString["direction"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        var direction = string.Equals(directionParam, "up", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
        _callbacks.MoveQueueItem(WebPathHelpers.DecodePath(encodedPath), direction);
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }


    /// <summary>
    /// POST /api/durations â€” accepts a JSON array of Base64-encoded file paths,
    /// returns a JSON object mapping each encoded path to its cached duration in seconds.
    /// Only paths already present in the in-memory cache are returned (never probes disk).
    /// </summary>
    private void HandleDurations(HttpListenerContext ctx)
    {
        using var reader = new System.IO.StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8);
        var body = reader.ReadToEnd();
        string[] encodedPaths;
        try
        {
            encodedPaths = JsonSerializer.Deserialize<string[]>(body) ?? [];
        }
        catch
        {
            TrySendResponse(ctx, 400, "text/plain", "Expected JSON array of encoded paths");
            return;
        }

        var result = new Dictionary<string, double>();
        foreach (var encoded in encodedPaths)
        {
            if (string.IsNullOrWhiteSpace(encoded)) continue;
            var filePath = WebPathHelpers.DecodePath(encoded);
            if (_videoDurationCache.TryGetValue(filePath, out var d) && d > 0)
                result[encoded] = d;
        }
        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(result));
    }

    private object BuildCardFile(string name, string filePath, IReadOnlyDictionary<string, RecentPlaybackItem> resumeMap, string folder = "", bool isFavorite = false, bool isLink = false, string? linkSourcePath = null, IReadOnlySet<string>? watchedSet = null)
    {
        resumeMap.TryGetValue(filePath, out var resume);
        var progress = resume is null || resume.DurationSeconds <= 0 ? 0 : Math.Round(resume.PositionSeconds / resume.DurationSeconds, 3);
        var watched = (watchedSet?.Contains(filePath) ?? false) || progress >= 0.95;
        long sizeBytes = 0;
        try { sizeBytes = new FileInfo(filePath).Length; } catch { }

        // Prefer duration from resume map (already known from playback).
        // Fall back to the background-populated cache.
        // If neither has a value yet, queue a background probe so future requests will have it.
        double durationSec = 0;
        if (resume is not null && resume.DurationSeconds > 0)
        {
            durationSec = Math.Round(resume.DurationSeconds, 1);
            _videoDurationCache[filePath] = durationSec; // keep cache warm
        }
        else if (_videoDurationCache.TryGetValue(filePath, out var cached) && cached > 0)
        {
            durationSec = cached;
        }
        else
        {
            // Schedule a one-shot background probe; result lands in cache for next request.
            var pathCapture = filePath;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var d = ReadFileDurationSeconds(pathCapture);
                if (d > 0)
                    _videoDurationCache[pathCapture] = d;
            });
        }

        return new
        {
            name,
            displayName = CleanDisplayTitle(name),
            path = WebPathHelpers.EncodePath(filePath),
            folder,
            favorite = isFavorite,
            position = resume is null ? 0 : Math.Round(resume.PositionSeconds, 1),
            duration = durationSec,
            progress,
            resume = resume is null ? string.Empty : FormatTime(resume.PositionSeconds),
            watched,
            isLink,
            linkPath = isLink && linkSourcePath is not null ? WebPathHelpers.EncodePath(linkSourcePath) : null,
            ext = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant(),
            sizeBytes
        };
    }

    private static double ReadFileDurationSeconds(string filePath)
    {
        try
        {
            using var tfile = TagLib.File.Create(filePath);
            var secs = tfile.Properties.Duration.TotalSeconds;
            return secs > 0 ? Math.Round(secs, 1) : 0;
        }
        catch { return 0; }
    }

    private static string CleanDisplayTitle(string name) => DisplayFormatHelpers.CleanDisplayTitle(name);

    private static string FormatTime(double seconds) => DisplayFormatHelpers.FormatTime(seconds);

    private static readonly NaturalStringComparer _naturalComparer = new();

    private void HandlePeers(HttpListenerContext ctx)
    {
        if (_broadcaster is null)
        {
            TrySendResponse(ctx, 200, "application/json", "[]");
            return;
        }

        var peers = _broadcaster.GetPeers().Select(p => new
        {
            name = p.Name,
            scheme = p.Scheme,
            host = p.Host,
            port = p.Port,
            url = p.Url,
            isSelf = p.IsSelf
        });

        ctx.Response.AddHeader("Cache-Control", "no-store");
        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(peers));
    }

    /// <summary>Extracts the client's IP address as a plain string, stripping the IPv4-mapped
    /// IPv6 prefix (<c>::ffff:</c>) so IPv4 clients always resolve to their dotted-decimal address.</summary>
    private static string GetClientIp(HttpListenerContext ctx)
    {
        var addr = ctx.Request.RemoteEndPoint?.Address?.ToString() ?? string.Empty;
        if (addr.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
            addr = addr["::ffff:".Length..];
        return addr;
    }

    // â”€â”€ Radio handlers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

}