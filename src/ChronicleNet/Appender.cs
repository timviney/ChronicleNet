using System.Buffers;

namespace ChronicleNet;

public sealed class Appender(ChronicleQueue queue)
{
    public long Append(ReadOnlySpan<byte> payload) // Example: payload = "hello" => [68 65 6c 6c 6f]
    {
        switch (payload.Length)
        {
            case 0:
                throw new ArgumentOutOfRangeException(nameof(payload), "Payload must contain at least one byte.");
            case > Framing.MaxPayloadLength:
                throw new ArgumentOutOfRangeException(nameof(payload), "Payload exceeds the maximum length of 2^30 - 1 bytes.");
        }

        lock (queue.WriteLock) // for now, only a single writer is supported
        {
            queue.ThrowIfDisposed();

            Segment segment = queue.ActiveSegment;
            int currentCycle = Segment.CycleFor(queue.UtcNow);

            if (currentCycle > segment.Cycle)
            {
                queue.RollTo(currentCycle);
                segment = queue.ActiveSegment;
            }
            
            int length = payload.Length; // Example: hello length = 5
            int paddedLength = Framing.AlignedRecordLength(length); // Example: paddedLength = 12 (5 + 4 header + 3 padding)
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

                // Example: buffer = [ 05 00 00 80 68 65 6c 6c 6f 00 00 00 ]
                //                     ^_header__^ ^__payload___^ ^_pad__^
                segment.Storage.WriteAt(writePosition, buffer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            // Commit: a second 4-byte write clears WIP (NotComplete). Readers observe either the
            // claim (not present) or the committed header (present); never a torn mix.
            Span<byte> commit = stackalloc byte[Framing.HeaderLength];
            Framing.WriteHeader(commit, length); // Example: commit = [ 05 00 00 00 ] (clears WIP)
            
            // Example: [ 05 00 00 00 68 65 6c 6c 6f 00 00 00 ]
            //            ^_header__^ ^__payload___^ ^_pad__^
            segment.Storage.WriteAt(writePosition, commit);

            segment.Advance(length);

            return ((long)segment.Cycle << 32) | (uint)sequence;
        }
    }
}
