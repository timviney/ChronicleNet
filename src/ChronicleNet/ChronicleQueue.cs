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
    private readonly List<Tailer> _tailers = [];
    private bool _disposed;

    private ChronicleQueue(string directory, QueueOptions options)
    {
        _directory = directory;
        _timeProvider = options.TimeProvider;
        ActiveSegment = OpenActiveSegment();
    }

    internal object WriteLock { get; } = new();
    internal Segment ActiveSegment { get; private set; }
    internal string DirectoryPath => _directory;
    public DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public Appender CreateAppender()
    {
        ThrowIfDisposed();
        return new Appender(this);
    }

    /// <summary>
    /// Creates an independent read cursor over this queue. The queue owns the tailer
    /// and disposes it (releasing its file handle) when the queue is disposed, so
    /// callers do not need to dispose tailers explicitly.
    /// </summary>
    public Tailer CreateTailer()
    {
        lock (WriteLock)
        {
            ThrowIfDisposed();

            var tailer = new Tailer(this);
            _tailers.Add(tailer);
            return tailer;
        }
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
    
    // The earliest day file in the directory is the lexicographic minimum, since
    // yyyyMMdd.cnq sorts chronologically. Falls back to the active segment if the
    // directory has no files yet.
    internal int EarliestCycle()
    {
        string? earliest = Directory
            .GetFiles(_directory, "*" + Segment.Extension)
            .MinBy(Path.GetFileName);

        return earliest is null
            ? ActiveSegment.Cycle
            : Segment.CycleForFileName(earliest);
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

            foreach (Tailer tailer in _tailers)
            {
                tailer.Dispose();
            }
            _tailers.Clear();

            ActiveSegment.Dispose();
        }
    }
}
