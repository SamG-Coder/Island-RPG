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
    double HoursOnIsland = 0);

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
    bool FreeformThought);

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
    double HoursOnIsland = 0);

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
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var prompt = JsonSerializer.Serialize(context, JsonOptions);
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
                "Use empty strings when unknown. Sentiment is -100..100.",
            prompt,
            stream = false,
            think = false,
            keep_alive = "5m",
            format = InterpretationSchema,
            options = new
            {
                temperature = .2,
                num_predict = 180
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
            return Validate(result, context);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or
                HttpRequestException or JsonException)
        {
            return null;
        }
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
                "Never repeat a word sequence or restate the sentence. Maximum 18 words.",
            prompt = JsonSerializer.Serialize(context, JsonOptions),
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
            Memory = Limit(value.Memory, 160)
        };
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

    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Trim().Equals(
            "none", StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals(
            "null", StringComparison.OrdinalIgnoreCase);

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
            line.Contains('\n') ||
            line.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries).Length > 20 ||
            HasRepeatedPhrase(line))
            return null;
        return Limit(line, 160);
    }

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
        },
        required = new[]
        {
            "addressedActorId", "referencedActorId",
            "desire", "action", "itemId", "quantity",
            "sentiment", "goal", "memory", "reply",
            "freeformThought"
        }
    };
}
