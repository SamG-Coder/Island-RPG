namespace IslandRpg.Protocol;

/// <summary>Raised when untrusted network input violates the wire contract.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message)
        : base(message)
    {
    }

    public ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
