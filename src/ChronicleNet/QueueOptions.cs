namespace ChronicleNet;

public sealed class QueueOptions
{
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
