namespace ChronicleNet;

internal static class FileHeader
{
    public const int Length = 16;  // magic (4) + version (4) + cycle (4) + reserved (4)

    public const uint Version = 1;

    // "CNQF" = ChronicleNet Queue Format. u8 literals are stored in static data,
    // so this span is safe to return from a property.
    private static ReadOnlySpan<byte> Magic => "CNQF"u8; // Stop-gap until we have richer metadata

    public static void Write(Span<byte> destination, int cycle)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException($"Destination must be at least {Length} bytes.", nameof(destination));
        }

        Magic.CopyTo(destination);                       // bytes 0..3  magic
        Framing.WriteHeader(destination[4..], (int)Version); // bytes 4..7  version (uint32 LE)
        Framing.WriteHeader(destination[8..], cycle);    // bytes 8..11 cycle (int32 LE)
        Framing.WriteHeader(destination[12..], 0);       // bytes 12..15 reserved
    }

    public static int ReadCycle(ReadOnlySpan<byte> source)
    {
        return Framing.ReadHeader(source[8..]);
    }

    public static void Validate(ReadOnlySpan<byte> source)
    {
        if (source.Length < Length)
        {
            throw new QueueFormatException($"File header too short: {source.Length} bytes (expected {Length}).");
        }

        if (!source[..4].SequenceEqual(Magic))
        {
            throw new QueueFormatException("Unrecognized file magic; expected \"CNQF\".");
        }

        uint version = unchecked((uint)Framing.ReadHeader(source[4..]));
        if (version != Version)
        {
            throw new QueueFormatException($"Unsupported format version {version}; expected {Version}.");
        }
    }
}
