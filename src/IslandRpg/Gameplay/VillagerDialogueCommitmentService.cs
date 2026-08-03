namespace IslandRpg.Gameplay;

/// <summary>
/// Repairs the narrow class of malformed local-model responses where the
/// spoken answer unmistakably accepts an executable item request while the
/// structured decision remains none/none. Ambiguous or negative speech is
/// deliberately left untouched.
/// </summary>
internal static class VillagerDialogueCommitmentService
{
    public static NpcAiInterpretation NormalizePendingProposal(
        NpcAiInterpretation interpretation,
        string proposalText)
    {
        if (interpretation.Decision is not ("" or "none") ||
            interpretation.Action is not ("" or "none" or "clarify") ||
            interpretation.Willingness < 70 ||
            !ClearlyAccepts(interpretation.Reply))
            return interpretation;

        foreach (var candidateAction in new[] { "gather", "give" })
        {
            if (!VillagerCommitmentService.TryResolveAiItemProposal(
                    proposalText,
                    candidateAction,
                    string.IsNullOrWhiteSpace(interpretation.ItemId)
                        ? proposalText
                        : interpretation.ItemId,
                    Math.Max(1, interpretation.Quantity),
                    out var kind,
                    out var itemId,
                    out var quantity))
                continue;
            return interpretation with
            {
                Decision = "accept",
                Action = kind == VillagerPromiseKind.GiveItem
                    ? "give"
                    : "gather",
                ItemId = itemId,
                Quantity = quantity,
                FreeformThought = false
            };
        }
        return interpretation;
    }

    private static bool ClearlyAccepts(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return false;
        var text = reply.Trim().ToLowerInvariant();
        if (new[]
            {
                "cannot", "can't", "will not", "won't", "refuse",
                "decline", "do not agree", "don't agree"
            }.Any(value => text.Contains(value, StringComparison.Ordinal)))
            return false;
        return new[]
        {
            "i will ", "i'll ", "i shall ", "yes", "agreed",
            "all right", "certainly", "consider it done"
        }.Any(value => text.Contains(value, StringComparison.Ordinal));
    }
}
