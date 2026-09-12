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
}
