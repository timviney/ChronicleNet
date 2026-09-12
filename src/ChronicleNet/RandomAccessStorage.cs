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

    // Flushes to the OS, but not to disk. This is because we don't want to block the writer thread on disk flushes,
    // which can be slow. The OS will eventually flush to disk on its own.
    public void Flush() => stream.Flush(flushToDisk: false);

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
