using System.Text;

namespace ChronicleNet.Tests;

public class RollTests
{
    [Fact]
    public void Midnight_roll_seals_the_first_file_and_continues_in_the_next()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 23, 59, 0, TimeSpan.Zero));
        using var temp = new TempDirectory();
        using var queue = ChronicleQueue.Open(temp.Path, new QueueOptions
        {
            TimeProvider = clock,
            PreGrowChunkSize = 1,
        });
        var appender = queue.CreateAppender();

        appender.Append("day1"u8);
        clock.Now = new DateTimeOffset(2026, 1, 2, 0, 1, 0, TimeSpan.Zero);
        appender.Append("day2"u8);

        string day1 = Path.Combine(temp.Path, "20260101.cnq");
        string day2 = Path.Combine(temp.Path, "20260102.cnq");
        Assert.True(File.Exists(day1));
        Assert.True(File.Exists(day2));

        // day1 = 16-byte header + "day1" record (4 header + 4 payload) + 4-byte end-of-data mark
        byte[] sealedBytes = QueueFiles.ReadAllBytes(day1);
        Assert.Equal(28, sealedBytes.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0xC0 }, sealedBytes[^4..]);

        // day2 = header + "day2" record
        Assert.Equal(24, QueueFiles.ReadAllBytes(day2).Length);
    }

    [Fact]
    public void Tailer_follows_the_roll()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 23, 59, 0, TimeSpan.Zero));
        using var temp = new TempDirectory();
        using var queue = ChronicleQueue.Open(temp.Path, new QueueOptions { TimeProvider = clock });
        var appender = queue.CreateAppender();

        appender.Append("day1"u8);
        clock.Now = new DateTimeOffset(2026, 1, 2, 0, 1, 0, TimeSpan.Zero);
        appender.Append("day2"u8);

        var tailer = queue.CreateTailer();
        tailer.ToStart();

        var read = new List<string>();
        while (tailer.TryRead(out var payload))
        {
            read.Add(Encoding.UTF8.GetString(payload));
        }

        Assert.Equal(new[] { "day1", "day2" }, read);
    }

    [Fact]
    public void Clock_moving_backwards_keeps_appending_to_the_active_file()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var temp = new TempDirectory();
        using var queue = ChronicleQueue.Open(temp.Path, new QueueOptions
        {
            TimeProvider = clock,
            PreGrowChunkSize = 1,
        });
        var appender = queue.CreateAppender();

        appender.Append("a"u8); // day 1
        clock.Now = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        appender.Append("b"u8); // rolls to day 2, sealing day 1
        clock.Now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        appender.Append("c"u8); // clock backwards: must stay in the active day-2 file

        string day1 = Path.Combine(temp.Path, "20260101.cnq");
        Assert.Equal(2, Directory.GetFiles(temp.Path, "*.cnq").Length);
        Assert.Equal(28, QueueFiles.ReadAllBytes(day1).Length); // sealed: 1 record + mark, unchanged

        var tailer = queue.CreateTailer();
        tailer.ToStart();
        var read = new List<string>();
        while (tailer.TryRead(out var payload))
        {
            read.Add(Encoding.UTF8.GetString(payload));
        }

        Assert.Equal(new[] { "a", "b", "c" }, read);
    }
}
