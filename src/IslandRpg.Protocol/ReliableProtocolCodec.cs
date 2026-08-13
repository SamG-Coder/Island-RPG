using System.Buffers.Binary;

namespace IslandRpg.Protocol;

public readonly record struct ReliableFrameHeader(
    ushort ProtocolVersion,
    ProtocolMessageKind Kind,
    int PayloadLength,
    ulong Sequence,
    ulong Tick);

/// <summary>Encodes reliable protocol messages without JSON or reflection.</summary>
public static class ReliableProtocolCodec
{
    public static byte[] Encode(IProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = new WireWriter();
        EncodePayload(message, payload);
        var frameLength = checked(ProtocolConstants.ReliableHeaderSize + payload.Length);
        if (frameLength > ProtocolConstants.MaxReliableFrameBytes)
        {
            throw new ProtocolException($"Reliable frame exceeds {ProtocolConstants.MaxReliableFrameBytes} bytes.");
        }

        var frame = new byte[frameLength];
        var header = frame.AsSpan(0, ProtocolConstants.ReliableHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header, ProtocolConstants.ReliableMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], ProtocolConstants.CurrentVersion);
        header[6] = (byte)message.Kind;
        header[7] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header[12..], message.Sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(header[20..], message.Tick);
        payload.CopyTo(frame.AsSpan(ProtocolConstants.ReliableHeaderSize));
        return frame;
    }

    public static IProtocolMessage Decode(ReadOnlySpan<byte> frame)
    {
        var header = ReadHeader(frame);
        if (frame.Length != ProtocolConstants.ReliableHeaderSize + header.PayloadLength)
        {
            throw new ProtocolException("Reliable frame length does not match its header.");
        }

        if (header.ProtocolVersion != ProtocolConstants.CurrentVersion &&
            header.Kind is not ProtocolMessageKind.HandshakeRequest and not ProtocolMessageKind.HandshakeRejected)
        {
            throw new ProtocolException($"Unsupported protocol version {header.ProtocolVersion}.");
        }

        var reader = new WireReader(frame[ProtocolConstants.ReliableHeaderSize..]);
        var message = DecodePayload(header, ref reader);
        reader.EnsureConsumed();
        return message;
    }

    /// <summary>Reads metadata without accepting or decoding the payload.</summary>
    public static ReliableFrameHeader ReadHeader(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < ProtocolConstants.ReliableHeaderSize)
        {
            throw new ProtocolException("Reliable frame is shorter than its header.");
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(frame);
        if (magic != ProtocolConstants.ReliableMagic)
        {
            throw new ProtocolException("Reliable frame has an invalid magic value.");
        }

        if (frame[7] != 0)
        {
            throw new ProtocolException("Reliable frame uses unsupported header flags.");
        }

        var kindValue = frame[6];
        if (!Enum.IsDefined((ProtocolMessageKind)kindValue))
        {
            throw new ProtocolException($"Unknown reliable message kind {kindValue}.");
        }

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(frame[8..]);
        if (payloadLength > ProtocolConstants.MaxReliableFrameBytes - ProtocolConstants.ReliableHeaderSize)
        {
            throw new ProtocolException("Declared reliable payload exceeds the frame limit.");
        }

        return new ReliableFrameHeader(
            BinaryPrimitives.ReadUInt16LittleEndian(frame[4..]),
            (ProtocolMessageKind)kindValue,
            (int)payloadLength,
            BinaryPrimitives.ReadUInt64LittleEndian(frame[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(frame[20..]));
    }

    private static void EncodePayload(IProtocolMessage message, WireWriter writer)
    {
        switch (message)
        {
            case HandshakeRequestMessage value:
                writer.WriteUInt16(value.ProtocolVersion);
                writer.WriteString(value.BuildVersion, ProtocolLimits.BuildVersionBytes, nameof(value.BuildVersion));
                writer.WriteString(value.ContentVersion, ProtocolLimits.ContentVersionBytes, nameof(value.ContentVersion));
                writer.WriteGuid(value.ClientId);
                writer.WriteGuid(value.RequestedWorldId);
                writer.WriteString(value.PlayerName, ProtocolLimits.PlayerNameBytes, nameof(value.PlayerName));
                writer.WriteUInt64(value.ClientNonce);
                writer.WriteUInt16(value.ClientSnapshotPort);
                writer.WriteUInt32((uint)value.Capabilities);
                writer.WriteGuid(value.ReconnectPlayerId);
                writer.WriteString(value.ReconnectToken, ProtocolLimits.ReconnectTokenBytes, nameof(value.ReconnectToken));
                break;
            case HandshakeAcceptedMessage value:
                writer.WriteUInt16(value.ProtocolVersion);
                writer.WriteString(value.BuildVersion, ProtocolLimits.BuildVersionBytes, nameof(value.BuildVersion));
                writer.WriteString(value.ContentVersion, ProtocolLimits.ContentVersionBytes, nameof(value.ContentVersion));
                writer.WriteGuid(value.SessionId);
                writer.WriteGuid(value.PlayerId);
                writer.WriteUInt64(value.PlayerEntityId);
                writer.WriteGuid(value.WorldId);
                writer.WriteUInt64(unchecked((ulong)value.WorldSeed));
                EnsureFinite(value.SpawnX, nameof(value.SpawnX));
                EnsureFinite(value.SpawnY, nameof(value.SpawnY));
                writer.WriteSingle(value.SpawnX);
                writer.WriteSingle(value.SpawnY);
                writer.WriteInt32(value.SpawnWorldLevel);
                writer.WriteUInt64(value.DatagramToken);
                writer.WriteUInt64(value.EchoClientNonce);
                writer.WriteUInt64(value.NextCommandSequence);
                writer.WriteString(value.ReconnectToken, ProtocolLimits.ReconnectTokenBytes, nameof(value.ReconnectToken));
                writer.WriteUInt16(value.ServerSnapshotPort);
                writer.WriteUInt16(value.ServerTickRate);
                writer.WriteUInt32((uint)value.Capabilities);
                break;
            case HandshakeRejectedMessage value:
                writer.WriteUInt16(value.ProtocolVersion);
                writer.WriteString(value.BuildVersion, ProtocolLimits.BuildVersionBytes, nameof(value.BuildVersion));
                writer.WriteString(value.ContentVersion, ProtocolLimits.ContentVersionBytes, nameof(value.ContentVersion));
                writer.WriteByte((byte)value.Code);
                writer.WriteString(value.Detail, ProtocolLimits.DetailBytes, nameof(value.Detail));
                break;
            case PlayerJoinedMessage value:
                writer.WriteGuid(value.PlayerId);
                writer.WriteString(value.PlayerName, ProtocolLimits.PlayerNameBytes, nameof(value.PlayerName));
                break;
            case PlayerLeftMessage value:
                writer.WriteGuid(value.PlayerId);
                writer.WriteByte((byte)value.Reason);
                writer.WriteString(value.Detail, ProtocolLimits.LeaveReasonBytes, nameof(value.Detail));
                break;
            case WalkCommandMessage value:
                EnsureFinite(value.DestinationX, nameof(value.DestinationX));
                EnsureFinite(value.DestinationY, nameof(value.DestinationY));
                writer.WriteSingle(value.DestinationX);
                writer.WriteSingle(value.DestinationY);
                writer.WriteInt32(value.WorldLevel);
                break;
            case StopCommandMessage:
                break;
            case ChatCommandMessage value:
                writer.WriteByte((byte)value.Channel);
                writer.WriteGuid(value.TargetPlayerId);
                writer.WriteString(value.Text, ProtocolLimits.ChatTextBytes, nameof(value.Text));
                break;
            case ChatBroadcastMessage value:
                writer.WriteGuid(value.SenderPlayerId);
                writer.WriteString(value.SenderPlayerName, ProtocolLimits.PlayerNameBytes, nameof(value.SenderPlayerName));
                writer.WriteByte((byte)value.Channel);
                writer.WriteGuid(value.TargetPlayerId);
                writer.WriteString(value.Text, ProtocolLimits.ChatTextBytes, nameof(value.Text));
                break;
            case CommandResultMessage value:
                writer.WriteUInt64(value.CommandSequence);
                writer.WriteBoolean(value.Accepted);
                writer.WriteByte((byte)value.RejectionCode);
                writer.WriteString(value.Detail, ProtocolLimits.DetailBytes, nameof(value.Detail));
                break;
            case EntitySnapshotMessage value:
                WriteSnapshotMetadata(writer, value.Metadata);
                WriteEntities(writer, value.Entities, ProtocolLimits.MaxSnapshotEntities);
                break;
            case ActionCommandMessage value:
                WriteActionCommand(writer, value);
                break;
            case ActionResultMessage value:
                WriteActionResult(writer, value);
                break;
            case PlayerStateMessage value:
                WritePlayerState(writer, value);
                break;
            case WorldObjectStateMessage value:
                WriteWorldObjectState(writer, value.Object);
                break;
            case WorldObjectDeltaBatchMessage value:
                WriteWorldObjectDeltaBatch(writer, value);
                break;
            case ContainerStateMessage value:
                WriteContainerState(writer, value);
                break;
            default:
                throw new ProtocolException($"Unsupported message type {message.GetType().FullName}.");
        }
    }

    private static IProtocolMessage DecodePayload(ReliableFrameHeader header, ref WireReader reader)
    {
        var sequence = header.Sequence;
        var tick = header.Tick;
        return header.Kind switch
        {
            ProtocolMessageKind.HandshakeRequest => new HandshakeRequestMessage(
                sequence, tick, reader.ReadUInt16(),
                reader.ReadString(ProtocolLimits.BuildVersionBytes, "BuildVersion"),
                reader.ReadString(ProtocolLimits.ContentVersionBytes, "ContentVersion"),
                reader.ReadGuid(),
                reader.ReadGuid(),
                reader.ReadString(ProtocolLimits.PlayerNameBytes, "PlayerName"),
                reader.ReadUInt64(), reader.ReadUInt16(),
                ReadEnum<ClientCapabilities>(reader.ReadUInt32(), "ClientCapabilities"),
                reader.ReadGuid(), reader.ReadString(ProtocolLimits.ReconnectTokenBytes, "ReconnectToken")),
            ProtocolMessageKind.HandshakeAccepted => new HandshakeAcceptedMessage(
                sequence, tick, reader.ReadUInt16(),
                reader.ReadString(ProtocolLimits.BuildVersionBytes, "BuildVersion"),
                reader.ReadString(ProtocolLimits.ContentVersionBytes, "ContentVersion"),
                reader.ReadGuid(), reader.ReadGuid(), reader.ReadUInt64(), reader.ReadGuid(), unchecked((long)reader.ReadUInt64()),
                ReadFinite(ref reader, "SpawnX"), ReadFinite(ref reader, "SpawnY"), reader.ReadInt32(),
                reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64(),
                reader.ReadString(ProtocolLimits.ReconnectTokenBytes, "ReconnectToken"),
                reader.ReadUInt16(), reader.ReadUInt16(),
                ReadEnum<ServerCapabilities>(reader.ReadUInt32(), "ServerCapabilities")),
            ProtocolMessageKind.HandshakeRejected => new HandshakeRejectedMessage(
                sequence, tick, reader.ReadUInt16(),
                reader.ReadString(ProtocolLimits.BuildVersionBytes, "BuildVersion"),
                reader.ReadString(ProtocolLimits.ContentVersionBytes, "ContentVersion"),
                ReadEnum<HandshakeRejectionCode>(reader.ReadByte(), "HandshakeRejectionCode"),
                reader.ReadString(ProtocolLimits.DetailBytes, "Detail")),
            ProtocolMessageKind.PlayerJoined => new PlayerJoinedMessage(
                sequence, tick, reader.ReadGuid(),
                reader.ReadString(ProtocolLimits.PlayerNameBytes, "PlayerName")),
            ProtocolMessageKind.PlayerLeft => new PlayerLeftMessage(
                sequence, tick, reader.ReadGuid(),
                ReadEnum<PlayerLeaveReason>(reader.ReadByte(), "PlayerLeaveReason"),
                reader.ReadString(ProtocolLimits.LeaveReasonBytes, "Detail")),
            ProtocolMessageKind.WalkCommand => ReadWalk(sequence, tick, ref reader),
            ProtocolMessageKind.StopCommand => new StopCommandMessage(sequence, tick),
            ProtocolMessageKind.ChatCommand => new ChatCommandMessage(
                sequence, tick, ReadEnum<ChatChannel>(reader.ReadByte(), "ChatChannel"),
                reader.ReadGuid(), reader.ReadString(ProtocolLimits.ChatTextBytes, "Text")),
            ProtocolMessageKind.ChatBroadcast => new ChatBroadcastMessage(
                sequence, tick, reader.ReadGuid(),
                reader.ReadString(ProtocolLimits.PlayerNameBytes, "SenderPlayerName"),
                ReadEnum<ChatChannel>(reader.ReadByte(), "ChatChannel"),
                reader.ReadGuid(), reader.ReadString(ProtocolLimits.ChatTextBytes, "Text")),
            ProtocolMessageKind.CommandResult => new CommandResultMessage(
                sequence, tick, reader.ReadUInt64(), reader.ReadBoolean(),
                ReadEnum<CommandRejectionCode>(reader.ReadByte(), "CommandRejectionCode"),
                reader.ReadString(ProtocolLimits.DetailBytes, "Detail")),
            ProtocolMessageKind.EntitySnapshot => new EntitySnapshotMessage(
                sequence, tick, ReadSnapshotMetadata(ref reader), ReadEntities(ref reader, ProtocolLimits.MaxSnapshotEntities)),
            ProtocolMessageKind.ActionCommand => ReadActionCommand(sequence, tick, ref reader),
            ProtocolMessageKind.ActionResult => ReadActionResult(sequence, tick, ref reader),
            ProtocolMessageKind.PlayerState => ReadPlayerState(sequence, tick, ref reader),
            ProtocolMessageKind.WorldObjectState => new WorldObjectStateMessage(
                sequence, tick, ReadWorldObjectState(ref reader)),
            ProtocolMessageKind.WorldObjectDeltaBatch =>
                ReadWorldObjectDeltaBatch(sequence, tick, ref reader),
            ProtocolMessageKind.ContainerState =>
                ReadContainerState(sequence, tick, ref reader),
            _ => throw new ProtocolException($"Unsupported reliable message kind {header.Kind}."),
        };
    }

    private static void WriteActionCommand(WireWriter writer, ActionCommandMessage value)
    {
        EnsureCommandId(value.CommandId);
        if (value.Payload is null)
        {
            throw new ProtocolException("Action payload cannot be null.");
        }

        writer.WriteGuid(value.CommandId);
        writer.WriteUInt32(value.ActorRevision);
        writer.WriteUInt32(value.InventoryRevision);
        switch (value.Payload)
        {
            case InventorySwapAction action:
                writer.WriteByte((byte)ActionCommandKind.InventorySwap);
                WriteInventorySlot(writer, action.SourceSlot, nameof(action.SourceSlot));
                WriteInventorySlot(writer, action.TargetSlot, nameof(action.TargetSlot));
                break;
            case CombineItemsAction action:
                writer.WriteByte((byte)ActionCommandKind.CombineItems);
                WriteInventorySlot(writer, action.SourceSlot, nameof(action.SourceSlot));
                WriteInventorySlot(writer, action.TargetSlot, nameof(action.TargetSlot));
                break;
            case CraftRecipeAction action:
                EnsureIdentifier(action.RecipeId, nameof(action.RecipeId));
                writer.WriteByte((byte)ActionCommandKind.CraftRecipe);
                writer.WriteString(action.RecipeId, ProtocolLimits.RecipeIdBytes, nameof(action.RecipeId));
                break;
            case ConsumeItemAction action:
                writer.WriteByte((byte)ActionCommandKind.ConsumeItem);
                WriteInventorySlot(writer, action.Slot, nameof(action.Slot));
                break;
            case PickUpWorldObjectAction action:
                writer.WriteByte((byte)ActionCommandKind.PickUpWorldObject);
                WriteWorldObjectReference(writer, action.Object);
                break;
            case DropInventoryItemAction action:
                writer.WriteByte((byte)ActionCommandKind.DropInventoryItem);
                WriteInventorySlot(
                    writer, action.InventorySlot, nameof(action.InventorySlot));
                WriteQuantity(writer, action.Quantity, nameof(action.Quantity));
                EnsureFinite(action.X, nameof(action.X));
                EnsureFinite(action.Y, nameof(action.Y));
                writer.WriteSingle(action.X);
                writer.WriteSingle(action.Y);
                writer.WriteInt16(action.WorldLevel);
                writer.WriteUInt32(action.ExpectedChunkRevision);
                break;
            case OpenContainerAction action:
                writer.WriteByte((byte)ActionCommandKind.OpenContainer);
                WriteWorldObjectReference(writer, action.Object);
                break;
            case ContainerTransferAction action:
                writer.WriteByte((byte)ActionCommandKind.ContainerTransfer);
                WriteWorldObjectReference(writer, action.Container);
                writer.WriteUInt32(action.ExpectedContainerRevision);
                EnsureDefined(action.Direction, nameof(action.Direction));
                writer.WriteByte((byte)action.Direction);
                WriteInventorySlot(
                    writer, action.InventorySlot, nameof(action.InventorySlot));
                WriteContainerSlot(
                    writer, action.ContainerSlot, nameof(action.ContainerSlot));
                WriteQuantity(writer, action.Quantity, nameof(action.Quantity));
                break;
            case AddCampfireFuelAction action:
                writer.WriteByte((byte)ActionCommandKind.AddCampfireFuel);
                WriteWorldObjectReference(writer, action.Campfire);
                WriteInventorySlot(
                    writer, action.InventorySlot, nameof(action.InventorySlot));
                break;
            case TakeCampfireFuelAction action:
                writer.WriteByte((byte)ActionCommandKind.TakeCampfireFuel);
                WriteWorldObjectReference(writer, action.Campfire);
                break;
            case LightCampfireAction action:
                writer.WriteByte((byte)ActionCommandKind.LightCampfire);
                WriteWorldObjectReference(writer, action.Campfire);
                break;
            case PlaceConstructionAction action:
                EnsureIdentifier(action.DefinitionId, nameof(action.DefinitionId));
                EnsureConstructionRotation(action.Rotation);
                EnsureFinite(action.X, nameof(action.X));
                EnsureFinite(action.Y, nameof(action.Y));
                writer.WriteByte((byte)ActionCommandKind.PlaceConstruction);
                writer.WriteString(
                    action.DefinitionId,
                    ProtocolLimits.DefinitionIdBytes,
                    nameof(action.DefinitionId));
                WriteInventorySlot(
                    writer, action.InventorySlot, nameof(action.InventorySlot));
                writer.WriteSingle(action.X);
                writer.WriteSingle(action.Y);
                writer.WriteInt16(action.WorldLevel);
                writer.WriteByte((byte)action.Rotation);
                writer.WriteUInt32(action.ExpectedChunkRevision);
                break;
            case BuildConstructionAction action:
                writer.WriteByte((byte)ActionCommandKind.BuildConstruction);
                WriteWorldObjectReference(writer, action.Construction);
                break;
            case DemolishWorldObjectAction action:
                writer.WriteByte((byte)ActionCommandKind.DemolishWorldObject);
                WriteWorldObjectReference(writer, action.Object);
                break;
            default:
                throw new ProtocolException(
                    $"Unsupported action payload type {value.Payload.GetType().FullName}.");
        }
    }

    private static ActionCommandMessage ReadActionCommand(
        ulong sequence,
        ulong tick,
        ref WireReader reader)
    {
        var commandId = reader.ReadGuid();
        EnsureCommandId(commandId);
        var actorRevision = reader.ReadUInt32();
        var inventoryRevision = reader.ReadUInt32();
        var kind = ReadEnum<ActionCommandKind>(reader.ReadByte(), "ActionCommandKind");
        IActionCommandPayload payload = kind switch
        {
            ActionCommandKind.InventorySwap => new InventorySwapAction(
                ReadInventorySlot(ref reader, "SourceSlot"),
                ReadInventorySlot(ref reader, "TargetSlot")),
            ActionCommandKind.CombineItems => new CombineItemsAction(
                ReadInventorySlot(ref reader, "SourceSlot"),
                ReadInventorySlot(ref reader, "TargetSlot")),
            ActionCommandKind.CraftRecipe => new CraftRecipeAction(
                ReadIdentifier(ref reader, ProtocolLimits.RecipeIdBytes, "RecipeId")),
            ActionCommandKind.ConsumeItem => new ConsumeItemAction(
                ReadInventorySlot(ref reader, "Slot")),
            ActionCommandKind.PickUpWorldObject => new PickUpWorldObjectAction(
                ReadWorldObjectReference(ref reader)),
            ActionCommandKind.DropInventoryItem => new DropInventoryItemAction(
                ReadInventorySlot(ref reader, "InventorySlot"),
                ReadQuantity(ref reader, "Quantity"),
                ReadFinite(ref reader, "X"),
                ReadFinite(ref reader, "Y"),
                reader.ReadInt16(),
                reader.ReadUInt32()),
            ActionCommandKind.OpenContainer => new OpenContainerAction(
                ReadWorldObjectReference(ref reader)),
            ActionCommandKind.ContainerTransfer => new ContainerTransferAction(
                ReadWorldObjectReference(ref reader),
                reader.ReadUInt32(),
                ReadEnum<ContainerTransferDirection>(
                    reader.ReadByte(), "ContainerTransferDirection"),
                ReadInventorySlot(ref reader, "InventorySlot"),
                ReadContainerSlot(ref reader, "ContainerSlot"),
                ReadQuantity(ref reader, "Quantity")),
            ActionCommandKind.AddCampfireFuel => new AddCampfireFuelAction(
                ReadWorldObjectReference(ref reader),
                ReadInventorySlot(ref reader, "InventorySlot")),
            ActionCommandKind.TakeCampfireFuel => new TakeCampfireFuelAction(
                ReadWorldObjectReference(ref reader)),
            ActionCommandKind.LightCampfire => new LightCampfireAction(
                ReadWorldObjectReference(ref reader)),
            ActionCommandKind.PlaceConstruction => ReadPlaceConstruction(
                ref reader),
            ActionCommandKind.BuildConstruction => new BuildConstructionAction(
                ReadWorldObjectReference(ref reader)),
            ActionCommandKind.DemolishWorldObject =>
                new DemolishWorldObjectAction(
                    ReadWorldObjectReference(ref reader)),
            _ => throw new ProtocolException($"Unsupported action command kind {kind}."),
        };
        return new ActionCommandMessage(
            sequence,
            tick,
            commandId,
            actorRevision,
            inventoryRevision,
            payload);
    }

    private static PlaceConstructionAction ReadPlaceConstruction(
        ref WireReader reader)
    {
        var definitionId = ReadIdentifier(
            ref reader, ProtocolLimits.DefinitionIdBytes, "DefinitionId");
        var inventorySlot = ReadInventorySlot(ref reader, "InventorySlot");
        var x = ReadFinite(ref reader, "X");
        var y = ReadFinite(ref reader, "Y");
        var worldLevel = reader.ReadInt16();
        var rotation = reader.ReadByte();
        EnsureConstructionRotation(rotation);
        return new PlaceConstructionAction(
            definitionId, inventorySlot, x, y, worldLevel, rotation,
            reader.ReadUInt32());
    }

    private static void WriteWorldObjectReference(
        WireWriter writer,
        WorldObjectReference value)
    {
        EnsureWorldObjectId(value.ObjectId);
        writer.WriteGuid(value.ObjectId);
        writer.WriteInt32(value.ChunkX);
        writer.WriteInt32(value.ChunkY);
        writer.WriteInt16(value.WorldLevel);
        writer.WriteUInt32(value.ExpectedObjectRevision);
        writer.WriteUInt32(value.ExpectedChunkRevision);
    }

    private static WorldObjectReference ReadWorldObjectReference(
        ref WireReader reader)
    {
        var result = new WorldObjectReference(
            reader.ReadGuid(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt16(),
            reader.ReadUInt32(),
            reader.ReadUInt32());
        EnsureWorldObjectId(result.ObjectId);
        return result;
    }

    private static void WriteWorldObjectState(
        WireWriter writer,
        WorldObjectState value)
    {
        ValidateWorldObjectState(value);
        writer.WriteGuid(value.ObjectId);
        writer.WriteInt32(value.ChunkX);
        writer.WriteInt32(value.ChunkY);
        writer.WriteInt16(value.WorldLevel);
        writer.WriteUInt32(value.ChunkRevision);
        writer.WriteUInt32(value.ObjectRevision);
        writer.WriteString(
            value.DefinitionId,
            ProtocolLimits.DefinitionIdBytes,
            nameof(value.DefinitionId));
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteByte((byte)value.Rotation);
        writer.WriteInt32(value.Health);
        writer.WriteInt32(value.MaximumHealth);
        writer.WriteBoolean(value.HasContainer);
    }

    private static WorldObjectState ReadWorldObjectState(
        ref WireReader reader)
    {
        var result = new WorldObjectState(
            reader.ReadGuid(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt16(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadString(
                ProtocolLimits.DefinitionIdBytes, "DefinitionId"),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadByte(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadBoolean());
        ValidateWorldObjectState(result);
        return result;
    }

    private static void ValidateWorldObjectState(WorldObjectState value)
    {
        EnsureWorldObjectId(value.ObjectId);
        EnsureIdentifier(value.DefinitionId, nameof(value.DefinitionId));
        EnsureFinite(value.X, nameof(value.X));
        EnsureFinite(value.Y, nameof(value.Y));
        EnsureConstructionRotation(value.Rotation);
        if (value.Health < 0 || value.MaximumHealth < 0 ||
            value.Health > value.MaximumHealth)
        {
            throw new ProtocolException(
                "World-object health must be between zero and its maximum.");
        }
    }

    private static void WriteWorldObjectDeltaBatch(
        WireWriter writer,
        WorldObjectDeltaBatchMessage value)
    {
        if (value.Deltas is null)
        {
            throw new ProtocolException("World-object deltas cannot be null.");
        }

        if (value.Deltas.Count is < 1 or > ProtocolLimits.MaxWorldObjectsPerBatch)
        {
            throw new ProtocolException(
                $"World-object delta count must be between 1 and " +
                $"{ProtocolLimits.MaxWorldObjectsPerBatch}.");
        }

        writer.WriteUInt16((ushort)value.Deltas.Count);
        foreach (var delta in value.Deltas)
        {
            EnsureDefined(delta.Kind, nameof(delta.Kind));
            writer.WriteByte((byte)delta.Kind);
            WriteWorldObjectReference(writer, delta.Reference);
            writer.WriteUInt32(delta.CurrentChunkRevision);
            if (delta.CurrentChunkRevision <= delta.Reference.ExpectedChunkRevision)
            {
                throw new ProtocolException(
                    "A world-object delta must advance its chunk revision.");
            }
            if (delta.Kind == WorldObjectDeltaKind.Upsert)
            {
                if (delta.State is not { } state ||
                    state.ObjectId != delta.Reference.ObjectId ||
                    state.ChunkX != delta.Reference.ChunkX ||
                    state.ChunkY != delta.Reference.ChunkY ||
                    state.WorldLevel != delta.Reference.WorldLevel ||
                    state.ChunkRevision != delta.CurrentChunkRevision ||
                    state.ObjectRevision <=
                    delta.Reference.ExpectedObjectRevision)
                {
                    throw new ProtocolException(
                        "An upsert delta state must match its reference.");
                }

                WriteWorldObjectState(writer, state);
            }
            else if (delta.State is not null)
            {
                throw new ProtocolException(
                    "A removal delta cannot include object state.");
            }
        }
    }

    private static WorldObjectDeltaBatchMessage ReadWorldObjectDeltaBatch(
        ulong sequence,
        ulong tick,
        ref WireReader reader)
    {
        var count = reader.ReadUInt16();
        if (count is < 1 or > ProtocolLimits.MaxWorldObjectsPerBatch)
        {
            throw new ProtocolException(
                $"World-object delta count must be between 1 and " +
                $"{ProtocolLimits.MaxWorldObjectsPerBatch}.");
        }

        var deltas = new WorldObjectDelta[count];
        for (var index = 0; index < count; index++)
        {
            var kind = ReadEnum<WorldObjectDeltaKind>(
                reader.ReadByte(), "WorldObjectDeltaKind");
            var reference = ReadWorldObjectReference(ref reader);
            var currentChunkRevision = reader.ReadUInt32();
            if (currentChunkRevision <= reference.ExpectedChunkRevision)
            {
                throw new ProtocolException(
                    "A world-object delta must advance its chunk revision.");
            }
            var state = kind == WorldObjectDeltaKind.Upsert
                ? ReadWorldObjectState(ref reader)
                : (WorldObjectState?)null;
            var delta = new WorldObjectDelta(
                kind, reference, currentChunkRevision, state);
            if (state is { } upsert &&
                (upsert.ObjectId != reference.ObjectId ||
                 upsert.ChunkX != reference.ChunkX ||
                 upsert.ChunkY != reference.ChunkY ||
                  upsert.WorldLevel != reference.WorldLevel ||
                  upsert.ChunkRevision != currentChunkRevision ||
                  upsert.ObjectRevision <= reference.ExpectedObjectRevision))
            {
                throw new ProtocolException(
                    "An upsert delta state must match its reference.");
            }

            deltas[index] = delta;
        }

        return new WorldObjectDeltaBatchMessage(sequence, tick, deltas);
    }

    private static void WriteContainerState(
        WireWriter writer,
        ContainerStateMessage value)
    {
        ValidateContainerState(value);
        WriteWorldObjectReference(writer, value.Container);
        writer.WriteUInt32(value.BaselineContainerRevision);
        writer.WriteUInt32(value.ContainerRevision);
        writer.WriteString(
            value.DefinitionId,
            ProtocolLimits.DefinitionIdBytes,
            nameof(value.DefinitionId));
        writer.WriteByte((byte)value.Access);
        writer.WriteByte((byte)value.SlotCount);
        writer.WriteBoolean(value.IsBaseline);
        writer.WriteByte((byte)value.Slots.Count);
        foreach (var slot in value.Slots)
        {
            writer.WriteByte((byte)slot.Slot);
            writer.WriteString(
                slot.ItemId, ProtocolLimits.ItemIdBytes, nameof(slot.ItemId));
            writer.WriteUInt16((ushort)slot.Quantity);
        }
    }

    private static ContainerStateMessage ReadContainerState(
        ulong sequence,
        ulong tick,
        ref WireReader reader)
    {
        var container = ReadWorldObjectReference(ref reader);
        var baselineRevision = reader.ReadUInt32();
        var revision = reader.ReadUInt32();
        var definitionId = reader.ReadString(
            ProtocolLimits.DefinitionIdBytes, "DefinitionId");
        var access = ReadEnum<ContainerAccessMode>(
            reader.ReadByte(), "ContainerAccessMode");
        var slotCount = reader.ReadByte();
        var isBaseline = reader.ReadBoolean();
        var changedCount = reader.ReadByte();
        if (changedCount > ProtocolLimits.MaxContainerSlots)
        {
            throw new ProtocolException(
                $"Container slot count exceeds " +
                $"{ProtocolLimits.MaxContainerSlots}.");
        }

        var slots = new ContainerSlotState[changedCount];
        for (var index = 0; index < changedCount; index++)
        {
            slots[index] = new ContainerSlotState(
                reader.ReadByte(),
                reader.ReadString(ProtocolLimits.ItemIdBytes, "ItemId"),
                reader.ReadUInt16());
        }

        var result = new ContainerStateMessage(
            sequence,
            tick,
            container,
            baselineRevision,
            revision,
            definitionId,
            access,
            slotCount,
            isBaseline,
            slots);
        ValidateContainerState(result);
        return result;
    }

    private static void ValidateContainerState(ContainerStateMessage value)
    {
        EnsureWorldObjectId(value.Container.ObjectId);
        EnsureIdentifier(value.DefinitionId, nameof(value.DefinitionId));
        EnsureDefined(value.Access, nameof(value.Access));
        if (value.SlotCount is < 1 or > ProtocolLimits.MaxContainerSlots)
        {
            throw new ProtocolException(
                $"Container SlotCount must be between 1 and " +
                $"{ProtocolLimits.MaxContainerSlots}.");
        }

        if (value.Slots is null)
        {
            throw new ProtocolException("Container Slots cannot be null.");
        }

        if (value.Slots.Count > value.SlotCount)
        {
            throw new ProtocolException(
                "Container changes cannot exceed the declared slot count.");
        }

        if (value.IsBaseline)
        {
            if (value.BaselineContainerRevision != 0 ||
                value.Slots.Count != value.SlotCount)
            {
                throw new ProtocolException(
                    "A container baseline must have no baseline revision and " +
                    "must contain every slot.");
            }
        }
        else
        {
            if (value.Slots.Count == 0 ||
                value.ContainerRevision <= value.BaselineContainerRevision)
            {
                throw new ProtocolException(
                    "A container delta must advance its revision and contain " +
                    "at least one changed slot.");
            }
        }

        if (value.Container.ExpectedObjectRevision != value.ContainerRevision)
        {
            throw new ProtocolException(
                "Container object and container-state revisions must match.");
        }

        Span<bool> seen = stackalloc bool[value.SlotCount];
        foreach (var slot in value.Slots)
        {
            if ((uint)slot.Slot >= value.SlotCount)
            {
                throw new ProtocolException(
                    $"Container slot {slot.Slot} is outside SlotCount.");
            }

            if (seen[slot.Slot])
            {
                throw new ProtocolException(
                    $"Container slot {slot.Slot} appears more than once.");
            }

            seen[slot.Slot] = true;
            if (slot.ItemId is null)
            {
                throw new ProtocolException("Container ItemId cannot be null.");
            }

            if (slot.ItemId.Length == 0)
            {
                if (slot.Quantity != 0)
                {
                    throw new ProtocolException(
                        "An empty container slot must have quantity zero.");
                }
            }
            else
            {
                EnsureIdentifier(slot.ItemId, nameof(slot.ItemId));
                if (slot.Quantity is < 1 or >
                    ProtocolLimits.MaxContainerTransferQuantity)
                {
                    throw new ProtocolException(
                        "Container quantity is outside its wire bounds.");
                }
            }
        }
    }

    private static void WriteActionResult(WireWriter writer, ActionResultMessage value)
    {
        EnsureCommandId(value.CommandId);
        ValidateActionResult(value.Accepted, value.RejectionCode);
        writer.WriteGuid(value.CommandId);
        writer.WriteBoolean(value.Accepted);
        writer.WriteByte((byte)value.RejectionCode);
        writer.WriteString(value.Detail, ProtocolLimits.DetailBytes, nameof(value.Detail));
        writer.WriteUInt32(value.ActorRevision);
        writer.WriteUInt32(value.InventoryRevision);
    }

    private static ActionResultMessage ReadActionResult(
        ulong sequence,
        ulong tick,
        ref WireReader reader)
    {
        var commandId = reader.ReadGuid();
        EnsureCommandId(commandId);
        var accepted = reader.ReadBoolean();
        var rejectionCode = ReadEnum<CommandRejectionCode>(
            reader.ReadByte(), "CommandRejectionCode");
        var detail = reader.ReadString(ProtocolLimits.DetailBytes, "Detail");
        var actorRevision = reader.ReadUInt32();
        var inventoryRevision = reader.ReadUInt32();
        ValidateActionResult(accepted, rejectionCode);
        return new ActionResultMessage(
            sequence,
            tick,
            commandId,
            accepted,
            rejectionCode,
            detail,
            actorRevision,
            inventoryRevision);
    }

    private static void WritePlayerState(WireWriter writer, PlayerStateMessage value)
    {
        ValidatePlayerState(value);
        writer.WriteGuid(value.PlayerId);
        writer.WriteUInt64(value.PlayerEntityId);
        writer.WriteByte((byte)value.Flags);
        writer.WriteUInt32(value.BaselineActorRevision);
        writer.WriteUInt32(value.BaselineInventoryRevision);
        writer.WriteUInt32(value.ActorRevision);
        writer.WriteUInt32(value.InventoryRevision);
        writer.WriteInt32(value.Health);
        writer.WriteSingle(value.Hunger);
        writer.WriteSingle(value.WellFedSeconds);
        writer.WriteInt32(value.CraftingExperience);
        writer.WriteInt32(value.CookingExperience);
        writer.WriteByte((byte)value.InventorySlots.Count);
        foreach (var slot in value.InventorySlots)
        {
            writer.WriteByte((byte)slot.Slot);
            writer.WriteString(slot.ItemId, ProtocolLimits.ItemIdBytes, nameof(slot.ItemId));
            writer.WriteUInt16((ushort)slot.Quantity);
        }
    }

    private static PlayerStateMessage ReadPlayerState(
        ulong sequence,
        ulong tick,
        ref WireReader reader)
    {
        var playerId = reader.ReadGuid();
        var playerEntityId = reader.ReadUInt64();
        var flags = ReadEnum<PlayerStateFlags>(reader.ReadByte(), "PlayerStateFlags");
        var baselineActorRevision = reader.ReadUInt32();
        var baselineInventoryRevision = reader.ReadUInt32();
        var actorRevision = reader.ReadUInt32();
        var inventoryRevision = reader.ReadUInt32();
        var health = reader.ReadInt32();
        var hunger = reader.ReadSingle();
        var wellFedSeconds = reader.ReadSingle();
        var craftingExperience = reader.ReadInt32();
        var cookingExperience = reader.ReadInt32();
        var count = reader.ReadByte();
        if (count > ProtocolLimits.PlayerInventorySlots)
        {
            throw new ProtocolException(
                $"Inventory slot count exceeds {ProtocolLimits.PlayerInventorySlots}.");
        }

        var inventorySlots = new InventorySlotState[count];
        for (var index = 0; index < count; index++)
        {
            inventorySlots[index] = new InventorySlotState(
                reader.ReadByte(),
                reader.ReadString(ProtocolLimits.ItemIdBytes, "ItemId"),
                reader.ReadUInt16());
        }

        var result = new PlayerStateMessage(
            sequence,
            tick,
            playerId,
            playerEntityId,
            flags,
            baselineActorRevision,
            baselineInventoryRevision,
            actorRevision,
            inventoryRevision,
            health,
            hunger,
            wellFedSeconds,
            craftingExperience,
            cookingExperience,
            inventorySlots);
        ValidatePlayerState(result);
        return result;
    }

    private static void ValidatePlayerState(PlayerStateMessage value)
    {
        _ = ReadEnum<PlayerStateFlags>((byte)value.Flags, nameof(value.Flags));
        if (value.Flags == PlayerStateFlags.None)
        {
            throw new ProtocolException("Player state must contain at least one section.");
        }

        var isBaseline = value.Flags.HasFlag(PlayerStateFlags.Baseline);
        var hasActor = value.Flags.HasFlag(PlayerStateFlags.Actor);
        var hasInventory = value.Flags.HasFlag(PlayerStateFlags.Inventory);
        if (isBaseline && (!hasActor || !hasInventory))
        {
            throw new ProtocolException(
                "A player baseline must contain both actor and inventory state.");
        }

        if (isBaseline &&
            (value.BaselineActorRevision != 0 || value.BaselineInventoryRevision != 0))
        {
            throw new ProtocolException("A player baseline cannot depend on earlier revisions.");
        }

        if (!hasActor && value.ActorRevision != value.BaselineActorRevision)
        {
            throw new ProtocolException(
                "Actor revision changed without an actor-state section.");
        }

        if (!hasInventory && value.InventoryRevision != value.BaselineInventoryRevision)
        {
            throw new ProtocolException(
                "Inventory revision changed without an inventory-state section.");
        }

        if (value.Health < 0)
        {
            throw new ProtocolException("Health cannot be negative.");
        }

        EnsureFinite(value.Hunger, nameof(value.Hunger));
        if (value.Hunger < 0 || value.Hunger > ProtocolLimits.MaxPlayerHunger)
        {
            throw new ProtocolException(
                $"Hunger must be between 0 and {ProtocolLimits.MaxPlayerHunger}.");
        }

        EnsureFinite(value.WellFedSeconds, nameof(value.WellFedSeconds));
        if (value.WellFedSeconds < 0)
        {
            throw new ProtocolException("WellFedSeconds cannot be negative.");
        }

        if (value.CraftingExperience < 0 || value.CookingExperience < 0)
        {
            throw new ProtocolException("Skill experience cannot be negative.");
        }

        if (value.InventorySlots is null)
        {
            throw new ProtocolException("InventorySlots cannot be null.");
        }

        if (value.InventorySlots.Count > ProtocolLimits.PlayerInventorySlots)
        {
            throw new ProtocolException(
                $"Inventory slot count exceeds {ProtocolLimits.PlayerInventorySlots}.");
        }

        if (!hasInventory && value.InventorySlots.Count != 0)
        {
            throw new ProtocolException(
                "Inventory changes were supplied without an inventory-state section.");
        }

        if (hasInventory && isBaseline &&
            value.InventorySlots.Count != ProtocolLimits.PlayerInventorySlots)
        {
            throw new ProtocolException(
                $"An inventory baseline must contain exactly {ProtocolLimits.PlayerInventorySlots} slots.");
        }

        if (hasInventory && !isBaseline && value.InventorySlots.Count == 0)
        {
            throw new ProtocolException("An inventory delta must contain at least one changed slot.");
        }

        Span<bool> seenSlots = stackalloc bool[ProtocolLimits.PlayerInventorySlots];
        foreach (var slot in value.InventorySlots)
        {
            EnsureInventorySlot(slot.Slot, nameof(slot.Slot));
            if (seenSlots[slot.Slot])
            {
                throw new ProtocolException($"Inventory slot {slot.Slot} appears more than once.");
            }

            seenSlots[slot.Slot] = true;
            if (slot.ItemId is null)
            {
                throw new ProtocolException("Inventory ItemId cannot be null.");
            }

            if (slot.ItemId.Length == 0)
            {
                if (slot.Quantity != 0)
                {
                    throw new ProtocolException("An empty inventory slot must have quantity zero.");
                }
            }
            else if (slot.Quantity is < 1 or > ProtocolLimits.MaxInventoryQuantity)
            {
                throw new ProtocolException(
                    $"Inventory quantity must be between 1 and {ProtocolLimits.MaxInventoryQuantity}.");
            }
        }
    }

    private static void ValidateActionResult(
        bool accepted,
        CommandRejectionCode rejectionCode)
    {
        _ = ReadEnum<CommandRejectionCode>((byte)rejectionCode, nameof(rejectionCode));
        if (accepted != (rejectionCode == CommandRejectionCode.None))
        {
            throw new ProtocolException(
                "Accepted action results require no rejection code, and rejected results require one.");
        }
    }

    private static void EnsureCommandId(Guid commandId)
    {
        if (commandId == Guid.Empty)
        {
            throw new ProtocolException("CommandId cannot be empty.");
        }
    }

    private static void EnsureWorldObjectId(Guid objectId)
    {
        if (objectId == Guid.Empty)
        {
            throw new ProtocolException("World object ID cannot be empty.");
        }
    }

    private static void EnsureDefined<TEnum>(TEnum value, string fieldName)
        where TEnum : struct, Enum =>
        _ = ReadEnum<TEnum>(Convert.ToUInt32(value), fieldName);

    private static void EnsureConstructionRotation(int rotation)
    {
        if (rotation is < ProtocolLimits.MinConstructionRotation or
            > ProtocolLimits.MaxConstructionRotation)
        {
            throw new ProtocolException(
                $"Rotation must be between " +
                $"{ProtocolLimits.MinConstructionRotation} and " +
                $"{ProtocolLimits.MaxConstructionRotation}.");
        }
    }

    private static void EnsureIdentifier(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ProtocolException($"{fieldName} cannot be empty.");
        }
    }

    private static string ReadIdentifier(
        ref WireReader reader,
        int maxBytes,
        string fieldName)
    {
        var result = reader.ReadString(maxBytes, fieldName);
        EnsureIdentifier(result, fieldName);
        return result;
    }

    private static void WriteInventorySlot(WireWriter writer, int slot, string fieldName)
    {
        EnsureInventorySlot(slot, fieldName);
        writer.WriteByte((byte)slot);
    }

    private static void WriteContainerSlot(
        WireWriter writer,
        int slot,
        string fieldName)
    {
        EnsureContainerSlot(slot, fieldName);
        writer.WriteByte((byte)slot);
    }

    private static int ReadContainerSlot(
        ref WireReader reader,
        string fieldName)
    {
        var slot = reader.ReadByte();
        EnsureContainerSlot(slot, fieldName);
        return slot;
    }

    private static void EnsureContainerSlot(int slot, string fieldName)
    {
        if ((uint)slot >= ProtocolLimits.MaxContainerSlots)
        {
            throw new ProtocolException(
                $"{fieldName} must be between 0 and " +
                $"{ProtocolLimits.MaxContainerSlots - 1}.");
        }
    }

    private static void WriteQuantity(
        WireWriter writer,
        int quantity,
        string fieldName)
    {
        EnsureQuantity(quantity, fieldName);
        writer.WriteUInt16((ushort)quantity);
    }

    private static int ReadQuantity(
        ref WireReader reader,
        string fieldName)
    {
        var quantity = reader.ReadUInt16();
        EnsureQuantity(quantity, fieldName);
        return quantity;
    }

    private static void EnsureQuantity(int quantity, string fieldName)
    {
        if (quantity is < 1 or > ProtocolLimits.MaxContainerTransferQuantity)
        {
            throw new ProtocolException(
                $"{fieldName} must be between 1 and " +
                $"{ProtocolLimits.MaxContainerTransferQuantity}.");
        }
    }

    private static int ReadInventorySlot(ref WireReader reader, string fieldName)
    {
        var slot = reader.ReadByte();
        EnsureInventorySlot(slot, fieldName);
        return slot;
    }

    private static void EnsureInventorySlot(int slot, string fieldName)
    {
        if ((uint)slot >= ProtocolLimits.PlayerInventorySlots)
        {
            throw new ProtocolException(
                $"{fieldName} must be between 0 and {ProtocolLimits.PlayerInventorySlots - 1}.");
        }
    }

    internal static void WriteSnapshotMetadata(WireWriter writer, SnapshotMetadata value)
    {
        writer.WriteUInt64(value.DatagramToken);
        writer.WriteUInt16(value.Sequence);
        writer.WriteUInt16(value.AcknowledgedSequence);
        writer.WriteUInt32(value.AcknowledgementBits);
        writer.WriteUInt64(value.ServerTick);
        writer.WriteUInt64(value.BaselineTick);
        writer.WriteByte((byte)value.Flags);
    }

    internal static SnapshotMetadata ReadSnapshotMetadata(ref WireReader reader) => new(
        reader.ReadUInt64(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt32(),
        reader.ReadUInt64(), reader.ReadUInt64(), ReadEnum<SnapshotFlags>(reader.ReadByte(), "SnapshotFlags"));

    internal static void WriteEntities(WireWriter writer, IReadOnlyList<EntitySnapshot> entities, int limit)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count > limit || entities.Count > ushort.MaxValue)
        {
            throw new ProtocolException($"Snapshot entity count exceeds {limit}.");
        }

        writer.WriteUInt16((ushort)entities.Count);
        foreach (var entity in entities)
        {
            EnsureFinite(entity.X, nameof(entity.X));
            EnsureFinite(entity.Y, nameof(entity.Y));
            EnsureFinite(entity.VelocityX, nameof(entity.VelocityX));
            EnsureFinite(entity.VelocityY, nameof(entity.VelocityY));
            writer.WriteUInt64(entity.EntityId);
            writer.WriteByte((byte)entity.EntityKind);
            writer.WriteByte(entity.AnimationState);
            writer.WriteInt16(entity.WorldLevel);
            writer.WriteSingle(entity.X);
            writer.WriteSingle(entity.Y);
            writer.WriteSingle(entity.VelocityX);
            writer.WriteSingle(entity.VelocityY);
            writer.WriteUInt32((uint)entity.State);
            writer.WriteUInt32(entity.Revision);
        }
    }

    internal static EntitySnapshot[] ReadEntities(ref WireReader reader, int limit)
    {
        var count = reader.ReadUInt16();
        if (count > limit)
        {
            throw new ProtocolException($"Snapshot entity count exceeds {limit}.");
        }

        if (reader.Remaining != count * EntitySnapshot.WireSize)
        {
            throw new ProtocolException("Snapshot entity count does not match its payload length.");
        }

        var entities = new EntitySnapshot[count];
        for (var index = 0; index < count; index++)
        {
            var entity = new EntitySnapshot(
                reader.ReadUInt64(),
                ReadEnum<NetworkEntityKind>(reader.ReadByte(), "NetworkEntityKind"),
                reader.ReadByte(), reader.ReadInt16(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(),
                ReadEnum<NetworkEntityState>(reader.ReadUInt32(), "NetworkEntityState"),
                reader.ReadUInt32());
            EnsureFinite(entity.X, nameof(entity.X));
            EnsureFinite(entity.Y, nameof(entity.Y));
            EnsureFinite(entity.VelocityX, nameof(entity.VelocityX));
            EnsureFinite(entity.VelocityY, nameof(entity.VelocityY));
            entities[index] = entity;
        }

        return entities;
    }

    private static WalkCommandMessage ReadWalk(ulong sequence, ulong tick, ref WireReader reader)
    {
        var x = reader.ReadSingle();
        var y = reader.ReadSingle();
        EnsureFinite(x, "DestinationX");
        EnsureFinite(y, "DestinationY");
        return new WalkCommandMessage(sequence, tick, x, y, reader.ReadInt32());
    }

    private static float ReadFinite(ref WireReader reader, string fieldName)
    {
        var value = reader.ReadSingle();
        EnsureFinite(value, fieldName);
        return value;
    }

    internal static TEnum ReadEnum<TEnum>(uint value, string fieldName)
        where TEnum : struct, Enum
    {
        var converted = (TEnum)Enum.ToObject(typeof(TEnum), value);
        if (!Enum.IsDefined(converted) && !typeof(TEnum).IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            throw new ProtocolException($"{fieldName} has unknown value {value}.");
        }

        if (typeof(TEnum).IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            var knownBits = Enum.GetValues<TEnum>().Aggregate(0UL, (bits, item) => bits | Convert.ToUInt64(item));
            if ((value & ~knownBits) != 0)
            {
                throw new ProtocolException($"{fieldName} contains unknown flags 0x{value & ~knownBits:X}.");
            }
        }

        return converted;
    }

    private static void EnsureFinite(float value, string fieldName)
    {
        if (!float.IsFinite(value))
        {
            throw new ProtocolException($"{fieldName} must be finite.");
        }
    }
}
