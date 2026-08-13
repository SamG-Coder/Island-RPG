using IslandRpg.Gameplay;

namespace IslandRpg.NetworkingChecks;

internal static class CoreCombatChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "core combat rolls are deterministic and sequence keyed",
            CombatRollsAreDeterministic);
        checks.Add(
            "core combat resolver preserves melee and stance XP rules",
            CombatResolverPreservesRules);
        checks.Add(
            "core action cooldowns are actor scoped",
            ActionCooldownsAreActorScoped);
        checks.Add(
            "core health recovery preserves fractions and dead state",
            HealthRecoveryPreservesFractions);
        checks.Add(
            "core timed healing is deterministic across update sizes",
            TimedHealingIsUpdateSizeIndependent);
        checks.Add(
            "core player death rules clamp damage and recover safely",
            PlayerDeathRulesAreSafe);
    }

    private static void CombatRollsAreDeterministic()
    {
        var key = new CombatRollKey(
            829_331,
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            7);
        var first = DeterministicCombatRandom.Create(key);
        var replay = DeterministicCombatRandom.Create(key);
        var next = DeterministicCombatRandom.Create(
            key with { AttackSequence = key.AttackSequence + 1 });

        CheckAssert.Equal(
            first,
            replay,
            "replaying the same accepted attack must reproduce both rolls");
        CheckAssert.True(
            first.HitRoll is >= 0 and < 1 &&
            first.DamageRoll is >= 0 and < 1,
            "combat random values must stay in the unit interval");
        CheckAssert.True(
            first != next,
            "the next accepted attack sequence must produce a new roll pair");
    }

    private static void CombatResolverPreservesRules()
    {
        var progression = new CombatProgression(
            AttackExperience: SkillService.ExperienceForLevel(7),
            StrengthExperience: SkillService.ExperienceForLevel(6),
            DefenceExperience: SkillService.ExperienceForLevel(5));
        var key = FirstHittingKey(progression);
        var inventory = new string?[PlayerInventory.Capacity];
        inventory[0] = ItemIds.StoneKnife;
        var request = new CombatAttackRequest(
            key,
            progression,
            MeleeCombatStance.Defensive,
            inventory);
        var rolls = DeterministicCombatRandom.Create(key);
        var expectedAttack = MeleeCombatService.Roll(
            progression.AttackExperience,
            progression.StrengthExperience,
            rolls.HitRoll,
            rolls.DamageRoll,
            inventory);
        var expectedAward = SkillService.AwardExperience(
            progression.DefenceExperience,
            expectedAttack.Experience);

        var result = CombatResolver.Resolve(request);

        CheckAssert.Equal(
            expectedAttack,
            result.Attack,
            "the resolver must preserve established accuracy, damage and knife rules");
        CheckAssert.Equal(
            progression.AttackExperience,
            result.Progression.AttackExperience,
            "defensive attacks must not train Attack");
        CheckAssert.Equal(
            progression.StrengthExperience,
            result.Progression.StrengthExperience,
            "defensive attacks must not train Strength");
        CheckAssert.Equal(
            expectedAward.Experience,
            result.Progression.DefenceExperience,
            "defensive attacks must route melee XP to Defence");
        CheckAssert.Equal(
            expectedAward.Gained,
            result.ExperienceGained,
            "the resolver must report the actual clamped XP gain");
        CheckAssert.Throws<ArgumentOutOfRangeException>(
            () => CombatResolver.Resolve(
                request with { Stance = (MeleeCombatStance)byte.MaxValue }),
            "undefined combat stances must fail instead of routing XP");
    }

    private static void ActionCooldownsAreActorScoped()
    {
        var cooldowns = new EntityActionCooldowns();

        CheckAssert.True(
            cooldowns.TryCommit("actor-a", EntityAction.Attack, 10, 2.4),
            "the first valid attack must commit");
        CheckAssert.False(
            cooldowns.TryCommit("actor-a", EntityAction.Attack, 12, 2.4),
            "the same actor must not bypass its attack cadence");
        CheckAssert.True(
            cooldowns.TryCommit("actor-b", EntityAction.Attack, 12, 2.4),
            "one actor's cadence must not block another actor");
        CheckAssert.True(
            cooldowns.TryCommit("actor-a", EntityAction.Move, 12, .1),
            "an attack cadence must not block another action category");
        CheckAssert.True(
            cooldowns.TryCommit("actor-a", EntityAction.Attack, 12.4, 2.4),
            "an attack must become available at its exact deadline");

        cooldowns.Forget("actor-a");
        CheckAssert.True(
            cooldowns.TryCommit("actor-a", EntityAction.Attack, 12.41, 2.4),
            "forgetting a despawned actor must release all of its cooldowns");
    }

    private static void HealthRecoveryPreservesFractions()
    {
        var single = EntityHealthRegenerationService.Advance(
            50, 100, 12, remainder: 0);
        var stepped = new HealthRegenerationUpdate(50, 0);
        for (var index = 0; index < 2; index++)
            stepped = EntityHealthRegenerationService.Advance(
                stepped.Health,
                100,
                6,
                remainder: stepped.Remainder);

        CheckAssert.Equal(
            single.Health,
            stepped.Health,
            "frequent simulation steps must not discard fractional regeneration");
        CheckAssert.True(
            MathF.Abs(single.Remainder - stepped.Remainder) < .00001f,
            "frequent simulation steps must preserve the same remainder");

        var dead = EntityHealthRegenerationService.Advance(
            0, 100, 300, multiplier: 20, remainder: .25f);
        CheckAssert.Equal(0, dead.Health,
            "dead actors must not regenerate");
        CheckAssert.Equal(.25f, dead.Remainder,
            "dead actors must not mutate stored regeneration progress");
    }

    private static void TimedHealingIsUpdateSizeIndependent()
    {
        var effect = new FoodEffect(
            HungerRestored: 0,
            HealthRestored: 0,
            WellFedSeconds: 0,
            TimedHealing: 12,
            TimedHealingSeconds: 6);
        var initial = TimedHealingService.Start(effect);
        var single = TimedHealingService.Advance(40, 100, 3, initial);
        var stepped = new TimedHealingUpdate(40, initial);
        for (var index = 0; index < 6; index++)
            stepped = TimedHealingService.Advance(
                stepped.Health, 100, .5f, stepped.State);

        CheckAssert.Equal(
            single.Health,
            stepped.Health,
            "timed healing health must not depend on update frequency");
        CheckAssert.True(
            MathF.Abs(
                single.State.RemainingHealth -
                stepped.State.RemainingHealth) < .00001f,
            "timed healing must consume the same amount across update sizes");
        CheckAssert.True(
            MathF.Abs(
                single.State.RemainingSeconds -
                stepped.State.RemainingSeconds) < .00001f,
            "timed healing must consume the same duration across update sizes");

        var dead = TimedHealingService.Advance(0, 100, 3, initial);
        CheckAssert.Equal(0, dead.Health,
            "dead actors must not receive timed healing");
        CheckAssert.Equal(initial, dead.State,
            "dead actors must retain rather than consume pending timed healing");
    }

    private static void PlayerDeathRulesAreSafe()
    {
        CheckAssert.Equal(
            10,
            PlayerDeathService.ApplyDamage(10, -50),
            "non-positive damage must never heal or hurt a player");
        CheckAssert.Equal(
            0,
            PlayerDeathService.ApplyDamage(10, 50),
            "lethal damage must clamp health at zero");
        CheckAssert.True(
            PlayerDeathService.IsDefeated(0),
            "zero health must be defeated");

        var recovery = PlayerDeathService.Recover(101);
        CheckAssert.Equal(50, recovery.Health,
            "recovery must restore half maximum health using integer rules");
        CheckAssert.Equal(
            PlayerDeathService.RecoveryHunger,
            recovery.Hunger,
            "recovery must restore the canonical hunger floor");
    }

    private static CombatRollKey FirstHittingKey(
        CombatProgression progression)
    {
        var key = new CombatRollKey(
            449_013,
            Guid.Parse("12345678-1234-5678-90ab-1234567890ab"),
            Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab"),
            0);
        for (ulong sequence = 0; sequence < 128; sequence++)
        {
            var candidate = key with { AttackSequence = sequence };
            var rolls = DeterministicCombatRandom.Create(candidate);
            if (MeleeCombatService.Roll(
                    progression.AttackExperience,
                    progression.StrengthExperience,
                    rolls.HitRoll,
                    rolls.DamageRoll).Hit)
                return candidate;
        }

        throw new InvalidOperationException(
            "the deterministic combat test could not locate a hitting sequence");
    }
}
