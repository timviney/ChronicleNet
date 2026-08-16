namespace ChronicleNet;

internal interface IStorage : IDisposable
{
    long Length { get; }

    void WriteAt(long offset, ReadOnlySpan<byte> data);
    
    /// <returns>the number of bytes read</returns>
    int ReadAt(long offset, Span<byte> buffer);

    void Flush();
}
