namespace IslandRpg.Gameplay;

internal readonly record struct VillagerConflictDecision(
    VillagerConflictIntent Intent,
    string Thought,
    int Risk,
    bool IsAggressive = false);

internal static class VillagerConflictService
{
    public const double ConflictDurationGameSeconds = 10 * 60;

    public static VillagerConflictDecision DecideResponse(
        VillagerState responder,
        VillagerState aggressor,
        bool wasAttacked,
        int nearbyAllies = 0)
    {
        if (responder.Health <= 0 || aggressor.Health <= 0)
            return new(VillagerConflictIntent.None,
                "A dead actor cannot enter conflict.", 0);
        var relationship = responder.Relationships?.FirstOrDefault(value =>
            value.CharacterId == aggressor.Id)?.State ?? default;
        var health = responder.Health;
        var weaponPower = VillagerWeaponAwareness.KnownKnifePower(
            responder, aggressor.Id);
        var weaponRisk = VillagerWeaponAwareness.RiskBonus(
            responder, aggressor.Id);
        if (health <= 20)
            return new(VillagerConflictIntent.Surrender,
                "I cannot survive another hit. I should surrender.", 10);
        if (weaponPower > 0 && health <= 35)
            return new(VillagerConflictIntent.Surrender,
                $"I saw their {VillagerWeaponAwareness.BestKnownKnife(responder, aggressor.Id)!.Name}. Fighting now could kill me.",
                Math.Min(100, 35 + weaponRisk));
        if (weaponPower > 0 && responder.Boldness < .45f)
            return new(VillagerConflictIntent.Flee,
                "I know they are armed with a knife. I should get away.",
                Math.Min(100, 40 + weaponRisk));
        if (weaponPower > 0 && nearbyAllies > 0 &&
            responder.Sociability >= .5f)
            return new(VillagerConflictIntent.CallForHelp,
                "They are armed. I should call an ally instead of facing them alone.",
                Math.Min(100, 50 + weaponRisk));
        if (responder.Boldness < .3f || relationship.Fear >= 20)
            return new(VillagerConflictIntent.Flee,
                "This is too dangerous. I should get away.", 20);
        if (nearbyAllies > 0 && responder.Sociability >= .65f &&
            responder.Boldness < .65f)
            return new(VillagerConflictIntent.CallForHelp,
                "I should call for help instead of facing this alone.", 45);
        if (wasAttacked &&
            (relationship.Resentment >= 15 || responder.Boldness >= .75f))
            return new(VillagerConflictIntent.Retaliate,
                "They attacked me. I will strike back while I still can.",
                Math.Min(100, 85 + weaponRisk), true);
        if (wasAttacked && responder.Boldness >= .5f)
            return new(VillagerConflictIntent.Defend,
                "I need to defend myself and make them stop.",
                Math.Min(100, 65 + weaponRisk), true);
        return new(VillagerConflictIntent.Warn,
            "I should warn them before this becomes violence.", 35);
    }

    public static VillagerState ApplyDecision(
        VillagerState responder,
        VillagerState aggressor,
        VillagerConflictDecision decision,
        string motive,
        double gameSeconds)
    {
        if (responder.Health <= 0 || aggressor.Health <= 0 ||
            decision.Intent == VillagerConflictIntent.None)
            return Clear(responder, gameSeconds);
        var clearsConflict = decision.Intent is
            VillagerConflictIntent.Forgive or
            VillagerConflictIntent.Deescalate;
        return responder with
        {
            ConflictTargetId = clearsConflict ? null : aggressor.Id,
            ConflictIntent = clearsConflict
                ? VillagerConflictIntent.None : decision.Intent,
            ConflictMotive = clearsConflict ? null : motive,
            ConflictExpiresGameSeconds = clearsConflict
                ? 0 : gameSeconds + ConflictDurationGameSeconds,
            FollowingActorId = null,
            Need = VillagerNeed.Safe,
            NextDecisionGameSeconds = gameSeconds,
            LastDeliberation = new(
                decision.Thought,
                "conflict_response",
                ActionName(decision.Intent),
                decision.IsAggressive ? 80 : 45,
                10,
                decision.Risk,
                90,
                gameSeconds)
        };
    }

    public static VillagerState Clear(VillagerState state, double gameSeconds) =>
        state with
        {
            ConflictTargetId = null,
            ConflictIntent = VillagerConflictIntent.None,
            ConflictMotive = null,
            ConflictExpiresGameSeconds = 0,
            TargetX = null,
            TargetY = null,
            Action = state.Health <= 0 ? EntityAction.Die : EntityAction.Idle,
            NextDecisionGameSeconds = gameSeconds
        };

    public static VillagerState Expire(
        VillagerState state,
        double gameSeconds) =>
        state.ConflictIntent != VillagerConflictIntent.None &&
        state.ConflictExpiresGameSeconds > 0 &&
        state.ConflictExpiresGameSeconds <= gameSeconds
            ? Clear(state, gameSeconds)
            : state;

    public static string ActionName(VillagerConflictIntent intent) => intent switch
    {
        VillagerConflictIntent.CallForHelp => "call_help",
        VillagerConflictIntent.Deescalate => "deescalate",
        _ => intent.ToString().ToLowerInvariant()
    };
}
