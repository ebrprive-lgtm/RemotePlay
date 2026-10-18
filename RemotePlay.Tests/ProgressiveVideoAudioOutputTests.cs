using System.Buffers.Binary;
using System.IO;
using System.Text;
using RemotePlay;
using Xunit;

namespace RemotePlay.Tests;

public sealed class ProgressiveVideoAudioOutputTests
{
    [Fact]
    public async Task FirstCompleteFragmentCanBeReadBeforeOutputCompletes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RemotePlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var partialPath = Path.Combine(directory, "stream.partial");
        var completePath = Path.Combine(directory, "stream.mp4");

        try
        {
            var output = new ProgressiveVideoAudioOutput();
            await output.BeginAttemptAsync(partialPath);
            using var subscription = output.Subscribe("first-reader");
            using var secondSubscription = output.Subscribe("second-reader");
            var destination = new CaptureStream();
            var secondDestination = new CaptureStream();
            var copyTask = output.CopyToAsync(destination, subscription.CancellationToken);
            var secondCopyTask = output.CopyToAsync(
                secondDestination,
                secondSubscription.CancellationToken);
            var initialization = MakeBox("ftyp", []);
            var firstFragment = Concat(
                MakeBox("moof", []),
                MakeBox("mdat", [1, 2, 3, 4]));
            var firstBytes = Concat(initialization, firstFragment);
            var firstFragmentReady = output.WaitForFirstMediaFragmentAsync(CancellationToken.None);

            await output.AppendAsync(initialization, CancellationToken.None);
            await Task.WhenAll(
                destination.WaitForLengthAsync(initialization.Length),
                secondDestination.WaitForLengthAsync(initialization.Length));
            await output.AppendAsync(firstFragment.AsMemory(0, firstFragment.Length - 1), CancellationToken.None);

            Assert.False(output.HasFirstMediaFragment);
            Assert.False(firstFragmentReady.IsCompleted);

            await output.AppendAsync(firstFragment.AsMemory(firstFragment.Length - 1), CancellationToken.None);
            Assert.True(await firstFragmentReady.WaitAsync(TimeSpan.FromSeconds(5)));
            await Task.WhenAll(
                destination.WaitForLengthAsync(firstBytes.Length),
                secondDestination.WaitForLengthAsync(firstBytes.Length));

            Assert.True(output.HasFirstMediaFragment);
            Assert.False(output.IsComplete);
            Assert.False(copyTask.IsCompleted);
            Assert.False(secondCopyTask.IsCompleted);
            Assert.Equal(firstBytes, destination.ToArray());
            Assert.Equal(firstBytes, secondDestination.ToArray());

            var remaining = MakeBox("free", [5, 6, 7]);
            await output.AppendAsync(remaining, CancellationToken.None);
            await output.CloseWriterAsync();
            File.Move(partialPath, completePath);
            output.Commit(completePath);

            await Task.WhenAll(
                copyTask.WaitAsync(TimeSpan.FromSeconds(5)),
                secondCopyTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(Concat(firstBytes, remaining), destination.ToArray());
            Assert.Equal(Concat(firstBytes, remaining), secondDestination.ToArray());
            Assert.True(output.IsComplete);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CommittedProgressiveFileIsDeletedAfterTheLastReaderFinishes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RemotePlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var partialPath = Path.Combine(directory, "stream.partial");

        try
        {
            var output = new ProgressiveVideoAudioOutput();
            await output.BeginAttemptAsync(partialPath);
            using var firstSubscription = output.Subscribe("first-reader");
            using var secondSubscription = output.Subscribe("second-reader");
            var initialization = MakeBox("ftyp", []);
            var fragment = Concat(
                MakeBox("moof", []),
                MakeBox("mdat", [1, 2, 3]));
            await output.AppendAsync(
                Concat(initialization, fragment),
                CancellationToken.None);
            await output.CloseWriterAsync();

            var cleanupCalls = new List<string>();
            output.Commit(partialPath, path =>
            {
                cleanupCalls.Add(path);
                File.Delete(path);
            });

            var firstDestination = new CaptureStream();
            var secondDestination = new CaptureStream();
            await Task.WhenAll(
                output.CopyToAsync(firstDestination, firstSubscription.CancellationToken),
                output.CopyToAsync(secondDestination, secondSubscription.CancellationToken));

            firstSubscription.Dispose();
            Assert.True(File.Exists(partialPath));

            secondSubscription.Dispose();
            Assert.False(File.Exists(partialPath));
            Assert.Equal(partialPath, Assert.Single(cleanupCalls));
            Assert.Equal(Concat(initialization, fragment), firstDestination.ToArray());
            Assert.Equal(Concat(initialization, fragment), secondDestination.ToArray());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CancellingOneReaderPreservesOtherActiveReaders()
    {
        var output = new ProgressiveVideoAudioOutput();
        using var first = output.Subscribe("first-reader");
        using var second = output.Subscribe("second-reader");

        Assert.True(output.CancelSubscriber(first.SubscriberId, out var hasOtherReaders));

        Assert.True(hasOtherReaders);
        Assert.True(output.HasActiveSubscribers);
        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(second.CancellationToken.IsCancellationRequested);
        second.Dispose();
        Assert.False(output.HasActiveSubscribers);
    }

    private static byte[] MakeBox(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), (uint)box.Length);
        Encoding.ASCII.GetBytes(type, box.AsSpan(4, 4));
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var combined = new byte[arrays.Sum(array => array.Length)];
        var offset = 0;
        foreach (var array in arrays)
        {
            array.CopyTo(combined, offset);
            offset += array.Length;
        }
        return combined;
    }

    private sealed class CaptureStream : Stream
    {
        private readonly object _gate = new();
        private readonly MemoryStream _inner = new();

        internal byte[] ToArray()
        {
            lock (_gate)
                return _inner.ToArray();
        }

        internal async Task WaitForLengthAsync(int expectedLength)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                lock (_gate)
                {
                    if (_inner.Length >= expectedLength)
                        return;
                }
                await Task.Delay(10, timeout.Token);
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length
        {
            get
            {
                lock (_gate)
                    return _inner.Length;
            }
        }
        public override long Position
        {
            get
            {
                lock (_gate)
                    return _inner.Position;
            }
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            lock (_gate)
                _inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
                return _inner.FlushAsync(cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                _inner.Write(buffer, offset, count);
            }
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _inner.Write(buffer.Span);
            }
            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_gate)
                    _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
