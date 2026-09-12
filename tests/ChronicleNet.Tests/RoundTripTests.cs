using System.Text;

namespace ChronicleNet.Tests;

public class RoundTripTests
{
    [Fact]
    public void Appended_payloads_read_back_in_order()
    {
        using var temp = new TempDirectory();
        using var queue = ChronicleQueue.Open(temp.Path);
        var appender = queue.CreateAppender();

        string[] payloads = ["one", "two", "three", "four"];
        foreach (string payload in payloads)
        {
            appender.Append(Encoding.UTF8.GetBytes(payload));
        }

        var tailer = queue.CreateTailer();
        tailer.ToStart();

        var read = new List<string>();
        while (tailer.TryRead(out var payload))
        {
            read.Add(Encoding.UTF8.GetString(payload));
        }

        Assert.Equal(payloads, read);
    }

    [Fact]
    public void Indexes_are_sequential_within_a_day()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var temp = new TempDirectory();
        using var queue = ChronicleQueue.Open(temp.Path, new QueueOptions { TimeProvider = clock });
        var appender = queue.CreateAppender();

        int cycle = new DateOnly(2026, 1, 1).DayNumber - new DateOnly(1970, 1, 1).DayNumber;

        for (int i = 0; i < 5; i++)
        {
            long expected = ((long)cycle << 32) | (uint)i;
            Assert.Equal(expected, appender.Append([(byte)i]));
        }

        var tailer = queue.CreateTailer();
        tailer.ToStart();

        for (int i = 0; i < 5; i++)
        {
            Assert.True(tailer.TryRead(out _));
            Assert.Equal(((long)cycle << 32) | (uint)i, tailer.CurrentIndex);
        }

        Assert.False(tailer.TryRead(out _));
    }
}
