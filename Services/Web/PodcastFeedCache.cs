using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace RemotePlay;

internal sealed class PodcastEpisodeDto
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("audioUrl")]
    public string AudioUrl { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("publishedUtc")]
    public DateTimeOffset? PublishedUtc { get; init; }

    [JsonPropertyName("durationSeconds")]
    public int? DurationSeconds { get; init; }
}

internal sealed record PodcastFeedValidators(
    Uri Uri,
    EntityTagHeaderValue? ETag,
    DateTimeOffset? LastModified)
{
    internal bool HasValidators => ETag is not null || LastModified.HasValue;
}

internal sealed record PodcastFeedCacheRefreshResult(
    PodcastEpisodeDto[]? Episodes,
    PodcastFeedValidators? Validators,
    bool NotModified,
    bool NoStore,
    TimeSpan? FreshnessLifetime);

internal sealed class PodcastFeedCache
{
    internal static readonly TimeSpan DefaultFreshnessLifetime = TimeSpan.FromMinutes(5);
    internal const long MaxEntrySizeBytes = 4L * 1024 * 1024;
    internal const long MaxTotalSizeBytes = 32L * 1024 * 1024;
    internal const int MaxEntryCount = 128;

    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<PodcastEpisodeDto[]>> _inflight = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private long _currentSizeBytes;
    private long _accessSequence;

    internal PodcastFeedCache(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal int CachedEntryCount
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    internal long CachedSizeBytes
    {
        get
        {
            lock (_gate)
                return _currentSizeBytes;
        }
    }

    internal Task<PodcastEpisodeDto[]> GetOrRefreshAsync(
        Uri feedUri,
        Func<PodcastFeedValidators?, CancellationToken, Task<PodcastFeedCacheRefreshResult>> refreshAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feedUri);
        ArgumentNullException.ThrowIfNull(refreshAsync);
        cancellationToken.ThrowIfCancellationRequested();

        if (!PodcastFeedHttpClient.IsValidHttpUri(feedUri)
            || !PodcastFeedHttpClient.IsSyntacticallyPublicUrl(feedUri.AbsoluteUri))
        {
            throw new PodcastDestinationRejectedException();
        }

        var normalizedUri = NormalizeUri(feedUri);
        var key = normalizedUri.AbsoluteUri;
        Task<PodcastEpisodeDto[]> refreshTask;
        TaskCompletionSource<PodcastEpisodeDto[]>? completion = null;
        CacheEntry? staleEntry = null;

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresUtc > _timeProvider.GetUtcNow())
                {
                    Touch(entry);
                    return Task.FromResult(entry.Episodes).WaitAsync(cancellationToken);
                }

                Touch(entry);
                staleEntry = entry;
            }

            if (!_inflight.TryGetValue(key, out refreshTask!))
            {
                completion = new TaskCompletionSource<PodcastEpisodeDto[]>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                refreshTask = completion.Task;
                _inflight.Add(key, refreshTask);
            }
        }

        if (completion is not null)
        {
            _ = RefreshAndStoreAsync(
                key,
                staleEntry,
                refreshAsync,
                completion);
        }

        return refreshTask.WaitAsync(cancellationToken);
    }

    internal static Uri NormalizeUri(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private async Task RefreshAndStoreAsync(
        string key,
        CacheEntry? staleEntry,
        Func<PodcastFeedValidators?, CancellationToken, Task<PodcastFeedCacheRefreshResult>> refreshAsync,
        TaskCompletionSource<PodcastEpisodeDto[]> completion)
    {
        try
        {
            var refresh = await refreshAsync(staleEntry?.Validators, CancellationToken.None).ConfigureAwait(false);

            PodcastEpisodeDto[] episodes;
            if (refresh.NotModified)
            {
                if (staleEntry is null)
                    throw new HttpRequestException("Podcast feed returned 304 without a cached representation.");

                episodes = staleEntry.Episodes;
            }
            else
            {
                episodes = refresh.Episodes
                    ?? throw new HttpRequestException("Podcast feed refresh returned no representation.");
            }

            var freshnessLifetime = refresh.FreshnessLifetime
                ?? (refresh.NotModified && staleEntry is not null
                    ? staleEntry.FreshnessLifetime
                    : DefaultFreshnessLifetime);
            freshnessLifetime = LimitFreshness(freshnessLifetime);
            var validators = refresh.NotModified
                ? refresh.Validators ?? staleEntry?.Validators
                : refresh.Validators;
            var entry = new CacheEntry(
                episodes,
                validators,
                _timeProvider.GetUtcNow() + freshnessLifetime,
                freshnessLifetime,
                EstimateSizeBytes(key, episodes, validators),
                0);

            lock (_gate)
            {
                _inflight.Remove(key);
                if (refresh.NoStore || entry.SizeBytes > MaxEntrySizeBytes)
                {
                    RemoveEntry(key);
                }
                else
                {
                    StoreEntry(key, entry);
                }
            }

            completion.TrySetResult(episodes);
        }
        catch (Exception ex)
        {
            lock (_gate)
                _inflight.Remove(key);

            completion.TrySetException(ex);
        }
    }

    private void StoreEntry(string key, CacheEntry entry)
    {
        RemoveEntry(key);
        while (_entries.Count >= MaxEntryCount
               || _currentSizeBytes + entry.SizeBytes > MaxTotalSizeBytes)
        {
            EvictLeastRecentlyUsedEntry();
        }

        Touch(entry);
        _entries.Add(key, entry);
        _currentSizeBytes += entry.SizeBytes;
    }

    private void EvictLeastRecentlyUsedEntry()
    {
        string? leastRecentlyUsedKey = null;
        var leastRecentlyUsedSequence = long.MaxValue;
        foreach (var pair in _entries)
        {
            if (pair.Value.AccessSequence < leastRecentlyUsedSequence)
            {
                leastRecentlyUsedKey = pair.Key;
                leastRecentlyUsedSequence = pair.Value.AccessSequence;
            }
        }

        if (leastRecentlyUsedKey is not null)
            RemoveEntry(leastRecentlyUsedKey);
    }

    private void RemoveEntry(string key)
    {
        if (_entries.Remove(key, out var removed))
            _currentSizeBytes -= removed.SizeBytes;
    }

    private void Touch(CacheEntry entry)
        => entry.AccessSequence = ++_accessSequence;

    private static TimeSpan LimitFreshness(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
            return TimeSpan.Zero;

        return lifetime > DefaultFreshnessLifetime
            ? DefaultFreshnessLifetime
            : lifetime;
    }

    private static long EstimateSizeBytes(
        string key,
        PodcastEpisodeDto[] episodes,
        PodcastFeedValidators? validators)
    {
        var size = 256L + 2L * key.Length;
        if (validators is not null)
        {
            size += 2L * validators.Uri.AbsoluteUri.Length;
            if (validators.ETag is not null)
                size += 2L * validators.ETag.ToString().Length;
        }

        size += 128L * episodes.LongLength;
        foreach (var episode in episodes)
        {
            size += StringSizeBytes(episode.Title);
            size += StringSizeBytes(episode.AudioUrl);
            size += StringSizeBytes(episode.Description);
        }

        return size;
    }

    private static long StringSizeBytes(string? value)
        => value is null ? 0 : 48L + 2L * value.Length;

    private sealed class CacheEntry(
        PodcastEpisodeDto[] episodes,
        PodcastFeedValidators? validators,
        DateTimeOffset expiresUtc,
        TimeSpan freshnessLifetime,
        long sizeBytes,
        long accessSequence)
    {
        internal PodcastEpisodeDto[] Episodes { get; } = episodes;
        internal PodcastFeedValidators? Validators { get; } = validators;
        internal DateTimeOffset ExpiresUtc { get; } = expiresUtc;
        internal TimeSpan FreshnessLifetime { get; } = freshnessLifetime;
        internal long SizeBytes { get; } = sizeBytes;
        internal long AccessSequence { get; set; } = accessSequence;
    }
}
