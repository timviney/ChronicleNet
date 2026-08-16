using System.Globalization;

namespace ChronicleNet;

internal sealed class Segment : IDisposable
{
    public const string Extension = ".cnq";

    private readonly IStorage _storage;

    public int Cycle { get; }

    public long WritePosition { get; private set; }

    public int RecordCount { get; private set; }

    private Segment(IStorage storage, int cycle, long writePosition, int recordCount)
    {
        _storage = storage;
        Cycle = cycle;
        WritePosition = writePosition;
        RecordCount = recordCount;
    }

    public IStorage Storage => _storage;

    public static string GetFileName(int cycle)
        => DateOnly.FromDateTime(DateTime.UnixEpoch.AddDays(cycle))
            .ToString("yyyyMMdd", CultureInfo.InvariantCulture) + Extension;

    public static string GetPath(string directory, int cycle)
        => Path.Combine(directory, GetFileName(cycle));

    public static Segment Create(string directory, int cycle)
    {
        string path = GetPath(directory, cycle);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        var storage = new RandomAccessStorage(stream);

        Span<byte> header = stackalloc byte[FileHeader.Length]; // no heap!
        FileHeader.Write(header, cycle);
        storage.WriteAt(0, header);

        return new Segment(storage, cycle, FileHeader.Length, 0);
    }

    public static Segment Open(string directory, int cycle)
    {
        string path = GetPath(directory, cycle);
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var storage = new RandomAccessStorage(stream);

        Span<byte> header = stackalloc byte[FileHeader.Length];
        int read = storage.ReadAt(0, header);
        if (read < FileHeader.Length)
        {
            storage.Dispose();
            throw new QueueFormatException($"File header too short: {read} bytes (expected {FileHeader.Length}).");
        }

        FileHeader.Validate(header);

        int fileCycle = FileHeader.ReadCycle(header);
        if (fileCycle != cycle)
        {
            storage.Dispose();
            throw new QueueFormatException($"Cycle mismatch: file holds {fileCycle}, expected {cycle}.");
        }

        (long writePosition, int recordCount) = Scan(storage);
        return new Segment(storage, cycle, writePosition, recordCount);
    }

    public void Dispose() => _storage.Dispose();

    private static (long WritePosition, int RecordCount) Scan(IStorage storage)
    {
        long offset = FileHeader.Length;
        int count = 0;
        Span<byte> header = stackalloc byte[Framing.HeaderLength];

        while (true)
        {
            int read = storage.ReadAt(offset, header);
            if (read < Framing.HeaderLength)
            {
                break; // past end of file
            }

            int value = Framing.ReadHeader(header);

            // Zero header = end of data; WIP set = crashed tail or end-of-data
            // mark. Either way, this offset is where the next append should land.
            if (value == 0 || Framing.IsNotComplete(value))
            {
                break;
            }

            int length = Framing.DecodeLength(value);
            if (length == 0)
            {
                break; // implausible: a complete record must carry >= 1 byte
            }

            offset += Framing.AlignedRecordLength(length);
            count++;
        }

        return (offset, count);
    }
}
