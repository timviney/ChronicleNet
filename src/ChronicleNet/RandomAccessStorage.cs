using Microsoft.Win32.SafeHandles;

namespace ChronicleNet;

internal sealed class RandomAccessStorage(
    FileStream stream,
    long preGrowChunkSize = RandomAccessStorage.DefaultPreGrowChunkSize)
    : IStorage
{
    public const long DefaultPreGrowChunkSize = 64L * 1024 * 1024;

    private readonly SafeFileHandle _handle = stream.SafeFileHandle;

    public long Length => RandomAccess.GetLength(_handle);

    public void WriteAt(long offset, ReadOnlySpan<byte> data)
    {
        EnsureCapacity(offset + data.Length);
        RandomAccess.Write(_handle, data, offset);
    }

    public int ReadAt(long offset, Span<byte> buffer)
        => RandomAccess.Read(_handle, buffer, offset); // buffer.Length defines the maximum number of bytes to read

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
