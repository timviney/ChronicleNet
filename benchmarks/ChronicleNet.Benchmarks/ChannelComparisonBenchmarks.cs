using System.Buffers.Binary;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using ChronicleNet;

namespace ChronicleNet.Benchmarks;

// Rough throughput comparison against System.Threading.Channels.Channel and a raw
// (unframed, unflushed) FileStream append.
//
// This is not apples-to-apples. Channel is in-memory and destructive (a read removes the
// item); the FileStream baseline keeps no commit protocol and never flushes; ChronicleNet
// is persisted, crash-safe, and non-destructive (a tailer never consumes). It answers
// "what do the persistence and framing guarantees cost?" rather than "which is better".
public class ComparisonConfig : ManualConfig
{
    public ComparisonConfig()
    {
        // InvocationCount = 1 so each measured iteration is exactly one isolated batch
        // (state is recreated in IterationSetup); more warmup/iterations for a rounded score.
        AddJob(Job.Default
            .WithInvocationCount(1)
            .WithUnrollFactor(1)
            .WithWarmupCount(5)
            .WithIterationCount(20)
            .WithLaunchCount(1));
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}

public abstract class ComparisonBase
{
    protected const int Count = 20_000;

    protected readonly byte[] Payload = new byte[64];

    protected string DirectoryPath = null!;
    protected ChronicleQueue Queue = null!;
    protected Appender Appender = null!;
    protected Tailer Tailer = null!;
    protected Channel<byte[]> Items = null!;
    protected FileStream RawLog = null!;

    [IterationSetup]
    public void Setup()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "chroniclenet-bench-" + Guid.NewGuid().ToString("N"));
        Queue = ChronicleQueue.Open(DirectoryPath);
        Appender = Queue.CreateAppender();
        Tailer = Queue.CreateTailer();
        Items = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        RawLog = new FileStream(Path.Combine(DirectoryPath, "raw.log"), FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        RawLog.Dispose();
        Queue.Dispose();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}

[Config(typeof(ComparisonConfig))]
public class WriteComparisonBenchmark : ComparisonBase
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
    public int FileStreamWrite()
    {
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, Payload.Length);
        for (int i = 0; i < Count; i++)
        {
            RawLog.Write(header);
            RawLog.Write(Payload);
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

[Config(typeof(ComparisonConfig))]
public class RoundTripComparisonBenchmark : ComparisonBase
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
