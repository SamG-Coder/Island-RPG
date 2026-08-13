using System.Buffers;
using System.Buffers.Binary;

namespace IslandRpg.Protocol;

/// <summary>
/// Reads and writes one length-prefixed reliable message at a time. The unsigned
/// 32-bit little-endian prefix excludes the prefix itself and is validated before
/// renting a payload buffer.
/// </summary>
public static class TcpFrameCodec
{
    public static async ValueTask WriteAsync(Stream stream, IProtocolMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await WriteFrameAsync(stream, ReliableProtocolCodec.Encode(message), cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (frame.Length is < ProtocolConstants.ReliableHeaderSize or > ProtocolConstants.MaxReliableFrameBytes)
        {
            throw new ProtocolException($"Reliable frame length {frame.Length} is outside the permitted range.");
        }

        var prefix = new byte[ProtocolConstants.TcpLengthPrefixSize];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)frame.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns null only when the stream closes before any prefix byte arrives.</summary>
    public static async ValueTask<IProtocolMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[ProtocolConstants.TcpLengthPrefixSize];
        var firstRead = await stream.ReadAsync(prefix.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (firstRead == 0)
        {
            return null;
        }

        await ReadExactlyAsync(stream, prefix.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var frameLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (frameLength is < ProtocolConstants.ReliableHeaderSize or > ProtocolConstants.MaxReliableFrameBytes)
        {
            throw new ProtocolException($"Declared reliable frame length {frameLength} is outside the permitted range.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent((int)frameLength);
        try
        {
            var frame = buffer.AsMemory(0, (int)frameLength);
            await ReadExactlyAsync(stream, frame, cancellationToken).ConfigureAwait(false);
            return ReliableProtocolCodec.Decode(frame.Span);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = await stream.ReadAsync(destination[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("TCP stream closed in the middle of a protocol frame.");
            }

            read += count;
        }
    }
}
