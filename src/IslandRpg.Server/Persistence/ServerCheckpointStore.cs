using System.Text.Json;
using IslandRpg.Caves;
using IslandRpg.Gameplay;
using IslandRpg.Simulation;

namespace IslandRpg.Server.Persistence;

/// <summary>
/// Crash-safe, server-owned checkpoint storage. A checkpoint crosses actor,
/// inventory and world-object aggregates in one file so a committed transfer
/// cannot be restored on only one side of the transaction.
/// </summary>
public sealed class ServerCheckpointStore
{
    public const long MaximumCheckpointBytes = 256L * 1024 * 1024;
    public const int MaximumActors = 1_024;
    public const int MaximumWorldObjects = 2_000_000;
    public const int MaximumChunkRevisions = 1_000_000;
    public const int MaximumContainerSlots = 1_024;
    public const int MaximumStackQuantity = 1_000_000;
    public const int PlayerInventoryCapacity = 28;
    public const int MaximumBoats = 256;
    public const int MaximumBoatRoutePoints = 4_096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = ServerCheckpointJsonContext.Default,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    private readonly object _sync = new();
    private readonly string _root;

    public ServerCheckpointStore(string saveRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveRoot);
        _root = Path.GetFullPath(saveRoot);
    }

    public string SaveRoot => _root;

    public string CheckpointPath(Guid worldId)
    {
        ValidateWorldId(worldId);
        return Path.Combine(_root, "Worlds", worldId.ToString("N"),
            "server-checkpoint.json");
    }

    public ServerCheckpointLoadResult? Load(Guid worldId)
    {
        ValidateWorldId(worldId);
        lock (_sync)
        {
            var path = CheckpointPath(worldId);
            if (TryRead(path, worldId, out var checkpoint))
                return new ServerCheckpointLoadResult(checkpoint!, false);

            var backup = BackupPath(path);
            if (TryRead(backup, worldId, out checkpoint))
                return new ServerCheckpointLoadResult(checkpoint!, true);
            if (File.Exists(path) || File.Exists(backup))
                throw new InvalidDataException(
                    "Authoritative checkpoint files exist, but none are valid.");
            return null;
        }
    }

    /// <summary>
    /// Prevents two dedicated-server processes from hosting and saving the
    /// same world concurrently. Hold the returned lease for the complete host
    /// lifetime, not merely for an individual save operation.
    /// </summary>
    public IDisposable AcquireWorldLease(Guid worldId)
    {
        ValidateWorldId(worldId);
        var directory = Path.GetDirectoryName(CheckpointPath(worldId))!;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "server.lock");
        try
        {
            return new WorldLease(new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.WriteThrough));
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"World {worldId:N} is already hosted by another server process.",
                exception);
        }
    }

    public void Save(ServerCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        Validate(checkpoint, checkpoint.WorldId);

        lock (_sync)
        {
            var path = CheckpointPath(checkpoint.WorldId);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);

            var current = LoadCurrentRevision(
                path, checkpoint.WorldId, out var primaryIsValid);
            if (current >= checkpoint.Revision)
            {
                throw new InvalidOperationException(
                    $"Checkpoint revision {checkpoint.Revision} is not newer than durable revision {current}.");
            }

            var data = JsonSerializer.SerializeToUtf8Bytes(checkpoint, JsonOptions);
            if (data.LongLength > MaximumCheckpointBytes)
                throw new InvalidOperationException("The checkpoint exceeds the durable size limit.");

            var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
            var backupTemporary = path + ".bak.tmp." + Guid.NewGuid().ToString("N");
            try
            {
                WriteDurable(temporary, data);
                if (primaryIsValid)
                {
                    File.Copy(path, backupTemporary, overwrite: false);
                    FlushExistingFile(backupTemporary);
                    File.Move(backupTemporary, BackupPath(path), overwrite: true);
                }

                if (primaryIsValid && OperatingSystem.IsWindows())
                {
                    // File.Replace asks Windows for one same-volume atomic
                    // replacement while retaining the known-good backup.
                    File.Replace(temporary, path, BackupPath(path),
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporary, path, overwrite: true);
                }
            }
            finally
            {
                DeleteIfPresent(temporary);
                DeleteIfPresent(backupTemporary);
            }
        }
    }

    private static long LoadCurrentRevision(
        string path,
        Guid worldId,
        out bool primaryIsValid)
    {
        primaryIsValid = TryRead(path, worldId, out var checkpoint);
        if (primaryIsValid) return checkpoint!.Revision;

        var primaryExists = File.Exists(path);
        var backupExists = File.Exists(BackupPath(path));
        if (TryRead(BackupPath(path), worldId, out checkpoint))
            return checkpoint!.Revision;
        if (primaryExists || backupExists)
            throw new InvalidDataException(
                "The current checkpoint is invalid; refusing to overwrite recoverable evidence.");
        return 0;
    }

    private static bool TryRead(
        string path,
        Guid worldId,
        out ServerCheckpoint? checkpoint)
    {
        checkpoint = null;
        if (!File.Exists(path)) return false;

        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumCheckpointBytes) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            checkpoint = JsonSerializer.Deserialize<ServerCheckpoint>(stream, JsonOptions);
            if (checkpoint is null) return false;
            Validate(checkpoint, worldId);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or ArgumentException or OverflowException)
        {
            checkpoint = null;
            return false;
        }
    }

    public static void Validate(ServerCheckpoint value, Guid expectedWorldId)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateWorldId(expectedWorldId);
        if (value.SchemaVersion != ServerCheckpoint.CurrentSchemaVersion)
            throw new InvalidDataException("The server checkpoint schema is unsupported.");
        if (value.Revision <= 0 || value.WorldId != expectedWorldId ||
            value.SessionId == Guid.Empty || value.Tick < 0 ||
            value.SnapshotSequence < 0)
            throw new InvalidDataException("The server checkpoint header is invalid.");
        ValidateText(value.BuildVersion, 64, nameof(value.BuildVersion));
        ValidateText(value.ContentVersion, 64, nameof(value.ContentVersion));
        if (value.Actors is null || value.Actors.Count > MaximumActors ||
            value.WorldObjects is null || value.WorldObjects.Count > MaximumWorldObjects ||
            value.ChunkRevisions is null ||
            value.ChunkRevisions.Count > MaximumChunkRevisions)
            throw new InvalidDataException("The server checkpoint collection limits are invalid.");

        var players = new HashSet<Guid>();
        var actors = new HashSet<Guid>();
        foreach (var actor in value.Actors)
        {
            if (actor.PlayerId == Guid.Empty || actor.ActorId == Guid.Empty ||
                !players.Add(actor.PlayerId) || !actors.Add(actor.ActorId) ||
                !float.IsFinite(actor.X) || !float.IsFinite(actor.Y) ||
                actor.LastProcessedCommandSequence < 0 || actor.ActorRevision == 0 ||
                actor.InventoryRevision == 0 || actor.Health is < 0 or > 100 ||
                !float.IsFinite(actor.Hunger) || actor.Hunger is < 0 or > 100 ||
                !float.IsFinite(actor.WellFedSeconds) || actor.WellFedSeconds < 0 ||
                actor.CraftingExperience < 0 || actor.CookingExperience < 0 ||
                actor.WoodcuttingExperience < 0 ||
                actor.FarmingExperience < 0 || actor.MiningExperience < 0 ||
                actor.AdventureExperience < 0 ||
                actor.DiggingExperience < 0 ||
                actor.FishingExperience < 0 ||
                actor.ReconnectTokenHash is not { Length: 32 } ||
                actor.Inventory is null || actor.Inventory.Count != PlayerInventoryCapacity ||
                actor.CommandReceipts is null ||
                actor.CommandReceipts.Count > 256)
                throw new InvalidDataException("An actor checkpoint is invalid.");
            ValidateText(actor.DisplayName, 64, nameof(actor.DisplayName));
            ValidateInventory(actor.Inventory);
            var commands = new HashSet<Guid>();
            foreach (var receipt in actor.CommandReceipts)
            {
                if (receipt.CommandId == Guid.Empty ||
                    !commands.Add(receipt.CommandId) ||
                    !GameplayIntentFingerprint.IsValid(
                        receipt.PayloadFingerprint) ||
                    !Enum.IsDefined(receipt.Status) ||
                    receipt.Error is { Length: > 512 } ||
                    receipt.Error?.Any(char.IsControl) == true)
                    throw new InvalidDataException(
                        "An actor command receipt checkpoint is invalid.");
            }
        }

        var objects = new HashSet<Guid>();
        var objectsById = new Dictionary<Guid, ServerWorldObjectCheckpoint>();
        foreach (var item in value.WorldObjects)
        {
            if (item.ObjectId == Guid.Empty || !objects.Add(item.ObjectId) ||
                !float.IsFinite(item.X) || !float.IsFinite(item.Y) ||
                item.ObjectRevision == 0 || item.ContainerRevision == 0 ||
                item.Health < 0 || item.MaximumHealth < 0 ||
                item.Health > item.MaximumHealth && item.MaximumHealth > 0 ||
                !double.IsFinite(item.LitUntilGameSeconds) ||
                item.LitUntilGameSeconds < 0 || item.Container is null ||
                item.FiremakingLevel is < 1 or > 20 ||
                !Enum.IsDefined(item.GateState) ||
                item.Container.Count > MaximumContainerSlots ||
                item.ChunkX != ChunkCoordinate(item.X) ||
                item.ChunkY != ChunkCoordinate(item.Y))
                throw new InvalidDataException("A world-object checkpoint is invalid.");
            objectsById.Add(item.ObjectId, item);
            ValidateText(item.DefinitionId, 128, nameof(item.DefinitionId));
            if (!item.HasContainer && item.Container.Count != 0)
                throw new InvalidDataException("A non-container object contains slots.");
            ValidateContainer(item.Container);
            if (item.LinkedObjectId == item.ObjectId)
                throw new InvalidDataException(
                    "A linked world object must use a distinct identity.");
        }

        foreach (var item in value.WorldObjects)
        {
            if (item.LinkedObjectId is not { } linkedId) continue;
            if (!objectsById.TryGetValue(linkedId, out var linked) ||
                linked.LinkedObjectId != item.ObjectId ||
                linked.DefinitionId != item.DefinitionId ||
                item.DefinitionId is not (
                    CaveExcavationRules.OpenShaftItemId or
                    CaveExcavationRules.RopedEntranceItemId) ||
                linked.WorldLevel == item.WorldLevel ||
                linked.X != item.X || linked.Y != item.Y)
                throw new InvalidDataException(
                    "A persisted cave link is missing its reciprocal portal.");
        }

        var excavationCadences = new HashSet<(Guid ActorId, Guid ObjectId)>();
        foreach (var cadence in value.ExcavationCadences ?? [])
        {
            if (!actors.Contains(cadence.ActorId) ||
                !objects.Contains(cadence.ExcavationId) ||
                !double.IsFinite(cadence.NextAllowedGameSeconds) ||
                cadence.NextAllowedGameSeconds < 0 ||
                !excavationCadences.Add(
                    (cadence.ActorId, cadence.ExcavationId)))
                throw new InvalidDataException(
                    "An excavation cadence checkpoint is invalid.");
        }

        var chunks = new HashSet<(int X, int Y, int Level)>();
        foreach (var chunk in value.ChunkRevisions)
            if (chunk.Revision == 0 || !chunks.Add((chunk.X, chunk.Y, chunk.WorldLevel)))
                throw new InvalidDataException("A chunk revision checkpoint is invalid.");

        var cookingActors = new HashSet<Guid>();
        foreach (var job in value.CookingJobs ?? [])
        {
            objectsById.TryGetValue(job.CampfireId, out var campfire);
            var validOutcome = CookingSkill.TryProfile(
                job.RawItemId, out var profile) &&
                (job.Burnt
                    ? job.ResultItemId == profile.BurntItemId &&
                      job.Experience == 0
                    : job.ResultItemId == profile.CookedItemId &&
                      job.Experience == profile.Experience);
            if (job.CommandId == Guid.Empty || job.ActorId == Guid.Empty ||
                job.CampfireId == Guid.Empty || job.DropObjectId == Guid.Empty ||
                !actors.Contains(job.ActorId) ||
                objects.Contains(job.DropObjectId) ||
                campfire is null ||
                campfire.DefinitionId != ItemIds.Campfire ||
                campfire.ChunkX != job.CampfireChunkX ||
                campfire.ChunkY != job.CampfireChunkY ||
                campfire.WorldLevel != job.WorldLevel ||
                campfire.X != job.CampfireX ||
                campfire.Y != job.CampfireY ||
                !float.IsFinite(job.CampfireX) ||
                !float.IsFinite(job.CampfireY) ||
                job.PreferredInventorySlot is < 0 or >= PlayerInventoryCapacity ||
                !validOutcome || job.CompletesAtTick <= value.Tick ||
                !cookingActors.Add(job.ActorId))
                throw new InvalidDataException(
                    "A cooking-job checkpoint is invalid.");
            ValidateText(job.RawItemId, 128, nameof(job.RawItemId));
            ValidateText(job.ResultItemId, 128, nameof(job.ResultItemId));
        }

        ValidateResources(value.Resources, actors);
        ValidateBoats(value.Boats, players, actors);
    }

    private static void ValidateBoats(
        IReadOnlyList<ServerBoatCheckpoint>? boats,
        IReadOnlySet<Guid> players,
        IReadOnlySet<Guid> actors)
    {
        boats ??= [];
        if (boats.Count > MaximumBoats)
            throw new InvalidDataException(
                "The boat checkpoint collection exceeds its hard limit.");
        var ids = new HashSet<Guid>();
        var occupants = new HashSet<Guid>();
        foreach (var boat in boats)
        {
            if (boat.BoatId == Guid.Empty || !ids.Add(boat.BoatId) ||
                boat.OwnerPlayerId == Guid.Empty ||
                !players.Contains(boat.OwnerPlayerId) ||
                boat.WorldLevel != 0 || boat.Revision == 0 ||
                !float.IsFinite(boat.X) || !float.IsFinite(boat.Y) ||
                !float.IsFinite(boat.FacingX) ||
                !float.IsFinite(boat.FacingY) ||
                boat.FacingX * boat.FacingX +
                    boat.FacingY * boat.FacingY <= .0001f ||
                boat.GroupId is { Length: > 64 } ||
                boat.GroupId?.Any(char.IsControl) == true ||
                (boat.OccupantActorId is null) !=
                    (boat.OccupantPlayerId is null) ||
                boat.OccupantActorId is { } occupantActor &&
                    (!actors.Contains(occupantActor) ||
                     !occupants.Add(occupantActor)) ||
                boat.OccupantPlayerId is { } occupantPlayer &&
                    !players.Contains(occupantPlayer) ||
                boat.RemainingRoute is null ||
                boat.RemainingRoute.Count > MaximumBoatRoutePoints ||
                !double.IsFinite(boat.PlanningCooldownSeconds) ||
                boat.PlanningCooldownSeconds < 0 ||
                boat.PlanningCooldownSeconds > 60 ||
                boat.RemainingRoute.Any(static point =>
                    !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
                throw new InvalidDataException(
                    "A boat checkpoint is invalid.");
        }
    }

    private static void ValidateResources(
        ServerResourceCheckpoint? resources,
        IReadOnlySet<Guid> actors)
    {
        resources ??= new ServerResourceCheckpoint([], []);
        if (resources.Chunks is null || resources.ActorCadences is null ||
            resources.Chunks.Count > MaximumChunkRevisions ||
            resources.ActorCadences.Count > MaximumActors *
                Enum.GetValues<IslandRpg.Resources.ResourceActionKind>().Length)
            throw new InvalidDataException(
                "The resource checkpoint collection limits are invalid.");
        var chunks = new HashSet<(int X, int Y, int Level)>();
        var nodes = new HashSet<Guid>();
        foreach (var chunk in resources.Chunks)
        {
            if (chunk.Revision == 0 || chunk.Nodes is null ||
                chunk.Nodes.Count > 4_096 ||
                !chunks.Add((chunk.X, chunk.Y, chunk.WorldLevel)))
                throw new InvalidDataException(
                    "A resource chunk checkpoint is invalid.");
            foreach (var node in chunk.Nodes)
            {
                if (node.NodeId == Guid.Empty || !nodes.Add(node.NodeId) ||
                    !Enum.IsDefined(node.Kind) || node.NodeRevision == 0 ||
                    node.NodeRevision > chunk.Revision || node.Health < 0 ||
                    node.Remaining < 0 ||
                    !IslandRpg.Resources.ResourceNodeStateRules.IsShapeValid(
                        new IslandRpg.Resources.ResourceNodeSparseState(
                            new IslandRpg.Resources.ResourceNodeId(node.NodeId),
                            node.Kind,
                            new WorldChunkKey(
                                chunk.X, chunk.Y, chunk.WorldLevel),
                            node.NodeRevision,
                            node.Health,
                            node.Remaining,
                            node.ReadyAtGameSeconds,
                            node.Depleted)))
                    throw new InvalidDataException(
                        "A resource node checkpoint is invalid.");
            }
        }
        var cadences = new HashSet<(
            Guid ActorId, IslandRpg.Resources.ResourceActionKind Action)>();
        foreach (var cadence in resources.ActorCadences)
        {
            if (!actors.Contains(cadence.ActorId) ||
                !Enum.IsDefined(cadence.Action) ||
                !double.IsFinite(cadence.ReadyAtGameSeconds) ||
                cadence.ReadyAtGameSeconds < 0 ||
                cadence.ActionOrdinal == 0 ||
                !cadences.Add((cadence.ActorId, cadence.Action)))
                throw new InvalidDataException(
                    "A resource cadence checkpoint is invalid.");
        }
    }

    private static void ValidateInventory(
        IReadOnlyList<ServerInventorySlotCheckpoint> slots)
    {
        var seen = new bool[PlayerInventoryCapacity];
        foreach (var slot in slots)
        {
            if (slot.Slot < 0 || slot.Slot >= seen.Length || seen[slot.Slot] ||
                slot.Quantity is < 0 or > MaximumStackQuantity ||
                (slot.ItemId is null) != (slot.Quantity == 0))
                throw new InvalidDataException("An inventory slot checkpoint is invalid.");
            if (slot.ItemId is not null)
                ValidateText(slot.ItemId, 128, nameof(slot.ItemId));
            seen[slot.Slot] = true;
        }
    }

    private static void ValidateContainer(
        IReadOnlyList<ServerContainerSlotCheckpoint> slots)
    {
        var indexes = new HashSet<int>();
        foreach (var slot in slots)
        {
            if (slot.Slot < 0 || slot.Slot >= MaximumContainerSlots ||
                !indexes.Add(slot.Slot) ||
                slot.Quantity is < 0 or > MaximumStackQuantity ||
                (slot.ItemId is null) != (slot.Quantity == 0))
                throw new InvalidDataException("A container slot checkpoint is invalid.");
            if (slot.ItemId is not null)
                ValidateText(slot.ItemId, 128, nameof(slot.ItemId));
        }
    }

    private static void ValidateText(string value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            value.Any(char.IsControl))
            throw new InvalidDataException($"Checkpoint field '{name}' is invalid.");
    }

    private static void WriteDurable(string path, ReadOnlySpan<byte> data)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }

    private static void FlushExistingFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static string BackupPath(string checkpointPath) =>
        checkpointPath + ".bak";

    private static int ChunkCoordinate(float position)
    {
        var cell = (int)MathF.Floor(position);
        var quotient = cell / 32;
        return cell % 32 < 0 ? quotient - 1 : quotient;
    }

    private static void DeleteIfPresent(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A successful checkpoint is already durable. A stale uniquely
            // named temporary file is harmless and never selected during load.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above; never hide the result of the durable replacement.
        }
    }

    private static void ValidateWorldId(Guid worldId)
    {
        if (worldId == Guid.Empty)
            throw new ArgumentException("World identity must not be empty.", nameof(worldId));
    }

    private sealed class WorldLease(FileStream stream) : IDisposable
    {
        private FileStream? _stream = stream;

        public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
    }
}
