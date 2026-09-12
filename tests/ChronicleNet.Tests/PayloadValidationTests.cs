using System.Runtime.InteropServices;

namespace ChronicleNet.Tests;

public class PayloadValidationTests
{
    [Fact]
    public void Empty_payload_is_rejected()
    {
        using var temp = new TempDirectory();
        using var queue = ChronicleQueue.Open(temp.Path, new QueueOptions { PreGrowChunkSize = 1 });
        var appender = queue.CreateAppender();

        Assert.Throws<ArgumentOutOfRangeException>(() => appender.Append(ReadOnlySpan<byte>.Empty));

        AssertQueueUnchanged(temp.Path, queue);
    }

    [Fact]
    public void Oversized_payload_is_rejected()
    {
        using var temp = new TempDirectory();
        using var queue = ChronicleQueue.Open(temp.Path, new QueueOptions { PreGrowChunkSize = 1 });
        var appender = queue.CreateAppender();

        // One byte over the 2^30 - 1 maximum. The span is never read, so this needs no
        // gigabyte-sized allocation; Append rejects it on length before touching memory.
        byte dummy = 0;
        ReadOnlySpan<byte> oversized = MemoryMarshal.CreateReadOnlySpan(ref dummy, 1 << 30);

        bool threw = false;
        try
        {
            appender.Append(oversized);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        Assert.True(threw, "Append should reject a payload larger than 2^30 - 1 bytes.");

        AssertQueueUnchanged(temp.Path, queue);
    }

    private static void AssertQueueUnchanged(string directory, ChronicleQueue queue)
    {
        // Only the 16-byte file header has been written.
        Assert.Equal(16, QueueFiles.ReadAllBytes(QueueFiles.Single(directory)).Length);

        var tailer = queue.CreateTailer();
        tailer.ToStart();
        Assert.False(tailer.TryRead(out _));

        // The queue is still usable: the first valid append gets sequence 0.
        long index = queue.CreateAppender().Append([1]);
        Assert.Equal(0u, (uint)index);
    }
}
