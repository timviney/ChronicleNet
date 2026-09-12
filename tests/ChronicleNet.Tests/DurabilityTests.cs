using System.Buffers.Binary;
using System.Text;

namespace ChronicleNet.Tests;

public class DurabilityTests
{
    [Fact]
    public void Committed_records_are_readable_through_a_fresh_handle_without_flush()
    {
        using var temp = new TempDirectory();
        using var queue = ChronicleQueue.Open(temp.Path);
        queue.CreateAppender().Append("hello"u8);

        string file = QueueFiles.Single(temp.Path);

        // A brand-new OS handle opened after the append, with no explicit flush anywhere.
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = Format.FileHeaderLength;

        Span<byte> header = stackalloc byte[4];
        stream.ReadExactly(header);
        int info = BinaryPrimitives.ReadInt32LittleEndian(header);

        Assert.False((info & Format.NotComplete) != 0); // WIP clear: the record is committed
        Assert.Equal(5, info & 0x3FFF_FFFF);            // payload length

        Span<byte> payload = stackalloc byte[5];
        stream.ReadExactly(payload);
        Assert.Equal("hello", Encoding.UTF8.GetString(payload));
    }
}
