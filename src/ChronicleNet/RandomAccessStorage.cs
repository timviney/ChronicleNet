using Microsoft.Win32.SafeHandles;

namespace ChronicleNet;

internal sealed class RandomAccessStorage(
    FileStream stream,
    long preGrowChunkSize = QueueOptions.DefaultPreGrowChunkSize)
    : IStorage
{
    private readonly SafeFileHandle _handle = stream.SafeFileHandle;

    // Read-only handle for tailers; FileShare.ReadWrite lets the writer keep its handle open too.
    public static RandomAccessStorage OpenRead(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new RandomAccessStorage(stream);
    }

    public long Length => RandomAccess.GetLength(_handle);

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
    public void Flush() => stream.Flush(flushToDisk: true);

    public void Dispose() => stream.Dispose();

    private void EnsureCapacity(long requiredLength)
    {
        if (requiredLength <= Length)
        {
            return;
        }
        
        // basically copying array resizing logic from List<T>

        long newLength = RoundUp(requiredLength, preGrowChunkSize);
        RandomAccess.SetLength(_handle, newLength);
    }

    private static long RoundUp(long value, long multiple)
        => (value + multiple - 1) / multiple * multiple;
}
