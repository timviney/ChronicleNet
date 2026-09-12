using Microsoft.Win32.SafeHandles;

namespace ChronicleNet;

internal sealed class RandomAccessStorage : IStorage
{
    private readonly FileStream _stream;
    private readonly SafeFileHandle _handle;
    private readonly long _preGrowChunkSize;
    private long _length;

    public RandomAccessStorage(FileStream stream, long preGrowChunkSize = QueueOptions.DefaultPreGrowChunkSize)
    {
        _stream = stream;
        _handle = stream.SafeFileHandle;
        _preGrowChunkSize = preGrowChunkSize;
        _length = RandomAccess.GetLength(_handle);
    }

    // Read-only handle for tailers; FileShare.ReadWrite lets the writer keep its handle open too.
    public static RandomAccessStorage OpenRead(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new RandomAccessStorage(stream);
    }

    // The last length we grew the file to. The writer owns the file and only ever extends it,
    // so this stays in sync via EnsureCapacity and avoids a GetLength syscall on every write.
    public long Length => _length;

    public void WriteAt(long offset, ReadOnlySpan<byte> data)
    {
        EnsureCapacity(offset + data.Length);
        RandomAccess.Write(_handle, data, offset);
    }

    /// <returns>the number of bytes read</returns>
    public int ReadAt(long offset, Span<byte> buffer)
        => RandomAccess.Read(_handle, buffer, offset);

    // Flush-to-disk (fsync). Expensive, so it never sits on the append hot path; it exists
    // for callers that opt into durability beyond the OS page cache.
    public void Flush() => _stream.Flush(flushToDisk: true);

    public void Dispose() => _stream.Dispose();

    private void EnsureCapacity(long requiredLength)
    {
        if (requiredLength <= _length)
        {
            return;
        }

        // basically copying array resizing logic from List<T>
        _length = RoundUp(requiredLength, _preGrowChunkSize);
        RandomAccess.SetLength(_handle, _length);
    }

    private static long RoundUp(long value, long multiple)
        => (value + multiple - 1) / multiple * multiple;
}
