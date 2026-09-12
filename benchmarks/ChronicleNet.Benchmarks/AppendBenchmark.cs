using BenchmarkDotNet.Attributes;
using ChronicleNet;

namespace ChronicleNet.Benchmarks;

[MemoryDiagnoser]
public class AppendBenchmark
{
    private readonly byte[] _payload = new byte[64];
    private string _directory = null!;
    private ChronicleQueue _queue = null!;
    private Appender _appender = null!;

    // A fresh queue per iteration keeps disk usage bounded and isolates the append path
    // from file-creation cost (the setup runs outside the measurement window).
    [IterationSetup]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "chroniclenet-bench-" + Guid.NewGuid().ToString("N"));
        _queue = ChronicleQueue.Open(_directory);
        _appender = _queue.CreateAppender();
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _queue.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Benchmark]
    public long Append() => _appender.Append(_payload);
}
