using System.Text;

namespace ChronicleNet.Tests;

public class MultiTailerTests
{
    [Fact]
    public void Tailers_from_different_positions_each_see_every_record()
    {
        using var temp = new TempDirectory();
        using var queue = ChronicleQueue.Open(temp.Path);
        var appender = queue.CreateAppender();
        appender.Append("a"u8);
        appender.Append("b"u8);
        appender.Append("c"u8);

        var fromStart = queue.CreateTailer();
        var fromSecond = queue.CreateTailer();
        fromStart.ToStart();
        fromSecond.ToStart();

        // Advance one tailer so the two cursors sit at different positions.
        Assert.True(fromSecond.TryRead(out var first));
        Assert.Equal("a", Encoding.UTF8.GetString(first));

        Assert.Equal(new[] { "a", "b", "c" }, ReadAll(fromStart));
        Assert.Equal(new[] { "b", "c" }, ReadAll(fromSecond));
    }

    [Fact]
    public void Reading_at_the_end_reports_not_present_then_observes_new_records()
    {
        using var temp = new TempDirectory();
        using var queue = ChronicleQueue.Open(temp.Path);
        var appender = queue.CreateAppender();
        appender.Append("a"u8);

        var atEnd = queue.CreateTailer();
        atEnd.ToEnd();

        Assert.False(atEnd.TryRead(out _)); // nothing new, no error, writer unaffected

        appender.Append("b"u8);

        Assert.True(atEnd.TryRead(out var payload));
        Assert.Equal("b", Encoding.UTF8.GetString(payload));
        Assert.False(atEnd.TryRead(out _));
    }

    private static string[] ReadAll(Tailer tailer)
    {
        var read = new List<string>();
        while (tailer.TryRead(out var payload))
        {
            read.Add(Encoding.UTF8.GetString(payload));
        }

        return read.ToArray();
    }
}
