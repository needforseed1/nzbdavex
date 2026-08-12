using System.Text;
using NzbWebDAV.Database;

namespace NzbWebDAV.Tests.Database;

public class BlobStoreTests
{
    [Fact]
    public async Task ExistingBlobRemainsReadableWhileReplacementIsWritten()
    {
        var directory = Directory.CreateTempSubdirectory("nzbdavex-blob-test-");
        var path = Path.Combine(directory.FullName, "blob");
        await File.WriteAllTextAsync(path, "old metadata");
        await using var source = new BlockingReadStream("new metadata");

        var writeTask = BlobStore.WriteBlobFile(path, source);
        await source.WaitUntilReadStartsAsync();

        try
        {
            await using (var existingReader = File.OpenRead(path))
            using (var textReader = new StreamReader(existingReader))
            {
                Assert.Equal("old metadata", await textReader.ReadToEndAsync());
                source.Release();
                await writeTask;
            }

            Assert.Equal("new metadata", await File.ReadAllTextAsync(path));
        }
        finally
        {
            source.Release();
            try
            {
                await writeTask;
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }
    }

    [Fact]
    public async Task FailedReplacementLeavesExistingBlobAndRemovesTemporaryFile()
    {
        var directory = Directory.CreateTempSubdirectory("nzbdavex-blob-test-");
        var path = Path.Combine(directory.FullName, "blob");
        await File.WriteAllTextAsync(path, "old metadata");
        await using var source = new FailingReadStream();

        try
        {
            await Assert.ThrowsAsync<IOException>(() => BlobStore.WriteBlobFile(path, source));

            Assert.Equal("old metadata", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.GetFiles(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class BlockingReadStream(string contents) : Stream
    {
        private readonly MemoryStream _inner = new(Encoding.UTF8.GetBytes(contents), writable: false);
        private readonly TaskCompletionSource _readStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _blocked;

        public Task WaitUntilReadStartsAsync() => _readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => _released.TrySetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_blocked)
            {
                _blocked = true;
                _readStarted.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken);
            }

            return await _inner.ReadAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class FailingReadStream : Stream
    {
        private bool _returnedPartialData;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_returnedPartialData)
                return ValueTask.FromException<int>(new IOException("Synthetic source failure."));

            _returnedPartialData = true;
            "partial data"u8.CopyTo(buffer.Span);
            return ValueTask.FromResult("partial data"u8.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
