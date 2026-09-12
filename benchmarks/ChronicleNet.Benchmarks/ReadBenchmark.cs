using BenchmarkDotNet.Attributes;
using ChronicleNet;

namespace ChronicleNet.Benchmarks;

[MemoryDiagnoser]
public class ReadBenchmark
{
    private const int RecordCount = 100_000;

    private readonly byte[] _payload = new byte[64];
    private string _directory = null!;
    private ChronicleQueue _queue = null!;
    private Tailer _tailer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "chroniclenet-bench-" + Guid.NewGuid().ToString("N"));
        _queue = ChronicleQueue.Open(_directory);
        var appender = _queue.CreateAppender();
        for (int i = 0; i < RecordCount; i++)
        {
            appender.Append(_payload);
        }

        _tailer = _queue.CreateTailer();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _queue.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Benchmark(OperationsPerInvoke = RecordCount)]
    public int ReadAll()
    {
        _tailer.ToStart();
        int count = 0;
        while (_tailer.TryRead(out _))
        {
            count++;
        }

        return count;
    }
}
