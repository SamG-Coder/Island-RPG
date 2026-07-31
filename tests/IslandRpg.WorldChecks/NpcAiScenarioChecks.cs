using System.Net;
using System.Text;
using System.Text.Json;
using IslandRpg.Gameplay;
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

        if (passed != 30)
            throw new InvalidOperationException(
                $"Expected 30 NPC AI scenarios, passed {passed}.");
        Console.WriteLine(
            "NPC AI scenario matrix passed: 30/30.");
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
                []));
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
