using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace IslandRpg.Protocol;

internal sealed class WireWriter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ArrayBufferWriter<byte> _buffer;

    public WireWriter(int initialCapacity = 128) => _buffer = new(initialCapacity);

    public int Length => _buffer.WrittenCount;
    public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

    public void WriteByte(byte value)
    {
        var destination = _buffer.GetSpan(1);
        destination[0] = value;
        _buffer.Advance(1);
    }

    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);
    public void WriteUInt16(ushort value) => Write(value, BinaryPrimitives.WriteUInt16LittleEndian);
    public void WriteInt16(short value) => Write(value, BinaryPrimitives.WriteInt16LittleEndian);
    public void WriteUInt32(uint value) => Write(value, BinaryPrimitives.WriteUInt32LittleEndian);
    public void WriteInt32(int value) => Write(value, BinaryPrimitives.WriteInt32LittleEndian);
    public void WriteUInt64(ulong value) => Write(value, BinaryPrimitives.WriteUInt64LittleEndian);
    public void WriteSingle(float value) => WriteUInt32(BitConverter.SingleToUInt32Bits(value));
    public void WriteDouble(double value) => WriteUInt64(BitConverter.DoubleToUInt64Bits(value));

    public void WriteGuid(Guid value)
    {
        var destination = _buffer.GetSpan(16);
        if (!value.TryWriteBytes(destination, bigEndian: true, out var bytesWritten) || bytesWritten != 16)
        {
            throw new InvalidOperationException("Could not encode a GUID.");
        }

        _buffer.Advance(16);
    }

    public void WriteString(string value, int maxBytes, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value);
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ProtocolException($"{fieldName} is not valid Unicode.", exception);
        }

        if (byteCount > maxBytes || byteCount > ushort.MaxValue)
        {
            throw new ProtocolException($"{fieldName} exceeds its {maxBytes}-byte UTF-8 limit.");
        }

        WriteUInt16((ushort)byteCount);
        var destination = _buffer.GetSpan(byteCount);
        try
        {
            var written = StrictUtf8.GetBytes(value.AsSpan(), destination);
            _buffer.Advance(written);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ProtocolException($"{fieldName} is not valid Unicode.", exception);
        }
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException("Destination is too small.", nameof(destination));
        }

        WrittenSpan.CopyTo(destination);
    }

    private void Write<T>(T value, SpanValueWriter<T> writer)
        where T : unmanaged
    {
        var length = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        var destination = _buffer.GetSpan(length);
        writer(destination, value);
        _buffer.Advance(length);
    }

    private delegate void SpanValueWriter<in T>(Span<byte> destination, T value);
}

internal ref struct WireReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReadOnlySpan<byte> _source;
    private int _offset;

    public WireReader(ReadOnlySpan<byte> source)
    {
        _source = source;
        _offset = 0;
    }

    public int Remaining => _source.Length - _offset;

    public byte ReadByte() => Take(1)[0];

    public bool ReadBoolean()
    {
        var value = ReadByte();
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new ProtocolException("Boolean field was neither zero nor one."),
        };
    }

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(sizeof(ushort)));
    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(sizeof(short)));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(sizeof(uint)));
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(sizeof(ulong)));
    public float ReadSingle() => BitConverter.UInt32BitsToSingle(ReadUInt32());
    public double ReadDouble() => BitConverter.UInt64BitsToDouble(ReadUInt64());

    public Guid ReadGuid()
    {
        var bytes = Take(16);
        return new Guid(bytes, bigEndian: true);
    }

    public string ReadString(int maxBytes, string fieldName)
    {
        var byteCount = ReadUInt16();
        if (byteCount > maxBytes)
        {
            throw new ProtocolException($"{fieldName} exceeds its {maxBytes}-byte UTF-8 limit.");
        }

        try
        {
            return StrictUtf8.GetString(Take(byteCount));
        }
        catch (DecoderFallbackException exception)
        {
            throw new ProtocolException($"{fieldName} is not valid UTF-8.", exception);
        }
    }

    public void EnsureConsumed()
    {
        if (Remaining != 0)
        {
            throw new ProtocolException($"Message contains {Remaining} unexpected trailing bytes.");
        }
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new ProtocolException("Message ended before all declared fields were read.");
        }

        var result = _source.Slice(_offset, count);
        _offset += count;
        return result;
    }
}
