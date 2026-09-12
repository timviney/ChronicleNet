namespace ChronicleNet.Tests;

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "chroniclenet-tests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class Format
{
    public const int FileHeaderLength = 16;
    public const int NotComplete = unchecked((int)0x8000_0000);
    public const int MetaData = 0x4000_0000;
    public const int EndOfData = unchecked((int)0xC000_0000);
}

internal static class QueueFiles
{
    public static string Single(string directory) => Directory.GetFiles(directory, "*.cnq").Single();

    public static byte[] ReadAllBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    // Overwrites a 4-byte little-endian record header, simulating a writer mid-append.
    public static void PokeInt32(string path, long offset, int value)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)value;
        bytes[1] = (byte)(value >> 8);
        bytes[2] = (byte)(value >> 16);
        bytes[3] = (byte)(value >> 24);
        RandomAccess.Write(stream.SafeFileHandle, bytes, offset);
    }
}
