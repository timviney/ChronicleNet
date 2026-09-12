namespace ChronicleNet.Tests;

public class GoldenByteTests
{
    [Fact]
    public void Known_payloads_produce_the_documented_bytes()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var temp = new TempDirectory();
        using var queue = ChronicleQueue.Open(temp.Path, new QueueOptions
        {
            TimeProvider = clock,
            PreGrowChunkSize = 1, // keep the file length exactly equal to the framed bytes
        });

        var appender = queue.CreateAppender();
        appender.Append("hello"u8);
        appender.Append("abcd"u8);
        appender.Append("xy"u8);

        byte[] expected =
        [
            // file header: magic "CNQF", version 1, cycle 20454 (days since epoch for 2026-01-01), reserved
            0x43, 0x4E, 0x51, 0x46, 0x01, 0x00, 0x00, 0x00,
            0xE6, 0x4F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,

            // "hello": header len=5, payload, 3 padding bytes
            0x05, 0x00, 0x00, 0x00, 0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0x00, 0x00,

            // "abcd": header len=4, payload, no padding (already aligned)
            0x04, 0x00, 0x00, 0x00, 0x61, 0x62, 0x63, 0x64,

            // "xy": header len=2, payload, 2 padding bytes
            0x02, 0x00, 0x00, 0x00, 0x78, 0x79, 0x00, 0x00,
        ];

        byte[] actual = QueueFiles.ReadAllBytes(QueueFiles.Single(temp.Path));

        Assert.Equal(expected, actual);
    }
}
