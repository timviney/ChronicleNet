using System.Buffers;

namespace ChronicleNet;

public sealed class Appender
{
    private readonly ChronicleQueue _queue;

    internal Appender(ChronicleQueue queue)
    {
        _queue = queue;
    }

    public long Append(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload must contain at least one byte.");
        }

        if (payload.Length > Framing.MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload exceeds the maximum length of 2^30 - 1 bytes.");
        }

        lock (_queue.WriteLock)
        {
            _queue.ThrowIfDisposed();

            Segment segment = _queue.ActiveSegment;
            int currentCycle = Segment.CycleFor(_queue.TimeProvider.GetUtcNow().UtcDateTime);

            if (currentCycle > segment.Cycle)
            {
                _queue.RollTo(currentCycle);
                segment = _queue.ActiveSegment;
            }

            // currentCycle <= segment.Cycle: clock moved backwards (or is steady).
            // Keep appending to the active file; never write past an end-of-data mark.

            int length = payload.Length;
            int paddedLength = Framing.AlignedRecordLength(length);
            long writePosition = segment.WritePosition;
            int sequence = segment.RecordCount;

            byte[] rented = ArrayPool<byte>.Shared.Rent(paddedLength);
            try
            {
                var buffer = rented.AsSpan(0, paddedLength);

                // Claim: header carries WIP | length; payload follows; padding is
                // zeroed so the on-disk bytes are deterministic.
                Framing.WriteHeader(buffer, Framing.NotComplete | length);
                payload.CopyTo(buffer[Framing.HeaderLength..]);
                buffer[(Framing.HeaderLength + length)..].Clear();

                segment.Storage.WriteAt(writePosition, buffer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            // Commit: a second 4-byte write clears WIP. Readers observe either the
            // claim (not present) or the committed header (present); never a torn mix.
            Span<byte> commit = stackalloc byte[Framing.HeaderLength];
            Framing.WriteHeader(commit, length);
            segment.Storage.WriteAt(writePosition, commit);

            segment.Advance(length);

            return ((long)segment.Cycle << 32) | (uint)sequence;
        }
    }
}
