using System.Buffers.Binary;
using IslandRpg.Resources;
using IslandRpg.Simulation;

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
                writer.WriteBoolean(value.IslandStart);
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
            case CookingResultMessage value:
                WriteCookingResult(writer, value);
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
            case WorldChunkRevisionBatchMessage value:
                WriteWorldChunkRevisionBatch(writer, value);
                break;
            case ResourceChunkBaselineMessage value:
                WriteResourceChunkBaseline(writer, value);
                break;
            case ResourceNodeDeltaBatchMessage value:
                WriteResourceNodeDeltaBatch(writer, value);
                break;
            case ResourceActionResultMessage value:
                WriteResourceActionResult(writer, value);
                break;
            case CaveActionResultMessage value:
                WriteCaveActionResult(writer, value);
                break;
            case BoatBaselineMessage value:
                WriteBoatBaseline(writer, value);
                break;
            case BoatDeltaBatchMessage value:
                WriteBoatDeltaBatch(writer, value);
                break;
            case BoatActionResultMessage value:
                WriteBoatActionResult(writer, value);
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
                ReadEnum<ServerCapabilities>(reader.ReadUInt32(), "ServerCapabilities"),
                reader.ReadBoolean()),
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
            ProtocolMessageKind.CookingResult =>
                ReadCookingResult(sequence, tick, ref reader),
            ProtocolMessageKind.PlayerState => ReadPlayerState(sequence, tick, ref reader),
            ProtocolMessageKind.WorldObjectState => new WorldObjectStateMessage(
                sequence, tick, ReadWorldObjectState(ref reader)),
            ProtocolMessageKind.WorldObjectDeltaBatch =>
                ReadWorldObjectDeltaBatch(sequence, tick, ref reader),
            ProtocolMessageKind.ContainerState =>
                ReadContainerState(sequence, tick, ref reader),
            ProtocolMessageKind.WorldChunkRevisionBatch =>
                ReadWorldChunkRevisionBatch(sequence, tick, ref reader),
            ProtocolMessageKind.ResourceChunkBaseline =>
                ReadResourceChunkBaseline(sequence, tick, ref reader),
            ProtocolMessageKind.ResourceNodeDeltaBatch =>
                ReadResourceNodeDeltaBatch(sequence, tick, ref reader),
            ProtocolMessageKind.ResourceActionResult =>
                ReadResourceActionResult(sequence, tick, ref reader),
            ProtocolMessageKind.CaveActionResult =>
                ReadCaveActionResult(sequence, tick, ref reader),
            ProtocolMessageKind.BoatBaseline =>
                ReadBoatBaseline(sequence, tick, ref reader),
            ProtocolMessageKind.BoatDeltaBatch =>
                ReadBoatDeltaBatch(sequence, tick, ref reader),
            ProtocolMessageKind.BoatActionResult =>
                ReadBoatActionResult(sequence, tick, ref reader),
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
            case CookOnCampfireAction action:
                writer.WriteByte((byte)ActionCommandKind.CookOnCampfire);
                WriteWorldObjectReference(writer, action.Campfire);
                WriteInventorySlot(
                    writer, action.InventorySlot, nameof(action.InventorySlot));
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
            case ResourceActionPayload action:
                writer.WriteByte((byte)ActionCommandKind.ResourceAction);
                WriteResourceAction(writer, action);
                break;
            case CaveActionPayload action:
                writer.WriteByte((byte)ActionCommandKind.CaveAction);
                WriteCaveAction(writer, action);
                break;
            case BoatActionPayload action:
                writer.WriteByte((byte)ActionCommandKind.BoatAction);
                WriteBoatAction(writer, action);
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
            ActionCommandKind.CookOnCampfire => new CookOnCampfireAction(
                ReadWorldObjectReference(ref reader),
                ReadInventorySlot(ref reader, "InventorySlot")),
            ActionCommandKind.PlaceConstruction => ReadPlaceConstruction(
                ref reader),
            ActionCommandKind.BuildConstruction => new BuildConstructionAction(
                ReadWorldObjectReference(ref reader)),
            ActionCommandKind.DemolishWorldObject =>
                new DemolishWorldObjectAction(
                    ReadWorldObjectReference(ref reader)),
            ActionCommandKind.ResourceAction => ReadResourceAction(ref reader),
            ActionCommandKind.CaveAction => ReadCaveAction(ref reader),
            ActionCommandKind.BoatAction => ReadBoatAction(ref reader),
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

    private static void WriteCaveAction(
        WireWriter writer,
        CaveActionPayload action)
    {
        EnsureDefined(action.Action, nameof(action.Action));
        writer.WriteByte((byte)action.Action);
        switch (action)
        {
            case StartExcavationAction start:
                EnsureFinite(start.X, nameof(start.X));
                EnsureFinite(start.Y, nameof(start.Y));
                writer.WriteSingle(start.X);
                writer.WriteSingle(start.Y);
                writer.WriteInt16(start.WorldLevel);
                WriteInventorySlot(
                    writer, start.ShovelInventorySlot,
                    nameof(start.ShovelInventorySlot));
                writer.WriteUInt32(start.ExpectedChunkRevision);
                break;
            case WorkExcavationAction work:
                WriteWorldObjectReference(writer, work.Excavation);
                WriteInventorySlot(
                    writer, work.ShovelInventorySlot,
                    nameof(work.ShovelInventorySlot));
                break;
            case RestoreExcavationAction restore:
                WriteWorldObjectReference(writer, restore.Excavation);
                break;
            case InstallCaveRopeAction install:
                WriteWorldObjectReference(writer, install.Shaft);
                WriteInventorySlot(
                    writer, install.RopeInventorySlot,
                    nameof(install.RopeInventorySlot));
                break;
            case TakeCaveRopeAction take:
                WriteWorldObjectReference(writer, take.Entrance);
                break;
            case FillExcavationAction fill:
                WriteWorldObjectReference(writer, fill.Excavation);
                WriteInventorySlot(
                    writer, fill.MaterialInventorySlot,
                    nameof(fill.MaterialInventorySlot));
                break;
            case TraverseCaveAction traverse:
                WriteWorldObjectReference(writer, traverse.Entrance);
                break;
            default:
                throw new ProtocolException(
                    $"Unsupported cave action payload {action.GetType().FullName}.");
        }
    }

    private static CaveActionPayload ReadCaveAction(ref WireReader reader)
    {
        var action = ReadEnum<CaveActionKind>(
            reader.ReadByte(), nameof(CaveActionKind));
        return action switch
        {
            CaveActionKind.StartExcavation => new StartExcavationAction(
                ReadFinite(ref reader, "X"),
                ReadFinite(ref reader, "Y"),
                reader.ReadInt16(),
                ReadInventorySlot(ref reader, "ShovelInventorySlot"),
                reader.ReadUInt32()),
            CaveActionKind.WorkExcavation => new WorkExcavationAction(
                ReadWorldObjectReference(ref reader),
                ReadInventorySlot(ref reader, "ShovelInventorySlot")),
            CaveActionKind.RestoreExcavation => new RestoreExcavationAction(
                ReadWorldObjectReference(ref reader)),
            CaveActionKind.InstallRope => new InstallCaveRopeAction(
                ReadWorldObjectReference(ref reader),
                ReadInventorySlot(ref reader, "RopeInventorySlot")),
            CaveActionKind.TakeRope => new TakeCaveRopeAction(
                ReadWorldObjectReference(ref reader)),
            CaveActionKind.FillExcavation => new FillExcavationAction(
                ReadWorldObjectReference(ref reader),
                ReadInventorySlot(ref reader, "MaterialInventorySlot")),
            CaveActionKind.Traverse => new TraverseCaveAction(
                ReadWorldObjectReference(ref reader)),
            _ => throw new ProtocolException(
                $"Unsupported cave action kind {action}.")
        };
    }

    private static void WriteResourceAction(
        WireWriter writer,
        ResourceActionPayload action)
    {
        EnsureDefined(action.Action, nameof(action.Action));
        WriteResourceNodeReference(writer, action.Resource);
        if (action.ToolInventorySlot < -1 ||
            action.ToolInventorySlot >= ProtocolLimits.PlayerInventorySlots)
        {
            throw new ProtocolException(
                "Resource-action tool slots must be -1 or a valid inventory slot.");
        }
        if ((action.Action is ResourceActionKind.GatherTreeStick or
                ResourceActionKind.GatherFibre) &&
            action.ToolInventorySlot != -1)
        {
            throw new ProtocolException(
                "This resource action cannot specify a tool slot.");
        }
        if ((action.Action is ResourceActionKind.CutTree or
                ResourceActionKind.Mine or ResourceActionKind.Fish) &&
            action.ToolInventorySlot < 0)
        {
            throw new ProtocolException(
                "This resource action requires an exact tool slot.");
        }
        writer.WriteByte((byte)action.Action);
        writer.WriteInt16((short)action.ToolInventorySlot);
    }

    private static ResourceActionPayload ReadResourceAction(
        ref WireReader reader)
    {
        var resource = ReadResourceNodeReference(ref reader);
        var action = ReadEnum<ResourceActionKind>(
            reader.ReadByte(), "ResourceActionKind");
        var toolSlot = reader.ReadInt16();
        if (toolSlot < -1 || toolSlot >= ProtocolLimits.PlayerInventorySlots)
        {
            throw new ProtocolException(
                "Resource-action tool slots must be -1 or a valid inventory slot.");
        }
        if ((action is ResourceActionKind.GatherTreeStick or
                ResourceActionKind.GatherFibre) && toolSlot != -1)
        {
            throw new ProtocolException(
                "This resource action cannot specify a tool slot.");
        }
        if ((action is ResourceActionKind.CutTree or
                ResourceActionKind.Mine or ResourceActionKind.Fish) &&
            toolSlot < 0)
        {
            throw new ProtocolException(
                "This resource action requires an exact tool slot.");
        }
        return new ResourceActionPayload(action, resource, toolSlot);
    }

    private static void WriteBoatAction(
        WireWriter writer,
        BoatActionPayload action)
    {
        EnsureDefined(action.Action, nameof(action.Action));
        writer.WriteByte((byte)action.Action);
        WriteBoatReference(writer, action.Boat);
        switch (action)
        {
            case BoardBoatAction:
            case StopBoatAction:
                break;
            case MoveBoatAction move:
                EnsureFinite(move.TargetX, nameof(move.TargetX));
                EnsureFinite(move.TargetY, nameof(move.TargetY));
                writer.WriteSingle(move.TargetX);
                writer.WriteSingle(move.TargetY);
                break;
            case DisembarkBoatAction disembark:
                EnsureFinite(disembark.TargetX, nameof(disembark.TargetX));
                EnsureFinite(disembark.TargetY, nameof(disembark.TargetY));
                writer.WriteSingle(disembark.TargetX);
                writer.WriteSingle(disembark.TargetY);
                break;
            default:
                throw new ProtocolException(
                    $"Unsupported boat action payload {action.GetType().FullName}.");
        }
    }

    private static BoatActionPayload ReadBoatAction(ref WireReader reader)
    {
        var action = ReadEnum<BoatActionKind>(
            reader.ReadByte(), nameof(BoatActionKind));
        var boat = ReadBoatReference(ref reader);
        return action switch
        {
            BoatActionKind.Board => new BoardBoatAction(boat),
            BoatActionKind.Move => new MoveBoatAction(
                boat,
                ReadFinite(ref reader, "TargetX"),
                ReadFinite(ref reader, "TargetY")),
            BoatActionKind.Stop => new StopBoatAction(boat),
            BoatActionKind.Disembark => new DisembarkBoatAction(
                boat,
                ReadFinite(ref reader, "TargetX"),
                ReadFinite(ref reader, "TargetY")),
            _ => throw new ProtocolException(
                $"Unsupported boat action kind {action}.")
        };
    }

    private static void WriteBoatReference(
        WireWriter writer,
        BoatReference value)
    {
        EnsureBoatId(value.BoatId);
        writer.WriteGuid(value.BoatId);
        writer.WriteUInt32(value.ExpectedRevision);
    }

    private static BoatReference ReadBoatReference(ref WireReader reader)
    {
        var result = new BoatReference(reader.ReadGuid(), reader.ReadUInt32());
        EnsureBoatId(result.BoatId);
        return result;
    }

    private static void WriteResourceNodeReference(
        WireWriter writer,
        ResourceNodeReference value)
    {
        EnsureResourceNodeId(value.Id);
        writer.WriteGuid(value.Id.Value);
        WriteResourceChunk(writer, value.Chunk);
        writer.WriteUInt32(value.ExpectedNodeRevision);
        writer.WriteUInt32(value.ExpectedResourceChunkRevision);
    }

    private static ResourceNodeReference ReadResourceNodeReference(
        ref WireReader reader)
    {
        var result = new ResourceNodeReference(
            new ResourceNodeId(reader.ReadGuid()),
            ReadResourceChunk(ref reader),
            reader.ReadUInt32(),
            reader.ReadUInt32());
        EnsureResourceNodeId(result.Id);
        return result;
    }

    private static void WriteResourceChunk(
        WireWriter writer,
        WorldChunkKey value)
    {
        writer.WriteInt32(value.X);
        writer.WriteInt32(value.Y);
        writer.WriteInt32(value.WorldLevel);
    }

    private static WorldChunkKey ReadResourceChunk(ref WireReader reader) =>
        new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());

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
        writer.WriteString(
            value.FuelItemId,
            ProtocolLimits.ItemIdBytes,
            nameof(value.FuelItemId));
        writer.WriteDouble(value.LitUntilGameSeconds);
        writer.WriteByte((byte)value.GateState);
        writer.WriteGuid(value.LinkedObjectId);
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
            reader.ReadBoolean(),
            reader.ReadString(ProtocolLimits.ItemIdBytes, "FuelItemId"),
            reader.ReadDouble(),
            (WorldObjectGateState)reader.ReadByte(),
            reader.ReadGuid());
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
        if (value.FuelItemId.Length > 0)
            EnsureIdentifier(value.FuelItemId, nameof(value.FuelItemId));
        if (!double.IsFinite(value.LitUntilGameSeconds) ||
            value.LitUntilGameSeconds < 0)
            throw new ProtocolException(
                "World-object burn time must be finite and non-negative.");
        if (!Enum.IsDefined(value.GateState))
            throw new ProtocolException("World-object gate state is invalid.");
        if (value.LinkedObjectId == value.ObjectId)
            throw new ProtocolException(
                "A linked world object must use a distinct identity.");
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

    private static void WriteWorldChunkRevisionBatch(
        WireWriter writer,
        WorldChunkRevisionBatchMessage value)
    {
        ValidateWorldChunkRevisionBatch(value.Chunks);
        writer.WriteUInt16((ushort)value.Chunks.Count);
        foreach (var chunk in value.Chunks)
        {
            writer.WriteInt32(chunk.ChunkX);
            writer.WriteInt32(chunk.ChunkY);
            writer.WriteInt16(chunk.WorldLevel);
            writer.WriteUInt32(chunk.Revision);
        }
    }

    private static WorldChunkRevisionBatchMessage ReadWorldChunkRevisionBatch(
        ulong sequence,
        ulong tick,
        ref WireReader reader)
    {
        var count = reader.ReadUInt16();
        if (count is < 1 or > ProtocolLimits.MaxWorldChunkRevisionsPerBatch)
        {
            throw new ProtocolException(
                $"World-chunk revision count must be between 1 and " +
                $"{ProtocolLimits.MaxWorldChunkRevisionsPerBatch}.");
        }

        var chunks = new WorldChunkRevisionState[count];
        for (var index = 0; index < chunks.Length; index++)
        {
            chunks[index] = new WorldChunkRevisionState(
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt16(),
                reader.ReadUInt32());
        }
        ValidateWorldChunkRevisionBatch(chunks);
        return new WorldChunkRevisionBatchMessage(sequence, tick, chunks);
    }

    private static void ValidateWorldChunkRevisionBatch(
        IReadOnlyList<WorldChunkRevisionState>? chunks)
    {
        if (chunks is null)
            throw new ProtocolException(
                "World-chunk revisions cannot be null.");
        if (chunks.Count is < 1 or
            > ProtocolLimits.MaxWorldChunkRevisionsPerBatch)
        {
            throw new ProtocolException(
                $"World-chunk revision count must be between 1 and " +
                $"{ProtocolLimits.MaxWorldChunkRevisionsPerBatch}.");
        }

        var revisions = new Dictionary<(int X, int Y, short Level), uint>(
            chunks.Count);
        foreach (var chunk in chunks)
        {
            if (chunk.Revision == 0)
                throw new ProtocolException(
                    "World-chunk revisions must be positive.");
            var key = (chunk.ChunkX, chunk.ChunkY, chunk.WorldLevel);
            if (revisions.TryGetValue(key, out var revision) &&
                revision != chunk.Revision)
            {
                throw new ProtocolException(
                    "One world-chunk batch contained conflicting duplicate entries.");
            }
            revisions[key] = chunk.Revision;
        }
    }

    private static void WriteResourceChunkBaseline(
        WireWriter writer,
        ResourceChunkBaselineMessage value)
    {
        ValidateResourceBaseline(value);
        WriteResourceChunk(writer, value.Chunk);
        writer.WriteUInt32(value.ResourceChunkRevision);
        writer.WriteUInt16((ushort)value.Nodes.Count);
        foreach (var node in value.Nodes) WriteResourceNodeState(writer, node);
        writer.WriteUInt16((ushort)value.Tombstones.Count);
        foreach (var tombstone in value.Tombstones)
        {
            writer.WriteGuid(tombstone.Id.Value);
            writer.WriteUInt32(tombstone.Revision);
        }
    }

    private static ResourceChunkBaselineMessage ReadResourceChunkBaseline(
        ulong sequence,
        ulong tick,
        ref WireReader reader)
    {
        var chunk = ReadResourceChunk(ref reader);
        var chunkRevision = reader.ReadUInt32();
        var nodeCount = reader.ReadUInt16();
        if (nodeCount > ProtocolLimits.MaxResourceNodesPerBatch)
            throw new ProtocolException("Resource baseline node count exceeds its hard limit.");
        var nodes = new ResourceNodeSparseState[nodeCount];
        for (var index = 0; index < nodes.Length; index++)
            nodes[index] = ReadResourceNodeState(ref reader);
        var tombstoneCount = reader.ReadUInt16();
        if (nodeCount + tombstoneCount > ProtocolLimits.MaxResourceNodesPerBatch)
            throw new ProtocolException("Resource baseline state exceeds its hard limit.");
        var tombstones = new ResourceNodeRevisionState[tombstoneCount];
        for (var index = 0; index < tombstones.Length; index++)
        {
            tombstones[index] = new ResourceNodeRevisionState(
                new ResourceNodeId(reader.ReadGuid()), reader.ReadUInt32());
        }
        var result = new ResourceChunkBaselineMessage(
            sequence, tick, chunk, chunkRevision, nodes, tombstones);
        ValidateResourceBaseline(result);
        return result;
    }

    private static void ValidateResourceBaseline(ResourceChunkBaselineMessage value)
    {
        if (value.Nodes is null || value.Tombstones is null)
            throw new ProtocolException("Resource baseline collections cannot be null.");
        if (value.Nodes.Count + value.Tombstones.Count >
            ProtocolLimits.MaxResourceNodesPerBatch)
            throw new ProtocolException("Resource baseline state exceeds its hard limit.");
        if (value.ResourceChunkRevision == 0 &&
            (value.Nodes.Count != 0 || value.Tombstones.Count != 0))
            throw new ProtocolException("An unmodified resource chunk cannot contain sparse state.");

        var ids = new HashSet<ResourceNodeId>();
        foreach (var node in value.Nodes)
        {
            ValidateResourceNodeState(node);
            if (node.Chunk != value.Chunk)
                throw new ProtocolException("Resource baseline node belongs to a different chunk.");
            if (node.NodeRevision > value.ResourceChunkRevision)
                throw new ProtocolException("A resource node revision cannot exceed its chunk revision.");
            if (!ids.Add(node.Id))
                throw new ProtocolException("Resource baseline contains a duplicate node ID.");
        }
        foreach (var tombstone in value.Tombstones)
        {
            EnsureResourceNodeId(tombstone.Id);
            if (tombstone.Revision == 0)
                throw new ProtocolException("Resource tombstone revisions must be positive.");
            if (tombstone.Revision > value.ResourceChunkRevision)
                throw new ProtocolException("A resource tombstone revision cannot exceed its chunk revision.");
            if (!ids.Add(tombstone.Id))
                throw new ProtocolException("Resource baseline contains a duplicate node ID.");
        }
    }

    private static void WriteResourceNodeDeltaBatch(
        WireWriter writer,
        ResourceNodeDeltaBatchMessage value)
    {
        ValidateResourceDeltas(value.Deltas);
        writer.WriteUInt16((ushort)value.Deltas.Count);
        foreach (var delta in value.Deltas)
        {
            writer.WriteByte((byte)delta.Kind);
            WriteResourceNodeReference(writer, delta.Reference);
            writer.WriteUInt32(delta.CurrentNodeRevision);
            writer.WriteUInt32(delta.CurrentResourceChunkRevision);
            if (delta.Kind == ResourceNodeDeltaKind.Upsert)
                WriteResourceNodeState(writer, delta.State!);
        }
    }

    private static ResourceNodeDeltaBatchMessage ReadResourceNodeDeltaBatch(
        ulong sequence,
        ulong tick,
        ref WireReader reader)
    {
        var count = reader.ReadUInt16();
        if (count is < 1 or > ProtocolLimits.MaxResourceNodesPerBatch)
            throw new ProtocolException("Resource delta count is outside its hard limit.");
        var deltas = new ResourceNodeDelta[count];
        for (var index = 0; index < deltas.Length; index++)
        {
            var kind = ReadEnum<ResourceNodeDeltaKind>(
                reader.ReadByte(), "ResourceNodeDeltaKind");
            var reference = ReadResourceNodeReference(ref reader);
            var nodeRevision = reader.ReadUInt32();
            var chunkRevision = reader.ReadUInt32();
            deltas[index] = new ResourceNodeDelta(
                kind,
                reference,
                nodeRevision,
                chunkRevision,
                kind == ResourceNodeDeltaKind.Upsert
                    ? ReadResourceNodeState(ref reader)
                    : null);
        }
        ValidateResourceDeltas(deltas);
        return new ResourceNodeDeltaBatchMessage(sequence, tick, deltas);
    }

    private static void ValidateResourceDeltas(
        IReadOnlyList<ResourceNodeDelta>? deltas)
    {
        if (deltas is null || deltas.Count is < 1 or
            > ProtocolLimits.MaxResourceNodesPerBatch)
            throw new ProtocolException("Resource delta count is outside its hard limit.");

        var ids = new HashSet<ResourceNodeId>();
        var chunks = new Dictionary<WorldChunkKey, (uint Expected, uint Current)>();
        foreach (var delta in deltas)
        {
            EnsureDefined(delta.Kind, nameof(delta.Kind));
            EnsureResourceNodeId(delta.Reference.Id);
            if (!ids.Add(delta.Reference.Id))
                throw new ProtocolException("One resource batch changed a node more than once.");
            if (delta.CurrentNodeRevision <= delta.Reference.ExpectedNodeRevision)
                throw new ProtocolException("A resource delta must advance its node revision.");
            if (delta.CurrentResourceChunkRevision <=
                delta.Reference.ExpectedResourceChunkRevision)
                throw new ProtocolException("A resource delta must advance its chunk revision.");
            if (delta.CurrentNodeRevision > delta.CurrentResourceChunkRevision)
                throw new ProtocolException("A resource node revision cannot exceed its chunk revision.");
            var transition = (
                delta.Reference.ExpectedResourceChunkRevision,
                delta.CurrentResourceChunkRevision);
            if (chunks.TryGetValue(delta.Reference.Chunk, out var existing) &&
                existing != transition)
                throw new ProtocolException("One resource chunk has conflicting atomic transitions.");
            chunks[delta.Reference.Chunk] = transition;

            if (delta.Kind == ResourceNodeDeltaKind.Upsert)
            {
                if (delta.State is not { } state)
                    throw new ProtocolException("A resource upsert omitted state.");
                ValidateResourceNodeState(state);
                if (state.Id != delta.Reference.Id ||
                    state.Chunk != delta.Reference.Chunk ||
                    state.NodeRevision != delta.CurrentNodeRevision)
                    throw new ProtocolException("A resource upsert does not match its reference.");
            }
            else if (delta.State is not null)
            {
                throw new ProtocolException("A resource removal cannot include state.");
            }
        }
    }

    private static void WriteResourceActionResult(
        WireWriter writer,
        ResourceActionResultMessage value)
    {
        ValidateResourceActionResult(value);
        writer.WriteGuid(value.CommandId);
        writer.WriteBoolean(value.Accepted);
        writer.WriteByte((byte)value.RejectionCode);
        writer.WriteString(value.Detail, ProtocolLimits.DetailBytes,
            nameof(value.Detail));
        writer.WriteUInt32(value.ActorRevision);
        writer.WriteUInt32(value.InventoryRevision);
        writer.WriteByte((byte)value.Action);
        WriteResourceNodeReference(writer, value.Resource);
        writer.WriteByte((byte)value.Rewards.Count);
        foreach (var reward in value.Rewards)
        {
            writer.WriteString(reward.ItemId, ProtocolLimits.ItemIdBytes,
                nameof(reward.ItemId));
            writer.WriteUInt16((ushort)reward.Quantity);
        }
        writer.WriteBoolean(value.Hit);
        writer.WriteInt32(value.Damage);
        writer.WriteBoolean(value.ToolWorn);
        writer.WriteBoolean(value.FishingOutcome is not null);
        if (value.FishingOutcome is { } fishing)
        {
            writer.WriteByte((byte)fishing.Species);
            writer.WriteBoolean(fishing.Caught);
            writer.WriteSingle(fishing.Chance);
        }
    }

    private static ResourceActionResultMessage ReadResourceActionResult(
        ulong sequence,
        ulong tick,
        ref WireReader reader)
    {
        var commandId = reader.ReadGuid();
        EnsureCommandId(commandId);
        var accepted = reader.ReadBoolean();
        var rejection = ReadEnum<CommandRejectionCode>(
            reader.ReadByte(), "CommandRejectionCode");
        var detail = reader.ReadString(ProtocolLimits.DetailBytes, "Detail");
        var actorRevision = reader.ReadUInt32();
        var inventoryRevision = reader.ReadUInt32();
        var action = ReadEnum<ResourceActionKind>(
            reader.ReadByte(), "ResourceActionKind");
        var resource = ReadResourceNodeReference(ref reader);
        var count = reader.ReadByte();
        if (count > ProtocolLimits.MaxResourceRewardsPerAction)
            throw new ProtocolException("Resource reward count exceeds its hard limit.");
        var rewards = new ResourceItemRewardState[count];
        for (var index = 0; index < rewards.Length; index++)
        {
            rewards[index] = new ResourceItemRewardState(
                ReadIdentifier(ref reader, ProtocolLimits.ItemIdBytes,
                    "RewardItemId"),
                reader.ReadUInt16());
        }
        var hit = reader.ReadBoolean();
        var damage = reader.ReadInt32();
        var toolWorn = reader.ReadBoolean();
        FishingOutcomeState? fishingOutcome = null;
        if (reader.ReadBoolean())
        {
            fishingOutcome = new FishingOutcomeState(
                ReadEnum<IslandRpg.Fishing.FishSpecies>(
                    reader.ReadByte(), "FishSpecies"),
                reader.ReadBoolean(),
                ReadFinite(ref reader, "FishingChance"));
        }
        var result = new ResourceActionResultMessage(
            sequence, tick, commandId, accepted, rejection, detail,
            actorRevision, inventoryRevision, action, resource, rewards,
            hit, damage, toolWorn, fishingOutcome);
        ValidateResourceActionResult(result);
        return result;
    }

    private static void ValidateResourceActionResult(
        ResourceActionResultMessage value)
    {
        EnsureCommandId(value.CommandId);
        ValidateActionResult(value.Accepted, value.RejectionCode);
        EnsureDefined(value.Action, nameof(value.Action));
        EnsureResourceNodeId(value.Resource.Id);
        if (value.Rewards is null || value.Rewards.Count >
            ProtocolLimits.MaxResourceRewardsPerAction)
            throw new ProtocolException("Resource reward count exceeds its hard limit.");
        if (value.Damage < 0 || !value.Hit && value.Damage != 0)
            throw new ProtocolException("Resource damage is inconsistent with its hit flag.");
        if (value.FishingOutcome is { } fishing)
        {
            EnsureDefined(fishing.Species, nameof(fishing.Species));
            EnsureFinite(fishing.Chance, nameof(fishing.Chance));
            if (value.Action != ResourceActionKind.Fish || !value.Accepted ||
                fishing.Chance is < 0 or > 1 || fishing.Caught != value.Hit ||
                fishing.Caught && value.Rewards.Count == 0 ||
                !fishing.Caught && value.Rewards.Count != 0)
            {
                throw new ProtocolException(
                    "Fishing outcome is inconsistent with the resource result.");
            }
        }
        else if (value.Action == ResourceActionKind.Fish && value.Accepted)
        {
            throw new ProtocolException(
                "An accepted fishing result must include its typed outcome.");
        }
        foreach (var reward in value.Rewards)
        {
            EnsureIdentifier(reward.ItemId, nameof(reward.ItemId));
            if (reward.Quantity is < 1 or > ProtocolLimits.MaxInventoryQuantity)
                throw new ProtocolException("Resource reward quantity is outside its wire bounds.");
        }
    }

    private static void WriteResourceNodeState(
        WireWriter writer,
        ResourceNodeSparseState value)
    {
        ValidateResourceNodeState(value);
        writer.WriteGuid(value.Id.Value);
        writer.WriteByte((byte)value.Kind);
        WriteResourceChunk(writer, value.Chunk);
        writer.WriteUInt32(value.NodeRevision);
        writer.WriteInt32(value.Health);
        writer.WriteInt32(value.Remaining);
        writer.WriteDouble(value.ReadyAtGameSeconds);
        writer.WriteBoolean(value.Depleted);
    }

    private static void WriteBoatBaseline(
        WireWriter writer,
        BoatBaselineMessage value)
    {
        ValidateBoatStates(value.Boats);
        writer.WriteUInt16((ushort)value.Boats.Count);
        foreach (var boat in value.Boats)
            WriteBoatState(writer, boat);
    }

    private static BoatBaselineMessage ReadBoatBaseline(
        ulong sequence,
        ulong tick,
        ref WireReader reader)
    {
        var count = reader.ReadUInt16();
        if (count > ProtocolLimits.MaxBoatsPerBatch)
            throw new ProtocolException("Boat baseline exceeds its hard limit.");
        var boats = new BoatState[count];
        for (var index = 0; index < boats.Length; index++)
            boats[index] = ReadBoatState(ref reader);
        ValidateBoatStates(boats);
        return new BoatBaselineMessage(sequence, tick, boats);
    }

    private static void WriteBoatDeltaBatch(
        WireWriter writer,
        BoatDeltaBatchMessage value)
    {
        ValidateBoatDeltas(value.Deltas);
        writer.WriteUInt16((ushort)value.Deltas.Count);
        foreach (var delta in value.Deltas)
        {
            writer.WriteByte((byte)delta.Kind);
            WriteBoatReference(writer, delta.Reference);
            writer.WriteUInt32(delta.CurrentRevision);
            writer.WriteBoolean(delta.State is not null);
            if (delta.State is { } state) WriteBoatState(writer, state);
        }
    }

    private static BoatDeltaBatchMessage ReadBoatDeltaBatch(
        ulong sequence,
        ulong tick,
        ref WireReader reader)
    {
        var count = reader.ReadUInt16();
        if (count > ProtocolLimits.MaxBoatsPerBatch)
            throw new ProtocolException("Boat delta batch exceeds its hard limit.");
        var deltas = new BoatDelta[count];
        for (var index = 0; index < deltas.Length; index++)
        {
            var kind = ReadEnum<BoatDeltaKind>(
                reader.ReadByte(), nameof(BoatDeltaKind));
            var reference = ReadBoatReference(ref reader);
            var revision = reader.ReadUInt32();
            var hasState = reader.ReadBoolean();
            deltas[index] = new BoatDelta(
                kind, reference, revision,
                hasState ? ReadBoatState(ref reader) : null);
        }
        ValidateBoatDeltas(deltas);
        return new BoatDeltaBatchMessage(sequence, tick, deltas);
    }

    private static void WriteBoatActionResult(
        WireWriter writer,
        BoatActionResultMessage value)
    {
        ValidateBoatActionResult(value);
        writer.WriteGuid(value.CommandId);
        writer.WriteByte((byte)value.Action);
        WriteBoatReference(writer, value.Boat);
        writer.WriteBoolean(value.Accepted);
        writer.WriteByte((byte)value.RejectionCode);
        writer.WriteString(value.Detail, ProtocolLimits.DetailBytes,
            nameof(value.Detail));
        writer.WriteUInt32(value.ActorRevision);
        writer.WriteUInt32(value.InventoryRevision);
        writer.WriteUInt32(value.BoatRevision);
        writer.WriteBoolean(value.Transitioned);
        writer.WriteSingle(value.ActorX);
        writer.WriteSingle(value.ActorY);
        writer.WriteInt16(value.ActorWorldLevel);
    }

    private static BoatActionResultMessage ReadBoatActionResult(
        ulong sequence,
        ulong tick,
        ref WireReader reader)
    {
        var result = new BoatActionResultMessage(
            sequence,
            tick,
            reader.ReadGuid(),
            ReadEnum<BoatActionKind>(reader.ReadByte(), nameof(BoatActionKind)),
            ReadBoatReference(ref reader),
            reader.ReadBoolean(),
            ReadEnum<CommandRejectionCode>(
                reader.ReadByte(), nameof(CommandRejectionCode)),
            reader.ReadString(ProtocolLimits.DetailBytes, "Detail"),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadBoolean(),
            ReadFinite(ref reader, "ActorX"),
            ReadFinite(ref reader, "ActorY"),
            reader.ReadInt16());
        ValidateBoatActionResult(result);
        return result;
    }

    private static void WriteBoatState(WireWriter writer, BoatState value)
    {
        ValidateBoatState(value);
        writer.WriteGuid(value.BoatId);
        writer.WriteUInt64(value.EntityId);
        writer.WriteUInt32(value.Revision);
        writer.WriteGuid(value.OwnerPlayerId);
        writer.WriteString(value.GroupOwnerId, ProtocolLimits.GroupOwnerIdBytes,
            nameof(value.GroupOwnerId));
        writer.WriteGuid(value.OccupantPlayerId);
        writer.WriteUInt64(value.OccupantEntityId);
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteSingle(value.FacingX);
        writer.WriteSingle(value.FacingY);
        writer.WriteInt16(value.WorldLevel);
        writer.WriteBoolean(value.Moving);
    }

    private static BoatState ReadBoatState(ref WireReader reader) => new(
        reader.ReadGuid(),
        reader.ReadUInt64(),
        reader.ReadUInt32(),
        reader.ReadGuid(),
        reader.ReadString(ProtocolLimits.GroupOwnerIdBytes, "GroupOwnerId"),
        reader.ReadGuid(),
        reader.ReadUInt64(),
        ReadFinite(ref reader, "BoatX"),
        ReadFinite(ref reader, "BoatY"),
        ReadFinite(ref reader, "BoatFacingX"),
        ReadFinite(ref reader, "BoatFacingY"),
        reader.ReadInt16(),
        reader.ReadBoolean());

    private static void ValidateBoatStates(IReadOnlyList<BoatState>? values)
    {
        if (values is null || values.Count > ProtocolLimits.MaxBoatsPerBatch)
            throw new ProtocolException("Boat state count exceeds its hard limit.");
        var boats = new HashSet<Guid>();
        var entities = new HashSet<ulong>();
        var occupants = new HashSet<Guid>();
        foreach (var value in values)
        {
            ValidateBoatState(value);
            if (!boats.Add(value.BoatId) || !entities.Add(value.EntityId) ||
                value.OccupantPlayerId != Guid.Empty &&
                !occupants.Add(value.OccupantPlayerId))
                throw new ProtocolException("Boat identities or occupants are duplicated.");
        }
    }

    private static void ValidateBoatState(BoatState value)
    {
        EnsureBoatId(value.BoatId);
        if (value.EntityId == 0 || value.Revision == 0)
            throw new ProtocolException("Boat state omitted its entity or revision.");
        EnsureFinite(value.X, nameof(value.X));
        EnsureFinite(value.Y, nameof(value.Y));
        EnsureFinite(value.FacingX, nameof(value.FacingX));
        EnsureFinite(value.FacingY, nameof(value.FacingY));
        if (value.FacingX * value.FacingX + value.FacingY * value.FacingY <= .0001f)
            throw new ProtocolException("Boat facing must be non-zero.");
        if ((value.OccupantPlayerId == Guid.Empty) !=
            (value.OccupantEntityId == 0))
            throw new ProtocolException("Boat occupant identities are incomplete.");
        if (value.OwnerPlayerId == Guid.Empty &&
            string.IsNullOrEmpty(value.GroupOwnerId))
            throw new ProtocolException("Boat state must have an individual or group owner.");
    }

    private static void ValidateBoatDeltas(IReadOnlyList<BoatDelta>? deltas)
    {
        if (deltas is null || deltas.Count == 0 ||
            deltas.Count > ProtocolLimits.MaxBoatsPerBatch)
            throw new ProtocolException("Boat delta count exceeds its hard limit.");
        var ids = new HashSet<Guid>();
        foreach (var delta in deltas)
        {
            EnsureDefined(delta.Kind, nameof(delta.Kind));
            EnsureBoatId(delta.Reference.BoatId);
            if (!ids.Add(delta.Reference.BoatId) ||
                delta.CurrentRevision <= delta.Reference.ExpectedRevision)
                throw new ProtocolException("Boat delta revision chain is invalid.");
            if (delta.Kind == BoatDeltaKind.Upsert)
            {
                if (delta.State is not { } state ||
                    state.BoatId != delta.Reference.BoatId ||
                    state.Revision != delta.CurrentRevision)
                    throw new ProtocolException("Boat upsert does not match its reference.");
                ValidateBoatState(state);
            }
            else if (delta.State is not null)
                throw new ProtocolException("Boat removal cannot include state.");
        }
    }

    private static void ValidateBoatActionResult(BoatActionResultMessage value)
    {
        EnsureCommandId(value.CommandId);
        EnsureDefined(value.Action, nameof(value.Action));
        EnsureBoatId(value.Boat.BoatId);
        ValidateActionResult(value.Accepted, value.RejectionCode);
        EnsureFinite(value.ActorX, nameof(value.ActorX));
        EnsureFinite(value.ActorY, nameof(value.ActorY));
        if (value.Transitioned && !value.Accepted ||
            value.BoatRevision < value.Boat.ExpectedRevision)
            throw new ProtocolException("Boat action result has inconsistent revisions.");
    }

    private static ResourceNodeSparseState ReadResourceNodeState(
        ref WireReader reader)
    {
        var result = new ResourceNodeSparseState(
            new ResourceNodeId(reader.ReadGuid()),
            ReadEnum<ResourceNodeKind>(reader.ReadByte(), "ResourceNodeKind"),
            ReadResourceChunk(ref reader),
            reader.ReadUInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadDouble(),
            reader.ReadBoolean());
        ValidateResourceNodeState(result);
        return result;
    }

    private static void ValidateResourceNodeState(ResourceNodeSparseState value)
    {
        EnsureResourceNodeId(value.Id);
        EnsureDefined(value.Kind, nameof(value.Kind));
        if (value.NodeRevision == 0)
            throw new ProtocolException("Sparse resource node revisions must be positive.");
        if (!ResourceNodeStateRules.IsShapeValid(value))
            throw new ProtocolException(
                "The sparse resource lifecycle state is invalid for its kind.");
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

    private static void WriteCookingResult(
        WireWriter writer, CookingResultMessage value)
    {
        EnsureCommandId(value.CommandId);
        EnsureIdentifier(value.RawItemId, nameof(value.RawItemId));
        EnsureIdentifier(value.ResultItemId, nameof(value.ResultItemId));
        if (value.Burnt && value.Interrupted)
            throw new ProtocolException(
                "Interrupted cooking cannot also be burnt.");
        writer.WriteGuid(value.CommandId);
        writer.WriteString(
            value.RawItemId, ProtocolLimits.ItemIdBytes,
            nameof(value.RawItemId));
        writer.WriteString(
            value.ResultItemId, ProtocolLimits.ItemIdBytes,
            nameof(value.ResultItemId));
        writer.WriteBoolean(value.Burnt);
        writer.WriteBoolean(value.Interrupted);
        writer.WriteUInt32(value.ActorRevision);
        writer.WriteUInt32(value.InventoryRevision);
    }

    private static CookingResultMessage ReadCookingResult(
        ulong sequence, ulong tick, ref WireReader reader)
    {
        var commandId = reader.ReadGuid();
        EnsureCommandId(commandId);
        var raw = ReadIdentifier(
            ref reader, ProtocolLimits.ItemIdBytes, "RawItemId");
        var result = ReadIdentifier(
            ref reader, ProtocolLimits.ItemIdBytes, "ResultItemId");
        var burnt = reader.ReadBoolean();
        var interrupted = reader.ReadBoolean();
        if (burnt && interrupted)
            throw new ProtocolException(
                "Interrupted cooking cannot also be burnt.");
        return new CookingResultMessage(
            sequence, tick, commandId, raw, result, burnt, interrupted,
            reader.ReadUInt32(), reader.ReadUInt32());
    }

    private static void WriteCaveActionResult(
        WireWriter writer,
        CaveActionResultMessage value)
    {
        EnsureCommandId(value.CommandId);
        EnsureDefined(value.Action, nameof(value.Action));
        ValidateActionResult(value.Accepted, value.RejectionCode);
        if (value.Transitioned)
        {
            if (!value.Accepted || value.Action != CaveActionKind.Traverse)
                throw new ProtocolException(
                    "Only an accepted cave traversal can carry a transition.");
            EnsureFinite(value.X, nameof(value.X));
            EnsureFinite(value.Y, nameof(value.Y));
        }
        else if (value.X != 0 || value.Y != 0 || value.WorldLevel != 0)
            throw new ProtocolException(
                "A cave receipt without a transition must clear its destination.");
        if (value.Damage < 0 ||
            ((value.Action != CaveActionKind.WorkExcavation ||
              !value.Accepted) &&
             (value.Damage != 0 || value.Completed)))
            throw new ProtocolException(
                "Only accepted excavation work can carry a cave outcome.");
        writer.WriteGuid(value.CommandId);
        writer.WriteByte((byte)value.Action);
        writer.WriteBoolean(value.Accepted);
        writer.WriteByte((byte)value.RejectionCode);
        writer.WriteString(
            value.Detail, ProtocolLimits.DetailBytes, nameof(value.Detail));
        writer.WriteUInt32(value.ActorRevision);
        writer.WriteUInt32(value.InventoryRevision);
        writer.WriteBoolean(value.Transitioned);
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteInt16(value.WorldLevel);
        writer.WriteInt32(value.Damage);
        writer.WriteBoolean(value.Completed);
    }

    private static CaveActionResultMessage ReadCaveActionResult(
        ulong sequence,
        ulong tick,
        ref WireReader reader)
    {
        var result = new CaveActionResultMessage(
            sequence,
            tick,
            reader.ReadGuid(),
            ReadEnum<CaveActionKind>(reader.ReadByte(), nameof(CaveActionKind)),
            reader.ReadBoolean(),
            ReadEnum<CommandRejectionCode>(
                reader.ReadByte(), nameof(CommandRejectionCode)),
            reader.ReadString(ProtocolLimits.DetailBytes, "Detail"),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadBoolean(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadInt16(),
            reader.ReadInt32(),
            reader.ReadBoolean());
        // Reuse the exact encode-side semantic validation.
        var discard = new WireWriter();
        WriteCaveActionResult(discard, result);
        return result;
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
        writer.WriteInt32(value.WoodcuttingExperience);
        writer.WriteInt32(value.FarmingExperience);
        writer.WriteInt32(value.MiningExperience);
        writer.WriteInt32(value.AdventureExperience);
        writer.WriteInt32(value.DiggingExperience);
        writer.WriteInt32(value.FishingExperience);
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
        var woodcuttingExperience = reader.ReadInt32();
        var farmingExperience = reader.ReadInt32();
        var miningExperience = reader.ReadInt32();
        var adventureExperience = reader.ReadInt32();
        var diggingExperience = reader.ReadInt32();
        var fishingExperience = reader.ReadInt32();

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
            inventorySlots,
            woodcuttingExperience,
            farmingExperience,
            miningExperience,
            adventureExperience,
            diggingExperience,
            fishingExperience);
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

        if (value.CraftingExperience < 0 || value.CookingExperience < 0 ||
            value.WoodcuttingExperience < 0 ||
            value.FarmingExperience < 0 ||
            value.MiningExperience < 0 ||
            value.AdventureExperience < 0 ||
            value.DiggingExperience < 0 ||
            value.FishingExperience < 0)
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

    private static void EnsureBoatId(Guid boatId)
    {
        if (boatId == Guid.Empty)
        {
            throw new ProtocolException("Boat ID cannot be empty.");
        }
    }

    private static void EnsureResourceNodeId(ResourceNodeId resourceNodeId)
    {
        if (resourceNodeId.IsEmpty)
            throw new ProtocolException("Resource node ID cannot be empty.");
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
