namespace ChronicleNet;

public sealed class Tailer(ChronicleQueue queue, int initialBufferSize = 4096) : IDisposable
{
    // Independent cursor: the day file being read and the byte offset within it.
    // Reading never mutates the log and never coordinates with the writer or other
    // tailers, so each tailer owns its own copy of this position.
    private int _cycle;
    private long _offset;

    private IStorage? _storage;
    private int _storageCycle;
    private readonly ReusableBuffer _buffer = new(initialBufferSize);
    private bool _disposed;

    internal int Cycle => _cycle;

    internal long Offset => _offset;

    /// <summary>Positions the cursor at the first record of the earliest day file.</summary>
    public void ToStart()
    {
        queue.ThrowIfDisposed();

        _cycle = queue.EarliestCycle();
        _offset = FileHeader.Length;
    }

    /// <summary>Positions the cursor at the current write position, seeing only later records.</summary>
    public void ToEnd()
    {
        queue.ThrowIfDisposed();

        Segment active = queue.ActiveSegment;
        _cycle = active.Cycle;
        _offset = active.WritePosition;
    }

    /// <summary>
    /// Reads the next committed record, if one is present. Returns false rather than
    /// blocking or throwing when the cursor is at the end of the data, at a write-in-progress
    /// record, or at an implausible header. The returned span is valid only until the next read.
    /// </summary>
    public bool TryRead(out ReadOnlySpan<byte> payload)
    {
        queue.ThrowIfDisposed();
        ObjectDisposedException.ThrowIf(_disposed, this);

        Span<byte> header = stackalloc byte[Framing.HeaderLength];

        while (true)
        {
            IStorage? storage = StorageForCurrentCycle();
            if (storage is null)
            {
                payload = default;
                return false;
            }

            int read = storage.ReadAt(_offset, header);
            if (read < Framing.HeaderLength)
            {
                payload = default;
                return false;
            }

            int info = Framing.ReadHeader(header);

            // All zeros = nothing written here yet.
            if (info == 0)
            {
                payload = default;
                return false;
            }

            // End-of-data mark: follow the roll into the next day's file if it exists.
            if (Framing.IsEndOfData(info))
            {
                if (!TryMoveToNextCycle())
                {
                    payload = default;
                    return false;
                }

                continue;
            }

            // A write in progress (or any other metadata) is treated as not present.
            if (Framing.IsNotComplete(info) || Framing.IsMetaData(info))
            {
                payload = default;
                return false;
            }

            int length = Framing.DecodeLength(info);
            if (length == 0)
            {
                payload = default; // implausible committed header
                return false;
            }

            Span<byte> buffer = _buffer.Get(length);
            int payloadRead = storage.ReadAt(_offset + Framing.HeaderLength, buffer);
            if (payloadRead < length)
            {
                payload = default; // claimed payload runs past the end of the file
                return false;
            }

            _offset += Framing.AlignedRecordLength(length);
            payload = buffer;
            return true;
        }
    }

    public void Dispose()
    {
        _storage?.Dispose();
        _storage = null;
        _disposed = true;
    }

    // The cursor only ever sits on one day file at a time, so one cached handle is
    // enough; it is swapped out when the cursor moves to another cycle.
    private IStorage? StorageForCurrentCycle()
    {
        if (_storage is not null && _storageCycle == _cycle)
        {
            return _storage;
        }

        _storage?.Dispose();
        _storage = null;

        string path = Segment.GetPath(queue.DirectoryPath, _cycle);
        if (!File.Exists(path))
        {
            return null; // the day file does not exist yet
        }

        _storage = RandomAccessStorage.OpenRead(path);
        _storageCycle = _cycle;
        return _storage;
    }

    private bool TryMoveToNextCycle()
    {
        int next = _cycle + 1;
        if (!File.Exists(Segment.GetPath(queue.DirectoryPath, next)))
        {
            return false;
        }

        _cycle = next;
        _offset = FileHeader.Length;
        return true;
    }
}
