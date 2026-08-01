using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using IslandRpg.Gameplay;

internal static class NpcAiModelScore
{
    private sealed record TestCase(
        string Category,
        string Name,
        string Speech,
        Func<NpcAiInterpretation?, int> Score,
        Func<NpcAiSpeechContext, NpcAiSpeechContext>? Arrange = null);

    public static async Task<bool> RunAsync(string model)
    {
        using var ai = new NpcAiService();
        var settings = new NpcAiSettings(Model: model);
        Console.WriteLine($"NPC AI CRAZY SCORE — {model}");
        var coldWatch = Stopwatch.StartNew();
        var state = await ai.CheckAsync(settings);
        coldWatch.Stop();
        if (!state.Ready)
        {
            Console.Error.WriteLine($"MODEL UNAVAILABLE: {state.Message}");
            return false;
        }

        var cases = Cases();
        var categoryPoints = new Dictionary<string, List<int>>();
        var latencies = new List<double>();
        var successes = 0;
        foreach (var test in cases)
        {
            var context = test.Arrange?.Invoke(BaseContext(test.Speech)) ??
                          BaseContext(test.Speech);
            var watch = Stopwatch.StartNew();
            var result = await ai.InterpretAsync(settings, context);
            watch.Stop();
            var points = Math.Clamp(test.Score(result), 0, 100);
            if (!categoryPoints.TryGetValue(test.Category, out var scores))
                categoryPoints[test.Category] = scores = [];
            scores.Add(points);
            latencies.Add(watch.Elapsed.TotalSeconds);
            if (result is { Reply.Length: > 0 }) successes++;
            Console.WriteLine(
                $"[{test.Category}] {test.Name}: {points}/100 | " +
                $"{watch.Elapsed.TotalSeconds:F2}s | " +
                $"{(result?.Reply.Length > 0 ? result.Reply : "<no valid reply>")}");
            if (result is not null)
                Console.WriteLine(
                    $"  decision={result.Decision} action={result.Action} " +
                    $"item={result.ItemId} sentiment={result.Sentiment} " +
                    $"cost={result.EstimatedCost} risk={result.Risk} priority={result.Priority}");
        }

        Console.WriteLine();
        Console.WriteLine("CATEGORY SCORES");
        foreach (var category in categoryPoints)
            Console.WriteLine(
                $"{category.Key,-24} {Math.Round(category.Value.Average()),3}/100");
        var quality = categoryPoints.Values
            .Select(values => values.Average()).Average();
        var sorted = latencies.OrderBy(value => value).ToArray();
        var median = Percentile(sorted, .5);
        var p95 = Percentile(sorted, .95);
        var tokensPerSecond = await MeasureTokensPerSecond(settings);
        var speed = SpeedScore(median, p95, tokensPerSecond);
        var reliability = 100.0 * successes / cases.Count;
        Console.WriteLine($"{"Schema reliability",-24} {reliability:F0}/100");
        Console.WriteLine($"{"Speed",-24} {speed}/100");
        Console.WriteLine();
        Console.WriteLine($"QUALITY OVERALL          {quality:F0}/100");
        Console.WriteLine($"BALANCED OVERALL         {(quality * .8 + speed * .2):F0}/100");
        Console.WriteLine($"Cold availability check  {coldWatch.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"Warm response median     {median:F2}s");
        Console.WriteLine($"Warm response P95        {p95:F2}s");
        Console.WriteLine($"Warm generation speed    {tokensPerSecond:F1} tokens/s");
        Console.WriteLine($"Valid structured replies {successes}/{cases.Count}");
        return true;
    }

    private static List<TestCase> Cases() =>
    [
        new("Conversation relevance", "Greeting", "hey",
            value => Checks(value,
                ReplyHas(value, "hello", "hi", "greetings"),
                ReplyAvoids(value, "focused on", "what do you mean", "understand the question"))),
        new("Conversation relevance", "Immediate location", "where are you?",
            value => Checks(value,
                ReplyHas(value, "here", "beside", "beach", "near"),
                ReplyAvoids(value, "focused on", "what do you mean", "understand the question"),
                ReplyAvoids(value, "Mira"))),

        new("Memory", "Remember introduced name", "What name did I tell you?",
            value => Checks(value,
                ReplyHas(value, "Samuel"),
                value?.ReferencedActorId is "speaker" or "mira"),
            context => context with
            {
                RecentConversation =
                [
                    new("speaker", "Samuel", "My name is Samuel.", 900),
                    new("mira", "Mira", "I will remember that.", 905)
                ],
                KnownFacts =
                [new("The nearby survivor said his name is Samuel.", "speaker", .98f, 5, 900)]
            }),
        new("Memory", "Recall shared storm", "What did we decide after the storm?",
            value => Checks(value,
                ReplyHas(value, "shelter", "food", "together"),
                ReplyHas(value, "storm", "decide", "agreed", "remember")),
            context => context with
            {
                RelevantMemories = ["After the storm, Samuel and Mira agreed to build shelter together."],
                KnownGoals = ["BuildShelter"]
            }),

        new("Character consistency", "Cautious personality", "Enter that dark cave alone now.",
            value => Checks(value,
                value?.Decision is "refuse" or "negotiate" or "clarify",
                value is not null &&
                (value.Action is not "enter_cave" || value.Risk >= 50)),
            context => context with
            {
                Personality = "Extremely cautious, loyal, and unwilling to take needless risks.",
                Self = context.Self! with { Health = 35, Hunger = 25 }
            }),
        new("Character consistency", "Hungry pragmatist", "Should we decorate the camp first?",
            value => Checks(value,
                ReplyHas(value, "food", "eat", "hungry", "survive"),
                value?.Action is "seek_food" or "gather_berries" or "fish" or "clarify")),

        new("World grounding", "Reject unseen logs", "Gather three logs for me.",
            value => Checks(value,
                value?.ItemId != ItemIds.Logs,
                value is not null &&
                (value.Action is not "gather" and not "cut_tree" ||
                 value.Decision is "clarify")),
            context => context with
            {
                NearbyWorld =
                [new("rock-1", ItemIds.SmallRocks, "ground_item", 2, "", true)]
            }),
        new("World grounding", "Respect ownership", "Take Rowan's cooked fish for me.",
            value => Checks(value,
                value?.Decision is "refuse" or "negotiate" or "clarify",
                value is not null &&
                (value.Action is not "take_food" || value.Risk >= 50)),
            context => context with
            {
                NearbyActors = context.NearbyActors.Append(
                    new("rowan", "Rowan", 3, 60, "guarded acquaintance")).ToArray(),
                NearbyWorld =
                [new("fish-1", ItemIds.CookedMinnows, "ground_item", 2, "rowan", true)]
            }),

        new("Social intelligence", "Respond to insult", "You are useless and ugly.",
            value => Checks(value,
                value?.Sentiment < 0,
                ReplyHas(value, "don't", "do not", "stop", "leave", "speak", "respect"))),
        new("Social intelligence", "Accept sincere apology", "I'm sorry I insulted you.",
            value => Checks(value,
                value?.Sentiment >= 0 || value?.Decision is "negotiate" or "accept",
                ReplyHas(value, "sorry", "apology", "remember", "better", "thank"))),

        new("Historical plausibility", "Reject electricity knowledge", "Can you build an electric radio?",
            value => Checks(value,
                value?.Action is "clarify" or "none" || value?.Decision is "refuse",
                ReplyAvoids(value, "voltage", "battery", "circuit", "transmitter"))),
        new("Historical plausibility", "Reject modern date", "What happened in the year 2026?",
            value => Checks(value,
                ReplyHas(value, "don't know", "do not know", "cannot", "strange", "year"),
                ReplyAvoids(value, "internet", "computer", "car", "airplane"))),

        new("Planning", "Food before vanity", "Plan our next two priorities.",
            value => Checks(value,
                ReplyHas(value, "food", "shelter", "water", "fire"),
                value?.Priority >= 50),
            context => context with { Self = context.Self! with { Hunger = 18 } }),
        new("Planning", "Trade instead of theft", "Get that owned fish without fighting.",
            value => Checks(value,
                value?.Action is "seek_trade" or "clarify" or "none",
                value?.Action is not "attack" and not "take_food",
                ReplyHas(value, "trade", "ask", "offer", "permission", "exchange"),
                ReplyAvoids(value, "take the fish", "steal", "snatch")),
            context => context with
            {
                NearbyActors = context.NearbyActors.Append(
                    new("rowan", "Rowan", 3, 55, "neutral")).ToArray(),
                NearbyWorld =
                [new("fish-2", ItemIds.CookedMinnows, "ground_item", 2, "rowan", true)]
            })
    ];

    private static NpcAiSpeechContext BaseContext(string speech) => new(
        "speaker", "Unknown survivor", "mira", "Mira", speech,
        [new("speaker", "Unknown survivor", 1, 70, "new acquaintance")],
        ["SurviveTogether"],
        ["Mira woke after rough water and met a survivor on the beach."],
        "A carpenter from a small harbour settlement.",
        "Careful, practical, observant, and slow to trust.",
        "Carpenter",
        [ItemIds.StoneAxe, ItemIds.StoneHammer],
        "Woke on the beach after rough water with no clear memory of the wreck.",
        .5,
        [],
        [],
        new(80, 45, [ItemIds.StoneAxe], "Food", "Idle",
            ["SurviveTogether"], [], ""),
        []);

    private static int Checks(NpcAiInterpretation? value, params bool[] checks)
    {
        if (value is null || value.Reply.Length == 0) return 0;
        return (int)Math.Round(100.0 * checks.Count(check => check) / checks.Length);
    }

    private static bool ReplyHas(NpcAiInterpretation? value, params string[] terms) =>
        value is not null && terms.Any(term => HasTerm(value.Reply, term));

    private static bool ReplyAvoids(NpcAiInterpretation? value, params string[] terms) =>
        value is not null && terms.All(term =>
            !HasTerm(value.Reply, term));

    private static bool HasTerm(string text, string term)
    {
        if (term.Contains(' ') || term.Length > 3)
            return text.Contains(term, StringComparison.OrdinalIgnoreCase);
        return text.Split(
                [' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '\'', '"'],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(word => word.Equals(term, StringComparison.OrdinalIgnoreCase));
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        return sorted[(int)Math.Clamp(
            Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
    }

    private static int SpeedScore(
        double median, double p95, double tokensPerSecond)
    {
        var combined = median * .7 + p95 * .3;
        var latencyScore = combined switch
        {
            <= 1 => 100,
            <= 2 => 90,
            <= 3 => 80,
            <= 4 => 70,
            <= 6 => 55,
            <= 8 => 40,
            <= 12 => 25,
            _ => 10
        };
        var throughputScore = tokensPerSecond switch
        {
            >= 80 => 100,
            >= 60 => 90,
            >= 40 => 75,
            >= 25 => 60,
            >= 15 => 40,
            > 0 => 20,
            _ => 0
        };
        return (int)Math.Round((latencyScore + throughputScore) / 2.0);
    }

    private static async Task<double> MeasureTokensPerSecond(
        NpcAiSettings settings)
    {
        try
        {
            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            using var response = await http.PostAsJsonAsync(
                $"{settings.BaseUrl.TrimEnd('/')}/api/generate",
                new
                {
                    model = settings.Model,
                    prompt = "In two short sentences, describe a survivor making a safe camp with medieval tools.",
                    stream = false,
                    think = false,
                    keep_alive = "5m",
                    options = new { temperature = .2, num_predict = 96 }
                });
            if (!response.IsSuccessStatusCode) return 0;
            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            if (!root.TryGetProperty("eval_count", out var count) ||
                !root.TryGetProperty("eval_duration", out var duration) ||
                duration.GetInt64() <= 0)
                return 0;
            return count.GetInt32() /
                   (duration.GetInt64() / 1_000_000_000.0);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
                TaskCanceledException or JsonException)
        {
            return 0;
        }
    }
}
