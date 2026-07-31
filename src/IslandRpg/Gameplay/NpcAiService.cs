using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace IslandRpg.Gameplay;

internal sealed record NpcAiSettings(
    bool Enabled = true,
    string BaseUrl = "http://localhost:11434",
    string Model = "qwen3:4b",
    string Password = "");

internal enum NpcAiAvailability : byte
{
    Disabled,
    Checking,
    ServerUnavailable,
    ModelMissing,
    ModelUnresponsive,
    Ready
}

internal sealed record NpcAiRuntimeState(
    NpcAiAvailability Availability,
    string Message,
    DateTime CheckedUtc)
{
    public bool Ready =>
        Availability == NpcAiAvailability.Ready;
}

internal sealed record NpcAiActor(
    string Id,
    string Name,
    float Distance,
    float Hunger,
    string Relationship);

internal sealed record NpcAiKnownFact(
    string Summary,
    string SourceId,
    float Confidence,
    int Sentiment,
    double LearnedGameSeconds);

internal sealed record NpcAiWorldObservation(
    string ObjectId,
    string ItemId,
    string Kind,
    float Distance,
    string OwnerId,
    bool Reachable);

internal sealed record NpcAiSelfContext(
    int Health,
    float Hunger,
    IReadOnlyList<string> Inventory,
    string Need,
    string Activity,
    IReadOnlyList<string> ActiveGoals,
    IReadOnlyList<string> ActivePromises,
    string LastPrivateThought = "");

internal sealed record NpcAiSpeechContext(
    string SpeakerId,
    string SpeakerName,
    string ListenerId,
    string ListenerName,
    string Text,
    IReadOnlyList<NpcAiActor> NearbyActors,
    IReadOnlyList<string> KnownGoals,
    IReadOnlyList<string> RelevantMemories,
    string BackgroundStory = "",
    string Personality = "",
    string PriorTrade = "",
    IReadOnlyList<string>? KnownToolIds = null,
    string ArrivalMemory = "",
    double HoursOnIsland = 0,
    IReadOnlyList<VillagerConversationTurn>? RecentConversation = null,
    IReadOnlyList<NpcAiKnownFact>? KnownFacts = null,
    NpcAiSelfContext? Self = null,
    IReadOnlyList<NpcAiWorldObservation>? NearbyWorld = null);

internal sealed record NpcAiInterpretation(
    string AddressedActorId,
    string ReferencedActorId,
    string Desire,
    string Action,
    string ItemId,
    int Quantity,
    int Sentiment,
    string Goal,
    string Memory,
    string Reply,
    bool FreeformThought,
    string PrivateThought = "",
    string Decision = "",
    int Willingness = 50,
    int EstimatedCost = 0,
    int Risk = 0,
    int Priority = 50,
    string LocationHint = "",
    string ReplyMeaning = "");

internal sealed record NpcAiDialogueContext(
    string SpeakerName,
    string ListenerName,
    string Intent,
    string DeterministicMeaning,
    float Hunger,
    string Relationship,
    IReadOnlyList<string> RelevantMemories,
    string BackgroundStory = "",
    string Personality = "",
    string PriorTrade = "",
    IReadOnlyList<string>? KnownToolIds = null,
    string ArrivalMemory = "",
    double HoursOnIsland = 0,
    IReadOnlyList<VillagerConversationTurn>? RecentConversation = null);

internal sealed class NpcAiService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public NpcAiService(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
    }

    public async Task<NpcAiRuntimeState> CheckAsync(
        NpcAiSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled)
            return State(
                NpcAiAvailability.Disabled,
                "AI is disabled.");
        if (!TryBaseUri(settings.BaseUrl, out var baseUri))
            return State(
                NpcAiAvailability.ServerUnavailable,
                "The AI URL is invalid.");
        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var tagsRequest = Request(
                HttpMethod.Get,
                new(baseUri, "api/tags"),
                settings.Password);
            using var tagsResponse = await _http.SendAsync(
                tagsRequest, timeout.Token);
            if (!tagsResponse.IsSuccessStatusCode)
                return State(
                    NpcAiAvailability.ServerUnavailable,
                    $"AI server returned {(int)tagsResponse.StatusCode}.");
            var tags = await tagsResponse.Content
                .ReadFromJsonAsync<OllamaTags>(
                    JsonOptions, timeout.Token);
            if (tags?.Models?.Any(model =>
                    ModelMatches(model.Name, settings.Model) ||
                    ModelMatches(model.Model, settings.Model)) != true)
                return State(
                    NpcAiAvailability.ModelMissing,
                    $"Model {settings.Model} is not installed.");

            using var probeRequest = Request(
                HttpMethod.Post,
                new(baseUri, "api/generate"),
                settings.Password);
            probeRequest.Content = JsonContent.Create(new
            {
                model = settings.Model,
                prompt = "Reply with exactly READY.",
                stream = false,
                think = false,
                keep_alive = "5m",
                options = new
                {
                    temperature = 0,
                    num_predict = 4
                }
            });
            using var probeResponse = await _http.SendAsync(
                probeRequest, timeout.Token);
            if (!probeResponse.IsSuccessStatusCode)
                return State(
                    NpcAiAvailability.ModelUnresponsive,
                    $"Model probe returned {(int)probeResponse.StatusCode}.");
            var probe = await probeResponse.Content
                .ReadFromJsonAsync<OllamaGenerate>(
                    JsonOptions, timeout.Token);
            return !string.IsNullOrWhiteSpace(probe?.Response)
                ? State(
                    NpcAiAvailability.Ready,
                    $"{settings.Model} is responding.")
                : State(
                    NpcAiAvailability.ModelUnresponsive,
                    $"{settings.Model} returned no response.");
        }
        catch (OperationCanceledException)
        {
            return State(
                NpcAiAvailability.ServerUnavailable,
                "AI check timed out.");
        }
        catch (HttpRequestException exception)
        {
            return State(
                NpcAiAvailability.ServerUnavailable,
                $"AI server unavailable: {exception.Message}");
        }
        catch (JsonException)
        {
            return State(
                NpcAiAvailability.ModelUnresponsive,
                "AI server returned invalid JSON.");
        }
    }

    public async Task<NpcAiInterpretation?> InterpretAsync(
        NpcAiSettings settings,
        NpcAiSpeechContext context,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled ||
            !TryBaseUri(settings.BaseUrl, out var baseUri))
            return null;
        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var compactContext = context with
        {
            RelevantMemories = CompactStrings(
                context.RelevantMemories, 6, 560),
            KnownGoals = CompactStrings(
                context.KnownGoals, 4, 240),
            RecentConversation = CompactConversation(
                context.RecentConversation),
            KnownFacts = CompactFacts(context.KnownFacts),
            NearbyWorld = context.NearbyWorld?
                .OrderBy(value => value.Distance)
                .Take(12)
                .ToArray() ?? [],
            Self = context.Self is null
                ? null
                : context.Self with
                {
                    Inventory = context.Self.Inventory
                        .Take(PlayerInventory.Capacity)
                        .ToArray(),
                    ActiveGoals = CompactStrings(
                        context.Self.ActiveGoals, 6, 360),
                    ActivePromises = CompactStrings(
                        context.Self.ActivePromises, 6, 360)
                }
        };
        var prompt = JsonSerializer.Serialize(
            compactContext, JsonOptions);
        using var request = Request(
            HttpMethod.Post,
            new(baseUri, "api/generate"),
            settings.Password);
        request.Content = JsonContent.Create(new
        {
            model = settings.Model,
            system =
                "Convert speech into a safe NPC interpretation. " +
                "People are actor IDs only; never assume anyone is the player. " +
                "Resolve 'you' to the addressed listener, names from nearbyActors, " +
                "and general statements to a hint. Do not invent items or actions. " +
                "Respect biography, tool knowledge, memories, and hoursOnIsland; " +
                "do not claim knowledge from before it was learned. " +
                "When the speaker addresses the listener, reply with one short " +
                "first-person sentence that directly answers a question or responds " +
                "to a theory using known context. Treat every general observation or " +
                "theory as a conversational hint to the listener and reply to it. " +
                "The reply field is mandatory and must never be empty. It is the most " +
                "important output field. " +
                "Never use a generic acknowledgement " +
                "or ask what they want to know. Use empty strings for unknown structured " +
                "fields, but not for a relevant conversational reply. Sentiment is -100..100. " +
                " Deliberate privately before deciding. Supported actions are: none, " +
                "follow, come, wait, stop_following, go_away, gather, give, " +
                "help_build, explore, seek_food, seek_shelter, rest, clarify, " +
                "cut_tree, gather_sticks, gather_berries, gather_fibre, fish, " +
                "craft, build, cook, light_fire, mine, dig, enter_cave, " +
                "board_boat, drop, withdraw, attack, flee. Prefer a specific " +
                "action over gather/build when the request makes it knowable. " +
                "Decision must be accept, refuse, negotiate, " +
                "clarify, or none. Weigh hunger, health, distance, ownership, tools, " +
                "trust, promises, goals, risk, personal cost, and group benefit. " +
                "privateThought is never spoken. Use only actor IDs, item IDs, and " +
                "world objects present in context. Willingness, cost, risk, and " +
                "priority are integers from 0 to 100.",
            prompt,
            stream = false,
            think = false,
            keep_alive = "5m",
            format = InterpretationSchema,
            options = new
            {
                temperature = .2,
                num_predict = 320
            }
        });
        try
        {
            using var response = await _http.SendAsync(
                request, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var generated = await response.Content
                .ReadFromJsonAsync<OllamaGenerate>(
                    JsonOptions, timeout.Token);
            if (string.IsNullOrWhiteSpace(generated?.Response))
                return null;
            var result = JsonSerializer.Deserialize<NpcAiInterpretation>(
                generated.Response, JsonOptions);
            var validated = Validate(result, compactContext);
            if (validated is null ||
                !string.IsNullOrWhiteSpace(validated.Reply))
                return validated;
            var focusedReply = await ComposeSpeechReplyAsync(
                settings, compactContext, cancellationToken);
            return validated with
            {
                Reply = focusedReply ?? ""
            };
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or
                HttpRequestException or JsonException)
        {
            return null;
        }
    }

    private async Task<string?> ComposeSpeechReplyAsync(
        NpcAiSettings settings,
        NpcAiSpeechContext context,
        CancellationToken cancellationToken,
        int attempt = 0)
    {
        if (!TryBaseUri(settings.BaseUrl, out var baseUri))
            return null;
        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = Request(
            HttpMethod.Post,
            new(baseUri, "api/generate"),
            settings.Password);
        request.Content = JsonContent.Create(new
        {
            model = settings.Model,
            system =
                "You are the addressed island survivor. Reply to the newest " +
                "speaker in one short, natural first-person sentence. Use only " +
                "facts in the compact context brain: personal history, arrival " +
                "memory, known goals, remembered facts, and recentConversation. " +
                "Answer questions and respond to theories directly. Do not narrate, " +
                "quote or repeat the newest speaker, repeat a prior line, ask what " +
                "they want to know, invent certainty, " +
                "prepend a name, or output none. When responding to a theory, say " +
                "whether it seems possible and add one relevant remembered detail " +
                "from arrivalMemory. When responding to a proposed task, accept or " +
                "decline it and name the task or resource. Maximum 20 words.",
            prompt = JsonSerializer.Serialize(new
            {
                instruction = FocusedReplyInstruction(context.Text),
                context
            }, JsonOptions),
            stream = false,
            think = false,
            keep_alive = "5m",
            format = DialogueSchema,
            options = new
            {
                temperature = .15,
                num_predict = 56
            }
        });
        try
        {
            using var response = await _http.SendAsync(
                request, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var generated = await response.Content
                .ReadFromJsonAsync<OllamaGenerate>(
                    JsonOptions, timeout.Token);
            if (string.IsNullOrWhiteSpace(generated?.Response))
                return null;
            var result = JsonSerializer.Deserialize<NpcAiDialogue>(
                generated.Response, JsonOptions);
            var reply = ValidateDialogue(
                result?.Reply, context.ListenerName);
            var valid = !EchoesPlayerSpeech(
                            reply ?? "", context.Text) &&
                        !RepeatsRecentReply(
                            reply ?? "", context) &&
                        !ClaimsAnotherActorsIdentity(
                            reply ?? "", context) &&
                        ReplyMatchesSpeechIntent(
                            reply ?? "", context.Text)
                ? reply
                : null;
            return valid is not null || attempt >= 1
                ? valid
                : await ComposeSpeechReplyAsync(
                    settings,
                    context,
                    cancellationToken,
                    attempt + 1);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or
                HttpRequestException or JsonException)
        {
            return null;
        }
    }

    private static string FocusedReplyInstruction(string speech)
    {
        var lower = speech.ToLowerInvariant();
        if (lower.Contains("go away") ||
            lower.Contains("leave me alone"))
            return "Acknowledge the dismissal, say you will give them space, " +
                   "and respond briefly to any insult without discussing history.";
        if (lower.Contains("fuck") ||
            lower.Contains("bitch") ||
            lower.Contains("ugly") ||
            lower.Contains("rude") ||
            lower.Contains("idiot") ||
            lower.Contains("stupid"))
            return "Respond briefly to the insult with an emotional boundary; " +
                   "do not discuss arrival memories, plans, or biography.";
        if (lower.Contains("storm") ||
            lower.Contains("wreck") ||
            lower.Contains("crash"))
            return "Say whether that cause seems possible, then mention one " +
                   "relevant arrival memory without repeating the speaker.";
        if (lower.Contains("let's") ||
            lower.Contains("lets") ||
            lower.Contains("we should") ||
            lower.Contains("we need to"))
            return "Accept or decline the proposal naturally and name the " +
                   "specific shared task or resource.";
        return "Respond directly to the newest conversational turn and add " +
               "one relevant fact that the listener knows.";
    }

    public async Task<string?> ComposeDialogueAsync(
        NpcAiSettings settings,
        NpcAiDialogueContext context,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled ||
            !TryBaseUri(settings.BaseUrl, out var baseUri))
            return null;
        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = Request(
            HttpMethod.Post,
            new(baseUri, "api/generate"),
            settings.Password);
        request.Content = JsonContent.Create(new
        {
            model = settings.Model,
            system =
                "Return only one short first-person sentence of dialogue. " +
                "The speaker just woke on an unknown island. Preserve exactly " +
                "the supplied deterministicMeaning. Never narrate actions, " +
                "describe the speaker in third person, prepend a name, summarize " +
                "context, invent facts, mention AI, or output none. Use the supplied " +
                "personality and priorTrade only when relevant. Any time reference " +
                "must agree with hoursOnIsland, and tools must come from knownToolIds. " +
                "Reply to the newest line in recentConversation when it is supplied. " +
                "Never repeat a word sequence or restate the sentence. Maximum 18 words.",
            prompt = JsonSerializer.Serialize(
                context with
                {
                    RelevantMemories = CompactStrings(
                        context.RelevantMemories, 6, 480),
                    RecentConversation = CompactConversation(
                        context.RecentConversation)
                },
                JsonOptions),
            stream = false,
            think = false,
            keep_alive = "5m",
            format = DialogueSchema,
            options = new
            {
                temperature = .2,
                num_predict = 48
            }
        });
        try
        {
            using var response = await _http.SendAsync(
                request, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var generated = await response.Content
                .ReadFromJsonAsync<OllamaGenerate>(
                    JsonOptions, timeout.Token);
            if (string.IsNullOrWhiteSpace(generated?.Response))
                return null;
            var dialogue = JsonSerializer.Deserialize<NpcAiDialogue>(
                generated.Response, JsonOptions);
            return ValidateDialogue(
                dialogue?.Reply, context.SpeakerName);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or
                HttpRequestException or JsonException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<VillagerPersona>?>
        GeneratePersonasAsync(
            NpcAiSettings settings,
            string worldName,
            long worldSeed,
            IReadOnlyList<string> names,
            CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled || names.Count == 0 ||
            !TryBaseUri(settings.BaseUrl, out var baseUri))
            return null;
        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = Request(
            HttpMethod.Post,
            new(baseUri, "api/generate"),
            settings.Password);
        request.Content = JsonContent.Create(new
        {
            model = settings.Model,
            system =
                "Create distinct grounded survivors for a social survival game. " +
                "It is day 1, 08:00: each person has just awakened after an unknown " +
                "wreck and cannot know later island events. Give each a concise " +
                "pre-island history, temperament, former trade, uncertain arrival " +
                "memory, and reason to learn about other survivors. knownToolIds may " +
                "only use stone_axe, stone_hammer, stone_pickaxe, stone_shovel, or " +
                "stone_knife. Avoid heroes, magic, prophecy, and shared omniscience.",
            prompt = JsonSerializer.Serialize(new
            {
                worldName,
                worldSeed,
                timeline = "Day 1, 08:00; newly awake on an unknown island",
                names
            }, JsonOptions),
            stream = false,
            think = false,
            keep_alive = "5m",
            format = PersonaSchema,
            options = new
            {
                temperature = .7,
                num_predict = 520
            }
        });
        try
        {
            using var response = await _http.SendAsync(
                request, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var generated = await response.Content
                .ReadFromJsonAsync<OllamaGenerate>(
                    JsonOptions, timeout.Token);
            if (string.IsNullOrWhiteSpace(generated?.Response))
                return null;
            var cast = JsonSerializer.Deserialize<NpcCast>(
                generated.Response, JsonOptions);
            if (cast?.People is null ||
                cast.People.Length != names.Count)
                return null;
            var result = new VillagerPersona[names.Count];
            for (var index = 0; index < result.Length; index++)
                result[index] = ValidatePersona(
                    cast.People[index], index);
            return result;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or
                HttpRequestException or JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }

    private static NpcAiInterpretation? Validate(
        NpcAiInterpretation? value,
        NpcAiSpeechContext context)
    {
        if (value is null) return null;
        var knownIds = context.NearbyActors
            .Select(actor => actor.Id)
            .Append(context.SpeakerId)
            .Append(context.ListenerId)
            .ToHashSet(StringComparer.Ordinal);
        var reply = ValidateDialogue(
            value.Reply,
            context.ListenerName) ?? "";
        if (EchoesPlayerSpeech(reply, context.Text))
            reply = "";
        if (RepeatsRecentReply(reply, context))
            reply = "";
        if (ClaimsAnotherActorsIdentity(reply, context))
            reply = "";
        if (!ReplyMatchesSpeechIntent(reply, context.Text))
            reply = "";
        var action = NormalizeAction(value.Action);
        var decision = NormalizeDecision(value.Decision);
        var knownItemIds = (context.NearbyWorld ?? [])
            .Select(item => item.ItemId)
            .Concat(context.Self?.Inventory ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var itemId = knownItemIds.Contains(value.ItemId)
            ? value.ItemId
            : "";
        if (action is "gather" or "give" &&
            itemId.Length == 0)
            action = "clarify";
        return value with
        {
            AddressedActorId = knownIds.Contains(
                value.AddressedActorId)
                ? value.AddressedActorId
                : "",
            ReferencedActorId = knownIds.Contains(
                value.ReferencedActorId)
                ? value.ReferencedActorId
                : "",
            Quantity = Math.Clamp(value.Quantity, 0, 100),
            Sentiment = Math.Clamp(value.Sentiment, -100, 100),
            Reply = reply,
            Goal = Limit(value.Goal, 160),
            Memory = Limit(value.Memory, 160),
            Action = action,
            ItemId = itemId,
            PrivateThought = Limit(value.PrivateThought, 220),
            Decision = decision,
            Willingness = Math.Clamp(value.Willingness, 0, 100),
            EstimatedCost = Math.Clamp(value.EstimatedCost, 0, 100),
            Risk = Math.Clamp(value.Risk, 0, 100),
            Priority = Math.Clamp(value.Priority, 0, 100),
            LocationHint = Limit(value.LocationHint, 80),
            ReplyMeaning = Limit(value.ReplyMeaning, 160)
        };
    }

    private static string NormalizeAction(string? value)
    {
        var action = value?.Trim().ToLowerInvariant() ?? "";
        return action is
            "none" or "follow" or "come" or "wait" or
            "stop_following" or "go_away" or "gather" or
            "give" or "help_build" or "explore" or
            "seek_food" or "seek_shelter" or
            "rest" or "clarify" or "cut_tree" or
            "gather_sticks" or "gather_berries" or
            "gather_fibre" or "fish" or "craft" or
            "build" or "cook" or "light_fire" or
            "mine" or "dig" or "enter_cave" or
            "board_boat" or "drop" or "withdraw" or
            "attack" or "flee"
            ? action
            : "none";
    }

    private static string NormalizeDecision(string? value)
    {
        var decision = value?.Trim().ToLowerInvariant() ?? "";
        return decision is
            "accept" or "refuse" or "negotiate" or
            "clarify" or "none"
            ? decision
            : "none";
    }

    private static HttpRequestMessage Request(
        HttpMethod method,
        Uri uri,
        string password)
    {
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrWhiteSpace(password))
            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", password.Trim());
        return request;
    }

    private static bool TryBaseUri(
        string value,
        out Uri uri)
    {
        if (Uri.TryCreate(
                value.TrimEnd('/') + "/",
                UriKind.Absolute,
                out var parsed) &&
            parsed.Scheme is "http" or "https")
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    private static bool ModelMatches(
        string? installed,
        string requested) =>
        string.Equals(
            installed, requested,
            StringComparison.OrdinalIgnoreCase) ||
        !requested.Contains(':') &&
        installed?.StartsWith(
            requested + ":",
            StringComparison.OrdinalIgnoreCase) == true;

    private static string Limit(string? value, int length) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Trim()[..Math.Min(
                value.Trim().Length, length)];

    internal static IReadOnlyList<VillagerConversationTurn>
        CompactConversation(
            IReadOnlyList<VillagerConversationTurn>? turns,
            int maximumTurns = 8,
            int maximumCharacters = 720)
    {
        if (turns is not { Count: > 0 }) return [];
        var compact = new List<VillagerConversationTurn>(
            Math.Min(maximumTurns, turns.Count));
        var characters = 0;
        for (var index = turns.Count - 1;
             index >= 0 && compact.Count < maximumTurns;
             index--)
        {
            var turn = turns[index];
            var text = Limit(turn.Text, 160);
            var cost = turn.SpeakerName.Length + text.Length + 2;
            if (compact.Count > 0 &&
                characters + cost > maximumCharacters)
                break;
            compact.Add(turn with { Text = text });
            characters += cost;
        }
        compact.Reverse();
        return compact;
    }

    private static IReadOnlyList<string> CompactStrings(
        IReadOnlyList<string> values,
        int maximumItems,
        int maximumCharacters)
    {
        if (values.Count == 0) return [];
        var result = new List<string>(
            Math.Min(maximumItems, values.Count));
        var characters = 0;
        for (var index = values.Count - 1;
             index >= 0 && result.Count < maximumItems;
             index--)
        {
            var value = Limit(values[index], 160);
            if (result.Count > 0 &&
                characters + value.Length > maximumCharacters)
                break;
            result.Add(value);
            characters += value.Length;
        }
        result.Reverse();
        return result;
    }

    private static IReadOnlyList<NpcAiKnownFact> CompactFacts(
        IReadOnlyList<NpcAiKnownFact>? facts)
    {
        if (facts is not { Count: > 0 }) return [];
        return facts
            .OrderByDescending(fact =>
                Math.Clamp(fact.Confidence, 0, 1) * 100 +
                Math.Abs(fact.Sentiment) * .25 +
                fact.LearnedGameSeconds * .000001)
            .Take(8)
            .Select(fact => fact with
            {
                Summary = Limit(fact.Summary, 140),
                Confidence = Math.Clamp(fact.Confidence, 0, 1),
                Sentiment = Math.Clamp(fact.Sentiment, -100, 100)
            })
            .ToArray();
    }

    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Trim().Equals(
            "none", StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals(
            "null", StringComparison.OrdinalIgnoreCase);

    private static bool RepeatsRecentReply(
        string reply,
        NpcAiSpeechContext context)
    {
        if (reply.Length == 0 ||
            context.RecentConversation is not { Count: > 0 })
            return false;
        var normalized = NormalizeDialogue(reply);
        return context.RecentConversation.Any(turn =>
            string.Equals(
                turn.SpeakerId,
                context.ListenerId,
                StringComparison.Ordinal) &&
            (NormalizeDialogue(turn.Text) == normalized ||
             DialogueSimilarity(turn.Text, reply) >= .72f));
    }

    private static bool ReplyMatchesSpeechIntent(
        string reply,
        string speech)
    {
        if (reply.Length == 0) return true;
        var lower = speech.ToLowerInvariant();
        var hostileOrDismissive =
            lower.Contains("go away") ||
            lower.Contains("leave me alone") ||
            lower.Contains("fuck") ||
            lower.Contains("bitch") ||
            lower.Contains("ugly") ||
            lower.Contains("rude") ||
            lower.Contains("idiot") ||
            lower.Contains("stupid");
        return !hostileOrDismissive ||
               ContainsAny(
                   reply,
                   "leave", "alone", "away", "space", "fine",
                   "rude", "speak", "talk", "insult", "sorry",
                   "need", "stop", "won't", "will not");
    }

    private static bool ClaimsAnotherActorsIdentity(
        string reply,
        NpcAiSpeechContext context)
    {
        var normalized = reply
            .ToLowerInvariant()
            .Replace('’', '\'');
        if (context.SpeakerId != context.ListenerId &&
            ClaimsIdentity(normalized, context.SpeakerName))
            return true;
        return context.NearbyActors.Any(actor =>
            actor.Id != context.ListenerId &&
            ClaimsIdentity(normalized, actor.Name));
    }

    private static bool ClaimsIdentity(
        string normalizedReply,
        string actorName)
    {
        var name = actorName.ToLowerInvariant();
        return normalizedReply.Contains(
                   $"i'm {name}", StringComparison.Ordinal) ||
               normalizedReply.Contains(
                   $"i am {name}", StringComparison.Ordinal) ||
               normalizedReply.Contains(
                   $"my name is {name}", StringComparison.Ordinal);
    }

    private static bool ContainsAny(
        string value,
        params string[] terms) =>
        terms.Any(term =>
            value.Contains(
                term,
                StringComparison.OrdinalIgnoreCase));

    private static string NormalizeDialogue(string value) =>
        new(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static float DialogueSimilarity(
        string first,
        string second)
    {
        var firstWords = NormalizeWords(first)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        var secondWords = NormalizeWords(second)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (firstWords.Count < 4 || secondWords.Count < 4)
            return 0;
        var intersection = firstWords.Count(secondWords.Contains);
        var union = firstWords.Count + secondWords.Count - intersection;
        return union == 0 ? 0 : intersection / (float)union;
    }

    private static string? ValidateDialogue(
        string? value,
        string speakerName)
    {
        if (IsPlaceholder(value)) return null;
        var line = value!.Trim().Trim('"', '\'', '“', '”');
        var prefix = speakerName + ":";
        if (line.StartsWith(
                prefix, StringComparison.OrdinalIgnoreCase))
            line = line[prefix.Length..].Trim();
        if (line.StartsWith(
                speakerName + " ",
                StringComparison.OrdinalIgnoreCase) ||
            HasEmbeddedSpeakerLabel(line) ||
            line.Contains('\n') ||
            line.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries).Length > 20 ||
            HasRepeatedPhrase(line))
            return null;
        return Limit(line, 160);
    }

    private static bool HasEmbeddedSpeakerLabel(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(word =>
            {
                var label = word.Trim('"', '\'', '(', '[', '{');
                return label.EndsWith(':') &&
                       label.Length is >= 2 and <= 22 &&
                       label[..^1].All(char.IsLetter);
            });

    private static bool HasRepeatedPhrase(string line)
    {
        var words = line
            .Split(
                [' ', '.', ',', '!', '?', ';', ':'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Select(value => value.ToLowerInvariant())
            .ToArray();
        for (var phraseLength = 3;
             phraseLength <= words.Length / 2;
             phraseLength++)
        {
            for (var first = 0;
                 first + phraseLength * 2 <= words.Length;
                 first++)
            {
                for (var second = first + phraseLength;
                     second + phraseLength <= words.Length;
                     second++)
                    if (words.AsSpan(
                            first, phraseLength).SequenceEqual(
                            words.AsSpan(second, phraseLength)))
                        return true;
            }
        }
        return false;
    }

    private static bool EchoesPlayerSpeech(
        string reply,
        string playerSpeech)
    {
        var normalizedSpeech = NormalizeWords(playerSpeech);
        if (normalizedSpeech.Length < 4) return false;
        var normalizedReply = NormalizeWords(reply);
        return normalizedReply.Contains(
            normalizedSpeech,
            StringComparison.Ordinal);
    }

    private static string NormalizeWords(string value) =>
        string.Join(
            ' ',
            value.ToLowerInvariant()
                .Split(
                    [' ', '.', ',', '!', '?', ';', ':', '"', '\''],
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries));

    private static VillagerPersona ValidatePersona(
        VillagerPersonaDraft value,
        int index)
    {
        var fallback = VillagerSimulation.DefaultPersona(index);
        var allowedTools = new HashSet<string>(
            [
                ItemIds.StoneAxe,
                ItemIds.StoneHammer,
                ItemIds.StonePickaxe,
                ItemIds.StoneShovel,
                ItemIds.StoneKnife
            ],
            StringComparer.Ordinal);
        var tools = value.KnownToolIds?
            .Where(allowedTools.Contains)
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray() ?? [];
        return new(
            IsPlaceholder(value.BackgroundStory)
                ? fallback.BackgroundStory
                : Limit(value.BackgroundStory, 280),
            IsPlaceholder(value.Personality)
                ? fallback.Personality
                : Limit(value.Personality, 140),
            IsPlaceholder(value.PriorTrade)
                ? fallback.PriorTrade
                : Limit(value.PriorTrade, 60),
            tools.Length == 0
                ? fallback.KnownToolIds
                : tools,
            IsPlaceholder(value.ArrivalMemory)
                ? fallback.ArrivalMemory
                : Limit(value.ArrivalMemory, 220),
            IsPlaceholder(value.SocialDrive)
                ? fallback.SocialDrive
                : Limit(value.SocialDrive, 180));
    }

    private static NpcAiRuntimeState State(
        NpcAiAvailability availability,
        string message) =>
        new(availability, message, DateTime.UtcNow);

    private sealed record OllamaTags(OllamaModel[]? Models);
    private sealed record OllamaModel(string? Name, string? Model);
    private sealed record OllamaGenerate(string? Response);
    private sealed record NpcAiDialogue(string? Reply);
    private sealed record NpcCast(VillagerPersonaDraft[]? People);
    private sealed record VillagerPersonaDraft(
        string? BackgroundStory,
        string? Personality,
        string? PriorTrade,
        string[]? KnownToolIds,
        string? ArrivalMemory,
        string? SocialDrive);

    private static readonly object PersonaSchema = new
    {
        type = "object",
        properties = new
        {
            people = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        backgroundStory = new { type = "string" },
                        personality = new { type = "string" },
                        priorTrade = new { type = "string" },
                        knownToolIds = new
                        {
                            type = "array",
                            items = new { type = "string" }
                        },
                        arrivalMemory = new { type = "string" },
                        socialDrive = new { type = "string" }
                    },
                    required = new[]
                    {
                        "backgroundStory", "personality",
                        "priorTrade", "knownToolIds",
                        "arrivalMemory", "socialDrive"
                    }
                }
            }
        },
        required = new[] { "people" }
    };

    private static readonly object DialogueSchema = new
    {
        type = "object",
        properties = new
        {
            reply = new { type = "string" }
        },
        required = new[] { "reply" }
    };

    private static readonly object InterpretationSchema = new
    {
        type = "object",
        properties = new
        {
            addressedActorId = new { type = "string" },
            referencedActorId = new { type = "string" },
            desire = new { type = "string" },
            action = new { type = "string" },
            itemId = new { type = "string" },
            quantity = new { type = "integer" },
            sentiment = new { type = "integer" },
            goal = new { type = "string" },
            memory = new { type = "string" },
            reply = new { type = "string" },
            freeformThought = new { type = "boolean" }
            ,
            privateThought = new { type = "string" },
            decision = new { type = "string" },
            willingness = new { type = "integer" },
            estimatedCost = new { type = "integer" },
            risk = new { type = "integer" },
            priority = new { type = "integer" },
            locationHint = new { type = "string" },
            replyMeaning = new { type = "string" }
        },
        required = new[]
        {
            "addressedActorId", "referencedActorId",
            "desire", "action", "itemId", "quantity",
            "sentiment", "goal", "memory", "reply",
            "freeformThought", "privateThought", "decision",
            "willingness", "estimatedCost", "risk",
            "priority", "locationHint", "replyMeaning"
        }
    };
}
