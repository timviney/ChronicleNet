namespace ChronicleNet;

public sealed class Tailer(ChronicleQueue queue, int initialBufferSize = 4096) : IDisposable
{
    // Independent cursor: the day file being read and the byte offset within it.
    // Reading never mutates the log and never coordinates with the writer or other
    // tailers, so each tailer owns its own copy of this position.
    private int _cycle;
    private long _offset;
    
    private int _sequence;
    private long _index = -1;

    private IStorage? _storage;
    private int _storageCycle;
    private readonly ReusableBuffer _buffer = new(initialBufferSize);
    private bool _disposed;

    internal int Cycle => _cycle;

    internal long Offset => _offset;

    /// <summary>
    /// The index (<c>(cycle &lt;&lt; 32) | sequence-in-day</c>) of the record returned by the
    /// most recent successful <see cref="TryRead"/>, or -1 if no record has been read since
    /// the cursor was last positioned with <see cref="ToStart"/> or <see cref="ToEnd"/>.
    /// A failed <see cref="TryRead"/> leaves this unchanged.
    /// </summary>
    public long CurrentIndex => _index;

    /// <summary>
    /// Positions the cursor at the first record of the earliest day file. Resets
    /// <see cref="CurrentIndex"/> to -1 until a record is read.
    /// </summary>
    public void ToStart()
    {
        queue.ThrowIfDisposed();

        _cycle = queue.EarliestCycle();
        _offset = FileHeader.Length;
        _sequence = 0;
        _index = -1;
    }

    /// <summary>
    /// Positions the cursor at the current write position, seeing only later records.
    /// Resets <see cref="CurrentIndex"/> to -1 until a record is read.
    /// </summary>
    public void ToEnd()
    {
        queue.ThrowIfDisposed();

        Segment active = queue.ActiveSegment;
        _cycle = active.Cycle;
        _offset = active.WritePosition;
        _sequence = active.RecordCount;
        _index = -1;
    }

    /// <summary>
    /// Reads the next committed record, if one is present, without blocking.
    /// </summary>
    /// <param name="payload">
    /// The record's payload. This is a view over an internal reusable buffer and is valid
    /// only until the next call to <see cref="TryRead"/> on this tailer. Copy it if it must
    /// outlive that call.
    /// </param>
    /// <returns>
    /// <c>true</c> when a committed record was read; <c>false</c> when the cursor is at the
    /// end of the data, at a write-in-progress (or otherwise unreadable) record, or at an
    /// implausible header.
    /// </returns>
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

            _index = ((long)_cycle << 32) | (uint)_sequence;
            _sequence++;
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
        _sequence = 0; // sequence-in-day restarts in the new file
        return true;
    }
}
