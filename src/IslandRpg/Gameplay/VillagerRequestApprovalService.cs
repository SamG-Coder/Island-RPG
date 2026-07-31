namespace IslandRpg.Gameplay;

internal enum VillagerRefusalStrategy : byte
{
    BeNice,
    SeekTrade,
    Threaten,
    TakeByForce
}

internal readonly record struct VillagerRequestApproval(
    bool Approved, string Reply, int Score, string Reason);

internal readonly record struct VillagerRefusalPlan(
    VillagerRefusalStrategy Strategy,
    string Thought,
    string Action,
    string? TradeItemId = null);

internal static class VillagerRequestApprovalService
{
    public static VillagerRequestApproval EvaluateFoodRequest(
        VillagerState requester,
        VillagerState owner,
        double gameSeconds)
    {
        var food = VillagerSimulation.CountFood(owner.Inventory);
        var relationship = Relationship(owner, requester.Id);
        var ownerNeedsFoodSoon =
            owner.Hunger <= VillagerNeedPatternMemory.FoodPlanningThreshold ||
            VillagerNeedPatternMemory.NeedsFoodSoon(
                owner, owner.Id, gameSeconds);
        var reserve = ownerNeedsFoodSoon ? 3 : 1;
        var offeredTradeItem = requester.LastDeliberation is
            { Action: "seek_trade", ItemId: { Length: > 0 } tradeItem } &&
            requester.Inventory.Contains(tradeItem)
                ? tradeItem
                : null;
        var score = (food - reserve) * 8 +
                    (int)MathF.Round(relationship.Trust * .6f) +
                    (int)MathF.Round(owner.Honesty * 8) +
                    (int)MathF.Round(owner.Sociability * 6) -
                    (int)MathF.Round(relationship.Resentment * .5f) +
                    (offeredTradeItem is null ? 0 : 40);
        if (food <= reserve)
            return new(false,
                "No. I need to keep what little food I have.",
                score, "protected_reserve");
        if (relationship.Trust <= -25)
            return new(false,
                "No. I don't trust you enough to share my supplies.",
                score, "distrust");
        if (score < 8)
            return new(false,
                "Not now. I may need this food soon.",
                score, "future_scarcity");
        if (offeredTradeItem is not null)
            return new(true,
                $"Yes. I'll trade one meal for your {ItemCatalog.Get(offeredTradeItem).Name}.",
                score, "trade_offer");
        return new(true,
            "Yes. I have enough to share one meal.",
            score, "surplus_available");
    }

    public static VillagerRefusalPlan PlanAfterRefusal(
        VillagerState requester,
        VillagerState owner)
    {
        var relationship = Relationship(requester, owner.Id);
        if (requester.Hunger <= 10 && requester.Boldness >= .65f)
            return new(
                VillagerRefusalStrategy.TakeByForce,
                "I may not survive another refusal. I could take the food by force.",
                "take_food");
        if ((requester.Hunger <= 20 || relationship.Resentment >= 20) &&
            requester.Boldness >= .55f)
            return new(
                VillagerRefusalStrategy.Threaten,
                "Pressure might make them reconsider, but it could destroy trust.",
                "threaten");
        var tradeItem = SelectTradeItem(owner);
        if (tradeItem is not null && requester.Honesty >= .35f)
            return new(
                VillagerRefusalStrategy.SeekTrade,
                $"I should find {ItemCatalog.Get(tradeItem).Name} and offer a fair trade.",
                "seek_trade",
                tradeItem);
        return new(
            VillagerRefusalStrategy.BeNice,
            "Their supplies are theirs. I should accept the refusal and find another way.",
            "accept_refusal");
    }

    public static (VillagerState Requester, VillagerState Owner)
        ApplyRefusal(
            VillagerState requester,
            VillagerState owner,
            VillagerRefusalPlan plan,
            double gameSeconds)
    {
        requester = AddRelationshipChange(
            requester, owner.Id,
            trust: plan.Strategy == VillagerRefusalStrategy.BeNice ? 0 : -1,
            resentment: plan.Strategy is
                VillagerRefusalStrategy.Threaten or
                VillagerRefusalStrategy.TakeByForce ? 3 : 0);
        owner = plan.Strategy switch
        {
            VillagerRefusalStrategy.Threaten => AddRelationshipChange(
                owner, requester.Id, trust: -5, fear: 8, resentment: 5),
            VillagerRefusalStrategy.TakeByForce => AddRelationshipChange(
                owner, requester.Id, trust: -15, fear: 12, resentment: 15),
            _ => owner
        };
        var memories = requester.Memories?.ToList() ?? [];
        memories.Add(new(
            Guid.NewGuid(),
            "request-refused",
            owner.Id,
            null,
            1,
            gameSeconds,
            Sentiment: plan.Strategy == VillagerRefusalStrategy.BeNice ? 0 : -5,
            Summary: $"{owner.Name} refused a request for food."));
        if (memories.Count > VillagerSimulation.MaximumMemories)
            memories.RemoveAt(0);
        requester = requester with
        {
            Memories = memories,
            LastDeliberation = new(
                plan.Thought,
                "refusal_response",
                plan.Action,
                Willingness: plan.Strategy == VillagerRefusalStrategy.BeNice
                    ? 30 : 75,
                EstimatedCost: plan.Strategy == VillagerRefusalStrategy.SeekTrade
                    ? 35 : 15,
                Risk: plan.Strategy switch
                {
                    VillagerRefusalStrategy.TakeByForce => 95,
                    VillagerRefusalStrategy.Threaten => 70,
                    _ => 15
                },
                Priority: Math.Clamp((int)(100 - requester.Hunger), 1, 100),
                GameSeconds: gameSeconds,
                ItemId: plan.TradeItemId ?? ItemIds.CookedMinnows),
            NextDecisionGameSeconds = gameSeconds
        };
        return (requester, owner);
    }

    private static string? SelectTradeItem(VillagerState owner)
    {
        foreach (var itemId in new[]
                 {
                     ItemIds.PlantFibres,
                     ItemIds.Sticks,
                     ItemIds.LargeRock
                 })
            if (VillagerWorkSupplyPlanner.NeedsItem(owner, itemId))
                return itemId;
        return ItemIds.Sticks;
    }

    private static RelationshipState Relationship(
        VillagerState state, string characterId) =>
        state.Relationships?.FirstOrDefault(value =>
            value.CharacterId == characterId)?.State ?? default;

    private static VillagerState AddRelationshipChange(
        VillagerState state,
        string characterId,
        float trust = 0,
        float fear = 0,
        float resentment = 0)
    {
        var relationships = state.Relationships?.ToList() ?? [];
        var index = relationships.FindIndex(value =>
            value.CharacterId == characterId);
        var existing = index >= 0
            ? relationships[index]
            : new VillagerRelationship(characterId, default);
        var updated = existing with
        {
            State = (existing.State with
            {
                Trust = existing.State.Trust + trust,
                Fear = existing.State.Fear + fear,
                Resentment = existing.State.Resentment + resentment
            }).Clamp()
        };
        if (index >= 0) relationships[index] = updated;
        else relationships.Add(updated);
        return state with { Relationships = relationships };
    }
}
