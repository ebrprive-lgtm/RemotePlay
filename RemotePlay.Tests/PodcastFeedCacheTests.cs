using Xunit;

namespace RemotePlay.Tests;

public sealed class PodcastFeedCacheTests
{
    [Fact]
    public async Task GetOrRefreshAsync_CoalescesConcurrentMissesForNormalizedUrl()
    {
        var cache = new PodcastFeedCache();
        var loaderStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoader = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaderCalls = 0;

        async Task<PodcastFeedCacheRefreshResult> RefreshAsync(
            PodcastFeedValidators? _,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref loaderCalls);
            loaderStarted.TrySetResult(true);
            await releaseLoader.Task.WaitAsync(cancellationToken);
            return CreateRefreshResult("Shared");
        }

        var firstRequest = cache.GetOrRefreshAsync(
            new Uri("https://FEED.test:443/feed.xml#part"),
            RefreshAsync,
            CancellationToken.None);
        await loaderStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondRequest = cache.GetOrRefreshAsync(
            new Uri("https://feed.test/feed.xml"),
            RefreshAsync,
            CancellationToken.None);
        releaseLoader.TrySetResult(true);

        var results = await Task.WhenAll(firstRequest, secondRequest);

        Assert.Equal(1, loaderCalls);
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task GetOrRefreshAsync_CancelledWaiterDoesNotCancelSharedRefresh()
    {
        var cache = new PodcastFeedCache();
        var loaderStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoader = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstCancellation = new CancellationTokenSource();
        var loaderCalls = 0;

        async Task<PodcastFeedCacheRefreshResult> RefreshAsync(
            PodcastFeedValidators? _,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref loaderCalls);
            Assert.False(cancellationToken.CanBeCanceled);
            loaderStarted.TrySetResult(true);
            await releaseLoader.Task.WaitAsync(cancellationToken);
            return CreateRefreshResult("Shared");
        }

        var firstRequest = cache.GetOrRefreshAsync(
            new Uri("https://feed.test/shared.xml"),
            RefreshAsync,
            firstCancellation.Token);
        await loaderStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondRequest = cache.GetOrRefreshAsync(
            new Uri("https://feed.test/shared.xml"),
            RefreshAsync,
            CancellationToken.None);

        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await firstRequest);
        releaseLoader.TrySetResult(true);

        var result = await secondRequest.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, loaderCalls);
        Assert.Equal("Shared", Assert.Single(result).Title);
    }

    [Fact]
    public async Task GetOrRefreshAsync_DoesNotCacheEntryLargerThanPerEntryLimit()
    {
        var cache = new PodcastFeedCache();
        var uri = new Uri("https://feed.test/large.xml");
        var episode = new PodcastEpisodeDto
        {
            Description = new string('x', (int)(PodcastFeedCache.MaxEntrySizeBytes / 2) + 1),
        };
        var loaderCalls = 0;

        Task<PodcastFeedCacheRefreshResult> RefreshAsync(
            PodcastFeedValidators? _,
            CancellationToken __)
        {
            loaderCalls++;
            return Task.FromResult(CreateRefreshResult(episode));
        }

        await cache.GetOrRefreshAsync(uri, RefreshAsync, CancellationToken.None);
        await cache.GetOrRefreshAsync(uri, RefreshAsync, CancellationToken.None);

        Assert.Equal(2, loaderCalls);
        Assert.Equal(0, cache.CachedEntryCount);
        Assert.Equal(0, cache.CachedSizeBytes);
    }

    [Fact]
    public async Task GetOrRefreshAsync_EvictsLeastRecentlyUsedEntriesAtEntryLimit()
    {
        var cache = new PodcastFeedCache();

        for (var index = 0; index < PodcastFeedCache.MaxEntryCount; index++)
        {
            var uri = new Uri($"https://feed.test/{index}.xml");
            await cache.GetOrRefreshAsync(
                uri,
                (_, _) => Task.FromResult(CreateRefreshResult(index.ToString())),
                CancellationToken.None);
        }

        var firstUri = new Uri("https://feed.test/0.xml");
        await cache.GetOrRefreshAsync(
            firstUri,
            (_, _) => throw new InvalidOperationException("A fresh entry should be served from cache."),
            CancellationToken.None);

        var newestUri = new Uri($"https://feed.test/{PodcastFeedCache.MaxEntryCount}.xml");
        await cache.GetOrRefreshAsync(
            newestUri,
            (_, _) => Task.FromResult(CreateRefreshResult("Newest")),
            CancellationToken.None);

        var evictedRefreshes = 0;
        await cache.GetOrRefreshAsync(
            new Uri("https://feed.test/1.xml"),
            (_, _) =>
            {
                evictedRefreshes++;
                return Task.FromResult(CreateRefreshResult("Reloaded"));
            },
            CancellationToken.None);

        Assert.Equal(1, evictedRefreshes);
        Assert.Equal(PodcastFeedCache.MaxEntryCount, cache.CachedEntryCount);
        Assert.InRange(cache.CachedSizeBytes, 1L, PodcastFeedCache.MaxTotalSizeBytes);
    }

    [Fact]
    public async Task GetOrRefreshAsync_EvictsEntriesToStayWithinAggregateSizeLimit()
    {
        var cache = new PodcastFeedCache();
        const int descriptionLength = 1_700_000;

        for (var index = 0; index < 10; index++)
        {
            var episode = new PodcastEpisodeDto
            {
                Description = new string('x', descriptionLength),
            };
            await cache.GetOrRefreshAsync(
                new Uri($"https://feed.test/large-{index}.xml"),
                (_, _) => Task.FromResult(CreateRefreshResult(episode)),
                CancellationToken.None);
        }

        Assert.InRange(cache.CachedSizeBytes, 1L, PodcastFeedCache.MaxTotalSizeBytes);
        Assert.InRange(cache.CachedEntryCount, 1, 9);

        var reloads = 0;
        await cache.GetOrRefreshAsync(
            new Uri("https://feed.test/large-0.xml"),
            (_, _) =>
            {
                reloads++;
                return Task.FromResult(CreateRefreshResult("Reloaded"));
            },
            CancellationToken.None);

        Assert.Equal(1, reloads);
        Assert.InRange(cache.CachedSizeBytes, 1L, PodcastFeedCache.MaxTotalSizeBytes);
    }

    private static PodcastFeedCacheRefreshResult CreateRefreshResult(string title)
        => CreateRefreshResult(new PodcastEpisodeDto { Title = title });

    private static PodcastFeedCacheRefreshResult CreateRefreshResult(PodcastEpisodeDto episode)
        => new([episode], null, NotModified: false, NoStore: false, FreshnessLifetime: null);
}
