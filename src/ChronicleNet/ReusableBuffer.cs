namespace ChronicleNet;

// A single reusable byte[] that grows on demand. One instance per appender/tailer
// keeps the hot paths allocation-free after warm-up. The span handed out stays
// valid only until the next call.
internal sealed class ReusableBuffer(int initialSize)
{
    private byte[] _buffer = new byte[initialSize];

    public Span<byte> Get(int length)
    {
        if (_buffer.Length < length)
        {
            _buffer = new byte[length];
        }

        return _buffer.AsSpan(0, length);
    }
}
