namespace ChronicleNet;

public sealed class QueueOptions
{
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public long PreGrowChunkSize { get; init; } = RandomAccessStorage.DefaultPreGrowChunkSize;
}
