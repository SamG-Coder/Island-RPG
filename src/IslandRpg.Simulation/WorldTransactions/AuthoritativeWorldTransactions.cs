using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using IslandRpg.Gameplay;
using IslandRpg.Caves;
using IslandRpg.World;

namespace IslandRpg.Simulation;

/// <summary>
/// Single-owner aggregate for atomic authoritative world transactions. The
/// simulation thread is the only writer; callers receive immutable snapshots
/// and deltas, never references to mutable inventories or world collections.
/// </summary>
public sealed class AuthoritativeWorldTransactions
{
    public const float InteractionRange = 3f;
    public const double ExcavationCadenceSeconds = .9;
    private const int MaximumRememberedCommands = 4096;

    private int? _ownerThreadId;
    private readonly Dictionary<Guid, ObjectState> _objects = [];
    private readonly Dictionary<WorldChunkKey, uint> _chunkRevisions = [];
    private readonly Dictionary<WorldChunkKey, HashSet<Guid>>
        _objectsByChunk = [];
    private readonly Dictionary<WorldChunkKey, HashSet<Guid>>
        _campfiresByChunk = [];
    private readonly Dictionary<(ActorId ActorId, Guid CommandId),
        CommandReceipt> _commandResults = [];
    private readonly Queue<(ActorId ActorId, Guid CommandId)> _commandOrder = [];
    private readonly Func<Guid> _newObjectId;
    private readonly ICaveExcavationEnvironment? _caves;
    private readonly Dictionary<(ActorId ActorId, Guid ExcavationId), double>
        _excavationCadences = [];

    public AuthoritativeWorldTransactions(
        Func<Guid>? newObjectId = null,
        ICaveExcavationEnvironment? caves = null)
    {
        _newObjectId = newObjectId ?? Guid.NewGuid;
        _caves = caves;
    }

    public static Guid DeriveCropObjectId(
        ActorId actorId, Guid commandId, uint expectedActorRevision)
    {
        if (actorId.Value == Guid.Empty)
            throw new ArgumentException(
                "A valid actor identity is required.", nameof(actorId));
        if (commandId == Guid.Empty)
            throw new ArgumentException(
                "A valid command identity is required.", nameof(commandId));
        if (expectedActorRevision == 0)
            throw new ArgumentOutOfRangeException(
                nameof(expectedActorRevision));

        Span<byte> identity = stackalloc byte[36];
        actorId.Value.TryWriteBytes(
            identity[..16], bigEndian: true, out _);
        commandId.TryWriteBytes(
            identity[16..32], bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(
            identity[32..], expectedActorRevision);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(identity, digest);
        return new Guid(digest[..16], bigEndian: true);
    }

    public AuthoritativeWorldObjectSnapshot AddObject(WorldObjectSeed seed)
    {
        return AddObjectCore(seed).Snapshot;
    }

    /// <summary>
    /// Commits a trusted autonomous object and returns the exact public
    /// revision transition. Other authoritative systems use this for durable
    /// effects such as deterministic enemy loot bags.
    /// </summary>
    public WorldTransactionResult AddObjectCommitted(
        Guid commandId,
        WorldObjectSeed seed)
    {
        if (commandId == Guid.Empty)
            throw new ArgumentException(
                "A stable autonomous command identity is required.",
                nameof(commandId));
        var committed = AddObjectCore(seed);
        return new WorldTransactionResult(
            commandId,
            WorldTransactionStatus.Accepted,
            0,
            0,
            [new WorldObjectTransactionDelta(
                WorldObjectChangeKind.Added,
                committed.Snapshot.ObjectId,
                committed.Snapshot.Chunk,
                0,
                committed.Snapshot.ObjectRevision,
                committed.Snapshot)],
            [committed.ChunkDelta],
            null,
            null,
            "An authoritative world object was created.");
    }

    private AddedObjectCommit AddObjectCore(WorldObjectSeed seed)
    {
        EnsureOwner();
        if (seed.ObjectId == Guid.Empty || !IsFinite(seed.Position) ||
            seed.ObjectRevision == 0 || seed.ContainerRevision == 0 ||
            string.IsNullOrWhiteSpace(seed.DefinitionId) ||
            !ValidGameSeconds(seed.LitUntilGameSeconds) ||
            !ValidGateState(seed.DefinitionId, seed.GateState) ||
            seed.LinkedObjectId == Guid.Empty ||
            seed.LinkedObjectId == seed.ObjectId)
            throw new ArgumentException("The world-object seed is invalid.", nameof(seed));
        var value = new WorldGroundObject(
            seed.ObjectId,
            seed.DefinitionId,
            seed.Position.X,
            seed.Position.Y,
            seed.FuelItemId,
            seed.LitUntilGameSeconds,
            seed.FiremakingLevel,
            seed.Health,
            seed.MaximumHealth,
            OwnerId: seed.OwnerId,
            GroupOwnerId: seed.GroupOwnerId,
            VisualFrame: seed.Rotation,
            GateState: ToCoreGateState(seed.GateState));
        if (!CropService.HasValidPersistentState(value))
            throw new ArgumentException(
                "The seeded crop state is invalid.", nameof(seed));
        if (seed.ContainerItems is { Count: > 0 })
        {
            if (!WorldItemContainerService.IsContainer(seed.DefinitionId))
                throw new ArgumentException(
                    "Only container objects can be seeded with contents.",
                    nameof(seed));
            var container = WorldItemContainerService.OpenForSeeding(value);
            foreach (var item in seed.ContainerItems)
                if (item.Quantity <= 0 ||
                    !container.TryAdd(item.ItemId, item.Quantity, item.OwnerId))
                    throw new ArgumentException(
                        "The seeded container contents are invalid or too large.",
                        nameof(seed));
            value = WorldItemContainerService.Save(value, container);
        }
        var chunk = WorldChunkKey.At(seed.Position, seed.WorldLevel);
        if (ChunkRevision(chunk) == uint.MaxValue)
            throw new OverflowException(
                "The world chunk revision cannot advance any further.");
        if (!_objects.TryAdd(seed.ObjectId,
                new ObjectState(value, chunk, seed.ObjectRevision,
                    seed.ContainerRevision, seed.LinkedObjectId)))
            throw new InvalidOperationException("The world object already exists.");
        IndexObject(_objects[seed.ObjectId]);
        var chunkDelta = AdvanceChunk(chunk);
        return new AddedObjectCommit(
            Snapshot(_objects[seed.ObjectId]),
            chunkDelta);
    }

    public AuthoritativeWorldObjectSnapshot CaptureObject(Guid objectId)
    {
        EnsureOwner();
        if (!_objects.TryGetValue(objectId, out var value))
            throw new KeyNotFoundException("The world object does not exist.");
        return Snapshot(value);
    }

    public uint CaptureChunkRevision(WorldChunkKey chunk)
    {
        EnsureOwner();
        return ChunkRevision(chunk);
    }

    public bool HasLitCampfireWithin(
        Vector2 position,
        int worldLevel,
        double gameSeconds,
        float range)
    {
        EnsureOwner();
        if (!IsFinite(position))
            throw new ArgumentOutOfRangeException(
                nameof(position), "The query position must be finite.");
        if (!ValidGameSeconds(gameSeconds))
            throw new ArgumentOutOfRangeException(
                nameof(gameSeconds),
                "Campfire time must be finite and non-negative.");
        if (!float.IsFinite(range) || range < 0)
            throw new ArgumentOutOfRangeException(
                nameof(range), "Campfire range must be finite and non-negative.");

        var rangeSquared = (double)range * range;
        var offset = new Vector2(range);
        var minimum = WorldChunkKey.At(position - offset, worldLevel);
        var maximum = WorldChunkKey.At(position + offset, worldLevel);
        for (var chunkY = minimum.Y; chunkY <= maximum.Y; chunkY++)
        for (var chunkX = minimum.X; chunkX <= maximum.X; chunkX++)
        {
            var chunk = new WorldChunkKey(chunkX, chunkY, worldLevel);
            if (!_campfiresByChunk.TryGetValue(chunk, out var candidates))
                continue;
            foreach (var objectId in candidates)
                if (_objects.TryGetValue(objectId, out var value) &&
                    CampfireService.State(value.Value, gameSeconds) ==
                        CampfireState.Lit &&
                    DistanceSquared(position, value.Value) <= rangeSquared)
                    return true;
        }
        return false;
    }

    private static double DistanceSquared(
        Vector2 position, WorldGroundObject value)
    {
        var x = (double)position.X - value.X;
        var y = (double)position.Y - value.Y;
        return x * x + y * y;
    }

    private void IndexObject(ObjectState value)
    {
        if (!_objectsByChunk.TryGetValue(value.Chunk, out var objects))
        {
            objects = [];
            _objectsByChunk.Add(value.Chunk, objects);
        }
        objects.Add(value.Value.Id);
        if (!CampfireService.IsCampfire(value.Value)) return;
        if (!_campfiresByChunk.TryGetValue(value.Chunk, out var campfires))
        {
            campfires = [];
            _campfiresByChunk.Add(value.Chunk, campfires);
        }
        campfires.Add(value.Value.Id);
    }

    private void UnindexObject(ObjectState value)
    {
        if (!_objectsByChunk.TryGetValue(value.Chunk, out var objects))
            return;
        objects.Remove(value.Value.Id);
        if (objects.Count == 0)
            _objectsByChunk.Remove(value.Chunk);
        if (!CampfireService.IsCampfire(value.Value) ||
            !_campfiresByChunk.TryGetValue(value.Chunk, out var campfires))
            return;
        campfires.Remove(value.Value.Id);
        if (campfires.Count == 0)
            _campfiresByChunk.Remove(value.Chunk);
    }

    public AuthoritativeWorldTransactionsCheckpoint CaptureCheckpoint()
    {
        EnsureOwner();
        var objects = _objects.Values
            .OrderBy(static value => value.Value.Id)
            .Select(value => new AuthoritativeWorldObjectCheckpoint(
                Snapshot(value),
                WorldItemContainerService.IsContainer(value.Value.ItemId)
                    ? ContainerSnapshot(value)
                    : null))
            .ToImmutableArray();
        var chunks = _chunkRevisions
            .OrderBy(static value => value.Key.WorldLevel)
            .ThenBy(static value => value.Key.X)
            .ThenBy(static value => value.Key.Y)
            .Select(static value => new AuthoritativeChunkRevisionSnapshot(
                value.Key, value.Value))
            .ToImmutableArray();
        var cadences = _excavationCadences
            .OrderBy(static value => value.Key.ActorId.Value)
            .ThenBy(static value => value.Key.ExcavationId)
            .Select(static value => new AuthoritativeExcavationCadenceCheckpoint(
                value.Key.ActorId, value.Key.ExcavationId, value.Value))
            .ToImmutableArray();
        return new(objects, chunks, cadences);
    }

    /// <summary>
    /// Replaces an empty aggregate with trusted persisted state. Unlike
    /// AddObject, this path never increments revisions while restoring them.
    /// Validation is completed into temporary collections before committing.
    /// </summary>
    public void RestoreCheckpoint(
        AuthoritativeWorldTransactionsCheckpoint checkpoint)
    {
        EnsureOwner();
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Objects.IsDefault ||
            checkpoint.ChunkRevisions.IsDefault)
        {
            throw new InvalidDataException(
                "The world checkpoint is incomplete.");
        }
        if (_objects.Count != 0 || _objectsByChunk.Count != 0 ||
            _campfiresByChunk.Count != 0 ||
            _chunkRevisions.Count != 0 ||
            _commandResults.Count != 0 || _excavationCadences.Count != 0)
        {
            throw new InvalidOperationException(
                "World state can only be restored into an empty aggregate.");
        }

        var chunks = new Dictionary<WorldChunkKey, uint>();
        foreach (var entry in checkpoint.ChunkRevisions)
        {
            if (entry.Revision == 0 ||
                !chunks.TryAdd(entry.Chunk, entry.Revision))
            {
                throw new InvalidDataException(
                    "The world checkpoint contains an invalid chunk revision.");
            }
        }

        var objects = new Dictionary<Guid, ObjectState>();
        foreach (var entry in checkpoint.Objects)
        {
            var snapshot = entry.Object;
            if (snapshot.ObjectId == Guid.Empty ||
                string.IsNullOrWhiteSpace(snapshot.DefinitionId) ||
                !IsFinite(snapshot.Position) ||
                snapshot.ObjectRevision == 0 ||
                snapshot.ContainerRevision == 0 ||
                snapshot.Health < 0 || snapshot.MaximumHealth < 0 ||
                snapshot.MaximumHealth > 0 &&
                snapshot.Health > snapshot.MaximumHealth ||
                !double.IsFinite(snapshot.LitUntilGameSeconds) ||
                snapshot.LitUntilGameSeconds < 0 ||
                snapshot.Chunk != WorldChunkKey.At(
                    snapshot.Position, snapshot.Chunk.WorldLevel) ||
                !chunks.ContainsKey(snapshot.Chunk) ||
                snapshot.FiremakingLevel is < 1 or > 20 ||
                !ValidGateState(snapshot.DefinitionId, snapshot.GateState) ||
                snapshot.LinkedObjectId == Guid.Empty ||
                snapshot.LinkedObjectId == snapshot.ObjectId ||
                objects.ContainsKey(snapshot.ObjectId))
            {
                throw new InvalidDataException(
                    "The world checkpoint contains an invalid object.");
            }

            WorldContainerContents? contents = null;
            var isStorage = WorldItemContainerService.IsContainer(
                snapshot.DefinitionId);
            if (isStorage != snapshot.HasContainer ||
                isStorage != (entry.Container is not null))
            {
                throw new InvalidDataException(
                    "The world checkpoint container metadata is inconsistent.");
            }

            if (entry.Container is { } container)
            {
                contents = RestoreContainer(snapshot, container, chunks);
            }

            var value = new WorldGroundObject(
                snapshot.ObjectId,
                snapshot.DefinitionId,
                snapshot.Position.X,
                snapshot.Position.Y,
                snapshot.FuelItemId,
                snapshot.LitUntilGameSeconds,
                snapshot.FiremakingLevel,
                snapshot.Health,
                snapshot.MaximumHealth,
                contents,
                snapshot.OwnerId,
                snapshot.GroupOwnerId,
                snapshot.Rotation,
                ToCoreGateState(snapshot.GateState));
            if (!CropService.HasValidPersistentState(value))
                throw new InvalidDataException(
                    "The world checkpoint contains invalid crop state.");
            objects.Add(snapshot.ObjectId, new ObjectState(
                value,
                snapshot.Chunk,
                snapshot.ObjectRevision,
                snapshot.ContainerRevision,
                snapshot.LinkedObjectId));
        }

        ValidateCaveLinks(objects);
        var cadences = checkpoint.ExcavationCadences.IsDefault
            ? ImmutableArray<AuthoritativeExcavationCadenceCheckpoint>.Empty
            : checkpoint.ExcavationCadences;
        var restoredCadences = new Dictionary<
            (ActorId ActorId, Guid ExcavationId), double>();
        foreach (var value in cadences)
        {
            if (value.ActorId.Value == Guid.Empty ||
                value.ExcavationId == Guid.Empty ||
                !double.IsFinite(value.NextAllowedGameSeconds) ||
                value.NextAllowedGameSeconds < 0 ||
                !objects.TryGetValue(value.ExcavationId, out var excavation) ||
                Kind(excavation.Value.ItemId) != ExcavationKind.DigSite ||
                excavation.Chunk.WorldLevel !=
                    CaveExcavationRules.SurfaceWorldLevel ||
                !restoredCadences.TryAdd(
                    (value.ActorId, value.ExcavationId),
                    value.NextAllowedGameSeconds))
                throw new InvalidDataException(
                    "The world checkpoint contains invalid excavation cadence state.");
        }
        foreach (var value in chunks) _chunkRevisions.Add(value.Key, value.Value);
        foreach (var value in objects)
        {
            _objects.Add(value.Key, value.Value);
            IndexObject(value.Value);
        }
        foreach (var value in restoredCadences)
            _excavationCadences.Add(value.Key, value.Value);
    }

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        PickUpWorldObjectTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => PickUp(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        DropInventoryItemTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => Drop(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        PlantCropTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => PlantCrop(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        HarvestCropTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => HarvestCrop(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        OpenWorldContainerTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => OpenContainer(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        TransferWorldContainerTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => TransferContainer(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        AddCampfireFuelTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => AddFuel(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        TakeCampfireFuelTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => TakeFuel(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        LightCampfireTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => Light(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        BeginCampfireCookingTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => BeginCooking(state, command));

    /// <summary>
    /// Completes a previously persisted server-owned cooking job. Completion
    /// is driven only by the session clock, never by a second client command.
    /// </summary>
    public WorldTransactionResult CompleteCooking(
        WorldTransactionActorInput input,
        CompleteCampfireCookingTransaction command)
    {
        EnsureOwner();
        var context = new WorldTransactionContext(
            command.OperationId,
            input.ActorId,
            input.Gameplay.ActorRevision,
            input.Gameplay.Inventory.Revision);
        var actor = CreateActor(input);
        if (actor is null)
            return Rejected(context, WorldTransactionStatus.InvalidCommand);
        // Completion is cleanup for an item reserved by an earlier accepted
        // command. A dead actor may not cook or earn XP, but the authority
        // must still return/drop that reserved item.
        var fireStillLit =
            actor.Health > 0 &&
            _objects.TryGetValue(command.CampfireId, out var fire) &&
            fire.Chunk == command.CampfireChunk &&
            CampfireService.IsCampfire(fire.Value) &&
            CampfireService.State(
                fire.Value, command.GameSeconds) == CampfireState.Lit;
        var output = fireStillLit ? command.ResultItemId : command.RawItemId;
        if (!ItemCatalog.TryGet(output, out _))
            return Rejected(context, WorldTransactionStatus.InvalidItem, actor);

        var inventory = actor.Inventory.Clone();
        var objectDeltas = ImmutableArray<WorldObjectTransactionDelta>.Empty;
        var chunkDeltas = ImmutableArray<WorldChunkRevisionDelta>.Empty;
        if (!inventory.TryAddAtPreferredSlot(
                output, command.PreferredInventorySlot))
        {
            if (command.DropObjectId == Guid.Empty ||
                _objects.ContainsKey(command.DropObjectId))
                return Rejected(context, WorldTransactionStatus.InvalidCommand,
                    actor, "The cooking drop identity is invalid.");
            var dropPosition = command.CampfirePosition + new Vector2(.38f, 0);
            var chunk = WorldChunkKey.At(
                dropPosition, command.CampfireChunk.WorldLevel);
            var drop = new ObjectState(new WorldGroundObject(
                command.DropObjectId,
                output,
                dropPosition.X,
                dropPosition.Y,
                OwnerId: actor.ActorId.ToString()), chunk, 1, 1, null);
            _objects.Add(command.DropObjectId, drop);
            IndexObject(drop);
            var chunkDelta = AdvanceChunk(chunk);
            objectDeltas =
            [
                new(WorldObjectChangeKind.Added,
                    command.DropObjectId, chunk, 0, 1, Snapshot(drop))
            ];
            chunkDeltas = [chunkDelta];
        }
        else
            CommitInventory(actor, inventory);

        if (fireStillLit && command.Experience > 0)
        {
            var cooking = CookingSkill.AwardExperience(
                actor.CookingExperience, command.Experience);
            var adventure = AdventureService.AwardFromAction(
                actor.AdventureExperience, cooking.Gained);
            actor.CookingExperience = cooking.Experience;
            actor.AdventureExperience = adventure.Experience;
            AdvanceActor(actor);
        }
        return Accepted(context, actor, objectDeltas, chunkDeltas) with
        {
            Detail = fireStillLit
                ? "cooking_completed"
                : "cooking_interrupted"
        };
    }

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        PlaceConstructionTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => PlaceConstruction(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        BuildConstructionTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => BuildConstruction(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        DemolishWorldObjectTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => Demolish(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        StartExcavationTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => StartExcavation(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        WorkExcavationTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => WorkExcavation(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        RestoreExcavationTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => RestoreExcavation(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        InstallCaveRopeTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => InstallCaveRope(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        TakeCaveRopeTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => TakeCaveRope(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        FillExcavationTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => FillExcavation(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        TraverseCaveTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => TraverseCave(state, command));

    /// <summary>
    /// Resolves a session-authored command against the aggregate's longer-lived
    /// receipt history before any current-world preconditions are evaluated.
    /// The fingerprint contains only the authenticated client payload, so
    /// server-authored timestamps do not turn an exact retry into a conflict.
    /// </summary>
    internal CachedWorldTransactionResolution ResolveCached(
        WorldTransactionContext context,
        out WorldTransactionResult result)
    {
        EnsureOwner();
        result = null!;
        if (context.CommandId == Guid.Empty ||
            context.ActorId.Value == Guid.Empty ||
            string.IsNullOrWhiteSpace(context.PayloadFingerprint))
            return CachedWorldTransactionResolution.Missing;

        if (!_commandResults.TryGetValue(
                (context.ActorId, context.CommandId), out var prior))
            return CachedWorldTransactionResolution.Missing;

        if (string.Equals(
                prior.PayloadFingerprint,
                context.PayloadFingerprint,
                StringComparison.Ordinal))
        {
            result = prior.Result;
            return CachedWorldTransactionResolution.Duplicate;
        }

        result = Rejected(
            context, WorldTransactionStatus.CommandIdConflict);
        return CachedWorldTransactionResolution.Conflict;
    }

    private WorldTransactionResult ExecuteCached(
        WorldTransactionActorInput input,
        WorldTransactionContext context,
        object command,
        Func<ActorState, WorldTransactionResult> operation)
    {
        EnsureOwner();
        if (context.CommandId == Guid.Empty || context.ActorId.Value == Guid.Empty ||
            input.ActorId != context.ActorId || !IsFinite(input.Position))
            return Rejected(context, WorldTransactionStatus.InvalidCommand);
        var key = (context.ActorId, context.CommandId);
        if (_commandResults.TryGetValue(key, out var prior))
            return SameCommand(prior, context, command)
                ? prior.Result
                : Rejected(context, WorldTransactionStatus.CommandIdConflict);
        var actor = CreateActor(input);
        WorldTransactionResult result;
        if (actor is null)
            result = Rejected(context, WorldTransactionStatus.InvalidCommand);
        else if (actor.Health <= 0)
            result = Rejected(context, WorldTransactionStatus.DeadActor, actor);
        else if (context.ExpectedActorRevision != actor.ActorRevision)
            result = Rejected(
                context, WorldTransactionStatus.StaleActorRevision, actor);
        else if (context.ExpectedInventoryRevision != actor.InventoryRevision)
            result = Rejected(
                context, WorldTransactionStatus.StaleInventoryRevision, actor);
        else
            result = operation(actor);
        Remember(key, context, command, result);
        return result;
    }

    private static bool SameCommand(
        CommandReceipt prior,
        WorldTransactionContext context,
        object command)
    {
        if (prior.PayloadFingerprint is not null ||
            context.PayloadFingerprint is not null)
        {
            return string.Equals(
                prior.PayloadFingerprint,
                context.PayloadFingerprint,
                StringComparison.Ordinal);
        }

        return Equals(prior.Command, command);
    }

    private WorldTransactionResult PickUp(
        ActorState actor, PickUpWorldObjectTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Object, out var state);
        if (rejected is not null) return rejected;
        if (!CanAccess(actor, state!.Value))
            return Rejected(command.Context, WorldTransactionStatus.AccessDenied, actor);
        if (!IsPortable(state.Value))
            return Rejected(command.Context, WorldTransactionStatus.NotPortable, actor);
        if (!ItemCatalog.TryGet(state.Value.ItemId, out _))
            return Rejected(command.Context, WorldTransactionStatus.InvalidItem, actor);
        var inventory = actor.Inventory.Clone();
        if (!inventory.TryAdd(state.Value.ItemId))
            return Rejected(command.Context, WorldTransactionStatus.InventoryFull, actor);

        var oldObjectRevision = state.ObjectRevision;
        UnindexObject(state);
        _objects.Remove(state.Value.Id);
        var chunk = AdvanceChunk(state.Chunk);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor,
            [new(WorldObjectChangeKind.Removed, state.Value.Id, state.Chunk,
                oldObjectRevision, checked(oldObjectRevision + 1), null)],
            [chunk]);
    }

    private WorldTransactionResult Drop(
        ActorState actor, DropInventoryItemTransaction command)
    {
        if (command.Quantity <= 0)
            return Rejected(command.Context, WorldTransactionStatus.InvalidQuantity, actor);
        if (!IsFinite(command.Position))
            return Rejected(command.Context, WorldTransactionStatus.InvalidPlacement, actor);
        if (command.WorldLevel != actor.WorldLevel)
            return Rejected(command.Context, WorldTransactionStatus.WrongWorldLevel, actor);
        if (!InRange(actor.Position, command.Position))
            return Rejected(command.Context, WorldTransactionStatus.OutOfRange, actor);
        var chunk = WorldChunkKey.At(command.Position, command.WorldLevel);
        if (ChunkRevision(chunk) != command.ExpectedChunkRevision)
            return Rejected(command.Context, WorldTransactionStatus.StaleChunkRevision, actor);
        if ((uint)command.InventorySlot >= (uint)actor.Inventory.Capacity)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidInventorySlot, actor);
        if (actor.Inventory[command.InventorySlot] is not { } stack ||
            stack.Quantity < command.Quantity)
            return Rejected(command.Context, WorldTransactionStatus.ItemUnavailable, actor);
        if (!PlayerInventory.CanDrop(stack.ItemId))
            return Rejected(command.Context, WorldTransactionStatus.InvalidItem, actor);

        var inventory = actor.Inventory.Clone();
        if (!inventory.TryTake(command.InventorySlot, command.Quantity, out var taken))
            return Rejected(command.Context, WorldTransactionStatus.ItemUnavailable, actor);
        var additions = new List<ObjectState>(command.Quantity);
        var usedIds = new HashSet<Guid>();
        for (var index = 0; index < command.Quantity; index++)
        {
            var id = _newObjectId();
            if (id == Guid.Empty || _objects.ContainsKey(id) || !usedIds.Add(id))
                return Rejected(command.Context,
                    WorldTransactionStatus.InvalidCommand, actor,
                    "The object identity source returned a duplicate ID.");
            additions.Add(new(
                new(id, taken.ItemId, command.Position.X, command.Position.Y,
                    OwnerId: actor.ActorId.ToString()),
                chunk, 1, 1, null));
        }

        foreach (var addition in additions)
        {
            _objects.Add(addition.Value.Id, addition);
            IndexObject(addition);
        }
        var chunkDelta = AdvanceChunk(chunk);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor,
            additions.Select(value => new WorldObjectTransactionDelta(
                WorldObjectChangeKind.Added, value.Value.Id, chunk,
                0, value.ObjectRevision, Snapshot(value))).ToImmutableArray(),
            [chunkDelta]);
    }

    private WorldTransactionResult PlantCrop(
        ActorState actor, PlantCropTransaction command)
    {
        if (command.CropObjectId == Guid.Empty ||
            _objects.ContainsKey(command.CropObjectId) ||
            !ValidGameSeconds(command.GameSeconds))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidCommand, actor);
        if (!CropService.IsTileCenter(command.Position))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidPlacement, actor);
        if (command.WorldLevel != actor.WorldLevel)
            return Rejected(command.Context,
                WorldTransactionStatus.WrongWorldLevel, actor);
        if (command.WorldLevel != 0)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidPlacement, actor,
                "Crops can only be planted on the surface.");
        if (!InRange(actor.Position, command.Position))
            return Rejected(command.Context,
                WorldTransactionStatus.OutOfRange, actor);

        var chunkKey = WorldChunkKey.At(
            command.Position, command.WorldLevel);
        if (ChunkRevision(chunkKey) != command.ExpectedChunkRevision)
            return Rejected(command.Context,
                WorldTransactionStatus.StaleChunkRevision, actor);
        if (TileOccupied(command.Position, command.WorldLevel))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidPlacement, actor,
                "The planting tile is occupied.");
        if ((uint)command.SeedInventorySlot >=
            (uint)actor.Inventory.Capacity)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidInventorySlot, actor);
        if (actor.Inventory[command.SeedInventorySlot] is not { } seed)
            return Rejected(command.Context,
                WorldTransactionStatus.ItemUnavailable, actor);
        if (!CropService.TryHarvestItem(seed.ItemId, out _))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidItem, actor);
        if (actor.ActorRevision == uint.MaxValue ||
            actor.InventoryRevision == uint.MaxValue ||
            ChunkRevision(chunkKey) == uint.MaxValue)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidCommand, actor,
                "A crop revision cannot advance any further.");

        var inventory = actor.Inventory.Clone();
        if (!inventory.TryTake(
                command.SeedInventorySlot, 1, out var consumed) ||
            !string.Equals(
                consumed.ItemId, seed.ItemId, StringComparison.Ordinal))
            return Rejected(command.Context,
                WorldTransactionStatus.ItemUnavailable, actor);

        var crop = CropService.Plant(
            command.CropObjectId,
            consumed.ItemId,
            command.Position.X,
            command.Position.Y,
            command.GameSeconds,
            actor.ActorId.ToString());
        var state = new ObjectState(crop, chunkKey, 1, 1, null);
        _objects.Add(command.CropObjectId, state);
        IndexObject(state);
        var chunk = AdvanceChunk(chunkKey);
        AwardFarming(actor, FarmingSkill.PlantingExperience);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor,
            [new(WorldObjectChangeKind.Added,
                crop.Id, chunkKey, 0, state.ObjectRevision,
                Snapshot(state))],
            [chunk]) with { Detail = "crop_planted" };
    }

    private WorldTransactionResult HarvestCrop(
        ActorState actor, HarvestCropTransaction command)
    {
        var rejected = ValidateObject(
            actor, command.Context, command.Crop, out var state);
        if (rejected is not null) return rejected;
        if (!ValidGameSeconds(command.GameSeconds))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidCommand, actor);
        if (!CanAccess(actor, state!.Value))
            return Rejected(command.Context,
                WorldTransactionStatus.AccessDenied, actor);
        if (!CropService.IsCrop(state.Value))
            return Rejected(command.Context,
                WorldTransactionStatus.NotCrop, actor);
        if (!CropService.IsReady(state.Value, command.GameSeconds))
            return Rejected(command.Context,
                WorldTransactionStatus.CropNotReady, actor);
        if (state.Value.FuelItemId is not { } harvestItemId ||
            !ItemCatalog.TryGet(harvestItemId, out _))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidItem, actor);
        if (actor.ActorRevision == uint.MaxValue ||
            actor.InventoryRevision == uint.MaxValue ||
            state.ObjectRevision == uint.MaxValue ||
            ChunkRevision(state.Chunk) == uint.MaxValue)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidCommand, actor,
                "A crop revision cannot advance any further.");

        var quantity = CropService.HarvestCount(
            actor.Inventory.Count(ItemIds.GatheringBasket) > 0);
        var inventory = actor.Inventory.Clone();
        if (!inventory.TryAdd(harvestItemId, quantity))
            return Rejected(command.Context,
                WorldTransactionStatus.InventoryFull, actor);

        var previousObjectRevision = state.ObjectRevision;
        UnindexObject(state);
        _objects.Remove(state.Value.Id);
        var chunk = AdvanceChunk(state.Chunk);
        AwardFarming(
            actor, FarmingSkill.PlantingExperience * quantity);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor,
            [new(WorldObjectChangeKind.Removed,
                state.Value.Id,
                state.Chunk,
                previousObjectRevision,
                checked(previousObjectRevision + 1),
                null)],
            [chunk]) with { Detail = "crop_harvested" };
    }

    private WorldTransactionResult OpenContainer(
        ActorState actor, OpenWorldContainerTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Container, out var state);
        if (rejected is not null) return rejected;
        if (!WorldItemContainerService.IsContainer(state!.Value.ItemId))
            return Rejected(command.Context, WorldTransactionStatus.NotContainer, actor);
        if (!CanAccess(actor, state.Value))
            return Rejected(command.Context, WorldTransactionStatus.AccessDenied, actor);
        return Accepted(command.Context, actor, [], [],
            container: ContainerSnapshot(state));
    }

    private WorldTransactionResult TransferContainer(
        ActorState actor, TransferWorldContainerTransaction command)
    {
        if (command.Quantity <= 0)
            return Rejected(command.Context, WorldTransactionStatus.InvalidQuantity, actor);
        var rejected = ValidateObject(actor, command.Context,
            command.Container, out var state, requireContainerRevision: true);
        if (rejected is not null) return rejected;
        if (!WorldItemContainerService.IsContainer(state!.Value.ItemId))
            return Rejected(command.Context, WorldTransactionStatus.NotContainer, actor);
        if (!CanAccess(actor, state.Value))
            return Rejected(command.Context, WorldTransactionStatus.AccessDenied, actor);

        var inventory = actor.Inventory.Clone();
        var container = WorldItemContainerService.Open(state.Value);
        if (command.Direction == WorldContainerTransferDirection.Deposit)
        {
            if (!container.Definition.AllowsDeposit)
                return Rejected(command.Context,
                    WorldTransactionStatus.ContainerDepositDenied, actor);
            if ((uint)command.InventorySlot >= (uint)actor.Inventory.Capacity)
                return Rejected(command.Context,
                    WorldTransactionStatus.InvalidInventorySlot, actor);
            if (actor.Inventory[command.InventorySlot] is not { } available ||
                available.Quantity < command.Quantity)
                return Rejected(command.Context,
                    WorldTransactionStatus.ItemUnavailable, actor);
        }
        else if (command.Direction == WorldContainerTransferDirection.Withdraw)
        {
            if ((uint)command.ContainerSlot >=
                (uint)container.Definition.Capacity ||
                container.StackAt(command.ContainerSlot) is not { } available ||
                available.Quantity < command.Quantity)
                return Rejected(command.Context,
                    WorldTransactionStatus.ContainerItemUnavailable, actor);
        }
        else
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidCommand, actor);
        var moved = command.Direction switch
        {
            WorldContainerTransferDirection.Deposit =>
                ItemContainerTransferService.TryDeposit(
                    inventory, command.InventorySlot, container, command.Quantity),
            WorldContainerTransferDirection.Withdraw =>
                ItemContainerTransferService.TryWithdraw(
                    container, command.ContainerSlot, inventory, command.Quantity),
            _ => false
        };
        if (!moved)
        {
            var status = command.Direction switch
            {
                WorldContainerTransferDirection.Deposit
                    when (uint)command.InventorySlot >=
                         (uint)actor.Inventory.Capacity =>
                    WorldTransactionStatus.InvalidInventorySlot,
                WorldContainerTransferDirection.Deposit =>
                    WorldTransactionStatus.ContainerFull,
                WorldContainerTransferDirection.Withdraw
                    when (uint)command.ContainerSlot >=
                         (uint)container.Definition.Capacity =>
                    WorldTransactionStatus.ContainerItemUnavailable,
                WorldContainerTransferDirection.Withdraw =>
                    WorldTransactionStatus.InventoryFull,
                _ => WorldTransactionStatus.InvalidCommand
            };
            return Rejected(command.Context, status, actor);
        }

        var previous = state.ObjectRevision;
        state.Value = WorldItemContainerService.Save(state.Value, container);
        state.ObjectRevision = checked(state.ObjectRevision + 1);
        state.ContainerRevision = checked(state.ContainerRevision + 1);
        var chunk = AdvanceChunk(state.Chunk);
        CommitInventory(actor, inventory);
        if (WorldItemContainerService.IsLootBag(state.Value.ItemId) &&
            container.IsEmpty)
        {
            // A loot bag is a transient authoritative container. Its final
            // withdrawal commits the inventory change and public removal in
            // one transaction, so an empty bag cannot accumulate or be
            // reopened between two separately observable mutations.
            var empty = ContainerSnapshot(state);
            UnindexObject(state);
            _objects.Remove(state.Value.Id);
            return Accepted(command.Context, actor,
                [new WorldObjectTransactionDelta(
                    WorldObjectChangeKind.Removed,
                    state.Value.Id,
                    state.Chunk,
                    previous,
                    state.ObjectRevision,
                    null)],
                [chunk],
                empty);
        }
        return Accepted(command.Context, actor,
            [UpdatedDelta(state, previous)], [chunk], ContainerSnapshot(state));
    }

    private WorldTransactionResult AddFuel(
        ActorState actor, AddCampfireFuelTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Campfire, out var state);
        if (rejected is not null) return rejected;
        if (!CanAccess(actor, state!.Value))
            return Rejected(command.Context, WorldTransactionStatus.AccessDenied, actor);
        if (!CampfireService.IsCampfire(state.Value))
            return Rejected(command.Context, WorldTransactionStatus.NotCampfire, actor);
        if ((uint)command.InventorySlot >= (uint)actor.Inventory.Capacity)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidInventorySlot, actor);
        if (actor.Inventory[command.InventorySlot] is not { } fuel ||
            !CampfireService.CanAddFuel(
                state.Value, fuel.ItemId, command.GameSeconds))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidCampfireState, actor);
        var inventory = actor.Inventory.Clone();
        if (!inventory.TryTake(command.InventorySlot, 1, out _))
            return Rejected(command.Context, WorldTransactionStatus.ItemUnavailable, actor);
        var previous = state.ObjectRevision;
        state.Value = CampfireService.AddFuel(
            state.Value, fuel.ItemId, command.GameSeconds);
        state.ObjectRevision = checked(state.ObjectRevision + 1);
        var chunk = AdvanceChunk(state.Chunk);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor,
            [UpdatedDelta(state, previous)], [chunk]);
    }

    private WorldTransactionResult TakeFuel(
        ActorState actor, TakeCampfireFuelTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Campfire, out var state);
        if (rejected is not null) return rejected;
        if (!CanAccess(actor, state!.Value))
            return Rejected(command.Context, WorldTransactionStatus.AccessDenied, actor);
        if (!CampfireService.IsCampfire(state.Value))
            return Rejected(command.Context, WorldTransactionStatus.NotCampfire, actor);
        if (!CampfireService.CanRemoveFuel(state.Value, command.GameSeconds) ||
            string.IsNullOrWhiteSpace(state.Value.FuelItemId))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidCampfireState, actor);
        var inventory = actor.Inventory.Clone();
        if (!inventory.TryAdd(state.Value.FuelItemId))
            return Rejected(command.Context, WorldTransactionStatus.InventoryFull, actor);
        var previous = state.ObjectRevision;
        state.Value = CampfireService.RemoveFuel(state.Value, command.GameSeconds);
        state.ObjectRevision = checked(state.ObjectRevision + 1);
        var chunk = AdvanceChunk(state.Chunk);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor,
            [UpdatedDelta(state, previous)], [chunk]);
    }

    private WorldTransactionResult Light(
        ActorState actor, LightCampfireTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Campfire, out var state);
        if (rejected is not null) return rejected;
        if (!CanAccess(actor, state!.Value))
            return Rejected(command.Context, WorldTransactionStatus.AccessDenied, actor);
        if (!CampfireService.IsCampfire(state.Value))
            return Rejected(command.Context, WorldTransactionStatus.NotCampfire, actor);
        var failure = CampfireService.LightFailure(
            state.Value, actor.Inventory.ItemIds(), command.GameSeconds);
        if (failure != CampfireLightFailure.None)
            return Rejected(command.Context,
                WorldTransactionStatus.CampfireLightingRequirementsMissing,
                actor, CampfireService.LightFailureCode(failure));
        var previous = state.ObjectRevision;
        state.Value = CampfireService.Light(
            state.Value, command.GameSeconds, actor.FiremakingLevel);
        state.ObjectRevision = checked(state.ObjectRevision + 1);
        var chunk = AdvanceChunk(state.Chunk);
        return Accepted(command.Context, actor,
            [UpdatedDelta(state, previous)], [chunk]);
    }

    private WorldTransactionResult BeginCooking(
        ActorState actor, BeginCampfireCookingTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Campfire, out var state);
        if (rejected is not null) return rejected;
        if (!CanAccess(actor, state!.Value))
            return Rejected(command.Context,
                WorldTransactionStatus.AccessDenied, actor);
        if (!CampfireService.IsCampfire(state.Value))
            return Rejected(command.Context,
                WorldTransactionStatus.NotCampfire, actor);
        if (CampfireService.State(state.Value, command.GameSeconds) !=
            CampfireState.Lit)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidCampfireState, actor,
                "The campfire must be lit before cooking.");
        if ((uint)command.InventorySlot >= (uint)actor.Inventory.Capacity)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidInventorySlot, actor);
        if (actor.Inventory[command.InventorySlot] is not { } raw)
            return Rejected(command.Context,
                WorldTransactionStatus.ItemUnavailable, actor);
        if (!CookingSkill.TryProfile(raw.ItemId, out var profile))
            return Rejected(command.Context,
                WorldTransactionStatus.NotCookable, actor);
        if (CookingSkill.LevelForExperience(actor.CookingExperience) <
            profile.RequiredLevel)
            return Rejected(command.Context,
                WorldTransactionStatus.CookingLocked, actor,
                $"Cooking level {profile.RequiredLevel} is required.");

        var inventory = actor.Inventory.Clone();
        if (!inventory.TryTake(command.InventorySlot, 1, out _))
            return Rejected(command.Context,
                WorldTransactionStatus.ItemUnavailable, actor);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor, [], []);
    }

    private WorldTransactionResult PlaceConstruction(
        ActorState actor, PlaceConstructionTransaction command)
    {
        if (!IsFinite(command.Position) || command.Rotation is < 0 or > 3)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidPlacement, actor);
        if (command.WorldLevel != actor.WorldLevel)
            return Rejected(command.Context, WorldTransactionStatus.WrongWorldLevel, actor);
        if (!InRange(actor.Position, command.Position))
            return Rejected(command.Context, WorldTransactionStatus.OutOfRange, actor);
        if (!ConstructionService.IsConstructible(command.DefinitionId))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidConstruction, actor);
        var chunkKey = WorldChunkKey.At(command.Position, command.WorldLevel);
        if (ChunkRevision(chunkKey) != command.ExpectedChunkRevision)
            return Rejected(command.Context,
                WorldTransactionStatus.StaleChunkRevision, actor);
        var recipe = CraftingSkill.Recipes.FirstOrDefault(value =>
            value.ResultItemId.Equals(command.DefinitionId,
                StringComparison.OrdinalIgnoreCase));
        if (recipe is null)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidConstruction, actor);
        var consume = CraftingService.TryConsumeForPlacement(
            recipe, actor.CraftingLevel, actor.Inventory, out var inventory);
        if (consume != CraftingService.CraftResult.Success)
            return Rejected(command.Context, consume switch
            {
                CraftingService.CraftResult.Locked =>
                    WorldTransactionStatus.ConstructionLocked,
                CraftingService.CraftResult.InventoryFull =>
                    WorldTransactionStatus.InventoryFull,
                _ => WorldTransactionStatus.MissingConstructionResources
            }, actor);
        var id = _newObjectId();
        if (id == Guid.Empty || _objects.ContainsKey(id))
            return Rejected(command.Context, WorldTransactionStatus.InvalidCommand,
                actor, "The object identity source returned a duplicate ID.");
        var value = ConstructionService.Begin(new(
            id, command.DefinitionId, command.Position.X, command.Position.Y,
            OwnerId: actor.ActorId.ToString(), VisualFrame: command.Rotation));
        var state = new ObjectState(value, chunkKey, 1, 1, null);
        _objects.Add(id, state);
        IndexObject(state);
        var chunk = AdvanceChunk(chunkKey);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor,
            [new(WorldObjectChangeKind.Added, id, chunkKey, 0, 1,
                Snapshot(state))], [chunk]);
    }

    private WorldTransactionResult BuildConstruction(
        ActorState actor, BuildConstructionTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Construction, out var state);
        if (rejected is not null) return rejected;
        if (!CanAccess(actor, state!.Value))
            return Rejected(command.Context, WorldTransactionStatus.AccessDenied, actor);
        if (!ConstructionService.IsConstructionSite(state.Value))
            return Rejected(command.Context,
                WorldTransactionStatus.NotConstructionSite, actor);
        if (actor.Inventory.Count(itemId =>
                ItemCatalog.Get(itemId).HasTag(ItemTag.Hammer)) < 1)
            return Rejected(command.Context,
                WorldTransactionStatus.MissingConstructionResources, actor);
        var previous = state.ObjectRevision;
        state.Value = ConstructionService.AddWork(state.Value,
            ConstructionService.WorkHealth(actor.CraftingLevel, actor.Energy));
        state.ObjectRevision = checked(state.ObjectRevision + 1);
        actor.CraftingExperience = SkillService.AwardExperience(
            actor.CraftingExperience, 6).Experience;
        var chunk = AdvanceChunk(state.Chunk);
        AdvanceActor(actor);
        return Accepted(command.Context, actor,
            [UpdatedDelta(state, previous)], [chunk]);
    }

    private WorldTransactionResult Demolish(
        ActorState actor, DemolishWorldObjectTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Object, out var state);
        if (rejected is not null) return rejected;
        if (!CanAccess(actor, state!.Value))
            return Rejected(command.Context, WorldTransactionStatus.AccessDenied, actor);
        if (!ConstructionService.IsConstructionSite(state.Value))
            return Rejected(command.Context,
                WorldTransactionStatus.NotConstructionSite, actor);
        var refund = ConstructionService.DemolitionRefund(state.Value);
        if (refund is null)
            return Rejected(command.Context,
                WorldTransactionStatus.NoDemolitionRefund, actor);
        var inventory = actor.Inventory.Clone();
        if (!inventory.TryAdd(refund))
            return Rejected(command.Context, WorldTransactionStatus.InventoryFull, actor);
        var previous = state.ObjectRevision;
        UnindexObject(state);
        _objects.Remove(state.Value.Id);
        var chunk = AdvanceChunk(state.Chunk);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor,
            [new(WorldObjectChangeKind.Removed, state.Value.Id, state.Chunk,
                previous, checked(previous + 1), null)], [chunk]);
    }

    private WorldTransactionResult StartExcavation(
        ActorState actor, StartExcavationTransaction command)
    {
        if (_caves is null || !ValidGameSeconds(command.GameSeconds) ||
            command.WorldLevel != CaveExcavationRules.SurfaceWorldLevel ||
            command.WorldLevel != actor.WorldLevel ||
            !IsFinite(command.Position))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidExcavation, actor);
        var position = _caves.Snap(command.Position);
        if (!_caves.IsSurfaceDiggable(position))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidPlacement, actor,
                "The selected ground cannot be excavated.");
        if (!InRange(actor.Position, position))
            return Rejected(command.Context,
                WorldTransactionStatus.OutOfRange, actor);
        var chunkKey = WorldChunkKey.At(position, command.WorldLevel);
        if (ChunkRevision(chunkKey) != command.ExpectedChunkRevision)
            return Rejected(command.Context,
                WorldTransactionStatus.StaleChunkRevision, actor);
        if (_objects.Values.Any(value =>
                value.Chunk.WorldLevel == command.WorldLevel &&
                Vector2.DistanceSquared(
                    new(value.Value.X, value.Value.Y), position) < .5f))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidPlacement, actor,
                "There is already an object on that patch of ground.");
        if (!TryShovel(actor, command.ShovelInventorySlot, out _))
            return Rejected(command.Context,
                WorldTransactionStatus.MissingExcavationTool, actor,
                "The selected inventory slot does not contain a usable shovel.");
        var id = _newObjectId();
        if (id == Guid.Empty || _objects.ContainsKey(id))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidCommand, actor,
                "The object identity source returned a duplicate ID.");
        var excavation = CaveExcavationRules.Begin(
            id, position, _caves.TerrainAt(position));
        var state = NewExcavationState(excavation, chunkKey);
        _objects.Add(id, state);
        IndexObject(state);
        var chunk = AdvanceChunk(chunkKey);
        return Accepted(command.Context, actor,
            [new(WorldObjectChangeKind.Added, id, chunkKey, 0, 1,
                Snapshot(state))], [chunk]) with
        {
            Detail = "excavation_started"
        };
    }

    private WorldTransactionResult WorkExcavation(
        ActorState actor, WorkExcavationTransaction command)
    {
        if (_caves is null || !ValidGameSeconds(command.GameSeconds))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidExcavation, actor);
        var rejected = ValidateObject(actor, command.Context,
            command.Excavation, out var state);
        if (rejected is not null) return rejected;
        var target = state!;
        var excavation = Excavation(target);
        if (excavation.Kind != ExcavationKind.DigSite)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidExcavation, actor,
                "Only an unfinished excavation can be worked.");
        if (!TryShovel(actor, command.ShovelInventorySlot, out var shovel))
            return Rejected(command.Context,
                WorldTransactionStatus.MissingExcavationTool, actor,
                "The selected inventory slot does not contain a usable shovel.");
        var cadenceKey = (actor.ActorId, excavation.Id);
        if (_excavationCadences.TryGetValue(cadenceKey, out var nextAllowed) &&
            command.GameSeconds < nextAllowed)
            return Rejected(command.Context,
                WorldTransactionStatus.ExcavationCadenceLocked, actor,
                "The next excavation strike is not ready.");

        var strike = CaveExcavationRules.Strike(
            excavation, actor.DiggingExperience, shovel.DiggingPower,
            _caves.IsCaveBelow(excavation.Position));
        // Resolve every generated identity and inventory consequence before
        // mutating aggregate state. A failing identity source must be a true
        // rejection, not a partially committed excavation.
        var nextSurface = ExcavationObject(target.Value, strike.State);
        Guid? linkedObjectId = null;
        ObjectState? underground = null;
        if (strike.State.Kind == ExcavationKind.OpenShaft)
        {
            var undergroundId = _newObjectId();
            if (undergroundId == Guid.Empty ||
                undergroundId == strike.State.Id ||
                _objects.ContainsKey(undergroundId) ||
                !CaveExcavationRules.TryPortalLink(
                    strike.State, undergroundId, out var portal))
                return Rejected(command.Context,
                    WorldTransactionStatus.InvalidCommand, actor,
                    "The cave portal identity source returned a duplicate ID.");
            linkedObjectId = portal.UndergroundObjectId;
            var undergroundChunk = WorldChunkKey.At(
                portal.Position, portal.UndergroundWorldLevel);
            underground = NewExcavationState(
                strike.State with { Id = undergroundId },
                undergroundChunk,
                portal.SurfaceObjectId);
        }

        var inventory = actor.Inventory.Clone();
        var inventoryChanged = false;
        ObjectState? rewardDrop = null;
        if (strike.Completed)
        {
            var rewardItemId = _caves.TerrainAt(excavation.Position).RewardItemId;
            if (inventory.TryAdd(rewardItemId))
                inventoryChanged = true;
            else
            {
                var dropId = _newObjectId();
                if (dropId == Guid.Empty || _objects.ContainsKey(dropId) ||
                    dropId == underground?.Value.Id)
                    return Rejected(command.Context,
                        WorldTransactionStatus.InvalidCommand, actor,
                        "The excavation reward identity source returned a duplicate ID.");
                var dropPosition = excavation.Position + new Vector2(.38f, 0);
                rewardDrop = new ObjectState(new WorldGroundObject(
                    dropId, rewardItemId, dropPosition.X, dropPosition.Y,
                    OwnerId: actor.ActorId.ToString()),
                    WorldChunkKey.At(dropPosition, actor.WorldLevel), 1, 1, null);
            }
        }

        var previousSurfaceRevision = target.ObjectRevision;
        target.Value = nextSurface;
        target.LinkedObjectId = linkedObjectId;
        target.ObjectRevision = checked(target.ObjectRevision + 1);
        if (underground is not null)
        {
            _objects.Add(underground.Value.Id, underground);
            IndexObject(underground);
        }
        if (rewardDrop is not null)
        {
            _objects.Add(rewardDrop.Value.Id, rewardDrop);
            IndexObject(rewardDrop);
        }
        if (strike.Completed)
            _excavationCadences.Remove(cadenceKey);
        else
            _excavationCadences[cadenceKey] =
                command.GameSeconds + ExcavationCadenceSeconds;
        AwardDigging(actor, strike.ExperienceGained);
        if (inventoryChanged)
        {
            actor.Inventory = inventory;
            actor.InventoryRevision = checked(actor.InventoryRevision + 1);
        }
        AdvanceActor(actor);

        var objectDeltas = ImmutableArray.CreateBuilder<
            WorldObjectTransactionDelta>(strike.Completed ? 3 : 1);
        objectDeltas.Add(UpdatedDelta(target, previousSurfaceRevision));
        if (underground is not null)
        {
            objectDeltas.Add(new(WorldObjectChangeKind.Added,
                underground.Value.Id, underground.Chunk, 0, 1,
                Snapshot(underground)));
        }
        if (rewardDrop is not null)
        {
            objectDeltas.Add(new(WorldObjectChangeKind.Added,
                rewardDrop.Value.Id, rewardDrop.Chunk, 0, 1,
                Snapshot(rewardDrop)));
        }
        var chunkDeltas = objectDeltas
            .Select(static value => value.Chunk)
            .Distinct()
            .Select(AdvanceChunk)
            .ToImmutableArray();
        return Accepted(command.Context, actor, objectDeltas, chunkDeltas) with
        {
            Detail = strike.Completed
                ? strike.State.Kind == ExcavationKind.OpenShaft
                    ? "cave_discovered"
                    : "shallow_hole_completed"
                : $"excavation_strike:{strike.Damage}",
            CaveOutcome = new(strike.Damage, strike.Completed)
        };
    }

    private WorldTransactionResult RestoreExcavation(
        ActorState actor, RestoreExcavationTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Excavation, out var state);
        if (rejected is not null) return rejected;
        if (!CaveExcavationRules.CanRestore(Excavation(state!)))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidExcavation, actor);
        return RemoveExcavationPair(
            actor, command.Context, state!, consumeInventory: null,
            "excavation_restored");
    }

    private WorldTransactionResult InstallCaveRope(
        ActorState actor, InstallCaveRopeTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Shaft, out var state);
        if (rejected is not null) return rejected;
        if (state!.Chunk.WorldLevel != CaveExcavationRules.SurfaceWorldLevel ||
            !CaveExcavationRules.TryInstallRope(
                Excavation(state), out var entrance) ||
            !TryLinkedExcavation(state, out var linked))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidCaveLink, actor);
        if (!TryInventoryItem(actor, command.RopeInventorySlot,
                CaveExcavationRules.RopeItemId))
            return Rejected(command.Context,
                WorldTransactionStatus.ItemUnavailable, actor);
        var inventory = actor.Inventory.Clone();
        if (!inventory.TryTake(command.RopeInventorySlot, 1, out _))
            return Rejected(command.Context,
                WorldTransactionStatus.ItemUnavailable, actor);
        return UpdateLinkedShafts(
            actor, command.Context, state, linked, entrance,
            inventory, "cave_rope_installed");
    }

    private WorldTransactionResult TakeCaveRope(
        ActorState actor, TakeCaveRopeTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Entrance, out var state);
        if (rejected is not null) return rejected;
        if (state!.Chunk.WorldLevel != CaveExcavationRules.SurfaceWorldLevel ||
            !CaveExcavationRules.TryTakeRope(
                Excavation(state), out var openShaft) ||
            !TryLinkedExcavation(state, out var linked))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidCaveLink, actor);
        var inventory = actor.Inventory.Clone();
        if (!inventory.TryAdd(CaveExcavationRules.RopeItemId))
            return Rejected(command.Context,
                WorldTransactionStatus.InventoryFull, actor);
        return UpdateLinkedShafts(
            actor, command.Context, state, linked, openShaft,
            inventory, "cave_rope_recovered");
    }

    private WorldTransactionResult FillExcavation(
        ActorState actor, FillExcavationTransaction command)
    {
        if (_caves is null)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidExcavation, actor);
        var rejected = ValidateObject(actor, command.Context,
            command.Excavation, out var state);
        if (rejected is not null) return rejected;
        if (state!.Chunk.WorldLevel != CaveExcavationRules.SurfaceWorldLevel ||
            (uint)command.MaterialInventorySlot >=
                (uint)actor.Inventory.Capacity ||
            actor.Inventory[command.MaterialInventorySlot] is not { } material ||
            !CaveExcavationRules.CanFillWith(
                Excavation(state),
                _caves.TerrainAt(new(state.Value.X, state.Value.Y)),
                material.ItemId))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidExcavation, actor,
                "The excavation cannot be filled with that material.");
        var inventory = actor.Inventory.Clone();
        if (!inventory.TryTake(command.MaterialInventorySlot, 1, out _))
            return Rejected(command.Context,
                WorldTransactionStatus.ItemUnavailable, actor);
        return RemoveExcavationPair(
            actor, command.Context, state, inventory,
            "excavation_filled");
    }

    private WorldTransactionResult TraverseCave(
        ActorState actor, TraverseCaveTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Entrance, out var state);
        if (rejected is not null) return rejected;
        var target = state!;
        if (!TryLinkedExcavation(target, out var linked) ||
            Excavation(target).Kind != ExcavationKind.RopedEntrance ||
            Excavation(linked).Kind != ExcavationKind.RopedEntrance ||
            !CaveExcavationRules.TryDestinationLevel(
                Excavation(target), actor.WorldLevel, out var destination) ||
            destination != linked.Chunk.WorldLevel)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidCaveLink, actor,
                "Both ends of the cave entrance must be linked and roped.");
        AdvanceActor(actor);
        return Accepted(command.Context, actor, [], []) with
        {
            Detail = destination == CaveExcavationRules.UndergroundWorldLevel
                ? "entered_cave"
                : "left_cave",
            ActorTransition = new(
                new(linked.Value.X, linked.Value.Y), destination)
        };
    }

    private WorldTransactionResult? ValidateObject(
        ActorState actor,
        WorldTransactionContext context,
        WorldObjectHandle handle,
        out ObjectState? state,
        bool requireContainerRevision = false)
    {
        state = null;
        if (!_objects.TryGetValue(handle.ObjectId, out var found))
            return Rejected(context, WorldTransactionStatus.ObjectNotFound, actor);
        if (found.Chunk != handle.Chunk)
            return Rejected(context,
                WorldTransactionStatus.ObjectLocationMismatch, actor);
        if (handle.Chunk.WorldLevel != actor.WorldLevel)
            return Rejected(context, WorldTransactionStatus.WrongWorldLevel, actor);
        if (found.ObjectRevision != handle.ExpectedObjectRevision)
            return Rejected(context,
                WorldTransactionStatus.StaleObjectRevision, actor);
        if (ChunkRevision(found.Chunk) != handle.ExpectedChunkRevision)
            return Rejected(context, WorldTransactionStatus.StaleChunkRevision, actor);
        if (requireContainerRevision &&
            found.ContainerRevision != handle.ExpectedContainerRevision)
            return Rejected(context,
                WorldTransactionStatus.StaleContainerRevision, actor);
        if (!InRange(actor.Position, new(found.Value.X, found.Value.Y)))
            return Rejected(context, WorldTransactionStatus.OutOfRange, actor);
        state = found;
        return null;
    }

    private static bool IsPortable(WorldGroundObject value) =>
        !WorldItemContainerService.IsContainer(value.ItemId) &&
        !CampfireService.IsCampfire(value) &&
        !CropService.IsCrop(value) &&
        !ConstructionService.IsConstructible(value.ItemId) &&
        Kind(value.ItemId) == ExcavationKind.None;

    private bool TileOccupied(Vector2 position, int worldLevel)
    {
        var tileX = MathF.Floor(position.X);
        var tileY = MathF.Floor(position.Y);
        var chunk = WorldChunkKey.At(position, worldLevel);
        return _objectsByChunk.TryGetValue(chunk, out var candidates) &&
               candidates.Any(objectId =>
                   _objects.TryGetValue(objectId, out var value) &&
                   MathF.Floor(value.Value.X) == tileX &&
                   MathF.Floor(value.Value.Y) == tileY);
    }

    private static ExcavationKind Kind(string itemId) => itemId switch
    {
        CaveExcavationRules.DigSiteItemId => ExcavationKind.DigSite,
        CaveExcavationRules.ShallowHoleItemId => ExcavationKind.ShallowHole,
        CaveExcavationRules.OpenShaftItemId => ExcavationKind.OpenShaft,
        CaveExcavationRules.RopedEntranceItemId => ExcavationKind.RopedEntrance,
        _ => ExcavationKind.None
    };

    private static CaveExcavationState Excavation(ObjectState state) => new(
        state.Value.Id,
        Kind(state.Value.ItemId),
        new(state.Value.X, state.Value.Y),
        state.Value.Health,
        state.Value.MaxHealth);

    private static string Definition(ExcavationKind kind) => kind switch
    {
        ExcavationKind.DigSite => CaveExcavationRules.DigSiteItemId,
        ExcavationKind.ShallowHole => CaveExcavationRules.ShallowHoleItemId,
        ExcavationKind.OpenShaft => CaveExcavationRules.OpenShaftItemId,
        ExcavationKind.RopedEntrance => CaveExcavationRules.RopedEntranceItemId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static WorldGroundObject ExcavationObject(
        WorldGroundObject value, CaveExcavationState excavation) =>
        value with
        {
            ItemId = Definition(excavation.Kind),
            Health = excavation.Health,
            MaxHealth = excavation.MaximumHealth
        };

    private static ObjectState NewExcavationState(
        CaveExcavationState excavation,
        WorldChunkKey chunk,
        Guid? linkedId = null) => new(
            new WorldGroundObject(
                excavation.Id,
                Definition(excavation.Kind),
                excavation.Position.X,
                excavation.Position.Y,
                Health: excavation.Health,
                MaxHealth: excavation.MaximumHealth),
            chunk, 1, 1, linkedId);

    private bool TryLinkedExcavation(
        ObjectState state, out ObjectState linked)
    {
        linked = null!;
        if (state.LinkedObjectId is not { } linkedId ||
            !_objects.TryGetValue(linkedId, out var found))
            return false;
        linked = found;
        return linked.LinkedObjectId == state.Value.Id &&
               linked.Chunk.WorldLevel != state.Chunk.WorldLevel &&
               new Vector2(linked.Value.X, linked.Value.Y) ==
               new Vector2(state.Value.X, state.Value.Y) &&
               Kind(linked.Value.ItemId) == Kind(state.Value.ItemId);
    }

    private static bool TryInventoryItem(
        ActorState actor, int slot, string itemId) =>
        (uint)slot < (uint)actor.Inventory.Capacity &&
        actor.Inventory[slot]?.ItemId == itemId;

    private static bool TryShovel(
        ActorState actor, int slot, out ItemDefinition shovel)
    {
        shovel = null!;
        if ((uint)slot >= (uint)actor.Inventory.Capacity ||
            actor.Inventory[slot] is not { } stack)
            return false;
        shovel = ItemCatalog.Get(stack.ItemId);
        return shovel.HasTag(ItemTag.Tool) &&
               shovel.HasTag(ItemTag.Shovel) &&
               shovel.DiggingPower > 0;
    }

    private WorldTransactionResult UpdateLinkedShafts(
        ActorState actor,
        WorldTransactionContext context,
        ObjectState surface,
        ObjectState underground,
        CaveExcavationState next,
        InventoryContainer inventory,
        string detail)
    {
        var surfacePrevious = surface.ObjectRevision;
        var undergroundPrevious = underground.ObjectRevision;
        surface.Value = ExcavationObject(surface.Value, next);
        underground.Value = ExcavationObject(
            underground.Value, next with { Id = underground.Value.Id });
        surface.ObjectRevision = checked(surface.ObjectRevision + 1);
        underground.ObjectRevision = checked(underground.ObjectRevision + 1);
        var firstChunk = AdvanceChunk(surface.Chunk);
        var secondChunk = AdvanceChunk(underground.Chunk);
        CommitInventory(actor, inventory);
        return Accepted(context, actor,
            [UpdatedDelta(surface, surfacePrevious),
             UpdatedDelta(underground, undergroundPrevious)],
            [firstChunk, secondChunk]) with { Detail = detail };
    }

    private WorldTransactionResult RemoveExcavationPair(
        ActorState actor,
        WorldTransactionContext context,
        ObjectState surface,
        InventoryContainer? consumeInventory,
        string detail)
    {
        var removed = new List<ObjectState> { surface };
        if (surface.LinkedObjectId is not null)
        {
            if (!TryLinkedExcavation(surface, out var linked))
                return Rejected(context,
                    WorldTransactionStatus.InvalidCaveLink, actor);
            removed.Add(linked);
        }
        foreach (var value in removed)
        {
            UnindexObject(value);
            _objects.Remove(value.Value.Id);
            foreach (var cadence in _excavationCadences.Keys
                         .Where(key => key.ExcavationId == value.Value.Id)
                         .ToArray())
                _excavationCadences.Remove(cadence);
        }
        var chunks = removed.Select(static value => value.Chunk)
            .Distinct()
            .Select(AdvanceChunk)
            .ToArray();
        if (consumeInventory is not null)
            CommitInventory(actor, consumeInventory);
        return Accepted(context, actor,
            removed.Select(value => new WorldObjectTransactionDelta(
                WorldObjectChangeKind.Removed,
                value.Value.Id,
                value.Chunk,
                value.ObjectRevision,
                checked(value.ObjectRevision + 1),
                null)), chunks) with { Detail = detail };
    }

    private static bool ValidGameSeconds(double value) =>
        double.IsFinite(value) && value >= 0;

    private static void AwardDigging(ActorState actor, int experience)
    {
        var digging = SkillService.AwardExperience(
            actor.DiggingExperience, experience);
        var adventure = AdventureService.AwardFromAction(
            actor.AdventureExperience, digging.Gained);
        actor.DiggingExperience = digging.Experience;
        actor.AdventureExperience = adventure.Experience;
    }

    private static void AwardFarming(ActorState actor, int experience)
    {
        var farming = FarmingSkill.AwardExperience(
            actor.FarmingExperience, experience);
        var adventure = AdventureService.AwardFromAction(
            actor.AdventureExperience, farming.Gained);
        actor.FarmingExperience = farming.Experience;
        actor.AdventureExperience = adventure.Experience;
    }

    private static bool CanAccess(ActorState actor, WorldGroundObject value) =>
        string.IsNullOrWhiteSpace(value.OwnerId) &&
        string.IsNullOrWhiteSpace(value.GroupOwnerId) ||
        string.Equals(value.OwnerId, actor.ActorId.ToString(),
            StringComparison.Ordinal) ||
        !string.IsNullOrWhiteSpace(actor.GroupId) &&
        string.Equals(value.GroupOwnerId, actor.GroupId,
            StringComparison.Ordinal);

    private static bool InRange(Vector2 actor, Vector2 target) =>
        Vector2.DistanceSquared(actor, target) <=
        InteractionRange * InteractionRange;

    private WorldTransactionResult Accepted(
        WorldTransactionContext context,
        ActorState actor,
        IEnumerable<WorldObjectTransactionDelta> objects,
        IEnumerable<WorldChunkRevisionDelta> chunks,
        WorldContainerSnapshot? container = null) =>
        new(context.CommandId, WorldTransactionStatus.Accepted,
            actor.ActorRevision, actor.InventoryRevision,
            objects.ToImmutableArray(), chunks.ToImmutableArray(),
            actor.GameplaySnapshot(), container);

    private WorldTransactionResult Rejected(
        WorldTransactionContext context,
        WorldTransactionStatus status,
        ActorState? actor = null,
        string detail = "") =>
        new(context.CommandId, status,
            actor?.ActorRevision ?? 0,
            actor?.InventoryRevision ?? 0,
            [], [], actor?.GameplaySnapshot(), null, detail);

    private static WorldObjectTransactionDelta UpdatedDelta(
        ObjectState state, uint previous) => new(
            WorldObjectChangeKind.Updated, state.Value.Id, state.Chunk,
            previous, state.ObjectRevision, Snapshot(state));

    private static AuthoritativeWorldObjectSnapshot Snapshot(ObjectState state) =>
        new(state.Value.Id, state.Value.ItemId,
            new(state.Value.X, state.Value.Y), state.Chunk,
            state.ObjectRevision, state.ContainerRevision,
            state.Value.VisualFrame, state.Value.Health, state.Value.MaxHealth,
            state.Value.OwnerId, state.Value.GroupOwnerId,
            state.Value.Container is not null ||
            WorldItemContainerService.IsContainer(state.Value.ItemId),
            state.Value.FuelItemId, state.Value.LitUntilGameSeconds,
            state.Value.FiremakingLevel,
            FromCoreGateState(state.Value),
            state.LinkedObjectId);

    private static WorldContainerContents RestoreContainer(
        AuthoritativeWorldObjectSnapshot snapshot,
        WorldContainerSnapshot container,
        IReadOnlyDictionary<WorldChunkKey, uint> chunks)
    {
        var definition = WorldItemContainerService.Definition(
            snapshot.ObjectId, snapshot.DefinitionId);
        if (container.ObjectId != snapshot.ObjectId ||
            container.Chunk != snapshot.Chunk ||
            container.ChunkRevision != chunks[snapshot.Chunk] ||
            container.ObjectRevision != snapshot.ObjectRevision ||
            container.ContainerRevision != snapshot.ContainerRevision ||
            !string.Equals(container.DefinitionId, snapshot.DefinitionId,
                StringComparison.OrdinalIgnoreCase) ||
            container.AllowsDeposit != definition.AllowsDeposit ||
            container.Slots.Length != definition.Capacity)
        {
            throw new InvalidDataException(
                "The world checkpoint contains invalid container metadata.");
        }

        var items = new string?[definition.Capacity];
        var quantities = new int[definition.Capacity];
        var owners = new string?[definition.Capacity];
        var seen = new bool[definition.Capacity];
        foreach (var slot in container.Slots)
        {
            if ((uint)slot.Slot >= (uint)definition.Capacity ||
                seen[slot.Slot])
            {
                throw new InvalidDataException(
                    "The world checkpoint contains invalid container slots.");
            }

            seen[slot.Slot] = true;
            if (slot.ItemId is null && slot.Quantity == 0)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(slot.ItemId) ||
                slot.Quantity <= 0 ||
                !ItemCatalog.TryGet(slot.ItemId, out var item) ||
                slot.Quantity > 1 && !item.CanStack)
            {
                throw new InvalidDataException(
                    "The world checkpoint contains an invalid container item.");
            }

            items[slot.Slot] = slot.ItemId;
            quantities[slot.Slot] = slot.Quantity;
            owners[slot.Slot] = slot.OwnerId;
        }
        if (seen.Any(value => !value))
        {
            throw new InvalidDataException(
                "The world checkpoint container baseline is incomplete.");
        }
        return new WorldContainerContents(items, quantities, owners);
    }

    private static bool ValidGateState(
        string definitionId,
        WorldGateAccessState state) => GateCatalog.IsGate(definitionId)
        ? state != WorldGateAccessState.None &&
          Enum.IsDefined(state)
        : state == WorldGateAccessState.None;

    private static void ValidateCaveLinks(
        IReadOnlyDictionary<Guid, ObjectState> objects)
    {
        foreach (var state in objects.Values)
        {
            var kind = Kind(state.Value.ItemId);
            if (kind != ExcavationKind.None &&
                !CaveExcavationRules.IsValid(Excavation(state)))
                throw new InvalidDataException(
                    "The world checkpoint contains invalid excavation state.");
            if (kind is ExcavationKind.DigSite or ExcavationKind.ShallowHole &&
                state.Chunk.WorldLevel !=
                    CaveExcavationRules.SurfaceWorldLevel)
                throw new InvalidDataException(
                    "A surface excavation is stored on the wrong world level.");
            if (kind is ExcavationKind.OpenShaft or
                ExcavationKind.RopedEntrance &&
                state.LinkedObjectId is null)
                throw new InvalidDataException(
                    "The world checkpoint contains an unlinked cave shaft.");
            if (kind is ExcavationKind.DigSite or ExcavationKind.ShallowHole &&
                state.LinkedObjectId is not null)
                throw new InvalidDataException(
                    "The world checkpoint links a non-shaft excavation.");
            if (state.LinkedObjectId is not { } linkedId) continue;
            if (!objects.TryGetValue(linkedId, out var linked) ||
                linked.LinkedObjectId != state.Value.Id ||
                linked.Value.Id == state.Value.Id ||
                linked.Value.ItemId != state.Value.ItemId ||
                linked.Value.Health != state.Value.Health ||
                linked.Value.MaxHealth != state.Value.MaxHealth ||
                new Vector2(linked.Value.X, linked.Value.Y) !=
                new Vector2(state.Value.X, state.Value.Y) ||
                !((state.Chunk.WorldLevel ==
                       CaveExcavationRules.SurfaceWorldLevel &&
                   linked.Chunk.WorldLevel ==
                       CaveExcavationRules.UndergroundWorldLevel) ||
                  (state.Chunk.WorldLevel ==
                       CaveExcavationRules.UndergroundWorldLevel &&
                   linked.Chunk.WorldLevel ==
                       CaveExcavationRules.SurfaceWorldLevel)) ||
                !IsCaveShaft(state.Value.ItemId))
            {
                throw new InvalidDataException(
                    "The world checkpoint contains an invalid cave link.");
            }
        }
    }

    private static bool IsCaveShaft(string itemId) =>
        itemId is CaveExcavationRules.OpenShaftItemId or
            CaveExcavationRules.RopedEntranceItemId;

    private static GateAccessState ToCoreGateState(WorldGateAccessState value) =>
        value switch
        {
            WorldGateAccessState.None or WorldGateAccessState.Unlocked =>
                GateAccessState.Unlocked,
            WorldGateAccessState.Opened => GateAccessState.Opened,
            WorldGateAccessState.Locked => GateAccessState.Locked,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static WorldGateAccessState FromCoreGateState(
        WorldGroundObject value)
    {
        if (!GateCatalog.IsGate(value.ItemId))
        {
            return WorldGateAccessState.None;
        }

        return value.GateState switch
        {
            GateAccessState.Unlocked => WorldGateAccessState.Unlocked,
            GateAccessState.Opened => WorldGateAccessState.Opened,
            GateAccessState.Locked => WorldGateAccessState.Locked,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    private WorldContainerSnapshot ContainerSnapshot(ObjectState state)
    {
        var container = WorldItemContainerService.Open(state.Value);
        var items = container.Items;
        var quantities = container.Quantities;
        var owners = container.OwnerIds;
        var slots = ImmutableArray.CreateBuilder<WorldContainerSlotSnapshot>(
            container.Definition.Capacity);
        for (var slot = 0; slot < container.Definition.Capacity; slot++)
            slots.Add(new(slot, items[slot], quantities[slot], owners[slot]));
        return new(state.Value.Id, state.Chunk, ChunkRevision(state.Chunk),
            state.ObjectRevision,
            state.ContainerRevision, state.Value.ItemId,
            container.Definition.AllowsDeposit, slots.MoveToImmutable());
    }

    private WorldChunkRevisionDelta AdvanceChunk(WorldChunkKey chunk)
    {
        var previous = ChunkRevision(chunk);
        var current = checked(previous + 1);
        _chunkRevisions[chunk] = current;
        return new(chunk, previous, current);
    }

    private uint ChunkRevision(WorldChunkKey chunk) =>
        _chunkRevisions.GetValueOrDefault(chunk);

    private static void AdvanceActor(ActorState actor) =>
        actor.ActorRevision = checked(actor.ActorRevision + 1);

    private static void CommitInventory(
        ActorState actor, InventoryContainer inventory)
    {
        actor.Inventory = inventory;
        actor.InventoryRevision = checked(actor.InventoryRevision + 1);
        AdvanceActor(actor);
    }

    private static ActorState? CreateActor(WorldTransactionActorInput input)
    {
        if (input.ActorId.Value == Guid.Empty ||
            input.Gameplay.ActorRevision == 0 ||
            input.Gameplay.Inventory.Revision == 0 ||
            input.Gameplay.Health < 0 ||
            input.Gameplay.WoodcuttingExperience < 0 ||
            input.Gameplay.FarmingExperience < 0 ||
            input.Gameplay.MiningExperience < 0 ||
            input.Gameplay.AdventureExperience < 0 ||
            input.Gameplay.DiggingExperience < 0 ||
            input.Gameplay.Inventory.Capacity != PlayerInventory.Capacity)
            return null;
        var inventory = PlayerInventory.CreateContainer();
        var encounteredSlots = new bool[inventory.Capacity];
        foreach (var slot in input.Gameplay.Inventory.Slots)
        {
            if (slot.Slot < 0 || slot.Slot >= inventory.Capacity)
                return null;
            if (encounteredSlots[slot.Slot]) return null;
            encounteredSlots[slot.Slot] = true;
            if (slot.ItemId is null && slot.Quantity == 0) continue;
            if (string.IsNullOrWhiteSpace(slot.ItemId) || slot.Quantity <= 0 ||
                !inventory.TrySetSlot(slot.Slot, slot.ItemId, slot.Quantity))
                return null;
        }
        if (encounteredSlots.Any(value => !value)) return null;
        return new(input, inventory);
    }

    private void Remember(
        (ActorId ActorId, Guid CommandId) key,
        WorldTransactionContext context,
        object command,
        WorldTransactionResult result)
    {
        _commandResults.Add(key, new(
            command, context.PayloadFingerprint, result));
        _commandOrder.Enqueue(key);
        while (_commandOrder.Count > MaximumRememberedCommands)
            _commandResults.Remove(_commandOrder.Dequeue());
    }

    private void EnsureOwner()
    {
        var currentThreadId = Environment.CurrentManagedThreadId;
        _ownerThreadId ??= currentThreadId;
        if (currentThreadId != _ownerThreadId)
            throw new InvalidOperationException(
                "World transactions must execute on their owning simulation thread.");
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private sealed class ActorState
    {
        private readonly PlayerGameplaySnapshot _source;

        public ActorState(
            WorldTransactionActorInput input, InventoryContainer inventory)
        {
            _source = input.Gameplay;
            ActorId = input.ActorId;
            Position = input.Position;
            WorldLevel = input.WorldLevel;
            Health = input.Gameplay.Health;
            ActorRevision = input.Gameplay.ActorRevision;
            InventoryRevision = input.Gameplay.Inventory.Revision;
            CraftingLevel = CraftingSkill.LevelForExperience(
                input.Gameplay.CraftingExperience);
            CraftingExperience = input.Gameplay.CraftingExperience;
            CookingExperience = input.Gameplay.CookingExperience;
            FarmingExperience = input.Gameplay.FarmingExperience;
            DiggingExperience = input.Gameplay.DiggingExperience;
            AdventureExperience = input.Gameplay.AdventureExperience;
            FiremakingLevel = Math.Clamp(input.FiremakingLevel, 1, 20);
            Energy = Math.Clamp(input.Energy, 0, 100);
            GroupId = input.GroupId;
            Inventory = inventory;
        }

        public ActorId ActorId { get; }
        public Vector2 Position { get; }
        public int WorldLevel { get; }
        public int Health { get; }
        public uint ActorRevision { get; set; }
        public uint InventoryRevision { get; set; }
        public int CraftingLevel { get; }
        public int CraftingExperience { get; set; }
        public int CookingExperience { get; set; }
        public int FarmingExperience { get; set; }
        public int DiggingExperience { get; set; }
        public int AdventureExperience { get; set; }
        public int FiremakingLevel { get; }
        public float Energy { get; }
        public string? GroupId { get; }
        public InventoryContainer Inventory { get; set; }

        public PlayerGameplaySnapshot GameplaySnapshot()
        {
            var slots = ImmutableArray.CreateBuilder<InventorySlotSnapshot>(
                Inventory.Capacity);
            for (var slot = 0; slot < Inventory.Capacity; slot++)
            {
                var value = Inventory[slot];
                slots.Add(new(slot, value?.ItemId, value?.Quantity ?? 0));
            }
            return _source with
            {
                ActorRevision = ActorRevision,
                CraftingExperience = CraftingExperience,
                CookingExperience = CookingExperience,
                FarmingExperience = FarmingExperience,
                DiggingExperience = DiggingExperience,
                AdventureExperience = AdventureExperience,
                Inventory = new(InventoryRevision, slots.MoveToImmutable())
            };
        }
    }

    private sealed class ObjectState(
        WorldGroundObject value,
        WorldChunkKey chunk,
        uint objectRevision,
        uint containerRevision,
        Guid? linkedObjectId)
    {
        public WorldGroundObject Value { get; set; } = value;
        public WorldChunkKey Chunk { get; } = chunk;
        public uint ObjectRevision { get; set; } = objectRevision;
        public uint ContainerRevision { get; set; } = containerRevision;
        public Guid? LinkedObjectId { get; set; } = linkedObjectId;
    }

    private sealed record CommandReceipt(
        object Command,
        string? PayloadFingerprint,
        WorldTransactionResult Result);

    private readonly record struct AddedObjectCommit(
        AuthoritativeWorldObjectSnapshot Snapshot,
        WorldChunkRevisionDelta ChunkDelta);
}

internal enum CachedWorldTransactionResolution : byte
{
    Missing,
    Duplicate,
    Conflict
}
