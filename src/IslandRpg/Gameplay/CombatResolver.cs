namespace IslandRpg.Gameplay;

/// <summary>
/// Identifies one accepted attack roll. The attack sequence must advance only
/// when the authority accepts an attack, rather than on a rejected command or
/// a render frame, so replay and checkpoint recovery produce the same result.
/// </summary>
public readonly record struct CombatRollKey(
    long WorldSeed,
    Guid AttackerId,
    Guid TargetId,
    ulong AttackSequence);

/// <summary>Unit-interval random values consumed by the melee rules.</summary>
public readonly record struct CombatRandomRoll(
    float HitRoll,
    float DamageRoll);

/// <summary>Combat skill experience before or after an attack.</summary>
public readonly record struct CombatProgression(
    int AttackExperience,
    int StrengthExperience,
    int DefenceExperience)
{
    internal int ExperienceFor(MeleeCombatStance stance) =>
        MeleeCombatService.ExperienceForStance(
            AttackExperience,
            StrengthExperience,
            DefenceExperience,
            stance);

    internal CombatProgression WithExperience(
        MeleeCombatStance stance,
        int experience) =>
        stance switch
        {
            MeleeCombatStance.Accurate => this with
            {
                AttackExperience = experience
            },
            MeleeCombatStance.Aggressive => this with
            {
                StrengthExperience = experience
            },
            _ => this with
            {
                DefenceExperience = experience
            }
        };
}

/// <summary>
/// Headless input required to resolve one accepted melee attack.
/// </summary>
public readonly record struct CombatAttackRequest(
    CombatRollKey RollKey,
    CombatProgression Progression,
    MeleeCombatStance Stance,
    string?[]? Inventory = null);

/// <summary>
/// Complete deterministic result of an attack, including stance-routed XP.
/// Target health and death remain authoritative caller-owned state transitions.
/// </summary>
public readonly record struct CombatAttackResolution(
    MeleeAttackRoll Attack,
    CombatProgression Progression,
    int ExperienceGained,
    int PreviousLevel,
    int Level)
{
    public bool LevelledUp => Level > PreviousLevel;
}

/// <summary>
/// Stateless, platform-independent random rolls for authoritative combat.
/// It deliberately avoids Random and HashCode, whose mutable/process-specific
/// state makes reconnect replay and checkpoint recovery non-deterministic.
/// </summary>
public static class DeterministicCombatRandom
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;
    private const ulong HitDomain = 0xD1B54A32D192ED03UL;
    private const ulong DamageDomain = 0x94D049BB133111EBUL;
    private const double Unit24 = 1.0 / (1 << 24);

    public static CombatRandomRoll Create(in CombatRollKey key)
    {
        var state = AppendInt64(OffsetBasis, key.WorldSeed);
        state = AppendGuid(state, key.AttackerId);
        state = AppendGuid(state, key.TargetId);
        state = AppendUInt64(state, key.AttackSequence);
        return new(
            UnitFloat(Mix(state ^ HitDomain)),
            UnitFloat(Mix(state ^ DamageDomain)));
    }

    private static ulong AppendGuid(ulong hash, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        foreach (var item in bytes) hash = AppendByte(hash, item);
        return hash;
    }

    private static ulong AppendInt64(ulong hash, long value) =>
        AppendUInt64(hash, unchecked((ulong)value));

    private static ulong AppendUInt64(ulong hash, ulong value)
    {
        for (var index = 0; index < sizeof(ulong); index++)
        {
            hash = AppendByte(hash, (byte)value);
            value >>= 8;
        }
        return hash;
    }

    private static ulong AppendByte(ulong hash, byte value) =>
        unchecked((hash ^ value) * Prime);

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value = unchecked(value * 0xBF58476D1CE4E5B9UL);
        value ^= value >> 27;
        value = unchecked(value * 0x94D049BB133111EBUL);
        return value ^ (value >> 31);
    }

    private static float UnitFloat(ulong value) =>
        (float)((value >> 40) * Unit24);
}

/// <summary>
/// Deterministically resolves melee hit, damage, knife bonus and stance XP
/// while preserving the established <see cref="MeleeCombatService"/> rules.
/// </summary>
public static class CombatResolver
{
    public static CombatAttackResolution Resolve(
        in CombatAttackRequest request)
    {
        if (!Enum.IsDefined(request.Stance))
            throw new ArgumentOutOfRangeException(nameof(request));
        var rolls = DeterministicCombatRandom.Create(request.RollKey);
        var attack = MeleeCombatService.Roll(
            request.Progression.AttackExperience,
            request.Progression.StrengthExperience,
            rolls.HitRoll,
            rolls.DamageRoll,
            request.Inventory);
        var previousExperience = request.Progression.ExperienceFor(
            request.Stance);
        var award = SkillService.AwardExperience(
            previousExperience,
            attack.Experience);
        return new(
            attack,
            request.Progression.WithExperience(
                request.Stance, award.Experience),
            award.Gained,
            award.PreviousLevel,
            award.Level);
    }
}
