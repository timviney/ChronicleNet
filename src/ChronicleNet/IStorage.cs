namespace ChronicleNet;

internal interface IStorage : IDisposable
{
    long Length { get; }

    void WriteAt(long offset, ReadOnlySpan<byte> data);

    int ReadAt(long offset, Span<byte> buffer);

    void Flush();
}
