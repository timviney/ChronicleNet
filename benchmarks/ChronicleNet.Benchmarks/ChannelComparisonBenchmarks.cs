using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using ChronicleNet;

namespace ChronicleNet.Benchmarks;

// Rough throughput comparison against System.Threading.Channels.Channel.
//
// This is not apples-to-apples: Channel is in-memory and destructive (a read removes the
// item), while ChronicleNet is persisted and non-destructive (a tailer never consumes).
// It answers "what does persistence cost?" rather than "which is better".
public abstract class ChannelComparisonBase
{
    protected const int Count = 10_000;

    protected readonly byte[] Payload = new byte[64];

    protected string DirectoryPath = null!;
    protected ChronicleQueue Queue = null!;
    protected Appender Appender = null!;
    protected Tailer Tailer = null!;
    protected Channel<byte[]> Items = null!;

    [IterationSetup]
    public void Setup()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "chroniclenet-bench-" + Guid.NewGuid().ToString("N"));
        Queue = ChronicleQueue.Open(DirectoryPath);
        Appender = Queue.CreateAppender();
        Tailer = Queue.CreateTailer();
        Items = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    }

    [IterationCleanup]
    public void Cleanup()
    {
        Queue.Dispose();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}

[MemoryDiagnoser]
public class WriteComparisonBenchmark : ChannelComparisonBase
{
    [Benchmark(Baseline = true, OperationsPerInvoke = Count)]
    public int ChannelWrite()
    {
        for (int i = 0; i < Count; i++)
        {
            Items.Writer.TryWrite(Payload);
        }

        return Count;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public int QueueWrite()
    {
        for (int i = 0; i < Count; i++)
        {
            Appender.Append(Payload);
        }

        return Count;
    }
}

[MemoryDiagnoser]
public class RoundTripComparisonBenchmark : ChannelComparisonBase
{
    [Benchmark(Baseline = true, OperationsPerInvoke = Count)]
    public int ChannelRoundTrip()
    {
        for (int i = 0; i < Count; i++)
        {
            Items.Writer.TryWrite(Payload);
        }

        int read = 0;
        while (Items.Reader.TryRead(out _))
        {
            read++;
        }

        return read;
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public int QueueRoundTrip()
    {
        Tailer.ToEnd(); // read exactly the records this invocation appends
        for (int i = 0; i < Count; i++)
        {
            Appender.Append(Payload);
        }

        int read = 0;
        while (read < Count && Tailer.TryRead(out _))
        {
            read++;
        }

        return read;
    }
}
