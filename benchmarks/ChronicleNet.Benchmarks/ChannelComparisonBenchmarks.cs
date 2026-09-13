using System.Buffers.Binary;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using ChronicleNet;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace ChronicleNet.Benchmarks;

// Rough throughput comparison against System.Threading.Channels.Channel, a raw
// (unframed, unflushed) FileStream append, and a Kafka producer/consumer.
//
// This is not apples-to-apples. Channel is in-memory and destructive (a read removes the
// item); the FileStream baseline keeps no commit protocol and never flushes; Kafka is a
// networked, replicated broker accessed over TCP; ChronicleNet is persisted, crash-safe,
// and non-destructive (a tailer never consumes). It answers "what do the persistence and
// framing guarantees cost?" rather than "which is better".
//
// The Kafka cases are skipped unless KAFKA_BOOTSTRAP_SERVERS is set (e.g. localhost:9092),
// so the default run needs no broker.
public static class KafkaBenchmarkEnvironment
{
    public const string BootstrapServersVariable = "KAFKA_BOOTSTRAP_SERVERS";

    public static readonly string? BootstrapServers =
        Environment.GetEnvironmentVariable(BootstrapServersVariable);

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(BootstrapServers);
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresKafkaAttribute : Attribute
{
}

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

        // Drop [RequiresKafka] workloads when no broker is configured instead of failing.
        AddFilter(new SimpleFilter(benchmark =>
            KafkaBenchmarkEnvironment.IsConfigured ||
            !benchmark.Descriptor.WorkloadMethod.IsDefined(typeof(RequiresKafkaAttribute), inherit: false)));
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

    protected string KafkaTopic = null!;
    protected IProducer<Null, byte[]> KafkaProducer = null!;
    protected IConsumer<Null, byte[]> KafkaConsumer = null!;
    protected IAdminClient KafkaAdmin = null!;

    [GlobalSetup]
    public void KafkaGlobalSetup()
    {
        if (!KafkaBenchmarkEnvironment.IsConfigured)
        {
            return;
        }

        // One unique topic per benchmark case keeps iterations isolated without re-creating
        // the topic inside the measurement window.
        KafkaTopic = "chroniclenet-bench-" + Guid.NewGuid().ToString("N");
        KafkaAdmin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = KafkaBenchmarkEnvironment.BootstrapServers,
        }).Build();
        KafkaAdmin.CreateTopicsAsync(new[]
        {
            new TopicSpecification { Name = KafkaTopic, NumPartitions = 1, ReplicationFactor = 1 },
        }).GetAwaiter().GetResult();

        // Acks.All mirrors ChronicleNet's commit-before-return durability guarantee. It is the
        // expensive but honest comparison; a fire-and-forget producer would flatter Kafka.
        KafkaProducer = new ProducerBuilder<Null, byte[]>(new ProducerConfig
        {
            BootstrapServers = KafkaBenchmarkEnvironment.BootstrapServers,
            Acks = Acks.All,
        }).Build();

        KafkaConsumer = new ConsumerBuilder<Null, byte[]>(new ConsumerConfig
        {
            BootstrapServers = KafkaBenchmarkEnvironment.BootstrapServers,
            GroupId = "chroniclenet-bench-" + Guid.NewGuid().ToString("N"),
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        // Assign directly (no group coordination); the position persists across iterations so a
        // round trip reads exactly the records it just produced.
        KafkaConsumer.Assign(new TopicPartitionOffset(KafkaTopic, new Partition(0), Offset.Beginning));
    }

    [GlobalCleanup]
    public void KafkaGlobalCleanup()
    {
        if (!KafkaBenchmarkEnvironment.IsConfigured)
        {
            return;
        }

        KafkaConsumer?.Dispose();
        KafkaProducer?.Dispose();

        if (KafkaAdmin is not null)
        {
            try
            {
                KafkaAdmin.DeleteTopicsAsync(new[] { KafkaTopic }).GetAwaiter().GetResult();
            }
            catch (KafkaException)
            {
                // Best effort: a leftover topic should not fail the run.
            }

            KafkaAdmin.Dispose();
        }
    }

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

    [Benchmark(OperationsPerInvoke = Count)]
    [RequiresKafka]
    public int KafkaWrite()
    {
        for (int i = 0; i < Count; i++)
        {
            KafkaProducer.Produce(KafkaTopic, new Message<Null, byte[]> { Value = Payload });
        }

        KafkaProducer.Flush(TimeSpan.FromSeconds(30));
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

    [Benchmark(OperationsPerInvoke = Count)]
    [RequiresKafka]
    public int KafkaRoundTrip()
    {
        for (int i = 0; i < Count; i++)
        {
            KafkaProducer.Produce(KafkaTopic, new Message<Null, byte[]> { Value = Payload });
        }

        KafkaProducer.Flush(TimeSpan.FromSeconds(30));

        int read = 0;
        while (read < Count)
        {
            ConsumeResult<Null, byte[]>? result = KafkaConsumer.Consume(5_000);
            if (result is null)
            {
                break;
            }

            read++;
        }

        return read;
    }
}
