namespace ChronicleNet;

public sealed class ChronicleQueue : IDisposable
{
    private readonly object _writeLock = new();
    private readonly string _directory;
    private readonly TimeProvider _timeProvider;
    private readonly long _preGrowChunkSize;

    private Segment _activeSegment;
    private bool _disposed;

    private ChronicleQueue(string directory, QueueOptions options)
    {
        _directory = directory;
        _timeProvider = options.TimeProvider;
        _preGrowChunkSize = options.PreGrowChunkSize;
        _activeSegment = OpenActiveSegment();
    }

    public static ChronicleQueue Open(string directory, QueueOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        QueueOptions resolved = options ?? new QueueOptions();
        Directory.CreateDirectory(directory);

        return new ChronicleQueue(directory, resolved);
    }

    public Appender CreateAppender()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new Appender(this);
    }

    internal object WriteLock => _writeLock;

    internal TimeProvider TimeProvider => _timeProvider;

    internal Segment ActiveSegment => _activeSegment;

    internal void RollTo(int cycle)
    {
        _activeSegment.Seal();
        _activeSegment.Dispose();
        _activeSegment = Segment.Create(_directory, cycle, _preGrowChunkSize);
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeSegment.Dispose();
        }
    }

    internal void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
            ? Segment.Create(_directory, currentCycle, _preGrowChunkSize)
            : Segment.Open(latest, _preGrowChunkSize);
    }
}
