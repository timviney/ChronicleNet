namespace ChronicleNet;

// A warm reusable buffer; the span it returns is only valid until the next call.
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
