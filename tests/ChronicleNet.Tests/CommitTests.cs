using System.Text;

namespace ChronicleNet.Tests;

public class CommitTests
{
    [Fact]
    public void Record_with_WIP_set_is_invisible_and_commit_makes_it_readable()
    {
        using var temp = new TempDirectory();
        using var queue = ChronicleQueue.Open(temp.Path, new QueueOptions { PreGrowChunkSize = 1 });
        queue.CreateAppender().Append("hello"u8);

        string file = QueueFiles.Single(temp.Path);
        long recordOffset = Format.FileHeaderLength; // first record starts after the file header
        const int length = 5;

        Assert.Equal("hello", ReadFirst(queue));

        // Simulate the writer's claim: WIP set, payload already on disk. The record (and
        // everything after it) must be invisible.
        QueueFiles.PokeInt32(file, recordOffset, Format.NotComplete | length);
        Assert.Null(ReadFirst(queue));

        // Simulate the commit: WIP cleared. The record is now readable.
        QueueFiles.PokeInt32(file, recordOffset, length);
        Assert.Equal("hello", ReadFirst(queue));
    }

    private static string? ReadFirst(ChronicleQueue queue)
    {
        var tailer = queue.CreateTailer();
        tailer.ToStart();
        return tailer.TryRead(out var payload) ? Encoding.UTF8.GetString(payload) : null;
    }
}
