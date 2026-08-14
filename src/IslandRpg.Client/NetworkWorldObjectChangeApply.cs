using IslandRpg.Protocol;

namespace IslandRpg.Client;

/// <summary>
/// Presentation-side dispatcher for one budgeted world-object slice. The
/// window used to receive these records from <c>WorldObjectsChanged</c>; the
/// poll path now feeds the same records here so known-id tracking, container
/// close, cave observe, and construction chaining stay on the real path.
/// </summary>
public sealed class NetworkWorldObjectChangeApply
{
    public HashSet<Guid> KnownObjectIds { get; } = [];

    public void Apply(
        IReadOnlyList<NetworkWorldObjectChange> changes,
        INetworkWorldObjectChangeObserver observer)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(observer);
        foreach (var change in changes)
        {
            KnownObjectIds.Add(change.ObjectId);
            if (change.Kind == WorldObjectDeltaKind.Remove)
            {
                observer.OnRemoved(change);
                continue;
            }
            if (change.State is not { } state) continue;
            observer.OnUpserted(change, state);
        }
        observer.OnSliceApplied(changes);
    }

    public void Reset() => KnownObjectIds.Clear();
}

public interface INetworkWorldObjectChangeObserver
{
    void OnRemoved(NetworkWorldObjectChange change);
    void OnUpserted(
        NetworkWorldObjectChange change,
        NetworkWorldObjectState state);
    void OnSliceApplied(IReadOnlyList<NetworkWorldObjectChange> changes);
}