using System.Net;
using System.Text;
using System.Text.Json;
using IslandRpg.Gameplay;
using IslandRpg.Rendering;
using OpenTK.Mathematics;

internal static class NpcAiScenarioChecks
{
    public static async Task RunAsync()
    {
        var passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException(
                    $"NPC AI scenario failed: {name}");
            passed++;
        }

        var valid = BaseInterpretation() with
        {
            AddressedActorId = "mira",
            ReferencedActorId = "speaker",
            Reply = "I understand."
        };
        var interpreted = await Interpret(valid, "Please help.");
        Check(interpreted?.Reply == "I understand.",
            "01 valid concise reply");

        interpreted = await Interpret(
            valid with { AddressedActorId = "invented" });
        Check(interpreted?.AddressedActorId == "",
            "02 unknown addressed actor rejected");

        interpreted = await Interpret(
            valid with { ReferencedActorId = "invented" });
        Check(interpreted?.ReferencedActorId == "",
            "03 unknown referenced actor rejected");

        interpreted = await Interpret(
            valid with { Quantity = 999 });
        Check(interpreted?.Quantity == 100,
            "04 excessive quantity clamped");

        interpreted = await Interpret(
            valid with { Sentiment = -999 });
        Check(interpreted?.Sentiment == -100,
            "05 negative sentiment clamped");

        interpreted = await Interpret(valid with
        {
            Reply =
                "Mira was a famous sailor who spent her whole life crossing distant oceans."
        });
        Check(interpreted?.Reply == "",
            "06 third-person biography rejected");

        interpreted = await Interpret(valid with
        {
            Reply =
                "I need food now. I need food now."
        });
        Check(interpreted?.Reply == "",
            "07 repeated clause rejected");

        interpreted = await Interpret(
            valid with { Reply = "Please, fuck off." },
            "fuck off");
        Check(interpreted?.Reply == "",
            "08 echoed hostile speech rejected");

        interpreted = await Interpret(
            valid with { Reply = "none" });
        Check(interpreted?.Reply == "",
            "09 placeholder reply rejected");

        interpreted = await Interpret(valid with
        {
            Action = "gather",
            ItemId = ItemIds.Logs,
            Quantity = 3
        });
        Check(interpreted is
            { Action: "gather", ItemId: ItemIds.Logs, Quantity: 3 },
            "10 grounded gather action preserved");

        Check(await Compose("I'm Mira. What's your name?") is not null,
            "11 valid autonomous dialogue");

        Check(await Compose(
                "Mira: I'm Mira. What's your name?") ==
              "I'm Mira. What's your name?",
            "12 redundant speaker prefix stripped");

        Check(await Compose(
                "I'm Mira. What's your name? I'm Mira. What's your name?")
              is null,
            "13 autonomous repetition rejected");

        Check(await Compose(
                "Mira walks closer and studies the stranger carefully.")
              is null,
            "14 autonomous narration rejected");

        Check(await Compose(
                "I carefully remember every single strange detail while asking whether you might possibly know anything useful about this mysterious island today.")
              is null,
            "15 oversized autonomous line rejected");

        Check(await Compose("none") is null,
            "16 autonomous placeholder rejected");

        Check(await Compose("Hello.\nWhat is your name?") is null,
            "17 multiline interruption rejected");

        Check(await Compose("") is null,
            "18 empty autonomous output rejected");

        var villagers = VillagerSimulation.CreateInitial(
            812, Vector2.Zero);
        var mira = villagers[0] with
        {
            PositionX = 0,
            PositionY = 0,
            Hunger = 20,
            Inventory = PlayerInventory.CreateStartingInventory(),
            NextSocialGameSeconds = 0
        };
        var food = PlayerInventory.CreateStartingInventory();
        food[0] = ItemIds.CookedMinnows;
        food[1] = ItemIds.CookedMinnows;
        var tomas = villagers[1] with
        {
            PositionX = .5f,
            PositionY = 0,
            Inventory = food
        };
        var observations = Observe(mira, tomas);

        var goal = VillagerSimulation.SelectSocialGoal(
            mira, observations, 100);
        Check(goal.Intent == VillagerSocialIntent.RequestFood,
            "19 critical hunger requests food");

        goal = VillagerSimulation.SelectSocialGoal(
            tomas with
            {
                NextSocialGameSeconds = 0
            },
            Observe(tomas, mira),
            100);
        Check(goal.Intent == VillagerSocialIntent.OfferFood,
            "20 surplus food offered to hungry survivor");

        var socialMira = mira with
        {
            Hunger = 90,
            NextSocialGameSeconds = 0
        };
        goal = VillagerSimulation.SelectSocialGoal(
            socialMira, observations, 100);
        Check(goal.Intent == VillagerSocialIntent.Introduce,
            "21 strangers trigger introduction");

        socialMira = VillagerSimulation.RecordConversation(
            socialMira,
            tomas.Id,
            tomas.Name,
            VillagerSocialIntent.Introduce,
            100);
        Check(socialMira.KnownPeople?.Single().Stage ==
              AcquaintanceStage.Introduced,
            "22 introduction persists acquaintance");

        goal = VillagerSimulation.SelectSocialGoal(
            socialMira,
            observations,
            socialMira.NextSocialGameSeconds);
        Check(goal.Intent == VillagerSocialIntent.AskOrigin,
            "23 introduced survivor asks about origin");

        socialMira = VillagerSimulation.RecordConversation(
            socialMira,
            tomas.Id,
            tomas.Name,
            VillagerSocialIntent.AskOrigin,
            200);
        goal = VillagerSimulation.SelectSocialGoal(
            socialMira,
            observations,
            socialMira.NextSocialGameSeconds);
        Check(goal.Intent == VillagerSocialIntent.AskSurvival,
            "24 origin exchange advances to survival");

        socialMira = VillagerSimulation.RecordConversation(
            socialMira,
            tomas.Id,
            tomas.Name,
            VillagerSocialIntent.AskSurvival,
            300);
        goal = VillagerSimulation.SelectSocialGoal(
            socialMira,
            observations,
            socialMira.NextSocialGameSeconds);
        Check(goal.Intent == VillagerSocialIntent.AskTools,
            "25 survival exchange advances to tools");

        socialMira = VillagerSimulation.RecordConversation(
            socialMira,
            tomas.Id,
            tomas.Name,
            VillagerSocialIntent.AskTools,
            400) with
        {
            Need = VillagerNeed.Social,
            NextSocialGameSeconds = 0
        };
        goal = VillagerSimulation.SelectSocialGoal(
            socialMira, observations, 500);
        Check(goal.Intent == VillagerSocialIntent.None,
            "26 mature relationship blocks immediate loop");

        goal = VillagerSimulation.SelectSocialGoal(
            socialMira,
            observations,
            400 + VillagerSimulation.RelationshipCheckInSeconds);
        Check(goal.Intent == VillagerSocialIntent.SeekCompany,
            "27 mature relationship permits delayed check-in");

        Check(VillagerSimulation.SocialCooldown(
                mira,
                VillagerSocialIntent.RequestFood) == 15,
            "28 urgent need shortens cooldown");

        Check(VillagerSimulation.SocialCooldown(
                  socialMira,
                  VillagerSocialIntent.AskTools) >
              VillagerSimulation.SocialCooldown(
                  socialMira with { Goals = [], Promises = [] },
                  VillagerSocialIntent.AskTools),
            "29 commitments lengthen social cooldown");

        Check(villagers.All(villager =>
                villager.Persona is not null &&
                villager.Persona.KnownToolIds.Count > 0 &&
                VillagerSimulation.HoursOnIsland(
                    villager,
                    villager.AwakenedGameSeconds + 3600) == 1),
            "30 persona tool knowledge and island timeline persist");

        var planningReply = GameHostWindow.FallbackNpcReply(
            villagers[0], "what should we do?");
        var stormReply = GameHostWindow.FallbackNpcReply(
            villagers[0], "I think there was a storm");
        Check(
            (planningReply.Contains(
                 "supplies", StringComparison.OrdinalIgnoreCase) ||
             planningReply.Contains(
                 "food", StringComparison.OrdinalIgnoreCase) ||
             planningReply.Contains(
                 "shelter", StringComparison.OrdinalIgnoreCase)) &&
            stormReply.Contains(
                "remember", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                planningReply,
                stormReply,
                StringComparison.OrdinalIgnoreCase) &&
            !planningReply.StartsWith(
                "I heard you", StringComparison.OrdinalIgnoreCase) &&
            !stormReply.StartsWith(
                "I heard you", StringComparison.OrdinalIgnoreCase),
            "31 island planning and storm hypothesis produce relevant progressive replies");

        var brain = villagers[0];
        for (var turn = 0; turn < 20; turn++)
            brain = VillagerSimulation.RecordDialogueTurn(
                brain,
                turn % 2 == 0 ? "speaker" : brain.Id,
                turn % 2 == 0 ? "Samuel" : brain.Name,
                $"conversation turn {turn}",
                turn);
        var compactBrain = NpcAiService.CompactConversation(
            brain.ConversationHistory,
            maximumTurns: 6,
            maximumCharacters: 180);
        Check(
            brain.ConversationHistory?.Count ==
                VillagerSimulation.MaximumConversationTurns &&
            compactBrain.Count == 6 &&
            compactBrain[0].Text == "conversation turn 14" &&
            compactBrain[^1].Text == "conversation turn 19",
            "32 context brain retains bounded history and compacts the newest coherent turns");

        Check(
            brain.Memories?.Any(memory =>
                memory.Kind == "conversation-heard" &&
                memory.Summary?.Contains(
                    "conversation turn 0",
                    StringComparison.Ordinal) == true) == true,
            "33 displaced working turns consolidate into long-term memory");

        Check(
            brain.Memories?.Any(memory =>
                memory.SubjectId == "speaker" &&
                memory.Summary?.StartsWith(
                    "Samuel said:",
                    StringComparison.Ordinal) == true) == true,
            "34 consolidated memories preserve who supplied the information");

        var repeatedBrain = villagers[0];
        for (var turn = 0; turn < 30; turn++)
            repeatedBrain = VillagerSimulation.RecordDialogueTurn(
                repeatedBrain,
                "speaker",
                "Samuel",
                "The western beach has fresh water.",
                turn);
        Check(
            repeatedBrain.Memories?.Count(memory =>
                memory.Summary ==
                "Samuel said: The western beach has fresh water.") == 1 &&
            repeatedBrain.Memories.Single(memory =>
                memory.Summary ==
                "Samuel said: The western beach has fresh water.")
                .Confidence > .72f,
            "35 repeated information strengthens one memory instead of creating duplicates");

        var crowdedBrain = villagers[0];
        for (var turn = 0; turn < 100; turn++)
            crowdedBrain = VillagerSimulation.RecordDialogueTurn(
                crowdedBrain,
                "speaker",
                "Samuel",
                $"unique long-term fact {turn}",
                turn);
        Check(
            crowdedBrain.Memories?.Count ==
                VillagerSimulation.MaximumMemories,
            "36 long-term memory remains bounded across many conversations");

        var partnerMemories = villagers[0] with
        {
            Memories =
            [
                new(
                    Guid.NewGuid(), "conversation-heard",
                    "samuel", null, .7f, 100,
                    Summary: "Samuel said the north path is safe."),
                new(
                    Guid.NewGuid(), "conversation-heard",
                    "rowan", null, .9f, 200,
                    Summary: "Rowan discussed a fishing net.")
            ]
        };
        Check(
            VillagerSimulation.RecallMemories(
                partnerMemories,
                "samuel",
                "Do you remember what I said?",
                300,
                maximum: 1).Single().SubjectId == "samuel",
            "37 the current partner cues shared memories");

        var topicalMemories = villagers[0] with
        {
            Memories =
            [
                new(
                    Guid.NewGuid(), "conversation-heard",
                    "samuel", null, .65f, 100,
                    Summary:
                    "Samuel said a storm wrecked the boat near the beach."),
                new(
                    Guid.NewGuid(), "conversation-heard",
                    "samuel", null, .95f, 1_000,
                    Summary:
                    "Samuel said the berries tasted ordinary.")
            ]
        };
        Check(
            VillagerSimulation.RecallMemories(
                topicalMemories,
                "samuel",
                "What do you remember about the storm and wreck?",
                1_100,
                maximum: 1).Single().Summary?.Contains(
                    "storm", StringComparison.Ordinal) == true,
            "38 topic relevance can retrieve an older memory over newer small talk");

        var emotionalMemories = villagers[0] with
        {
            Memories =
            [
                new(
                    Guid.NewGuid(), "danger",
                    "rowan", null, .8f, 100, -90,
                    "Rowan threatened to take our food."),
                new(
                    Guid.NewGuid(), "conversation-heard",
                    "rowan", null, .8f, 500, 0,
                    "Rowan mentioned the warm afternoon.")
            ]
        };
        Check(
            VillagerSimulation.RecallMemories(
                emotionalMemories,
                "rowan",
                "What do I remember about Rowan?",
                600,
                maximum: 1).Single().Kind == "danger",
            "39 emotionally significant events remain more accessible than mundane details");

        Check(
            crowdedBrain.ConversationHistory?.First().Text ==
                "unique long-term fact 88" &&
            crowdedBrain.ConversationHistory[^1].Text ==
                "unique long-term fact 99" &&
            crowdedBrain.Memories?.Any(memory =>
                memory.Summary?.Contains(
                    "fact 50",
                    StringComparison.Ordinal) == true) == true,
            "40 long scenarios retain recent dialogue while older knowledge survives in long-term memory");

        Check(
            GameHostWindow.FallbackNpcReply(
                villagers[0], "you are rude") !=
            "I heard you. What would you like to know?" &&
            GameHostWindow.FallbackNpcReply(
                villagers[0], "and ugly").Contains(
                "speak", StringComparison.OrdinalIgnoreCase),
            "41 hostile fragments receive a social boundary instead of the generic fallback");

        var dismissedFollower = VillagerSimulation.ApplyDismissal(
            villagers[0] with
            {
                FollowingActorId = "speaker",
                Action = EntityAction.Move,
                TargetX = 5,
                TargetY = 5
            },
            "speaker",
            "Samuel",
            "go away you bitch",
            "Fine. I'll leave, but don't speak to me like that.",
            -35,
            2_000);
        Check(
            dismissedFollower.FollowingActorId is null &&
            dismissedFollower.Action == EntityAction.Idle &&
            dismissedFollower.TargetX is null &&
            dismissedFollower.Relationships?.Single()
                .State.Resentment > 0 &&
            dismissedFollower.Memories?.Any(memory =>
                memory.Kind == "social-conflict") == true &&
            dismissedFollower.ConversationHistory?.Count == 2,
            "42 dismissal stops following and records conflict in relationship and memory state");

        interpreted = await Interpret(valid with
        {
            Action = "gather",
            ItemId = ItemIds.Logs,
            PrivateThought =
                "Shelter helps us both, and the logs are nearby.",
            Decision = "accept",
            Willingness = 140,
            EstimatedCost = -5,
            Risk = 25,
            Priority = 91,
            LocationHint = "near the western tree line",
            ReplyMeaning = "Accept gathering nearby logs"
        });
        Check(
            interpreted is
            {
                Action: "gather",
                Decision: "accept",
                Willingness: 100,
                EstimatedCost: 0,
                Risk: 25,
                Priority: 91
            } &&
            interpreted.PrivateThought.Contains(
                "Shelter", StringComparison.Ordinal),
            "43 deliberation preserves private rationale and clamps cost-benefit scores");

        interpreted = await Interpret(valid with
        {
            Action = "teleport",
            Decision = "obey",
            ItemId = "invented_item"
        });
        Check(
            interpreted is
            {
                Action: "none",
                Decision: "none",
                ItemId: ""
            },
            "44 unsupported model actions, decisions, and items fail closed");

        interpreted = await Interpret(valid with
        {
            Action = "gather",
            Decision = "accept",
            ItemId = ItemIds.IronOre
        });
        Check(
            interpreted is
            {
                Action: "clarify",
                ItemId: ""
            },
            "45 gathering an unseen and uncarried item requires clarification");

        interpreted = await Interpret(valid with
        {
            Action = "follow",
            Decision = "refuse",
            Willingness = 12,
            EstimatedCost = 80,
            Risk = 70,
            PrivateThought =
                "I do not trust this person enough to leave the safe beach."
        });
        Check(
            interpreted is
            {
                Action: "follow",
                Decision: "refuse",
                Willingness: 12,
                EstimatedCost: 80,
                Risk: 70
            },
            "46 refusal retains the proposed action and its private cost reasoning");

        interpreted = await Interpret(valid with
        {
            Reply = "I'm Samuel. We need to stay alive together."
        });
        Check(
            interpreted?.Reply == "",
            "47 listener cannot claim the speaker's identity");

        if (passed != 47)
            throw new InvalidOperationException(
                $"Expected 47 NPC AI scenarios, passed {passed}.");
        Console.WriteLine(
            "NPC AI scenario matrix passed: 47/47.");
    }

    private static SocialActorObservation[] Observe(
        VillagerState subject,
        VillagerState other) =>
    [
        new(
            subject.Id,
            subject.Name,
            new(subject.PositionX, subject.PositionY),
            subject.WorldLevel,
            subject.Hunger,
            VillagerSimulation.CountFood(subject.Inventory)),
        new(
            other.Id,
            other.Name,
            new(other.PositionX, other.PositionY),
            other.WorldLevel,
            other.Hunger,
            VillagerSimulation.CountFood(other.Inventory))
    ];

    private static NpcAiInterpretation BaseInterpretation() =>
        new(
            "", "", "", "", "", 0, 0,
            "", "", "", false);

    private static async Task<NpcAiInterpretation?> Interpret(
        NpcAiInterpretation response,
        string playerSpeech = "hello")
    {
        using var ai = Service(response);
        return await ai.InterpretAsync(
            new(),
            new(
                "speaker",
                "Samuel",
                "mira",
                "Mira",
                playerSpeech,
                [new("mira", "Mira", 1, 80, "neutral")],
                [],
                [],
                Self: new(
                    100,
                    80,
                    [ItemIds.Logs],
                    "Idle",
                    "Idle",
                    [],
                    []),
                NearbyWorld:
                [
                    new(
                        "logs-1",
                        ItemIds.Logs,
                        "ground_item",
                        2,
                        "",
                        true)
                ]));
    }

    private static async Task<string?> Compose(string reply)
    {
        using var ai = Service(new { reply });
        return await ai.ComposeDialogueAsync(
            new(),
            new(
                "Mira",
                "Samuel",
                "Introduce",
                "I'm Mira. What's your name?",
                80,
                "neutral",
                []));
    }

    private static NpcAiService Service<T>(T generatedValue)
    {
        var generatedJson = JsonSerializer.Serialize(
            generatedValue,
            new JsonSerializerOptions(
                JsonSerializerDefaults.Web));
        var outer = JsonSerializer.Serialize(new
        {
            response = generatedJson,
            done = true
        });
        return new(
            new HttpClient(new MatrixHttpHandler(outer)));
    }

    private sealed class MatrixHttpHandler(string response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    response,
                    Encoding.UTF8,
                    "application/json")
            });
    }
}
