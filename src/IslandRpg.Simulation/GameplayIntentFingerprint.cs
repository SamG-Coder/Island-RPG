using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace IslandRpg.Simulation;

/// <summary>
/// Produces a stable, culture-independent identity for a gameplay payload.
/// Every value is preceded by an explicit type discriminator and encoded with
/// a fixed byte order. The digest is fixed-size, so durable receipt histories
/// remain bounded even when an intent contains text.
/// </summary>
public static class GameplayIntentFingerprint
{
    public const int HexLength = 64;

    public static string Create(GameplayIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        using var writer = new FingerprintWriter();
        writer.Guid(intent.CommandId);
        writer.UInt32(intent.ExpectedInventoryRevision);
        writer.UInt32(intent.ExpectedActorRevision);

        switch (intent)
        {
            case SwapInventorySlotsIntent value:
                writer.Tag(1);
                writer.Int32(value.SourceSlot);
                writer.Int32(value.TargetSlot);
                break;
            case CombineInventorySlotsIntent value:
                writer.Tag(2);
                writer.Int32(value.FirstSlot);
                writer.Int32(value.SecondSlot);
                break;
            case CraftRecipeIntent value:
                writer.Tag(3);
                writer.Text(value.RecipeId);
                break;
            case ConsumeFoodIntent value:
                writer.Tag(4);
                writer.Int32(value.Slot);
                break;
            case PickUpWorldObjectIntent value:
                writer.Tag(5);
                writer.Handle(value.Object);
                break;
            case DropInventoryItemIntent value:
                writer.Tag(6);
                writer.Int32(value.InventorySlot);
                writer.Int32(value.Quantity);
                writer.Vector(value.Position);
                writer.Int32(value.WorldLevel);
                writer.UInt32(value.ExpectedChunkRevision);
                break;
            case OpenWorldContainerIntent value:
                writer.Tag(7);
                writer.Handle(value.Container);
                break;
            case TransferWorldContainerIntent value:
                writer.Tag(8);
                writer.Handle(value.Container);
                writer.Int32((int)value.Direction);
                writer.Int32(value.InventorySlot);
                writer.Int32(value.ContainerSlot);
                writer.Int32(value.Quantity);
                break;
            case AddCampfireFuelIntent value:
                writer.Tag(9);
                writer.Handle(value.Campfire);
                writer.Int32(value.InventorySlot);
                break;
            case TakeCampfireFuelIntent value:
                writer.Tag(10);
                writer.Handle(value.Campfire);
                break;
            case LightCampfireIntent value:
                writer.Tag(11);
                writer.Handle(value.Campfire);
                break;
            case PlaceConstructionIntent value:
                writer.Tag(12);
                writer.Text(value.DefinitionId);
                writer.Vector(value.Position);
                writer.Int32(value.WorldLevel);
                writer.Int32(value.Rotation);
                writer.UInt32(value.ExpectedChunkRevision);
                break;
            case BuildConstructionIntent value:
                writer.Tag(13);
                writer.Handle(value.Construction);
                break;
            case DemolishWorldObjectIntent value:
                writer.Tag(14);
                writer.Handle(value.Object);
                break;
            case CookOnCampfireIntent value:
                writer.Tag(15);
                writer.Handle(value.Campfire);
                writer.Int32(value.InventorySlot);
                break;
            case GatherTreeStickIntent value:
                writer.Tag(16);
                writer.Resource(value.Node);
                break;
            case StrikeTreeIntent value:
                writer.Tag(17);
                writer.Resource(value.Node);
                writer.Int32(value.ToolInventorySlot);
                break;
            case GatherFibreIntent value:
                writer.Tag(18);
                writer.Resource(value.Node);
                break;
            case GatherBerriesIntent value:
                writer.Tag(19);
                writer.Resource(value.Node);
                writer.Int32(value.ToolInventorySlot);
                break;
            case MineResourceIntent value:
                writer.Tag(20);
                writer.Resource(value.Node);
                writer.Int32(value.ToolInventorySlot);
                break;
            case StartExcavationIntent value:
                writer.Tag(21);
                writer.Vector(value.Position);
                writer.Int32(value.WorldLevel);
                writer.Int32(value.ShovelInventorySlot);
                writer.UInt32(value.ExpectedChunkRevision);
                break;
            case WorkExcavationIntent value:
                writer.Tag(22);
                writer.Handle(value.Excavation);
                writer.Int32(value.ShovelInventorySlot);
                break;
            case RestoreExcavationIntent value:
                writer.Tag(23);
                writer.Handle(value.Excavation);
                break;
            case InstallCaveRopeIntent value:
                writer.Tag(24);
                writer.Handle(value.Shaft);
                writer.Int32(value.RopeInventorySlot);
                break;
            case TakeCaveRopeIntent value:
                writer.Tag(25);
                writer.Handle(value.Entrance);
                break;
            case FillExcavationIntent value:
                writer.Tag(26);
                writer.Handle(value.Excavation);
                writer.Int32(value.MaterialInventorySlot);
                break;
            case TraverseCaveIntent value:
                writer.Tag(27);
                writer.Handle(value.Entrance);
                break;
            case CatchFishIntent value:
                writer.Tag(28);
                writer.Resource(value.Node);
                writer.Int32(value.FishingNetInventorySlot);
                break;
            case BoardBoatIntent value:
                writer.Tag(29);
                writer.Boat(value.Boat);
                break;
            case MoveBoatIntent value:
                writer.Tag(30);
                writer.Boat(value.Boat);
                writer.Vector(value.Target);
                break;
            case StopBoatIntent value:
                writer.Tag(31);
                writer.Boat(value.Boat);
                break;
            case DisembarkBoatIntent value:
                writer.Tag(32);
                writer.Boat(value.Boat);
                writer.Vector(value.RequestedLanding);
                break;
            case SetCombatTargetIntent value:
                writer.Tag(33);
                writer.Guid(value.Enemy.EnemyId.Value);
                writer.UInt32(value.Enemy.ExpectedRevision);
                break;
            case CancelCombatIntent:
                writer.Tag(34);
                break;
            case SetCombatStanceIntent value:
                writer.Tag(35);
                writer.Int32((int)value.Stance);
                break;
            case RespawnIntent:
                writer.Tag(36);
                break;
            case PlantCropIntent value:
                writer.Tag(37);
                writer.Int32(value.SeedInventorySlot);
                writer.Vector(value.Position);
                writer.Int32(value.WorldLevel);
                writer.UInt32(value.ExpectedChunkRevision);
                break;
            case HarvestCropIntent value:
                writer.Tag(38);
                writer.Handle(value.Crop);
                break;
            default:
                throw new NotSupportedException(
                    $"Gameplay intent type '{intent.GetType().Name}' has no canonical fingerprint.");
        }

        return Convert.ToHexString(writer.Finish()).ToLowerInvariant();
    }

    public static bool IsValid(string? value) =>
        value is { Length: HexLength } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed class FingerprintWriter : IDisposable
    {
        private readonly IncrementalHash _hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void Tag(byte value) => _hash.AppendData([value]);

        public void Int32(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            _hash.AppendData(bytes);
        }

        public void UInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            _hash.AppendData(bytes);
        }

        public void Guid(Guid value)
        {
            Span<byte> bytes = stackalloc byte[16];
            value.TryWriteBytes(bytes, bigEndian: true, out _);
            _hash.AppendData(bytes);
        }

        public void Vector(System.Numerics.Vector2 value)
        {
            Int32(BitConverter.SingleToInt32Bits(value.X));
            Int32(BitConverter.SingleToInt32Bits(value.Y));
        }

        public void Handle(WorldObjectHandle value)
        {
            Guid(value.ObjectId);
            Int32(value.Chunk.X);
            Int32(value.Chunk.Y);
            Int32(value.Chunk.WorldLevel);
            UInt32(value.ExpectedObjectRevision);
            UInt32(value.ExpectedChunkRevision);
            UInt32(value.ExpectedContainerRevision);
        }

        public void Resource(IslandRpg.Resources.ResourceNodeReference value)
        {
            Guid(value.Id.Value);
            Int32(value.Chunk.X);
            Int32(value.Chunk.Y);
            Int32(value.Chunk.WorldLevel);
            UInt32(value.ExpectedNodeRevision);
            UInt32(value.ExpectedResourceChunkRevision);
        }

        public void Boat(BoatReference value)
        {
            Guid(value.BoatId.Value);
            UInt32(value.ExpectedRevision);
        }

        public void Text(string? value)
        {
            if (value is null)
            {
                Int32(-1);
                return;
            }

            var byteCount = Encoding.UTF8.GetByteCount(value);
            Int32(byteCount);
            var encoder = Encoding.UTF8.GetEncoder();
            ReadOnlySpan<char> remaining = value;
            Span<byte> buffer = stackalloc byte[256];
            while (!remaining.IsEmpty)
            {
                encoder.Convert(remaining, buffer, flush: true,
                    out var charsUsed, out var bytesUsed, out _);
                _hash.AppendData(buffer[..bytesUsed]);
                remaining = remaining[charsUsed..];
            }
        }

        public byte[] Finish() => _hash.GetHashAndReset();

        public void Dispose() => _hash.Dispose();
    }
}
