namespace ChronicleNet;

public sealed class Tailer(ChronicleQueue queue)
{
    // Independent cursor: the day file being read and the byte offset within it.
    // Reading never mutates the log and never coordinates with the writer or other
    // tailers, so each tailer owns its own copy of this position.
    internal int Cycle { get; private set; }

    internal long Offset { get; private set; }

    /// <summary>Positions the cursor at the first record of the earliest day file.</summary>
    public void ToStart()
    {
        queue.ThrowIfDisposed();

        Cycle = queue.EarliestCycle();
        Offset = FileHeader.Length;
    }

    /// <summary>Positions the cursor at the current write position, seeing only later records.</summary>
    public void ToEnd()
    {
        queue.ThrowIfDisposed();

        Segment active = queue.ActiveSegment;
        Cycle = active.Cycle;
        Offset = active.WritePosition;
    }
}
