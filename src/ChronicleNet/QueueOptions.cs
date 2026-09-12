namespace ChronicleNet;

public sealed class QueueOptions
{
    public const long DefaultPreGrowChunkSize = 64L * 1024 * 1024;
    
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    
    public long PreGrowChunkSize { get; init; } = DefaultPreGrowChunkSize;
}
