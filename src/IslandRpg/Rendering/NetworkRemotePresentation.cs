using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Simulation;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

/// <summary>
/// Applies one remote actor snapshot. GameHostWindow and WorldChecks both
/// call this so a finished one-shot Idle publish actually stops Gather.
/// </summary>
internal static class NetworkRemotePresentation
{
    public static void Apply(
        WorldEntity entity,
        Vector2 position,
        Vector2 velocity,
        NetworkEntityState state,
        byte animationState,
        float elapsed)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (state.HasFlag(NetworkEntityState.Dead))
        {
            entity.Die();
            entity.CorrectPosition(position);
            entity.AdvanceAction(elapsed);
            return;
        }

        var moving = velocity.LengthSquared > .0001f ||
                     state.HasFlag(NetworkEntityState.Moving);
        var skill = ActorSkillStance.UnpackAction(animationState);
        var generation = ActorSkillStance.UnpackGeneration(animationState);
        if (!moving &&
            (state.HasFlag(NetworkEntityState.Interacting) ||
             ActorSkillStance.IsPublished(skill)))
        {
            entity.CorrectPosition(position);
            entity.PresentSkill(skill, generation, position + entity.Facing);
            entity.AdvanceAction(elapsed);
            return;
        }

        if (!moving && entity.Action != EntityAction.Idle &&
            entity.Action != EntityAction.Move)
            entity.Stop();

        entity.PresentRemoteWalk(position, velocity, moving, elapsed);
    }
}