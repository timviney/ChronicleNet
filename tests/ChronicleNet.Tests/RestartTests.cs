using System.Text;

namespace ChronicleNet.Tests;

public class RestartTests
{
    [Fact]
    public void Reopening_same_day_preserves_records_and_continues_sequence()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var temp = new TempDirectory();

        using (var queue = ChronicleQueue.Open(temp.Path, new QueueOptions { TimeProvider = clock }))
        {
            var appender = queue.CreateAppender();
            appender.Append("a"u8);
            appender.Append("b"u8);
        }

        long thirdIndex;
        using (var queue = ChronicleQueue.Open(temp.Path, new QueueOptions { TimeProvider = clock }))
        {
            thirdIndex = queue.CreateAppender().Append("c"u8);

            var tailer = queue.CreateTailer();
            tailer.ToStart();
            var read = new List<string>();
            while (tailer.TryRead(out var payload))
            {
                read.Add(Encoding.UTF8.GetString(payload));
            }

            Assert.Equal(new[] { "a", "b", "c" }, read);
        }

        Assert.Equal(2u, (uint)thirdIndex);
    }

    [Fact]
    public void Fabricated_WIP_tail_is_superseded_by_the_next_append()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var temp = new TempDirectory();
        string file;

        using (var queue = ChronicleQueue.Open(temp.Path, new QueueOptions
        {
            TimeProvider = clock,
            PreGrowChunkSize = 1,
        }))
        {
            queue.CreateAppender().Append("a"u8);
            file = QueueFiles.Single(temp.Path);
        }

        // Simulate a crash after claim: a WIP header claiming 4 bytes at the write position.
        // "a" occupies the aligned 8-byte record, so the next write position is 16 + 8.
        long writePosition = Format.FileHeaderLength + 8;
        QueueFiles.PokeInt32(file, writePosition, Format.NotComplete | 4);

        using (var queue = ChronicleQueue.Open(temp.Path, new QueueOptions
        {
            TimeProvider = clock,
            PreGrowChunkSize = 1,
        }))
        {
            long index = queue.CreateAppender().Append("b"u8);
            Assert.Equal(1u, (uint)index); // the abandoned record did not consume a sequence

            var tailer = queue.CreateTailer();
            tailer.ToStart();
            var read = new List<string>();
            while (tailer.TryRead(out var payload))
            {
                read.Add(Encoding.UTF8.GetString(payload));
            }

            Assert.Equal(new[] { "a", "b" }, read);
        }
    }
}
