using System.Buffers.Binary;

namespace IslandRpg.Protocol;

/// <summary>Allocation-free codec for unreliable snapshots bounded to a 1200-byte datagram.</summary>
public static class UdpSnapshotCodec
{
    public const int MaxEntitiesPerDatagram =
        (ProtocolConstants.MaxUdpDatagramBytes - ProtocolConstants.UdpSnapshotHeaderSize) / EntitySnapshot.WireSize;

    public static bool TryEncode(SnapshotMetadata metadata, ReadOnlySpan<EntitySnapshot> entities, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        var required = ProtocolConstants.UdpSnapshotHeaderSize + (entities.Length * EntitySnapshot.WireSize);
        if (entities.Length > MaxEntitiesPerDatagram || required > ProtocolConstants.MaxUdpDatagramBytes || destination.Length < required)
        {
            return false;
        }

        var header = destination[..ProtocolConstants.UdpSnapshotHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ProtocolConstants.SnapshotMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], ProtocolConstants.CurrentVersion);
        header[6] = (byte)metadata.Flags;
        header[7] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], metadata.DatagramToken);
        BinaryPrimitives.WriteUInt16LittleEndian(header[16..], metadata.Sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(header[18..], metadata.AcknowledgedSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], metadata.AcknowledgementBits);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], metadata.ServerTick);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], metadata.BaselineTick);
        BinaryPrimitives.WriteUInt16LittleEndian(header[40..], (ushort)entities.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[42..], (ushort)(entities.Length * EntitySnapshot.WireSize));

        var offset = ProtocolConstants.UdpSnapshotHeaderSize;
        foreach (var entity in entities)
        {
            if (!IsValid(entity))
            {
                return false;
            }

            WriteEntity(destination.Slice(offset, EntitySnapshot.WireSize), entity);
            offset += EntitySnapshot.WireSize;
        }

        bytesWritten = required;
        return true;
    }

    public static byte[] Encode(SnapshotMetadata metadata, ReadOnlySpan<EntitySnapshot> entities)
    {
        var bytes = new byte[ProtocolConstants.UdpSnapshotHeaderSize + (entities.Length * EntitySnapshot.WireSize)];
        if (!TryEncode(metadata, entities, bytes, out var written) || written != bytes.Length)
        {
            throw new ProtocolException($"UDP snapshot must contain at most {MaxEntitiesPerDatagram} valid entities.");
        }

        return bytes;
    }

    /// <summary>Returns false rather than throwing for malformed, incompatible, or truncated UDP input.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> datagram, Span<EntitySnapshot> entityDestination, out SnapshotMetadata metadata, out int entityCount)
    {
        metadata = default;
        entityCount = 0;
        if (datagram.Length is < ProtocolConstants.UdpSnapshotHeaderSize or > ProtocolConstants.MaxUdpDatagramBytes ||
            BinaryPrimitives.ReadUInt32LittleEndian(datagram) != ProtocolConstants.SnapshotMagic ||
            BinaryPrimitives.ReadUInt16LittleEndian(datagram[4..]) != ProtocolConstants.CurrentVersion || datagram[7] != 0)
        {
            return false;
        }

        const byte knownFlags = (byte)(SnapshotFlags.Keyframe | SnapshotFlags.Delta);
        if ((datagram[6] & ~knownFlags) != 0)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(datagram[40..]);
        var payloadBytes = BinaryPrimitives.ReadUInt16LittleEndian(datagram[42..]);
        if (count > MaxEntitiesPerDatagram || entityDestination.Length < count ||
            payloadBytes != count * EntitySnapshot.WireSize || datagram.Length != ProtocolConstants.UdpSnapshotHeaderSize + payloadBytes)
        {
            return false;
        }

        metadata = new SnapshotMetadata(
            BinaryPrimitives.ReadUInt64LittleEndian(datagram[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(datagram[16..]),
            BinaryPrimitives.ReadUInt16LittleEndian(datagram[18..]),
            BinaryPrimitives.ReadUInt32LittleEndian(datagram[20..]),
            BinaryPrimitives.ReadUInt64LittleEndian(datagram[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(datagram[32..]),
            (SnapshotFlags)datagram[6]);

        var offset = ProtocolConstants.UdpSnapshotHeaderSize;
        for (var index = 0; index < count; index++)
        {
            var entity = ReadEntity(datagram.Slice(offset, EntitySnapshot.WireSize));
            if (!IsValid(entity))
            {
                metadata = default;
                return false;
            }

            entityDestination[index] = entity;
            offset += EntitySnapshot.WireSize;
        }

        entityCount = count;
        return true;
    }

    private static void WriteEntity(Span<byte> value, EntitySnapshot entity)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(value, entity.EntityId);
        value[8] = (byte)entity.EntityKind;
        value[9] = entity.AnimationState;
        BinaryPrimitives.WriteInt16LittleEndian(value[10..], entity.WorldLevel);
        BinaryPrimitives.WriteUInt32LittleEndian(value[12..], BitConverter.SingleToUInt32Bits(entity.X));
        BinaryPrimitives.WriteUInt32LittleEndian(value[16..], BitConverter.SingleToUInt32Bits(entity.Y));
        BinaryPrimitives.WriteUInt32LittleEndian(value[20..], BitConverter.SingleToUInt32Bits(entity.VelocityX));
        BinaryPrimitives.WriteUInt32LittleEndian(value[24..], BitConverter.SingleToUInt32Bits(entity.VelocityY));
        BinaryPrimitives.WriteUInt32LittleEndian(value[28..], (uint)entity.State);
        BinaryPrimitives.WriteUInt32LittleEndian(value[32..], entity.Revision);
    }

    private static EntitySnapshot ReadEntity(ReadOnlySpan<byte> value) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(value), (NetworkEntityKind)value[8], value[9],
        BinaryPrimitives.ReadInt16LittleEndian(value[10..]),
        BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(value[12..])),
        BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(value[16..])),
        BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(value[20..])),
        BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(value[24..])),
        (NetworkEntityState)BinaryPrimitives.ReadUInt32LittleEndian(value[28..]),
        BinaryPrimitives.ReadUInt32LittleEndian(value[32..]));

    private static bool IsValid(EntitySnapshot entity)
    {
        const NetworkEntityState knownState = NetworkEntityState.Moving | NetworkEntityState.Dead |
            NetworkEntityState.InCombat | NetworkEntityState.Interacting | NetworkEntityState.Hidden;
        return float.IsFinite(entity.X) && float.IsFinite(entity.Y) &&
            float.IsFinite(entity.VelocityX) && float.IsFinite(entity.VelocityY) &&
            Enum.IsDefined(entity.EntityKind) && (entity.State & ~knownState) == 0;
    }
}
