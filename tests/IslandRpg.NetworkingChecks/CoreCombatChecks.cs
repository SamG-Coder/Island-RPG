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
        checks.Add(
            "core slime statuses preserve effects across fixed-step sizes",
            SlimeStatusesAreStepIndependent);
        checks.Add(
            "core slime splits have stable identities and geometry",
            SlimeSplitsAreDeterministic);
        checks.Add(
            "core enemy attacks and loot replay exactly",
            EnemyAttackAndLootReplayExactly);
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

    private static void SlimeStatusesAreStepIndependent()
    {
        var status = SlimeCombatRules.Apply(
            default, EnemyKind.WaterSlime, 10);
        status = SlimeCombatRules.Apply(
            status, EnemyKind.GrassSlime, 10);
        status = SlimeCombatRules.Apply(
            status, EnemyKind.CaveSlime, 10);

        CheckAssert.False(
            SlimeCombatRules.CanAcquireTarget(EnemyKind.GrassSlime, false, 0),
            "unprovoked surface slimes must not acquire a target");
        CheckAssert.True(
            SlimeCombatRules.CanAcquireTarget(EnemyKind.GrassSlime, true, 25),
            "a provoked surface slime must acquire its attacker");
        CheckAssert.True(
            SlimeCombatRules.CanAcquireTarget(EnemyKind.CaveSlime, false, 16),
            "cave slimes must auto-aggro inside the cave radius");
        CheckAssert.False(
            SlimeCombatRules.CanAcquireTarget(EnemyKind.CaveSlime, false, 36),
            "cave slimes must not auto-aggro outside the cave radius");
        CheckAssert.Equal(0f, status.MovementMultiplier(10.5),
            "a grass slime root must take precedence over water slow");
        CheckAssert.Equal(.58f, status.MovementMultiplier(12),
            "the water slow must resume after the shorter root expires");

        var single = SlimeCombatRules.Advance(status, 14.75);
        var steppedStatus = status;
        var steppedDamage = 0;
        var steppedTicks = 0;
        for (var now = 10.25; now <= 14.75; now += .25)
        {
            var step = SlimeCombatRules.Advance(steppedStatus, now);
            steppedStatus = step.Status;
            steppedDamage += step.PoisonDamage;
            steppedTicks += step.PoisonTicks;
        }

        CheckAssert.Equal(single.PoisonDamage, steppedDamage,
            "poison damage must not depend on simulation update size");
        CheckAssert.Equal(single.PoisonTicks, steppedTicks,
            "poison tick count must not depend on simulation update size");
        CheckAssert.Equal(single.Status, steppedStatus,
            "poison deadlines must converge after catch-up");
        CheckAssert.Equal(4, single.PoisonTicks,
            "a five-second poison must tick at seconds one through four");
    }

    private static void SlimeSplitsAreDeterministic()
    {
        var source = new SlimeSplitSource(
            Guid.Parse("6f3a4cac-0d0d-47de-97a3-70b19c57007e"),
            Guid.Parse("43a5f764-9795-4ccd-9ef0-dfab55940627"),
            EnemyKind.SandSlime,
            new(8, 12),
            new(10, 14),
            WorldLevel: 0,
            PowerLevel: 6,
            MaximumHealth: 42,
            SizeScale: SlimeCombatRules.SizeScale(6),
            SplitGeneration: 0);

        var first = SlimeCombatRules.Split(source, 2187);
        var replay = SlimeCombatRules.Split(source, 2187);

        CheckAssert.SequenceEqual(first, replay,
            "split child IDs and positions must replay exactly");
        CheckAssert.Equal(2, first.Length,
            "an eligible large slime must split into two children");
        CheckAssert.True(first[0].EnemyId != first[1].EnemyId,
            "split children must receive distinct stable identities");
        CheckAssert.True(first.All(child => child.EnemyId != Guid.Empty),
            "split children must never receive the empty identity");
        CheckAssert.True(first.All(child =>
                child.PowerLevel == 3 &&
                child.Health == 14 &&
                child.SplitGeneration == 1),
            "split children must preserve established power, health and generation rules");
        CheckAssert.Equal(0, SlimeCombatRules.Split(
                source with { SplitGeneration = 1 }, 2187).Length,
            "a split child must not split recursively");
    }

    private static void EnemyAttackAndLootReplayExactly()
    {
        var request = new EnemyAttackRequest(
            998_144,
            Guid.Parse("c108f2bd-298c-48ac-861b-d104716e84a8"),
            Guid.Parse("bf778cd9-f7b0-4653-ab3d-4c440f94f5d9"),
            AttackSequence: 12,
            PowerLevel: 7);
        var firstAttack = EnemyCombatRules.ResolveAttack(request);
        var replayAttack = EnemyCombatRules.ResolveAttack(request);

        CheckAssert.Equal(firstAttack, replayAttack,
            "an accepted enemy attack must replay exactly");
        CheckAssert.Equal(0, EnemyCombatRules.ApplyDamage(4, 8),
            "enemy damage must clamp health at zero");
        var defended = EnemyCombatRules.ResolveAttack(request with
        {
            TargetDefenceExperience = SkillService.ExperienceForLevel(20),
            TargetStance = MeleeCombatStance.Defensive
        });
        CheckAssert.True(
            !defended.Hit || defended.Damage == firstAttack.Damage,
            "defence may prevent a hit but must not mutate its damage roll");

        var lootSource = new SlimeLootSource(
            998_144, request.EnemyId, EnemyKind.CaveSlime, 7);
        var firstLoot = SlimeCombatRules.RollLoot(lootSource);
        var replayLoot = SlimeCombatRules.RollLoot(lootSource);
        CheckAssert.SequenceEqual(firstLoot, replayLoot,
            "loot must replay exactly from the authoritative death identity");
        CheckAssert.True(firstLoot is [{ ItemId: ItemIds.SlimeGel }, ..],
            "every defeated slime must drop slime gel first");
        CheckAssert.Equal(ItemIds.Coal,
            SlimeCombatRules.BiomeReagent(EnemyKind.CaveSlime),
            "cave slime reagent identity must be preserved");
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
