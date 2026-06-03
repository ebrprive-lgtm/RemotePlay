using System.IO;
using System.Net;

namespace RemotePlay;

// Background library and music index refresh management.

internal sealed partial class WebServer
{
    private void RefreshLibraryIndexIfIdle()
    {
        if (_lastIndexRefreshUtc is not null && DateTimeOffset.UtcNow - _lastIndexRefreshUtc.Value < TimeSpan.FromDays(1))
            return;

        StartLibraryIndexRefresh(force: false);
    }

    private void StartLibraryIndexRefresh(bool force)
    {
        lock (_libraryIndexGate)
        {
            if (_isIndexing)
            {
                Logger.Info($"Library index refresh skipped; already indexing (force={force})");
                return;
            }

            if (!force && HasFreshLibraryIndex())
            {
                Logger.Info($"Library index refresh skipped; cache is fresh ({_libraryIndex.Length} videos)");
                return;
            }

            _isIndexing = true;
            _scanStartedUtc = DateTimeOffset.UtcNow;
            _scannedFiles = 0;
            _scannedFolders = 0;
            _lastScanError = string.Empty;
            Logger.Info($"Library index refresh starting (force={force}, cached={_libraryIndex.Length})");
        }

        Task.Run(() =>
        {
            try
            {
                NetworkShareHelper.EnsureConnected(_config.AllResolvedMoviesPaths, _config.NetworkShareCredentials);

                var allFiles = new List<LibraryFile>();

                foreach (var root in _config.AllResolvedMoviesPaths)
                {
                    if (!Directory.Exists(root))
                    {
                        Logger.Info($"Movie library root not found, skipping: {root}");
                        continue;
                    }

                    var rootFiles = EnumerateLibraryVideoFiles(root, _hiddenFolderNames, () => Interlocked.Increment(ref _scannedFolders))
                        .Select(f =>
                        {
                            Interlocked.Increment(ref _scannedFiles);
                            return f;
                        })
                        .ToArray();

                    var videoFiles = rootFiles
                        .Where(f => WebPathHelpers.IsVideoFile(f, _videoExtensions))
                        .Select(f => BuildLibraryFile(root, f));

                    var linkFiles = rootFiles
                        .Where(RplinkHelper.IsRplinkFile)
                        .Select(f => BuildLibraryFileForLink(root, f))
                        .Where(f => f is not null)
                        .Select(f => f!);

                    var folderLinkFiles = rootFiles
                        .Where(RplinkHelper.IsRplinkFile)
                        .SelectMany(f =>
                        {
                            var items = BuildLibraryFilesForFolderLink(root, f).ToList();
                            if (items.Count > 0)
                                Interlocked.Add(ref _scannedFiles, items.Count);
                            return items;
                        });

                    allFiles.AddRange(videoFiles.Concat(linkFiles).Concat(folderLinkFiles));
                }

                if (allFiles.Count == 0 && _config.AllResolvedMoviesPaths.Length == 0)
                {
                    _libraryIndex = [];
                    _lastIndexRefreshUtc = DateTimeOffset.UtcNow;
                    return;
                }

                var files = allFiles
                    // deduplicate: prefer the link entry over a plain entry for the same real path
                    .GroupBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(f => f.IsLink).First())
                    .OrderBy(f => f.SearchText, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                _libraryIndex = files;
                _lastIndexRefreshUtc = DateTimeOffset.UtcNow;
                SaveLibraryIndexCache();
                Logger.Detail($"Library index refreshed: {files.Length} videos across {_config.AllResolvedMoviesPaths.Length} root(s)");
            }
            catch (Exception ex)
            {
                _lastScanError = ex.Message;
                Logger.Error("Library index refresh failed", ex);
            }
            finally
            {
                lock (_libraryIndexGate)
                    _isIndexing = false;
            }
        });
    }

    // How long a persisted music index is considered fresh before a background re-scan is run.
    private static readonly TimeSpan MusicIndexMaxAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Called on startup. If a recent cache was loaded, defers the full re-scan by a short
    /// warm-up period so the app is immediately usable; the background scan still runs to pick
    /// up any files added since the cache was written. If no cache (or it is stale), scans now.
    /// </summary>
    private void StartMusicIndexRefreshIfNeeded()
    {
        DateTimeOffset? lastRefresh;
        int cachedCount;
        lock (_musicIndexGate)
        {
            lastRefresh = _lastMusicIndexRefreshUtc;
            cachedCount = _musicIndex.Length;
        }

        if (lastRefresh.HasValue && cachedCount > 0 && DateTimeOffset.UtcNow - lastRefresh.Value < MusicIndexMaxAge)
        {
            Logger.Info($"Music index cache is fresh ({cachedCount} tracks, age {(DateTimeOffset.UtcNow - lastRefresh.Value).TotalMinutes:F0} min). " +
                        "Background re-scan deferred by 60 s.");
            // Give the app 60 seconds to fully start before doing the verify scan in the background.
            Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ => StartMusicIndexRefresh());
        }
        else
        {
            Logger.Info(cachedCount == 0
                ? "No music index cache found â€” starting full scan."
                : $"Music index cache is stale ({cachedCount} tracks) â€” starting full scan.");
            StartMusicIndexRefresh();
        }
    }

    /// <summary>
    /// Rebuilds only the M3U/M3U8 index without re-scanning audio files.
    /// Called on startup when the music track cache is already loaded from disk
    /// so that dynamic-folder include/exclude filtering works immediately.
    /// </summary>
    private void StartM3uIndexRefresh()
    {
        Task.Run(() =>
        {
            try
            {
                var scanRoot = _config.ResolvedMusicPath;
                Logger.Info($"M3U index: starting background scan of '{scanRoot}'");
                var m3uAudioExtSet = new HashSet<string>(_musicExtensions, StringComparer.OrdinalIgnoreCase);
                var newM3uIndex = MusicScanner.ScanM3uFiles(
                    scanRoot,
                    audioExtensions: m3uAudioExtSet,
                    ignoredFolderNames: _hiddenFolderNames);
                foreach (var additionalRoot in _config.AllResolvedMusicPaths.Skip(1))
                {
                    if (!Directory.Exists(additionalRoot)) continue;
                    var extra = MusicScanner.ScanM3uFiles(additionalRoot, m3uAudioExtSet, _hiddenFolderNames);
                    Logger.Info($"M3U index: additional root '{additionalRoot}' found {extra.Count} playlist(s)");
                    foreach (var kv in extra) newM3uIndex[kv.Key] = kv.Value;
                }
                // Log sample paths so path/separator issues are visible in logs
                int n = 0;
                foreach (var key in newM3uIndex.Keys)
                {
                    Logger.Info($"M3U index sample [{n}]: '{key}' ({newM3uIndex[key].TrackPaths.Length} track(s))");
                    if (++n >= 5) break;
                }
                lock (_musicIndexGate)
                    _m3uIndex = newM3uIndex;
                Logger.Info($"M3U index: indexed {newM3uIndex.Count} playlist(s) total");
                SaveM3uIndexCache();
            }
            catch (Exception ex)
            {
                Logger.Warning("M3uIndexRefresh", $"M3U scan failed: {ex.Message}");
            }
        });
    }

        private void StartMusicIndexRefresh()
    {
        CancellationTokenSource cts;
        MusicScanJob job;
        lock (_musicIndexGate)
        {
            if (_isMusicIndexing)
                _musicScanCts?.Cancel();

            _musicScanCts?.Dispose();
            cts = _musicScanCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            job = _activeMusicScanJob = new MusicScanJob(_config.ResolvedMusicPath);
            _isMusicIndexing = true;
            _musicScanProgress = 0;
            _musicScanFolder = string.Empty;
            _lastMusicScanError = string.Empty;
        }

        Task.Run(() =>
        {
            try
            {
                NetworkShareHelper.EnsureConnected(_config.AllResolvedMusicPaths, _config.NetworkShareCredentials);

                // Accumulate all files; merge each folder's batch into the live index as it arrives
                var allFiles = new List<MusicFile>();

                var progress = new Progress<int>(count =>
                {
                    lock (_musicIndexGate)
                        _musicScanProgress = count;
                });

                void onFolder(string folder)
                {
                    lock (_musicIndexGate)
                        _musicScanFolder = folder;
                }

                void onFolderComplete(IReadOnlyList<MusicFile> batch)
                {
                    allFiles.AddRange(batch);
                    // Update the live index after every folder so browse requests
                    // immediately see the newly indexed tracks.
                    var snapshot = allFiles.ToArray();
                    Array.Sort(snapshot, (a, b) =>
                        string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));
                    lock (_musicIndexGate)
                        _musicIndex = snapshot;
                }

                // Scan the primary root with the live-prioritisable job, then scan
                // any additional roots with simple independent jobs.
                MusicScanner.Scan(job, _musicExtensions, onFolderComplete, onFolder, progress, cts.Token, _hiddenFolderNames);

                foreach (var additionalRoot in _config.AllResolvedMusicPaths.Skip(1))
                {
                    if (cts.Token.IsCancellationRequested) break;
                    if (!Directory.Exists(additionalRoot)) continue;

                    Logger.Info($"Music scan: starting additional root '{additionalRoot}'");
                    var additionalJob = new MusicScanJob(additionalRoot);
                    MusicScanner.Scan(additionalJob, _musicExtensions, onFolderComplete, onFolder, progress, cts.Token, _hiddenFolderNames);
                }

                // â”€â”€ M3U index pass â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                // Build the in-memory M3U index so dynamic-folder expand never reads from
                // disk at expand time. Done while _isMusicIndexing is still true so callers
                // don't start using the index prematurely.
                Logger.Info("Music scan: indexing M3U/M3U8 playlistsâ€¦");
                var m3uAudioExtSet = new HashSet<string>(_musicExtensions, StringComparer.OrdinalIgnoreCase);
                var newM3uIndex = MusicScanner.ScanM3uFiles(
                    _config.ResolvedMusicPath,
                    audioExtensions: m3uAudioExtSet,
                    ignoredFolderNames: _hiddenFolderNames,
                    cancellationToken: cts.Token);
                foreach (var additionalRoot in _config.AllResolvedMusicPaths.Skip(1))
                {
                    if (cts.Token.IsCancellationRequested) break;
                    if (!Directory.Exists(additionalRoot)) continue;
                    var extra = MusicScanner.ScanM3uFiles(additionalRoot, m3uAudioExtSet, _hiddenFolderNames, cts.Token);
                    foreach (var kv in extra) newM3uIndex[kv.Key] = kv.Value;
                }
                lock (_musicIndexGate)
                    _m3uIndex = newM3uIndex;
                Logger.Info($"Music scan: indexed {newM3uIndex.Count} playlist(s)");
                SaveM3uIndexCache();

                lock (_musicIndexGate)
                {
                    _musicScanProgress = _musicIndex.Length;
                    _lastMusicIndexRefreshUtc = DateTimeOffset.UtcNow;
                    _activeMusicScanJob = null;
                    _isMusicIndexing = false;       // fast scan done â€” index is usable now
                    _isMusicEnriching = true;
                }
                Logger.Info($"Music index refreshed: {_musicIndex.Length} tracks â€” starting tag enrichment pass");
                SaveMusicIndexCache();

                // â”€â”€ Tag enrichment pass â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                // The fast scan above built the index with name+path only. Now read audio
                // tags in the background so genre/year become available for dynamic-folder
                // filters. The index stays usable throughout; batches are pushed live.
                try
                {
                    MusicFile[] snapshot;
                    lock (_musicIndexGate) snapshot = _musicIndex;

                    MusicScanner.EnrichWithTags(snapshot, enriched =>
                    {
                        lock (_musicIndexGate)
                            _musicIndex = enriched;
                    }, batchSize: 200, cancellationToken: cts.Token);

                    Logger.Info($"Music tag enrichment complete: {_musicIndex.Length} tracks enriched");
                    SaveMusicIndexCache();
                }
                catch (OperationCanceledException) { Logger.Info("Music tag enrichment was cancelled"); }
                catch (Exception ex)               { Logger.Warning("MusicTagEnrich", $"Tag enrichment failed: {ex.Message}"); }
                finally
                {
                    lock (_musicIndexGate)
                        _isMusicEnriching = false;
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Info("Music scan was cancelled");
                lock (_musicIndexGate)
                    _activeMusicScanJob = null;
            }
            catch (Exception ex)
            {
                lock (_musicIndexGate)
                {
                    _lastMusicScanError = ex.Message;
                    _activeMusicScanJob = null;
                }
                Logger.Error("Music index refresh failed", ex);
            }
            finally
            {
                lock (_musicIndexGate)
                {
                    _isMusicIndexing = false;
                    _isMusicEnriching = false;
                }
            }
        }, cts.Token);
    }

    private static string BuildSearchText(string root, string filePath) =>
        LibraryIndexHelpers.BuildSearchText(root, filePath);

    private static LibraryFile BuildLibraryFile(string root, string filePath) =>
        LibraryIndexHelpers.BuildLibraryFile(root, filePath);

    private static LibraryFile? BuildLibraryFileForLink(string root, string rplinkPath) =>
        LibraryIndexHelpers.BuildLibraryFileForLink(root, rplinkPath);

    /// <summary>Enumerates all video files inside the target directory of a folder-type <c>.rplink</c>.
    /// Each yielded entry is tagged with <see cref="LibraryFile.IsLink"/> and
    /// <see cref="LibraryFile.LinkSourcePath"/> so all index-driven operations
    /// (search, thumbnail queue, favourites) work on the linked content.</summary>
    private IEnumerable<LibraryFile> BuildLibraryFilesForFolderLink(string root, string rplinkPath)
    {
        var targetDir = RplinkHelper.TryReadTarget(rplinkPath);
        if (targetDir is null || !Directory.Exists(targetDir))
            yield break;

        var linkLabel = Path.GetFileNameWithoutExtension(rplinkPath);

        foreach (var file in LibraryIndexHelpers.EnumerateLibraryVideoFiles(targetDir, _hiddenFolderNames))
        {
            if (!WebPathHelpers.IsVideoFile(file, _videoExtensions))
                continue;

            long sizeBytes = 0;
            DateTime lastWriteUtc = default;
            try
            {
                var info = new FileInfo(file);
                sizeBytes = info.Length;
                lastWriteUtc = info.LastWriteTimeUtc;
            }
            catch { }

            var relativeInTarget = Path.GetRelativePath(targetDir, file)
                .Replace(Path.DirectorySeparatorChar, ' ')
                .Replace(Path.AltDirectorySeparatorChar, ' ');
            var searchText = $"{linkLabel} {relativeInTarget}";

            yield return new LibraryFile(
                Path.GetFileNameWithoutExtension(file),
                file,
                WebPathHelpers.EncodePath(file),
                Path.GetFileName(Path.GetDirectoryName(file)) ?? string.Empty,
                searchText,
                sizeBytes,
                lastWriteUtc,
                IsLink: true,
                LinkSourcePath: rplinkPath);
        }
    }

    private static IEnumerable<string> EnumerateLibraryVideoFiles(string root, IReadOnlySet<string> ignoredFolderNames, Action? onFolderScanned = null) =>
        LibraryIndexHelpers.EnumerateLibraryVideoFiles(root, ignoredFolderNames, onFolderScanned);

    private static object[] BuildBreadcrumbs(string root, string targetDir) =>
        LibraryIndexHelpers.BuildBreadcrumbs(root, targetDir);


}