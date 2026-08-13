namespace IslandRpg.Resources;

/// <summary>
/// Identifies the descriptor field which determines whether a procedural
/// resource is depleted. Keeping this policy beside the shared contracts
/// prevents protocol, persistence and simulation code from interpreting the
/// same sparse state differently.
/// </summary>
public enum ResourceNodeDepletionBasis : byte
{
    Health = 1,
    Remaining = 2
}

/// <summary>
/// Canonical lifecycle rules for procedural resource defaults and sparse
/// overlays. Health resources are permanently damaged until destroyed;
/// remaining resources may become temporarily depleted and regenerate.
/// </summary>
public static class ResourceNodeStateRules
{
    public static ResourceNodeDepletionBasis DepletionBasis(
        ResourceNodeKind kind) => kind switch
    {
        ResourceNodeKind.Tree or ResourceNodeKind.MiningNode =>
            ResourceNodeDepletionBasis.Health,
        ResourceNodeKind.FibreShrub or ResourceNodeKind.BerryBush or
            ResourceNodeKind.FishSchool =>
            ResourceNodeDepletionBasis.Remaining,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static bool ActionTargets(
        ResourceActionKind action,
        ResourceNodeKind kind) => action switch
    {
        ResourceActionKind.CutTree or
            ResourceActionKind.GatherTreeStick =>
            kind == ResourceNodeKind.Tree,
        ResourceActionKind.GatherFibre =>
            kind == ResourceNodeKind.FibreShrub,
        ResourceActionKind.GatherBerries =>
            kind == ResourceNodeKind.BerryBush,
        ResourceActionKind.Mine =>
            kind == ResourceNodeKind.MiningNode,
        ResourceActionKind.Fish =>
            kind == ResourceNodeKind.FishSchool,
        _ => false
    };

    /// <summary>
    /// Validates the lifecycle portion of deterministic generator output.
    /// World bounds and catalog-specific numerical ceilings remain the
    /// responsibility of <see cref="ProceduralResourceCatalog"/>.
    /// </summary>
    public static bool AreValidDefaults(
        ResourceNodeKind kind,
        int initialHealth,
        int maximumHealth,
        int initialRemaining,
        double regrowthGameSeconds)
    {
        if (initialHealth < 0 || maximumHealth < 0 ||
            initialHealth > maximumHealth || initialRemaining < 0 ||
            !double.IsFinite(regrowthGameSeconds) ||
            regrowthGameSeconds < 0)
            return false;

        return kind switch
        {
            // Loose sticks are an independent, bounded secondary harvest.
            ResourceNodeKind.Tree =>
                initialHealth > 0 && maximumHealth > 0 &&
                regrowthGameSeconds == 0,
            ResourceNodeKind.MiningNode =>
                initialHealth > 0 && maximumHealth > 0 &&
                initialRemaining == 0 && regrowthGameSeconds == 0,
            ResourceNodeKind.FibreShrub or ResourceNodeKind.BerryBush =>
                initialHealth == 0 && maximumHealth == 0 &&
                initialRemaining > 0 && regrowthGameSeconds > 0,
            // Fish schools use remaining stock. A zero regrowth interval is
            // valid for a deliberately non-renewable school.
            ResourceNodeKind.FishSchool =>
                initialHealth == 0 && maximumHealth == 0 &&
                initialRemaining > 0,
            _ => false
        };
    }

    public static ResourceNodeSparseState CreateDefault(
        ResourceNodeDescriptor descriptor,
        uint nodeRevision = 0)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Id.IsEmpty || !AreValidDefaults(
                descriptor.Kind,
                descriptor.InitialHealth,
                descriptor.MaximumHealth,
                descriptor.InitialRemaining,
                descriptor.RegrowthGameSeconds))
        {
            throw new ArgumentException(
                "The resource descriptor has invalid lifecycle defaults.",
                nameof(descriptor));
        }

        return new ResourceNodeSparseState(
            descriptor.Id,
            descriptor.Kind,
            descriptor.Chunk,
            nodeRevision,
            descriptor.InitialHealth,
            descriptor.InitialRemaining,
            ReadyAtGameSeconds: 0,
            Depleted: false);
    }

    /// <summary>
    /// Descriptor-independent validation suitable for hostile serialized or
    /// wire data. Exact bounds and renewable deadlines require the canonical
    /// descriptor and are checked by <see cref="IsValid"/>.
    /// </summary>
    public static bool IsShapeValid(ResourceNodeSparseState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Id.IsEmpty || state.Health < 0 || state.Remaining < 0 ||
            !double.IsFinite(state.ReadyAtGameSeconds) ||
            state.ReadyAtGameSeconds < 0)
            return false;

        return state.Kind switch
        {
            ResourceNodeKind.Tree =>
                state.ReadyAtGameSeconds == 0 &&
                state.Depleted == (state.Health == 0),
            ResourceNodeKind.MiningNode =>
                state.Remaining == 0 && state.ReadyAtGameSeconds == 0 &&
                state.Depleted == (state.Health == 0),
            ResourceNodeKind.FibreShrub or ResourceNodeKind.BerryBush or
                ResourceNodeKind.FishSchool =>
                state.Health == 0 &&
                state.Depleted == (state.Remaining == 0) &&
                (state.Depleted || state.ReadyAtGameSeconds == 0),
            _ => false
        };
    }

    public static bool IsValid(
        ResourceNodeDescriptor descriptor,
        ResourceNodeSparseState state)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(state);
        if (!AreValidDefaults(
                descriptor.Kind,
                descriptor.InitialHealth,
                descriptor.MaximumHealth,
                descriptor.InitialRemaining,
                descriptor.RegrowthGameSeconds) ||
            !IsShapeValid(state) ||
            state.Id != descriptor.Id || state.Kind != descriptor.Kind ||
            state.Chunk != descriptor.Chunk ||
            state.Health > descriptor.MaximumHealth ||
            state.Remaining > descriptor.InitialRemaining)
            return false;

        if (!state.Depleted) return state.ReadyAtGameSeconds == 0;
        return descriptor.RegrowthGameSeconds > 0
            ? state.ReadyAtGameSeconds > 0
            : state.ReadyAtGameSeconds == 0;
    }

    public static bool IsRegrowthReady(
        ResourceNodeDescriptor descriptor,
        ResourceNodeSparseState state,
        double gameSeconds) =>
        double.IsFinite(gameSeconds) && gameSeconds >= 0 &&
        IsValid(descriptor, state) && state.Depleted &&
        descriptor.RegrowthGameSeconds > 0 &&
        state.ReadyAtGameSeconds > 0 &&
        gameSeconds >= state.ReadyAtGameSeconds;

    /// <summary>
    /// Applies damage to a health-backed node. The method rejects healing,
    /// no-op damage and remaining-backed resources so callers cannot produce
    /// a sparse revision which disagrees with the descriptor lifecycle.
    /// </summary>
    public static bool TryApplyDamage(
        ResourceNodeDescriptor descriptor,
        ResourceNodeSparseState state,
        int resultingHealth,
        out ResourceNodeSparseState damaged)
    {
        damaged = state;
        if (!IsValid(descriptor, state) || state.Depleted ||
            DepletionBasis(descriptor.Kind) !=
                ResourceNodeDepletionBasis.Health ||
            resultingHealth < 0 || resultingHealth >= state.Health ||
            state.NodeRevision == uint.MaxValue)
            return false;

        damaged = state with
        {
            NodeRevision = state.NodeRevision + 1,
            Health = resultingHealth,
            Depleted = resultingHealth == 0
        };
        return true;
    }

    /// <summary>
    /// Consumes deterministic stock from a node. Tree sticks are secondary
    /// stock and never fell the tree; remaining-backed resources enter their
    /// depleted/regrowth phase when the final unit is taken.
    /// </summary>
    public static bool TryConsumeRemaining(
        ResourceNodeDescriptor descriptor,
        ResourceNodeSparseState state,
        int quantity,
        double gameSeconds,
        out ResourceNodeSparseState consumed)
    {
        consumed = state;
        if (quantity <= 0 || quantity > state.Remaining ||
            !double.IsFinite(gameSeconds) || gameSeconds < 0 ||
            !IsValid(descriptor, state) || state.Depleted ||
            state.NodeRevision == uint.MaxValue)
            return false;

        var remaining = state.Remaining - quantity;
        var depleted = DepletionBasis(descriptor.Kind) ==
                           ResourceNodeDepletionBasis.Remaining &&
                       remaining == 0;
        var readyAt = depleted && descriptor.RegrowthGameSeconds > 0
            ? RegrowthDeadline(descriptor, gameSeconds)
            : 0;
        consumed = state with
        {
            NodeRevision = state.NodeRevision + 1,
            Remaining = remaining,
            ReadyAtGameSeconds = readyAt,
            Depleted = depleted
        };
        return true;
    }

    /// <summary>
    /// Materializes one due regeneration as a new sparse revision. The
    /// owning aggregate remains responsible for advancing the containing
    /// resource-chunk revision and publishing the resulting delta.
    /// </summary>
    public static bool TryRegrow(
        ResourceNodeDescriptor descriptor,
        ResourceNodeSparseState state,
        double gameSeconds,
        out ResourceNodeSparseState regrown)
    {
        regrown = state;
        if (!IsRegrowthReady(descriptor, state, gameSeconds) ||
            state.NodeRevision == uint.MaxValue)
            return false;
        regrown = CreateDefault(descriptor, state.NodeRevision + 1);
        return true;
    }

    public static double RegrowthDeadline(
        ResourceNodeDescriptor descriptor,
        double gameSeconds)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!double.IsFinite(gameSeconds) || gameSeconds < 0 ||
            descriptor.RegrowthGameSeconds <= 0 ||
            !double.IsFinite(descriptor.RegrowthGameSeconds))
            throw new ArgumentOutOfRangeException(nameof(gameSeconds));
        var result = gameSeconds + descriptor.RegrowthGameSeconds;
        if (!double.IsFinite(result) || result <= gameSeconds)
            throw new ArgumentOutOfRangeException(nameof(gameSeconds));
        return result;
    }
}
