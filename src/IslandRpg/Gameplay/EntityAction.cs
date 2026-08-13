namespace IslandRpg.Gameplay;

/// <summary>
/// Stable actor action categories shared by headless gameplay rules and the
/// presentation layer. Keep this enum free of renderer-specific state.
/// </summary>
internal enum EntityAction
{
    Idle,
    Move,
    Attack,
    Work,
    Build,
    Gather,
    Dig,
    Mine,
    Fish,
    Hurt,
    Die
}
