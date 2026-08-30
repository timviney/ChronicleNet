namespace ChronicleNet;

public sealed class ChronicleQueue : IDisposable
{
    public static ChronicleQueue Open(string directory, QueueOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        QueueOptions resolved = options ?? new QueueOptions();
        Directory.CreateDirectory(directory);

        return new ChronicleQueue(directory, resolved);
    }
    
    private readonly string _directory;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    private ChronicleQueue(string directory, QueueOptions options)
    {
        _directory = directory;
        _timeProvider = options.TimeProvider;
        ActiveSegment = OpenActiveSegment();
    }

    internal object WriteLock { get; } = new();
    internal Segment ActiveSegment { get; private set; }
    public DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public Appender CreateAppender()
    {
        ThrowIfDisposed();
        return new Appender(this);
    }
    
    internal void RollTo(int cycle)
    {
        ActiveSegment.Seal();
        ActiveSegment.Dispose();
        ActiveSegment = Segment.Create(_directory, cycle);
    }

    private Segment OpenActiveSegment()
    {
        int currentCycle = Segment.CycleFor(_timeProvider.GetUtcNow().UtcDateTime);

        // yyyyMMdd.cnq sorts lexicographically, so the maximum name is the most
        // recent day file.
        string? latest = Directory
            .GetFiles(_directory, "*" + Segment.Extension)
            .MaxBy(Path.GetFileName);

        return latest is null
            ? Segment.Create(_directory, currentCycle)
            : Segment.Open(latest);
    }
    
    internal void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
    
    public void Dispose()
    {
        lock (WriteLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ActiveSegment.Dispose();
        }
    }
}
