namespace ChronicleNet;

internal static class Framing
{
    public const int HeaderLength = 4;

    public const int NotComplete = unchecked((int)0x8000_0000); // bit 31: write in progress
    public const int MetaData = 0x4000_0000;                    // bit 30: metadata / end-of-data
    public const int LengthMask = 0x3FFF_FFFF;                  // bits 0..29: payload length

    public const int EndOfData = NotComplete | MetaData;

    public const int MaxPayloadLength = LengthMask; // ie never go above 30 bits to avoid conflicting with flags above

    public const int Alignment = 4;

    // Alignment - 1 = 3 = binary 0b000...011. The ~ flips every bit, giving
    // 0b111...100, a mask whose low two bits are 0. AND-ing with it clears the
    // low two bits, which rounds down to a multiple of 4. But, by adding 3 first
    // (Alignment - 1), we ultimately round up to a multiple of 4 instead.
    public static int AlignedRecordLength(int payloadLength)
        => (payloadLength + HeaderLength + Alignment - 1) & ~(Alignment - 1);

    // Encodes the header as little-endian: the least-significant byte goes
    // first. Each byte is extracted by shifting the header right by a multiple
    // of 8 and truncating to the low 8 bits via the (byte) cast (the cast
    // discards the high bits, so it also works for negative values).
    public static void WriteHeader(Span<byte> destination, int header)
    {
        destination[0] = (byte)header;          // bits  0..7
        destination[1] = (byte)(header >> 8);   // bits  8..15
        destination[2] = (byte)(header >> 16);  // bits 16..23
        destination[3] = (byte)(header >> 24);  // bits 24..31
    }

    // Reverse of WriteHeader: reassemble the int32 from least- to most-significant byte.
    public static int ReadHeader(ReadOnlySpan<byte> source)
    {
        return source[0]
             | (source[1] << 8)
             | (source[2] << 16)
             | (source[3] << 24);
    }

    public static int DecodeLength(int header) => header & LengthMask;

    public static bool IsNotComplete(int header) => (header & NotComplete) != 0;

    public static bool IsMetaData(int header) => (header & MetaData) != 0;

    public static bool IsEndOfData(int header) => header == EndOfData;
}
