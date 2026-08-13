using IslandRpg.Simulation;

namespace IslandRpg.Resources;

/// <summary>
/// Combines independent deterministic resource families into one bounded
/// catalog input. Identity derivation and duplicate rejection remain in
/// <see cref="ProceduralResourceCatalog"/>, so adding a resource family does
/// not create a second authority or lookup path.
/// </summary>
public sealed class CompositeResourceDescriptorSource :
    IProceduralResourceDescriptorSource
{
    private readonly IProceduralResourceDescriptorSource[] _sources;

    public CompositeResourceDescriptorSource(
        params IProceduralResourceDescriptorSource[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Length == 0 || sources.Any(static value => value is null))
            throw new ArgumentException(
                "At least one non-null resource source is required.",
                nameof(sources));
        _sources = [.. sources];
    }

    public IReadOnlyList<ProceduralResourceSeed> DescribeChunk(
        long worldSeed,
        WorldChunkKey chunk)
    {
        var result = new List<ProceduralResourceSeed>();
        foreach (var source in _sources)
        {
            var values = source.DescribeChunk(worldSeed, chunk) ??
                         throw new InvalidOperationException(
                             "A procedural resource source returned null.");
            result.AddRange(values);
        }
        return result;
    }
}
