using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using IslandRpg.Gameplay;
using IslandRpg.Resources;

namespace IslandRpg.Simulation;

/// <summary>
/// Single-owner authority over procedural resource mutations. The immutable
/// catalog regenerates defaults; this aggregate retains only sparse changes.
/// </summary>
public sealed class AuthoritativeResourceTransactions
{
    private readonly long _worldSeed;
    private readonly IResourceDescriptorResolver _catalog;
    private readonly AuthoritativeResourceTransactionOptions _options;
    private readonly Dictionary<ResourceNodeId, ResourceNodeSparseState>
        _nodes = [];
    private readonly Dictionary<WorldChunkKey, uint> _chunkRevisions = [];
    private readonly Dictionary<(ActorId ActorId, ResourceActionKind Action),
        CadenceState> _cadences = [];
    private int? _ownerThreadId;

    public AuthoritativeResourceTransactions(
        long worldSeed,
        IResourceDescriptorResolver catalog,
        AuthoritativeResourceTransactionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _worldSeed = worldSeed;
        _catalog = catalog;
        _options = (options ?? new AuthoritativeResourceTransactionOptions())
            .ValidatedCopy();
    }

    public ResourceTransactionResult Execute(
        WorldTransactionActorInput actor,
        GatherTreeStickTransaction command)
    {
        EnsureOwner();
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteGather(actor, command);
    }

    public ResourceTransactionResult Execute(
        WorldTransactionActorInput actor,
        StrikeTreeTransaction command)
    {
        EnsureOwner();
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteStrike(actor, command);
    }

    public ResourceTransactionResult Execute(
        WorldTransactionActorInput actor,
        GatherFibreTransaction command)
    {
        EnsureOwner();
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteFibre(actor, command);
    }

    public ResourceTransactionResult Execute(
        WorldTransactionActorInput actor,
        GatherBerriesTransaction command)
    {
        EnsureOwner();
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteBerries(actor, command);
    }

    public ResourceTransactionResult Execute(
        WorldTransactionActorInput actor,
        MineResourceTransaction command)
    {
        EnsureOwner();
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteMiningStrike(actor, command);
    }

    public uint CaptureChunkRevision(WorldChunkKey chunk)
    {
        EnsureOwner();
        return ChunkRevision(chunk);
    }

    public ResourceChunkSparseState CaptureChunk(WorldChunkKey chunk)
    {
        EnsureOwner();
        return new(
            chunk,
            ChunkRevision(chunk),
            _nodes.Values
                .Where(value => value.Chunk == chunk)
                .OrderBy(static value => value.Id.Value)
                .ToImmutableArray());
    }

    public AuthoritativeResourceTransactionsCheckpoint CaptureCheckpoint()
    {
        EnsureOwner();
        var chunks = _chunkRevisions
            .OrderBy(static value => value.Key.WorldLevel)
            .ThenBy(static value => value.Key.X)
            .ThenBy(static value => value.Key.Y)
            .Select(value => new ResourceChunkSparseState(
                value.Key,
                value.Value,
                _nodes.Values
                    .Where(node => node.Chunk == value.Key)
                    .OrderBy(static node => node.Id.Value)
                    .ToImmutableArray()))
            .ToImmutableArray();
        var cadences = _cadences
            .OrderBy(static value => value.Key.ActorId.Value)
            .ThenBy(static value => value.Key.Action)
            .Select(static value => new ResourceActorCadenceCheckpoint(
                value.Key.ActorId,
                value.Key.Action,
                value.Value.ReadyAtGameSeconds,
                value.Value.ActionOrdinal))
            .ToImmutableArray();
        return new(chunks, cadences);
    }

    public void RestoreCheckpoint(
        AuthoritativeResourceTransactionsCheckpoint checkpoint)
    {
        EnsureOwner();
        if (_nodes.Count != 0 || _chunkRevisions.Count != 0 ||
            _cadences.Count != 0)
            throw new InvalidOperationException(
                "Resources can only restore into an empty aggregate.");

        var restored = PrepareCheckpoint(checkpoint);
        foreach (var value in restored.Nodes)
            _nodes.Add(value.Key, value.Value);
        foreach (var value in restored.Chunks)
            _chunkRevisions.Add(value.Key, value.Value);
        foreach (var value in restored.Cadences)
            _cadences.Add(value.Key, value.Value);
    }

    /// <summary>
    /// Validates durable resource state without mutating the aggregate. The
    /// session uses this before committing either of its world aggregates so
    /// a malformed resource overlay cannot cause a partial restore.
    /// </summary>
    internal void ValidateCheckpoint(
        AuthoritativeResourceTransactionsCheckpoint checkpoint)
    {
        EnsureOwner();
        _ = PrepareCheckpoint(checkpoint);
    }

    private PreparedCheckpoint PrepareCheckpoint(
        AuthoritativeResourceTransactionsCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Chunks.IsDefault || checkpoint.ActorCadences.IsDefault)
            throw new InvalidDataException(
                "The resource checkpoint is incomplete.");

        var nodes = new Dictionary<ResourceNodeId,
            ResourceNodeSparseState>();
        var chunks = new Dictionary<WorldChunkKey, uint>();
        foreach (var chunk in checkpoint.Chunks)
        {
            if (chunk.ResourceChunkRevision == 0 ||
                chunk.Nodes.IsDefault ||
                chunk.Nodes.IsEmpty ||
                !chunks.TryAdd(chunk.Chunk, chunk.ResourceChunkRevision))
                throw new InvalidDataException(
                    "The resource checkpoint contains an invalid chunk.");
            ulong mutationCount = 0;
            foreach (var node in chunk.Nodes)
            {
                var reference = new ResourceNodeReference(
                    node.Id, node.Chunk, node.NodeRevision,
                    chunk.ResourceChunkRevision);
                if (node.Chunk != chunk.Chunk || node.NodeRevision == 0 ||
                    !_catalog.TryResolve(_worldSeed, reference,
                        out var descriptor) ||
                    descriptor.Kind != node.Kind ||
                    !ResourceNodeStateRules.IsValid(descriptor, node) ||
                    !nodes.TryAdd(node.Id, node))
                    throw new InvalidDataException(
                        "The resource checkpoint contains an invalid node.");
                mutationCount = checked(mutationCount + node.NodeRevision);
            }
            if (mutationCount != chunk.ResourceChunkRevision)
                throw new InvalidDataException(
                    "The resource chunk revision does not match its sparse node mutations.");
        }

        var cadences = new Dictionary<
            (ActorId ActorId, ResourceActionKind Action), CadenceState>();
        foreach (var value in checkpoint.ActorCadences)
        {
            if (value.ActorId.Value == Guid.Empty ||
                value.Action is not ResourceActionKind.CutTree and
                    not ResourceActionKind.GatherTreeStick and
                    not ResourceActionKind.GatherFibre and
                    not ResourceActionKind.GatherBerries and
                    not ResourceActionKind.Mine ||
                !double.IsFinite(value.ReadyAtGameSeconds) ||
                value.ReadyAtGameSeconds < 0 ||
                value.ActionOrdinal == 0 ||
                !cadences.TryAdd(
                    (value.ActorId, value.Action),
                    new(value.ReadyAtGameSeconds,
                        value.ActionOrdinal)))
                throw new InvalidDataException(
                    "The resource checkpoint contains invalid cadence state.");
        }

        return new(nodes, chunks, cadences);
    }

    private ResourceTransactionResult ExecuteGather(
        WorldTransactionActorInput input,
        GatherTreeStickTransaction command)
    {
        var validation = Validate(
            input, command.Context, command.Node,
            ResourceActionKind.GatherTreeStick, command.GameSeconds,
            out var descriptor, out var current, out var cadence);
        if (validation is not null) return validation;
        if (current.Remaining <= 0 || current.Depleted)
            return Rejected(input, command.Context,
                ResourceTransactionStatus.Depleted,
                "This tree has no loose sticks remaining.");

        var inventory = LoadInventory(input.Gameplay);
        if (!inventory.TryAdd(ItemIds.Sticks))
            return Rejected(input, command.Context,
                ResourceTransactionStatus.InventoryFull,
                "The carried inventory cannot hold another stick.");

        var ordinal = checked(cadence.ActionOrdinal + 1);
        var rewards = ImmutableArray.CreateBuilder<ResourceItemReward>(2);
        rewards.Add(new(ItemIds.Sticks, 1));
        if (current.Remaining == 1 &&
            SurfaceTreeCatalog.TryGetVisual(
                descriptor.Variant, out var visual))
        {
            var seedRolls = DeterministicRolls(
                input.ActorId, descriptor.Id, ordinal,
                ResourceActionKind.GatherTreeStick);
            var seedCount = seedRolls.Reward < .10f
                ? 2
                : seedRolls.Reward < .35f
                    ? 1
                    : 0;
            var added = 0;
            while (added < seedCount &&
                   inventory.TryAdd(visual.SeedItemId))
                added++;
            if (added > 0)
                rewards.Add(new(visual.SeedItemId, added));
        }

        var previous = current;
        if (!ResourceNodeStateRules.TryConsumeRemaining(
                descriptor, current, 1, command.GameSeconds,
                out current))
            throw new InvalidOperationException(
                "Validated tree-stick state could not be consumed.");
        var chunk = AdvanceChunk(descriptor.Chunk);
        _nodes[descriptor.Id] = current;
        CommitCadence(input.ActorId, ResourceActionKind.GatherTreeStick,
            command.GameSeconds,
            ordinal);
        var gameplay = UpdatedGameplay(
            input.Gameplay, inventory,
            inventoryChanged: true,
            woodcuttingExperience: input.Gameplay.WoodcuttingExperience);
        return Accepted(
            command.Context, gameplay, previous, current, chunk,
            rewards.ToImmutable());
    }

    private ResourceTransactionResult ExecuteStrike(
        WorldTransactionActorInput input,
        StrikeTreeTransaction command)
    {
        var validation = Validate(
            input, command.Context, command.Node,
            ResourceActionKind.CutTree, command.GameSeconds,
            out var descriptor, out var current, out var cadence);
        if (validation is not null) return validation;
        if (current.Health <= 0 || current.Depleted)
            return Rejected(input, command.Context,
                ResourceTransactionStatus.Depleted,
                "The tree has already been felled.");

        var inventory = LoadInventory(input.Gameplay);
        var inventoryChanged = TryAutoSharpen(
            inventory, command.ToolInventorySlot);
        var axeSlot = command.ToolInventorySlot;
        if ((uint)axeSlot >= (uint)inventory.Capacity ||
            inventory[axeSlot] is not { } selectedAxe ||
            !UsableAxe(selectedAxe.ItemId))
            return Rejected(input, command.Context,
                ResourceTransactionStatus.MissingTool,
                "The selected inventory slot does not contain a usable axe.");
        var axe = ItemCatalog.Get(selectedAxe.ItemId);
        if (!SurfaceTreeCatalog.TryGetVisual(
                descriptor.Variant, out var visual))
            return Rejected(input, command.Context,
                ResourceTransactionStatus.ResourceNotFound,
                "The tree variant is not recognized by the authority.");
        var ordinal = checked(cadence.ActionOrdinal + 1);
        var random = DeterministicRolls(
            input.ActorId, descriptor.Id, ordinal,
            ResourceActionKind.CutTree);
        var strike = ResourceStrikeService.Woodcut(
            input.Gameplay.WoodcuttingExperience,
            current.Health,
            descriptor.MaximumHealth,
            axe.WoodcuttingPower,
            random.Accuracy,
            random.Damage);

        var rolledRewardQuantity = 0;
        var grantedRewardQuantity = 0;
        if (strike.Hit)
        {
            rolledRewardQuantity = strike.Depleted
                ? WoodcuttingSkill.FellingLogCount(descriptor.MaximumHealth)
                : WoodcuttingSkill.GrantsSwingLog(
                    strike.Experience.Level, random.Reward)
                    ? 1
                    : 0;
            // Harvest capacity never rolls back a physical strike. This
            // mirrors the solo interaction: the tree, skill and tool outcome
            // commit atomically, while only wood which fits is carried.
            grantedRewardQuantity = inventory.AddUpTo(
                visual.LogItemId, rolledRewardQuantity);
        }

        var toolWorn = axe.Id == ItemIds.StoneAxe && random.Wear < .01f;
        if (toolWorn)
        {
            if (!inventory.TryReplace(axeSlot, ItemIds.BluntStoneAxe))
                throw new InvalidOperationException(
                    "Validated stone axe wear could not be committed.");
            inventoryChanged = true;
        }

        ResourceNodeTransactionDelta? nodeDelta = null;
        ResourceChunkRevisionDelta? chunkDelta = null;
        if (strike.Hit)
        {
            var previous = current;
            if (!ResourceNodeStateRules.TryApplyDamage(
                    descriptor, current, strike.Health,
                    out current))
                throw new InvalidOperationException(
                    "Validated tree damage could not be committed.");
            _nodes[descriptor.Id] = current;
            nodeDelta = new(previous, current);
            chunkDelta = AdvanceChunk(descriptor.Chunk);
        }

        CommitCadence(input.ActorId, ResourceActionKind.CutTree,
            command.GameSeconds, ordinal);
        var experienceChanged = strike.Experience.Experience !=
                                input.Gameplay.WoodcuttingExperience;
        var gameplay = UpdatedGameplay(
            input.Gameplay,
            inventory,
            inventoryChanged || grantedRewardQuantity > 0,
            strike.Experience.Experience,
            actorChanged: experienceChanged);
        var rewards = grantedRewardQuantity > 0
            ? ImmutableArray.Create(
                new ResourceItemReward(
                    visual.LogItemId, grantedRewardQuantity))
            : ImmutableArray<ResourceItemReward>.Empty;
        var overflow = rolledRewardQuantity - grantedRewardQuantity;
        return new ResourceTransactionResult(
            command.Context.CommandId,
            ResourceTransactionStatus.Accepted,
            gameplay.ActorRevision,
            gameplay.Inventory.Revision,
            gameplay,
            nodeDelta,
            chunkDelta,
            rewards,
            strike.Hit,
            strike.Damage,
            toolWorn,
            overflow > 0
                ? $"{overflow} cut wood could not be carried and was left behind."
                : string.Empty);
    }

    private ResourceTransactionResult ExecuteFibre(
        WorldTransactionActorInput input,
        GatherFibreTransaction command)
    {
        var validation = Validate(
            input, command.Context, command.Node,
            ResourceActionKind.GatherFibre, command.GameSeconds,
            out var descriptor, out var current, out var cadence,
            out var regrowth);
        if (validation is not null) return validation;
        if (!SurfaceVegetationCatalog.TryGetVisual(
                descriptor.Variant, out var visual) ||
            visual.ResourceKind != ResourceNodeKind.FibreShrub ||
            string.IsNullOrWhiteSpace(visual.GatherItemId))
            return Rejected(input, command.Context,
                ResourceTransactionStatus.ResourceNotFound,
                "The fibre shrub variant is not recognized by the authority.");
        if (current.Remaining <= 0 || current.Depleted)
            return Rejected(input, command.Context,
                ResourceTransactionStatus.Depleted,
                "This shrub needs time to regrow.");

        var inventory = LoadInventory(input.Gameplay);
        var ordinal = checked(cadence.ActionOrdinal + 1);
        var random = DeterministicRolls(
            input.ActorId, descriptor.Id, ordinal,
            ResourceActionKind.GatherFibre);
        var requested = 1 + (random.Reward < .5f ? 1 : 0) +
                        GatheringBasketBonus(inventory);
        var gathered = inventory.AddUpTo(visual.GatherItemId!, requested);
        if (gathered == 0)
            return Rejected(input, command.Context,
                ResourceTransactionStatus.InventoryFull,
                "The carried inventory cannot hold gathered fibre.");

        return CommitRenewableHarvest(
            input, command.Context, descriptor, current, regrowth,
            ResourceActionKind.GatherFibre, command.GameSeconds,
            ordinal, inventory, visual.GatherItemId!, gathered, requested,
            farmingExperience: input.Gameplay.FarmingExperience,
            adventureExperience: AwardAdventureExperience(
                input.Gameplay.AdventureExperience, gathered * 2));
    }

    private ResourceTransactionResult ExecuteBerries(
        WorldTransactionActorInput input,
        GatherBerriesTransaction command)
    {
        if (command.ToolInventorySlot < -1)
            return Rejected(input, command.Context,
                ResourceTransactionStatus.InvalidCommand,
                "The berry-gathering tool slot is malformed.");
        var validation = Validate(
            input, command.Context, command.Node,
            ResourceActionKind.GatherBerries, command.GameSeconds,
            out var descriptor, out var current, out var cadence,
            out var regrowth);
        if (validation is not null) return validation;
        if (!SurfaceVegetationCatalog.TryGetVisual(
                descriptor.Variant, out var visual) ||
            visual.ResourceKind != ResourceNodeKind.BerryBush ||
            string.IsNullOrWhiteSpace(visual.GatherItemId))
            return Rejected(input, command.Context,
                ResourceTransactionStatus.ResourceNotFound,
                "The berry bush variant is not recognized by the authority.");
        if (current.Remaining <= 0 || current.Depleted)
            return Rejected(input, command.Context,
                ResourceTransactionStatus.Depleted,
                "This bush needs time to grow more berries.");
        ItemDefinition? sickle = null;
        if (command.ToolInventorySlot != -1)
        {
            var sourceInventory = LoadInventory(input.Gameplay);
            if ((uint)command.ToolInventorySlot >=
                    (uint)sourceInventory.Capacity ||
                sourceInventory[command.ToolInventorySlot] is not
                    { } selected ||
                !UsableSickle(selected.ItemId))
                return Rejected(input, command.Context,
                    ResourceTransactionStatus.MissingTool,
                    "The selected inventory slot does not contain a usable sickle.");
            sickle = ItemCatalog.Get(selected.ItemId);
        }

        var inventory = LoadInventory(input.Gameplay);
        var ordinal = checked(cadence.ActionOrdinal + 1);
        var random = DeterministicRolls(
            input.ActorId, descriptor.Id, ordinal,
            ResourceActionKind.GatherBerries);
        var farmingLevel = FarmingSkill.LevelForExperience(
            input.Gameplay.FarmingExperience);
        var sickleBonus = sickle is not null &&
                          random.Accuracy < Math.Min(
                              .75f,
                              .35f + Math.Clamp(farmingLevel - 1, 0, 19) *
                              .01f + sickle.FarmingPower * .10f)
            ? 1
            : 0;
        var requested = 1 + (int)(random.Reward * 3) + sickleBonus +
                        GatheringBasketBonus(inventory);
        var gathered = inventory.AddUpTo(visual.GatherItemId!, requested);
        if (gathered == 0)
            return Rejected(input, command.Context,
                ResourceTransactionStatus.InventoryFull,
                "The carried inventory cannot hold gathered berries.");

        var farming = FarmingSkill.AwardExperience(
            input.Gameplay.FarmingExperience,
            checked(18 * gathered)).Experience;
        var gainedFarming = farming - input.Gameplay.FarmingExperience;
        return CommitRenewableHarvest(
            input, command.Context, descriptor, current, regrowth,
            ResourceActionKind.GatherBerries, command.GameSeconds,
            ordinal, inventory, visual.GatherItemId!, gathered, requested,
            farming,
            AwardAdventureExperience(
                input.Gameplay.AdventureExperience, gainedFarming));
    }

    private ResourceTransactionResult ExecuteMiningStrike(
        WorldTransactionActorInput input,
        MineResourceTransaction command)
    {
        var validation = Validate(
            input, command.Context, command.Node,
            ResourceActionKind.Mine, command.GameSeconds,
            out var descriptor, out var current, out var cadence);
        if (validation is not null) return validation;
        if (current.Health <= 0 || current.Depleted)
            return Rejected(input, command.Context,
                ResourceTransactionStatus.Depleted,
                "The mining node has already been depleted.");
        if (!UndergroundMiningCatalog.TryGetVisual(
                descriptor.Variant, out var visual))
            return Rejected(input, command.Context,
                ResourceTransactionStatus.ResourceNotFound,
                "The mining-node variant is not recognized by the authority.");

        var inventory = LoadInventory(input.Gameplay);
        var toolSlot = command.ToolInventorySlot;
        if ((uint)toolSlot >= (uint)inventory.Capacity ||
            inventory[toolSlot] is not { } selectedPickaxe ||
            !UsablePickaxe(selectedPickaxe.ItemId))
            return Rejected(input, command.Context,
                ResourceTransactionStatus.MissingTool,
                "The selected inventory slot does not contain a usable pickaxe.");
        var pickaxe = ItemCatalog.Get(selectedPickaxe.ItemId);
        if (visual.RewardItemId is { } rewardItemId &&
            !inventory.CanAdd(rewardItemId))
            return Rejected(input, command.Context,
                ResourceTransactionStatus.InventoryFull,
                "The carried inventory cannot hold this node's mined item.");
        var ordinal = checked(cadence.ActionOrdinal + 1);
        var random = DeterministicRolls(
            input.ActorId, descriptor.Id, ordinal,
            ResourceActionKind.Mine);
        var strike = ResourceStrikeService.Mine(
            input.Gameplay.MiningExperience,
            current.Health,
            pickaxe.MiningPower,
            visual.CompletionExperience,
            random.Accuracy,
            random.Damage);

        var grantedRewardQuantity = 0;
        if (strike.Depleted && visual.RewardItemId is { } completionReward)
            grantedRewardQuantity = inventory.AddUpTo(completionReward, 1);

        ResourceNodeTransactionDelta? nodeDelta = null;
        ResourceChunkRevisionDelta? chunkDelta = null;
        if (strike.Hit)
        {
            var previous = current;
            if (!ResourceNodeStateRules.TryApplyDamage(
                    descriptor, current, strike.Health,
                    out current))
                throw new InvalidOperationException(
                    "Validated mining damage could not be committed.");
            _nodes[descriptor.Id] = current;
            nodeDelta = new(previous, current);
            chunkDelta = AdvanceChunk(descriptor.Chunk);
        }

        CommitCadence(input.ActorId, ResourceActionKind.Mine,
            command.GameSeconds, ordinal);
        var gainedMining = strike.Experience.Experience -
                           input.Gameplay.MiningExperience;
        var adventureExperience = AwardAdventureExperience(
            input.Gameplay.AdventureExperience, gainedMining);
        var experienceChanged = gainedMining != 0 ||
                                adventureExperience !=
                                input.Gameplay.AdventureExperience;
        var inventoryChanged = grantedRewardQuantity > 0;
        var gameplay = UpdatedGameplay(
            input.Gameplay,
            inventory,
            inventoryChanged,
            input.Gameplay.WoodcuttingExperience,
            miningExperience: strike.Experience.Experience,
            adventureExperience: adventureExperience,
            actorChanged: experienceChanged);
        var rewards = grantedRewardQuantity > 0
            ? ImmutableArray.Create(new ResourceItemReward(
                visual.RewardItemId!, grantedRewardQuantity))
            : ImmutableArray<ResourceItemReward>.Empty;
        return new ResourceTransactionResult(
            command.Context.CommandId,
            ResourceTransactionStatus.Accepted,
            gameplay.ActorRevision,
            gameplay.Inventory.Revision,
            gameplay,
            nodeDelta,
            chunkDelta,
            rewards,
            strike.Hit,
            strike.Damage,
            ToolWorn: false,
            Detail: string.Empty);
    }

    private ResourceTransactionResult? Validate(
        WorldTransactionActorInput input,
        WorldTransactionContext context,
        ResourceNodeReference reference,
        ResourceActionKind action,
        double gameSeconds,
        out ResourceNodeDescriptor descriptor,
        out ResourceNodeSparseState current,
        out CadenceState cadence)
    {
        return Validate(
            input, context, reference, action, gameSeconds,
            out descriptor, out current, out cadence, out _);
    }

    private ResourceTransactionResult? Validate(
        WorldTransactionActorInput input,
        WorldTransactionContext context,
        ResourceNodeReference reference,
        ResourceActionKind action,
        double gameSeconds,
        out ResourceNodeDescriptor descriptor,
        out ResourceNodeSparseState current,
        out CadenceState cadence,
        out ResourceNodeTransactionDelta? regrowth)
    {
        descriptor = null!;
        current = null!;
        cadence = default;
        regrowth = null;
        if (context.CommandId == Guid.Empty ||
            context.ActorId != input.ActorId ||
            !double.IsFinite(gameSeconds) || gameSeconds < 0)
            return Rejected(input, context,
                ResourceTransactionStatus.InvalidCommand,
                "The resource command is malformed.");
        if (input.ActorId.Value == Guid.Empty ||
            input.Gameplay.ActorRevision == 0 ||
            input.Gameplay.Inventory.Revision == 0)
            return Rejected(input, context,
                ResourceTransactionStatus.ActorNotFound,
                "The actor gameplay state is invalid.");
        if (input.Gameplay.Health <= 0)
            return Rejected(input, context,
                ResourceTransactionStatus.DeadActor,
                "Dead actors cannot interact with resources.");
        if (context.ExpectedActorRevision != input.Gameplay.ActorRevision)
            return Rejected(input, context,
                ResourceTransactionStatus.StaleActorRevision,
                "The actor revision is stale.");
        if (context.ExpectedInventoryRevision != input.Gameplay.Inventory.Revision)
            return Rejected(input, context,
                ResourceTransactionStatus.StaleInventoryRevision,
                "The inventory revision is stale.");
        if (!_catalog.TryResolve(_worldSeed, reference, out descriptor))
            return Rejected(input, context,
                ResourceTransactionStatus.ResourceNotFound,
                "The resource identity is not present in its claimed chunk.");
        if (!ResourceNodeStateRules.ActionTargets(action, descriptor.Kind))
            return Rejected(input, context,
                ResourceTransactionStatus.WrongResourceKind,
                "This action is not valid for the referenced resource.");
        current = EffectiveState(descriptor);
        if (reference.ExpectedNodeRevision != current.NodeRevision)
            return Rejected(input, context,
                ResourceTransactionStatus.StaleNodeRevision,
                "The resource node revision is stale.");
        if (reference.ExpectedResourceChunkRevision !=
            ChunkRevision(descriptor.Chunk))
            return Rejected(input, context,
                ResourceTransactionStatus.StaleResourceChunkRevision,
                "The resource chunk revision is stale.");
        if (input.WorldLevel != descriptor.Chunk.WorldLevel)
            return Rejected(input, context,
                ResourceTransactionStatus.WrongWorldLevel,
                "The actor and resource are on different world levels.");
        if (!IsFinite(input.Position) || Vector2.DistanceSquared(
                input.Position, descriptor.Position) >
            _options.InteractionRange * _options.InteractionRange)
            return Rejected(input, context,
                ResourceTransactionStatus.OutOfRange,
                "The resource is outside interaction range.");
        cadence = _cadences.GetValueOrDefault((input.ActorId, action));
        var policy = Cadence(action);
        if (!policy.IsReady(gameSeconds, cadence.ReadyAtGameSeconds))
            return Rejected(input, context,
                ResourceTransactionStatus.CadenceLocked,
                "The resource interaction cadence has not elapsed.");

        // A due regrowth is prepared here but remains uncommitted until every
        // action-specific tool and inventory check succeeds. The accepted
        // harvest atomically publishes both logical revisions, avoiding a
        // hidden mutation when a later validation rejects the command.
        if (ResourceNodeStateRules.TryRegrow(
                descriptor, current, gameSeconds, out var regrown))
        {
            regrowth = new(current, regrown);
            current = regrown;
        }
        return null;
    }

    private ResourceNodeSparseState EffectiveState(
        ResourceNodeDescriptor descriptor) =>
        _nodes.GetValueOrDefault(descriptor.Id) ??
        ResourceNodeStateRules.CreateDefault(descriptor);

    private void CommitCadence(
        ActorId actorId,
        ResourceActionKind action,
        double gameSeconds,
        ulong actionOrdinal)
    {
        _cadences[(actorId, action)] = new(
            Cadence(action).NextReadyAt(gameSeconds), actionOrdinal);
    }

    private ResourceActionCadence Cadence(ResourceActionKind action) =>
        action switch
        {
            ResourceActionKind.GatherTreeStick =>
                _options.GatherTreeStickCadence,
            ResourceActionKind.CutTree => _options.StrikeTreeCadence,
            ResourceActionKind.GatherFibre => _options.GatherFibreCadence,
            ResourceActionKind.GatherBerries =>
                _options.GatherBerriesCadence,
            ResourceActionKind.Mine => _options.MineCadence,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

    private ResourceTransactionResult CommitRenewableHarvest(
        WorldTransactionActorInput input,
        WorldTransactionContext context,
        ResourceNodeDescriptor descriptor,
        ResourceNodeSparseState current,
        ResourceNodeTransactionDelta? regrowth,
        ResourceActionKind action,
        double gameSeconds,
        ulong ordinal,
        InventoryContainer inventory,
        string itemId,
        int gathered,
        int requested,
        int farmingExperience,
        int adventureExperience)
    {
        var previous = regrowth?.Previous ?? current;
        if (!ResourceNodeStateRules.TryConsumeRemaining(
                descriptor, current, current.Remaining, gameSeconds,
                out current))
            throw new InvalidOperationException(
                "Validated renewable resource state could not be harvested.");
        _nodes[descriptor.Id] = current;
        var firstChunk = AdvanceChunk(descriptor.Chunk);
        var chunk = regrowth is null
            ? firstChunk
            : new ResourceChunkRevisionDelta(
                descriptor.Chunk,
                firstChunk.PreviousRevision,
                AdvanceChunk(descriptor.Chunk).CurrentRevision);
        CommitCadence(input.ActorId, action, gameSeconds, ordinal);
        var gameplay = UpdatedGameplay(
            input.Gameplay, inventory,
            inventoryChanged: true,
            woodcuttingExperience: input.Gameplay.WoodcuttingExperience,
            farmingExperience,
            adventureExperience,
            actorChanged:
                farmingExperience != input.Gameplay.FarmingExperience ||
                adventureExperience != input.Gameplay.AdventureExperience);
        var detail = gathered < requested
            ? $"{requested - gathered} gathered items could not be carried and were left behind."
            : string.Empty;
        if (regrowth is not null)
            detail = string.IsNullOrEmpty(detail)
                ? "The resource regrew before this harvest."
                : $"The resource regrew before this harvest. {detail}";
        return new ResourceTransactionResult(
            context.CommandId,
            ResourceTransactionStatus.Accepted,
            gameplay.ActorRevision,
            gameplay.Inventory.Revision,
            gameplay,
            new(previous, current),
            chunk,
            [new(itemId, gathered)],
            Detail: detail);
    }

    private ResourceChunkRevisionDelta AdvanceChunk(WorldChunkKey chunk)
    {
        var previous = ChunkRevision(chunk);
        var current = checked(previous + 1);
        _chunkRevisions[chunk] = current;
        return new(chunk, previous, current);
    }

    private uint ChunkRevision(WorldChunkKey chunk) =>
        _chunkRevisions.GetValueOrDefault(chunk);

    private static bool TryAutoSharpen(
        InventoryContainer inventory,
        int toolSlot)
    {
        if ((uint)toolSlot >= (uint)inventory.Capacity ||
            inventory[toolSlot]?.ItemId != ItemIds.BluntStoneAxe)
            return false;
        var rocks = FindSlot(inventory, ItemIds.SmallRocks);
        return rocks >= 0 &&
               ToolUpkeepService.TrySharpenStoneTool(
                   inventory, rocks, toolSlot, out var updated) &&
               CommitCandidate(inventory, updated);
    }

    private static bool CommitCandidate(
        InventoryContainer inventory,
        InventoryContainer candidate)
    {
        inventory.CopyFrom(candidate);
        return true;
    }

    private static bool UsableAxe(string itemId)
    {
        var item = ItemCatalog.Get(itemId);
        return item.HasTag(ItemTag.Tool) && item.HasTag(ItemTag.Axe) &&
               item.WoodcuttingPower > 0;
    }

    private static bool UsableSickle(string itemId)
    {
        var item = ItemCatalog.Get(itemId);
        return item.HasTag(ItemTag.Tool) && item.HasTag(ItemTag.Sickle) &&
               item.FarmingPower > 0;
    }

    private static bool UsablePickaxe(string itemId)
    {
        var item = ItemCatalog.Get(itemId);
        return item.HasTag(ItemTag.Tool) && item.HasTag(ItemTag.Pickaxe) &&
               item.MiningPower > 0;
    }

    private static int GatheringBasketBonus(InventoryContainer inventory) =>
        FindSlot(inventory, ItemIds.GatheringBasket) >= 0 ? 1 : 0;

    private static int AwardAdventureExperience(
        int currentExperience,
        int actionExperience) => AdventureService.AwardFromAction(
            currentExperience, actionExperience).Experience;

    private static int FindSlot(
        InventoryContainer inventory,
        string itemId)
    {
        for (var slot = 0; slot < inventory.Capacity; slot++)
            if (inventory[slot]?.ItemId == itemId) return slot;
        return -1;
    }

    private static InventoryContainer LoadInventory(
        PlayerGameplaySnapshot gameplay)
    {
        var inventory = PlayerInventory.CreateContainer();
        var seen = new bool[inventory.Capacity];
        foreach (var value in gameplay.Inventory.Slots)
        {
            if ((uint)value.Slot >= (uint)inventory.Capacity ||
                seen[value.Slot])
                throw new InvalidOperationException(
                    "The actor inventory snapshot is invalid.");
            seen[value.Slot] = true;
            if (value.ItemId is null && value.Quantity == 0) continue;
            if (string.IsNullOrWhiteSpace(value.ItemId) || value.Quantity <= 0 ||
                !inventory.TrySetSlot(
                    value.Slot, value.ItemId, value.Quantity))
                throw new InvalidOperationException(
                    "The actor inventory snapshot is invalid.");
        }
        if (seen.Any(static value => !value))
            throw new InvalidOperationException(
                "The actor inventory snapshot is incomplete.");
        return inventory;
    }

    private static PlayerGameplaySnapshot UpdatedGameplay(
        PlayerGameplaySnapshot source,
        InventoryContainer inventory,
        bool inventoryChanged,
        int woodcuttingExperience,
        int? farmingExperience = null,
        int? adventureExperience = null,
        int? miningExperience = null,
        bool actorChanged = false)
    {
        var slots = ImmutableArray.CreateBuilder<InventorySlotSnapshot>(
            inventory.Capacity);
        for (var slot = 0; slot < inventory.Capacity; slot++)
        {
            var value = inventory[slot];
            slots.Add(new(slot, value?.ItemId, value?.Quantity ?? 0));
        }
        return source with
        {
            ActorRevision = inventoryChanged || actorChanged
                ? checked(source.ActorRevision + 1)
                : source.ActorRevision,
            Inventory = new(
                inventoryChanged
                    ? checked(source.Inventory.Revision + 1)
                    : source.Inventory.Revision,
                slots.MoveToImmutable()),
            WoodcuttingExperience = woodcuttingExperience,
            FarmingExperience = farmingExperience ??
                                source.FarmingExperience,
            MiningExperience = miningExperience ??
                               source.MiningExperience,
            AdventureExperience = adventureExperience ??
                                  source.AdventureExperience
        };
    }

    private static ResourceTransactionResult Accepted(
        WorldTransactionContext context,
        PlayerGameplaySnapshot gameplay,
        ResourceNodeSparseState previous,
        ResourceNodeSparseState current,
        ResourceChunkRevisionDelta chunk,
        ImmutableArray<ResourceItemReward> rewards) => new(
            context.CommandId,
            ResourceTransactionStatus.Accepted,
            gameplay.ActorRevision,
            gameplay.Inventory.Revision,
            gameplay,
            new(previous, current),
            chunk,
            rewards);

    private static ResourceTransactionResult Rejected(
        WorldTransactionActorInput input,
        WorldTransactionContext context,
        ResourceTransactionStatus status,
        string detail) => new(
            context.CommandId,
            status,
            input.Gameplay.ActorRevision,
            input.Gameplay.Inventory.Revision,
            input.Gameplay,
            null,
            null,
            ImmutableArray<ResourceItemReward>.Empty,
            Detail: detail);

    private DeterministicResourceRolls DeterministicRolls(
        ActorId actorId,
        ResourceNodeId nodeId,
        ulong ordinal,
        ResourceActionKind action)
    {
        Span<byte> input = stackalloc byte[8 + 16 + 16 + 8 + 1];
        BinaryPrimitives.WriteInt64BigEndian(input, _worldSeed);
        actorId.Value.TryWriteBytes(input[8..24], bigEndian: true, out _);
        nodeId.Value.TryWriteBytes(input[24..40], bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64BigEndian(input[40..48], ordinal);
        input[48] = (byte)action;
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        return new(
            UnitRoll(digest[0..4]),
            UnitRoll(digest[4..8]),
            UnitRoll(digest[8..12]),
            UnitRoll(digest[12..16]));
    }

    private static float UnitRoll(ReadOnlySpan<byte> value) =>
        (float)(BinaryPrimitives.ReadUInt32BigEndian(value) / 4294967296d);

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private void EnsureOwner()
    {
        var threadId = Environment.CurrentManagedThreadId;
        _ownerThreadId ??= threadId;
        if (_ownerThreadId != threadId)
            throw new InvalidOperationException(
                "Resource transactions must execute on their owning simulation thread.");
    }

    private readonly record struct CadenceState(
        double ReadyAtGameSeconds,
        ulong ActionOrdinal);

    private sealed record PreparedCheckpoint(
        Dictionary<ResourceNodeId, ResourceNodeSparseState> Nodes,
        Dictionary<WorldChunkKey, uint> Chunks,
        Dictionary<(ActorId ActorId, ResourceActionKind Action), CadenceState>
            Cadences);

    private readonly record struct DeterministicResourceRolls(
        float Accuracy,
        float Damage,
        float Reward,
        float Wear);
}
