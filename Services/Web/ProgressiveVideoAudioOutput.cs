using System.Buffers.Binary;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;

namespace RemotePlay;

internal sealed class ProgressiveVideoAudioOutput
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SubscriberGroup> _subscribers = new(StringComparer.Ordinal);
    private readonly Mp4ReadinessDetector _readinessDetector = new();
    private TaskCompletionSource<bool> _changed = CreateSignal();
    private FileStream? _writer;
    private string? _temporaryPath;
    private string? _completedPath;
    private Action<string>? _completedPathCleanup;
    private long _availableBytes;
    private bool _firstMediaFragmentReady;
    private bool _completed;
    private Exception? _failure;

    internal bool HasFirstMediaFragment
    {
        get
        {
            lock (_gate)
                return _firstMediaFragmentReady;
        }
    }

    internal bool IsComplete
    {
        get
        {
            lock (_gate)
                return _completed && _failure is null;
        }
    }

    internal bool HasActiveSubscribers
    {
        get
        {
            lock (_gate)
                return HasOtherActiveSubscribers(null);
        }
    }

    internal ProgressiveVideoAudioSubscription Subscribe(string? subscriberId)
    {
        var id = string.IsNullOrWhiteSpace(subscriberId)
            ? Guid.NewGuid().ToString("N")
            : subscriberId;

        lock (_gate)
        {
            if (!_subscribers.TryGetValue(id, out var group) || group.IsCancelled)
            {
                group = new SubscriberGroup(id);
                _subscribers[id] = group;
            }

            group.ActiveConnections++;
            return new ProgressiveVideoAudioSubscription(this, group);
        }
    }

    internal bool CancelSubscriber(string subscriberId, out bool hasOtherActiveSubscribers)
    {
        SubscriberGroup? group;
        TaskCompletionSource<bool> changed;
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(subscriberId, out group) || group.IsCancelled)
            {
                hasOtherActiveSubscribers = HasOtherActiveSubscribers(null);
                return false;
            }

            group.IsCancelled = true;
            if (group.ActiveConnections == 0)
                _subscribers.Remove(subscriberId);
            hasOtherActiveSubscribers = HasOtherActiveSubscribers(group);
            changed = PulseUnderLock();
        }

        try
        {
            group.CancellationTokenSource.Cancel();
        }
        finally
        {
            if (group.ActiveConnections == 0)
                group.CancellationTokenSource.Dispose();
            changed.TrySetResult(true);
        }

        return true;
    }

    internal async Task BeginAttemptAsync(string temporaryPath)
    {
        var writer = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        TaskCompletionSource<bool> changed;
        lock (_gate)
        {
            if (_writer is not null)
            {
                writer.Dispose();
                throw new InvalidOperationException("A progressive MP4 write attempt is already active.");
            }

            _temporaryPath = temporaryPath;
            _completedPath = null;
            _completedPathCleanup = null;
            _availableBytes = 0;
            _firstMediaFragmentReady = false;
            _completed = false;
            _failure = null;
            _readinessDetector.Reset();
            _writer = writer;
            changed = PulseUnderLock();
        }

        changed.TrySetResult(true);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    internal async Task AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (bytes.IsEmpty)
            return;

        FileStream writer;
        long startingOffset;
        lock (_gate)
        {
            writer = _writer
                ?? throw new InvalidOperationException("No progressive MP4 write attempt is active.");
            startingOffset = _availableBytes;
        }

        await writer.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        var availableBytes = checked(startingOffset + bytes.Length);
        var firstFragmentReady = _readinessDetector.Observe(
            bytes.Span,
            startingOffset,
            availableBytes);

        TaskCompletionSource<bool> changed;
        lock (_gate)
        {
            if (!ReferenceEquals(_writer, writer))
                throw new InvalidOperationException("The progressive MP4 write attempt changed unexpectedly.");

            _availableBytes = availableBytes;
            _firstMediaFragmentReady |= firstFragmentReady;
            changed = PulseUnderLock();
        }

        changed.TrySetResult(true);
    }

    internal async Task CloseWriterAsync()
    {
        FileStream? writer;
        lock (_gate)
        {
            writer = _writer;
            _writer = null;
        }

        if (writer is null)
            return;

        try
        {
            await writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal void Commit(string completedPath, Action<string>? cleanupWhenReadersFinish = null)
    {
        TaskCompletionSource<bool> changed;
        (string Path, Action<string> Cleanup)? cleanup;
        lock (_gate)
        {
            if (!_firstMediaFragmentReady)
                throw new VideoAudioTranscodingException(
                    "FFmpeg completed without producing a playable fragmented MP4.");

            _completedPath = completedPath;
            _completedPathCleanup = cleanupWhenReadersFinish;
            _temporaryPath = null;
            _completed = true;
            _failure = null;
            cleanup = TakeCompletedPathCleanupUnderLock();
            changed = PulseUnderLock();
        }

        changed.TrySetResult(true);
        if (cleanup is { } completedPathCleanup)
            completedPathCleanup.Cleanup(completedPathCleanup.Path);
    }

    internal void CompleteFromExistingFile(string completedPath)
    {
        TaskCompletionSource<bool> changed;
        lock (_gate)
        {
            _completedPath = completedPath;
            _temporaryPath = null;
            _completed = true;
            _failure = null;
            changed = PulseUnderLock();
        }

        changed.TrySetResult(true);
    }

    internal void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        TaskCompletionSource<bool> changed;
        lock (_gate)
        {
            _completed = true;
            _failure = exception;
            changed = PulseUnderLock();
        }

        changed.TrySetResult(true);
    }

    internal async Task<bool> WaitForFirstMediaFragmentAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changedTask;
            Exception? failure;
            bool completed;
            lock (_gate)
            {
                if (_firstMediaFragmentReady)
                    return true;

                failure = _failure;
                completed = _completed;
                changedTask = _changed.Task;
            }

            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
            if (completed)
                return false;

            await changedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task CopyToAsync(Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await using var reader = await OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[81920];
        long position = 0;

        while (true)
        {
            long availableBytes;
            bool completed;
            Exception? failure;
            Task changedTask;
            lock (_gate)
            {
                availableBytes = _availableBytes;
                completed = _completed;
                failure = _failure;
                changedTask = _changed.Task;
            }

            if (position < availableBytes)
            {
                reader.Position = position;
                var bytesToRead = (int)Math.Min(buffer.Length, availableBytes - position);
                var read = await reader.ReadAsync(
                    buffer.AsMemory(0, bytesToRead),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                position += read;
                continue;
            }

            if (completed)
            {
                if (failure is not null)
                    ExceptionDispatchInfo.Capture(failure).Throw();
                return;
            }

            await changedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<FileStream> OpenReaderAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            string? path;
            bool completed;
            Exception? failure;
            Task changedTask;
            lock (_gate)
            {
                path = _completedPath ?? _temporaryPath;
                completed = _completed;
                failure = _failure;
                changedTask = _changed.Task;
            }

            if (path is not null)
            {
                try
                {
                    return new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        81920,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                }
                catch (FileNotFoundException) when (!completed)
                {
                }
                catch (DirectoryNotFoundException) when (!completed)
                {
                }
            }

            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
            if (completed)
                throw new VideoAudioTranscodingException("The completed compatible video output is unavailable.");

            await changedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool HasOtherActiveSubscribers(SubscriberGroup? excluded)
    {
        return _subscribers.Values.Any(group =>
            !ReferenceEquals(group, excluded)
            && !group.IsCancelled
            && group.ActiveConnections > 0);
    }

    private void Release(SubscriberGroup group)
    {
        var disposeCancellation = false;
        (string Path, Action<string> Cleanup)? cleanup;
        lock (_gate)
        {
            if (group.ActiveConnections > 0)
                group.ActiveConnections--;

            if (group.ActiveConnections == 0 && (group.IsCancelled || _completed))
            {
                if (_subscribers.TryGetValue(group.Id, out var current)
                    && ReferenceEquals(current, group))
                {
                    _subscribers.Remove(group.Id);
                }
                disposeCancellation = true;
            }
            cleanup = TakeCompletedPathCleanupUnderLock();
        }

        if (disposeCancellation)
            group.CancellationTokenSource.Dispose();
        if (cleanup is { } completedPathCleanup)
            completedPathCleanup.Cleanup(completedPathCleanup.Path);
    }

    private (string Path, Action<string> Cleanup)? TakeCompletedPathCleanupUnderLock()
    {
        if (!_completed
            || _failure is not null
            || _completedPath is null
            || _completedPathCleanup is null
            || HasOtherActiveSubscribers(null))
        {
            return null;
        }

        var cleanup = (_completedPath, _completedPathCleanup);
        _completedPathCleanup = null;
        return cleanup;
    }

    private TaskCompletionSource<bool> PulseUnderLock()
    {
        var previous = _changed;
        _changed = CreateSignal();
        return previous;
    }

    private static TaskCompletionSource<bool> CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class SubscriberGroup(string id)
    {
        internal string Id { get; } = id;
        internal CancellationTokenSource CancellationTokenSource { get; } = new();
        internal int ActiveConnections { get; set; }
        internal bool IsCancelled { get; set; }
    }

    private sealed class Mp4ReadinessDetector
    {
        private readonly byte[] _header = new byte[16];
        private long _nextBoxOffset;
        private long? _currentBoxEnd;
        private string? _currentBoxType;
        private int _headerLength;
        private bool _sawMovieFragment;

        internal void Reset()
        {
            _nextBoxOffset = 0;
            _currentBoxEnd = null;
            _currentBoxType = null;
            _headerLength = 0;
            _sawMovieFragment = false;
        }

        internal bool Observe(ReadOnlySpan<byte> bytes, long appendOffset, long availableBytes)
        {
            while (true)
            {
                if (_currentBoxEnd is { } boxEnd)
                {
                    if (availableBytes < boxEnd)
                        return false;

                    if (_currentBoxType == "mdat" && _sawMovieFragment)
                        return true;

                    _nextBoxOffset = boxEnd;
                    _currentBoxEnd = null;
                    _currentBoxType = null;
                    _headerLength = 0;
                    continue;
                }

                var requiredHeaderLength = _headerLength < 8
                    ? 8
                    : BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(0, 4)) == 1
                        ? 16
                        : 8;
                if (_headerLength < requiredHeaderLength)
                {
                    var sourceOffset = _nextBoxOffset + _headerLength;
                    var sourceIndex = sourceOffset - appendOffset;
                    if (sourceIndex < 0 || sourceIndex >= bytes.Length)
                        return false;

                    var copyLength = (int)Math.Min(
                        requiredHeaderLength - _headerLength,
                        bytes.Length - sourceIndex);
                    bytes.Slice((int)sourceIndex, copyLength)
                        .CopyTo(_header.AsSpan(_headerLength));
                    _headerLength += copyLength;
                    if (_headerLength < requiredHeaderLength)
                        return false;
                }

                var size32 = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(0, 4));
                var headerSize = size32 == 1 ? 16 : 8;
                var boxSize = size32 == 1
                    ? BinaryPrimitives.ReadUInt64BigEndian(_header.AsSpan(8, 8))
                    : size32;
                if (boxSize < (ulong)headerSize
                    || boxSize == 0
                    || boxSize > (ulong)(long.MaxValue - _nextBoxOffset))
                {
                    throw new VideoAudioTranscodingException(
                        "FFmpeg produced an invalid fragmented MP4 box.");
                }

                _currentBoxType = Encoding.ASCII.GetString(_header, 4, 4);
                if (_currentBoxType == "moof")
                    _sawMovieFragment = true;
                _currentBoxEnd = _nextBoxOffset + (long)boxSize;
            }
        }
    }

    internal sealed class ProgressiveVideoAudioSubscription : IDisposable
    {
        private ProgressiveVideoAudioOutput? _owner;
        private readonly SubscriberGroup _group;

        internal ProgressiveVideoAudioSubscription(
            ProgressiveVideoAudioOutput owner,
            SubscriberGroup group)
        {
            _owner = owner;
            _group = group;
        }

        internal CancellationToken CancellationToken => _group.CancellationTokenSource.Token;
        internal string SubscriberId => _group.Id;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(_group);
        }
    }
}
