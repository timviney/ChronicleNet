namespace ChronicleNet;

public sealed class QueueFormatException : IOException
{
    public QueueFormatException(string message) : base(message) { }

    public QueueFormatException(string message, Exception innerException) : base(message, innerException) { }
}
