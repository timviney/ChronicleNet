using System.Collections.Concurrent;
using System.Text;

namespace ChronicleNet.Tests;

public class ConcurrencyTests
{
    [Fact]
    public void Concurrent_appends_yield_every_record_exactly_once_in_a_total_order()
    {
        using var temp = new TempDirectory();
        using var queue = ChronicleQueue.Open(temp.Path);

        const int threadCount = 4;
        const int perThread = 250;
        const int total = threadCount * perThread;

        var assignedIndexes = new ConcurrentBag<long>();
        var workers = new List<Thread>();

        for (int t = 0; t < threadCount; t++)
        {
            int id = t;
            var worker = new Thread(() =>
            {
                var appender = queue.CreateAppender();
                for (int i = 0; i < perThread; i++)
                {
                    assignedIndexes.Add(appender.Append(Encoding.UTF8.GetBytes($"{id}:{i}")));
                }
            });
            workers.Add(worker);
            worker.Start();
        }

        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        // Every append got a distinct, gap-free sequence number for the day.
        Assert.Equal(total, assignedIndexes.Count);
        Assert.Equal(
            Enumerable.Range(0, total).Select(i => (uint)i).ToArray(),
            assignedIndexes.Select(i => (uint)i).OrderBy(x => x).ToArray());

        // Reading back yields every record exactly once, in a strictly increasing total order.
        var tailer = queue.CreateTailer();
        tailer.ToStart();

        var read = new HashSet<string>();
        long previous = -1;
        while (tailer.TryRead(out var payload))
        {
            if (previous >= 0)
            {
                Assert.Equal(previous + 1, tailer.CurrentIndex);
            }

            previous = tailer.CurrentIndex;
            read.Add(Encoding.UTF8.GetString(payload));
        }

        Assert.Equal(total, read.Count);
        for (int t = 0; t < threadCount; t++)
        {
            for (int i = 0; i < perThread; i++)
            {
                Assert.Contains($"{t}:{i}", read);
            }
        }
    }
}
