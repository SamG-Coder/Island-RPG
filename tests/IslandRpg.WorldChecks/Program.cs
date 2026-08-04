using IslandRpg.World;
using IslandRpg.Assets;
using IslandRpg.Audio;
using IslandRpg.Gameplay;
using IslandRpg.Persistence;
using IslandRpg.Rendering;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;
using StbImageSharp;

WorldCheckProcess.DisableWindowsCrashDialogs();

var slimeExportIndex = Array.FindIndex(
    args, value => value.Equals(
        "--export-slime-rig", StringComparison.OrdinalIgnoreCase));
if (slimeExportIndex >= 0)
{
    if (slimeExportIndex + 1 >= args.Length)
        throw new ArgumentException(
            "--export-slime-rig requires an output directory.");
    var combatDirectory = Path.Combine(
        AppContext.BaseDirectory, "Resources", "Images", "Combat");
    SlimeSpriteRig.Load(
            Path.Combine(combatDirectory, "slime-sprites.png"),
            Path.Combine(combatDirectory, "slime-sprites-back.png"))
        .ExportMovementPreview(args[slimeExportIndex + 1]);
    return;
}

if (args.Contains("--ai-score", StringComparer.OrdinalIgnoreCase))
{
    var modelIndex = Array.FindIndex(args, value => value == "--model");
    var model = modelIndex >= 0 && modelIndex + 1 < args.Length
        ? args[modelIndex + 1]
        : new GameSettings().EffectiveAi.Model;
    Environment.ExitCode = await NpcAiModelScore.RunAsync(model) ? 0 : 1;
    return;
}

if (args.Contains(
        "--live-arrival-scenario",
        StringComparer.OrdinalIgnoreCase))
{
    var modelIndex = Array.FindIndex(
        args, value => value == "--model");
    var model = modelIndex >= 0 &&
                modelIndex + 1 < args.Length
        ? args[modelIndex + 1]
        : new GameSettings().EffectiveAi.Model;
    Environment.ExitCode =
        await RunLiveArrivalScenario(model) ? 0 : 1;
    return;
}

if (args.Contains(
        "--live-ai-contract",
        StringComparer.OrdinalIgnoreCase))
{
    var modelIndex = Array.FindIndex(
        args, value => value == "--model");
    var model = modelIndex >= 0 &&
                modelIndex + 1 < args.Length
        ? args[modelIndex + 1]
        : new GameSettings().EffectiveAi.Model;
    Environment.ExitCode =
        await RunLiveAiContract(model) ? 0 : 1;
    return;
}

var worldCheckAssertions = 0;
var legacyStackInventory = new string?[PlayerInventory.Capacity];
legacyStackInventory[0] = ItemIds.SlimeGel;
legacyStackInventory[1] = ItemIds.SlimeGel;
legacyStackInventory[2] = ItemIds.PlantFibres;
legacyStackInventory[3] = ItemIds.PlantFibres;
legacyStackInventory[4] = ItemIds.StoneAxe;
var migratedStackInventory = PlayerInventory.Load(legacyStackInventory);
Require(
    migratedStackInventory.Count(ItemIds.SlimeGel) == 2 &&
    migratedStackInventory.Count(ItemIds.PlantFibres) == 2 &&
    migratedStackInventory.ItemCount == 5 &&
    migratedStackInventory.UsedSlots == 4 &&
    migratedStackInventory.Quantities().Contains(2),
    "legacy migration must stack slime drops while keeping ordinary resources in separate slots");
var roundTrippedStackInventory = PlayerInventory.Load(
    migratedStackInventory.ItemIds(),
    migratedStackInventory.Quantities());
Require(
    roundTrippedStackInventory.Count(ItemIds.SlimeGel) == 2 &&
    roundTrippedStackInventory.Count(ItemIds.PlantFibres) == 2 &&
    roundTrippedStackInventory.Count(ItemIds.StoneAxe) == 1,
    "player inventory quantity saves must round-trip stack and non-stack counts");
var partialHarvestInventory = new InventoryContainer(3);
partialHarvestInventory.TryAdd(ItemIds.StoneAxe);
Require(
    partialHarvestInventory.AddUpTo(ItemIds.WildBerries, 3) == 2 &&
    partialHarvestInventory.Count(ItemIds.WildBerries) == 2 &&
    partialHarvestInventory.UsedSlots == 3,
    "world harvesting must collect the quantity that fits instead of rejecting the whole yield");
var stackHarvestInventory = new InventoryContainer(1);
stackHarvestInventory.TryAdd(ItemIds.SlimeGel, 2);
Require(
    stackHarvestInventory.AddUpTo(ItemIds.SlimeGel, 5) == 5 &&
    stackHarvestInventory.Count(ItemIds.SlimeGel) == 7 &&
    stackHarvestInventory.UsedSlots == 1,
    "partial harvest collection must preserve stackable-item capacity");
var craftingStacks = PlayerInventory.CreateContainer();
craftingStacks.TryAdd(ItemIds.PlantFibres, 5);
var stackRopeRecipe = CraftingSkill.Recipes.Single(
    recipe => recipe.Id == "rope");
var stackedCraftResult = CraftingService.TryCraftDetailed(
    stackRopeRecipe, 1, craftingStacks, out var craftedStacks);
Require(
    stackedCraftResult == CraftingService.CraftResult.Success &&
    craftedStacks.Count(ItemIds.PlantFibres) == 2 &&
    craftedStacks.Count(ItemIds.Rope) == 1 &&
    craftedStacks.ItemCount == 3,
    "crafting must consume quantities from stacks and conserve the recipe result");
var alternativePortableTorchRecipe = CraftingSkill.Recipes.Single(
    recipe => recipe.Id == "portable-torch");
var preferredIngredientInventory = PlayerInventory.CreateContainer();
preferredIngredientInventory.TryAdd(ItemIds.SlimeGel, 4);
preferredIngredientInventory.TryAdd(ItemIds.PlantFibres, 3);
preferredIngredientInventory.TryAdd(ItemIds.Sticks);
preferredIngredientInventory.TryAdd(ItemIds.Charcoal);
Require(
    CraftingService.TryCraftDetailed(
        alternativePortableTorchRecipe,
        alternativePortableTorchRecipe.RequiredLevel,
        preferredIngredientInventory,
        out var preferredIngredientResult) ==
    CraftingService.CraftResult.Success &&
    preferredIngredientResult.Count(ItemIds.PlantFibres) == 1 &&
    preferredIngredientResult.Count(ItemIds.SlimeGel) == 4 &&
    preferredIngredientResult.Count(ItemIds.PortableTorch) == 1,
    "crafting must preserve alternatives when enough named ingredients are carried");
Require(
    CraftingService.TryCraft(
        alternativePortableTorchRecipe,
        alternativePortableTorchRecipe.RequiredLevel,
        [
            ItemIds.SlimeGel, ItemIds.SlimeGel,
            ItemIds.PlantFibres, ItemIds.Sticks, ItemIds.Charcoal
        ],
        out var mixedAlternativeResult) &&
    mixedAlternativeResult.Count(item => item == ItemIds.PlantFibres) == 0 &&
    mixedAlternativeResult.Count(item => item == ItemIds.SlimeGel) == 1 &&
    mixedAlternativeResult.Count(item => item == ItemIds.PortableTorch) == 1,
    "crafting must use named ingredients before only the alternatives needed to cover a shortage");
var playerTransferInventory = PlayerInventory.CreateContainer();
playerTransferInventory.TryAdd(ItemIds.SlimeGel, 7);
var stackTransferContainer = new ItemContainerState(
    new(
        Guid.NewGuid(), "Stack transfer", 2, 2,
        AllowStacking: true));
var transferredStackItems = stackTransferContainer.TransferMatchingFrom(
    playerTransferInventory, ItemIds.SlimeGel, 5);
Require(
    transferredStackItems == 5 &&
    playerTransferInventory.Count(ItemIds.SlimeGel) == 2 &&
    stackTransferContainer.Quantities.Sum() == 5,
    "container transfers must move requested stack quantities without duplication");
var wrappedChat = ChatTextLayout.Wrap(
    "one two three four", 9, text => text.Length);
var chatLayout = new ChatUiControlState();
chatLayout.Configure(
    ChatDisplaySize.Medium, true, 16, text => text.Length);
chatLayout.Layout(new(0, 0, 700, 500));
chatLayout.AddMessage(
    "one two three four", ChatMessageStyle.Debug);
Require(
    wrappedChat.SequenceEqual(["one two", "three", "four"]) &&
    chatLayout.VisibleRows == 12 &&
    chatLayout.DisplayLines.Count == 1,
    "chat layout must expose medium row capacity and reusable word wrapping");
chatLayout.Configure(
    ChatDisplaySize.Large, false, 16, text => text.Length);
Require(
    chatLayout.VisibleRows == 16 &&
    chatLayout.DisplayLines.Single().Text == "one two three four" &&
    chatLayout.DisplayLines.Single().Style == ChatMessageStyle.Debug,
    "large chat layout must preserve unwrapped debug messages and their style");
var historyChat = new ChatUiControlState();
for (var index = 0; index < 12; index++)
    historyChat.AddMessage($"history {index}", ChatMessageStyle.Npc);
historyChat.AddMessage("Codex: state", ChatMessageStyle.Debug);
var historyReader = new ChatHistoryReader();
var recentHistory = historyReader.Read(
    historyChat.Messages, ChatHistoryScope.Last10);
var emptyUnreadHistory = historyReader.Read(
    historyChat.Messages, ChatHistoryScope.Unread);
historyChat.AddMessage("Codex: nearby", ChatMessageStyle.Debug);
historyChat.AddMessage("new reply", ChatMessageStyle.Npc);
var unreadHistory = historyReader.Read(
    historyChat.Messages, ChatHistoryScope.Unread);
Require(
    recentHistory.Messages.Count == 10 &&
    recentHistory.Messages[0].Text == "history 2" &&
    recentHistory.Messages.All(message =>
        message.Style != ChatMessageStyle.Debug) &&
    emptyUnreadHistory.Messages.Count == 0 &&
    unreadHistory.Messages.Count == 1 &&
    unreadHistory.Messages[0].Text == "new reply" &&
    ChatHistoryReader.TryParseScope(
        "not read", out var parsedHistoryScope) &&
    parsedHistoryScope == ChatHistoryScope.Unread,
    "control chat history must exclude debug lines and maintain an independent unread cursor");
var worldSessionChat = new ChatUiControlState();
var worldSessionReader = new ChatHistoryReader();
worldSessionChat.AddMessage("old world event", ChatMessageStyle.Action);
worldSessionReader.Read(worldSessionChat.Messages, ChatHistoryScope.All);
worldSessionChat.AddMessage("late old-world event", ChatMessageStyle.Action);
worldSessionChat.ClearMessages();
worldSessionChat.AddMessage("new world event", ChatMessageStyle.Action);
var newWorldUnread = worldSessionReader.Read(
    worldSessionChat.Messages, ChatHistoryScope.Unread);
Require(
    worldSessionChat.Messages.Count == 1 &&
    newWorldUnread.Messages.Count == 1 &&
    newWorldUnread.Messages[0].Text == "new world event",
    "new world chat reset must remove stale messages without breaking unread tracking");
var stateHistoryReader = new ChatHistoryReader();
var stateUnreadHistory = stateHistoryReader.Read(
    historyChat.Messages, ChatHistoryScope.Unread);
Require(
    stateUnreadHistory.Messages.Count == 13 &&
    stateUnreadHistory.Messages[^1].Text == "new reply",
    "state polling must maintain an unread chat cursor independent from explicit chat-history reads");
using (var publishedMessages = new GameControlPipe(
           $"world-check-published-{Guid.NewGuid():N}"))
{
    publishedMessages.Publish(new
    {
        eventType = "player_message",
        text = "meet me at the shore"
    });
    Require(
        publishedMessages.DrainStatePublished().Single()
            .GetProperty("text").GetString() == "meet me at the shore" &&
        publishedMessages.DrainPublished().Single()
            .GetProperty("eventType").GetString() == "player_message" &&
        publishedMessages.DrainStatePublished().Length == 0 &&
        publishedMessages.DrainPublished().Length == 0,
        "state and events commands must receive independent unread /codex message streams");
}
Require(
    ChatCommandRegistry.TryParse(
        "/codex inspect the western shore", out var codexCommand) &&
    codexCommand.Definition.Name == "/codex" &&
    codexCommand.Arguments.SequenceEqual(
        ["inspect", "the", "western", "shore"]),
    "the player must be able to publish free-form messages through /codex");
Require(
    ControlModalCommands.CanRespawn(true, ModalScreenKind.Death) &&
    !ControlModalCommands.CanRespawn(false, ModalScreenKind.Death) &&
    !ControlModalCommands.CanRespawn(
        true, ModalScreenKind.QuestComplete),
    "the control pipe must expose normal respawning only while a defeated player has the death modal open");
Require(
    PlayerDeathService.IsDefeated(0) &&
    PlayerDeathService.IsDefeated(-1) &&
    !PlayerDeathService.IsDefeated(1),
    "persisted zero-health players must restore the normal defeated lifecycle instead of loading as actionable actors");
var screenshotCheckPath = Path.Combine(
    Path.GetTempPath(), $"island-rpg-shot-{Guid.NewGuid():N}.png");
try
{
    PngScreenshotWriter.Write(
        screenshotCheckPath,
        [
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255
        ],
        2, 2, flipVertically: true);
    using var screenshotStream = File.OpenRead(screenshotCheckPath);
    var screenshotImage = ImageResult.FromStream(
        screenshotStream, ColorComponents.RedGreenBlueAlpha);
    Require(
        screenshotImage.Width == 2 && screenshotImage.Height == 2 &&
        screenshotImage.Data[0] == 0 &&
        screenshotImage.Data[1] == 0 &&
        screenshotImage.Data[2] == 255,
        "control-pipe screenshots must be valid vertically corrected PNG files");
}
finally
{
    File.Delete(screenshotCheckPath);
}
var observeSummaryDirectory = Path.Combine(
    Path.GetTempPath(), "IslandRpg.WorldChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(observeSummaryDirectory);
try
{
    var summaryPath = Path.Combine(observeSummaryDirectory, "observe");
    var summary = new ObserveSummaryAccumulator(summaryPath);
    summary.Observe(0, "mira", "villager_snapshot", new
    {
        Name = "Mira", Need = "Idle", Activity = "Idle", Action = "Idle",
        GoalObjectId = (string?)null, ConversationPartnerId = (string?)null
    });
    summary.Observe(1, "mira", "world_action_failed", new
    {
        Action = "ApproachStorage", Reason = "no_reachable_approach"
    });
    summary.Observe(2, "mira", "world_action_succeeded", new
    {
        Action = "TakeItem"
    });
    summary.Observe(35, "mira", "villager_snapshot", new
    {
        Name = "Mira", Need = "Idle", Activity = "Idle", Action = "Idle",
        GoalObjectId = (string?)null, ConversationPartnerId = (string?)null
    });
    var summaryJson = File.ReadAllText(summaryPath + ".summary.json");
    var summaryLog = File.ReadAllText(summaryPath + ".summary.log");
    using var summaryDocument = System.Text.Json.JsonDocument.Parse(summaryJson);
    var summaryRoot = summaryDocument.RootElement;
    var summaryVillager = summaryRoot.GetProperty("Villagers")[0];
    Require(summaryRoot.GetProperty("FailureCounts")
                .GetProperty("ApproachStorage:no_reachable_approach")
                .GetInt32() == 1 &&
            summaryVillager.GetProperty("Name").GetString() == "Mira" &&
            summaryVillager.GetProperty("SuccessfulActions").GetInt32() == 1 &&
            summaryVillager.GetProperty("FailedActions").GetInt32() == 1 &&
            summaryVillager.GetProperty("PotentiallyStalled").GetBoolean() &&
            summaryLog.Contains("Mira |", StringComparison.Ordinal) &&
            summaryLog.Contains("successes/failures", StringComparison.Ordinal),
        "observe summaries must persist failure counts plus per-villager progress and stall fields in JSON and log form");
}
finally
{
    Directory.Delete(observeSummaryDirectory, recursive: true);
}
var cinematicDirector = new CinematicSceneDirector(
    10,
    [new(2, 8, SceneCameraTarget.Player, Vector2.Zero, 1.4f, .8f)],
    [new(1, "thunder"), new(3, "impact")]);
cinematicDirector.Start();
cinematicDirector.Advance(1.5);
var firstCinematicCue = cinematicDirector.TryDequeueCue(out var firstCue) &&
                         firstCue == "thunder";
var noRepeatedCinematicCue =
    !cinematicDirector.TryDequeueCue(out _);
cinematicDirector.Advance(2);
var secondCinematicCue = cinematicDirector.TryDequeueCue(out var secondCue) &&
                          secondCue == "impact";
var cinematicShot = cinematicDirector.CurrentShot();
Require(
    firstCinematicCue && noRepeatedCinematicCue && secondCinematicCue &&
    cinematicShot is { Target: SceneCameraTarget.Player } &&
    cinematicDirector.CurrentZoom(cinematicShot.Value) < 1.4f &&
    cinematicDirector.CurrentZoom(cinematicShot.Value) > .8f,
    "cinematic directors must emit ordered cues once and interpolate reusable camera shots");
Require(
    Math.Abs(GameHostWindow.AnchoredSpriteTop(610, 320, 282, 320) - 328) < .001,
    "cinematic ships must place their authored hull anchor on the ocean waterline");
Require(
    GameHostWindow.SinkingOffset(27, 300) == 0 &&
    GameHostWindow.SinkingOffset(31, 300) is > 4 and < 5 &&
    GameHostWindow.SinkingOffset(35, 300) is > 8 and < 10 &&
    GameHostWindow.SinkingFrameIndex(0, 6) == 0 &&
    GameHostWindow.SinkingFrameIndex(1, 6) == 5 &&
    GameHostWindow.ShipTrackedTravel(35) >
        GameHostWindow.ShipTrackedTravel(34) &&
    GameHostWindow.ShipTrackedTravel(35) -
        GameHostWindow.ShipTrackedTravel(34) <
    GameHostWindow.ShipTrackedTravel(28) -
        GameHostWindow.ShipTrackedTravel(27) &&
    GameHostWindow.CreditOpacity(8, 7, 16) > .99f &&
    GameHostWindow.CreditOpacity(16, 7, 16) == 0 &&
    GameHostWindow.CinematicShipScreenX(12.07f, 1280, 400) > 750 &&
    GameHostWindow.CinematicShipScreenX(12.09f, 1280, 400) < -395 &&
    GameHostWindow.CinematicSceneLoopFade(
        12.083333f, 1280, 400) > .98f &&
    GameHostWindow.CinematicSceneLoopFade(22, 1280, 400) == 0 &&
    GameHostWindow.CinematicShipScreenX(24, 1280, 400) >
        GameHostWindow.CinematicShipScreenX(22, 1280, 400),
    "wreck cinematics must keep ships afloat before impact and sink gradually afterwards");
var seededStormA = GameHostWindow.BuildStormCues(new Random(1200));
var seededStormB = GameHostWindow.BuildStormCues(new Random(1200));
Require(
    seededStormA.SequenceEqual(seededStormB) &&
    seededStormA.Count(value => value.Name == "ship-impact") == 1 &&
    seededStormA.Count(value => value.Name.StartsWith("thunder")) >= 6 &&
    seededStormA.Any(value => value.Name == "thunder-flash") &&
    Math.Abs(GameHostWindow.CinematicSeaZoom(31) - 1) < .001f &&
    Math.Abs(GameHostWindow.CinematicSeaZoom(36) - 1.45f) < .001f &&
    GameHostWindow.CinematicFireVisibility(27) == 1 &&
    Math.Abs(GameHostWindow.CinematicFireVisibility(31) - .5f) < .001f &&
    GameHostWindow.CinematicFireVisibility(35) == 0,
    "opening storms must randomize reproducibly and push into the beach reveal zoom");
Require(
    Age2MusicCatalog.FindNamedTrack(
        [@"C:\aoe\music1.mp3", @"C:\aoe\xmusic10.mp3"],
        "XMUSIC10.MP3") == @"C:\aoe\xmusic10.mp3",
    "cinematic music overrides must resolve installed AoE tracks by file name");
var reusablePanCamera = new CinematicPanCamera();
reusablePanCamera.Update(700, 1000, .1f);
var reusablePanStarted = reusablePanCamera.Panning;
for (var index = 0; index < 24; index++)
    reusablePanCamera.Update(700, 1000, .1f);
Require(
    reusablePanStarted && !reusablePanCamera.Panning &&
    Math.Abs(700 - reusablePanCamera.Offset - 200) < .01f,
    "reusable cinematic pan cameras must restore their authored screen mark");
var settlementFourSource = VillagerSimulation.CreateInitial(
    2187, Vector2.Zero, population: 4);
var settlementFourConfigured = ObserveScenarioService.Configure(
    ObserveScenarioService.SettlementFour, 2187, settlementFourSource);
var settlementTenSource = VillagerSimulation.CreateInitial(
    2187, Vector2.Zero, population: 10);
var settlementTenConfigured = ObserveScenarioService.Configure(
    ObserveScenarioService.SettlementTen, 2187, settlementTenSource);
var openingIncidentVillagers = VillagerOpeningIncidentService.Apply(
    settlementTenSource, 2187,
    settlementTenSource[0].AwakenedGameSeconds);
var repeatedOpeningIncident = VillagerOpeningIncidentService.Apply(
    settlementTenSource, 2187,
    settlementTenSource[0].AwakenedGameSeconds);
var variedOpeningIncident = VillagerOpeningIncidentService.Apply(
    settlementTenSource, 99173,
    settlementTenSource[0].AwakenedGameSeconds);
var openingIncidentAccounts =
    VillagerOpeningIncidentService.Accounts(openingIncidentVillagers);
var openingInjured = openingIncidentVillagers.Where(value =>
    value.Health < AdventureService.BaseMaximumHealth).ToArray();
static string IncidentSignature(IEnumerable<VillagerState> villagers) =>
    string.Join("|", villagers.OrderBy(value => value.Id).SelectMany(value =>
        (value.Memories ?? []).Where(memory =>
                memory.Kind.StartsWith("wreck_", StringComparison.Ordinal))
            .OrderBy(memory => memory.EventId)
            .Select(memory =>
                $"{value.Id}:{memory.EventId}:{memory.Kind}:" +
                $"{memory.SubjectId}:{memory.Sentiment}:{memory.Summary}")));
var incidentIds = openingIncidentVillagers.SelectMany(value =>
        value.Memories ?? [])
    .Where(value => value.Kind.StartsWith(
        "wreck_", StringComparison.Ordinal))
    .Select(value => value.EventId).Distinct().ToArray();
var rescueHelperIds = openingIncidentVillagers
    .Where(value => value.Memories?.Any(memory =>
        memory.Kind == "wreck_rescue" &&
        memory.Summary?.StartsWith("I ", StringComparison.Ordinal) == true) ==
        true)
    .Select(value => value.Id)
    .ToHashSet(StringComparer.Ordinal);
var exposedInjury = VillagerOpeningIncidentService.ApplyShoreExposure(
    settlementTenSource[0] with
    {
        Health = 20,
        Action = EntityAction.Hurt
    }, Biome.Beach, damage: 3);
var safeInjury = VillagerOpeningIncidentService.ApplyShoreExposure(
    settlementTenSource[0] with
    {
        Health = 20,
        Action = EntityAction.Hurt
    }, Biome.Grassland, damage: 3);
Require(
    openingInjured.Length >= 1 &&
    openingInjured.All(value => value.Health > 0) &&
    openingInjured.All(value => value.Memories?.Any(memory =>
        memory.Kind == "wreck_rescue" &&
        memory.Summary?.StartsWith("I ", StringComparison.Ordinal) != true) ==
        true) &&
    rescueHelperIds.Count > 0 &&
    openingIncidentVillagers.Where(value => rescueHelperIds.Contains(value.Id))
        .All(value => value.Health == AdventureService.BaseMaximumHealth) &&
    incidentIds.Length is >= 3 and <= 10 &&
    IncidentSignature(openingIncidentVillagers) ==
        IncidentSignature(repeatedOpeningIncident) &&
    IncidentSignature(openingIncidentVillagers) !=
        IncidentSignature(variedOpeningIncident) &&
    VillagerOpeningIncidentService.IsActive(
        openingIncidentVillagers,
        settlementTenSource[0].AwakenedGameSeconds) &&
    !VillagerOpeningIncidentService.IsActive(
        openingIncidentVillagers,
        settlementTenSource[0].AwakenedGameSeconds +
        VillagerOpeningIncidentService.IncidentRealSeconds *
        VillagerSimulation.GameSecondsPerRealSecond) &&
    openingIncidentAccounts.Count >= 2 &&
    openingIncidentAccounts.All(value => value.UseAi) &&
    exposedInjury.Health == 17 &&
    exposedInjury.Action == EntityAction.Hurt &&
    safeInjury.Health == 20 &&
    openingIncidentVillagers.Any(value =>
        value.Relationships?.Count > 0),
    "seeded opening incidents must vary by run while every collapsed survivor receives a deterministic rescue and social history");
var priorityVillager = settlementTenSource[0] with
{
    ProjectAssignment = new(
        ItemIds.Campfire,
        settlementTenSource[0].Id,
        [new(ItemIds.LargeRock, 1)],
        0),
    GoalObjectId = Guid.NewGuid()
};
Require(
    VillagerIntentPriorityService.HasCommittedWork(priorityVillager) &&
    VillagerIntentPriorityService.HasAssignedProject(priorityVillager) &&
    VillagerIntentPriorityService.ShouldProtectCommittedWork(
        priorityVillager) &&
    VillagerIntentPriorityService.CanInterruptScriptedActivity(
        priorityVillager) &&
    !VillagerIntentPriorityService.ShouldProtectCommittedWork(
        priorityVillager with { Hunger = 20 }) &&
    VillagerIntentPriorityService.HasUrgentOverride(
        priorityVillager with { Hunger = 20 }) &&
    VillagerIntentPriorityService.CanInterruptScriptedActivity(
        priorityVillager with { Hunger = 20 }) &&
    !VillagerIntentPriorityService.ShouldProtectCommittedWork(
        priorityVillager with
        {
            Energy = 10,
            Activity = VillagerActivity.Resting
        }) &&
    !VillagerIntentPriorityService.HasCommittedWork(
        priorityVillager with
        {
            ProjectAssignment = null,
            GoalObjectId = null
        }) &&
    !VillagerIntentPriorityService.HasAssignedProject(
        priorityVillager with { ProjectAssignment = null }) &&
    !VillagerIntentPriorityService.HasCommittedWork(
        priorityVillager with { Health = 0 }),
    "committed work must be protected from optional goals while yielding to urgent needs, fatigue, and death");
var leadershipCouncil = VillagerLeadershipService.HoldCouncil(
    settlementTenSource)!;
var repeatedLeadershipCouncil = VillagerLeadershipService.HoldCouncil(
    settlementTenSource)!;
Require(
    VillagerLeadershipService.HoldCouncil(
        settlementTenSource.Take(1).ToArray()) is null &&
    VillagerLeadershipService.HoldCouncil(
        settlementTenSource.Take(2).ToArray()) is null &&
    VillagerLeadershipService.HoldCouncil(
        settlementTenSource.Take(3).ToArray()) is not null,
    "formal councils and settlement groups must require at least three living survivors");
var councilVillagers = VillagerLeadershipService.ApplyCouncil(
    settlementTenSource, leadershipCouncil, 28_800);
var releasedCouncilVillagers = VillagerLeadershipService.ApplyCouncil(
    settlementTenSource.Select(value => value with
    {
        Need = VillagerNeed.Social,
        Activity = VillagerActivity.Conversing,
        ConversationPartnerId = "council-speaker",
        FollowingActorId = "council-speaker",
        TargetX = 40,
        TargetY = 40,
        NextDecisionGameSeconds = 99_999
    }).ToArray(), leadershipCouncil, 28_800);
var openingCouncilLines = VillagerGroupConversationService.OpeningCouncil(
    settlementTenSource, leadershipCouncil);
var councilPlan = VillagerSettlementProjectService.Plan(
    councilVillagers,
    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    leadershipCouncil.LeaderId)!;
var councilLeader = councilVillagers.Single(value =>
    value.Id == leadershipCouncil.LeaderId);
var councilSupporter = leadershipCouncil.Votes.First(value =>
    value.VoterId != leadershipCouncil.LeaderId &&
    value.CandidateId == leadershipCouncil.LeaderId);
var supportingVillager = councilVillagers.Single(value =>
    value.Id == councilSupporter.VoterId);
Require(
    leadershipCouncil.LeaderId == repeatedLeadershipCouncil.LeaderId &&
    leadershipCouncil.Votes.Count == settlementTenSource.Length &&
    openingCouncilLines.Count(line => line.Purpose == "introduction") ==
        settlementTenSource.Length &&
    openingCouncilLines.Where(line => line.Purpose == "introduction")
        .Select(line => line.Text).Distinct(StringComparer.Ordinal).Count() ==
        settlementTenSource.Length &&
    openingCouncilLines.Count(line => line.UseAi) >=
        openingCouncilLines.Count(line => line.Purpose == "dissent") + 2 &&
    openingCouncilLines.Count(line =>
        line.Purpose is "support" or "dissent") ==
        settlementTenSource.Length &&
    settlementTenSource.All(villager => openingCouncilLines.Any(line =>
        line.SpeakerId == villager.Id && line.Purpose == "introduction")) &&
    openingCouncilLines.Take(settlementTenSource.Length).All(line =>
        line.Purpose == "introduction") &&
    openingCouncilLines[settlementTenSource.Length].Purpose ==
        "nomination-call" &&
    openingCouncilLines.Skip(settlementTenSource.Length + 1)
        .TakeWhile(line => line.Purpose == "proposal")
        .All(line => line.UseAi) &&
    openingCouncilLines[^1].Purpose == "decision" &&
    councilVillagers.All(value =>
        value.RecognizedLeaderId == leadershipCouncil.LeaderId) &&
    councilVillagers.All(value => value.Memories?.Any(memory =>
        memory.Kind is "leadership-council" or "leadership-contested" &&
        memory.SubjectId == leadershipCouncil.LeaderId) == true) &&
    councilPlan.LeaderId == leadershipCouncil.LeaderId &&
    councilPlan.Worksite == new Vector2(
        councilLeader.PositionX, councilLeader.PositionY) &&
    councilPlan.Assignments.Count == settlementTenSource.Length,
    "the opening council must deterministically choose a known leader and establish one shared project worksite");
Require(
    VillagerLeadershipService.IsLeader(councilLeader) &&
    supportingVillager.Relationships?.Single(value =>
        value.CharacterId == councilLeader.Id).State.Respect > 0,
    "recognized leaders must have visible status and receive initial follower respect");
Require(releasedCouncilVillagers.All(value =>
            value.Activity == VillagerActivity.Idle &&
            value.Need == VillagerNeed.Idle &&
            value.ConversationPartnerId is null &&
            value.FollowingActorId is null &&
            value.TargetX is null &&
            value.TargetY is null &&
            value.NextDecisionGameSeconds == 28_800 &&
            value.NextLeadershipChallengeGameSeconds ==
            28_800 +
            VillagerLeadershipService.MinimumLeadershipTenureGameSeconds),
    "council completion must release every participant for immediate work while retaining a shared leader cooldown");
var missedAssignment = VillagerLeadershipService.ApplyMissedAssignment(
    councilLeader, supportingVillager, ItemIds.Campfire, 32_400);
Require(
    missedAssignment.Worker.Memories?.Any(value =>
        value.Kind == "missed-assignment" &&
        value.SubjectId == councilLeader.Id) == true &&
    missedAssignment.Worker.Relationships?.Single(value =>
        value.CharacterId == councilLeader.Id).State.Resentment > 0 &&
    missedAssignment.Leader.Relationships?.Single(value =>
        value.CharacterId == supportingVillager.Id).State.Respect < 0,
    "idle assigned workers must receive one remembered social consequence from leadership");
var councilAssignments = councilVillagers.Select(villager =>
    new VillagerProjectAssignment(
        councilPlan.ProjectItemId,
        councilPlan.BuilderId,
        councilPlan.Assignments[villager.Id],
        28_800,
        councilPlan.LeaderId,
        councilPlan.Worksite.X,
        councilPlan.Worksite.Y,
        councilPlan.WorksiteLevel)).ToArray();
Require(
    councilAssignments.All(value =>
        value.WorksiteX == councilPlan.Worksite.X &&
        value.WorksiteY == councilPlan.Worksite.Y &&
        value.WorksiteLevel == councilPlan.WorksiteLevel),
    "every project assignment must carry the same fixed delivery rendezvous");
var assignedCouncilVillagers = councilVillagers.Select((villager, index) =>
    villager with { ProjectAssignment = councilAssignments[index] }).ToArray();
var replannedCouncilProject = VillagerSettlementProjectService.Plan(
    assignedCouncilVillagers,
    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    leadershipCouncil.LeaderId)!;
Require(
    assignedCouncilVillagers.All(villager =>
        replannedCouncilProject.Assignments[villager.Id]
            .SequenceEqual(villager.ProjectAssignment!.Requirements)),
    "active project responsibilities must remain stable when work roles are recalculated");
var councilOffsets = settlementTenSource.Select((villager, index) =>
    VillagerGroupConversationService.CircleOffset(
        villager.Id, index, settlementTenSource.Length)).ToArray();
Require(
    councilOffsets.All(offset =>
        offset.Length() is >= 3.09f and <= 4.21f) &&
    councilOffsets.Select(offset => MathF.Round(offset.Length(), 2))
        .Distinct().Count() > 4,
    "the opening council must gather into a human-looking rough circle");
var dissentingVote = leadershipCouncil.Votes.FirstOrDefault(value =>
    value.CandidateId != leadershipCouncil.LeaderId);
Require(
    dissentingVote is not null,
    "the ten-person council fixture must include a dissenting voter");
var cooledCouncilVillagers = assignedCouncilVillagers.Select(villager =>
    villager with
    {
        Boldness = villager.Id == dissentingVote!.VoterId
            ? 1
            : villager.Boldness,
        ProjectAssignment = villager.Id == dissentingVote.VoterId
            ? villager.ProjectAssignment! with
            {
                Requirements = [new(ItemIds.LargeRock, 2)],
                AssignedGameSeconds = 28_800
            }
            : villager.ProjectAssignment
    }).ToArray();
Require(
    VillagerLeadershipService.SelectChallenger(
        cooledCouncilVillagers, 30_600) is null,
    "a stalled dissenter must respect the post-council leadership cooldown instead of immediately reopening the election");
var challengeVillagers = assignedCouncilVillagers.Select((villager, index) =>
    villager with
    {
        Boldness = villager.Id == dissentingVote!.VoterId ? 1 : villager.Boldness,
        NextLeadershipChallengeGameSeconds = 0,
        ProjectAssignment = index == 0
            ? villager.ProjectAssignment! with
            {
                Requirements = [new(ItemIds.LargeRock, 2)],
                AssignedGameSeconds = 0
            }
            : villager.ProjectAssignment
    }).ToArray();
var selectedChallenger = VillagerLeadershipService.SelectChallenger(
    challengeVillagers, 60 * 60);
Require(
    selectedChallenger?.Id == dissentingVote!.VoterId &&
    VillagerLeadershipService.SelectChallenger(
        challengeVillagers.Select(value => value with
        {
            NextLeadershipChallengeGameSeconds = 2 * 60 * 60
        }).ToArray(),
        60 * 60) is null,
    "a bold dissenter may call a new election after a stalled plan, subject to cooldown");
Require(
    new GameSettings().UnlimitedZoom,
    "unlimited zoom must be enabled by default");
var defaultAi = new GameSettings().EffectiveAi;

var advancedGenerated = new[]
{
    VillagerSimulation.DefaultPersona(0),
    VillagerSimulation.DefaultPersona(1)
};
var advancedSetups = NewWorldSurvivorSetupService.Build(
    2,
    advancedGenerated,
    ["Elara", "Bram"],
    ["Bold but generous", ""],
    ["", "Sailor"],
    ["Raised by a healer", ""],
    ["stone axe, cooked minnows", "stone knife"],
    "Their ship broke apart in a night storm.");
Require(advancedSetups[0].Name == "Elara" &&
        advancedSetups[0].Persona.Personality == "Bold but generous" &&
        advancedSetups[0].Persona.PriorTrade == advancedGenerated[0].PriorTrade,
    "advanced survivor setup must merge explicit overrides with generated personas");
Require(advancedSetups.All(value =>
        value.Persona.ArrivalMemory == "Their ship broke apart in a night storm."),
    "advanced survivor setup must give every NPC the shared story");
Require(advancedSetups[0].StartingItems.Contains(ItemIds.StoneAxe) &&
        advancedSetups[1].StartingItems.Contains(ItemIds.StoneKnife),
    "advanced survivor setup must resolve catalog item names");
Require(NewWorldSurvivorSetupService.UnknownItems("stone axe, laser gun")
        .SequenceEqual(["laser gun"]),
    "advanced survivor setup must report unknown starting items");
var advancedVillagers = VillagerSimulation.CreateInitial(
    991, Vector2.Zero, _ => true, population: 2, setups: advancedSetups);
Require(advancedVillagers[0].Name == "Elara" &&
        advancedVillagers[0].Inventory.Contains(ItemIds.StoneAxe) &&
        advancedVillagers[1].Inventory.Contains(ItemIds.StoneKnife),
    "initial NPC creation must apply advanced names and starting inventories");
var lateStarvation = SurvivalService.Advance(1, 0, 100, 20);
Require(lateStarvation.Hunger == 0 && lateStarvation.Health == 96,
    "starvation damage must apply only to time spent at zero hunger");
var fractionalStarvation = SurvivalService.Advance(0, 0, 100, .5f);
for (var index = 0; index < 3; index++)
    fractionalStarvation = SurvivalService.Advance(
        fractionalStarvation.Hunger,
        fractionalStarvation.WellFedSeconds,
        fractionalStarvation.Health,
        .5f,
        starvationDamageRemainder:
            fractionalStarvation.StarvationDamageRemainder);
Require(fractionalStarvation.Health == 99 &&
        fractionalStarvation.StarvationDamageRemainder == 0,
    "fractional starvation damage must survive frequent small updates");
var longCatchUp = VillagerSimulation.CatchUp(
    advancedVillagers[0] with
    {
        SurvivalTimeScaleVersion = 1,
        LastSimulatedGameSeconds = 0,
        Hunger = 100,
        WellFedSeconds = 4000
    },
    48 * 60 * 60,
    hungerLossMultiplier: 0);
Require(longCatchUp.LastSimulatedGameSeconds == 48 * 60 * 60 &&
        Math.Abs(longCatchUp.WellFedSeconds - 1120) < .01f,
    "villager catch-up must process elapsed time beyond the first game day");
foreach (var tiringAction in new[]
         {
             EntityAction.Move, EntityAction.Gather, EntityAction.Work,
             EntityAction.Attack, EntityAction.Dig, EntityAction.Mine,
             EntityAction.Fish
         })
{
    var tiredWorker = VillagerFatigueService.Advance(
        advancedVillagers[0] with
        {
            Action = tiringAction,
            Energy = 100,
            LastEnergyGameSeconds = 0
        },
        10 * VillagerSimulation.GameSecondsPerRealSecond);
    Require(tiredWorker.Energy < 100,
        $"{tiringAction} must consume villager energy");
}
var restedVillager = VillagerFatigueService.Advance(
    advancedVillagers[0] with
    {
        Action = EntityAction.Idle,
        Activity = VillagerActivity.Resting,
        Energy = 10,
        LastEnergyGameSeconds = 0
    },
    10 * VillagerSimulation.GameSecondsPerRealSecond);
Require(restedVillager.Energy == 25,
    "resting must recover villager energy using elapsed real time");
var exhaustedWorker = advancedVillagers[0] with
{
    Hunger = 80,
    Energy = 10,
    Action = EntityAction.Work,
    Activity = VillagerActivity.SeekingResource,
    TargetX = 4,
    TargetY = 5,
    GoalObjectId = Guid.NewGuid()
};
var restingWorker = VillagerFatigueService.BeginRest(exhaustedWorker, 100);
Require(VillagerFatigueService.ShouldRest(exhaustedWorker) &&
        restingWorker.Activity == VillagerActivity.Resting &&
        restingWorker.Action == EntityAction.Idle &&
        restingWorker.TargetX is null &&
        restingWorker.TargetY is null &&
        restingWorker.GoalObjectId is null,
    "low energy must interrupt non-essential work and clear its target");
var hungryExhausted = exhaustedWorker with { Hunger = 30 };
var endangeredExhausted = exhaustedWorker with { Health = 20 };
var fightingExhausted = exhaustedWorker with
{
    ConflictIntent = VillagerConflictIntent.Defend
};
Require(!VillagerFatigueService.ShouldRest(hungryExhausted) &&
        VillagerSimulation.Decide(hungryExhausted, Vector2.Zero, 100).Need ==
            VillagerNeed.Food &&
        !VillagerFatigueService.ShouldRest(endangeredExhausted) &&
        VillagerSimulation.Decide(endangeredExhausted, Vector2.Zero, 100).Need ==
            VillagerNeed.Safe &&
        !VillagerFatigueService.ShouldRest(fightingExhausted),
    "hunger, immediate danger, and active conflict must override resting");
var fatigueCatchUpStart = advancedVillagers[0] with
{
    Hunger = 100,
    Energy = 100,
    Action = EntityAction.Work,
    LastSimulatedGameSeconds = 0,
    LastEnergyGameSeconds = 0
};
var singleFatigueCatchUp = VillagerSimulation.CatchUp(
    fatigueCatchUpStart, 600, hungerLossMultiplier: 0);
var steppedFatigueCatchUp = fatigueCatchUpStart;
for (var step = 1; step <= 10; step++)
    steppedFatigueCatchUp = VillagerSimulation.CatchUp(
        steppedFatigueCatchUp, step * 60, hungerLossMultiplier: 0);
Require(Math.Abs(singleFatigueCatchUp.Energy -
                 steppedFatigueCatchUp.Energy) < .001f &&
        singleFatigueCatchUp.LastEnergyGameSeconds == 600 &&
        steppedFatigueCatchUp.LastEnergyGameSeconds == 600,
    "fatigue catch-up must match equivalent smaller simulation steps");
Require(VillagerFatigueService.MovementEffectiveness(25) <
            VillagerFatigueService.MovementEffectiveness(100) &&
        VillagerFatigueService.WorkEffectiveness(25) <
            VillagerFatigueService.WorkEffectiveness(100) &&
        VillagerFatigueService.AdjustedWorkDuration(10, 25) >
            VillagerFatigueService.AdjustedWorkDuration(10, 100),
    "low energy must reduce movement speed and work effectiveness");
var adrenalineStart = advancedVillagers[0] with
{
    Health = 18,
    Energy = 5,
    AdrenalineStress = 0,
    LastAdrenalineGameSeconds = 100
};
var adrenaline = VillagerAdrenalineService.Advance(
    adrenalineStart, 100, immediateDanger: true);
var blockedAdrenaline = VillagerAdrenalineService.Advance(
    adrenaline with { Energy = 1 }, 200, immediateDanger: true);
var recoveredAdrenaline = VillagerAdrenalineService.Advance(
    blockedAdrenaline,
    adrenaline.AdrenalineCooldownUntilGameSeconds + 600,
    immediateDanger: false);
var deadAdrenaline = VillagerAdrenalineService.Advance(
    adrenalineStart with { Health = 0 }, 200, immediateDanger: true);
Require(adrenaline.Energy > adrenalineStart.Energy &&
        adrenaline.AdrenalineStress ==
            VillagerAdrenalineService.StressCost &&
        VillagerAdrenalineService.IsActive(adrenaline, 101) &&
        blockedAdrenaline.Energy == 1 &&
        recoveredAdrenaline.AdrenalineStress <
            blockedAdrenaline.AdrenalineStress &&
        deadAdrenaline == (adrenalineStart with { Health = 0 }),
    "danger must trigger bounded adrenaline energy with stress, cooldown, recovery, and no dead-state mutation");
var facingAttacker = VillagerFacingService.Face(
    advancedVillagers[0] with
    {
        PositionX = 2,
        PositionY = 3,
        FacingX = -1,
        FacingY = 0
    },
    new(2, 8));
var yellCaller = advancedVillagers[0] with
{
    Id = "yell-caller", PositionX = 0, PositionY = 0,
    Health = 80, Hunger = 80, Energy = 80
};
var nearYellResponder = yellCaller with
{
    Id = "near-yell-responder", PositionX = 23,
    Sociability = .8f, Boldness = .7f,
    SettlementGroupId = "yell-group"
};
var groupedYellCaller = yellCaller with
{
    SettlementGroupId = "yell-group"
};
var farYellResponder = yellCaller with
{
    Id = "far-yell-responder", PositionX = 25
};
var markedYell = VillagerYellService.MarkYelled(yellCaller, 100);
Require(Math.Abs(facingAttacker.FacingX) < .001f &&
        facingAttacker.FacingY > .999f &&
        VillagerYellService.CanHearAndRespond(
            nearYellResponder, yellCaller) &&
        !VillagerYellService.CanHearAndRespond(
            farYellResponder, yellCaller) &&
        !VillagerYellService.CanHearAndRespond(
            nearYellResponder with { Hunger = 1 }, yellCaller) &&
        VillagerYellService.ShouldAnswer(
            nearYellResponder, groupedYellCaller, "aggressor",
            default, sameSettlement: true) &&
        !VillagerYellService.ShouldAnswer(
            nearYellResponder, groupedYellCaller, "aggressor",
            new RelationshipState(Trust: -20), sameSettlement: true) &&
        !VillagerYellService.ShouldAnswer(
            nearYellResponder with { ConflictTargetId = "someone-else" },
            groupedYellCaller, "aggressor", default,
            sameSettlement: true) &&
        !VillagerYellService.CanYell(markedYell, 101) &&
        VillagerYellService.CanYell(
            markedYell, markedYell.NextYellGameSeconds),
    "NPC attacks must face their target while yells use an extended, survival-aware hearing radius and cooldown");
var locationMemoryVillager = VillagerLocationMemoryService.Remember(
    advancedVillagers[0] with
    {
        Hunger = 80,
        Need = VillagerNeed.Idle,
        PositionX = 0,
        PositionY = 0
    },
    VillagerLocationType.FoodSource,
    new(10, 2),
    worldLevel: 0,
    gameSeconds: 100);
var discoveredFoodLocation = locationMemoryVillager.LocationMemories!.Single();
Require(discoveredFoodLocation.Type == VillagerLocationType.FoodSource &&
        discoveredFoodLocation.PositionX == 10 &&
        discoveredFoodLocation.PositionY == 2 &&
        discoveredFoodLocation.WorldLevel == 0 &&
        discoveredFoodLocation.Confidence ==
            VillagerLocationMemoryService.DiscoveryConfidence &&
        discoveredFoodLocation.LastObservedGameSeconds == 100,
    "villagers must remember personally discovered resource locations with position, level, confidence, and observation time");
var rememberedFoodAction = VillagerSimulation.SelectWorldAction(
    locationMemoryVillager,
    [],
    gameSeconds: 110);
Require(rememberedFoodAction.Kind ==
            VillagerWorldActionKind.ApproachItem &&
        rememberedFoodAction.ObjectId is null &&
        rememberedFoodAction.Target is { X: > 0 } &&
        rememberedFoodAction.RememberedLocation == discoveredFoodLocation,
    "villagers must approach a remembered useful location when no nearby target is visible");
var foreignStorageMemory = VillagerLocationMemoryService.Remember(
    locationMemoryVillager,
    VillagerLocationType.Storage,
    new(4, 0),
    worldLevel: 0,
    gameSeconds: 105,
    ownerId: "someone-else");
var fullLocationInventory = Enumerable.Repeat<string?>(
    ItemIds.Sticks, PlayerInventory.Capacity).ToArray();
Require(
    VillagerSimulation.SelectWorldAction(
        foreignStorageMemory with
        {
            Inventory = fullLocationInventory,
            LocationMemories = foreignStorageMemory.LocationMemories!
                .Where(value => value.Type == VillagerLocationType.Storage)
                .ToArray()
        },
        [], 110).Kind == VillagerWorldActionKind.None,
    "villagers must not approach remembered storage they cannot use");
var ownedStorageMemory = VillagerLocationMemoryService.Remember(
    locationMemoryVillager,
    VillagerLocationType.Storage,
    new(4, 0),
    worldLevel: 0,
    gameSeconds: 105,
    ownerId: locationMemoryVillager.Id);
Require(
    VillagerSimulation.SelectWorldAction(
        ownedStorageMemory with
        {
            Inventory = fullLocationInventory,
            LocationMemories = ownedStorageMemory.LocationMemories!
                .Where(value => value.Type == VillagerLocationType.Storage)
                .ToArray()
        },
        [], 110).Kind == VillagerWorldActionKind.ApproachStorage,
    "a full villager may approach personally owned remembered storage");
var unreachableRememberedFood = VillagerLocationMemoryService.MarkUnreachable(
    locationMemoryVillager,
    discoveredFoodLocation.Type,
    new(discoveredFoodLocation.PositionX, discoveredFoodLocation.PositionY),
    discoveredFoodLocation.WorldLevel,
    gameSeconds: 110);
var unreachableFoodMemory = unreachableRememberedFood.LocationMemories!
    .Single();
Require(unreachableRememberedFood.FailedLocations is
            [{ Type: VillagerLocationType.FoodSource, Failures: 1 }] &&
        VillagerLocationMemoryService.IsTemporarilyFailed(
            unreachableRememberedFood, unreachableFoodMemory, 111) &&
        VillagerSimulation.SelectWorldAction(
            unreachableRememberedFood, [], gameSeconds: 111).Kind ==
        VillagerWorldActionKind.None &&
        unreachableFoodMemory.Confidence < discoveredFoodLocation.Confidence,
    "an unreachable remembered location without an object ID must be keyed, deprioritized, and temporarily blacklisted");
var visuallyRefreshedFailedFood =
    VillagerLocationMemoryService.ObserveWorldObjects(
        unreachableRememberedFood,
        [new(
            Guid.NewGuid(), ItemIds.WildBerries,
            new(discoveredFoodLocation.PositionX,
                discoveredFoodLocation.PositionY),
            null, false)],
        gameSeconds: 111.5);
Require(VillagerLocationMemoryService.IsTemporarilyFailed(
        visuallyRefreshedFailedFood,
        visuallyRefreshedFailedFood.LocationMemories!.Single(),
        gameSeconds: 112),
    "seeing an unreachable resource again must refresh its existence without incorrectly clearing the failed-path cooldown");
var retriedUnreachableFood = VillagerLocationMemoryService.MarkUnreachable(
    unreachableRememberedFood,
    discoveredFoodLocation.Type,
    new(discoveredFoodLocation.PositionX, discoveredFoodLocation.PositionY),
    discoveredFoodLocation.WorldLevel,
    gameSeconds: 112);
var retriedFailure = retriedUnreachableFood.FailedLocations!.Single();
Require(retriedFailure.Failures == 2 &&
        retriedFailure.RetryAfterGameSeconds >
        unreachableRememberedFood.FailedLocations!.Single()
            .RetryAfterGameSeconds,
    "repeated remembered-location failures must extend the retry window rather than create duplicate anonymous failures");
var rediscoveredAfterFailure = VillagerLocationMemoryService.Remember(
    retriedUnreachableFood,
    VillagerLocationType.FoodSource,
    new(10, 2),
    worldLevel: 0,
    gameSeconds: 113);
Require(rediscoveredAfterFailure.FailedLocations is null &&
        VillagerSimulation.SelectWorldAction(
            rediscoveredAfterFailure, [], gameSeconds: 114).Kind ==
        VillagerWorldActionKind.ApproachItem,
    "rediscovering a remembered location must clear its failure blacklist so the villager can try it again");
var dangerousFoodLocation = VillagerLocationMemoryService.Remember(
    locationMemoryVillager,
    VillagerLocationType.Danger,
    new(10, 2),
    worldLevel: 0,
    gameSeconds: 105,
    confidence: 1);
Require(VillagerSimulation.SelectWorldAction(
            dangerousFoodLocation, [], 110).Kind ==
        VillagerWorldActionKind.None,
    "villagers must avoid remembered useful locations inside a known danger area");
var urgentDangerAction = VillagerSimulation.SelectWorldAction(
    dangerousFoodLocation with { Hunger = 30 },
    [],
    gameSeconds: 110);
Require(urgentDangerAction.Kind == VillagerWorldActionKind.ApproachItem,
    "urgent hunger must override remembered danger avoidance");
var decayedLocationConfidence =
    VillagerLocationMemoryService.ConfidenceAt(
        discoveredFoodLocation,
        100 + 24 * 60 * 60);
Require(decayedLocationConfidence < discoveredFoodLocation.Confidence &&
        decayedLocationConfidence > 0,
    "resource location confidence must decay with elapsed game time");
var emptyLocationVillager = VillagerLocationMemoryService.ObserveEmpty(
    locationMemoryVillager,
    VillagerLocationType.FoodSource,
    new(10, 2),
    worldLevel: 0,
    gameSeconds: 200);
Require(emptyLocationVillager.LocationMemories!.Single().Confidence <
            discoveredFoodLocation.Confidence - .4f,
    "reaching a remembered resource location and finding nothing must significantly reduce confidence");
var refreshedLocationVillager = VillagerLocationMemoryService.Remember(
    emptyLocationVillager,
    VillagerLocationType.FoodSource,
    new(10, 2),
    worldLevel: 0,
    gameSeconds: 250);
Require(refreshedLocationVillager.LocationMemories!.Single() is
        {
            Confidence: >= VillagerLocationMemoryService.DiscoveryConfidence,
            LastObservedGameSeconds: 250
        },
    "rediscovering a valid resource location must refresh confidence and observation time");
var boundedLocationVillager = advancedVillagers[0];
for (var locationIndex = 0;
     locationIndex < VillagerLocationMemoryService.MaximumMemories + 12;
     locationIndex++)
    boundedLocationVillager = VillagerLocationMemoryService.Remember(
        boundedLocationVillager,
        VillagerLocationType.WoodSource,
        new(locationIndex * 3, 0),
        worldLevel: 0,
        gameSeconds: locationIndex);
Require(boundedLocationVillager.LocationMemories?.Count ==
            VillagerLocationMemoryService.MaximumMemories,
    "per-villager location memory must remain bounded for save size");
var deadVillager = advancedVillagers[0] with
{
    Health = 0,
    Energy = 42,
    LastEnergyGameSeconds = 0,
    Action = EntityAction.Move,
    TargetX = 9,
    TargetY = 9,
    ConflictIntent = VillagerConflictIntent.Retaliate
};
Require(VillagerSimulation.Decide(deadVillager, Vector2.Zero, 100) == default &&
        VillagerSimulation.SelectSocialGoal(deadVillager, [], 100) == default &&
        VillagerSimulation.SelectWorldAction(deadVillager, [], 100) == default,
    "dead villagers must not produce decisions, social goals, or work actions");
var immobileDeadVillager = VillagerSimulation.AdvanceMovement(
    deadVillager, 1, gameSeconds: 100);
Require(immobileDeadVillager.Action == EntityAction.Die &&
        immobileDeadVillager.PositionX == deadVillager.PositionX &&
        immobileDeadVillager.PositionY == deadVillager.PositionY &&
        immobileDeadVillager.TargetX is null,
    "dead villagers must not continue moving");
var immediatelyReconsideringVillager = VillagerSimulation.AdvanceMovement(
    advancedVillagers[0] with
    {
        Action = EntityAction.Move,
        Activity = VillagerActivity.Exploring,
        TargetX = advancedVillagers[0].PositionX + .1f,
        TargetY = advancedVillagers[0].PositionY,
        NextDecisionGameSeconds = 1_000
    },
    elapsed: 1,
    gameSeconds: 200);
Require(immediatelyReconsideringVillager.Action == EntityAction.Idle &&
        immediatelyReconsideringVillager.NextDecisionGameSeconds == 200,
    "NPCs must reconsider immediately after reaching an intermediate movement target instead of standing until the old deadline");
var caughtUpDeadVillager = VillagerSimulation.CatchUp(
    deadVillager, 600, hungerLossMultiplier: 0);
Require(caughtUpDeadVillager.Energy == deadVillager.Energy,
    "dead villagers must never gain or lose energy");
Require(VillagerConflictService.DecideResponse(
            deadVillager, advancedVillagers[1], true).Intent ==
        VillagerConflictIntent.None,
    "dead villagers must not make conflict decisions");
var theftIncidentId = Guid.NewGuid();
var theftObserver = VillagerSimulation.ObserveUnauthorizedTaking(
    advancedVillagers[0], theftIncidentId, ItemIds.Sticks,
    advancedVillagers[0].Id, advancedVillagers[1].Id,
    100, 1, 5, out _);
var duplicateTheftObserver = VillagerSimulation.ObserveUnauthorizedTaking(
    theftObserver, theftIncidentId, ItemIds.Sticks,
    advancedVillagers[0].Id, advancedVillagers[1].Id,
    101, 1, 5, out var duplicateReaction);
Require(duplicateTheftObserver.Relationships![0].OwnershipOffences == 1 &&
        duplicateTheftObserver.Memories!.Count == theftObserver.Memories!.Count &&
        duplicateReaction == OwnershipReaction.None,
    "the same theft incident must be counted only once");
var failedObjectId = Guid.NewGuid();
var fallbackObjectId = Guid.NewGuid();
var blockedTargetVillager = VillagerSimulation.BlockMovement(
    advancedVillagers[0] with
    {
        GoalObjectId = failedObjectId,
        Hunger = 30,
        Inventory = new string?[28]
    },
    100);
var fallbackAction = VillagerSimulation.SelectWorldAction(
    blockedTargetVillager,
    [
        new(failedObjectId, ItemIds.WildBerries, new(1, 0), null, false),
        new(fallbackObjectId, ItemIds.WildBerries, new(2, 0), null, false)
    ],
    101);
Require(fallbackAction.ObjectId == fallbackObjectId,
    "blocked resource targets must be temporarily blacklisted");
var invalidDamageVictim = advancedVillagers[0];
Require(VillagerSimulation.RecordAttack(
            invalidDamageVictim, advancedVillagers[1].Id,
            advancedVillagers[1].Name, 0, 100) == invalidDamageVictim &&
        VillagerSimulation.RecordAttack(
            invalidDamageVictim, advancedVillagers[1].Id,
            advancedVillagers[1].Name, -5, 100) == invalidDamageVictim,
    "zero and negative combat damage must have no effects");
Require(EntityActionLifecycle.HasCompletedAnimation(
        EntityAction.Gather, 1, 5, .2f),
    "NPCs and players must share action-animation completion semantics");
Require(EntityActionLifecycle.FramesPerDirection(75) == 15,
    "NPC action completion must use one facing cycle rather than all directional textures");
var npcController = new NpcController();
var impactCount = 0;
var gatherIntent = new NpcBrainIntent(
    "gather_fibre", EntityAction.Gather, new Vector2(2, 3), "shrub:1");
Require(npcController.TryBegin(
        "npc-controller-check",
        gatherIntent,
        () =>
        {
            impactCount++;
            return new(gatherIntent, true);
        }),
    "NPC controller must accept a brain intent when the actor is free");
Require(!npcController.TryBegin(
        "npc-controller-check",
        gatherIntent,
        () => new(gatherIntent, true)),
    "NPC controller must not replace an action already being performed");
npcController.Advance("npc-controller-check", EntityAction.Gather, .54, 1);
Require(impactCount == 0 &&
        npcController.Phase("npc-controller-check") == NpcActionPhase.Acting,
    "NPC world interaction must wait for the action impact frame");
npcController.Advance("npc-controller-check", EntityAction.Gather, .55, 1);
Require(impactCount == 1 &&
        !npcController.IsBusy("npc-controller-check"),
    "NPC world interaction must finish at the impact frame instead of holding a stale animation");
npcController.Advance("npc-controller-check", EntityAction.Gather, .9, 1);
Require(impactCount == 1,
    "NPC world interaction must not repeat after completion");
Require(npcController.TryDequeueResult(out var controlledActorId,
            out var controlledResult) &&
        controlledActorId == "npc-controller-check" &&
        controlledResult.Succeeded,
    "NPC controller must return the interaction result to the brain queue");
var targetStillAvailable = true;
var validationController = new NpcController();
Require(validationController.TryBegin(
        "validated-npc", gatherIntent,
        () => new(gatherIntent, true),
        targetAvailable: () => targetStillAvailable),
    "NPC controller must accept a live target validator");
targetStillAvailable = false;
validationController.Advance("validated-npc", EntityAction.Gather, .1, 1);
Require(!validationController.IsBusy("validated-npc") &&
        validationController.TryDequeueResult(
            out _, out var unavailableResult) &&
        unavailableResult.Reason == "target_unavailable",
    "NPC actions must cancel immediately when their world target disappears");
var interruptedController = new NpcController();
var cancellationCount = 0;
Require(interruptedController.TryBegin(
        "interrupted-npc",
        gatherIntent,
        () => new(gatherIntent, true),
        () => cancellationCount++),
    "NPC controller must accept cancellable world interactions");
interruptedController.Advance(
    "interrupted-npc", EntityAction.Idle, 0, 1);
Require(!interruptedController.IsBusy("interrupted-npc") &&
        cancellationCount == 1 &&
        interruptedController.TryDequeueResult(
            out _, out var interruptedResult) &&
        interruptedResult.Reason == "interrupted",
    "interrupted NPC actions must release their controller and reservation");
var completedGatherAnimation = VillagerSimulation.CompleteAction(
    advancedVillagers[0] with
    {
        Action = EntityAction.Gather,
        ActionTime = 1,
        Activity = VillagerActivity.SeekingResource,
        TargetX = 4,
        TargetY = 5
    });
Require(completedGatherAnimation.Action == EntityAction.Idle &&
        completedGatherAnimation.ActionTime == 0 &&
        completedGatherAnimation.Activity == VillagerActivity.Idle &&
        completedGatherAnimation.TargetX is null &&
        completedGatherAnimation.TargetY is null,
    "completed NPC work animations must explicitly return to idle");
Require(VillagerSimulation.ScheduleNextDecision(
            advancedVillagers[0].Id, 1000, 60) !=
        VillagerSimulation.ScheduleNextDecision(
            advancedVillagers[1].Id, 1000, 60),
    "NPC decision cadence must be deterministically staggered per actor");
Require(
    defaultAi.Enabled &&
    defaultAi.BaseUrl == "http://localhost:11434" &&
    defaultAi.Model == "Gemma4:12B" &&
    (new GameSettings(Ai: defaultAi with
    {
        Model = "qwen3.6:35b-a3b"
    })).EffectiveAi.Model == "Gemma4:12B" &&
    (new GameSettings(Ai: defaultAi with
    {
        Model = "custom-local-model"
    })).EffectiveAi.Model == "custom-local-model",
    "AI settings must default and migrate retired defaults to Gemma4:12B without replacing explicit custom model overrides");
Require(NpcAiService.AvailabilityProbeTimeout >= TimeSpan.FromSeconds(45),
    "AI availability checks must allow large local models to cold-load");
Require(
    NpcAiService.ConfusesSelfWithSpeaker(
        "You have a steady memory, Alina.", "Alina") &&
    NpcAiService.ConfusesSelfWithSpeaker(
        "You remember me, Alina.", "Alina") &&
    !NpcAiService.ConfusesSelfWithSpeaker(
        "I remember you, Rowan.", "Alina") &&
    !NpcAiService.ConfusesSelfWithSpeaker(
        "I am Alina.", "Alina"),
    "NPC dialogue validation must reject second-person replies that address the NPC by its own name without rejecting valid identity statements");
Require(
    NpcAiService.AnswersSpeakerNameQuestion(
        "Your name is Rowan.", "What name did I tell you?", "Rowan") &&
    !NpcAiService.AnswersSpeakerNameQuestion(
        "I am not certain which name you mean, Rowan.",
        "What name did I tell you?", "Rowan") &&
    !NpcAiService.AnswersSpeakerNameQuestion(
        "I remember you.", "Do you remember my name?", "Rowan") &&
    NpcAiService.AnswersSpeakerNameQuestion(
        "I do not know.", "Do you remember my name?", "Unknown survivor"),
    "NPC name-question validation must require a known speaker name without inventing one for strangers");
Require(OllamaRequestPolicy.KeepAlive == "30m",
    "all Ollama requests must share the 30-minute residency policy");
await NpcAiScenarioChecks.RunAsync();
if (string.Equals(
        Environment.GetEnvironmentVariable("ISLANDRPG_LIVE_OLLAMA_CHECK"),
        "1",
        StringComparison.Ordinal))
{
    using var liveProposalAi = new NpcAiService();
    var liveOpcodeCases = new (
        string Speech,
        VillagerPromisePlanAction Opcode,
        string[] Actions)[]
    {
        ("Bring me two logs.", VillagerPromisePlanAction.Collect, ["gather"]),
        ("Can I have one of your berries?", VillagerPromisePlanAction.Deliver, ["give"]),
        ("Meet me back at this spot in one minute.", VillagerPromisePlanAction.Rendezvous, ["meet"]),
        ("Go to this safe shelter spot.", VillagerPromisePlanAction.MoveTo, ["seek_shelter"]),
        ("Enter that cave entrance.", VillagerPromisePlanAction.InteractWithTarget, ["enter_cave"]),
        ("Craft a rope for our camp.", VillagerPromisePlanAction.CraftItem, ["craft"]),
        ("Build a campfire here.", VillagerPromisePlanAction.BuildObject, ["build", "light_fire"]),
        ("Drop one log here for the cache.", VillagerPromisePlanAction.DepositItem, ["drop"]),
        ("Withdraw one log from storage.", VillagerPromisePlanAction.WithdrawItem, ["withdraw"]),
        ("Follow me for a while.", VillagerPromisePlanAction.FollowActor, ["follow", "come"]),
        ("Explore the land beyond the palms.", VillagerPromisePlanAction.ExploreArea, ["explore"]),
        ("Wait here for one minute.", VillagerPromisePlanAction.WaitUntil, ["wait"]),
        ("Warn me about the danger ahead.", VillagerPromisePlanAction.TalkToActor, ["warn"]),
        ("Defend yourself from my attack.", VillagerPromisePlanAction.AttackTarget, ["defend", "retaliate", "attack"]),
        ("Run away from me now.", VillagerPromisePlanAction.FleeFromTarget, ["flee", "go_away"]),
        ("Rest here until you recover.", VillagerPromisePlanAction.Rest, ["rest"]),
        ("Find food before you starve.", VillagerPromisePlanAction.Eat, ["seek_food", "take_food"]),
        ("Cut down that tree for logs.", VillagerPromisePlanAction.CutTree, ["cut_tree"]),
        ("Mine that iron deposit.", VillagerPromisePlanAction.Mine, ["mine"]),
        ("Catch us a fish at the shore.", VillagerPromisePlanAction.Fish, ["fish"]),
        ("Cook that raw fish on the fire.", VillagerPromisePlanAction.Cook, ["cook"]),
        ("Dig into the soft ground here.", VillagerPromisePlanAction.Dig, ["dig"])
    };
    var liveWorld = new NpcAiWorldObservation[]
    {
        new("logs", ItemIds.Logs, "item", 2, "", true),
        new("berries", ItemIds.WildBerries, "item", 2, "joan", true),
        new("cave", ItemIds.CaveEntrance, "item", 3, "", true),
        new("campfire", ItemIds.Campfire, "item", 2, "", true),
        new("storage", ItemIds.StorageChest, "storage", 2, "", true),
        new("tree", ItemIds.PalmLogs, "tree", 3, "", true),
        new("iron", ItemIds.IronOre, "ore", 3, "", true),
        new("fish", ItemIds.RawMinnows, "fish", 3, "", true)
    };
    var liveOpcodeFailures = new List<string>();
    foreach (var liveCase in liveOpcodeCases)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var interpretation = await liveProposalAi.InterpretAsync(
            defaultAi,
            new(
                "player-requester", "Sam", "joan", "Joan",
                liveCase.Speech,
                [new("joan", "Joan", 1, 90, "trusts")],
                [],
                [],
                Personality: "cooperative, practical, willing to help Sam",
                KnownToolIds:
                [
                    ItemIds.StoneAxe, ItemIds.StonePickaxe,
                    ItemIds.StoneShovel, ItemIds.StoneKnife
                ],
                RecentConversation:
                [
                    new("joan", "Joan",
                        "I am willing to help with a reasonable task.", 400)
                ],
                Self: new(
                    100, 90,
                    [
                        ItemIds.WildBerries, ItemIds.Logs,
                        ItemIds.StoneAxe, ItemIds.StonePickaxe,
                        ItemIds.StoneShovel, ItemIds.RawMinnows
                    ],
                    "Idle", "Idle", [], []),
                NearbyWorld: liveWorld));
        timer.Stop();
        var actionMatches = interpretation is not null &&
                            liveCase.Actions.Contains(
                                interpretation.Action,
                                StringComparer.Ordinal);
        var compiled = interpretation is null
            ? advancedVillagers[0]
            : VillagerPromisePlanService.CompileAiDirective(
                advancedVillagers[0] with { ActionPlan = null },
                interpretation.Action,
                interpretation.ItemId,
                Math.Max(1, interpretation.Quantity),
                "player-requester", 4, 5,
                (int)WorldLevel.Overworld, 600,
                interpretation.DelayMinutes);
        var passed = interpretation?.Decision is
                         "accept" or "refuse" or "negotiate" or "clarify" &&
                     actionMatches &&
                     VillagerPromisePlanService.CurrentDirective(compiled)?.Action ==
                         liveCase.Opcode;
        if (!passed)
            liveOpcodeFailures.Add(
                $"'{liveCase.Speech}' expected {liveCase.Opcode}; " +
                $"decision={interpretation?.Decision}, action={interpretation?.Action}, " +
                $"item={interpretation?.ItemId}");
        Console.WriteLine(
            $"Live opcode {liveCase.Opcode,-20} {timer.ElapsedMilliseconds,6} ms | " +
            $"decision={interpretation?.Decision,-9} action={interpretation?.Action,-14} " +
            $"item={interpretation?.ItemId,-20} | {liveCase.Speech}");
    }
    Require(
        liveOpcodeFailures.Count == 0,
        "live Ollama opcode failures: " +
        string.Join("; ", liveOpcodeFailures));
}
using (var disabledAi = new NpcAiService(
           new HttpClient(new StubHttpHandler(_ =>
               throw new InvalidOperationException(
                   "disabled AI must not make requests")))))
{
    var disabledState = await disabledAi.CheckAsync(
        defaultAi with { Enabled = false });
    Require(
        disabledState.Availability ==
            NpcAiAvailability.Disabled,
        "disabled AI must remain fail-closed without network work");
}
using (var missingAi = new NpcAiService(
           new HttpClient(new StubHttpHandler(_ =>
               StubHttpHandler.Json(
                   """{"models":[]}""")))))
{
    var missingState = await missingAi.CheckAsync(defaultAi);
    Require(
        missingState.Availability ==
            NpcAiAvailability.ModelMissing,
        "AI startup must require the configured model to be installed");
}
using (var readyAi = new NpcAiService(
           new HttpClient(new StubHttpHandler(request =>
               request.RequestUri?.AbsolutePath == "/api/tags"
                   ? StubHttpHandler.Json(
                       """
                       {"models":[{"name":"Gemma4:12B","model":"Gemma4:12B"}]}
                       """)
                   : StubHttpHandler.Json(
                       """{"response":"READY","done":true}""")))))
{
    var readyState = await readyAi.CheckAsync(defaultAi);
    Require(
        readyState.Availability ==
            NpcAiAvailability.Ready,
        "AI runtime state must require a successful live model response");
}
using (var interpretingAi = new NpcAiService(
           new HttpClient(new StubHttpHandler(_ =>
               StubHttpHandler.Json(
                   """
                   {"response":"{\"addressedActorId\":\"mira\",\"referencedActorId\":\"invented\",\"desire\":\"food\",\"action\":\"give\",\"itemId\":\"cooked_minnows\",\"quantity\":999,\"sentiment\":-999,\"goal\":\"help Mira\",\"memory\":\"Mira asked for food\",\"reply\":\"I can help.\",\"freeformThought\":false}","done":true}
                   """)))))
{
    var interpretation = await interpretingAi.InterpretAsync(
        defaultAi,
        new(
            "speaker",
            "Sam",
            "mira",
            "Mira",
            "Can you give Mira food?",
            [new("mira", "Mira", 2, 20, "neutral")],
            [],
            [],
            Self: new(
                100,
                80,
                [ItemIds.CookedMinnows],
                "Idle",
                "Idle",
                [],
                [])));
    Require(
        interpretation is
        {
            AddressedActorId: "mira",
            ReferencedActorId: "",
            Quantity: 100,
            Sentiment: -100,
            Action: "give"
        } &&
        !typeof(NpcAiSpeechContext).GetProperties()
            .Any(property =>
                property.Name.Contains(
                    "Player",
                    StringComparison.OrdinalIgnoreCase)),
        "AI interpretations must validate actor references and clamp untrusted structured output");
}
var supportedNpcWorldActions = new[]
{
    "cut_tree", "gather_sticks", "gather_berries", "gather_fibre",
    "fish", "craft", "build", "cook", "light_fire", "mine", "dig",
    "enter_cave", "board_boat", "drop", "withdraw", "attack", "flee"
};
foreach (var expectedAction in supportedNpcWorldActions)
{
    using var actionAi = new NpcAiService(new HttpClient(
        new StubHttpHandler(_ => StubHttpHandler.Json(
            $$"""
            {"response":"{\"addressedActorId\":\"mira\",\"action\":\"{{expectedAction}}\",\"reply\":\"I will try.\",\"decision\":\"accept\"}","done":true}
            """))));
    var parsed = await actionAi.InterpretAsync(
        defaultAi,
        new("speaker", "Sam", "mira", "Mira", "Please do that.",
            [new("mira", "Mira", 2, 20, "neutral")], [], []));
    Require(parsed is not null && parsed.Action == expectedAction,
        $"NPC AI action '{expectedAction}' must survive structured validation");
}
using (var dialogueAi = new NpcAiService(
           new HttpClient(new StubHttpHandler(_ =>
               StubHttpHandler.Json(
                   """
                   {"response":"{\"reply\":\"I'm Mira. Do you remember anything before the beach?\"}","done":true}
                   """)))))
{
    var dialogue = await dialogueAi.ComposeDialogueAsync(
        defaultAi,
        new(
            "Mira",
            "Sam",
            VillagerSocialIntent.Introduce.ToString(),
            "I'm Mira. What's your name?",
            82,
            "neutral",
            []));
    Require(
        dialogue ==
            "I'm Mira. Do you remember anything before the beach?",
        "the model must fill natural dialogue without controlling the underlying social intent");
}
using (var biographyDumpAi = new NpcAiService(
           new HttpClient(new StubHttpHandler(_ =>
               StubHttpHandler.Json(
                   """
                   {"response":"{\"addressedActorId\":\"mira\",\"referencedActorId\":\"speaker\",\"desire\":\"introduce\",\"action\":\"\",\"itemId\":\"\",\"quantity\":0,\"sentiment\":0,\"goal\":\"\",\"memory\":\"Samuel introduced himself\",\"reply\":\"Mira was a skilled cartographer from a coastal village where she spent her life mapping trade routes and remembering every detail of her former home.\",\"freeformThought\":false}","done":true}
                   """)))))
{
    var interpretation = await biographyDumpAi.InterpretAsync(
        defaultAi,
        new(
            "speaker", "Samuel", "mira", "Mira", "Samuel",
            [new("mira", "Mira", 1, 80, "neutral")],
            [], []));
    Require(
        interpretation is { Reply: "" },
        "player-response interpretation must reject third-person biography dumps");
}
using (var echoingAi = new NpcAiService(
           new HttpClient(new StubHttpHandler(_ =>
               StubHttpHandler.Json(
                   """
                   {"response":"{\"addressedActorId\":\"mira\",\"referencedActorId\":\"speaker\",\"desire\":\"hostile\",\"action\":\"\",\"itemId\":\"\",\"quantity\":0,\"sentiment\":-40,\"goal\":\"\",\"memory\":\"Samuel insulted Mira\",\"reply\":\"Nice to meet you, fuck off.\",\"freeformThought\":false}","done":true}
                   """)))))
{
    var interpretation = await echoingAi.InterpretAsync(
        defaultAi,
        new(
            "speaker", "Samuel", "mira", "Mira", "fuck off",
            [new("mira", "Mira", 1, 80, "neutral")],
            [], []));
    Require(
        interpretation is { Reply: "", Sentiment: -40 },
        "NPC replies must retain negative sentiment while rejecting echoed player abuse");
}
using (var placeholderDialogueAi = new NpcAiService(
           new HttpClient(new StubHttpHandler(_ =>
               StubHttpHandler.Json(
                   """
                   {"response":"{\"reply\":\"none\"}","done":true}
                   """)))))
{
    Require(
        await placeholderDialogueAi.ComposeDialogueAsync(
            defaultAi,
            new(
                "Mira", "Sam", "AskOrigin",
                "How did we get here?", 82, "neutral", [])) is null,
        "placeholder model dialogue must be rejected so deterministic speech can take over");
}
using (var narrationDialogueAi = new NpcAiService(
           new HttpClient(new StubHttpHandler(_ =>
               StubHttpHandler.Json(
                   """
                   {"response":"{\"reply\":\"Mira walks closer and feels worried about the mysterious island.\"}","done":true}
                   """)))))
{
    Require(
        await narrationDialogueAi.ComposeDialogueAsync(
            defaultAi,
            new(
                "Mira", "Sam", "Introduce",
                "I'm Mira. What's your name?", 82, "neutral", [])) is null,
        "third-person model narration must be rejected instead of appearing as NPC speech");
}
using (var repeatingDialogueAi = new NpcAiService(
           new HttpClient(new StubHttpHandler(_ =>
               StubHttpHandler.Json(
                   """
                   {"response":"{\"reply\":\"I'm Mira. What's your name? I'm Mira. What's your name?\"}","done":true}
                   """)))))
{
    Require(
        await repeatingDialogueAi.ComposeDialogueAsync(
            defaultAi,
            new(
                "Mira", "Sam", "Introduce",
                "I'm Mira. What's your name?", 82, "neutral", [])) is null,
        "repeating model clauses must be rejected so the deterministic line appears once");
}
using (var embeddedSpeakerEchoAi = new NpcAiService(
           new HttpClient(new StubHttpHandler(_ =>
               StubHttpHandler.Json(
                   """
                   {"response":"{\"reply\":\"I'm Tomas. It's good to meet you, M: I'm Mira. What's your name?\"}","done":true}
                   """)))))
{
    Require(
        await embeddedSpeakerEchoAi.ComposeDialogueAsync(
            defaultAi,
            new(
                "Tomas", "Mira", "Introduce",
                "I'm Tomas. It's good to meet you, Mira.",
                99, "neutral", [],
                RecentConversation:
                [
                    new(
                        "mira", "Mira",
                        "I'm Mira. What's your name?", 1)
                ])) is null,
        "the exact embedded speaker-label echo seen in live Observe mode must be rejected");
}
using (var personaAi = new NpcAiService(
           new HttpClient(new StubHttpHandler(_ =>
               StubHttpHandler.Json(
                   """
                   {"response":"{\"people\":[{\"backgroundStory\":\"A cooper who repaired water barrels in a harbour town.\",\"personality\":\"Curious but careful.\",\"priorTrade\":\"Structural engineer\",\"knownToolIds\":[\"stone_hammer\",\"laser_rifle\"],\"arrivalMemory\":\"Woke beside broken timber at dawn.\",\"socialDrive\":\"Needs to learn who remembers the wreck.\"}]}","done":true}
                   """)))))
{
    var personas = await personaAi.GeneratePersonasAsync(
        defaultAi, "Test Island", 42, ["Mira", "Tomas", "Rowan"]);
    var personaGenders = VillagerSimulation.GendersForPopulation(3, 42);
    Require(
        personas is { Count: 3 } &&
        personas[0] is
        {
            KnownToolIds.Count: 1
        } persona &&
        MedievalDemographics.IsTradeCompatible(
            persona.PriorTrade, personaGenders[0]) &&
        persona.KnownToolIds[0] == ItemIds.StoneHammer &&
        personas[1].PriorTrade ==
            VillagerSimulation.DefaultPersona(
                1, personaGenders[1]).PriorTrade &&
        personas[2].PriorTrade ==
            VillagerSimulation.DefaultPersona(
                2, personaGenders[2]).PriorTrade &&
        HistoricalKnowledgePolicy.IsPlausible(personas[0].BackgroundStory),
        "world creation must complete partial casts, reject invented tools, and replace post-1200 AD or sex-incompatible trades");
}

var miraOwner = ItemOwner.Character("mira");
var playerOwner = ItemOwner.Character("player");
var privateAxe = new ItemOwnership(
    miraOwner,
    AcquiredBy: OwnershipAcquisition.Crafted);
Require(
    ItemOwnershipService.IsAuthorized(
        privateAxe, "mira", OwnershipAction.Use) &&
    !ItemOwnershipService.IsAuthorized(
        privateAxe, "player", OwnershipAction.Take) &&
    ItemOwnershipService.IsAuthorized(
        ItemOwnership.Unclaimed, "player", OwnershipAction.Take) &&
    ItemOwnershipService.Transfer(
        privateAxe,
        playerOwner,
        OwnershipAcquisition.Gifted,
        120).Owner == playerOwner,
    "ownership authorization and explicit transfers must distinguish owner, access, and acquisition");
var knowledge = new OwnershipKnowledge();
var ownedAxeId = Guid.NewGuid();
knowledge.Observe(new(
    ownedAxeId,
    "tomas",
    "player",
    "mira",
    OwnershipEvidenceKind.Witnessed,
    1,
    140));
Require(
    knowledge.TryGet(ownedAxeId, out var axeBelief) &&
    axeBelief.BelievedOwnerId == "mira" &&
    axeBelief.SuspectedHolderId == "player" &&
    axeBelief.Confidence == 1,
    "villagers must learn ownership from evidence rather than global knowledge");
var relationships = new RelationshipLedger();
var theftIncident = new OwnershipIncident(
    ownedAxeId,
    ItemIds.BronzeAxe,
    "mira",
    "player",
    1,
    20,
    1,
    Returned: false,
    WasEmergency: false);
var damagedRelationship =
    relationships.Apply("mira", "player", theftIncident);
Require(
    damagedRelationship.Trust < 0 &&
    damagedRelationship.Resentment > 0 &&
    ItemOwnershipService.Assess(
        theftIncident, damagedRelationship) >=
        OwnershipReaction.DemandCompensation &&
    relationships.Get("player", "mira") == default,
    "ownership incidents must change directional relationships and escalate reactions");
var ownershipBenchmark = System.Diagnostics.Stopwatch.StartNew();
for (var index = 0; index < 100_000; index++)
{
    _ = ItemOwnershipService.IsAuthorized(
        privateAxe,
        index % 2 == 0 ? "mira" : "player",
        OwnershipAction.Take);
    _ = relationships.Get("mira", "player");
}
ownershipBenchmark.Stop();
Require(
    ownershipBenchmark.ElapsedMilliseconds < 1000,
    "indexed ownership and relationship checks must remain suitable for simulation hot paths");

var villagerSpawnA = VillagerSimulation.CreateInitial(
    2187, Vector2.Zero);
var villagerSpawnB = VillagerSimulation.CreateInitial(
    2187, Vector2.Zero);
var soloVillagerSpawn = VillagerSimulation.CreateInitial(
    2187, Vector2.Zero, population: 0);
var twoVillagerSpawn = VillagerSimulation.CreateInitial(
    2187, Vector2.Zero, population: 2);
Require(
    VillagerSimulationClock.ReconcileWorldTime(
        100,
        [villagerSpawnA[0] with
        {
            LastSimulatedGameSeconds = 175,
            NextDecisionGameSeconds = 200
        }]) == 175 &&
    VillagerSimulationClock.ReconcileWorldTime(
        250,
        [villagerSpawnA[0] with
        {
            LastSimulatedGameSeconds = 175
        }]) == 250,
    "loading must reconcile a stale world clock from the last simulated villager time without advancing to an unprocessed future deadline");
var oneRealMinuteLater = VillagerSimulation.CatchUp(
    villagerSpawnA[0],
    VillagerSimulation.GameSecondsPerRealSecond * 60);
Require(
    MathF.Abs(
        oneRealMinuteLater.Hunger -
        (SurvivalService.MaximumHunger -
         SurvivalService.BaseHungerLossPerSecond * 60)) <
    .001f &&
    oneRealMinuteLater.Health ==
        AdventureService.BaseMaximumHealth,
    "villager survival catch-up must convert accelerated game time back to real seconds");
var sharedMinute = SurvivalService.Advance(
    villagerSpawnA[0].Hunger,
    villagerSpawnA[0].WellFedSeconds,
    villagerSpawnA[0].Health,
    60);
Require(
    MathF.Abs(oneRealMinuteLater.Hunger - sharedMinute.Hunger) < .001f &&
    oneRealMinuteLater.Health == sharedMinute.Health &&
    MathF.Abs(oneRealMinuteLater.WellFedSeconds -
              sharedMinute.WellFedSeconds) < .001f,
    "villager hunger, health, and well-fed catch-up must exactly use the player's shared survival result");
var carriedMealStart = villagerSpawnA[0] with
{
    Hunger = 34,
    Health = 80,
    Inventory = [ItemIds.WildBerries, null, null],
    LastSimulatedGameSeconds = 0,
    SurvivalTimeScaleVersion = 1
};
var carriedMealCatchUp = VillagerSimulation.CatchUp(
    carriedMealStart,
    VillagerSimulation.GameSecondsPerRealSecond);
Require(
    carriedMealCatchUp.Inventory.All(value => value is null) &&
    carriedMealCatchUp.Hunger > carriedMealStart.Hunger &&
    carriedMealCatchUp.Health >= carriedMealStart.Health &&
    carriedMealCatchUp.Memories?.Any(value =>
        value.Summary?.Contains(
            "after eating", StringComparison.Ordinal) == true) == true,
    "off-level catch-up must consume carried food and immediately record the meal");
var crossingMealStart = carriedMealStart with
{
    Hunger = 36,
    Inventory = [ItemIds.WildBerries, null, null],
    Memories = null
};
var crossingMealCatchUp = VillagerSimulation.CatchUp(
    crossingMealStart,
    VillagerSimulation.GameSecondsPerRealSecond * 24);
Require(
    crossingMealCatchUp.Inventory.All(value => value is null) &&
    crossingMealCatchUp.Health >= crossingMealStart.Health &&
    crossingMealCatchUp.Hunger >
        VillagerFoodService.UrgentHungerThreshold,
    "a long catch-up chunk must stop at the urgent threshold and eat before starvation");
var mealConsistencyStart = carriedMealStart with
{
    Hunger = 50,
    Inventory =
    [
        ItemIds.WildBerries,
        ItemIds.TropicalBerries,
        ItemIds.CookedMinnows
    ],
    Memories = null
};
var singleMealCatchUp = VillagerSimulation.CatchUp(
    mealConsistencyStart,
    VillagerSimulation.GameSecondsPerRealSecond * 360);
var steppedMealCatchUp = mealConsistencyStart;
for (var step = 1; step <= 36; step++)
    steppedMealCatchUp = VillagerSimulation.CatchUp(
        steppedMealCatchUp,
        VillagerSimulation.GameSecondsPerRealSecond * step * 10);
Require(
    MathF.Abs(singleMealCatchUp.Hunger - steppedMealCatchUp.Hunger) < .001f &&
    singleMealCatchUp.Health == steppedMealCatchUp.Health &&
    singleMealCatchUp.Inventory.SequenceEqual(steppedMealCatchUp.Inventory),
    "carried-food catch-up must match equivalent smaller simulation steps");
var deadCarriedMeal = VillagerSimulation.CatchUp(
    carriedMealStart with
    {
        Health = 0,
        Hunger = 0,
        DeathCause = "Defeated.",
        Inventory = [ItemIds.WildBerries, null, null]
    },
    VillagerSimulation.GameSecondsPerRealSecond * 60);
Require(
    deadCarriedMeal.Inventory[0] == ItemIds.WildBerries &&
    deadCarriedMeal.Hunger == 0 &&
    deadCarriedMeal.Health == 0,
    "dead villagers must not consume carried food during catch-up");
var wellFedStart = villagerSpawnA[0] with
{
    Hunger = 70,
    Health = 80,
    WellFedSeconds = 120
};
var wellFedVillagerMinute = VillagerSimulation.CatchUp(
    wellFedStart,
    wellFedStart.LastSimulatedGameSeconds +
    VillagerSimulation.GameSecondsPerRealSecond * 60);
var wellFedPlayerMinute = SurvivalService.Advance(70, 120, 80, 60);
var wellFedPlayerRegeneration =
    EntityHealthRegenerationService.Advance(
        wellFedPlayerMinute.Health,
        AdventureService.BaseMaximumHealth,
        60);
Require(
    MathF.Abs(wellFedVillagerMinute.Hunger -
              wellFedPlayerMinute.Hunger) < .001f &&
    wellFedVillagerMinute.Health == wellFedPlayerRegeneration.Health &&
    MathF.Abs(wellFedVillagerMinute.WellFedSeconds -
              wellFedPlayerMinute.WellFedSeconds) < .001f,
    "villager well-fed duration and hunger protection must match the player exactly");
var halfRecovery = EntityHealthRegenerationService.Advance(
    50, 100, 6);
var accumulatedRecovery = EntityHealthRegenerationService.Advance(
    halfRecovery.Health, 100, 6, remainder: halfRecovery.Remainder);
var fireRecovery = EntityHealthRegenerationService.Advance(
    50,
    100,
    .6f,
    EntityHealthRegenerationService.LitCampfireHumanMultiplier);
var deadRecovery = EntityHealthRegenerationService.Advance(
    0,
    100,
    120,
    EntityHealthRegenerationService.LitCampfireHumanMultiplier,
    .5f);
var fireRecoveredVillager = VillagerSimulation.CatchUp(
    villagerSpawnA[0] with
    {
        Health = 50,
        LastSimulatedGameSeconds = 0
    },
    VillagerSimulation.GameSecondsPerRealSecond * .6,
    healthRegenerationMultiplier:
        EntityHealthRegenerationService.LitCampfireHumanMultiplier);
Require(
    EntityHealthRegenerationService.BaseHealthPerSecond ==
        SurvivalService.BaseHungerLossPerSecond &&
    halfRecovery.Health == 50 &&
    MathF.Abs(halfRecovery.Remainder - .5f) < .001f &&
    accumulatedRecovery.Health == 51 &&
    MathF.Abs(accumulatedRecovery.Remainder) < .001f &&
    fireRecovery.Health == 51 &&
    fireRecoveredVillager.Health == 51 &&
    deadRecovery.Health == 0 &&
    MathF.Abs(deadRecovery.Remainder - .5f) < .001f,
    "all living entities must share fractional health regeneration while humans recover twenty times faster by lit fires");
var starvingStart = villagerSpawnA[0] with
{
    Hunger = 0,
    Health = 5,
    WellFedSeconds = 0
};
var starvedVillager = VillagerSimulation.CatchUp(
    starvingStart,
    starvingStart.LastSimulatedGameSeconds +
    VillagerSimulation.GameSecondsPerRealSecond * 10);
var starvedPlayer = SurvivalService.Advance(0, 0, 5, 10);
Require(
    starvedVillager.Health == starvedPlayer.Health &&
    starvedVillager.Health == 0,
    "villager starvation damage and death must match the player exactly");
var repairedLegacyStarvation =
    VillagerSimulation.CatchUp(
        villagerSpawnA[0] with
        {
            Health = 0,
            Hunger = 0,
            SurvivalTimeScaleVersion = 0
        },
        0);
var preservedViolenceDefeat =
    VillagerSimulation.CatchUp(
        repairedLegacyStarvation with
        {
            Health = 0,
            Hunger = 0,
            SurvivalTimeScaleVersion = 0,
            Memories =
            [
                new(
                    Guid.NewGuid(),
                    "violence",
                    "player",
                    null,
                    1,
                    0,
                    -100,
                    "Samuel attacked Mira.")
            ]
        },
        0);
Require(
    repairedLegacyStarvation.Health ==
        AdventureService.BaseMaximumHealth &&
    repairedLegacyStarvation.Hunger == 25 &&
    repairedLegacyStarvation.SurvivalTimeScaleVersion == 1 &&
    preservedViolenceDefeat.Health == 0,
    "the survival time-scale migration must repair starvation-corrupted saves without reviving violence deaths");
Require(soloVillagerSpawn.Length == 0,
    "zero-population worlds must remain solo");
Require(twoVillagerSpawn.Length == 2,
    "villager spawning must respect the world's requested population");
Require(
    villagerSpawnA.Select(value => (
            value.Id, value.Name,
            value.PositionX, value.PositionY,
            value.Sociability, value.Honesty, value.Boldness))
        .SequenceEqual(villagerSpawnB.Select(value => (
            value.Id, value.Name,
            value.PositionX, value.PositionY,
            value.Sociability, value.Honesty, value.Boldness))) &&
    villagerSpawnA.Select(value => value.Id).Distinct().Count() ==
        VillagerSimulation.InitialPopulation &&
    villagerSpawnA.All(value =>
        value.Inventory.Length == PlayerInventory.Capacity),
    "initial villagers must have deterministic permanent identities and player-sized inventories");
Require(
    villagerSpawnA.All(value =>
        value.Persona is not null &&
        value.Persona.KnownToolIds.Count > 0 &&
        VillagerSimulation.HoursOnIsland(
            value,
            value.AwakenedGameSeconds + 7200) == 2),
    "villagers must retain tool knowledge and calculate what their timeline permits them to know");
Require(
    VillagerSimulation.Tier(
        Vector2.Zero, Vector2.Zero) ==
        VillagerSimulationTier.Nearby &&
    VillagerSimulation.Tier(
        new(64, 0), Vector2.Zero) ==
        VillagerSimulationTier.Regional &&
    VillagerSimulation.Tier(
        new(256, 0), Vector2.Zero) ==
        VillagerSimulationTier.Distant &&
    VillagerSimulation.DecisionInterval(
        VillagerSimulationTier.Distant) >
        VillagerSimulation.DecisionInterval(
            VillagerSimulationTier.Nearby),
    "villager simulation frequency must decrease with distance");
Require(
    VillagerSimulation.NearbyDecisionSeconds /
        VillagerSimulation.GameSecondsPerRealSecond == 3 &&
    VillagerSimulation.GatherPauseSeconds /
        VillagerSimulation.GameSecondsPerRealSecond == 2 &&
    VillagerSimulation.SocialCooldownRealSeconds >= 90,
    "ordinary decisions, gathering, and social speech cooldowns must be authored in real-time seconds");
var waitingStatusVillager = villagerSpawnA[0] with
{
    LastDeliberation = null,
    NextDecisionGameSeconds = 1_120,
    WorkRole = VillagerWorkRole.Wood
};
Require(
    VillagerStatusService.CurrentThought(
        waitingStatusVillager, 1_000).Contains("2.0s") &&
    VillagerStatusService.CurrentThought(
        waitingStatusVillager with
        {
            NextDecisionGameSeconds = 1_000
        }, 1_000).Contains("wood task") &&
    VillagerStatusService.CurrentThought(
        waitingStatusVillager with
        {
            Activity = VillagerActivity.Resting,
            Energy = 10
        }, 1_000).Contains("50"),
    "observation status must explain idle cooldown, role work, and genuine rest");
var hungryVillagerInventory = PlayerInventory.CreateStartingInventory();
hungryVillagerInventory[0] = ItemIds.CookedMinnows;
var hungryVillager = villagerSpawnA[0] with
{
    Hunger = 10,
    Inventory = hungryVillagerInventory
};
var hungryDecision = VillagerSimulation.Decide(
    hungryVillager, Vector2.Zero, 100);
var fedVillager = VillagerSimulation.ApplyDecision(
    hungryVillager,
    hungryDecision,
    VillagerSimulationTier.Nearby,
    100);
SurvivalService.TryFoodEffect(
    ItemIds.CookedMinnows, out var sharedFoodEffect);
var expectedMeal = SurvivalService.Eat(
    sharedFoodEffect,
    hungryVillager.Hunger,
    hungryVillager.WellFedSeconds,
    hungryVillager.Health,
    AdventureService.BaseMaximumHealth);
Require(
    hungryDecision.Need == VillagerNeed.Food &&
    hungryDecision.ConsumeSlot == 0 &&
    fedVillager.Inventory[0] is null &&
    fedVillager.Hunger == expectedMeal.Hunger &&
    fedVillager.Health == expectedMeal.Health &&
    fedVillager.WellFedSeconds == expectedMeal.WellFedSeconds,
    "villager food hunger, healing, and well-fed effects must exactly match the player's shared meal result");

var workCoordinator = new VillagerWorkCoordinator();
Require(
    workCoordinator.TryReserve("tree:one", "mira", 100) &&
    !workCoordinator.IsAvailable("tree:one", "rowan", 100) &&
    workCoordinator.IsAvailable("tree:one", "mira", 100),
    "scarce villager targets must remain available only to their owner");
Require(
    workCoordinator.TryReserve("fish:two", "mira", 110) &&
    workCoordinator.IsAvailable("tree:one", "rowan", 110) &&
    workCoordinator.Count == 1,
    "reserving a new target must atomically release an actor's prior target");
workCoordinator.Expire(
    110 + VillagerWorkCoordinator.ReservationSeconds + 1);
Require(
    workCoordinator.IsAvailable("fish:two", "rowan", 999) &&
    workCoordinator.Count == 0,
    "abandoned target reservations must expire without accumulating state");
foreach (var actorState in new[] { "dead", "idle", "abandoned", "role-changed" })
{
    var target = $"reservation:{actorState}";
    Require(workCoordinator.TryReserve(target, actorState, 200),
        $"{actorState} villager must be able to reserve a work target");
    workCoordinator.ReleaseActor(actorState);
    Require(workCoordinator.IsAvailable(target, "other", 200) &&
            workCoordinator.Count == 0,
        $"{actorState} villager must immediately release its work reservation");
}
var woodInventory = PlayerInventory.CreateStartingInventory();
woodInventory[0] = ItemIds.StoneAxe;
var roleAssignments = VillagerWorkCoordinator.AssignRoles([
    hungryVillager with { Id = "hungry", Hunger = 10 },
    hungryVillager with
    {
        Id = "woodworker",
        Hunger = 90,
        Inventory = woodInventory,
        WoodcuttingExperience = 500
    },
    hungryVillager with { Id = "third", Hunger = 80 }
]);
Require(
    roleAssignments["hungry"] != VillagerWorkRole.Food &&
    roleAssignments["third"] == VillagerWorkRole.Food &&
    roleAssignments["woodworker"] == VillagerWorkRole.Wood &&
    roleAssignments.Values.Distinct().Count() == 3,
    "temporary roles must protect a near-starving villager while covering food, specialist wood work, and a complementary third job");
var scaledWorkforce = Enumerable.Range(0, 10).Select(index =>
    hungryVillager with
    {
        Id = $"scaled-worker-{index}",
        Hunger = 80,
        Health = 100,
        Inventory = PlayerInventory.CreateStartingInventory(),
        Activity = index == 8
            ? VillagerActivity.Conversing
            : index == 9
                ? VillagerActivity.Blocked
                : VillagerActivity.Idle
    }).ToArray();
var scaledRoles = VillagerWorkCoordinator.AssignRoles(scaledWorkforce);
Require(!VillagerWorkCoordinator.IsAvailableForWork(scaledWorkforce[8]) &&
        !VillagerWorkCoordinator.IsAvailableForWork(scaledWorkforce[9]) &&
        scaledRoles[scaledWorkforce[8].Id] == VillagerWorkRole.Unassigned &&
        scaledRoles[scaledWorkforce[9].Id] == VillagerWorkRole.Unassigned &&
        scaledRoles.Values.Count(value => value == VillagerWorkRole.Food) == 3 &&
        scaledRoles.Values.Count(value => value == VillagerWorkRole.Wood) == 2 &&
        scaledRoles.Values.Any(value => value is
            VillagerWorkRole.Crafting or VillagerWorkRole.Exploration),
    "role planning must exclude unavailable villagers while scaling food and wood coverage to settlement deficits without consuming every worker");
var projectVillagers = new[]
{
    hungryVillager with
    {
        Id = "project-food",
        Hunger = 80,
        WorkRole = VillagerWorkRole.Food
    },
    hungryVillager with
    {
        Id = "project-wood",
        Hunger = 80,
        WorkRole = VillagerWorkRole.Wood,
        Inventory = woodInventory
    },
    hungryVillager with
    {
        Id = "project-builder",
        Hunger = 80,
        WorkRole = VillagerWorkRole.Crafting
    }
};
var campfireProject = VillagerSettlementProjectService.Plan(
    projectVillagers,
    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
Require(campfireProject is
        {
            ProjectItemId: ItemIds.Campfire,
            BuilderId: "project-builder"
        } &&
        campfireProject.Assignments.Values
            .SelectMany(value => value)
            .Where(value => value.ItemId == ItemIds.LargeRock)
            .Sum(value => value.Quantity) == 3 &&
        VillagerSettlementProjectService.SuggestedContribution(
            campfireProject) is
        {
            ItemId: ItemIds.LargeRock,
            Quantity: 3
        },
    "settlement planning must select a crafter, divide resources, and expose one reusable contribution request for a participating player");
var unavailableBuilderProject = VillagerSettlementProjectService.Plan(
    projectVillagers.Select(value => value with
    {
        Activity = value.Id == "project-builder"
            ? VillagerActivity.Blocked
            : VillagerActivity.Idle,
        ProjectAssignment = new VillagerProjectAssignment(
            ItemIds.Campfire,
            "project-builder",
            [new(ItemIds.LargeRock, 1)],
            AssignedGameSeconds: 0)
    }).ToArray(),
    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    gameSeconds:
        VillagerSettlementProjectService.BuilderReplacementDelayGameSeconds +
        1);
Require(unavailableBuilderProject is { BuilderId: "project-builder" },
    "temporary blocked activity must not churn a living project builder");
var incapacitatedBuilderProject = VillagerSettlementProjectService.Plan(
    projectVillagers.Select(value => value with
    {
        Health = value.Id == "project-builder" ? 20 : value.Health,
        ProjectAssignment = new VillagerProjectAssignment(
            ItemIds.Campfire,
            "project-builder",
            [new(ItemIds.LargeRock, 1)],
            AssignedGameSeconds: 0)
    }).ToArray(),
    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    gameSeconds:
        VillagerSettlementProjectService.BuilderReplacementDelayGameSeconds +
        1);
Require(incapacitatedBuilderProject is
        { BuilderId: not "project-builder" } &&
        !incapacitatedBuilderProject.Assignments.ContainsKey(
            "project-builder"),
    "an incapacitated project builder must still be replaced after the grace period");
var reassignedProjectVillagers = projectVillagers
    .Select(value => value with
    {
        WorkRole = value.Id == "project-food"
            ? VillagerWorkRole.Crafting
            : value.WorkRole,
        CraftingExperience = value.Id == "project-food" ? 10_000 : 0,
        ProjectAssignment = new(
            ItemIds.Campfire,
            "project-builder",
            [],
            100)
    })
    .ToArray();
Require(
    VillagerSettlementProjectService.Plan(
        reassignedProjectVillagers,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase))?.BuilderId ==
        "project-builder",
    "active settlement projects must retain their living builder across role reassignment");
var workbenchProject = VillagerSettlementProjectService.Plan(
    projectVillagers,
    new HashSet<string>([ItemIds.Campfire],
        StringComparer.OrdinalIgnoreCase));
var storageProject = VillagerSettlementProjectService.Plan(
    projectVillagers,
    new HashSet<string>([ItemIds.Campfire, ItemIds.Workbench],
        StringComparer.OrdinalIgnoreCase));
Require(workbenchProject?.ProjectItemId == ItemIds.Workbench &&
        workbenchProject.Assignments["project-wood"].Any(value =>
            value.ItemId == ItemIds.Logs) &&
        storageProject?.ProjectItemId == ItemIds.StorageChest &&
        VillagerSettlementProjectService.Plan(
            projectVillagers,
            new HashSet<string>(
                [ItemIds.Campfire, ItemIds.Workbench, ItemIds.StorageChest],
                StringComparer.OrdinalIgnoreCase))?.ProjectItemId ==
            ItemIds.WoodenWall,
    "settlement projects must progress from campfire through required workbench infrastructure, storage and a defensive wall");
Require(
    VillagerSettlementProjectService.Plan(
        projectVillagers,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        defensivePriority: true)?.ProjectItemId == ItemIds.WoodenWall,
    "serious unresolved attacks must prioritize a defensive wall without waiting for optional camp infrastructure");
var perimeterSites = VillagerSettlementProjectService.DefensivePerimeter(
    new Vector2(10, 20));
Require(perimeterSites.Count == 8 &&
        perimeterSites.Distinct().Count() == perimeterSites.Count &&
        VillagerSettlementProjectService.NextDefensiveWorksite(
            new Vector2(10, 20), perimeterSites.Take(7).ToArray()) ==
        perimeterSites[7] &&
        VillagerSettlementProjectService.NextDefensiveWorksite(
            new Vector2(10, 20), perimeterSites) is null,
    "defensive wall projects must advance through a bounded eight-segment perimeter and finish once every site is occupied");
var assignedProjectVillager = projectVillagers[0] with
{
    ProjectAssignment = new(
        ItemIds.Campfire,
        "project-builder",
        [new(ItemIds.LargeRock, 1)],
        AssignedGameSeconds: 100)
};
var accountableContributor = assignedProjectVillager with
{
    ProjectAssignment = assignedProjectVillager.ProjectAssignment! with
    {
        LeaderId = "project-leader"
    }
};
var selfAccountableLeader = accountableContributor with
{
    Id = "project-leader"
};
Require(
    VillagerSettlementProjectService.CanReceiveAccountabilityPrompt(
        accountableContributor) &&
    !VillagerSettlementProjectService.CanReceiveAccountabilityPrompt(
        selfAccountableLeader),
    "project accountability must target helpers without making leaders confront themselves");
var completedProjectBuilder = projectVillagers[2] with
{
    Inventory = PlayerInventory.CreateStartingInventory(),
    ProjectAssignment = new(
        ItemIds.Campfire,
        "project-builder",
        [],
        AssignedGameSeconds: 100)
};
completedProjectBuilder.Inventory[0] = ItemIds.Campfire;
Require(VillagerSettlementProjectService.NeedsItem(
            assignedProjectVillager, ItemIds.LargeRock) &&
        !VillagerSettlementProjectService.CanReceiveAccountabilityPrompt(
            completedProjectBuilder) &&
        VillagerSettlementProjectService.CarriesCompletedProject(
            completedProjectBuilder) &&
        !VillagerSettlementProjectService.CarriesCompletedProject(
            assignedProjectVillager) &&
        VillagerResourcePriority.Score(
            assignedProjectVillager, ItemIds.LargeRock) == 90 &&
        VillagerSettlementProjectService.IsStalled(
            assignedProjectVillager,
            100 +
            VillagerSettlementProjectService.AccountabilityDelayGameSeconds),
    "project assignments must drive resource gathering and expose stalled idle contributors for accountability");
var contributionInventory = new string?[28];
contributionInventory[0] = ItemIds.OakLogs;
var logContributor = assignedProjectVillager with
{
    Inventory = contributionInventory,
    ProjectAssignment = new(
        ItemIds.Workbench,
        "project-builder",
        [new(ItemIds.Logs, 1)],
        AssignedGameSeconds: 100)
};
Require(VillagerSettlementProjectService.ContributionSlot(
            logContributor,
            projectVillagers[2]) == 0 &&
        VillagerSettlementProjectService.MatchesRequirement(
            ItemIds.OakLogs, ItemIds.Logs),
    "project delivery must accept interchangeable log types through the shared contribution contract");
var opportunisticRockContributor = assignedProjectVillager with
{
    Inventory = new string?[PlayerInventory.Capacity],
    ProjectAssignment = assignedProjectVillager.ProjectAssignment! with
    {
        Requirements = []
    }
};
opportunisticRockContributor.Inventory[0] = ItemIds.LargeRock;
Require(
    VillagerSettlementProjectService.ContributionSlot(
        opportunisticRockContributor, projectVillagers[2]) == 0 &&
    VillagerSettlementProjectService.RequirementsFor(ItemIds.Campfire)
        .SequenceEqual([new(ItemIds.LargeRock, 3)]),
    "any participant carrying a needed project material must be allowed to deliver it even when someone else received the original order");
var projectCraftInventory = new string?[28];
projectCraftInventory[0] = ItemIds.SmallRocks;
projectCraftInventory[1] = ItemIds.SmallRocks;
projectCraftInventory[2] = ItemIds.Plank;
var projectBuilder = projectVillagers[2] with
{
    Inventory = projectCraftInventory,
    ProjectAssignment = new(
        ItemIds.Campfire,
        "project-builder",
        [],
        100)
};
Require(
    VillagerCraftPlanner.CraftingDependencyOrder(ItemIds.Campfire)
        .SequenceEqual(
            [ItemIds.MediumRock, ItemIds.SmallRocks, ItemIds.Campfire]) &&
    VillagerCraftPlanner.PriorityFor(projectBuilder).Take(3)
        .SequenceEqual(
            [ItemIds.MediumRock, ItemIds.SmallRocks, ItemIds.Campfire]),
    "project crafting must follow the recipe dependency graph before optional tool crafting");
var contributorWithRock = assignedProjectVillager with
{
    Inventory = new string?[PlayerInventory.Capacity]
};
contributorWithRock.Inventory[0] = ItemIds.LargeRock;
Require(
    VillagerCraftPlanner.ConsumesAssignedContribution(
        ItemIds.MediumRock, contributorWithRock) &&
    !VillagerCraftPlanner.ConsumesAssignedContribution(
        ItemIds.StoneKnife, contributorWithRock) &&
    Vector2.Distance(
        Vector2.Zero,
        VillagerSettlementProjectService.RendezvousPoint(
            Vector2.Zero, contributorWithRock.Id, isBuilder: false)) <
        VillagerSimulation.InteractionRange,
    "contributors must preserve assigned materials and receive a stable in-range rendezvous slot");
var completedCampfireRockInventory =
    (string?[])projectCraftInventory.Clone();
completedCampfireRockInventory[3] = ItemIds.SmallRocks;
Require(VillagerCraftPlanner.Needs(ItemIds.SmallRocks, projectBuilder) &&
        !VillagerCraftPlanner.Needs(
            ItemIds.SmallRocks,
            projectBuilder with
            {
                Inventory = completedCampfireRockInventory
            }) &&
        VillagerCraftPlanner.Needs(
            ItemIds.Plank,
            projectBuilder with
            {
                ProjectAssignment = projectBuilder.ProjectAssignment! with
                {
                    ProjectItemId = ItemIds.Workbench
                }
            }),
    "project crafting must continue until campfire rock and infrastructure plank quantities are complete");
var soloRoles = VillagerWorkCoordinator.AssignRoles([
    hungryVillager with { Id = "solo-builder", Hunger = 80 }
]);
var duoRoles = VillagerWorkCoordinator.AssignRoles([
    hungryVillager with { Id = "duo-food", Hunger = 80 },
    hungryVillager with { Id = "duo-builder", Hunger = 75 }
]);
var soloExplorerRoles = VillagerWorkCoordinator.AssignRoles([
    hungryVillager with
    {
        Id = "solo-explorer",
        Hunger = 80,
        MiningExperience = 10_000
    }
]);
var craftingDuoRoles = VillagerWorkCoordinator.AssignRoles([
    hungryVillager with
    {
        Id = "crafting-food",
        Hunger = 80,
        FishingExperience = 10_000
    },
    hungryVillager with
    {
        Id = "crafting-specialist",
        Hunger = 75,
        CraftingExperience = 10_000
    }
]);
Require(soloRoles.Values.All(value =>
            value == VillagerWorkRole.Unassigned) &&
        soloExplorerRoles.Values.All(value =>
            value == VillagerWorkRole.Unassigned) &&
        duoRoles.Values.All(value =>
            value == VillagerWorkRole.Unassigned) &&
        craftingDuoRoles.Values.All(value =>
            value == VillagerWorkRole.Unassigned) &&
        VillagerCraftPlanner.PriorityFor(VillagerWorkRole.Unassigned)
            .Contains(ItemIds.Campfire) &&
        VillagerCraftPlanner.PriorityFor(VillagerWorkRole.Unassigned)
            .Contains(ItemIds.StoneAxe) &&
        VillagerCraftPlanner.PriorityFor(VillagerWorkRole.Unassigned)
            .Contains(ItemIds.StonePickaxe),
    "one and two survivors must remain independent all-rounders with survival, crafting and exploration progression");
var independentPair = VillagerSimulation.CreateInitial(
    8442, Vector2.Zero, population: 2, gameSeconds: 100);
var independentRelationship = independentPair[0].Relationships!.Single();
var independentIntroduction = VillagerSimulation.SelectSocialGoal(
    independentPair[0] with { NextSocialGameSeconds = 0 },
    [
        new(
            independentPair[1].Id,
            VillagerSimulation.PerceivedName(
                independentPair[0], independentPair[1].Id),
            new(independentPair[1].PositionX, independentPair[1].PositionY),
            independentPair[1].WorldLevel,
            independentPair[1].Hunger,
            VillagerSimulation.CountFood(independentPair[1].Inventory))
    ],
    gameSeconds: 101);
var independentCamper = independentPair[0] with
{
    PositionX = 0,
    PositionY = 0,
    LocationMemories =
    [
        new(8, 0, 0, VillagerLocationType.FoodSource, .9f, 200),
        new(9, 1, 0, VillagerLocationType.WoodSource, .9f, 200),
        new(-8, 0, 0, VillagerLocationType.Danger, 1, 200)
    ]
};
independentCamper = IndependentSurvivorPolicy.ConsiderPersonalCamp(
    independentCamper,
    livingPopulation: 2,
    gameSeconds: 100 +
        IndependentSurvivorPolicy.CampDecisionDelayGameSeconds);
Require(
    independentRelationship.CharacterId == independentPair[1].Id &&
    independentRelationship.State.Trust < 0 &&
    independentPair[0].KnownPeople is
        [{ Stage: AcquaintanceStage.Seen }] &&
    independentIntroduction.Intent == VillagerSocialIntent.Introduce &&
    IndependentSurvivorPolicy.PersonalCamp(independentCamper) is { X: > 0 } &&
    VillagerSettlementProjectService.Plan(
        independentPair,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)) is null &&
    !IndependentSurvivorPolicy.CanFormSettlement(2) &&
    IndependentSurvivorPolicy.CanFormSettlement(3),
    "independent pairs must begin wary, talk directly, choose personal camps from observed survival resources, and avoid settlement projects");
var schismLeader = independentPair[0] with
{
    Id = "schism-leader",
    Boldness = .4f,
    Sociability = .6f
};
var schismSupporter = independentPair[1] with
{
    Id = "schism-supporter"
};
string? schismCandidateId = null;
IReadOnlySet<string>? schismDepartures = null;
for (var attempt = 0; attempt < 256 && schismCandidateId is null; attempt++)
{
    var candidateId = $"schism-candidate-{attempt}";
    var candidate = independentPair[1] with
    {
        Id = candidateId,
        Boldness = 1,
        Sociability = 0,
        Relationships =
        [
            new(schismLeader.Id, new(Trust: -60))
        ]
    };
    var result = new VillagerLeadershipResult(
        schismLeader.Id,
        [
            new(schismLeader.Id, schismLeader.Id, 10),
            new(schismSupporter.Id, schismLeader.Id, 8),
            new(candidate.Id, candidate.Id, 9)
        ],
        Contested: true);
    var departures = IndependentSurvivorPolicy.LeadershipDepartures(
        [schismLeader, schismSupporter, candidate], result);
    if (departures.Count == 0) continue;
    schismCandidateId = candidateId;
    schismDepartures = departures;
}
Require(
    schismDepartures is { Count: 1 } &&
    schismDepartures.Contains(schismCandidateId!) &&
    3 - schismDepartures.Count == 2 &&
    IndependentSurvivorPolicy.IsIndependent(
        schismSupporter with { IndependentByChoice = true },
        livingPopulation: 10),
    "a rejected leadership result may collapse a three-person settlement and solo-by-choice survivors must remain independent in larger populations");
var stableRoles = VillagerWorkCoordinator.AssignRoles([
    hungryVillager with
    {
        Id = "food-incumbent",
        Hunger = 70,
        WorkRole = VillagerWorkRole.Food
    },
    hungryVillager with
    {
        Id = "wood-incumbent",
        Hunger = 65,
        WorkRole = VillagerWorkRole.Wood
    }
]);
var urgentRoleChange = VillagerWorkCoordinator.AssignRoles([
    hungryVillager with
    {
        Id = "food-incumbent",
        Hunger = 70,
        WorkRole = VillagerWorkRole.Food
    },
    hungryVillager with
    {
        Id = "wood-incumbent",
        Hunger = 40,
        WorkRole = VillagerWorkRole.Wood
    }
]);
Require(
    stableRoles.Values.All(value =>
        value == VillagerWorkRole.Unassigned) &&
    urgentRoleChange.Values.All(value =>
        value == VillagerWorkRole.Unassigned),
    "two-person survivors must not retain stale specialist roles when entering independent all-rounder mode");
var skillAwareAxeInventory = PlayerInventory.CreateStartingInventory();
skillAwareAxeInventory[0] = ItemIds.StoneAxe;
var skillAwareKnifeInventory = PlayerInventory.CreateStartingInventory();
skillAwareKnifeInventory[0] = ItemIds.StoneKnife;
var skillAwareWorkers = new[]
{
    hungryVillager with
    {
        Id = "skilled-fisher",
        Hunger = 80,
        FishingExperience = 500
    },
    hungryVillager with
    {
        Id = "skilled-woodworker",
        Hunger = 80,
        Inventory = skillAwareAxeInventory,
        WoodcuttingExperience = 500
    },
    hungryVillager with
    {
        Id = "skilled-crafter",
        Hunger = 80,
        Inventory = skillAwareKnifeInventory,
        CraftingExperience = 500
    }
};
var resourceForecast = VillagerWorkPlanner.Forecast(skillAwareWorkers);
var skillAwareRoles = VillagerWorkCoordinator.AssignRoles(skillAwareWorkers);
Require(
    resourceForecast is
        { LivingPeople: 3, Food: 1, FoodDeficit: 5, WoodDeficit: 12 } &&
    skillAwareRoles["skilled-fisher"] == VillagerWorkRole.Food &&
    skillAwareRoles["skilled-woodworker"] == VillagerWorkRole.Wood &&
    skillAwareRoles["skilled-crafter"] == VillagerWorkRole.Crafting,
    "future work plans must combine demonstrated skills, best tools, and remaining group resource deficits");
var foodSupplyInventory = PlayerInventory.CreateStartingInventory();
foodSupplyInventory[0] = ItemIds.PlantFibres;
foodSupplyInventory[1] = ItemIds.PlantFibres;
Require(
    VillagerWorkSupplyPlanner.NeedsFibre(
        hungryVillager with
        {
            WorkRole = VillagerWorkRole.Food,
            Inventory = foodSupplyInventory
        }) &&
    !VillagerWorkSupplyPlanner.NeedsFibre(
        hungryVillager with
        {
            WorkRole = VillagerWorkRole.Wood,
            Inventory = foodSupplyInventory
        }) &&
    VillagerWorkSupplyPlanner.NeedsSticks(
        hungryVillager with
        {
            WorkRole = VillagerWorkRole.Wood,
            Inventory = foodSupplyInventory
        }),
    "work supply targets must send food workers toward fibre and wood workers toward axe prerequisites");
foodSupplyInventory[2] = ItemIds.PlantFibres;
Require(
    !VillagerWorkSupplyPlanner.NeedsFibre(
        hungryVillager with
        {
            WorkRole = VillagerWorkRole.Food,
            Inventory = foodSupplyInventory
        }),
    "food workers must stop at the first fibre quota so crafting can run before further gathering");
foodSupplyInventory[3] = ItemIds.Rope;
Require(
    VillagerWorkSupplyPlanner.NeedsFibre(
        hungryVillager with
        {
            WorkRole = VillagerWorkRole.Food,
            Inventory = foodSupplyInventory
        }),
    "after crafting rope, food workers must gather only the remaining fibre needed for a fishing net");

var sharedGatherInventory = PlayerInventory.CreateStartingInventory();
var sharedGather = EntityInteractionService.Gather(
    sharedGatherInventory, ItemIds.PlantFibres, 3);
Require(
    sharedGather.Succeeded &&
    sharedGather.Inventory.Count(value =>
        value == ItemIds.PlantFibres) == 3,
    "player and NPC gathering must share the same actor-neutral capacity mutation");
var ropeRecipe = CraftingSkill.Recipes.Single(value =>
    value.ResultItemId == ItemIds.Rope);
var sharedCraft = EntityInteractionService.Craft(
    sharedGather.Inventory,
    ropeRecipe,
    craftingLevel: 1,
    stationAvailable: true);
Require(
    sharedCraft.Succeeded &&
    sharedCraft.Inventory.Count(value => value == ItemIds.Rope) == 1 &&
    sharedCraft.Inventory.All(value => value != ItemIds.PlantFibres),
    "player and NPC crafting must execute the same CraftingService recipe and consumption rules");
var cookingInventory = PlayerInventory.CreateStartingInventory();
cookingInventory[0] = ItemIds.RawMinnows;
var sharedCooking = EntityInteractionService.Cook(
    cookingInventory, 0, cookingLevel: 100, roll: .99f);
Require(
    sharedCooking.Succeeded &&
    sharedCooking.Inventory[0] == ItemIds.CookedMinnows,
    "player and NPC cooking must share CookingSkill eligibility and result rules");
var sharedStewInventory = PlayerInventory.CreateStartingInventory();
sharedStewInventory[0] = ItemIds.RawMinnows;
sharedStewInventory[1] = ItemIds.WildBerries;
var sharedStew = EntityInteractionService.CookStew(
    sharedStewInventory, StewCookingService.RequiredLevel);
Require(
    sharedStew.Succeeded &&
    sharedStew.Inventory.Count(value =>
        value == ItemIds.FishBerryStew) == 1 &&
    sharedStew.Inventory.All(value =>
        value != ItemIds.RawMinnows &&
        value != ItemIds.WildBerries),
    "player and NPC stew cooking must share ingredient consumption and output rules");
var lockedStew = EntityInteractionService.CookStew(
    sharedStewInventory, StewCookingService.RequiredLevel - 1);
Require(
    !lockedStew.Succeeded &&
    lockedStew.Failure == "level_locked" &&
    lockedStew.Inventory[0] == ItemIds.RawMinnows &&
    lockedStew.Inventory[1] == ItemIds.WildBerries,
    "failed actor-neutral stew cooking must not consume ingredients");
var transferSource = PlayerInventory.CreateStartingInventory();
var transferDestination = PlayerInventory.CreateStartingInventory();
transferSource[3] = ItemIds.CookedMinnows;
Require(
    EntityInteractionService.TryTransfer(
        transferSource,
        transferDestination,
        3,
        out var transferredSource,
        out var transferredDestination,
        out var transferredItem) &&
    transferredItem == ItemIds.CookedMinnows &&
    transferredSource[3] is null &&
    transferredDestination.Count(value =>
        value == ItemIds.CookedMinnows) == 1,
    "gifts and coordinated item hand-offs must use one atomic actor-neutral transfer");
var eatingInventory = PlayerInventory.CreateStartingInventory();
eatingInventory[2] = ItemIds.CookedMinnows;
var sharedEating = EntityInteractionService.Eat(
    eatingInventory, 2, 40, 0, 50, 100);
var invalidEating = EntityInteractionService.Eat(
    eatingInventory, 1, 40, 0, 50, 100);
Require(
    sharedEating.Succeeded &&
    sharedEating.Inventory[2] is null &&
    sharedEating.Survival.Hunger > 40 &&
    sharedEating.Survival.Health > 50 &&
    !invalidEating.Succeeded &&
    invalidEating.Inventory[2] == ItemIds.CookedMinnows &&
    invalidEating.Survival.Hunger == 40,
    "player and NPC eating must atomically consume food and apply the same survival effect");
var plantingInventory = PlayerInventory.CreateStartingInventory();
plantingInventory[4] = ItemIds.BeanSeeds;
var sharedPlanting = EntityInteractionService.Plant(
    plantingInventory, 4, 3.5f, 4.5f, 100, "actor");
Require(
    sharedPlanting.Succeeded &&
    sharedPlanting.Inventory[4] is null &&
    sharedPlanting.Object is
    {
        ItemId: ItemIds.BeanCrop,
        OwnerId: "actor",
        X: 3.5f,
        Y: 4.5f
    },
    "player and NPC planting must consume one seed and create the same owned crop atomically");
var sharedHarvest = EntityInteractionService.Harvest(
    plantingInventory,
    sharedPlanting.Object!,
    100 + CropService.GrowthGameSeconds);
var earlyHarvest = EntityInteractionService.Harvest(
    plantingInventory, sharedPlanting.Object!, 100);
Require(
    sharedHarvest.Succeeded && sharedHarvest.Quantity >= 2 &&
    sharedHarvest.ItemId == ItemIds.Beans &&
    !earlyHarvest.Succeeded && earlyHarvest.Failure == "not_ready" &&
    earlyHarvest.Inventory.SequenceEqual(plantingInventory),
    "crop harvest readiness, yield and inventory failure behaviour must be actor-neutral");
var sharedFishing = EntityInteractionService.CatchFish(
    PlayerInventory.CreateStartingInventory(),
    0,
    WorldFishSpecies.ShoreMinnows);
Require(
    sharedFishing.Succeeded &&
    sharedFishing.ItemId == ItemIds.RawMinnows &&
    sharedFishing.Inventory.Count(value => value == ItemIds.RawMinnows) == 1 &&
    sharedFishing.Experience.Gained ==
    FishingSkill.Profile(WorldFishSpecies.ShoreMinnows).Experience,
    "player and NPC fishing must share catch inventory and skill progression outcomes");
var placementInventory = PlayerInventory.CreateStartingInventory();
placementInventory[0] = ItemIds.Campfire;
var sharedPlacement = EntityInteractionService.Place(
    placementInventory, 0, 8, 9, "builder");
Require(
    sharedPlacement.Succeeded &&
    sharedPlacement.Inventory[0] is null &&
    sharedPlacement.Object is
    {
        ItemId: ItemIds.Campfire,
        OwnerId: "builder",
        X: 8,
        Y: 9
    },
    "player and NPC placement must consume and create the same owned world object atomically");
var wallInventory = PlayerInventory.CreateStartingInventory();
wallInventory[0] = ItemIds.WoodenWall;
var plannedWall = EntityInteractionService.Place(
    wallInventory, 0, 12.5f, 14.5f, "wall-builder");
Require(
    plannedWall.Succeeded &&
    plannedWall.Inventory[0] is null &&
    plannedWall.Object is
    {
        ItemId: ItemIds.WoodenWall,
        OwnerId: "wall-builder",
        Health: 1,
        MaxHealth: ConstructionService.WoodenWallMaximumHealth
    } &&
    ConstructionService.IsConstructionSite(plannedWall.Object) &&
    ConstructionService.Stage(plannedWall.Object) ==
    ConstructionStage.Planned,
    "placing a constructible must atomically create a persisted planned site instead of a completed wall");
var wallFoundation = ConstructionService.AddWork(
    plannedWall.Object!, 20);
var wallFrame = ConstructionService.AddWork(wallFoundation, 40);
var wallNearlyComplete = ConstructionService.AddWork(wallFrame, 35);
var finishedWall = ConstructionService.AddWork(wallNearlyComplete, 999);
Require(
    ConstructionService.Stage(wallFoundation) ==
        ConstructionStage.Foundation &&
    ConstructionService.Stage(wallFrame) == ConstructionStage.Frame &&
    ConstructionService.Stage(wallNearlyComplete) ==
        ConstructionStage.NearlyComplete &&
    ConstructionService.Stage(finishedWall) ==
        ConstructionStage.Complete &&
    finishedWall.Health == finishedWall.MaxHealth &&
    !ConstructionService.IsConstructionSite(finishedWall),
    "NPC construction work must add bounded health and advance every wall stage before completion");
Require(
    ConstructionService.Angle(plannedWall.Object!) is >= 0 and < 5 &&
    ConstructionService.Angle(plannedWall.Object!) ==
        ConstructionService.Angle(plannedWall.Object!) &&
    PlaceableObjectCatalog.TryGet(
        ItemIds.WoodenWall, out var woodenWallDefinition) &&
    woodenWallDefinition.GroundContactWidth > 0 &&
    woodenWallDefinition.GroundContactDepth > 0,
    "wooden walls must select a stable AoE direction and expose collision from their planned stage");
var wallStoredPosition = new Vector2(12.5f, 8.5f);
PlaceableObjectCatalog.TryGet(
    ItemIds.WoodenWall, out var wallInteractionDefinition);
var wallContactCenter = PlaceableObjectCatalog.GroundContactCenter(
    ItemIds.WoodenWall, wallStoredPosition);
var diagonalBuilder = wallContactCenter + new Vector2(4, 4);
var diagonalWorkPoint = PlaceableObjectCatalog.ClosestInteractionPoint(
    ItemIds.WoodenWall, wallStoredPosition, diagonalBuilder);
var sideBuilder = wallContactCenter + new Vector2(-4, .1f);
var sideWorkPoint = PlaceableObjectCatalog.ClosestInteractionPoint(
    ItemIds.WoodenWall, wallStoredPosition, sideBuilder);
Require(
    diagonalWorkPoint.X > wallContactCenter.X &&
    diagonalWorkPoint.Y > wallContactCenter.Y &&
    MathF.Abs(diagonalWorkPoint.X - wallContactCenter.X -
        (wallInteractionDefinition!.GroundContactWidth * .5f + .32f)) < .001f &&
    MathF.Abs(diagonalWorkPoint.Y - wallContactCenter.Y -
        (wallInteractionDefinition.GroundContactDepth * .5f + .32f)) < .001f &&
    sideWorkPoint.X < wallContactCenter.X &&
    MathF.Abs(sideWorkPoint.Y - sideBuilder.Y) < .001f,
    "player and NPC builders must approach the nearest wall edge or diagonal corner without entering its blocked tile");
Require(
    PalisadeWallVisuals.WallGraphic == "WALL1N1G" &&
    PalisadeWallVisuals.WallGraphicId == 587 &&
    PalisadeWallVisuals.ShadowGraphic == "WALL1N0G" &&
    PalisadeWallVisuals.ShadowGraphicId == 586 &&
    PalisadeWallVisuals.FrontFrameKey == "WALL1N1G@587#2",
    "wooden walls must use the basic AoE palisade layers rather than the fortified wall or placement flag");
var plannedWallVisual = PalisadeWallVisuals.Resolve(plannedWall.Object!, 3);
var completedWallVisual = PalisadeWallVisuals.Resolve(finishedWall, 3);
Require(
    plannedWallVisual.Wall.StartsWith(
        "WCON2NNW#", StringComparison.Ordinal) &&
    plannedWallVisual.Shadow?.StartsWith(
        "WCON2N0W#", StringComparison.Ordinal) == true &&
    completedWallVisual.Wall.StartsWith(
        "WALL1N1G@587#", StringComparison.Ordinal) &&
    completedWallVisual.Shadow?.StartsWith(
        "WALL1N0G@586#", StringComparison.Ordinal) == true,
    "unfinished and completed palisades must always resolve persistent world-render atlas layers");
var droppedInteraction = EntityInteractionService.Drop(
    sharedPlacement.Inventory,
    0,
    2,
    3,
    "actor");
Require(
    !droppedInteraction.Succeeded &&
    droppedInteraction.Inventory.SequenceEqual(sharedPlacement.Inventory),
    "failed entity drops must preserve inventory and create no world object");
var fuelInventory = PlayerInventory.CreateStartingInventory();
fuelInventory[0] = ItemIds.Logs;
var interactionEmptyCampfire = sharedPlacement.Object!;
var fueledInteraction = EntityInteractionService.AddCampfireFuel(
    fuelInventory, 0, interactionEmptyCampfire, 200);
var takenFuelInteraction = EntityInteractionService.TakeCampfireFuel(
    PlayerInventory.CreateStartingInventory(),
    fueledInteraction.Object!,
    200);
Require(
    fueledInteraction.Succeeded &&
    fueledInteraction.Inventory[0] is null &&
    fueledInteraction.Object?.FuelItemId == ItemIds.Logs &&
    takenFuelInteraction.Succeeded &&
    takenFuelInteraction.Inventory.Count(value => value == ItemIds.Logs) == 1 &&
    takenFuelInteraction.Object?.FuelItemId is null,
    "campfire fuel addition and removal must atomically update actor inventory and the shared world object");
var toolInventory = PlayerInventory.CreateStartingInventory();
toolInventory[0] = ItemIds.StoneAxe;
Require(
    EntityInteractionService.TryBluntStoneTool(
        toolInventory, ItemIds.StoneAxe, 0,
        out var bluntedToolInventory) &&
    bluntedToolInventory[0] == ItemIds.BluntStoneAxe &&
    toolInventory[0] == ItemIds.StoneAxe,
    "player and NPC stone tools must share durability mutation without modifying the input inventory");
var settlementGroup = SettlementGroupService.Form(
    "world-check",
    "group-leader",
    ["group-leader", "group-builder", "group-scout"],
    new(10, 12),
    0,
    500);
var playerSettlementGroup = SettlementGroupService.IncludeMember(
    settlementGroup, "player-member");
Require(
    SettlementGroupService.IsMember(
        playerSettlementGroup, "player-member") &&
    playerSettlementGroup.MemberIds.Count ==
        settlementGroup.MemberIds.Count + 1 &&
    ReferenceEquals(
        SettlementGroupService.IncludeMember(
            playerSettlementGroup, "player-member"),
        playerSettlementGroup),
    "settlement membership must include a player actor exactly once");
var groupMember = new VillagerState(
    "group-builder", "Builder", EntityGender.Male,
    0, 0, 10, 12, 0, 100, 100,
    PlayerInventory.CreateStartingInventory(),
    SettlementGroupId: settlementGroup.Id);
var groupOutsider = groupMember with
{
    Id = "outsider",
    SettlementGroupId = null
};
var unclaimedCacheItem = new WorldGroundObject(
    Guid.NewGuid(), ItemIds.PlantFibres, 10.5f, 12.5f);
var groupCacheItem = SettlementGroupService.ClaimForGroup(
    unclaimedCacheItem, settlementGroup);
Require(
    groupCacheItem.OwnerId is null &&
    groupCacheItem.GroupOwnerId == settlementGroup.Id &&
    SettlementGroupService.IsInCache(
        settlementGroup, groupCacheItem) &&
    SettlementGroupService.CanAccess(
        groupMember,
        groupCacheItem.OwnerId,
        groupCacheItem.GroupOwnerId) &&
    !SettlementGroupService.CanAccess(
        groupOutsider,
        groupCacheItem.OwnerId,
        groupCacheItem.GroupOwnerId),
    "ground-cache ownership must grant group members access without making settlement items public");
var ignoredCacheAction = VillagerSimulation.SelectWorldAction(
    groupMember,
    [
        new(
            groupCacheItem.Id,
            groupCacheItem.ItemId,
            new(groupCacheItem.X, groupCacheItem.Y),
            groupCacheItem.OwnerId,
            IsStorage: false,
            groupCacheItem.GroupOwnerId)
    ]);
Require(
    ignoredCacheAction == default,
    "normal gathering must not pick group-owned project supplies back out of the shared ground cache");
var reconGroup = settlementGroup with
{
    MemberIds =
    [
        "group-leader", "group-builder", "group-scout",
        "group-scout-2", "group-worker-1", "group-worker-2"
    ]
};
var openingGroup = SettlementOpeningService.AssignScouts(
    SettlementGroupService.IncludeMember(reconGroup, "player-member"),
    [
        groupMember with { Id = "group-leader" },
        groupMember,
        groupMember with { Id = "group-scout", Boldness = .9f },
        groupMember with { Id = "group-scout-2", Boldness = .8f },
        groupMember with { Id = "group-worker-1" },
        groupMember with { Id = "group-worker-2" }
    ],
    target => target);
Require(
    openingGroup.OpeningStage == SettlementOpeningStage.Reconnaissance &&
    openingGroup.ScoutAssignments is { Count: 6 } &&
    openingGroup.ScoutAssignments.Select(value => value.Sector)
        .Distinct().Count() == 6 &&
    Vector2.DistanceSquared(
        new(openingGroup.ScoutAssignments[0].TargetX,
            openingGroup.ScoutAssignments[0].TargetY),
        new(openingGroup.ScoutAssignments[1].TargetX,
            openingGroup.ScoutAssignments[1].TargetY)) > 20 * 20 &&
    openingGroup.ScoutAssignments.Any(value =>
        value.ScoutId == "group-leader"),
    "opening coordination must send every capable survivor, including the leader, to a distinct outward sector before projects begin");
var legStart = WorldLevelNavigation.NearestWalkable(
    8841,
    new Vector2(4.5f, 4.5f),
    Vector2.Zero,
    (int)WorldLevel.Overworld);
var explorationLeg = VillagerExplorationService.NextLeg(
    8841,
    legStart,
    legStart + new Vector2(40, 0),
    (int)WorldLevel.Overworld);
Require(
    WorldLevelNavigation.IsWalkable(
        8841,
        (int)MathF.Floor(explorationLeg.X),
        (int)MathF.Floor(explorationLeg.Y),
        (int)WorldLevel.Overworld) &&
    Vector2.Dot(
        explorationLeg - legStart,
        Vector2.UnitX) >= 0 &&
    Vector2.DistanceSquared(legStart, explorationLeg) <=
        (VillagerExplorationService.LegDistance + 1) *
        (VillagerExplorationService.LegDistance + 1),
    "exploration must advance through a short forward route leg rather than assigning a remote target or reversing direction");
var routedLeg = VillagerExplorationService.LegFromRoute(
    Vector2.Zero,
    Enumerable.Range(1, 40)
        .Select(index => new Vector2(index * .25f, index * .1f))
        .ToArray());
Require(
    routedLeg.Length >= VillagerExplorationService.LegDistance &&
    routedLeg.Length < VillagerExplorationService.LegDistance + .5f,
    "an asynchronously calculated route must be consumed as a bounded exploration leg");
var incrementalGroup = openingGroup with
{
    ScoutAssignments = [openingGroup.ScoutAssignments![0]],
    ScoutReports = null
};
var incrementalScoutId = incrementalGroup.ScoutAssignments![0].ScoutId;
var incrementalReport = new SettlementScoutReport(
    incrementalScoutId, 0, 0,
    Water: false, Food: false, Wood: true, Stone: true,
    Danger: false, DefensibleGround: false,
    CampScore: 20, GameSeconds: 650);
var naturalScoutReport = SettlementScoutDialogueService.NaturalReport(
    incrementalReport with
    {
        PositionX = 12,
        Food = true,
        Wood = false,
        Stone = true
    },
    Vector2.Zero);
Require(
    naturalScoutReport.Contains("east of here") &&
    naturalScoutReport.Contains("food") &&
    naturalScoutReport.Contains("workable stone") &&
    !naturalScoutReport.Contains("food found") &&
    !naturalScoutReport.Contains("wood not found"),
    "scout dialogue fallback must express structured findings naturally for Ollama rather than reciting boolean fields");
var wreckPersona = VillagerSimulation.DefaultPersona(0);
var caravanPersona = WorldOpeningScenarioService.ApplyArrival(
    wreckPersona, islandStart: false);
Require(
    WorldOpeningScenarioService.ApplyArrival(
        wreckPersona, islandStart: true) == wreckPersona &&
    caravanPersona.BackgroundStory.Contains(
        "merchant caravan", StringComparison.OrdinalIgnoreCase) &&
    caravanPersona.ArrivalMemory.Contains(
        "raiders", StringComparison.OrdinalIgnoreCase) &&
    !caravanPersona.ArrivalMemory.Contains(
        "wreck", StringComparison.OrdinalIgnoreCase),
    "inland openings must replace shipwreck history with a caravan-ambush background while island starts retain it");
Require(
    CaravanSupplyService.Barrels.Count == 3 &&
    CaravanSupplyService.Barrels.SelectMany(value => value).All(value =>
        value.Quantity > 0 && ItemCatalog.TryGet(value.ItemId, out _)) &&
    CaravanSupplyService.Barrels[0].Any(value =>
        value.ItemId == ItemIds.StoneAxe) &&
    CaravanSupplyService.Barrels[1].Any(value =>
        value.ItemId == ItemIds.Logs) &&
    CaravanSupplyService.Barrels[2].Any(value =>
        value.ItemId == ItemIds.CookedMinnows),
    "caravan starts must provide valid bounded tool, resource and food barrel manifests");
for (var leg = 0;
     leg < VillagerExplorationService.OpeningScoutLegs - 1;
     leg++)
{
    incrementalGroup = SettlementOpeningService.RecordScoutObservation(
        incrementalGroup,
        incrementalReport with
        {
            ScoutId = incrementalScoutId,
            PositionX = leg,
            Food = false,
            CampScore = 20 + leg
        });
}
Require(
    incrementalGroup.ScoutAssignments![0].LegsCompleted ==
        VillagerExplorationService.OpeningScoutLegs - 1 &&
    !incrementalGroup.ScoutAssignments[0].Returning &&
    incrementalGroup.ScoutReports![0].CampScore ==
        20 + VillagerExplorationService.OpeningScoutLegs - 2,
    "a scout must observe and retain the best site over several incremental legs before returning");
incrementalGroup = SettlementOpeningService.RecordScoutObservation(
    incrementalGroup,
    incrementalReport with
    {
        ScoutId = incrementalScoutId,
        PositionX = 4,
        Food = false,
        CampScore = 10
    });
Require(
    incrementalGroup.ScoutAssignments![0].Returning &&
    incrementalGroup.ScoutAssignments[0].Reached &&
    incrementalGroup.ScoutReports![0].CampScore ==
        20 + VillagerExplorationService.OpeningScoutLegs - 2,
    "a scout must return after the bounded search while preserving the best observed candidate rather than only the final stop");
var campDecisionWithPlayer = SettlementOpeningService.DecideCamp(
    openingGroup with
    {
        OpeningStage = SettlementOpeningStage.ComparingCamps,
        ScoutReports =
        [
            new(
                "group-scout", 20, 20,
                true, true, true, true, false, true, 90, 600)
        ]
    },
    [
        groupMember with { Id = "group-leader" },
        groupMember,
        groupMember with { Id = "group-scout" },
        groupMember with { Id = "group-scout-2" },
        groupMember with { Id = "group-worker-1" },
        groupMember with { Id = "group-worker-2" }
    ]);
Require(
    SettlementGroupService.IsMember(
        campDecisionWithPlayer, "player-member"),
    "camp votes must preserve non-autonomous player members who do not produce villager responses");
openingGroup = openingGroup with
{
    ScoutAssignments = openingGroup.ScoutAssignments!.Take(2).ToArray()
};
var promisingReport = new SettlementScoutReport(
    "group-scout", 34, 12,
    Water: true, Food: true, Wood: true, Stone: true,
    Danger: false, DefensibleGround: true,
    CampScore: 92, GameSeconds: 700);
openingGroup = SettlementOpeningService.RecordReport(
    openingGroup, promisingReport);
openingGroup = SettlementOpeningService.MarkReported(
    openingGroup, "group-scout");
openingGroup = SettlementOpeningService.RecordReport(
    openingGroup,
    promisingReport with
    {
        ScoutId = "group-scout-2",
        PositionX = -12,
        CampScore = 40
    });
openingGroup = SettlementOpeningService.MarkReported(
    openingGroup, "group-scout-2");
Require(
    openingGroup.OpeningStage == SettlementOpeningStage.ComparingCamps &&
    SettlementOpeningService.BestCamp(openingGroup) == promisingReport &&
    SettlementOpeningService.BestViableCamp(openingGroup) == promisingReport,
    "a returned scout report must advance the group to candidate-camp comparison");
var unsuitableOpening = openingGroup with
{
    ScoutReports =
    [
        promisingReport with
        {
            Food = false,
            Wood = false,
            CampScore = 200
        }
    ]
};
var extendedReconnaissance = SettlementOpeningService
    .ContinueReconnaissance(unsuitableOpening);
Require(
    SettlementOpeningService.BestViableCamp(unsuitableOpening) is null &&
    extendedReconnaissance.OpeningStage ==
        SettlementOpeningStage.Reconnaissance &&
    extendedReconnaissance.ReconnaissanceRound == 1 &&
    extendedReconnaissance.CoordinatedReconnaissance &&
    extendedReconnaissance.ScoutAssignments is null &&
    extendedReconnaissance.ScoutReports is null,
    "a high-scoring site without the minimum food, wood and stone package must trigger a coordinated reconnaissance round");
openingGroup = openingGroup with
{
    MemberIds = ["group-leader", "group-builder", "group-scout"]
};
var decidedOpeningGroup = SettlementOpeningService.DecideCamp(
    openingGroup,
    [
        groupMember with { Id = "group-leader" },
        groupMember,
        groupMember with { Id = "group-scout" }
    ]);
Require(
    decidedOpeningGroup.OpeningStage == SettlementOpeningStage.MovingToCamp &&
    decidedOpeningGroup.CampX == promisingReport.PositionX &&
    decidedOpeningGroup.CampY == promisingReport.PositionY &&
    decidedOpeningGroup.CampResponses?.All(value =>
        value.Response == SettlementCampResponseKind.Agree) == true &&
    SettlementOpeningService.CompleteMove(decidedOpeningGroup).OpeningStage ==
        SettlementOpeningStage.CacheReady,
    "the group must respond to the selected camp, migrate, then establish its shared cache");
var contestedOpening = openingGroup with
{
    ScoutReports =
    [
        promisingReport with
        {
            Danger = false,
            Water = false,
            CampScore = 5
        }
    ]
};
var dissenter = groupMember with
{
    Boldness = .9f,
    Relationships =
    [
        new("group-leader", new(Trust: -60))
    ]
};
var dividedGroup = SettlementOpeningService.DecideCamp(
    contestedOpening,
    [groupMember with { Id = "group-leader" }, dissenter,
        groupMember with { Id = "group-scout" }]);
Require(
    dividedGroup.CampResponses?.Any(value =>
        value.VillagerId == dissenter.Id &&
        value.Response == SettlementCampResponseKind.Leave) == true &&
    !dividedGroup.MemberIds.Contains(dissenter.Id, StringComparer.Ordinal),
    "strong distrust and a low-quality but minimally viable camp must allow a bold member to leave the initial group");
var cacheFibres = Enumerable.Range(0, 3)
    .Select(index => SettlementGroupService.ClaimForGroup(
        new(
            Guid.NewGuid(),
            ItemIds.PlantFibres,
            10 + index * .2f,
            12),
        settlementGroup))
    .ToArray();
var cachedRopeCraft = EntityInteractionService.CraftWithGroundCache(
    groupMember.Inventory,
    cacheFibres,
    ropeRecipe,
    craftingLevel: 1,
    stationAvailable: true);
var failedCachedRopeCraft = EntityInteractionService.CraftWithGroundCache(
    groupMember.Inventory,
    cacheFibres.Take(2).ToArray(),
    ropeRecipe,
    craftingLevel: 1,
    stationAvailable: true);
Require(
    cachedRopeCraft.Succeeded &&
    cachedRopeCraft.Inventory.Count(value => value == ItemIds.Rope) == 1 &&
    cachedRopeCraft.ConsumedCacheObjectIds.Count == 3 &&
    cachedRopeCraft.ReturnedCacheItemIds.Count == 0 &&
    !failedCachedRopeCraft.Succeeded &&
    failedCachedRopeCraft.ConsumedCacheObjectIds.Count == 0 &&
    failedCachedRopeCraft.Inventory.SequenceEqual(groupMember.Inventory),
    "builders must atomically craft from group-owned ground materials without consuming cache items on failure");
var scout = groupMember with
{
    Id = "group-scout",
    LocationMemories =
    [
        new(
            30, 40, 0,
            VillagerLocationType.FoodSource,
            .9f,
            600)
    ]
};
var reportedGroup = SettlementGroupService.ReportDiscoveries(
    settlementGroup, scout);
var informedBuilder = SettlementGroupService.LearnReports(
    groupMember, reportedGroup, 620);
Require(
    reportedGroup.SharedLocations is { Count: 1 } &&
    reportedGroup.SharedLocations[0].ReporterId == scout.Id &&
    informedBuilder.LocationMemories?.Any(memory =>
        memory.Type == VillagerLocationType.FoodSource &&
        memory.PositionX == 30 && memory.PositionY == 40 &&
        memory.Confidence < .9f) == true,
    "scouts must report personally discovered locations and members must learn lower-confidence shared knowledge");

var observeOptions = AppOptions.Parse([
    "--observe", "--observe-seconds", "45",
    "--observe-log-interval", "1.5",
    "--observe-output", ".observe-live",
    "--observe-scenario", ObserveScenarioService.DesertSurplus,
    "--observe-hunger-rate", "4",
    "--observe-food-count", "3"
]);
Require(
    observeOptions.Observe && observeOptions.Game &&
    observeOptions.ObserveSeconds == 45 &&
    observeOptions.ObserveLogIntervalSeconds == 1.5 &&
    observeOptions.ObserveOutputFolder == ".observe-live" &&
    observeOptions.ObserveScenario ==
        ObserveScenarioService.DesertSurplus &&
    observeOptions.ObserveHungerRateMultiplier == 4 &&
    observeOptions.ObserveStartingFoodCount == 3 &&
    ObserveModePolicy.RequiredVillagerCount(
        ObserveScenarioService.DesertSurplus) == 2 &&
    ObserveModePolicy.RequiredVillagerCount(
        ObserveScenarioService.IslandResourceTrio) == 3 &&
    ObserveModePolicy.RequiredVillagerCount(
        ObserveScenarioService.IslandFuturesTrio) == 3 &&
    ObserveModePolicy.RequiredVillagerCount(
        ObserveScenarioService.SettlementFour) == 4 &&
    ObserveModePolicy.RequiredVillagerCount(
        ObserveScenarioService.SettlementTen) == 10 &&
    ObserveScenarioService.IsSupported(
        ObserveScenarioService.SettlementFour) &&
    ObserveScenarioService.IsSupported(
        ObserveScenarioService.SettlementTen) &&
    VillagerSimulation.NamePoolSize == 100 &&
    VillagerSimulation.AvailableNameCount == 100 &&
    VillagerSimulation.NamesForPopulation(4, 2187).Count == 4 &&
    VillagerSimulation.NamesForPopulation(4, 2187).Distinct().Count() == 4 &&
    VillagerSimulation.NamesForPopulation(4, 2187).SequenceEqual(
        VillagerSimulation.NamesForPopulation(4, 2187)) &&
    VillagerSimulation.NamesForPopulation(10, 2187)
        .Select((name, index) => MedievalDemographics.IsNameCompatible(
            name,
            VillagerSimulation.GendersForPopulation(10, 2187)[index]))
        .All(compatible => compatible) &&
    VillagerSimulation.GendersForPopulation(10, 2187)
        .Count(gender => gender == EntityGender.Female) == 5 &&
    VillagerSimulation.GendersForPopulation(10, 2187)
        .Count(gender => gender == EntityGender.Male) == 5 &&
    MedievalDemographics.GendersForNames(
        ["Margery", "William", "Custom"], 2187)[0] ==
        EntityGender.Female &&
    MedievalDemographics.GendersForNames(
        ["Margery", "William", "Custom"], 2187)[1] ==
        EntityGender.Male &&
    VillagerSimulation.CreateInitial(
            2187, Vector2.Zero, population: 10)
        .All(villager =>
            MedievalDemographics.IsNameCompatible(
                villager.Name, villager.Gender) &&
            MedievalDemographics.IsTradeCompatible(
                villager.Persona!.PriorTrade, villager.Gender)) &&
    settlementFourConfigured.Count == 4 &&
    !ReferenceEquals(settlementFourConfigured, settlementFourSource) &&
    settlementTenConfigured.Count == 10 &&
    settlementTenConfigured.Select(value => value.Name).Distinct().Count() == 10 &&
    !ReferenceEquals(settlementTenConfigured, settlementTenSource) &&
    !ObserveModePolicy.ObserverParticipatesInSimulation,
    "Observe CLI configuration must request exactly two villagers and exclude the hidden observer");
var acceleratedHunger = VillagerSimulation.CatchUp(
    villagerSpawnA[0],
    villagerSpawnA[0].LastSimulatedGameSeconds +
    60 * VillagerSimulation.GameSecondsPerRealSecond,
    hungerLossMultiplier: 4);
Require(
    MathF.Abs(acceleratedHunger.Hunger -
        (SurvivalService.MaximumHunger -
         SurvivalService.BaseHungerLossPerSecond * 60 * 4)) < .001f,
    "observe hunger acceleration must scale NPC hunger loss without changing the shared default survival rate");
var decliningNeedMemory = VillagerNeedPatternMemory.ObserveHunger(
    villagerSpawnA[0] with
    {
        Hunger = 50,
        PositionX = 0,
        PositionY = 0,
        Inventory = PlayerInventory.CreateStartingInventory()
    },
    villagerSpawnA[0].Id,
    villagerSpawnA[0].Name,
    60,
    gameSeconds: 100);
decliningNeedMemory = VillagerNeedPatternMemory.ObserveHunger(
    decliningNeedMemory,
    decliningNeedMemory.Id,
    decliningNeedMemory.Name,
    50,
    gameSeconds: 700);
var stableNeedMemory = VillagerNeedPatternMemory.ObserveHunger(
    villagerSpawnA[0],
    villagerSpawnA[0].Id,
    villagerSpawnA[0].Name,
    60,
    gameSeconds: 100);
stableNeedMemory = VillagerNeedPatternMemory.ObserveHunger(
    stableNeedMemory,
    stableNeedMemory.Id,
    stableNeedMemory.Name,
    59,
    gameSeconds: 700);
Require(
    VillagerNeedPatternMemory.NeedsFoodSoon(
        decliningNeedMemory, decliningNeedMemory.Id, 700) &&
    !VillagerNeedPatternMemory.NeedsFoodSoon(
        stableNeedMemory, stableNeedMemory.Id, 700),
    "NPC planning must react to a repeated declining need pattern without treating a stable history as a crisis");
var mealInventory = new string?[28];
mealInventory[0] = ItemIds.CookedMinnows;
var fedForecastVillager = VillagerSimulation.ApplyDecision(
    decliningNeedMemory with
    {
        Inventory = mealInventory,
        Hunger = 20
    },
    new(VillagerNeed.Food, MoveTarget: null, ConsumeSlot: 0),
    VillagerSimulationTier.Nearby,
    gameSeconds: 5000);
Require(!VillagerNeedPatternMemory.NeedsFoodSoon(
            fedForecastVillager,
            fedForecastVillager.Id,
            5000) &&
        fedForecastVillager.Memories!.Count(memory =>
            memory.Kind == VillagerNeedPatternMemory.HungerSampleKind &&
            memory.SubjectId == fedForecastVillager.Id) == 1,
    "eating must reset stale downward hunger forecasts with an immediate post-meal sample");
var futureFoodProvider = new SocialActorObservation(
    "future-provider",
    "Tomas",
    new(1, 0),
    0,
    90,
    20);
var proactiveFoodGoal = VillagerSimulation.SelectSocialGoal(
    decliningNeedMemory with { NextSocialGameSeconds = 650 },
    new[] { futureFoodProvider },
    gameSeconds: 700);
Require(
    proactiveFoodGoal.Intent == VillagerSocialIntent.RequestFood &&
    proactiveFoodGoal.Speech?.Contains(
        "plan", StringComparison.OrdinalIgnoreCase) == true,
    "a forecast food shortage must create a proactive sharing request before current hunger reaches crisis level");
var coolingDownFoodGoal = VillagerSimulation.SelectSocialGoal(
    decliningNeedMemory with
    {
        Hunger = 10,
        NextSocialGameSeconds = 9999
    },
    new[] { futureFoodProvider },
    gameSeconds: 700);
Require(
    coolingDownFoodGoal.Intent == VillagerSocialIntent.None,
    "urgent hunger must not bypass the social cooldown and repeatedly spam the same food request");
Require(
    VillagerIntentPriorityService.NeedsUrgentFood(
        decliningNeedMemory with { Hunger = 35, Health = 1 }) &&
    !VillagerIntentPriorityService.NeedsUrgentFood(
        decliningNeedMemory with { Hunger = 35, Health = 0 }) &&
    !VillagerIntentPriorityService.NeedsUrgentFood(
        decliningNeedMemory with { Hunger = 36, Health = 1 }),
    "urgent autonomous food acquisition must use the shared living-and-hunger boundary");
var scarceOwnerInventory = PlayerInventory.CreateStartingInventory();
scarceOwnerInventory[0] = ItemIds.CookedMinnows;
var requestOwner = villagerSpawnA[1] with
{
    Inventory = scarceOwnerInventory,
    Hunger = 30
};
var politeRequester = decliningNeedMemory with
{
    Honesty = .1f,
    Boldness = .2f
};
var traderRequester = decliningNeedMemory with
{
    Honesty = .8f,
    Boldness = .2f
};
var threateningRequester = decliningNeedMemory with
{
    Hunger = 15,
    Boldness = .6f
};
var drasticRequester = decliningNeedMemory with
{
    Hunger = 5,
    Boldness = .8f
};
var refusedFood = VillagerRequestApprovalService.EvaluateFoodRequest(
    traderRequester, requestOwner, gameSeconds: 800);
var surplusOwnerInventory = PlayerInventory.CreateStartingInventory();
for (var surplusSlot = 0; surplusSlot < 20; surplusSlot++)
    surplusOwnerInventory[surplusSlot] = ItemIds.CookedMinnows;
var approvedFood = VillagerRequestApprovalService.EvaluateFoodRequest(
    traderRequester,
    requestOwner with
    {
        Hunger = 90,
        Inventory = surplusOwnerInventory
    },
    gameSeconds: 800);
var politePlan = VillagerRequestApprovalService.PlanAfterRefusal(
    politeRequester, requestOwner);
var tradePlan = VillagerRequestApprovalService.PlanAfterRefusal(
    traderRequester, requestOwner);
var threatPlan = VillagerRequestApprovalService.PlanAfterRefusal(
    threateningRequester, requestOwner);
var drasticPlan = VillagerRequestApprovalService.PlanAfterRefusal(
    drasticRequester, requestOwner);
var armedOwner = requestOwner with
{
    Inventory =
    [
        ItemIds.IronKnife,
        .. PlayerInventory.CreateStartingInventory()[1..]
    ]
};
var weaponAwareRequester = VillagerCapabilityMemory.Observe(
    drasticRequester with { Honesty = .8f },
    armedOwner.Id,
    armedOwner.Name,
    VillagerCapabilityMemory.VisibleTools(armedOwner.Inventory),
    distance: 1,
    gameSeconds: 790);
var armedOwnerPlan = VillagerRequestApprovalService.PlanAfterRefusal(
    weaponAwareRequester, armedOwner);
Require(
    !refusedFood.Approved &&
    refusedFood.Reason == "protected_reserve" &&
    approvedFood.Approved &&
    approvedFood.Reason == "surplus_available" &&
    politePlan.Strategy == VillagerRefusalStrategy.BeNice &&
    tradePlan.Strategy == VillagerRefusalStrategy.SeekTrade &&
    tradePlan.TradeItemId is not null &&
    threatPlan.Strategy == VillagerRefusalStrategy.Threaten &&
    drasticPlan.Strategy == VillagerRefusalStrategy.TakeByForce,
    "food requests must require owner approval and refusal must branch by urgency, personality, and fair alternatives");
Require(
    armedOwnerPlan.Strategy == VillagerRefusalStrategy.Threaten &&
    armedOwnerPlan.Strategy != VillagerRefusalStrategy.TakeByForce &&
    VillagerWeaponAwareness.BestKnownKnife(
        weaponAwareRequester, armedOwner.Id)?.Id == ItemIds.IronKnife &&
    VillagerWeaponAwareness.KnownKnifePower(
        weaponAwareRequester, armedOwner.Id) == 3,
    "NPCs must avoid reckless force when their own memory identifies an armed food owner");
var refusalStates = VillagerRequestApprovalService.ApplyRefusal(
    drasticRequester, requestOwner, drasticPlan, gameSeconds: 800);
Require(
    refusalStates.Requester.LastDeliberation is
        { Action: "take_food", Risk: 95 } &&
    refusalStates.Requester.Memories?.Any(memory =>
        memory.Kind == "request-refused" &&
        memory.SubjectId == requestOwner.Id) == true &&
    refusalStates.Owner.Relationships?.Single(value =>
        value.CharacterId == drasticRequester.Id).State is
        { Fear: > 0, Resentment: > 0, Trust: < 0 },
    "drastic refusal branches must persist thought, memory, fear, resentment, and lost trust");
var bondedWitness = politeRequester with
{
    Health = 70,
    Boldness = .8f,
    Sociability = .6f,
    Relationships =
    [
        new VillagerRelationship(
            requestOwner.Id,
            new(Trust: 30, Affection: 20))
    ]
};
var protectiveWitness = VillagerWitnessResponseService.Decide(
    bondedWitness, requestOwner, "player", attackerArmed: false);
var helpSeekingWitness = VillagerWitnessResponseService.Decide(
    bondedWitness with { Boldness = .4f },
    requestOwner, "player", attackerArmed: true);
var fleeingWitness = VillagerWitnessResponseService.Decide(
    politeRequester with
    {
        Health = 70,
        Boldness = .15f,
        Honesty = .2f,
        Sociability = .2f,
        Relationships = []
    },
    requestOwner, "player", attackerArmed: false);
var warningWitness = VillagerWitnessResponseService.Decide(
    politeRequester with
    {
        Health = 70,
        Boldness = .5f,
        Honesty = .8f,
        Sociability = .4f,
        Relationships = []
    },
    requestOwner, "player", attackerArmed: false);
var uninvolvedWitness = VillagerWitnessResponseService.Decide(
    politeRequester with
    {
        Health = 70,
        Boldness = .4f,
        Honesty = .2f,
        Sociability = .2f,
        Relationships = []
    },
    requestOwner, "player", attackerArmed: false);
Require(protectiveWitness.Intent == VillagerWitnessIntent.Protect,
    "a bold friend must protect an attacked friend");
Require(helpSeekingWitness.Intent == VillagerWitnessIntent.SeekHelp,
    "a cautious friend must seek help against an armed attacker");
Require(fleeingWitness.Intent == VillagerWitnessIntent.BackAway,
    "a timid witness must back away from violence");
Require(warningWitness.Intent == VillagerWitnessIntent.Warn,
    "a principled witness must warn the attacker");
Require(uninvolvedWitness.Intent == VillagerWitnessIntent.Ignore,
    "a detached witness may avoid intervening for an acquaintance");
var justiceLeader = politeRequester with
{
    Id = "justice-leader",
    Name = "Justice leader",
    Health = 100,
    Boldness = .2f,
    Honesty = .2f,
    Relationships = []
};
var justiceVictim = requestOwner with
{
    Id = "justice-victim",
    Name = "Justice victim",
    Health = 90
};
var justiceGroup = SettlementGroupService.Form(
    "justice-world",
    justiceLeader.Id,
    [justiceLeader.Id, justiceVictim.Id, "player", "member-a", "member-b"],
    new(0, 0), 0, 100);
var warningJudgment = SettlementJusticeService.Judge(
    justiceGroup, justiceLeader, justiceVictim,
    "player", 1, false, [justiceLeader, justiceVictim], 200);
var restitutionJudgment = SettlementJusticeService.Judge(
    justiceGroup, justiceLeader, justiceVictim,
    "player", 2, false, [justiceLeader, justiceVictim], 210);
var avoidanceJudgment = SettlementJusticeService.Judge(
    justiceGroup, justiceLeader,
    justiceVictim with { Health = 50 },
    "player", 3, false, [justiceLeader, justiceVictim], 220);
var collectiveJudgment = SettlementJusticeService.Judge(
    justiceGroup,
    justiceLeader with { Boldness = .8f },
    justiceVictim with { Health = 50 },
    "outsider", 3, false,
    [justiceLeader with { Boldness = .8f }, justiceVictim], 230);
var exileJudgment = SettlementJusticeService.Judge(
    justiceGroup,
    justiceLeader with { Honesty = .8f },
    justiceVictim with { Health = 20 },
    "player", 6, true,
    [justiceLeader with { Honesty = .8f }, justiceVictim], 240);
Require(
    warningJudgment.Outcome == SettlementJusticeOutcome.Warning &&
    restitutionJudgment.Outcome == SettlementJusticeOutcome.Restitution &&
    avoidanceJudgment.Outcome == SettlementJusticeOutcome.Avoidance &&
    collectiveJudgment.Outcome ==
        SettlementJusticeOutcome.CollectiveDefense &&
    exileJudgment.Outcome == SettlementJusticeOutcome.Exile,
    "settlement justice must expose every escalating live outcome");
var aftermathMembers = new[]
{
    justiceLeader,
    justiceVictim,
    politeRequester with { Id = "aftermath-a", Name = "Aftermath A" },
    bondedWitness with { Id = "aftermath-b", Name = "Aftermath B" },
    drasticRequester with { Id = "aftermath-c", Name = "Aftermath C" },
    traderRequester with { Id = "aftermath-d", Name = "Aftermath D" }
};
var aftermath = SocialIncidentAftermathService.Begin(
    null, justiceGroup, justiceVictim, "player",
    aftermathMembers, 300);
var aidAssignment = aftermath.Assignments.Single(value =>
    value.Role == SocialAftermathRole.AidVictim);
var aidActor = aftermathMembers.Single(value =>
    value.Id == aidAssignment.ActorId);
var aidedActor = SocialIncidentAftermathService.RecordCompletedInteraction(
    aidActor, aftermath, aidAssignment,
    "Player", justiceVictim.Name, justiceVictim.Name, 950);
var supportedVictim = SocialIncidentAftermathService.RecordReceivedSupport(
    justiceVictim, aftermath, aidAssignment,
    aidActor.Name, 950);
var accountAssignment = aftermath.Assignments.Single(value =>
    value.Role == SocialAftermathRole.ShareAccount);
var accountListener = aftermathMembers.Single(value =>
    value.Id == accountAssignment.TargetId);
var informedListener = SocialIncidentAftermathService.RecordHeardAccount(
    accountListener, justiceVictim, aftermath,
    "Player", justiceVictim.Name, 960);
var completedAftermath = SocialIncidentAftermathService.Complete(
    aftermath, aidActor.Id);
Require(aftermath.Assignments.Count <=
            SocialIncidentAftermathService.MaximumAssignments &&
        aftermath.Assignments.Select(value => value.ActorId)
            .Distinct().Count() == aftermath.Assignments.Count &&
        aftermath.Assignments.Any(value =>
            value.Role == SocialAftermathRole.GuardVictim) &&
        aftermath.Assignments.Any(value =>
            value.Role == SocialAftermathRole.ShareAccount) &&
        aftermath.Assignments.Any(value =>
            value.Role == SocialAftermathRole.ConfrontAggressor) &&
        aidedActor.Memories?.Any(value =>
            value.EventId == aftermath.IncidentId) == true &&
        supportedVictim.Memories?.Any(value =>
            value.Kind == "aftermath-received-aid") == true &&
        supportedVictim.Relationships?.Single(value =>
            value.CharacterId == aidActor.Id).State.Gratitude == 12 &&
        informedListener.Memories?.Single(value =>
            value.Kind == "aftermath-heard-account").Confidence ==
            .45f + justiceVictim.Honesty * .4f &&
        completedAftermath.Assignments.Single(value =>
            value.ActorId == aidActor.Id).Completed,
    "social aftermath must bound unique visible roles and apply memories, gratitude, and completion only after an interaction succeeds");
var resolvedRestitution = SettlementJusticeService.ResolveRestitution(
    restitutionJudgment, "player", justiceVictim.Id);
var wrongRestitution = SettlementJusticeService.ResolveRestitution(
    restitutionJudgment, "someone-else", justiceVictim.Id);
var exiledGroup = SettlementGroupService.RemoveMember(
    justiceGroup with { ActiveJusticeCase = exileJudgment }, "player");
var preservedExile = SettlementJusticeService.PreserveEscalation(
    exileJudgment,
    restitutionJudgment with { FiledGameSeconds = 250 });
Require(
    resolvedRestitution.Resolved &&
    resolvedRestitution.RestitutionRemaining == 0 &&
    !wrongRestitution.Resolved &&
    !SettlementGroupService.IsMember(exiledGroup, "player") &&
    SettlementJusticeService.IsExiled(exiledGroup, "player") &&
    !SettlementJusticeService.IsExiled(exiledGroup, "member-a") &&
    !SettlementGroupService.CanAccess(
        exiledGroup, "player", null, justiceGroup.Id) &&
    SettlementGroupService.CanAccess(
        exiledGroup, "member-a", null, justiceGroup.Id) &&
    preservedExile.Outcome == SettlementJusticeOutcome.Exile &&
    exiledGroup.ActiveJusticeCase?.Outcome ==
        SettlementJusticeOutcome.Exile,
    "restitution must resolve only through the offender gifting the victim, while exile must revoke membership and persist judgment");
var exclusionPolicy = new SettlementExclusionPolicy(
    Radius: 10,
    DisengageRadius: 12,
    InitialGraceGameSeconds: 100,
    ReentryGraceGameSeconds: 40,
    FinalWarningGameSeconds: 20);
var initialExclusion = SettlementExclusionService.Advance(
    exclusionPolicy, null, "player", new(1, 1), new(0, 0), 1000);
var initiallyOutsideExclusion = SettlementExclusionService.Advance(
    exclusionPolicy, null, "player", new(13, 0), new(0, 0), 1000);
var finalExclusion = SettlementExclusionService.Advance(
    exclusionPolicy, initialExclusion.State,
    "player", new(1, 1), new(0, 0), 1100);
var enforcedExclusion = SettlementExclusionService.Advance(
    exclusionPolicy, finalExclusion.State,
    "player", new(1, 1), new(0, 0), 1120);
var departedExclusion = SettlementExclusionService.Advance(
    exclusionPolicy, enforcedExclusion.State,
    "player", new(13, 0), new(0, 0), 1130);
var reenteredExclusion = SettlementExclusionService.Advance(
    exclusionPolicy, departedExclusion.State,
    "player", new(2, 0), new(0, 0), 1140);
var eligibleResponder = advancedVillagers[0] with
{
    Id = "eligible-responder", Health = 90, Hunger = 80,
    Energy = 80, Boldness = .9f
};
var starvingResponder = advancedVillagers[0] with
{
    Id = "starving-responder", Health = 90, Hunger = 5,
    Energy = 80, Boldness = 1
};
var responders = SettlementExclusionService.SelectResponders(
    new[] { eligibleResponder, starvingResponder });
Require(
    initialExclusion.State.Stage == SettlementExclusionStage.Grace &&
    initiallyOutsideExclusion.Changed &&
    initiallyOutsideExclusion.State.Stage ==
        SettlementExclusionStage.Outside &&
    finalExclusion.State.Stage == SettlementExclusionStage.FinalWarning &&
    enforcedExclusion.State.Stage == SettlementExclusionStage.Enforcement &&
    departedExclusion.State.Stage == SettlementExclusionStage.Outside &&
    reenteredExclusion.State.Stage == SettlementExclusionStage.Grace &&
    reenteredExclusion.State.Entries == 2 &&
    reenteredExclusion.State.DeadlineGameSeconds == 1180 &&
    responders.SetEquals(new[] { eligibleResponder.Id }),
    "exclusion zones must grant grace, warn, enforce, disengage beyond the boundary, escalate faster on re-entry, and keep starving villagers off enforcement duty");
var frightenedConflict = VillagerConflictService.DecideResponse(
    politeRequester with { Health = 70, Boldness = .2f },
    drasticRequester,
    wasAttacked: true);
var socialConflict = VillagerConflictService.DecideResponse(
    traderRequester with
    {
        Health = 70,
        Boldness = .4f,
        Sociability = .8f
    },
    drasticRequester,
    wasAttacked: true,
    nearbyAllies: 1);
var defensiveConflict = VillagerConflictService.DecideResponse(
    threateningRequester with { Health = 70, Boldness = .6f },
    drasticRequester,
    wasAttacked: true);
var playerTargetConflict = politeRequester with
{
    ConflictTargetId = "player-target",
    ConflictIntent = VillagerConflictIntent.Defend
};
Require(VillagerConflictService.TargetsActor(
            playerTargetConflict, "player-target") &&
        !VillagerConflictService.TargetsActor(
            playerTargetConflict, "another-actor"),
    "player-target conflicts must remain distinguishable from NPC-target conflicts so the player combat controller can execute them");
var retaliatingConflict = VillagerConflictService.DecideResponse(
    drasticRequester with { Health = 70, Boldness = .8f },
    threateningRequester,
    wasAttacked: true);
var surrenderConflict = VillagerConflictService.DecideResponse(
    drasticRequester with { Health = 20 },
    threateningRequester,
    wasAttacked: true);
var armedAggressor = drasticRequester with
{
    Id = "armed-aggressor",
    Name = "Armed aggressor",
    Inventory =
    [
        ItemIds.IronKnife,
        .. PlayerInventory.CreateStartingInventory()[1..]
    ]
};
var weaponAwareDefender = VillagerCapabilityMemory.Observe(
    threateningRequester with { Health = 30, Boldness = .8f },
    armedAggressor.Id,
    armedAggressor.Name,
    VillagerCapabilityMemory.VisibleTools(armedAggressor.Inventory),
    distance: 1,
    gameSeconds: 800);
var armedConflict = VillagerConflictService.DecideResponse(
    weaponAwareDefender,
    armedAggressor,
    wasAttacked: true);
Require(
    frightenedConflict.Intent == VillagerConflictIntent.Flee &&
    socialConflict.Intent == VillagerConflictIntent.CallForHelp &&
    defensiveConflict.Intent == VillagerConflictIntent.Defend &&
    retaliatingConflict.Intent == VillagerConflictIntent.Retaliate &&
    surrenderConflict.Intent == VillagerConflictIntent.Surrender &&
    armedConflict.Intent == VillagerConflictIntent.Surrender &&
    armedConflict.Thought.Contains("iron knife"),
    "NPC conflict responses must account for health, boldness, allies, and prior relationship pressure");
var persistedConflict = VillagerConflictService.ApplyDecision(
    threateningRequester,
    drasticRequester,
    defensiveConflict,
    "food taken by force",
    gameSeconds: 900);
var clearedConflict = VillagerConflictService.Clear(
    persistedConflict, gameSeconds: 950);
Require(
    persistedConflict.ConflictTargetId == drasticRequester.Id &&
    persistedConflict.ConflictIntent == VillagerConflictIntent.Defend &&
    persistedConflict.ConflictExpiresGameSeconds > 900 &&
    persistedConflict.LastDeliberation is
        { Action: "defend", Decision: "conflict_response" } &&
    clearedConflict.ConflictTargetId is null &&
    clearedConflict.ConflictIntent == VillagerConflictIntent.None,
    "NPC conflict decisions must persist as executable actions and cleanly de-escalate");
var automaticallyExpiredConflict = VillagerConflictService.Expire(
    persistedConflict,
    persistedConflict.ConflictExpiresGameSeconds + 1);
Require(automaticallyExpiredConflict.ConflictIntent ==
            VillagerConflictIntent.None &&
        automaticallyExpiredConflict.ConflictTargetId is null,
    "expired conflicts must clear through lifecycle processing without another hostile decision");
var tradeSeekingState = VillagerRequestApprovalService.ApplyRefusal(
    traderRequester, requestOwner, tradePlan, gameSeconds: 800).Requester;
Require(
    tradePlan.TradeItemId is { } wantedTradeItem &&
    VillagerResourcePriority.Score(
        tradeSeekingState, wantedTradeItem) == 95,
    "a fair-trade refusal branch must turn the proposed trade item into a concrete gathering priority");
var desertScenarioVillagers = ObserveScenarioService.Configure(
    ObserveScenarioService.DesertSurplus,
    2187,
    twoVillagerSpawn);
Require(
    desertScenarioVillagers.Count == 2 &&
    InfiniteWorldGenerator.BiomeAt(
        2187,
        (int)MathF.Floor(desertScenarioVillagers[0].PositionX),
        (int)MathF.Floor(desertScenarioVillagers[0].PositionY)) is
        Biome.DesertSand or Biome.CrackedEarth &&
    Vector2.Distance(
        new(
            desertScenarioVillagers[0].PositionX,
            desertScenarioVillagers[0].PositionY),
        new(
            desertScenarioVillagers[1].PositionX,
            desertScenarioVillagers[1].PositionY)) == 1 &&
    VillagerSimulation.CountFood(
        desertScenarioVillagers[0].Inventory) == 20 &&
    desertScenarioVillagers[1].Hunger == 70 &&
    desertScenarioVillagers[1].Inventory.Count(value =>
        value == ItemIds.StoneKnife) == 1,
    "desert-surplus must start two adjacent desert survivors with asymmetric food and knife resources");
var scarceScenarioVillagers = ObserveScenarioService.Configure(
    ObserveScenarioService.DesertSurplus,
    2187,
    twoVillagerSpawn,
    startingFoodCount: 1);
Require(
    VillagerSimulation.CountFood(
        scarceScenarioVillagers[0].Inventory) == 1,
    "observe scenarios must support controlled starting-food scarcity without changing their biome or actors");
var knifeConflictVillagers = ObserveScenarioService.Configure(
    ObserveScenarioService.DesertKnifeConflict,
    2187,
    twoVillagerSpawn,
    startingFoodCount: 20);
Require(
    ObserveScenarioService.IsSupported(
        ObserveScenarioService.DesertKnifeConflict) &&
    VillagerSimulation.CountFood(
        knifeConflictVillagers[0].Inventory) == 2 &&
    knifeConflictVillagers[0].Hunger == 35 &&
    knifeConflictVillagers[0].Boldness == .62f &&
    knifeConflictVillagers[1].Inventory.Count(value =>
        value == ItemIds.StoneKnife) == 1 &&
    knifeConflictVillagers[1].Hunger == 8 &&
    knifeConflictVillagers[1].Boldness == .82f,
    "desert knife conflict scenarios must fix scarcity and personality pressure regardless of generic food options");
var islandResourceTrio = ObserveScenarioService.Configure(
    ObserveScenarioService.IslandResourceTrio,
    2187,
    villagerSpawnA);
var islandTrioPositions = islandResourceTrio
    .Select(value => new Vector2(value.PositionX, value.PositionY))
    .ToArray();
var islandTrioRoles = VillagerWorkCoordinator.AssignRoles(islandResourceTrio);
Require(
    ObserveScenarioService.IsSupported(
        ObserveScenarioService.IslandResourceTrio) &&
    islandResourceTrio.Count == 3 &&
    VillagerSimulation.CountFood(islandResourceTrio[0].Inventory) == 2 &&
    islandResourceTrio[1].Inventory.Count(value =>
        value == ItemIds.StoneAxe) == 1 &&
    islandResourceTrio[2].Inventory.Count(value =>
        value == ItemIds.StoneKnife) == 1 &&
    islandTrioRoles[islandResourceTrio[1].Id] == VillagerWorkRole.Wood &&
    islandTrioRoles[islandResourceTrio[2].Id] == VillagerWorkRole.Crafting &&
    islandTrioPositions.All(position =>
        WorldLevelNavigation.IsWalkable(
            2187,
            (int)MathF.Floor(position.X),
            (int)MathF.Floor(position.Y),
            (int)WorldLevel.Overworld)) &&
    islandTrioPositions.SelectMany((position, index) =>
        islandTrioPositions.Skip(index + 1).Select(other =>
            Vector2.Distance(position, other))).All(distance =>
                distance is >= 1 and <= 1.5f),
    "island resource trio scenarios must place three nearby survivors on land with exactly two fish, one axe, and one knife");
var islandFuturesTrio = ObserveScenarioService.Configure(
    ObserveScenarioService.IslandFuturesTrio,
    2187,
    villagerSpawnA);
Require(
    ObserveScenarioService.IsSupported(
        ObserveScenarioService.IslandFuturesTrio) &&
    islandFuturesTrio.Count == 3 &&
    islandFuturesTrio[0].Inventory.Contains(ItemIds.GatheringBasket) &&
    islandFuturesTrio[0].Inventory.Contains(ItemIds.WildGrainSeeds) &&
    islandFuturesTrio[0].Inventory.Contains(ItemIds.BeanSeeds) &&
    islandFuturesTrio[0].Inventory.Contains(ItemIds.RootSeeds) &&
    VillagerSimulation.CountFood(islandFuturesTrio[0].Inventory) == 2 &&
    islandFuturesTrio[1].Inventory.Contains(ItemIds.StoneAxe) &&
    islandFuturesTrio[2].Inventory.Contains(ItemIds.StoneKnife) &&
    islandFuturesTrio[2].Inventory.Contains(ItemIds.StoneHammer) &&
    islandFuturesTrio[2].Inventory.Contains(ItemIds.StonePickaxe) &&
    islandFuturesTrio[2].Inventory.Contains(ItemIds.StoneShovel) &&
    islandFuturesTrio[2].Inventory.Contains(ItemIds.PortableTorch) &&
    islandFuturesTrio.All(value => WorldLevelNavigation.IsWalkable(
        2187,
        (int)MathF.Floor(value.PositionX),
        (int)MathF.Floor(value.PositionY),
        (int)WorldLevel.Overworld)),
    "island futures scenarios must start three nearby specialists with crops, scarce food, tools, and cave equipment");
var observeFocus = ObserveModePolicy.Focus(
    [
        villagerSpawnA[0] with { PositionX = 10, PositionY = 20 },
        villagerSpawnA[1] with { PositionX = 14, PositionY = 24 },
        villagerSpawnA[2] with { PositionX = 999, PositionY = 999, Health = 0 }
    ],
    (int)WorldLevel.Overworld,
    new(-500, -500));
Require(
    observeFocus == new Vector2(12, 22),
    "Observe camera and chunk focus must track the centroid of living villagers, never the hidden player");
var observeJson = ObserveEventLog.Serialize(
    1.25, 28875, "Day 1 08:01", villagerSpawnA[0].Id,
    "world_decision", new { Action = "TakeItem" });
using (var observeDocument = System.Text.Json.JsonDocument.Parse(
           observeJson["[OBSERVE] ".Length..]))
{
    var observeRoot = observeDocument.RootElement;
    Require(
        observeRoot.GetProperty("RealSeconds").GetDouble() == 1.25 &&
        observeRoot.GetProperty("GameSeconds").GetDouble() == 28875 &&
        observeRoot.GetProperty("GameTime").GetString() == "Day 1 08:01" &&
        observeRoot.GetProperty("VillagerId").GetString() == villagerSpawnA[0].Id &&
        observeRoot.GetProperty("EventType").GetString() == "world_decision" &&
        observeRoot.GetProperty("Data").GetProperty("Action").GetString() == "TakeItem",
        "Observe logs must be machine-readable and contain time, identity, event type, and data");
}
using (var capturedObserveOutput = new StringWriter())
{
    ObserveEventLog.Write(
        capturedObserveOutput,
        2, 28920, "Day 1 08:02", villagerSpawnA[1].Id,
        "state_changed", new { Need = "Food" });
    Require(
        capturedObserveOutput.ToString().StartsWith(
            "[OBSERVE] {", StringComparison.Ordinal) &&
        capturedObserveOutput.ToString().Contains(
            "\"EventType\":\"state_changed\"",
            StringComparison.Ordinal),
        "automated tests must be able to capture Observe console events live through a TextWriter");
}
var offendedVillager =
    VillagerSimulation.ObserveUnauthorizedTaking(
        villagerSpawnA[0],
        Guid.NewGuid(),
        ItemIds.BronzeAxe,
        villagerSpawnA[0].Id,
        "player",
        200,
        1,
        30,
        out var villagerReaction);
Require(
    offendedVillager.Memories?.Single().Kind ==
        "unauthorized-item-taken" &&
    offendedVillager.Relationships?.Single()
        .State.Trust < 0 &&
    villagerReaction >= OwnershipReaction.DemandCompensation,
    "a witnessing owner must remember theft, distrust the suspect, and confront them");
var villagerBenchmark =
    System.Diagnostics.Stopwatch.StartNew();
var benchmarkVillager = villagerSpawnA[1];
for (var index = 0; index < 100_000; index++)
{
    var tier = VillagerSimulation.Tier(
        new(benchmarkVillager.PositionX,
            benchmarkVillager.PositionY),
        new(index & 255, index & 127));
    _ = VillagerSimulation.DecisionInterval(tier);
}
villagerBenchmark.Stop();
Require(
    villagerBenchmark.ElapsedMilliseconds < 1000,
    "villager tier selection must remain allocation-free and fast at scale");
var nearbyRockId = Guid.NewGuid();
var usefulFoodId = Guid.NewGuid();
var playerOwnedToolId = Guid.NewGuid();
var worldObjects = new VillagerWorldObject[]
{
    new(
        nearbyRockId,
        ItemIds.SmallRocks,
        new(.5f, 0),
        null,
        IsStorage: false),
    new(
        usefulFoodId,
        ItemIds.CookedMinnows,
        new(6, 0),
        null,
        IsStorage: false),
    new(
        playerOwnedToolId,
        ItemIds.BronzeAxe,
        new(.25f, 0),
        "player",
        IsStorage: false)
};
var hungryWorldAction =
    VillagerSimulation.SelectWorldAction(
        hungryVillager with
        {
            PositionX = 0,
            PositionY = 0,
            Inventory =
                PlayerInventory.CreateStartingInventory()
        },
        worldObjects);
Require(
    hungryWorldAction.Kind ==
        VillagerWorldActionKind.ApproachItem &&
    hungryWorldAction.ObjectId == usefulFoodId,
    "a hungry villager must prioritize useful food while ignoring another character's property");
var closeFoodAction =
    VillagerSimulation.SelectWorldAction(
        hungryVillager with
        {
            PositionX = 5.5f,
            PositionY = 0,
            Inventory =
                PlayerInventory.CreateStartingInventory()
        },
        worldObjects);
Require(
    closeFoodAction.Kind ==
        VillagerWorldActionKind.TakeItem &&
    closeFoodAction.ObjectId == usefulFoodId,
    "villagers must convert an approach goal into a take action inside interaction range");
var fullVillagerInventory =
    PlayerInventory.CreateStartingInventory();
for (var index = 0;
     index < VillagerSimulation.StorageDepositThreshold;
     index++)
    fullVillagerInventory[index] = ItemIds.SmallRocks;
var ownStorageId = Guid.NewGuid();
var storageAction =
    VillagerSimulation.SelectWorldAction(
        villagerSpawnA[0] with
        {
            PositionX = 0,
            PositionY = 0,
            Inventory = fullVillagerInventory
        },
        new VillagerWorldObject[]
        {
            new(
                Guid.NewGuid(),
                ItemIds.StorageChest,
                new(.25f, 0),
                "player",
                IsStorage: true),
            new(
                ownStorageId,
                ItemIds.StorageChest,
                new(.5f, 0),
                villagerSpawnA[0].Id,
                IsStorage: true)
        });
Require(
    storageAction.Kind ==
        VillagerWorldActionKind.DepositItems &&
    storageAction.ObjectId == ownStorageId,
    "villagers must deposit only into storage they own");
var stockedInventory =
    PlayerInventory.CreateStartingInventory();
stockedInventory[0] = ItemIds.Logs;
stockedInventory[1] = ItemIds.OakLogs;
var stockpileLogId = Guid.NewGuid();
var restrainedGathering =
    VillagerSimulation.SelectWorldAction(
        villagerSpawnA[0] with
        {
            PositionX = 0,
            PositionY = 0,
            Hunger = 90,
            Inventory = stockedInventory
        },
        new VillagerWorldObject[]
        {
            new(
                stockpileLogId,
                ItemIds.PineLogs,
                new(.25f, 0),
                null,
                IsStorage: false),
            new(
                Guid.NewGuid(),
                ItemIds.CookedMinnows,
                new(.25f, .25f),
                null,
                IsStorage: false)
        });
Require(
    restrainedGathering.Kind ==
        VillagerWorldActionKind.TakeItem &&
    restrainedGathering.ObjectId == stockpileLogId,
    "an unfinished stockpile goal must override passive resource limits");
Require(
    VillagerSimulation.FootBoxesOverlap(
        Vector2.Zero,
        new(
            VillagerSimulation.FootBoxWidth * .5f,
            VillagerSimulation.FootBoxDepth * .5f)) &&
    !VillagerSimulation.FootBoxesOverlap(
        Vector2.Zero,
        new(VillagerSimulation.FootBoxWidth, 0)) &&
    !VillagerSimulation.FootBoxesOverlap(
        Vector2.Zero,
        new(0, VillagerSimulation.FootBoxDepth)),
    "villager collision must use compact ground-contact boxes rather than full sprite bounds");
var collisionObstacle = new Vector2(
    VillagerSimulation.FootBoxWidth + .01f,
    0);
Require(
    VillagerSimulation.TryCollisionSidestep(
        Vector2.Zero,
        new(.02f, 0),
        Vector2.UnitX,
        collisionObstacle,
        _ => true,
        out var collisionSidestep) &&
    !VillagerSimulation.FootBoxesOverlap(
        collisionSidestep,
        collisionObstacle) &&
    MathF.Abs(collisionSidestep.Y) > .001f,
    "villagers must sidestep an actor blocking a valid movement target before abandoning it");
var socialFoodInventory =
    PlayerInventory.CreateStartingInventory();
socialFoodInventory[0] = ItemIds.CookedMinnows;
socialFoodInventory[1] = ItemIds.CookedMinnows;
var hungrySocialVillager = villagerSpawnA[0] with
{
    Hunger = 20,
    Inventory = PlayerInventory.CreateStartingInventory(),
    PositionX = 0,
    PositionY = 0
};
var socialProvider = villagerSpawnA[1] with
{
    Inventory = socialFoodInventory,
    PositionX = 2,
    PositionY = 0
};
var socialGoal = VillagerSimulation.SelectSocialGoal(
    hungrySocialVillager,
    new SocialActorObservation[]
    {
        new(
            hungrySocialVillager.Id,
            hungrySocialVillager.Name,
            Vector2.Zero,
            0,
            hungrySocialVillager.Hunger,
            0),
        new(
            socialProvider.Id,
            socialProvider.Name,
            new(2, 0),
            0,
            socialProvider.Hunger,
            2),
        new(
            "unlabelled-third-actor",
            "Sam",
            new(7, 0),
            0,
            100,
            5)
    });
Require(
    socialGoal.Intent ==
        VillagerSocialIntent.RequestFood &&
    socialGoal.OtherActorId == socialProvider.Id &&
    socialGoal.Target is not null &&
    !typeof(SocialActorObservation).GetProperties()
        .Any(property =>
            property.Name.Contains(
                "Player",
                StringComparison.OrdinalIgnoreCase)),
    "social survival planning must select people by perceived needs and supplies without knowing who is player-controlled");
var scarceProviderGoal = VillagerSimulation.SelectSocialGoal(
    socialProvider with { Hunger = 35 },
    new SocialActorObservation[]
    {
        new(
            hungrySocialVillager.Id,
            hungrySocialVillager.Name,
            new(1, 0),
            0,
            hungrySocialVillager.Hunger,
            0)
    },
    gameSeconds: 100);
Require(
    scarceProviderGoal.Intent != VillagerSocialIntent.OfferFood,
    "NPCs who expect to need their two-meal reserve must wait for an approval request instead of volunteering it away");
var curiousVillager = villagerSpawnA[0] with
{
    PositionX = 0,
    PositionY = 0,
    NextSocialGameSeconds = 0
};
var stranger = new SocialActorObservation(
    "stranger-id", "Unknown survivor", new(.5f, 0),
    0, 90, 0);
var introduction = VillagerSimulation.SelectSocialGoal(
    curiousVillager, new[] { stranger }, gameSeconds: 100);
Require(
    introduction.Intent == VillagerSocialIntent.Introduce &&
    introduction.Speech?.Contains(
        curiousVillager.Name,
        StringComparison.Ordinal) == true,
    "unknown nearby people must create a deliberate introduction goal");
var diagonalStranger = stranger with { Position = Vector2.One };
var diagonalIntroduction = VillagerSimulation.SelectSocialGoal(
    curiousVillager, new[] { diagonalStranger }, gameSeconds: 100);
Require(
    diagonalIntroduction.Speech is not null &&
    diagonalIntroduction.Target is null,
    "diagonal-adjacent villagers must be close enough to speak without entering a blocked approach loop");
var distantStranger = stranger with { Position = new(2, 0) };
var introductionApproach = VillagerSimulation.SelectSocialGoal(
    curiousVillager, new[] { distantStranger }, gameSeconds: 100);
Require(
    introductionApproach.Target is { } introductionTarget &&
    Vector2.Distance(
        introductionTarget,
        distantStranger.Position) >=
        VillagerSimulation.InteractionRange * .85f,
    "social approaches must stop at conversation range instead of walking into or swapping through the other actor");
var axeCarrierInventory = PlayerInventory.CreateStartingInventory();
axeCarrierInventory[0] = ItemIds.StoneAxe;
var visibleAxeTools = VillagerCapabilityMemory.VisibleTools(
    axeCarrierInventory);
var capabilityObserver = VillagerCapabilityMemory.Observe(
    curiousVillager,
    "axe-carrier",
    "Tomas",
    visibleAxeTools,
    distance: 2,
    gameSeconds: 120);
capabilityObserver = VillagerCapabilityMemory.Observe(
    capabilityObserver,
    "axe-carrier",
    "Tomas",
    visibleAxeTools,
    distance: 2,
    gameSeconds: 180);
var distantCapabilityObserver = VillagerCapabilityMemory.Observe(
    curiousVillager,
    "distant-axe-carrier",
    "Reed",
    visibleAxeTools,
    VillagerSimulation.SocialRange + 1,
    gameSeconds: 120);
Require(
    VillagerCapabilityMemory.KnownTools(
        capabilityObserver, "axe-carrier")
        .SequenceEqual([ItemIds.StoneAxe]) &&
    capabilityObserver.Memories?.Count(memory =>
        memory.Kind == VillagerCapabilityMemory.ObservedToolKind &&
        memory.SubjectId == "axe-carrier") == 1 &&
    VillagerCapabilityMemory.KnownTools(
        distantCapabilityObserver, "distant-axe-carrier").Count == 0,
    "nearby visible tools must create one refreshed owner-specific memory without granting distant inventory knowledge");
var capabilitySocialObserver = capabilityObserver with
{
    KnownPeople =
    [
        new(
            "ordinary-worker",
            AcquaintanceStage.Cooperative,
            "Mira",
            0),
        new(
            "axe-carrier",
            AcquaintanceStage.Cooperative,
            "Tomas",
            0)
    ],
    NextSocialGameSeconds = 0
};
var capabilityGoal = VillagerSimulation.SelectSocialGoal(
    capabilitySocialObserver,
    new SocialActorObservation[]
    {
        new(
            "ordinary-worker", "Mira", new(1, 0), 0, 90, 0),
        new(
            "axe-carrier", "Tomas", new(1.4f, 0), 0, 90, 0,
            visibleAxeTools)
    },
    gameSeconds: 200);
Require(
    capabilityGoal.Intent == VillagerSocialIntent.AskTools &&
    capabilityGoal.OtherActorId == "axe-carrier" &&
    capabilityGoal.Speech?.Contains(
        "Stone Axe", StringComparison.OrdinalIgnoreCase) == true,
    "an observed tool holder must be treated as a salient skilled person and asked specifically about that tool");
Require(
    VillagerSimulation.PerceivedName(
        curiousVillager, stranger.Id) == "the stranger" &&
    !VillagerSimulation.TryExtractIntroducedName(
        "Could you gather some wood?", out _) &&
    VillagerSimulation.TryExtractIntroducedName(
        "Hello, my name is Captain Reed.", out var claimedName) &&
    claimedName == "Captain Reed" &&
    VillagerSimulation.TryExtractIntroducedName(
        "I'm Samantha.", out var similarName) &&
    similarName == "Samantha" &&
    !VillagerSimulation.TryExtractIntroducedName(
        "I'm hungry.", out _),
    "NPCs must not know profile names until an actor explicitly introduces themselves");
var unansweredIntroduction =
    VillagerSimulation.RecordIntroductionResponse(
        curiousVillager, stranger.Id, null, 100);
Require(
    VillagerSimulation.KnownPerson(
        unansweredIntroduction, stranger.Id) is
        {
            Stage: AcquaintanceStage.Seen,
            StatedName: null,
            ConversationCount: 1
        } &&
    unansweredIntroduction.NextSocialGameSeconds ==
        100 + VillagerSimulation.IntroductionRetryRealSeconds *
        VillagerSimulation.GameSecondsPerRealSecond &&
    VillagerSimulation.SelectSocialGoal(
        unansweredIntroduction,
        new[] { stranger },
        gameSeconds: unansweredIntroduction.NextSocialGameSeconds - 1)
        .Intent == VillagerSocialIntent.None,
    "an unanswered introduction must remember the attempt without inventing a name or repeatedly asking before the retry window");
Require(
    GameHostWindow.FallbackNpcReply(
        unansweredIntroduction,
        "What is my name?",
        stranger.Id) == "You haven't told me your name yet.",
    "NPC fallback dialogue must not invent a player's name before an introduction");
var longPipeDialogue = new string('x', 220);
Require(
    VillagerSimulation.RecordDialogueTurn(
            curiousVillager,
            "player",
            "Rook",
            longPipeDialogue,
            101)
        .ConversationHistory?.Last().Text == longPipeDialogue,
    "conversation memory must retain the full 256-character chat input rather than truncating valid pipe dialogue at 160 characters");
curiousVillager = VillagerSimulation.RecordConversation(
    curiousVillager,
    stranger.Id,
    "Sam",
    introduction.Intent,
    100);
Require(
    curiousVillager.KnownPeople?.Single() is
        {
            Stage: AcquaintanceStage.Introduced,
            StatedName: "Sam",
            ConversationCount: 1
        } &&
    VillagerSimulation.PerceivedName(
        curiousVillager, stranger.Id) == "Sam" &&
    GameHostWindow.FallbackNpcReply(
        curiousVillager,
        "What name did I tell you?",
        stranger.Id) == "You told me your name is Sam." &&
    curiousVillager.Memories?.Any(value =>
        value.Kind == "social-knowledge" &&
        value.SubjectId == stranger.Id) == true &&
    curiousVillager.Relationships?.Single().CharacterId ==
        stranger.Id &&
    curiousVillager.Relationships.Single().State.Trust > 0 &&
    VillagerSimulation.SelectSocialGoal(
        curiousVillager,
        new[] { stranger },
        gameSeconds: 110).Intent ==
        VillagerSocialIntent.None,
    "introductions must become persistent knowledge and enforce a quiet social cooldown");
Require(
    curiousVillager.NextSocialGameSeconds - 100 ==
        VillagerSimulation.SocialRealCooldown(
            curiousVillager,
            introduction.Intent) *
        VillagerSimulation.GameSecondsPerRealSecond,
    "social cooldowns must convert exactly once from real seconds to game seconds");
var originQuestion = VillagerSimulation.SelectSocialGoal(
    curiousVillager,
    new[] { stranger },
    gameSeconds: curiousVillager.NextSocialGameSeconds);
Require(
    originQuestion.Intent == VillagerSocialIntent.AskOrigin,
    "acquaintances must gradually seek information about how they reached the island");
var urgentSocialCooldown = VillagerSimulation.SocialCooldown(
    curiousVillager with
    {
        Hunger = 15,
        Need = VillagerNeed.Food,
        Goals = [],
        Promises = []
    },
    VillagerSocialIntent.RequestFood);
var uncommittedCooldown = VillagerSimulation.SocialCooldown(
    curiousVillager with
    {
        Goals = [],
        Promises = []
    },
    VillagerSocialIntent.AskSurvival);
var committedCooldown = VillagerSimulation.SocialCooldown(
    curiousVillager,
    VillagerSocialIntent.AskSurvival);
Require(
    urgentSocialCooldown == 15 &&
    committedCooldown > uncommittedCooldown &&
    VillagerSimulation.SocialRealCooldown(
        curiousVillager,
        VillagerSocialIntent.SeekCompany) >= 12,
    "social cooldowns must shorten for urgent needs and lengthen when active goals require attention");
var matureRelationship = curiousVillager with
{
    Need = VillagerNeed.Social,
    NextSocialGameSeconds = 0,
    KnownPeople =
    [
        new(
            stranger.Id,
            AcquaintanceStage.DiscussedSkills,
            "Sam",
            500,
            4)
    ]
};
Require(
    VillagerSimulation.SelectSocialGoal(
        matureRelationship,
        new[] { stranger },
        gameSeconds: 600).Intent ==
        VillagerSocialIntent.None &&
    VillagerSimulation.SelectSocialGoal(
        matureRelationship,
        new[] { stranger },
        gameSeconds:
            500 +
            VillagerSimulation.RelationshipCheckInSeconds).Intent ==
        VillagerSocialIntent.SeekCompany,
    "completed acquaintances must not loop companionship dialogue and may only check in after a substantial interval");
var bondAwareSocialState = matureRelationship with
{
    KnownPeople =
    [
        new("friend-id", AcquaintanceStage.DiscussedSkills,
            "Friend", 0, 5),
        new("rival-id", AcquaintanceStage.DiscussedSkills,
            "Rival", 0, 5)
    ],
    Relationships =
    [
        new("friend-id", new(Trust: 25, Affection: 15)),
        new("rival-id", new(Trust: -20, Resentment: 25))
    ]
};
var bondAwareSocialGoal = VillagerSimulation.SelectSocialGoal(
    bondAwareSocialState,
    new SocialActorObservation[]
    {
        new("rival-id", "Rival", new(.5f, 0), 0, 90, 0),
        new("friend-id", "Friend", new(2, 0), 0, 90, 0)
    },
    VillagerSimulation.RelationshipCheckInSeconds + 1);
Require(
    bondAwareSocialGoal.OtherActorId == "friend-id" &&
    bondAwareSocialGoal.Intent == VillagerSocialIntent.SeekCompany,
    "optional companionship must prefer a farther friend over a nearby rival");
Require(
    villagerSpawnA.All(value =>
        value.Goals?.Count == 2 &&
        value.Goals.All(goal =>
            goal.Status == CommitmentStatus.Active)),
    "new villagers must begin with persistent food and wood survival goals");
var goalProgressVillager = VillagerCommitmentService.RecordAcquiredItem(
    villagerSpawnA[0], ItemIds.WildBerries, 2);
goalProgressVillager = VillagerCommitmentService.RecordAcquiredItem(
    goalProgressVillager, ItemIds.OakLogs, 4);
Require(
    goalProgressVillager.Goals?.Single(goal =>
        goal.Kind == VillagerGoalKind.StockpileFood).Progress == 2 &&
    goalProgressVillager.Goals.Single(goal =>
        goal.Kind == VillagerGoalKind.StockpileWood).Status ==
        CommitmentStatus.Fulfilled,
    "actual food and any log acquisitions must advance persistent survival goals");
var explorationResourceVillager = villagerSpawnA[0] with
{
    PositionX = 0,
    PositionY = 0,
    WorkRole = VillagerWorkRole.Exploration,
    Inventory = PlayerInventory.CreateStartingInventory()
};
var loneAllRounder = explorationResourceVillager with
{
    WorkRole = VillagerWorkRole.Unassigned,
    SettlementGroupId = null,
    Inventory = new string?[PlayerInventory.Capacity]
};
var giftGiverInventory = new string?[PlayerInventory.Capacity];
giftGiverInventory[3] = ItemIds.StoneAxe;
var giftReceiverInventory = new string?[PlayerInventory.Capacity];
Require(
    VillagerGiftTransferService.TryTransfer(
        giftGiverInventory, 3, ItemIds.StoneAxe,
        giftReceiverInventory,
        out var giftGiverAfter, out var giftReceiverAfter) &&
    giftGiverAfter[3] is null &&
    PlayerInventory.Count(giftReceiverAfter, ItemIds.StoneAxe) == 1 &&
    !VillagerGiftTransferService.TryTransfer(
        giftGiverInventory, 3, ItemIds.StoneKnife,
        giftReceiverInventory,
        out var rejectedGiftGiver, out var rejectedGiftReceiver) &&
    rejectedGiftGiver[3] == ItemIds.StoneAxe &&
    PlayerInventory.Count(rejectedGiftReceiver) == 0,
    "accepted NPC gifts must transfer atomically while stale or mismatched offers leave both inventories unchanged");
var interactionInventory = new string?[PlayerInventory.Capacity];
interactionInventory[2] = ItemIds.CookedMinnows;
interactionInventory[5] = ItemIds.StoneAxe;
var foodInteractionMenu = VillagerInteractionMenu.Build(
    interactionInventory, -1);
var selectedInteractionMenu = VillagerInteractionMenu.Build(
    interactionInventory, 5);
var emptyInteractionMenu = VillagerInteractionMenu.Build(
    new string?[PlayerInventory.Capacity], -1);
Require(
    foodInteractionMenu.Select(value => value.Label).SequenceEqual(
        ["Walk here", "Attack", "Give food", "Examine"]) &&
    foodInteractionMenu[2].InventorySlot == 2 &&
    selectedInteractionMenu.Select(value => value.Label).SequenceEqual(
        ["Walk here", "Attack", "Give stone axe", "Examine"]) &&
    selectedInteractionMenu[2].InventorySlot == 5 &&
    emptyInteractionMenu.Select(value => value.Label).SequenceEqual(
        ["Walk here", "Attack", "Examine"]),
    "villager right-click options must prioritize the selected gift, otherwise offer food, and omit Give when nothing is available");
var offeredAxeId = Guid.NewGuid();
var offeredAxeAction = VillagerSimulation.SelectWorldAction(
    loneAllRounder,
    new VillagerWorldObject[]
    {
        new(offeredAxeId, ItemIds.StoneAxe, new(.25f, 0), null, false)
    });
Require(
    VillagerWorkCapability.IsAllRounder(loneAllRounder) &&
    VillagerWorkCapability.CanPerform(
        loneAllRounder, VillagerWorkRole.Wood) &&
    VillagerWorkCapability.CanPerform(
        loneAllRounder, VillagerWorkRole.Exploration) &&
    VillagerResourcePriority.Score(loneAllRounder, ItemIds.StoneAxe) == 75 &&
    offeredAxeAction.Kind == VillagerWorldActionKind.TakeItem &&
    offeredAxeAction.ObjectId == offeredAxeId,
    "a lone all-rounder must pick up a useful offered axe and be eligible for woodcutting and exploration work");
var equippedAllRounder = loneAllRounder with
{
    Inventory = new string?[PlayerInventory.Capacity]
};
equippedAllRounder.Inventory[0] = ItemIds.StoneAxe;
Require(
    VillagerResourcePriority.Score(equippedAllRounder, ItemIds.StoneAxe) == 0,
    "an all-rounder must not loop picking up duplicate tools that do not improve their equipment");
Require(
    VillagerResourcePriority.Score(
        explorationResourceVillager, ItemIds.ClamShell) == 0 &&
    VillagerResourcePriority.Score(
        explorationResourceVillager, ItemIds.LargeRock) > 0 &&
    VillagerResourcePriority.Score(
        explorationResourceVillager, ItemIds.Sticks) > 0 &&
    !VillagerCraftPlanner.PriorityFor(VillagerWorkRole.Exploration)
        .Contains(ItemIds.Rope) &&
    VillagerCraftPlanner.PriorityFor(VillagerWorkRole.Food)
        .Contains(ItemIds.Rope),
    "role supply plans must reject decorative shoreline items and reserve rope for roles that use it");
var prerequisiteRockId = Guid.NewGuid();
var prerequisiteAction = VillagerSimulation.SelectWorldAction(
    explorationResourceVillager,
    new VillagerWorldObject[]
    {
        new(Guid.NewGuid(), ItemIds.ClamShell, new(.1f, 0), null, false),
        new(prerequisiteRockId, ItemIds.LargeRock, new(.5f, 0), null, false)
    });
Require(
    prerequisiteAction.Kind == VillagerWorldActionKind.TakeItem &&
    prerequisiteAction.ObjectId == prerequisiteRockId,
    "NPC world targeting must choose a required primitive-tool material over a nearer irrelevant collectible");
Require(
    DialogueResponseService.Resolve(
        "It's good to meet you, M",
        "It's good to meet you, Mira.") ==
        "It's good to meet you, Mira." &&
    DialogueResponseService.Resolve(
        "I can gather wood.",
        "Fallback") == "I can gather wood.",
    "truncated dialogue must fall back without replacing complete model responses");
Require(
    VillagerCommitmentService.TryParseGatherRequest(
        "Could you gather 3 logs for me?",
        out var promisedItem,
        out var promisedQuantity) &&
    promisedItem == ItemIds.Logs &&
    promisedQuantity == 3,
    "natural chat requests must resolve to validated item commitments");
Require(
    VillagerCommitmentService.TryParseGatherRequest(
        "If I gather stones for you, would you gather fibre for me?",
        out var tradedItem,
        out var tradedQuantity) &&
    tradedItem == ItemIds.PlantFibres &&
    tradedQuantity == 1,
    "conditional trades must plan the NPC's requested clause and resolve common item aliases");
var requestTemplates = new Func<string, string>[]
{
    item => $"Please fetch two {item} for me.",
    item => $"Can you find {item}?",
    item => $"Would you collect {item} for me?",
    item => $"Go look for {item}.",
    item => $"Pick up {item} and bring it back."
};
var requestTemplateIndex = 0;
foreach (var catalogItem in ItemCatalog.All.Where(item => item.Droppable))
{
    var template = requestTemplates[
        requestTemplateIndex++ % requestTemplates.Length];
    var requestText = template(catalogItem.Name);
    Require(
        VillagerCommitmentService.TryParseGatherRequest(
            requestText, out var resolvedCatalogItem,
            out var resolvedCatalogQuantity) &&
        resolvedCatalogItem == catalogItem.Id &&
        resolvedCatalogQuantity ==
            (requestText.Contains("two", StringComparison.Ordinal) ? 2 : 1),
        $"catalog-driven request language must resolve '{requestText}' to {catalogItem.Id}");
}
Require(
    VillagerCommitmentService.TryParseGatherRequest(
        "Could you fetch a bundle of timber?",
        out var timberItem, out _) && timberItem == ItemIds.Logs &&
    VillagerCommitmentService.TryParseGatherRequest(
        "Please search for some kindling.",
        out var kindlingItem, out _) && kindlingItem == ItemIds.Sticks &&
    VillagerCommitmentService.TryParseGatherRequest(
        "Will you bring me three fibers?",
        out var fiberItem, out var fiberQuantity) &&
    fiberItem == ItemIds.PlantFibres && fiberQuantity == 3,
    "resource synonyms and written quantities must resolve through the reusable item-language layer");
Require(
    VillagerCommitmentService.TryResolveAiItemProposal(
        "If I gather stones for you, would you gather fibre for me?",
        "gather", "fibre", 1,
        out var aiProposalKind,
        out var aiProposalItem,
        out var aiProposalQuantity) &&
    aiProposalKind == VillagerPromiseKind.GatherItem &&
    aiProposalItem == ItemIds.PlantFibres &&
    aiProposalQuantity == 1 &&
    !VillagerCommitmentService.TryResolveAiItemProposal(
        "Would you gather fibre for me?",
        "none", "fibre", 1, out _, out _, out _) &&
    !VillagerCommitmentService.TryResolveAiItemProposal(
        "Would you gather fibre for me?",
        "gather", ItemIds.Logs, 1, out _, out _, out _),
    "only Ollama's structured executable proposal may enter the NPC brain, and its item must validate against the player's words");
const string observedImmediateGiveRequest =
    "Conrad, we need ten planks. You have several logs. " +
    "Will you give me four logs now so I can start building?";
Require(
    VillagerCommitmentService.TryResolveAiItemProposal(
        observedImmediateGiveRequest,
        "give", ItemIds.Logs, 4,
        out var immediateGiveKind,
        out var immediateGiveItem,
        out var immediateGiveQuantity) &&
    immediateGiveKind == VillagerPromiseKind.GiveItem &&
    immediateGiveItem == ItemIds.Logs &&
    immediateGiveQuantity == 4,
    "direct hand-over wording must become a quantity-aware give proposal rather than speech-only acceptance");
Require(
    VillagerCollectionRouteService.For(ItemIds.Logs) ==
        VillagerCollectionRoute.TreeLogs &&
    VillagerCollectionRouteService.For(ItemIds.OakLogs) ==
        VillagerCollectionRoute.TreeLogs &&
    VillagerCollectionRouteService.For(ItemIds.Sticks) ==
        VillagerCollectionRoute.TreeSticks &&
    VillagerCollectionRouteService.For(ItemIds.PlantFibres) ==
        VillagerCollectionRoute.Forage &&
    VillagerCollectionRouteService.For(ItemIds.WildBerries) ==
        VillagerCollectionRoute.Forage &&
    VillagerCollectionRouteService.For(ItemIds.RawMinnows) ==
        VillagerCollectionRoute.Fish &&
    VillagerCollectionRouteService.For(ItemIds.CopperOre) ==
        VillagerCollectionRoute.Mine &&
    VillagerCollectionRouteService.For(ItemIds.SpiralShell) ==
        VillagerCollectionRoute.Ground,
    "promised collection must route items to their producing capability instead of unrelated generic gathering");
Require(
    !VillagerCollectionRouteService.HasRequiredTool(
        VillagerCollectionRoute.TreeLogs, new string?[28]) &&
    VillagerCollectionRouteService.HasRequiredTool(
        VillagerCollectionRoute.TreeLogs,
        [ItemIds.StoneAxe, .. new string?[27]]) &&
    !VillagerCollectionRouteService.HasRequiredTool(
        VillagerCollectionRoute.Fish, new string?[28]) &&
    !VillagerCollectionRouteService.HasRequiredTool(
        VillagerCollectionRoute.Mine, new string?[28]) &&
    VillagerCollectionRouteService.HasRequiredTool(
        VillagerCollectionRoute.Forage, new string?[28]),
    "promised production must identify tool prerequisites while leaving tool-free gathering available");
const string trailingConfirmationRequest =
    "Edith, please bring me two sticks for the storage chest now. " +
    "Will you gather and give them to me?";
Require(
    VillagerCommitmentService.TryResolveAiItemProposal(
        trailingConfirmationRequest,
        "gather", ItemIds.Sticks, 1,
        out var trailingConfirmationKind,
        out var trailingConfirmationItem,
        out var trailingConfirmationQuantity) &&
    trailingConfirmationKind == VillagerPromiseKind.GiveItem &&
    trailingConfirmationItem == ItemIds.Sticks &&
    trailingConfirmationQuantity == 2,
    "an item-less confirmation clause must retain the concrete item, quantity and delivery intent from the preceding request");
var immediateGiveAcceptance = VillagerCommitmentService.TryAccept(
    villagerSpawnA[0] with
    {
        Inventory =
        [
            ItemIds.Logs, ItemIds.Logs, ItemIds.Logs, ItemIds.Logs
        ]
    }, "requester", immediateGiveKind,
    immediateGiveItem, immediateGiveQuantity, 275);
var immediateGivePlan = immediateGiveAcceptance.Promise is { } givePromise
    ? VillagerPromisePlanService.CompileAiDirective(
        VillagerCommitmentService.AddPromise(
            villagerSpawnA[0] with
            {
                Inventory =
                [
                    ItemIds.Logs, ItemIds.Logs,
                    ItemIds.Logs, ItemIds.Logs
                ]
            }, givePromise),
        "give", immediateGiveItem, immediateGiveQuantity,
        "requester", 4, 5, 0, 275, 0)
    : villagerSpawnA[0];
Require(
    immediateGiveAcceptance.Accepted &&
    VillagerPromisePlanService.CurrentDirective(immediateGivePlan) is null &&
    immediateGivePlan.ActionPlan is
    [
        {
            Action: VillagerPromisePlanAction.Deliver,
            ItemId: ItemIds.Logs,
            RemainingQuantity: 4
        }
    ],
    "an accepted immediate give request must enter the formal controller plan once without a duplicate directive");
const string observedSplitResponsibilityRequest =
    "Stephen, please gather three large rocks and meet me at this spot " +
    "in one hour. I will gather plant fibre while you do that. Do you agree?";
Require(
    VillagerCommitmentService.TryResolveAiItemProposal(
        observedSplitResponsibilityRequest,
        "gather",
        ItemIds.LargeRock,
        1,
        out var splitProposalKind,
        out var splitProposalItem,
        out var splitProposalQuantity) &&
    splitProposalKind == VillagerPromiseKind.GatherItem &&
    splitProposalItem == ItemIds.LargeRock &&
    splitProposalQuantity == 3,
    "an NPC request followed by the player's separate task must preserve actor responsibility and the NPC quantity");
Require(
    VillagerCommitmentService.TryResolveAiItemProposal(
        observedSplitResponsibilityRequest,
        "meet",
        "",
        1,
        out var rendezvousProposalKind,
        out var rendezvousProposalItem,
        out var rendezvousProposalQuantity) &&
    rendezvousProposalKind == VillagerPromiseKind.GatherItem &&
    rendezvousProposalItem == ItemIds.LargeRock &&
    rendezvousProposalQuantity == 3,
    "an accepted combined gather-and-rendezvous request must preserve the collection commitment when the model prioritizes meet");
const string observedInformalResponsibilityRequest =
    "Linnet, please gather three more large rocks while I find fibre and " +
    "sticks. Meet me here when you are done. Do you agree?";
Require(
    VillagerCommitmentService.TryResolveAiItemProposal(
        observedInformalResponsibilityRequest,
        "gather",
        ItemIds.LargeRock,
        3,
        out _,
        out var informalProposalItem,
        out var informalProposalQuantity) &&
    informalProposalItem == ItemIds.LargeRock &&
    informalProposalQuantity == 3,
    "an NPC task followed by 'while I' player work must not bind the player's item to the NPC promise");
Require(
    VillagerCommitmentService.TryResolveAiItemProposal(
        "Yvette, please gather one wild berry for me now. Do you agree?",
        "clarify", "", 1,
        out var clarifiedKind,
        out var clarifiedItem,
        out var clarifiedQuantity) &&
    clarifiedKind == VillagerPromiseKind.GatherItem &&
    clarifiedItem == ItemIds.WildBerries &&
    clarifiedQuantity == 1,
    "an accepting model reply mislabeled as clarify must still execute the player's concrete item request");
var clarifiedAcceptance = VillagerCommitmentService.TryAccept(
    villagerSpawnA[0],
    "requester",
    clarifiedKind,
    clarifiedItem,
    clarifiedQuantity,
    300);
Require(
    clarifiedAcceptance.Accepted &&
    clarifiedAcceptance.Promise is not null,
    "a resolved concrete berry request must be eligible for an executable villager commitment");
var distantFibre = new WorldVegetationRenderItem(
    0, 2, 2, new(2.5f, 2.5f), "fibre-distant", "plant", null,
    CanGatherFibre: true, CanGatherBerries: false);
var requestedFibre = new WorldVegetationRenderItem(
    1, 20, 20, new(20.25f, 20.25f), "fibre-requested", "plant", null,
    CanGatherFibre: true, CanGatherBerries: false);
var pipeVegetation = new[]
{
    new ControlVegetationTarget(
        distantFibre, new(2.5f, 2.5f), IsReady: false),
    new ControlVegetationTarget(requestedFibre, new(20.25f, 20.25f))
};
Require(
    ControlTargetSelection.Vegetation(
        pipeVegetation, false, null, new(20.2f, 20.2f), true) ==
        requestedFibre &&
    ControlTargetSelection.Vegetation(
        pipeVegetation, false, "FIBRE-DISTANT", new(20.2f, 20.2f), true) ==
        null &&
    ControlTargetSelection.Vegetation(
        pipeVegetation, false, null, new(50, 50), true) is null,
    "control-pipe vegetation targeting must prefer ready requested coordinates and reject depleted or unrelated plants");
var visuallyNearMiningNode = new WorldVegetationRenderItem(
    2, 60, 60, new(0, 0), "ore-visually-near", "ore", null,
    CanGatherFibre: false, CanGatherBerries: false);
var tileNearMiningNode = new WorldVegetationRenderItem(
    3, 5, 5, new(9000, 9000), "ore-tile-near", "ore", null,
    CanGatherFibre: false, CanGatherBerries: false);
var pipeMiningNodes = new[]
{
    new ControlMiningTarget(
        visuallyNearMiningNode, new(60.5f, 60.5f), true),
    new ControlMiningTarget(
        tileNearMiningNode, new(5.5f, 5.5f), true),
    new ControlMiningTarget(
        requestedFibre, new(5.25f, 5.25f), false)
};
Require(
    ControlTargetSelection.Mining(
        pipeMiningNodes, null, new(5, 5), false) == tileNearMiningNode &&
    ControlTargetSelection.Mining(
        pipeMiningNodes, "ORE-VISUALLY-NEAR", new(5, 5), false) ==
        visuallyNearMiningNode &&
    ControlTargetSelection.Mining(
        pipeMiningNodes, null, new(20, 20), true) is null,
    "control-pipe mining targeting must use tile positions rather than projected render coordinates, preserve exact-key selection, and reject distant nodes");
Require(
    ControlCombatCommands.TryParseStance(
        "accurate", out var accuratePipeStance) &&
    accuratePipeStance == MeleeCombatStance.Accurate &&
    ControlCombatCommands.TryParseStance(
        "AGGRESSIVE", out var aggressivePipeStance) &&
    aggressivePipeStance == MeleeCombatStance.Aggressive &&
    ControlCombatCommands.TryParseStance(
        "defensive", out var defensivePipeStance) &&
    defensivePipeStance == MeleeCombatStance.Defensive &&
    !ControlCombatCommands.TryParseStance("magic", out _),
    "control-pipe combat styles must accept every melee stance and reject unsupported styles");
var pipeEnemyNear = new EnemyState(
    Guid.NewGuid(), Guid.NewGuid(), EnemyKind.GrassSlime,
    new(3, 3), new(3, 3), new(3, 3), 0, 1, 20, 20);
var pipeEnemyFar = new EnemyState(
    Guid.NewGuid(), Guid.NewGuid(), EnemyKind.GrassSlime,
    new(30, 30), new(30, 30), new(30, 30), 0, 1, 20, 20);
Require(
    ControlTargetSelection.Enemy(
        [pipeEnemyFar, pipeEnemyNear], null, Vector2.Zero) == pipeEnemyNear &&
    ControlTargetSelection.Enemy(
        [pipeEnemyNear, pipeEnemyFar], pipeEnemyFar.Id.ToString(),
        Vector2.Zero) == pipeEnemyFar &&
    ControlTargetSelection.Enemy(
        [pipeEnemyNear], pipeEnemyNear.Id.ToString("N"),
        Vector2.Zero) == pipeEnemyNear &&
    ControlTargetSelection.Enemy(
        [pipeEnemyNear], "not-an-id", Vector2.Zero) is null,
    "control-pipe enemy targeting must accept serialized and compact IDs, default to the nearest enemy, and reject malformed IDs");
var acceptance = VillagerCommitmentService.TryAccept(
    villagerSpawnA[0],
    "requester",
    VillagerPromiseKind.GatherItem,
    promisedItem,
    promisedQuantity,
    300);
Require(
    acceptance.Accepted &&
    acceptance.Promise is
    {
        Status: CommitmentStatus.Active,
        TargetQuantity: 3,
        Progress: 0
    },
    "available villagers must accept bounded, measurable promises");
var committedVillager =
    VillagerCommitmentService.AddPromise(
        villagerSpawnA[0],
        acceptance.Promise!);
Require(
    committedVillager.ActionPlan is
    [
        {
            Action: VillagerPromisePlanAction.Collect,
            ItemId: ItemIds.Logs,
            RemainingQuantity: 3
        }
    ],
    "accepting an Ollama-guided commitment must compile a persistent controller action queue");
var opcodeMappings = new (string Action, VillagerPromisePlanAction Opcode)[]
{
    ("gather", VillagerPromisePlanAction.Collect),
    ("give", VillagerPromisePlanAction.Deliver),
    ("meet", VillagerPromisePlanAction.Rendezvous),
    ("seek_shelter", VillagerPromisePlanAction.MoveTo),
    ("enter_cave", VillagerPromisePlanAction.InteractWithTarget),
    ("craft", VillagerPromisePlanAction.CraftItem),
    ("build", VillagerPromisePlanAction.BuildObject),
    ("drop", VillagerPromisePlanAction.DepositItem),
    ("withdraw", VillagerPromisePlanAction.WithdrawItem),
    ("follow", VillagerPromisePlanAction.FollowActor),
    ("explore", VillagerPromisePlanAction.ExploreArea),
    ("wait", VillagerPromisePlanAction.WaitUntil),
    ("warn", VillagerPromisePlanAction.TalkToActor),
    ("attack", VillagerPromisePlanAction.AttackTarget),
    ("flee", VillagerPromisePlanAction.FleeFromTarget),
    ("rest", VillagerPromisePlanAction.Rest),
    ("seek_food", VillagerPromisePlanAction.SeekFood),
    ("cut_tree", VillagerPromisePlanAction.CutTree),
    ("mine", VillagerPromisePlanAction.Mine),
    ("fish", VillagerPromisePlanAction.Fish),
    ("cook", VillagerPromisePlanAction.Cook),
    ("dig", VillagerPromisePlanAction.Dig)
};

var beginnerFish = new WorldFish(
    6, 0, WorldFishSpecies.ShoreMinnows,
    "FISHS_NN", 0, "fish:beginner");
var advancedFish = new WorldFish(
    1, 0, WorldFishSpecies.OceanMackerel,
    "FISHS_NN", 0, "fish:advanced");
var selectedBeginnerFish = FishingTargetSelection.Select(
    [advancedFish, beginnerFish], null, 1, 1);
var rejectedAdvancedFish = FishingTargetSelection.Select(
    [advancedFish, beginnerFish], advancedFish.StableKey, 1, 1);
Require(
    selectedBeginnerFish.Fish == beginnerFish &&
    rejectedAdvancedFish.Failure ==
        FishingTargetFailure.FishingLevelRequired &&
    rejectedAdvancedFish.Requirement?.RequiredLevel == 13 &&
    FishingTargetSelection.Select(
        [beginnerFish], null, 1, null).Failure ==
        FishingTargetFailure.FishingNetNotFound,
    "automatic fishing must skip inaccessible nearby fish while exact targets report their skill or equipment requirement");
Require(
    WorldActionReach.CanComplete(
        new Vector2(3.08f, 0), Vector2.Zero, 3) &&
    !WorldActionReach.CanComplete(
        new Vector2(3.081f, 0), Vector2.Zero, 3),
    "fishing approach selection and queued-action completion must use the same reach boundary");

var seekFoodPlan = VillagerPromisePlanService.CompileAiDirective(
    villagerSpawnA[0] with { ActionPlan = null },
    "seek_food", "", 1, "requester", null, null,
    (int)WorldLevel.Overworld, 600, 0);
Require(
    VillagerPromisePlanService.PlansFor(seekFoodPlan).Single().Action ==
        VillagerPromisePlanAction.SeekFood &&
    VillagerPromisePlanService.CurrentPlanDescription(
        seekFoodPlan, 600) == "Seeking food nearby." &&
    VillagerPromisePlanService.CurrentDirective(
        VillagerCommitmentService.RecordAcquiredItem(
            seekFoodPlan, ItemIds.WildBerries)) is null,
    "accepted seek-food dialogue must remain observable and complete only after the NPC physically acquires edible food");
Require(
    VillagerPromisePlanService.CurrentDirective(
        VillagerCommitmentService.RecordAcquiredItem(
            seekFoodPlan, ItemIds.PlantFibres)) is
        { Action: VillagerPromisePlanAction.SeekFood },
    "non-food acquisitions must not falsely complete an accepted seek-food directive");
foreach (var mapping in opcodeMappings)
{
    var planned = VillagerPromisePlanService.CompileAiDirective(
        villagerSpawnA[0] with { ActionPlan = null },
        mapping.Action,
        ItemIds.Logs,
        2,
        "requester",
        4,
        5,
        (int)WorldLevel.Overworld,
        600,
        10);
    Require(
        VillagerPromisePlanService.CurrentDirective(planned) is
        {
            PromiseId: var directivePromiseId,
            TargetActorId: "requester",
            TargetX: 4,
            TargetY: 5,
            ExecuteAfterGameSeconds: 1200
        } directive &&
        directivePromiseId == Guid.Empty &&
        directive.Action == mapping.Opcode,
        $"Ollama action '{mapping.Action}' must compile to the {mapping.Opcode} controller opcode");
}
var retryPlan = VillagerPromisePlanService.CompileAiDirective(
    villagerSpawnA[0] with { ActionPlan = null },
    "seek_shelter", "", 1, null, null, null,
    (int)WorldLevel.Overworld, 600, 0);
var retryStep = VillagerPromisePlanService.CurrentDirective(retryPlan)!;
for (var retry = 0; retry < retryStep.MaximumAttempts; retry++)
{
    var activeRetry = VillagerPromisePlanService.CurrentDirective(retryPlan);
    if (activeRetry is null) break;
    retryPlan = VillagerPromisePlanService.FailOrRetryDirective(
        retryPlan, activeRetry);
}
Require(
    VillagerPromisePlanService.CurrentDirective(retryPlan) is null,
    "invalid or unreachable controller steps must leave the queue after their bounded retry budget");
var persistentCollectPlan = VillagerPromisePlanService.CompileAiDirective(
    villagerSpawnA[0] with { ActionPlan = null },
    "gather", ItemIds.LargeRock, 3, "requester", null, null,
    (int)WorldLevel.Overworld, 600, 0);
var collectedOne = VillagerCommitmentService.RecordAcquiredItem(
    persistentCollectPlan, ItemIds.LargeRock);
var collectedThree = VillagerCommitmentService.RecordAcquiredItem(
    collectedOne, ItemIds.LargeRock, 2);
Require(
    VillagerPromisePlanService.CurrentDirective(persistentCollectPlan) is
        { RemainingQuantity: 3 } &&
    VillagerPromisePlanService.CurrentDirective(collectedOne) is
        { RemainingQuantity: 2 } &&
    VillagerPromisePlanService.CurrentDirective(collectedThree) is null,
    "collection directives must remain queued until matching acquired items satisfy their quantity");
Require(
    VillagerCommitmentService.TryResolveAiItemProposal(
        "Can you gather two plant fibres and bring them back to me here?",
        "gather", ItemIds.PlantFibres, 2,
        out var deliveryKind, out var deliveryItem, out var deliveryQuantity) &&
    deliveryKind == VillagerPromiseKind.GiveItem &&
    deliveryItem == ItemIds.PlantFibres &&
    deliveryQuantity == 2,
    "explicit bring-back language must create a delivery promise even when Ollama emits the generic gather opcode");
var malformedSpokenAcceptance = new NpcAiInterpretation(
    "merewin", "", "help", "none", "", 2, 0,
    "", "", "I shall gather the plant fibres for your tools immediately.",
    false,
    "Merewin needs materials. I must find and collect two pieces of plant fibre.",
    "none", 100, 5, 0, 80);
var normalizedSpokenAcceptance = VillagerDialogueCommitmentService
    .NormalizePendingProposal(
        malformedSpokenAcceptance,
        "Can you gather two plant fibres and bring them back to me here?");
var preservedRefusal = VillagerDialogueCommitmentService
    .NormalizePendingProposal(
        malformedSpokenAcceptance with
        {
            Reply = "I cannot agree to gather those plant fibres."
        },
        "Can you gather two plant fibres and bring them back to me here?");
Require(
    normalizedSpokenAcceptance is
    {
        Decision: "accept",
        Action: "give",
        ItemId: ItemIds.PlantFibres,
        Quantity: 2
    } &&
    preservedRefusal.Decision == "none" &&
    preservedRefusal.Action == "none",
    "clear high-willingness spoken acceptance must repair malformed none/none model output without converting refusals");
var liveProposalResolution = VillagerCommitmentService
    .ResolveAcceptedItemProposal(
        villagerSpawnA[0] with
        {
            Inventory = [ItemIds.PlantFibres]
        },
        "merewin",
        "Conrad, please gather two plant fibres and bring them back to me here. Will you do that now?",
        normalizedSpokenAcceptance,
        700);
Require(
    liveProposalResolution is
    {
        Recognized: true,
        Accepted: true,
        State:
        {
            Promises:
            [
                {
                    Kind: VillagerPromiseKind.GiveItem,
                    PromiseeId: "merewin",
                    ItemId: ItemIds.PlantFibres,
                    TargetQuantity: 2
                }
            ]
        }
    } &&
    liveProposalResolution.State.ActionPlan is
    [
        {
            Action: VillagerPromisePlanAction.Collect,
            RemainingQuantity: 1
        },
        {
            Action: VillagerPromisePlanAction.Deliver,
            RemainingQuantity: 2
        }
    ],
    "the exact live proposal must atomically create one executable delivery promise and account for carried fibre");
var liveDeliveryPromise = liveProposalResolution.State.Promises!.Single();
var partiallyDeliveredPromise = liveDeliveryPromise with { Progress = 1 };
var deliveryReadyState = liveProposalResolution.State with
{
    Promises = [partiallyDeliveredPromise],
    Inventory = [ItemIds.PlantFibres]
};
Require(
    VillagerCommitmentService.HasDeliverableItem(
        liveProposalResolution.State, liveDeliveryPromise) &&
    !VillagerCommitmentService.HasDeliverableItem(
        liveProposalResolution.State with
        {
            Inventory = [ItemIds.LargeRock]
        },
        liveDeliveryPromise) &&
    !VillagerPromisePlanService.NeedsItem(
        deliveryReadyState, ItemIds.PlantFibres) &&
    VillagerPromisePlanService.NeedsItem(
        deliveryReadyState with { Inventory = [] },
        ItemIds.PlantFibres),
    "delivery rendezvous must require a matching carried item, and collection must stop once carried stock covers the outstanding promise");
var deliveryAcceptance = VillagerCommitmentService.TryAccept(
    villagerSpawnA[0] with
    {
        Inventory = [ItemIds.PlantFibres]
    },
    "requester",
    VillagerPromiseKind.GiveItem,
    ItemIds.PlantFibres,
    2,
    700);
var deliveryPlan = VillagerCommitmentService.AddPromise(
    villagerSpawnA[0] with
    {
        Inventory = [ItemIds.PlantFibres]
    },
    deliveryAcceptance.Promise!);
deliveryPlan = VillagerPromisePlanService.CompileAiDirective(
    deliveryPlan,
    "gather",
    ItemIds.PlantFibres,
    2,
    "requester",
    null,
    null,
    (int)WorldLevel.Overworld,
    700,
    0);
Require(
    VillagerPromisePlanService.CurrentDirective(deliveryPlan) is null &&
    deliveryPlan.ActionPlan is
    [
        {
            PromiseId: var collectPromiseId,
            Action: VillagerPromisePlanAction.Collect,
            RemainingQuantity: 1
        },
        {
            PromiseId: var deliverPromiseId,
            Action: VillagerPromisePlanAction.Deliver,
            RemainingQuantity: 2
        }
    ] &&
    collectPromiseId == deliveryAcceptance.Promise!.Id &&
    deliverPromiseId == deliveryAcceptance.Promise.Id,
    "formal delivery promises must replace duplicate Ollama directives and account for matching items already carried");
var unrelatedGroundItemId = Guid.NewGuid();
var promisedGroundItemId = Guid.NewGuid();
var exactPromiseTarget = VillagerSimulation.SelectWorldAction(
    villagerSpawnA[0],
    [
        new(unrelatedGroundItemId, ItemIds.LargeRock, new Vector2(.5f, 0),
            null, false, null),
        new(promisedGroundItemId, ItemIds.PlantFibres, new Vector2(2, 0),
            null, false, null)
    ],
    700,
    ItemIds.PlantFibres);
Require(
    exactPromiseTarget.Kind is (VillagerWorldActionKind.TakeItem or
        VillagerWorldActionKind.ApproachItem) &&
    exactPromiseTarget.ObjectId == promisedGroundItemId,
    "committed collection must select the promised resource instead of a closer unrelated ground item");
var persistentSearchVillager = villagerSpawnA[0] with
{
    PositionX = 10,
    PositionY = 20,
    TargetX = 18,
    TargetY = 20,
    Action = EntityAction.Move
};
Require(
    VillagerSettlementProjectService.ContinuingExplorationTarget(
        persistentSearchVillager, 700) == new Vector2(18, 20) &&
    VillagerSettlementProjectService.ContinuingExplorationTarget(
        persistentSearchVillager with
        {
            PositionX = 18,
            PositionY = 20
        },
        700) != new Vector2(18, 20),
    "resource searches must finish a persistent outward exploration leg before choosing another direction");
var scheduledPromiseVillager =
    VillagerPromisePlanService.ScheduleRendezvous(
        committedVillager,
        "requester",
        12.5f,
        -4.5f,
        (int)WorldLevel.Overworld,
        300 + 60 * 60);
var scheduledPlans =
    VillagerPromisePlanService.PlansFor(scheduledPromiseVillager);
Require(
    scheduledPlans.Count == 2 &&
    scheduledPromiseVillager.ActionPlan?.Count == 2 &&
    scheduledPlans[0] is
    {
        Action: VillagerPromisePlanAction.Collect,
        ItemId: ItemIds.Logs,
        RemainingQuantity: 3
    } &&
    scheduledPlans[1] is
    {
        Action: VillagerPromisePlanAction.Rendezvous,
        TargetX: 12.5f,
        TargetY: -4.5f
    } &&
    VillagerIntentPriorityService.ShouldProtectCommittedWork(
        scheduledPromiseVillager) &&
    VillagerPromisePlanService.DueRendezvous(
        scheduledPromiseVillager, 3899) is null &&
    VillagerPromisePlanService.DueRendezvous(
        scheduledPromiseVillager, 3900) is not null,
    "accepted promises must become prioritized collect and timed rendezvous plan steps instead of remaining social narration");
Require(
    VillagerStatusService.CurrentThought(
        scheduledPromiseVillager with
        {
            LastDeliberation = new(
                "I am still talking.", "accept", "gather",
                90, 10, 5, 90, 300, ItemIds.Logs)
        },
        301).Contains("Collecting 3 logs") &&
    VillagerStatusService.CurrentThought(
        scheduledPromiseVillager, 3900).Contains("meeting place"),
    "observable NPC status must show the executable promise plan instead of stale social deliberation text");
var readyToReturn = VillagerCommitmentService.RecordAcquiredItem(
    scheduledPromiseVillager, ItemIds.OakLogs, 3);
var completedRendezvous =
    VillagerPromisePlanService.RecordRendezvousReached(
        readyToReturn, acceptance.Promise!.Id);
Require(
    readyToReturn.Promises?.Single().Progress == 3 &&
    readyToReturn.Promises.Single().Status == CommitmentStatus.Active &&
    readyToReturn.ActionPlan is
    [
        {
            Action: VillagerPromisePlanAction.Rendezvous,
            RemainingQuantity: 0
        }
    ] &&
    completedRendezvous.Promises?.Single().Status ==
        CommitmentStatus.Fulfilled &&
    completedRendezvous.ActionPlan?.Count == 0 &&
    !VillagerPromisePlanService.HasActiveWork(completedRendezvous),
    "interchangeable gathered resources must satisfy collection while a scheduled promise remains active until the NPC physically returns");
var promisePriorityInventory =
    PlayerInventory.CreateStartingInventory();
promisePriorityInventory[0] = ItemIds.Logs;
promisePriorityInventory[1] = ItemIds.OakLogs;
committedVillager = committedVillager with
{
    Inventory = promisePriorityInventory
};
var promisedGatherAction =
    VillagerSimulation.SelectWorldAction(
        committedVillager,
        new VillagerWorldObject[]
        {
            new(
                Guid.NewGuid(),
                ItemIds.Logs,
                new(2, 0),
                null,
                IsStorage: false),
            new(
                Guid.NewGuid(),
                ItemIds.SmallRocks,
                new(.25f, 0),
                null,
                IsStorage: false)
        });
Require(
    promisedGatherAction.ObjectId is not null &&
    promisedGatherAction.Target is not null,
    "accepted promises must override personal stock limits and drive real world actions");
committedVillager =
    VillagerCommitmentService.RecordAcquiredItem(
        committedVillager, ItemIds.Logs, 2);
Require(
    committedVillager.Promises?.Single().Progress == 2 &&
    committedVillager.Promises.Single().Status ==
        CommitmentStatus.Active &&
    committedVillager.ActionPlan?.Single() is
    {
        Action: VillagerPromisePlanAction.Collect,
        RemainingQuantity: 1
    },
    "promise progress must track actual acquired items and recompile the remaining controller work");
committedVillager =
    VillagerCommitmentService.RecordAcquiredItem(
        committedVillager, ItemIds.Logs);
Require(
    committedVillager.Promises?.Single().Status ==
        CommitmentStatus.Fulfilled &&
    committedVillager.ActionPlan?.Count == 0 &&
    VillagerCommitmentService.ApplyOutcome(
        default,
        CommitmentStatus.Fulfilled).Trust > 0,
    "fulfilling a promise must complete it and improve trust");
var brokenPromiseVillager =
    VillagerCommitmentService.UpdateDeadlines(
        VillagerCommitmentService.AddPromise(
            villagerSpawnA[1],
            acceptance.Promise! with
            {
                PromisorId = villagerSpawnA[1].Id,
                DeadlineGameSeconds = 10
            }),
        11);
Require(
    brokenPromiseVillager.Promises?.Single().Status ==
        CommitmentStatus.Broken &&
    brokenPromiseVillager.ActionPlan?.Count == 0 &&
    VillagerCommitmentService.ApplyOutcome(
        default,
        CommitmentStatus.Broken).Trust < 0,
    "expired promises must become broken commitments with negative social consequences");
var brokenPromisor = VillagerCommitmentService.AddPromise(
    villagerSpawnA[0],
    acceptance.Promise! with
    {
        PromisorId = villagerSpawnA[0].Id,
        PromiseeId = villagerSpawnA[1].Id,
        DeadlineGameSeconds = 10
    });
var brokenPromisee = villagerSpawnA[1];
(brokenPromisor, brokenPromisee) =
    VillagerCommitmentService.UpdateDeadlines(
        brokenPromisor, brokenPromisee, 11);
var brokenTrust = brokenPromisee.Relationships!.Single(value =>
    value.CharacterId == brokenPromisor.Id).State.Trust;
(brokenPromisor, brokenPromisee) =
    VillagerCommitmentService.UpdateDeadlines(
        brokenPromisor, brokenPromisee, 12);
Require(brokenPromisor.Promises!.Single().Status ==
            CommitmentStatus.Broken &&
        brokenPromisee.Memories!.Count(value =>
            value.Kind == "promise-broken" &&
            value.SubjectId == brokenPromisor.Id) == 1 &&
        brokenPromisee.Relationships!.Single(value =>
            value.CharacterId == brokenPromisor.Id).State.Trust == brokenTrust,
    "broken promises must reduce trust, create one memory, and apply consequences only once");
var favorAcceptance = VillagerCommitmentService.TryAccept(
    villagerSpawnA[0],
    villagerSpawnA[1].Id,
    VillagerPromiseKind.GiveItem,
    ItemIds.Sticks,
    quantity: 2,
    gameSeconds: 400);
var playerBerryAcceptance = VillagerCommitmentService.TryAccept(
    villagerSpawnA[0],
    "player-requester",
    VillagerPromiseKind.GiveItem,
    ItemIds.WildBerries,
    quantity: 1,
    gameSeconds: 400);
var berryGiverInventory = new string?[PlayerInventory.Capacity];
berryGiverInventory[0] = ItemIds.WildBerries;
var berryGiver = VillagerCommitmentService.AddPromise(
    villagerSpawnA[0] with { Inventory = berryGiverInventory },
    playerBerryAcceptance.Promise!);
var playerBerryInventory = new string?[PlayerInventory.Capacity];
Require(
    berryGiver.ActionPlan?.Single() is
    {
        Action: VillagerPromisePlanAction.Deliver,
        ItemId: ItemIds.WildBerries,
        RemainingQuantity: 1
    },
    "an accepted player request for an item already carried by the NPC must queue delivery, not collection");
var berryDelivered = VillagerCommitmentService
    .TryCompleteDeliveryToInventory(
        berryGiver,
        "player-requester",
        playerBerryInventory,
        playerBerryAcceptance.Promise!.Id,
        410,
        out var berryGiverAfterDelivery,
        out var playerAfterBerryDelivery);
Require(
    berryDelivered &&
    !berryGiverAfterDelivery.Inventory.Contains(ItemIds.WildBerries) &&
    playerAfterBerryDelivery.Count(value =>
        value == ItemIds.WildBerries) == 1 &&
    berryGiverAfterDelivery.Promises?.Single().Status ==
        CommitmentStatus.Fulfilled &&
    berryGiverAfterDelivery.ActionPlan?.Count == 0,
    "NPC-to-player delivery must atomically transfer the accepted item, fulfil the promise, and clear its controller queue");
var fullPlayerInventory = Enumerable.Repeat<string?>(
    ItemIds.Logs, PlayerInventory.Capacity).ToArray();
Require(
    !VillagerCommitmentService.TryCompleteDeliveryToInventory(
        berryGiver,
        "player-requester",
        fullPlayerInventory,
        playerBerryAcceptance.Promise.Id,
        410,
        out var blockedBerryGiver,
        out var blockedPlayerInventory) &&
    blockedBerryGiver.Inventory.Contains(ItemIds.WildBerries) &&
    blockedBerryGiver.Promises?.Single().Progress == 0 &&
    blockedPlayerInventory.All(value => value == ItemIds.Logs),
    "NPC-to-player delivery must remain atomic when the player's inventory is full");
var favorPromisor = VillagerCommitmentService.AddPromise(
    villagerSpawnA[0] with
    {
        Inventory = new string?[28]
        {
            ItemIds.Sticks, ItemIds.Sticks,
            null, null, null, null, null,
            null, null, null, null, null, null, null,
            null, null, null, null, null, null, null,
            null, null, null, null, null, null, null
        }
    }, favorAcceptance.Promise!);
var favorPromisee = villagerSpawnA[1];
var emptyDeliveryPromisor = VillagerCommitmentService.AddPromise(
    villagerSpawnA[0] with { Inventory = new string?[28] },
    favorAcceptance.Promise!);
var emptyDeliveryPromisee = villagerSpawnA[1];
(emptyDeliveryPromisor, emptyDeliveryPromisee) =
    VillagerCommitmentService.CompleteDelivery(
        emptyDeliveryPromisor,
        emptyDeliveryPromisee,
        favorAcceptance.Promise!.Id,
        405);
Require(emptyDeliveryPromisor.Promises!.Single().Progress == 0 &&
        !emptyDeliveryPromisee.Inventory.Contains(ItemIds.Sticks),
    "promise delivery must not progress when the promised item is absent");
var fullDeliveryPromisor = VillagerCommitmentService.AddPromise(
    villagerSpawnA[0] with
    {
        Inventory = new string?[28]
        {
            ItemIds.Sticks,
            null, null, null, null, null, null,
            null, null, null, null, null, null, null,
            null, null, null, null, null, null, null,
            null, null, null, null, null, null, null
        }
    },
    favorAcceptance.Promise!);
var fullDeliveryPromisee = villagerSpawnA[1] with
{
    Inventory = Enumerable.Repeat<string?>(ItemIds.Logs, 28).ToArray()
};
(fullDeliveryPromisor, fullDeliveryPromisee) =
    VillagerCommitmentService.CompleteDelivery(
        fullDeliveryPromisor,
        fullDeliveryPromisee,
        favorAcceptance.Promise!.Id,
        407);
Require(fullDeliveryPromisor.Promises!.Single().Progress == 0 &&
        fullDeliveryPromisor.Inventory.Count(value =>
            value == ItemIds.Sticks) == 1 &&
        fullDeliveryPromisee.Inventory.All(value => value == ItemIds.Logs) &&
        fullDeliveryPromisee.Relationships is null,
    "promise delivery must remain atomic when the promisee inventory has no space");
(favorPromisor, favorPromisee) =
    VillagerCommitmentService.CompleteDelivery(
        favorPromisor,
        favorPromisee,
        favorAcceptance.Promise!.Id,
        gameSeconds: 410);
Require(
    favorPromisor.Promises?.Single() is
        { Progress: 1, Status: CommitmentStatus.Active } &&
    favorPromisor.Inventory.Count(value => value == ItemIds.Sticks) == 1 &&
    favorPromisee.Inventory.Count(value => value == ItemIds.Sticks) == 1 &&
    favorPromisee.Relationships is null,
    "partial favor delivery must retain the promise without awarding completion gratitude early");
(favorPromisor, favorPromisee) =
    VillagerCommitmentService.CompleteDelivery(
        favorPromisor,
        favorPromisee,
        favorAcceptance.Promise!.Id,
        gameSeconds: 420);
Require(
    favorPromisor.Promises?.Single() is
        { Progress: 2, Status: CommitmentStatus.Fulfilled } &&
    !favorPromisor.Inventory.Contains(ItemIds.Sticks) &&
    favorPromisee.Inventory.Count(value => value == ItemIds.Sticks) == 2 &&
    favorPromisor.Memories?.Any(memory =>
        memory.Kind == "favor-delivered" &&
        memory.SubjectId == favorPromisee.Id) == true &&
    favorPromisee.Memories?.Any(memory =>
        memory.Kind == "favor-completed" &&
        memory.SubjectId == favorPromisor.Id) == true &&
    favorPromisee.Relationships?.Single(value =>
        value.CharacterId == favorPromisor.Id).State is
        { Trust: > 0, Respect: > 0, Gratitude: > 0 },
    "completed favors must persist delivery, fulfillment, gratitude, trust, and respect for both actors");
var commitmentBenchmark =
    System.Diagnostics.Stopwatch.StartNew();
for (var index = 0; index < 100_000; index++)
    _ = VillagerCommitmentService.UpdateDeadlines(
        committedVillager, 301);
commitmentBenchmark.Stop();
Require(
    commitmentBenchmark.ElapsedMilliseconds < 1000,
    "bounded promise maintenance must remain cheap enough for large distant populations");
var movementState = VillagerSimulation.ApplyDecision(
    villagerSpawnA[0] with
    {
        PositionX = 0,
        PositionY = 0
    },
    new(VillagerNeed.Explore, new(2, 0)),
    VillagerSimulationTier.Nearby,
    250);
Require(
    movementState.Action == EntityAction.Move &&
    movementState.FacingX == 1 &&
    movementState.FacingY == 0 &&
    movementState.PositionX == 0 &&
    movementState.TargetX == 2,
    "villager plans must persist action and facing for shared animation rendering");
var partiallyMovedVillager =
    VillagerSimulation.AdvanceMovement(
        movementState, .25f);
var arrivedVillager =
    VillagerSimulation.AdvanceMovement(
        partiallyMovedVillager, 1);
Require(
    partiallyMovedVillager.PositionX > 0 &&
    partiallyMovedVillager.PositionX < 1 &&
    Math.Abs(partiallyMovedVillager.ActionTime - .25) < .0001 &&
    MathF.Abs(
        partiallyMovedVillager.PositionX -
        ActorMovementService.BaseMoveSpeed * .25f) < .0001f &&
    partiallyMovedVillager.Action == EntityAction.Move &&
    arrivedVillager.PositionX == 2 &&
    arrivedVillager.Action == EntityAction.Idle &&
    arrivedVillager.ActionTime == 0 &&
    arrivedVillager.TargetX is null,
    "villagers must interpolate at a bounded speed and stop exactly at their destination");
var followingState = VillagerSimulation.RetargetFollowing(
    partiallyMovedVillager with
    {
        FollowingActorId = "player",
        ActionTime = 1.75
    },
    new(3, 1),
    251);
followingState = VillagerSimulation.RetargetFollowing(
    followingState,
    new(3.25f, 1.25f),
    252);
Require(
    followingState.Action == EntityAction.Move &&
    followingState.Activity == VillagerActivity.Following &&
    followingState.ActionTime == 1.75 &&
    followingState.TargetX == 3.25f &&
    followingState.TargetY == 1.25f,
    "retargeting a moving follower must preserve its walk-cycle time");
var stableFollowTarget = VillagerSimulation.FollowTarget(
    new(0, 0),
    new(4, 0));
var routedFollower = VillagerSimulation.RetargetFollowing(
    followingState,
    stableFollowTarget,
    253);
Require(
    MathF.Abs(
        Vector2.Distance(stableFollowTarget, new(4, 0)) -
        VillagerSimulation.FollowStopDistance) < .001f &&
    !VillagerSimulation.NeedsFollowRetarget(
        routedFollower,
        stableFollowTarget + new Vector2(.1f, 0)) &&
    VillagerSimulation.NeedsFollowRetarget(
        routedFollower,
        stableFollowTarget +
        new Vector2(
            VillagerSimulation.FollowRetargetDistance,
            0)),
    "followers must hold a personal-space target and avoid rebuilding nearly identical routes");
var dialogueExchange =
    VillagerSimulation.RecordSharedDialogueLine(
        villagerSpawnA[0],
        villagerSpawnA[1],
        "We should look for fresh water.",
        254);
dialogueExchange =
    VillagerSimulation.RecordSharedDialogueLine(
        dialogueExchange.Listener,
        dialogueExchange.Speaker,
        "Agreed. I'll search near the trees.",
        255);
Require(
    dialogueExchange.Speaker.ConversationHistory?.Count == 2 &&
    dialogueExchange.Listener.ConversationHistory?.Count == 2 &&
    dialogueExchange.Speaker.ConversationHistory
        .Select(turn => turn.Text)
        .SequenceEqual(
        [
            "We should look for fresh water.",
            "Agreed. I'll search near the trees."
        ]) &&
    dialogueExchange.Listener.ConversationHistory
        .Select(turn => turn.Text)
        .SequenceEqual(
        [
            "We should look for fresh water.",
            "Agreed. I'll search near the trees."
        ]),
    "both villagers must remember both sides of their conversation in order");
var giftItemId = Guid.NewGuid();
var giftedVillager = VillagerSimulation.RecordGift(
    villagerSpawnA[0],
    "player",
    "Samuel",
    giftItemId,
    ItemIds.StoneAxe,
    260);
Require(
    giftedVillager.Memories?.Any(memory =>
        memory.Kind == "gift-received" &&
        memory.SubjectId == "player" &&
        memory.ItemInstanceId == giftItemId) == true &&
    giftedVillager.Relationships?.Single(value =>
        value.CharacterId == "player").State.Trust > 0 &&
    giftedVillager.Relationships.Single(value =>
        value.CharacterId == "player").State.Affection > 0,
    "a gifted owned item must become a positive social memory");
var attackedVillager = VillagerSimulation.RecordAttack(
    giftedVillager,
    "player",
    "Samuel",
    7,
    261);
Require(
    attackedVillager.Health == giftedVillager.Health - 7 &&
    attackedVillager.Action == EntityAction.Idle &&
    attackedVillager.FollowingActorId is null &&
    attackedVillager.Need == VillagerNeed.Safe &&
    attackedVillager.Memories?.Any(memory =>
        memory.Kind == "violence" &&
        memory.SubjectId == "player" &&
        memory.Sentiment <= -20) == true &&
    attackedVillager.Relationships?.Single(value =>
        value.CharacterId == "player").State.Resentment > 0,
    "attacking a villager must damage them, stop following, and create a hostile memory");
var attackWitness =
    VillagerSimulation.RecordWitnessedAttack(
        villagerSpawnA[1],
        "player",
        "Samuel",
        attackedVillager.Id,
        attackedVillager.Name,
        262);
var defeatedVillager = VillagerSimulation.RecordAttack(
    attackedVillager,
    "player",
    "Samuel",
    10_000,
    263);
Require(
    attackWitness.Memories?.Any(memory =>
        memory.Kind == "witnessed-violence" &&
        memory.SubjectId == "player") == true &&
    attackWitness.Relationships?.Single(value =>
        value.CharacterId == "player").State.Resentment > 0 &&
    defeatedVillager.Health == 0 &&
    defeatedVillager.Action == EntityAction.Die &&
    defeatedVillager.DeathCause == "Killed by Samuel." &&
    VillagerSimulation.CatchUp(
        defeatedVillager, 10_000).Health == 0,
    "nearby villagers must remember witnessed violence and defeated villagers must remain permanently dead");
Require(
    !MeleeCombatService.ShouldRepathMovingTarget(
        1,
        2,
        new(4, 4),
        new(4.1f, 4.1f)) &&
    MeleeCombatService.ShouldRepathMovingTarget(
        2,
        2,
        new(4, 4),
        new(4.1f, 4.1f)) &&
    MeleeCombatService.ShouldRepathMovingTarget(
        1,
        2,
        new(4, 4),
        new(
            4 + MeleeCombatService.MovingTargetRepathDistance + .01f,
            4)),
    "moving combat targets must repath on a bounded timer or meaningful displacement");
Require(
    !MeleeCombatService.ShouldRequestMovingTargetPath(
        true, 10, 0, Vector2.Zero, Vector2.One) &&
    MeleeCombatService.ShouldRequestMovingTargetPath(
        false, 10, 0, Vector2.Zero, Vector2.One),
    "moving-target combat must not continuously cancel an unfinished path calculation");
var conversationState = VillagerSimulation.BeginConversation(
    movementState,
    "player",
    gameSeconds: 1_000,
    realSeconds: 4);
Require(
    conversationState.Activity == VillagerActivity.Conversing &&
    conversationState.ConversationPartnerId == "player" &&
    conversationState.TargetX is null &&
    conversationState.NextDecisionGameSeconds == 1_240,
    "conversation must be an explicit activity that cancels movement and postpones decisions");
var resumedConversationState = VillagerSimulation.ResumeAfterConversation(
    conversationState, 1_240);
Require(
    resumedConversationState.Activity == VillagerActivity.Idle &&
    resumedConversationState.ConversationPartnerId is null &&
    resumedConversationState.TargetX is null &&
    resumedConversationState.TargetY is null &&
    resumedConversationState.NextDecisionGameSeconds == 1_241,
    "a completed scripted conversation must release the villager into an immediately schedulable idle state");
var reflectionState = VillagerSimulation.CompleteConversation(
    conversationState, 1_240);
Require(
    reflectionState.Activity == VillagerActivity.Reflecting &&
    reflectionState.NextDecisionGameSeconds >
    conversationState.NextDecisionGameSeconds &&
    VillagerSimulation.CompleteReflection(
        reflectionState,
        reflectionState.ActivityUntilGameSeconds - 1).Activity ==
    VillagerActivity.Reflecting,
    "villagers must pause to orient after a conversation instead of immediately choosing a task");
var reflectedState = VillagerSimulation.CompleteReflection(
    reflectionState,
    reflectionState.ActivityUntilGameSeconds);
Require(
    reflectedState.Activity == VillagerActivity.Idle &&
    reflectedState.NextDecisionGameSeconds ==
    reflectionState.ActivityUntilGameSeconds,
    "reflection must release into a deliberate decision at its scheduled time");
var blockedTargetId = Guid.NewGuid();
var blockedState = VillagerSimulation.AdvanceMovement(
    movementState with { GoalObjectId = blockedTargetId },
    .25f,
    canOccupy: _ => false,
    gameSeconds: 2_000);
Require(
    blockedState.Activity == VillagerActivity.Blocked &&
    blockedState.Action == EntityAction.Idle &&
    blockedState.TargetX is null &&
    blockedState.BlockedMoveAttempts == 1 &&
    blockedState.NextDecisionGameSeconds > 2_000,
    "blocked movement must clear stale targets and schedule a bounded replan");
Require(
    blockedState.FailedTargets?.Single(value =>
        value.TargetId == blockedTargetId).RetryAfterGameSeconds ==
        2_000 + VillagerSimulation.FailedTargetRetryGameSeconds &&
    !VillagerSimulation.ShouldYieldThroughActor(1) &&
    VillagerSimulation.ShouldYieldThroughActor(2),
    "failed targets must remain blacklisted and repeatedly blocked actors must yield through each other");
Require(
    !VillagerSimulation.ShouldResolveActorCollision(
        blockedState, blockedState) &&
    VillagerSimulation.ShouldResolveActorCollision(
        movementState,
        movementState with { PositionX = movementState.PositionX + .1f }),
    "stationary villagers must not repeatedly block each other while moving villagers still resolve collisions");
Require(
    VillagerRelationshipClassifier.Classify(new(
        Trust: 24, Affection: 16)) == VillagerRelationshipKind.Friend &&
    VillagerRelationshipClassifier.Classify(new(
        Trust: 52, Affection: 41)) == VillagerRelationshipKind.CloseBond &&
    VillagerRelationshipClassifier.Classify(new(
        Trust: -18, Resentment: 24)) == VillagerRelationshipKind.Rival &&
    VillagerRelationshipClassifier.Classify(new(
        Trust: -20, Fear: 42, Resentment: 30)) ==
        VillagerRelationshipKind.FearedEnemy,
    "relationship values must map to stable friendship, bond, rivalry, and enemy classifications");
var classifiedRelationships = VillagerRelationshipClassifier.Summarize(
[
    new("friend", new(Trust: 25, Affection: 15)),
    new("bond", new(Trust: 50, Affection: 40)),
    new("rival", new(Trust: -20, Resentment: 25)),
    new("enemy", new(Trust: -40))
]);
Require(
    classifiedRelationships == new VillagerRelationshipSummary(1, 1, 1, 1),
    "relationship summaries must count each durable social state once");
Require(
    VillagerRelationshipClassifier.SocialPreferenceAdjustment(
        VillagerRelationshipKind.CloseBond) <
    VillagerRelationshipClassifier.SocialPreferenceAdjustment(
        VillagerRelationshipKind.Friend) &&
    VillagerRelationshipClassifier.SocialPreferenceAdjustment(
        VillagerRelationshipKind.Friend) < 0 &&
    VillagerRelationshipClassifier.SocialPreferenceAdjustment(
        VillagerRelationshipKind.Rival) > 0 &&
    VillagerRelationshipClassifier.SocialPreferenceAdjustment(
        VillagerRelationshipKind.FearedEnemy) >
    VillagerRelationshipClassifier.SocialPreferenceAdjustment(
        VillagerRelationshipKind.Enemy),
    "optional social choices must prefer close bonds and avoid increasingly dangerous relationships");
Require(
    !VillagerRelationshipClassifier.WillDefend(new(Trust: .5f)) &&
    VillagerRelationshipClassifier.WillDefend(new(
        Trust: 25, Affection: 15)) &&
    VillagerRelationshipClassifier.WillDefend(new(
        Trust: 8, Gratitude: 30)) &&
    VillagerRelationshipClassifier.WillDefend(new(
        Trust: 5, Respect: 22), subjectIsLeader: true),
    "casual acquaintances must stay out of fights while friends, rescuers, and respected leaders can receive aid");
Require(
    VillagerRelationshipClassifier.PromptDescription(new(
        Trust: 25, Affection: 15)) == "considers a friend" &&
    VillagerRelationshipClassifier.PromptDescription(new(
        Trust: -20, Resentment: 25)) == "considers a rival" &&
    VillagerRelationshipClassifier.PromptDescription(new(
        Trust: -20, Fear: 40, Resentment: 25)) == "fears as an enemy",
    "dialogue prompts must use the same classified bonds and hostilities as gameplay");
var devotedRelationship = new RelationshipState(
    Trust: 100, Affection: 100, Respect: 100, Gratitude: 100);
Require(
    VillagerRelationshipClassifier.Attraction(
        "adult-man", EntityGender.Male,
        "adult-woman", EntityGender.Female,
        devotedRelationship) == VillagerAttractionLevel.Devoted &&
    VillagerRelationshipClassifier.Attraction(
        "adult-man", EntityGender.Male,
        "adult-man-two", EntityGender.Male,
        devotedRelationship) == VillagerAttractionLevel.None &&
    VillagerRelationshipClassifier.Attraction(
        "adult-woman", EntityGender.Female,
        "adult-woman-two", EntityGender.Female,
        devotedRelationship) == VillagerAttractionLevel.None &&
    VillagerRelationshipClassifier.Attraction(
        "adult-man", EntityGender.Male,
        "hostile-woman", EntityGender.Female,
        devotedRelationship with { Resentment = 20 }) ==
        VillagerAttractionLevel.None,
    "romantic attraction must be adult opposite-sex only and suppressed by hostility");
Require(
    VillagerRelationshipClassifier.AttractionPreferenceAdjustment(
        VillagerAttractionLevel.Attracted) <
    VillagerRelationshipClassifier.AttractionPreferenceAdjustment(
        VillagerAttractionLevel.Interest) &&
    VillagerRelationshipClassifier.AttractionPreferenceAdjustment(
        VillagerAttractionLevel.Devoted) <
    VillagerRelationshipClassifier.AttractionPreferenceAdjustment(
        VillagerAttractionLevel.Attracted),
    "stronger romantic attraction must create a stronger optional social pull");
var enemyTransition = VillagerRelationshipClassifier.Transition(
    "villager", EntityGender.Female,
    "player", EntityGender.Male,
    default,
    new(Trust: -40, Resentment: 50));
Require(
    enemyTransition.PlayerMessage("Alice") ==
        "Alice now considers you an enemy." &&
    VillagerRelationshipClassifier.Transition(
        "villager", EntityGender.Female,
        "player", EntityGender.Female,
        new(Trust: 10, Affection: 8),
        new(Trust: 25, Affection: 15))
        .PlayerMessage("Alice") ==
        "Alice now considers you a friend.",
    "meaningful player relationship thresholds must create concise milestone feedback");
var movementProbeEntity = new WorldEntity(Vector2.Zero);
Require(
    movementProbeEntity.MoveSpeed ==
        ActorMovementService.BaseMoveSpeed &&
    ActorMovementService.TerrainSpeedMultiplier(
        wading: true, 0, 0) == .62f &&
    ActorMovementService.TerrainSpeedMultiplier(
        wading: false, 0, 2) < 1,
    "players and villagers must share base speed, wading slowdown, and uphill slowdown");
var observationSet =
    Enumerable.Range(0, 64)
        .Select(index => new VillagerWorldObject(
            Guid.NewGuid(),
            ItemIds.SmallRocks,
            new(index % 8, index / 8),
            null,
            IsStorage: false))
        .ToArray();
var plannerBenchmark =
    System.Diagnostics.Stopwatch.StartNew();
for (var index = 0; index < 10_000; index++)
    _ = VillagerSimulation.SelectWorldAction(
        villagerSpawnA[0], observationSet);
plannerBenchmark.Stop();
Require(
    plannerBenchmark.ElapsedMilliseconds < 1000,
    "villager resource scoring must remain fast across repeated active-region decisions");

var playerCommandHints = ChatCommandRegistry.Filter("/h", false);
var developerCommandHints = ChatCommandRegistry.Filter("/h", true);
Require(
    playerCommandHints.Select(command => command.Name)
        .SequenceEqual(["/help"]) &&
    developerCommandHints.Any(command => command.Name == "/heal") &&
    !playerCommandHints.Any(command => command.RequiresDeveloperMode) &&
    ChatCommandRegistry.TryParse(
        "/teleport 12.5 -8", out var parsedTeleport) &&
    parsedTeleport.Definition.RequiresDeveloperMode &&
    parsedTeleport.Arguments.SequenceEqual(["12.5", "-8"]),
    "chat command filtering must match prefixes, permissions, and arguments");
var commandDropdown = new CommandHintDropdownState();
commandDropdown.UpdateItems(
    ChatCommandRegistry.Filter("/", true),
    new Vector4(100, 500, 360, 38));
Require(
    commandDropdown.Visible &&
    commandDropdown.Bounds.Y < 500 &&
    commandDropdown.VisibleCount == 6 &&
    commandDropdown.CanScroll &&
    commandDropdown.FirstVisibleIndex == 0 &&
    commandDropdown.Scroll(new(110, 300), -1) &&
    commandDropdown.FirstVisibleIndex == 1 &&
    commandDropdown.ScrollThumbBounds.W > 0,
    "command hints must form a reusable scrollable dropdown above the chat input");
var gameGraphics = GameHostWindow.RequiredGraphicsFor(
    GameHostWindow.PreviewMode.Game);
Require(
    gameGraphics is not null &&
    gameGraphics.Contains("VMBAS_DN") &&
    gameGraphics.Contains("VFBAS_DN") &&
    gameGraphics.Contains("VMBAS_SN") &&
    gameGraphics.Contains("VFBAS_SN") &&
    gameGraphics.Contains("SHIPF5SF"),
    "game asset loading must include death, skeleton, and fishing boat sheets");

const long fishingBoatSeed = 67;
var fishingBoatOrigin = new Vector2(0, 0);
var fishingBoatSpawn = FishingBoatTravel.FindInitialPosition(
    fishingBoatSeed, fishingBoatOrigin);
var fishingBoatBiome = InfiniteWorldGenerator.BiomeAt(
    fishingBoatSeed,
    (int)MathF.Floor(fishingBoatSpawn.X),
    (int)MathF.Floor(fishingBoatSpawn.Y));
Vector2? adjacentWater = null;
Vector2? adjacentLand = null;
for (var y = -2; y <= 2; y++)
for (var x = -2; x <= 2; x++)
{
    if (x == 0 && y == 0) continue;
    var candidate = fishingBoatSpawn + new Vector2(x, y);
    var biome = InfiniteWorldGenerator.BiomeAt(
        fishingBoatSeed,
        (int)MathF.Floor(candidate.X),
        (int)MathF.Floor(candidate.Y));
    if (FishingBoatTravel.IsNavigable(biome))
        adjacentWater ??= candidate;
    else
        adjacentLand ??= candidate;
}
Require(
    fishingBoatBiome == Biome.ShallowWater &&
    adjacentWater is { } water &&
    FishingBoatTravel.FindPath(
        fishingBoatSeed, fishingBoatSpawn, water).Count > 0 &&
    adjacentLand is { } land &&
    FishingBoatTravel.CanDisembark(
        fishingBoatSeed, fishingBoatSpawn, land) &&
    FishingBoatTravel.FindDisembarkLanding(
        fishingBoatSeed,
        fishingBoatSpawn,
        land + new Vector2(8, 8)) is { } resolvedLanding &&
    FishingBoatTravel.CanDisembark(
        fishingBoatSeed, fishingBoatSpawn, resolvedLanding) &&
    !FishingBoatTravel.CanDisembark(
        fishingBoatSeed, fishingBoatSpawn, land + new Vector2(8, 8)),
    "fishing boats must spawn at shore, route through water, and resolve nearby shoreline landings");
var fishingBoatRider = new WorldEntity(
    fishingBoatSpawn, EntityGender.Male);
fishingBoatRider.FishAt(fishingBoatSpawn + Vector2.UnitX);
fishingBoatRider.Update(.5f);
var fishingActionTime = fishingBoatRider.ActionTime;
fishingBoatRider.SyncPosition(
    fishingBoatSpawn + new Vector2(.25f, .25f));
fishingBoatRider.AdvanceAction(.5f);
Require(
    fishingBoatRider.Action == EntityAction.Fish &&
    fishingBoatRider.ActionTime == fishingActionTime + .5f,
    "boat riders must preserve and advance an active fishing action");

var adventureAward = AdventureService.AwardFromAction(0, 400);
Require(
    adventureAward.Experience == 100 &&
    AdventureService.MaximumLevel == 100 &&
    AdventureService.LevelForExperience(
        AdventureService.ExperienceForLevel(100)) == 100 &&
    AdventureService.MaximumHealth(
        AdventureService.ExperienceForLevel(100)) == 298,
    "all-action Adventure progression must cap at level 100 and scale maximum health");
Require(
    SurvivalService.TryFoodEffect(
        ItemIds.FishBerryStew, out var stewEffect) &&
    SurvivalService.TryFoodEffect(
        ItemIds.CookedMinnows, out var minnowsEffect) &&
    stewEffect.HungerRestored > minnowsEffect.HungerRestored &&
    stewEffect.WellFedSeconds > minnowsEffect.WellFedSeconds,
    "better food must restore more hunger and slow hunger for longer");
var wellFedSurvival = SurvivalService.Advance(
    100, 60, 100, 60);
var normalSurvival = SurvivalService.Advance(
    100, 0, 100, 60);
Require(
    wellFedSurvival.Hunger > normalSurvival.Hunger &&
    wellFedSurvival.WellFedSeconds == 0,
    "well-fed time must slow hunger drain and expire deterministically");
var starvation = SurvivalService.Advance(0, 0, 100, 10);
Require(starvation.Health == 95,
    "empty hunger must cause deterministic starvation damage");
Require(
    PlayerDeathService.ApplyDamage(12, 5) == 7 &&
    PlayerDeathService.ApplyDamage(3, 8) == 0 &&
    PlayerDeathService.ApplyDamage(12, -4) == 12,
    "player damage must clamp at zero and ignore negative damage");
var recovery = PlayerDeathService.Recover(101);
Require(
    recovery.Health == 50 &&
    recovery.Hunger == PlayerDeathService.RecoveryHunger &&
    recovery.WellFedSeconds == 0,
    "defeat recovery must restore half health and minimum survival resources");
var meleeHit = MeleeCombatService.Roll(
    attackExperience: 0,
    strengthExperience: 0,
    hitRoll: 0,
    damageRoll: 0);
var meleeMiss = MeleeCombatService.Roll(
    attackExperience: 0,
    strengthExperience: 0,
    hitRoll: .99f,
    damageRoll: 0);
var knifeMeleeHit = MeleeCombatService.Roll(
    attackExperience: 0,
    strengthExperience: 0,
    hitRoll: 0,
    damageRoll: 0,
    inventory:
    [
        ItemIds.StoneKnife,
        ItemIds.IronKnife,
        ItemIds.IronKnife
    ]);
var nonKnifeWeaponHit = MeleeCombatService.Roll(
    attackExperience: 0,
    strengthExperience: 0,
    hitRoll: 0,
    damageRoll: 0,
    inventory: [ItemIds.IronAxe]);
var attackCooldowns = new EntityActionCooldowns();
Require(
    attackCooldowns.TryCommit(
        "attacker-a", EntityAction.Attack, 10, 2.4) &&
    !attackCooldowns.TryCommit(
        "attacker-a", EntityAction.Attack, 10.1, 2.4) &&
    !attackCooldowns.TryCommit(
        "attacker-a", EntityAction.Attack, 12.39, 2.4) &&
    attackCooldowns.TryCommit(
        "attacker-b", EntityAction.Attack, 10.1, 2.4) &&
    attackCooldowns.TryCommit(
        "attacker-a", EntityAction.Attack, 12.4, 2.4) &&
    attackCooldowns.TryCommit(
        "attacker-a", EntityAction.Attack, 100, 2.4) &&
    !attackCooldowns.TryCommit(
        "attacker-a", EntityAction.Attack, 100.01, 2.4) &&
    attackCooldowns.ReadyAt(
        "attacker-a", EntityAction.Attack) == 102.4,
    "melee cooldowns must be per attacker, survive movement or target changes, and never replay overdue impacts");
var sharedCombatCooldowns = new EntityActionCooldowns();
var firstSharedAttack = EntityInteractionService.TryMeleeAttack(
    sharedCombatCooldowns, "shared-a", 20,
    0, 0, 0, 0, 0);
var retargetedSharedAttack = EntityInteractionService.TryMeleeAttack(
    sharedCombatCooldowns, "shared-a", 20.1,
    0, 0, 0, 0, 0);
var otherEntitySharedAttack = EntityInteractionService.TryMeleeAttack(
    sharedCombatCooldowns, "shared-b", 20.1,
    0, 0, 0, 0, 0);
var recoveredSharedAttack = EntityInteractionService.TryMeleeAttack(
    sharedCombatCooldowns, "shared-a",
    20 + MeleeCombatService.AttackIntervalSeconds + .001,
    0, 0, 0, 0, 0);
Require(
    firstSharedAttack is { Succeeded: true, Attack.Hit: true } &&
    retargetedSharedAttack is
        { Succeeded: false, Failure: "attack_cooldown" } &&
    otherEntitySharedAttack.Succeeded &&
    recoveredSharedAttack.Succeeded,
    "the shared entity interaction layer must enforce combat cadence independently of controller and target");
var entityFeedback = new EntityFeedbackState();
entityFeedback.ShowImpact("tree:oak", 7, true, 50);
entityFeedback.ShowImpact("villager:ada", -3, false, 51);
Require(
    entityFeedback.HealthVisible("tree:oak", 52.99) &&
    !entityFeedback.HealthVisible("tree:oak", 53) &&
    entityFeedback.TryGet("tree:oak", out var treeFeedback) &&
    treeFeedback is { Damage: 7, Hit: true, ImpactAt: 50 } &&
    entityFeedback.TryGet("villager:ada", out var villagerFeedback) &&
    villagerFeedback is { Damage: 0, Hit: false } &&
    entityFeedback.LatestImpactTargetKey == "villager:ada",
    "all damageable entity types must share bounded health and impact presentation state");
Require(
    meleeHit is { Hit: true, Damage: 1, Experience: 4 } &&
    !meleeMiss.Hit &&
    knifeMeleeHit.Damage == 4 &&
    nonKnifeWeaponHit.Damage == meleeHit.Damage &&
    MeleeCombatService.KnifeDamageBonus(
        [ItemIds.StoneKnife, ItemIds.IronKnife, ItemIds.IronKnife]) == 3 &&
    MeleeCombatService.AttackIntervalSeconds == 2.4f,
    "melee must apply only one best-knife damage bonus and leave other carried tools unarmed");
Require(
    MeleeCombatService.ShouldAutoRetaliate(
        true, playerDefeated: false, hasCombatTarget: false) &&
    !MeleeCombatService.ShouldAutoRetaliate(
        false, playerDefeated: false, hasCombatTarget: false) &&
    !MeleeCombatService.ShouldAutoRetaliate(
        true, playerDefeated: true, hasCombatTarget: false) &&
    !MeleeCombatService.ShouldAutoRetaliate(
        true, playerDefeated: false, hasCombatTarget: true),
    "auto-retaliation must be optional and must not override death or an existing combat target");
Require(
    PlaceableObjectCatalog.TryGet(
        ItemIds.TrainingDummy, out var dummyDefinition) &&
    dummyDefinition.ChromaKeyMagenta &&
    dummyDefinition.GroundContactWidth == .45f &&
    dummyDefinition.GroundContactDepth == .3f &&
    dummyDefinition.GroundContactWidth <
        dummyDefinition.FootprintWidth &&
    ItemCatalog.Get(ItemIds.TrainingDummy)
        .HasTag(ItemTag.PlaceableObject),
    "the training dummy must remain a dev-bank placeable combat target");

var defaultDisplaySettings = new GameSettings();
Require(
    GameCursorFrames.MineAndPickUp == 3 &&
    GameCursorFrames.OpenStorage == 6 &&
    GameCursorFrames.CraftingStation == 7 &&
    GameCursorFrames.ClimbDown == 15 &&
    GameCursorFrames.ClimbUp == 16,
    "mining, storage, crafting stations, and cave traversal must retain their authored AoE cursor frames");
Require(defaultDisplaySettings.VSyncMode ==
            DisplayVSyncMode.Adaptive &&
        defaultDisplaySettings.FrameRateLimit == 0 &&
        !defaultDisplaySettings.UseTestAssets &&
        defaultDisplaySettings.AutoRetaliate,
    "display settings must default to adaptive VSync and unlimited FPS");
var cycledDisplaySettings =
    DisplaySettingsController.CycleVSync(defaultDisplaySettings);
Require(cycledDisplaySettings.VSyncMode == DisplayVSyncMode.Off &&
        DisplaySettingsController.CycleVSync(
            DisplaySettingsController.CycleVSync(
                cycledDisplaySettings)).VSyncMode ==
        DisplayVSyncMode.Adaptive,
    "VSync settings must cycle through adaptive, off, and on");
var frameLimitedSettings =
    DisplaySettingsController.CycleFrameRateLimit(
        defaultDisplaySettings);
Require(frameLimitedSettings.FrameRateLimit == 60 &&
        DisplaySettingsController.FrameRateLabel(0) == "Unlimited" &&
        DisplaySettingsController.FrameRateLabel(144) == "144 FPS" &&
        DisplaySettingsController.SimulationUpdatesPerSecond == 60 &&
        DisplaySettingsController.GameLoopFrequency(
            defaultDisplaySettings) == 0 &&
        DisplaySettingsController.GameLoopFrequency(
            defaultDisplaySettings with { FrameRateLimit = 144 }) == 144 &&
        DisplaySettingsController.GameLoopFrequency(
            defaultDisplaySettings with { FrameRateLimit = 90 }) == 0,
    "frame limits must cycle from unlimited through supported FPS presets");

var overworldCacheChunk = new ChunkCoordinate(4, -2, (int)WorldLevel.Overworld);
var undergroundCacheChunk = new ChunkCoordinate(4, -2, (int)WorldLevel.Underground);
var activeCacheCenter = new ChunkCoordinate(4, -2, (int)WorldLevel.Overworld);
Require(
    !WorldChunkCachePolicy.IsOutsideRetentionRadius(
        overworldCacheChunk, activeCacheCenter, 3) &&
    !WorldChunkCachePolicy.IsOutsideRetentionRadius(
        undergroundCacheChunk, activeCacheCenter, 3),
    "nearby chunks from both levels must remain cached across level transitions");
Require(
    WorldChunkCachePolicy.IsActiveLevel(
        overworldCacheChunk, (int)WorldLevel.Overworld) &&
    !WorldChunkCachePolicy.IsActiveLevel(
        undergroundCacheChunk, (int)WorldLevel.Overworld),
    "CPU world queries must reject cached chunks from inactive levels");
Require(
    WorldChunkCachePolicy.IsOutsideRetentionRadius(
        new ChunkCoordinate(8, -2, (int)WorldLevel.Underground),
        activeCacheCenter,
        3),
    "inactive-level chunk caching must remain spatially bounded");

var metrics = new PerformanceMetricsOverlay();
metrics.RecordFrame(1d / 60);
metrics.RecordFrame(1d / 30);
var metricSnapshot = metrics.Snapshot();
Require(metricSnapshot.FrameMilliseconds.Count == 2 &&
        Math.Abs(metricSnapshot.CurrentFrameMilliseconds -
                 (1000d / 30)) < .01 &&
        Math.Abs(metricSnapshot.AverageFrameMilliseconds - 25) < .01 &&
        Math.Abs(metricSnapshot.FramesPerSecond - 40) < .01,
    "performance metrics must report ordered FPS and frame-time history");

var hoverGate = new WorldHoverProbeGate();
var hoverProbeCount = 0;
for (var frame = 0; frame < 1_000; frame++)
    if (hoverGate.ShouldProbe(
            new(320, 180), new(20, -10), .8f,
            blocked: false, nowSeconds: 0))
        hoverProbeCount++;
Require(hoverProbeCount == 1 &&
        hoverGate.ShouldProbe(
            new(321, 180), new(20, -10), .8f,
            blocked: false, nowSeconds: .01) &&
        hoverGate.ShouldProbe(
            new(321, 180), new(20, -10), .8f,
            blocked: false, nowSeconds: .12),
    "stationary cursor probing must be skipped until input changes or expires");
var movingCameraProbeCount = 0;
hoverGate.Invalidate();
for (var frame = 0; frame < 240; frame++)
    if (hoverGate.ShouldProbe(
            new(321, 180),
            new(20 + frame, -10),
            .8f,
            blocked: false,
            nowSeconds: frame / 240d))
        movingCameraProbeCount++;
Require(
    movingCameraProbeCount is >= 9 and <= 11,
    "following a moving player must throttle stationary-cursor world probes");
var miningItems = new[]
{
    ItemIds.Coal, ItemIds.TinOre, ItemIds.CopperOre, ItemIds.IronOre
};
Require(miningItems.All(id =>
        ItemCatalog.Get(id).HasTag(ItemTag.MiningMaterial) &&
        ItemCatalog.Get(id).HasTag(ItemTag.MiningSprite)) &&
    miningItems.Select(id => ItemCatalog.Get(id).SpriteCell)
        .SequenceEqual(new int?[] { 0, 1, 2, 3 }),
    "mining rewards must use the four generated mining item cells");
Require(PlayerInventory.BestPickaxe(
            [ItemIds.StoneAxe, ItemIds.StonePickaxe])?.Id ==
        ItemIds.StonePickaxe,
    "mining must select a tagged pickaxe instead of another tool");
Require(
    PlayerInventory.BestPickaxe(
        [ItemIds.StonePickaxe, ItemIds.BronzePickaxe,
         ItemIds.IronPickaxe])?.Id == ItemIds.IronPickaxe &&
    ItemCatalog.Get(ItemIds.BronzePickaxe).MiningPower == 2 &&
    ItemCatalog.Get(ItemIds.IronPickaxe).MiningPower == 3 &&
    CraftingSkill.Recipes.Any(recipe =>
        recipe.ResultItemId == ItemIds.Bloomery &&
        recipe.RequiredStationItemId == ItemIds.Workbench) &&
    CraftingSkill.Recipes.Any(recipe =>
        recipe.ResultItemId == ItemIds.BronzeBar &&
        recipe.RequiredStationItemId == ItemIds.Bloomery &&
        recipe.Ingredients.Any(ingredient =>
            ingredient.ItemId == ItemIds.TinOre)) &&
    CraftingSkill.Recipes.Any(recipe =>
        recipe.ResultItemId == ItemIds.SmithingAnvil &&
        recipe.RequiredStationItemId == ItemIds.Workbench) &&
    CraftingSkill.Recipes.Any(recipe =>
        recipe.ResultItemId == ItemIds.IronBloom &&
        recipe.RequiredStationItemId == ItemIds.Bloomery &&
        recipe.Ingredients.Any(ingredient =>
            ingredient.ItemId == ItemIds.Coal)) &&
    CraftingSkill.Recipes.Any(recipe =>
        recipe.ResultItemId == ItemIds.IronBar &&
        recipe.RequiredStationItemId == ItemIds.SmithingAnvil &&
        recipe.Ingredients.Any(ingredient =>
            ingredient.ItemId == ItemIds.IronBloom)) &&
    CraftingSkill.Recipes.Any(recipe =>
        recipe.ResultItemId == ItemIds.BronzePickaxe &&
        recipe.Ingredients.Any(ingredient =>
            ingredient.ItemId == ItemIds.BronzeBar)) &&
    CraftingSkill.Recipes.Any(recipe =>
        recipe.ResultItemId == ItemIds.IronPickaxe &&
        recipe.Ingredients.Any(ingredient =>
            ingredient.ItemId == ItemIds.IronBar)) &&
    CraftingSkill.Recipes.Any(recipe =>
        recipe.ResultItemId == ItemIds.IronAxe &&
        recipe.Ingredients.Any(ingredient =>
            ingredient.ItemId == ItemIds.IronBar)),
    "metalworking stations must be built at a workbench before processing ores and forging tools");
Require(
    new[] { ItemIds.BronzeBar, ItemIds.IronBloom, ItemIds.IronBar }
        .Select(ItemCatalog.Get)
        .All(item => item.HasTag(ItemTag.MetalMaterialSprite)) &&
    new[] { ItemIds.BronzeBar, ItemIds.IronBloom, ItemIds.IronBar }
        .Select(id => ItemCatalog.Get(id).SpriteCell)
        .SequenceEqual(new int?[] { 0, 1, 2 }),
    "metalworking intermediates must use the generated material sprite sheet");
var noviceMiningStrike = MiningSkill.Roll(0, 0, 0, 1);
Require(noviceMiningStrike.Hit && noviceMiningStrike.Damage > 0 &&
        !MiningSkill.Roll(0, .99f, 0, 1).Hit &&
        MiningSkill.HitChance(20) > MiningSkill.HitChance(1),
    "mining strikes must scale and retain a miss chance");
var woodcutInteraction = new EntityResourceInteraction(
    EntityResourceAction.Woodcut, 0, 100, 100, 1, 0, 0);
var playerWoodcutStrike = EntityInteractionService.StrikeResource(
    woodcutInteraction);
var npcWoodcutStrike = EntityInteractionService.StrikeResource(
    woodcutInteraction);
var npcWoodcutMiss = EntityInteractionService.StrikeResource(
    woodcutInteraction with { AccuracyRoll = 1 });
Require(
    playerWoodcutStrike == npcWoodcutStrike &&
    npcWoodcutStrike.Hit && npcWoodcutStrike.Damage > 0 &&
    npcWoodcutStrike.Health == 100 - npcWoodcutStrike.Damage &&
    npcWoodcutStrike.Experience.Gained == npcWoodcutStrike.Damage &&
    !npcWoodcutMiss.Hit && npcWoodcutMiss.Damage == 0 &&
    npcWoodcutMiss.Health == 100 &&
    npcWoodcutMiss.Experience.Gained == 0,
    "player and NPC woodcutting must use the same atomic damage and skill outcome");
var miningInteraction = new EntityResourceInteraction(
    EntityResourceAction.Mine, 0, 1, 1, 1, 0, 0, 25);
var playerMiningFinish = EntityInteractionService.StrikeResource(
    miningInteraction);
var npcMiningFinish = EntityInteractionService.StrikeResource(
    miningInteraction);
Require(
    playerMiningFinish == npcMiningFinish &&
    npcMiningFinish.Hit && npcMiningFinish.Depleted &&
    npcMiningFinish.Damage == 1 && npcMiningFinish.Health == 0 &&
    npcMiningFinish.Experience.Gained == 26,
    "player and NPC mining must clamp damage and award identical completion XP");
var sharedMeleeInventory = PlayerInventory.CreateStartingInventory();
sharedMeleeInventory[0] = ItemIds.StoneKnife;
var playerMeleeInteraction = EntityInteractionService.MeleeAttack(
    250, 500, 250, 0, .5f, sharedMeleeInventory);
var npcMeleeInteraction = EntityInteractionService.MeleeAttack(
    250, 500, 250, 0, .5f, sharedMeleeInventory);
var missedMeleeInteraction = EntityInteractionService.MeleeAttack(
    250, 500, 250, 1, .5f, sharedMeleeInventory);
Require(
    playerMeleeInteraction == npcMeleeInteraction &&
    playerMeleeInteraction.Attack.Hit &&
    playerMeleeInteraction.Attack.Damage > 1 &&
    playerMeleeInteraction.Experience.Gained ==
    playerMeleeInteraction.Attack.Experience &&
    !missedMeleeInteraction.Attack.Hit &&
    missedMeleeInteraction.Experience.Gained == 0,
    "player and NPC melee must share hit, weapon damage, and progression outcomes");
var actionMemoryVillager = new VillagerState(
    "skill-memory", "Rin", EntityGender.Female, 0, 0,
    0, 0, 0, 100, 100, PlayerInventory.CreateStartingInventory());
actionMemoryVillager = VillagerActionMemoryService.RecordResourceStrike(
    actionMemoryVillager, "mining", "coal:1", "coal seam",
    npcMiningFinish, 120);
var firstSkillMemory = actionMemoryVillager.Memories!.Single(memory =>
    memory.Kind == VillagerActionMemoryService.SkillActionKind);
actionMemoryVillager = VillagerActionMemoryService.RecordResourceStrike(
    actionMemoryVillager, "mining", "coal:1", "coal seam",
    ResourceStrikeService.Mine(26, 10, 1, 25, 1, 0), 180);
var refreshedSkillMemory = actionMemoryVillager.Memories!.Single(memory =>
    memory.Kind == VillagerActionMemoryService.SkillActionKind);
Require(
    refreshedSkillMemory.EventId == firstSkillMemory.EventId &&
    refreshedSkillMemory.GameSeconds == 180 &&
    refreshedSkillMemory.Summary!.Contains("missed") &&
    refreshedSkillMemory.ObservedValue == 26,
    "NPC skill feedback must persist in bounded memory and consolidate repeated actions");
var inspectedVillager = actionMemoryVillager with
{
    WoodcuttingExperience = 125,
    FarmingExperience = 250,
    CraftingExperience = 500,
    MiningExperience = 1_000,
    AttackExperience = 2_000,
    StrengthExperience = 3_000,
    DefenceExperience = 4_000
};
Require(
    VillagerSkillService.Experience(
        inspectedVillager, SkillType.Woodcutting) == 125 &&
    VillagerSkillService.Experience(
        inspectedVillager, SkillType.Farming) == 250 &&
    VillagerSkillService.Experience(
        inspectedVillager, SkillType.Crafting) == 500 &&
    VillagerSkillService.Experience(
        inspectedVillager, SkillType.Mining) == 1_000 &&
    VillagerSkillService.Experience(
        inspectedVillager, SkillType.Attack) == 2_000 &&
    VillagerSkillService.Experience(
        inspectedVillager, SkillType.Strength) == 3_000 &&
    VillagerSkillService.Experience(
        inspectedVillager, SkillType.Defence) == 4_000 &&
    VillagerSkillService.Level(
        inspectedVillager, SkillType.Mining) ==
        SkillService.LevelForExperience(1_000),
    "observe inspection must read each NPC's own persisted skill XP and level");
Require(
    MiningNodeCatalog.TryGet(
        new(0, 0, UndergroundResourceGenerator.Coal, 0,
            WorldVegetationKind.Plant, false),
        out var coalNode) &&
    coalNode.RewardItemId == ItemIds.Coal &&
    MiningNodeCatalog.TryGet(
        new(0, 0, "ROCKF3_NN", 0,
            WorldVegetationKind.Plant, false),
        out var staticNode) &&
    staticNode.RewardItemId is null &&
    staticNode.CompletionExperience > coalNode.CompletionExperience,
    "ore nodes must reward items while large formations reward XP only");
var miningHitPixels = new byte[32 * 32 * 4];
miningHitPixels[(16 * 32 + 16) * 4 + 3] = 255;
var miningHitFrame = new SpriteFrame(32, 32, 16, 28, miningHitPixels);
Require(
    SpriteHitTesting.Contains(
        miningHitFrame, (0, 0, 32, 32), new(20, 16), 1, 4) &&
    !SpriteHitTesting.Contains(
        miningHitFrame, (0, 0, 32, 32), new(25, 16), 1, 4),
    "mining sprite selection must allow size-aware edge tolerance without selecting distant empty space");
Require(
    ItemCatalog.Get(ItemIds.WildBerries) is var wildBerries &&
    wildBerries.HasTag(ItemTag.Berry) &&
    wildBerries.HasTag(ItemTag.BerrySprite) &&
    wildBerries.SpriteCell == 0 &&
    ItemCatalog.Get(ItemIds.TropicalBerries) is var tropicalBerries &&
    tropicalBerries.HasTag(ItemTag.Berry) &&
    tropicalBerries.HasTag(ItemTag.BerrySprite) &&
    tropicalBerries.SpriteCell == 1,
    "both forage bush families must have dedicated generated berry rewards");
var berryFarmingAward = FarmingSkill.AwardExperience(0, 36);
Require(
    berryFarmingAward.Experience == 36 &&
    berryFarmingAward.Gained == 36,
    "berry harvesting XP must use the shared Farming progression");

var questProgress = QuestService.Normalize(null);
Require(
    questProgress[0].Status == QuestStatus.InProgress &&
    questProgress.Skip(1).All(value => value.Status == QuestStatus.Locked),
    "a new character must begin the first quest with later quests locked");
var questExperience = 0;
foreach (var questEvent in new[]
         {
             new QuestEvent(QuestEventType.GatherItem, ItemIds.LargeRock, 5),
             new QuestEvent(QuestEventType.GatherItem, ItemIds.Sticks, 2),
             new QuestEvent(QuestEventType.GatherItem, ItemIds.PlantFibres, 2)
         })
{
    var update = QuestService.Apply(
        questProgress, questExperience, questEvent);
    questProgress = update.Progress;
    questExperience = update.AdventureExperience;
}
Require(
    questProgress[0].Status == QuestStatus.Complete &&
    questProgress[1].Status == QuestStatus.InProgress &&
    QuestService.Definitions[1].Id == "tools-of-survival" &&
    QuestService.Definitions[2].Id == "first-light" &&
    QuestService.ActiveQuest(questProgress)?.Definition.Id ==
        "tools-of-survival" &&
    questExperience == 50,
    "shoreline gathering must unlock tools before the fuel-dependent fire quest");
var duplicateQuestUpdate = QuestService.Apply(
    questProgress,
    questExperience,
    new QuestEvent(QuestEventType.GatherItem, ItemIds.Sticks));
Require(
    duplicateQuestUpdate.AdventureExperience == questExperience,
    "completed quest events must not award Adventure XP twice");
var inventoryCraftProgress = questProgress;
var inventoryCraftExperience = questExperience;
var reconciledInventoryEvents = QuestService.InventoryProgressEvents(
    questProgress,
    [ItemIds.MediumRock, ItemIds.MediumRock, ItemIds.StoneKnife]);
Require(
    reconciledInventoryEvents.Any(value =>
        value.TargetId == ItemIds.MediumRock && value.Amount == 2) &&
    reconciledInventoryEvents.Any(value =>
        value.TargetId == ItemIds.StoneKnife && value.Amount == 1),
    "active inventory objectives must reconcile held result quantities whenever inventory state changes");
foreach (var questEvent in new[]
         {
             new QuestEvent(QuestEventType.CraftItem, ItemIds.MediumRock, 8),
             new QuestEvent(QuestEventType.CraftItem, ItemIds.SharpenedRock, 2),
             new QuestEvent(QuestEventType.CraftItem, ItemIds.StoneKnife),
             new QuestEvent(QuestEventType.CraftItem, ItemIds.StoneAxe),
             new QuestEvent(QuestEventType.CraftItem, ItemIds.SmallRocks, 4)
         })
{
    var update = QuestService.Apply(
        inventoryCraftProgress, inventoryCraftExperience, questEvent);
    inventoryCraftProgress = update.Progress;
    inventoryCraftExperience = update.AdventureExperience;
}
Require(
    inventoryCraftProgress[1].Status == QuestStatus.Complete &&
    inventoryCraftProgress[2].Status == QuestStatus.InProgress &&
    inventoryCraftExperience == 250,
    "successful direct item-use crafts must satisfy tool objectives and unlock the fire quest");
Require(
    PlayerInventory.AddedCount(
        [ItemIds.LargeRock, ItemIds.LargeRock, ItemIds.MediumRock],
        [ItemIds.LargeRock, ItemIds.MediumRock, ItemIds.MediumRock,
            ItemIds.MediumRock],
        ItemIds.MediumRock) == 2 &&
    PlayerInventory.AddedCount(
        [ItemIds.MediumRock, ItemIds.MediumRock],
        [ItemIds.MediumRock, ItemIds.SmallRocks, ItemIds.SmallRocks],
        ItemIds.SmallRocks) == 2,
    "quest craft quantities must use positive result-item inventory deltas across conversions");

Console.WriteLine(
    "World-hover probe benchmark (1,000 stationary updates): " +
    $"legacy 1,000 scans, gated {hoverProbeCount} scan; " +
    $"moving camera {movingCameraProbeCount} scans/second.");
var selectedHoverDepth = float.NegativeInfinity;
Require(
    WorldHoverSelection.Prefer(10, ref selectedHoverDepth) &&
    !WorldHoverSelection.Prefer(9, ref selectedHoverDepth) &&
    WorldHoverSelection.Prefer(11, ref selectedHoverDepth) &&
    selectedHoverDepth == 11,
    "allocation-free hover traversal must retain the frontmost candidate");

const long terrainBenchmarkSeed = 974_321;
const int terrainBenchmarkTiles = 8;
const int terrainBenchmarkStride = terrainBenchmarkTiles + 1;
var terrainHeightGrid = new float[
    terrainBenchmarkStride * terrainBenchmarkStride];
for (var y = 0; y <= terrainBenchmarkTiles; y++)
for (var x = 0; x <= terrainBenchmarkTiles; x++)
    terrainHeightGrid[y * terrainBenchmarkStride + x] =
        InfiniteWorldGenerator.SampleRenderedHeight(
            terrainBenchmarkSeed, x, y);
var terrainSamples = Enumerable.Range(0, 1_024)
    .Select(index => new Vector2(
        ((index * 37) % 790) / 100f,
        ((index * 61) % 790) / 100f))
    .ToArray();
var directTerrainTimer = System.Diagnostics.Stopwatch.StartNew();
var directTerrainTotal = 0f;
foreach (var sample in terrainSamples)
    directTerrainTotal += InfiniteWorldGenerator.SampleRenderedHeight(
        terrainBenchmarkSeed, sample.X, sample.Y);
directTerrainTimer.Stop();
var loadedTerrainTimer = System.Diagnostics.Stopwatch.StartNew();
var loadedTerrainTotal = 0f;
foreach (var sample in terrainSamples)
{
    var tileX = (int)MathF.Floor(sample.X);
    var tileY = (int)MathF.Floor(sample.Y);
    loadedTerrainTotal += LoadedTerrainSampler.Interpolate(
        terrainHeightGrid,
        terrainBenchmarkStride,
        tileX,
        tileY,
        sample.X - tileX,
        sample.Y - tileY);
}
loadedTerrainTimer.Stop();
Require(MathF.Abs(directTerrainTotal - loadedTerrainTotal) < .01f &&
        loadedTerrainTimer.ElapsedTicks <
        directTerrainTimer.ElapsedTicks,
    "loaded terrain sampling must match procedural heights and run faster");
Console.WriteLine(
    $"Terrain sampling benchmark ({terrainSamples.Length:N0} positions): " +
    $"procedural {directTerrainTimer.Elapsed.TotalMilliseconds:N1} ms, " +
    $"loaded {loadedTerrainTimer.Elapsed.TotalMilliseconds:N1} ms.");

var renderItems = Enumerable.Range(0, 8_192)
    .Select(index => new WorldRenderItem(
        new(
            ((index * 7919) % 997) / 7f,
            ((index * 3571) % 991) / 5f),
        1,
        $"item:{(index * 104729) % 8191:D4}",
        $"atlas:{index % 31}"))
    .ToArray();
var expectedRenderOrder = WorldRenderQueue.LegacyOrder(renderItems);
var reusableRenderQueue = new WorldRenderQueue();
reusableRenderQueue.Reset(renderItems.Length);
reusableRenderQueue.GroundOutlineVertices.AddRange(
    Enumerable.Repeat(1f, renderItems.Length * 30));
var outlineCapacity =
    reusableRenderQueue.GroundOutlineVertices.Capacity;
reusableRenderQueue.Reset(renderItems.Length);
Require(
    reusableRenderQueue.GroundOutlineVertices.Count == 0 &&
    reusableRenderQueue.GroundOutlineVertices.Capacity == outlineCapacity,
    "ground-item outline vertices must be cleared and reused without reallocating");
foreach (var item in renderItems)
    reusableRenderQueue.AddObject(
        item.World, item.Opacity, item.StableKey, item.AtlasKey);
reusableRenderQueue.Sort();
Require(expectedRenderOrder.Zip(
            reusableRenderQueue.Objects,
            (expected, actual) =>
                expected.World == actual.World &&
                expected.Opacity == actual.Opacity &&
                expected.StableKey == actual.StableKey &&
                expected.AtlasKey == actual.AtlasKey)
        .All(matches => matches),
    "the reusable render queue must preserve the legacy isometric depth order");
const int renderBenchmarkIterations = 32;
_ = WorldRenderQueue.LegacyOrder(renderItems);
reusableRenderQueue.Reset(renderItems.Length);
foreach (var item in renderItems)
    reusableRenderQueue.AddObject(
        item.World, item.Opacity, item.StableKey, item.AtlasKey);
reusableRenderQueue.Sort();
GC.Collect();
GC.WaitForPendingFinalizers();
var legacyAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
var legacyTimer = System.Diagnostics.Stopwatch.StartNew();
for (var iteration = 0;
     iteration < renderBenchmarkIterations;
     iteration++)
    _ = WorldRenderQueue.LegacyOrder(renderItems);
legacyTimer.Stop();
var legacyAllocated =
    GC.GetAllocatedBytesForCurrentThread() - legacyAllocatedBefore;
GC.Collect();
GC.WaitForPendingFinalizers();
var optimizedAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
var optimizedTimer = System.Diagnostics.Stopwatch.StartNew();
for (var iteration = 0;
     iteration < renderBenchmarkIterations;
     iteration++)
{
    reusableRenderQueue.Reset(renderItems.Length);
    foreach (var item in renderItems)
        reusableRenderQueue.AddObject(
            item.World, item.Opacity, item.StableKey, item.AtlasKey);
    reusableRenderQueue.Sort();
}
optimizedTimer.Stop();
var optimizedAllocated =
    GC.GetAllocatedBytesForCurrentThread() - optimizedAllocatedBefore;
Require(optimizedAllocated * 4 < legacyAllocated,
    "the reusable render queue must remove most legacy managed allocations");
Console.WriteLine(
    "Render queue benchmark " +
    $"({renderItems.Length:N0} items x {renderBenchmarkIterations}): " +
    $"legacy {legacyTimer.Elapsed.TotalMilliseconds:N1} ms / " +
    $"{legacyAllocated:N0} B, reusable " +
    $"{optimizedTimer.Elapsed.TotalMilliseconds:N1} ms / " +
    $"{optimizedAllocated:N0} B.");
var vertexSource = Enumerable.Range(0, 196_608)
    .Select(index => index / 17f)
    .ToList();
const int vertexBenchmarkIterations = 48;
_ = vertexSource.ToArray();
_ = reusableRenderQueue.CopyVertices(vertexSource);
GC.Collect();
GC.WaitForPendingFinalizers();
var legacyVertexAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
var legacyVertexTimer = System.Diagnostics.Stopwatch.StartNew();
float[] legacyVertexUpload = [];
for (var iteration = 0;
     iteration < vertexBenchmarkIterations;
     iteration++)
    legacyVertexUpload = vertexSource.ToArray();
legacyVertexTimer.Stop();
var legacyVertexAllocated =
    GC.GetAllocatedBytesForCurrentThread() - legacyVertexAllocatedBefore;
GC.Collect();
GC.WaitForPendingFinalizers();
var reusableVertexAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
var reusableVertexTimer = System.Diagnostics.Stopwatch.StartNew();
float[] reusableVertexUpload = [];
for (var iteration = 0;
     iteration < vertexBenchmarkIterations;
     iteration++)
    reusableVertexUpload =
        reusableRenderQueue.CopyVertices(vertexSource);
reusableVertexTimer.Stop();
var reusableVertexAllocated =
    GC.GetAllocatedBytesForCurrentThread() - reusableVertexAllocatedBefore;
Require(legacyVertexUpload[12345] == reusableVertexUpload[12345] &&
        reusableVertexAllocated * 100 < legacyVertexAllocated,
    "reusable vertex staging must preserve data while eliminating upload arrays");
Console.WriteLine(
    "Vertex staging benchmark " +
    $"({vertexSource.Count:N0} floats x {vertexBenchmarkIterations}): " +
    $"legacy {legacyVertexTimer.Elapsed.TotalMilliseconds:N1} ms / " +
    $"{legacyVertexAllocated:N0} B, reusable " +
    $"{reusableVertexTimer.Elapsed.TotalMilliseconds:N1} ms / " +
    $"{reusableVertexAllocated:N0} B.");

Require(FarmingSkill.LevelForExperience(0) == 1 &&
        FarmingSkill.LevelForExperience(
            FarmingSkill.ExperienceForLevel(20)) == 20,
    "farming must use the complete 20-level progression");
Require(FarmingSkill.PlantingExperience > 0,
    "planting a seed must award farming experience");
Require(GameHostWindow.SeedTreeType(ItemIds.OakSeeds) == "FOAK_NN" &&
        GameHostWindow.SeedTreeType(ItemIds.PineSeeds) == "FPIN_NN" &&
        GameHostWindow.SeedTreeType(ItemIds.CactusSeeds) == "FCAC_NN" &&
        GameHostWindow.SeedTreeType(ItemIds.Logs) is null,
    "each seed must map to its matching tree graphic");
var morning = WorldTime.At(8 * 60 * 60);
var newGameTime = WorldTime.At(WorldTime.NewGameStartGameSeconds);
var normalSpawn = GameHostWindow.FindPlayableSpawn(2187);
var normalSpawnTileX = (int)MathF.Floor(normalSpawn.X);
var normalSpawnTileY = (int)MathF.Floor(normalSpawn.Y);
var cinematicBeachTile = (
    from y in Enumerable.Range(-80, 161)
    from x in Enumerable.Range(-80, 161)
    where InfiniteWorldGenerator.BiomeAt(2187, x, y) == Biome.Beach
    select new Vector2(x + .5f, y + .5f)).First();
var nonBeachLandTile = (
    from y in Enumerable.Range(-80, 161)
    from x in Enumerable.Range(-80, 161)
    let biome = InfiniteWorldGenerator.BiomeAt(2187, x, y)
    where biome is not (Biome.Beach or Biome.DeepWater or
        Biome.ShallowWater or Biome.RiverWater or Biome.MangroveShallows)
    select new Vector2(x + .5f, y + .5f)).First();
using var cancelledSpawnSearch = new CancellationTokenSource();
cancelledSpawnSearch.Cancel();
var spawnCancellationObserved = false;
try
{
    GameHostWindow.FindPlayableSpawn(
        2187, cancelledSpawnSearch.Token);
}
catch (OperationCanceledException)
{
    spawnCancellationObserved = true;
}
var midnight = WorldTime.At(0);
var nextDay = WorldTime.At(24 * 60 * 60);
Require(newGameTime.Day == 1 && newGameTime.Hour == 3 &&
        newGameTime.Daylight < morning.Daylight &&
        morning.Day == 1 && morning.Hour == 8 &&
        midnight.Daylight < morning.Daylight &&
        nextDay.Day == 2 && nextDay.Hour == 0,
    "world time must track day number, clock time, and daylight");
Require(
    InfiniteWorldGenerator.BiomeAt(
        2187, normalSpawnTileX, normalSpawnTileY) is not (
            Biome.DeepWater or Biome.ShallowWater or
            Biome.RiverWater or Biome.MangroveShallows) &&
    Math.Max(Math.Abs(normalSpawnTileX), Math.Abs(normalSpawnTileY)) <= 160 &&
    GameHostWindow.ShouldPlayOpeningCinematic(2187, normalSpawn) ==
        (InfiniteWorldGenerator.BiomeAt(
            2187, normalSpawnTileX, normalSpawnTileY) == Biome.Beach) &&
    GameHostWindow.ShouldPlayOpeningCinematic(
        2187, cinematicBeachTile) &&
    !GameHostWindow.ShouldPlayOpeningCinematic(
        2187, nonBeachLandTile) &&
    spawnCancellationObserved,
    "new games must select the normal nearest land start, gate the intro on an actual beach, and honor cancellation");
Require(WorldTime.Advance(0, WorldTime.RealSecondsPerGameDay) ==
        24 * 60 * 60,
    "one full game day must take 24 real minutes");
Require(WorldTime.At(
            morning.Hour * 60 * 60 + 12 * 60 * 60).Hour == 20,
    "the developer twelve-hour advance must preserve exact world-clock arithmetic");
Require(CampfireLightSource.Opacity(0, 0) == 0 &&
        CampfireLightSource.Opacity(0, 1) > .8f,
    "campfire lighting must disappear in daylight and remain strong at night");
Require(
    WorldLighting.Darkness(
        1, (int)WorldLevel.Overworld) == 0 &&
    WorldLighting.Darkness(
        1, (int)WorldLevel.Underground) == 1,
    "underground lighting must remain dark regardless of surface daylight");
var caveProbe = new WorldGroundObject(
    Guid.NewGuid(), ItemIds.CaveHole, 4.5f, 8.5f);
var freshDigSite = new WorldGroundObject(
    Guid.NewGuid(), ItemIds.DigSite, 4.5f, 8.5f,
    Health: 100, MaxHealth: 100);
var advancedDigSite = freshDigSite with { Health = 25 };
Require(
    CaveEntranceService.IsHole(caveProbe) &&
    CaveEntranceService.IsCaveShaft(caveProbe) &&
    CaveEntranceService.CanFill(caveProbe) &&
    CaveEntranceService.Opacity(freshDigSite) <
        CaveEntranceService.Opacity(advancedDigSite) &&
    CaveEntranceService.Opacity(caveProbe) == 1 &&
    CaveEntranceService.IsEntrance(
        CaveEntranceService.InstallRope(caveProbe)) &&
    CaveEntranceService.IsCaveShaft(
        CaveEntranceService.InstallRope(caveProbe)) &&
    CraftingSkill.Recipes.Any(recipe =>
        recipe.ResultItemId == ItemIds.StoneShovel) &&
    CraftingSkill.Recipes.Any(recipe =>
        recipe.ResultItemId == ItemIds.Rope),
    "cave access and excavation presentation must follow their authored states");
Require(
    CaveEntranceService.TryProspect(
        9187, 58.5f, 39.5f, out var caveProspect) &&
    CaveEntranceService.CaveBelow(
        9187, caveProspect.X, caveProspect.Y) &&
    caveProspect.Distance is > 0 and <= 32 &&
    !string.IsNullOrWhiteSpace(caveProspect.Direction) &&
    CaveEntranceService.ProspectMessage(caveProspect).Contains(
        caveProspect.Direction, StringComparison.Ordinal),
    "failed excavations must provide a bounded, truthful bearing toward nearby cave-bearing ground");
var sandDigging = DiggingSkill.Terrain(Biome.Beach);
var rockDigging = DiggingSkill.Terrain(Biome.Rock);
Require(
    sandDigging.RewardItemId == ItemIds.Sand &&
    rockDigging.RewardItemId == ItemIds.Dirt &&
    rockDigging.Health > sandDigging.Health &&
    DiggingSkill.Damage(
        DiggingSkill.ExperienceForLevel(20)) >
    DiggingSkill.Damage(0),
    "digging must reward terrain material and scale effort by terrain and skill");

Require(WoodcuttingSkill.LevelForExperience(0) == 1,
    "woodcutting must begin at level one");
Require(WoodcuttingSkill.LevelForExperience(
        WoodcuttingSkill.ExperienceForLevel(20)) == 20,
    "woodcutting progression must reach the level twenty cap");
for (var level = 2; level < WoodcuttingSkill.MaximumLevel; level++)
    Require(
        WoodcuttingSkill.ExperienceForLevel(level + 1) -
        WoodcuttingSkill.ExperienceForLevel(level) >
        WoodcuttingSkill.ExperienceForLevel(level) -
        WoodcuttingSkill.ExperienceForLevel(level - 1),
        $"woodcutting level {level + 1} must require more XP than level {level}");
Require(
    WoodcuttingSkill.HitChance(20) > WoodcuttingSkill.HitChance(1),
    "higher woodcutting levels must hit more reliably");
Require(
    Math.Abs(WoodcuttingSkill.SwingLogChance(1) - .05f) < .00001f &&
    Math.Abs(WoodcuttingSkill.SwingLogChance(20) - .25f) < .00001f &&
    WoodcuttingSkill.GrantsSwingLog(1, .0499f) &&
    !WoodcuttingSkill.GrantsSwingLog(1, .0501f) &&
    WoodcuttingSkill.GrantsSwingLog(20, .2499f),
    "damaging woodcutting swings must have a log chance scaling from 5 to 25 percent");
Require(
    WoodcuttingSkill.FellingLogCount(65) == 2 &&
    WoodcuttingSkill.FellingLogCount(75) == 2 &&
    WoodcuttingSkill.FellingLogCount(125) == 3 &&
    WoodcuttingSkill.FellingLogCount(175) == 4,
    "felling rewards must scale with tree durability while guaranteeing more than one construction log");
var noviceHit = WoodcuttingSkill.Roll(0, 0, 0);
var masterHit = WoodcuttingSkill.Roll(
    WoodcuttingSkill.ExperienceForLevel(20), 0, .999f);
Require(noviceHit.Hit && masterHit.Hit && masterHit.Damage > noviceHit.Damage,
    "higher woodcutting levels must deal more damage");
Require(!WoodcuttingSkill.Roll(0, .9f, 0).Hit,
    "a novice woodcutter must be able to miss");
Require(CraftingSkill.LevelForExperience(0) == 1 &&
        CraftingSkill.LevelForExperience(
            CraftingSkill.ExperienceForLevel(20)) == 20,
    "crafting must use the complete 20-level progression");
var primitiveNetRecipe = CraftingSkill.Recipes.Single(
    recipe => recipe.ResultItemId == ItemIds.PrimitiveFishingNet);
Require(primitiveNetRecipe.Category == CraftingCategory.Tools &&
        primitiveNetRecipe.RequiredLevel == 2 &&
        primitiveNetRecipe.Ingredients.SequenceEqual(
            [new CraftingIngredient(ItemIds.PlantFibres, 6)]) &&
        primitiveNetRecipe.Steps.Count == 3 &&
        CraftingSkill.Availability(
            primitiveNetRecipe, 2,
            Enumerable.Repeat(ItemIds.PlantFibres, 6).ToArray()) ==
        RecipeAvailability.Ready,
    "the primitive fishing net must be a level-two tool woven from six fibres");
var stoneKnifeRecipe = CraftingSkill.Recipes.Single(
    recipe => recipe.ResultItemId == ItemIds.StoneKnife);
Require(stoneKnifeRecipe.Category == CraftingCategory.Tools &&
        stoneKnifeRecipe.RequiredLevel == 1 &&
        stoneKnifeRecipe.Ingredients.SequenceEqual(
        [
            new CraftingIngredient(ItemIds.PlantFibres, 1),
            new CraftingIngredient(ItemIds.SharpenedRock, 1)
        ]) &&
        CraftingSkill.Availability(
            stoneKnifeRecipe, 1,
            [ItemIds.PlantFibres, ItemIds.SharpenedRock]) ==
        RecipeAvailability.Ready,
    "the stone knife must be a level-one tool made from fibre and a sharp rock");
Require(CraftingService.TryCraft(
            stoneKnifeRecipe, 1,
            [ItemIds.PlantFibres, ItemIds.SharpenedRock],
            out var craftedStoneKnife) &&
        craftedStoneKnife.Count(
            item => item == ItemIds.StoneKnife) == 1 &&
        !craftedStoneKnife.Contains(ItemIds.PlantFibres) &&
        !craftedStoneKnife.Contains(ItemIds.SharpenedRock),
    "crafting a stone knife must consume its fibre and sharp rock");
var plankRecipe = CraftingSkill.Recipes.Single(
    recipe => recipe.ResultItemId == ItemIds.Plank);
Require(plankRecipe.RequiredTools?.SequenceEqual(
            [new CraftingToolRequirement(ItemTag.Knife, "knife")]) == true &&
        CraftingService.TryCraft(
            plankRecipe, 2,
            [ItemIds.StoneKnife, ItemIds.OakLogs],
            out var craftedPlank) &&
        craftedPlank.Contains(ItemIds.StoneKnife) &&
        craftedPlank.Contains(ItemIds.Plank),
    "the plank recipe must accept any authored log and preserve any knife tool");
var metalToolIds = new[]
{
    ItemIds.BronzeHammer,
    ItemIds.IronHammer,
    ItemIds.BronzeKnife,
    ItemIds.IronKnife,
    ItemIds.BronzeShovel,
    ItemIds.IronShovel,
    ItemIds.IronSickle
};
Require(
    metalToolIds.Select(ItemCatalog.Get).All(item =>
        item.HasTag(ItemTag.Tool) &&
        item.HasTag(ItemTag.AdvancedToolSprite)) &&
    metalToolIds.Select(ItemCatalog.Get).Select(item => item.SpriteCell)
        .SequenceEqual(Enumerable.Range(0, 7).Select(value => (int?)value)) &&
    CraftingSkill.Recipes.Count(recipe =>
        metalToolIds.Contains(recipe.ResultItemId)) == metalToolIds.Length,
    "the complete bronze and iron tool gap must use one shared sprite family and have recipes");
Require(
    ItemCatalog.Get(ItemIds.BronzeShovel).DiggingPower == 2 &&
    ItemCatalog.Get(ItemIds.IronShovel).DiggingPower == 3 &&
    DiggingSkill.Damage(0, 3) > DiggingSkill.Damage(0, 1) &&
    ItemCatalog.Get(ItemIds.IronSickle).FarmingPower == 2,
    "advanced gathering tools must improve their associated skill power");
Require(
    PlayerInventory.BestHammer(
        [ItemIds.StoneHammer, ItemIds.IronHammer])?.Id ==
        ItemIds.IronHammer &&
    PlayerInventory.BestKnife(
        [ItemIds.StoneKnife, ItemIds.BronzeKnife])?.Id ==
        ItemIds.BronzeKnife &&
    ItemCatalog.Get(ItemIds.StoneKnife).HasTag(ItemTag.Weapon) &&
    ItemCatalog.Get(ItemIds.BronzeKnife).HasTag(ItemTag.Weapon) &&
    ItemCatalog.Get(ItemIds.IronKnife).HasTag(ItemTag.Weapon) &&
    PlayerInventory.TryBreakRock(
        [ItemIds.BronzeHammer, ItemIds.LargeRock], 0, 1,
        out var bronzeHammerSplit) &&
    bronzeHammerSplit[0] == ItemIds.BronzeHammer &&
    bronzeHammerSplit.Count(item => item == ItemIds.MediumRock) == 2,
    "knife selection must prefer one best weapon while metal hammers continue using shared tool actions");
var bronzeHammerRecipe = CraftingSkill.Recipes.Single(recipe =>
    recipe.ResultItemId == ItemIds.BronzeHammer);
Require(
    CraftingSkill.AwardExperience(
        0, bronzeHammerRecipe, [ItemIds.IronHammer]).Gained >
    CraftingSkill.AwardExperience(
        0, bronzeHammerRecipe, [ItemIds.StoneHammer]).Gained &&
    ItemDescriptionService.Describe(
        ItemCatalog.Get(ItemIds.IronHammer)).Contains("Hammer power: 3") &&
    ItemDescriptionService.Describe(
        ItemCatalog.Get(ItemIds.AdvancedFishingNet)).Contains(
            "Fishing power: 3"),
    "advanced crafting tools must grant a power benefit and all tool powers must be visible when examined");
Require(
    VillagerCraftPlanner.PriorityFor(VillagerWorkRole.Food)
        .Contains(ItemIds.AdvancedFishingNet) &&
    VillagerCraftPlanner.PriorityFor(VillagerWorkRole.Crafting)
        .Contains(ItemIds.IronHammer) &&
    VillagerCraftPlanner.PriorityFor(VillagerWorkRole.Exploration)
        .Contains(ItemIds.IronShovel) &&
    !VillagerCraftPlanner.Needs(
        ItemIds.StoneHammer, [ItemIds.IronHammer]) &&
    VillagerCraftPlanner.Needs(
        ItemIds.IronHammer, [ItemIds.StoneHammer]),
    "NPC crafting plans must pursue role upgrades without replacing superior tools with primitive ones");
foreach (var sheetDefinition in new[]
         {
             ItemSpriteSheetCatalog.AdvancedTools,
             ItemSpriteSheetCatalog.FishingNetUpgrades
         })
{
    var spritePath = Path.Combine(
        AppContext.BaseDirectory, "Resources", "Images",
        sheetDefinition.FileName);
    Require(File.Exists(spritePath),
        $"{sheetDefinition.FileName} must be copied beside the game");
    using var spriteStream = File.OpenRead(spritePath);
    var spriteSheet = ImageResult.FromStream(
        spriteStream, ColorComponents.RedGreenBlueAlpha);
    Require(
        spriteSheet.Width == sheetDefinition.Width &&
        spriteSheet.Height == sheetDefinition.Height &&
        spriteSheet.Data[3] == 0,
        $"{sheetDefinition.FileName} must have the configured transparent 32-pixel grid");
    for (var cell = 0; cell < sheetDefinition.CellCount; cell++)
        Require(
            Enumerable.Range(0, sheetDefinition.CellSize)
                .SelectMany(y => Enumerable.Range(0, sheetDefinition.CellSize)
                    .Select(x => ((y * spriteSheet.Width) +
                                  cell * sheetDefinition.CellSize + x) * 4 + 3))
                .Any(alphaIndex => spriteSheet.Data[alphaIndex] > 0),
            $"{sheetDefinition.FileName} cell {cell} must contain a visible sprite");
}
var pickaxeRecipe = CraftingSkill.Recipes.Single(
    recipe => recipe.ResultItemId == ItemIds.StonePickaxe);
Require(pickaxeRecipe.Category == CraftingCategory.Tools &&
        pickaxeRecipe.RequiredLevel == 1 &&
        pickaxeRecipe.Ingredients.Count == 3 &&
        pickaxeRecipe.Steps.Count == 3,
    "the stone pickaxe recipe must define its level, materials, and ordered steps");
Require(CraftingSkill.Availability(
            pickaxeRecipe, 1, []) ==
        RecipeAvailability.MissingResources &&
        CraftingSkill.Availability(
            pickaxeRecipe, 1,
            [ItemIds.SharpenedRock, ItemIds.MediumRock, ItemIds.Sticks]) ==
        RecipeAvailability.Ready,
    "the level-one stone pickaxe must still require all of its resources");
var pickaxeContainer = PlayerInventory.CreateContainer();
Require(
    pickaxeContainer.TryAdd(ItemIds.SharpenedRock) &&
    pickaxeContainer.TryAdd(ItemIds.MediumRock) &&
    pickaxeContainer.TryAdd(ItemIds.Sticks) &&
    CraftingService.TryCraftDetailed(
        pickaxeRecipe, 1, pickaxeContainer,
        out var craftedPickaxeContainer) ==
    CraftingService.CraftResult.Success &&
    craftedPickaxeContainer.Count(ItemIds.StonePickaxe) == 1 &&
    craftedPickaxeContainer.ItemCount == 1,
    "container-based crafting must allow transient recipe products without exposing or persisting them as catalog items");
var workbenchRecipe = CraftingSkill.Recipes.Single(
    recipe => recipe.ResultItemId == ItemIds.Workbench);
var storageChestRecipe = CraftingSkill.Recipes.Single(
    recipe => recipe.ResultItemId == ItemIds.StorageChest);
Require(
    storageChestRecipe.RequiredLevel == 4,
    "the wooden storage chest must unlock at Crafting level four");
Require(workbenchRecipe.Category == CraftingCategory.Furniture &&
        workbenchRecipe.RequiredLevel == 3 &&
        workbenchRecipe.Experience == 76 &&
        workbenchRecipe.Ingredients.SequenceEqual(
        [
            new CraftingIngredient(ItemIds.Plank, 4),
            new CraftingIngredient(ItemIds.Sticks, 2)
        ]) &&
        workbenchRecipe.RequiredTools?.SequenceEqual(
            [new CraftingToolRequirement(ItemTag.Hammer, "hammer")]) ==
        true &&
        CraftingSkill.Availability(
            workbenchRecipe, 3,
            [
                ItemIds.Plank, ItemIds.Plank,
                ItemIds.Plank, ItemIds.Plank,
                ItemIds.Sticks, ItemIds.Sticks,
                ItemIds.StoneHammer
            ]) == RecipeAvailability.Ready,
    "the workbench must be a level-three Furniture recipe made with a stone hammer");
const int preWorkbenchQuestCraftingExperience = 449;
Require(
    CraftingSkill.AwardExperience(
        preWorkbenchQuestCraftingExperience,
        workbenchRecipe,
        [ItemIds.StoneHammer]).Level >=
    storageChestRecipe.RequiredLevel,
    "the intended early quest crafts plus the workbench must unlock the level-four storage chest without a duplicate filler craft");
Require(CraftingSkill.Availability(
            workbenchRecipe, 3,
            [
                ItemIds.Plank, ItemIds.Plank,
                ItemIds.Plank, ItemIds.Plank,
                ItemIds.Sticks, ItemIds.Sticks
            ]) == RecipeAvailability.MissingResources &&
        CraftingService.TryCraft(
            workbenchRecipe, 3,
            [
                ItemIds.Plank, ItemIds.Plank,
                ItemIds.Plank, ItemIds.Plank,
                ItemIds.Sticks, ItemIds.Sticks,
                ItemIds.StoneHammer
            ],
            out var craftedWorkbenchInventory) &&
        craftedWorkbenchInventory.Contains(ItemIds.Workbench) &&
        craftedWorkbenchInventory.Contains(ItemIds.StoneHammer),
    "crafting a workbench must require but not consume its stone hammer");
Require(CraftingSkill.Availability(
            workbenchRecipe, 3,
            [
                ItemIds.Plank, ItemIds.Plank,
                ItemIds.Plank, ItemIds.Plank,
                ItemIds.Sticks, ItemIds.Sticks,
                ItemIds.BluntStoneHammer
            ]) == RecipeAvailability.Ready,
    "the workbench must accept any item registered as a hammer");
var campfireRecipe = CraftingSkill.Recipes.Single(
    recipe => recipe.ResultItemId == ItemIds.Campfire);
Require(campfireRecipe.Category == CraftingCategory.Furniture &&
        campfireRecipe.RequiredLevel == 1 &&
        campfireRecipe.Experience == 25 &&
        campfireRecipe.Ingredients.SequenceEqual(
            [new CraftingIngredient(ItemIds.SmallRocks, 3)]) &&
        CraftingSkill.Availability(
            campfireRecipe, 1,
            [
                ItemIds.SmallRocks,
                ItemIds.SmallRocks,
                ItemIds.SmallRocks
            ]) == RecipeAvailability.Ready,
    "the campfire must be a level-one Furniture recipe made from small rocks");
var emptyCampfire = new WorldGroundObject(
    Guid.NewGuid(), ItemIds.Campfire, 4.5f, 7.5f);
Require(CampfireService.State(emptyCampfire, 100) ==
            CampfireState.Empty &&
        CampfireService.CanAddFuel(
            emptyCampfire, ItemIds.OakLogs, 100) &&
        !CampfireService.CanAddFuel(
            emptyCampfire, ItemIds.Sticks, 100),
    "an empty campfire must accept any log-tagged item but reject sticks");
var fueledCampfire = CampfireService.AddFuel(
    emptyCampfire, ItemIds.OakLogs, 100);
Require(fueledCampfire.FuelItemId == ItemIds.OakLogs &&
        CampfireService.State(fueledCampfire, 100) ==
            CampfireState.Fueled &&
        CampfireService.CanLight(
            fueledCampfire,
            [ItemIds.SmallRocks, ItemIds.StoneKnife],
            100) &&
        !CampfireService.CanLight(
            fueledCampfire, [ItemIds.SmallRocks], 100) &&
        CampfireService.LightFailure(
            emptyCampfire,
            [ItemIds.SmallRocks, ItemIds.StoneKnife],
            100) == CampfireLightFailure.NotFueled &&
        CampfireService.LightFailure(
            fueledCampfire, [ItemIds.StoneKnife], 100) ==
            CampfireLightFailure.SmallRocksMissing &&
        CampfireService.LightFailure(
            fueledCampfire, [ItemIds.SmallRocks], 100) ==
            CampfireLightFailure.KnifeMissing &&
        CampfireService.LightFailureCode(
            CampfireLightFailure.SmallRocksMissing) ==
            "campfire_small_rocks_missing" &&
        CampfireService.LightFailureMessage(
            CampfireLightFailure.KnifeMissing).Contains("knife"),
    "campfire fuel must preserve its exact log type and lighting must require small rocks and a knife");
var litCampfire = CampfireService.Light(fueledCampfire, 100);
Require(CampfireService.State(litCampfire, 100) ==
            CampfireState.Lit &&
        litCampfire.LitUntilGameSeconds ==
            100 + WorldTime.GameSecondsPerDay * 2 &&
        CampfireService.Expire(
            litCampfire,
            100 + WorldTime.GameSecondsPerDay * 2).FuelItemId is null,
    "a lit campfire must burn for two full game-days and then consume its fuel");
var masterFire = CampfireService.Light(
    fueledCampfire, 100, FiremakingSkill.MaximumLevel);
Require(
    masterFire.FiremakingLevel == FiremakingSkill.MaximumLevel &&
    masterFire.LitUntilGameSeconds ==
        100 + FiremakingSkill.DurationGameSeconds(20) &&
    masterFire.LitUntilGameSeconds >
        litCampfire.LitUntilGameSeconds &&
    FiremakingSkill.LightRadiusPixels(20) >
        FiremakingSkill.LightRadiusPixels(1) &&
    FiremakingSkill.DurationGameSeconds(1) ==
        WorldTime.GameSecondsPerDay * 2 &&
    FiremakingSkill.LightRadiusPixels(1) ==
        FiremakingSkill.BaseLightRadiusPixels * 2 &&
    FiremakingSkill.LightIntensity(20) >
        FiremakingSkill.LightIntensity(1) &&
    FiremakingSkill.FlameTier(1) == 0 &&
    FiremakingSkill.FlameTier(6) == 1 &&
    FiremakingSkill.FlameTier(11) == 2 &&
    FiremakingSkill.FlameTier(16) == 3,
    "a fire must persist its lighting level and scale duration, light, and flame presentation through level 20");
var placeableUploadCount = 0;
var placeableSprites = PlaceableObjectSprites.Load(
    Path.Combine(AppContext.BaseDirectory, "Resources", "Images"),
    _ => ++placeableUploadCount);
var logTypeCount = ItemCatalog.All.Count(item =>
    item.HasTag(ItemTag.Log) && item.SpriteCell is not null);
Require(
    placeableSprites.CampfireAtlasFrames.Count() ==
    logTypeCount *
    (1 + FiremakingSkill.FlameTierCount *
        CampfireService.AnimationFrameCount) &&
    placeableSprites.TryGet(
        ItemIds.CookingPot, out var cookingPotSprite) &&
    cookingPotSprite.Frame.Width == 50 &&
    cookingPotSprite.Frame.Height == 50 &&
    placeableSprites.TryGet(
        ItemIds.StorageChest, out var storageChestSprite) &&
    storageChestSprite.Frame.Width == 60 &&
    placeableSprites.TryGet(
        ItemIds.SmithingAnvil, out var anvilSprite) &&
    anvilSprite.Frame.Width == 56 &&
    placeableSprites.TryGet(
        ItemIds.TrainingDummy, out var dummySprite) &&
    dummySprite.Frame.Height == 72 &&
    placeableSprites.TryGet(
        ItemIds.StorageBarrel, out var storageBarrelSprite) &&
    storageBarrelSprite.Frame.Height == 58 &&
    placeableUploadCount > 0,
    "placeable sprites must include the cooking pot and every campfire fuel, animation frame, and Firemaking flame tier");
var returnedFuelCampfire = CampfireService.RemoveFuel(
    fueledCampfire, 100);
Require(returnedFuelCampfire.FuelItemId is null &&
        CampfireService.State(returnedFuelCampfire, 100) ==
            CampfireState.Empty,
    "taking fuel must return an unlit campfire to its empty state");
Require(PlaceableObjectCatalog.TryGet(
            ItemIds.Workbench, out var workbenchDefinition) &&
        workbenchDefinition.FootprintWidth == 2 &&
        workbenchDefinition.FootprintDepth == 1 &&
        PlaceableObjectCatalog.ProjectedFrontOffsetPixels(
            ItemIds.Workbench) == 36 &&
        PlaceableObjectCatalog.SnapToGrid(
            ItemIds.Workbench, new(4.31f, 7.72f)) ==
        new OpenTK.Mathematics.Vector2(4.25f, 7.75f) &&
        PlaceableObjectCatalog.ContainsPoint(
            workbenchDefinition,
            new OpenTK.Mathematics.Vector2(4.25f, 7.75f),
            new OpenTK.Mathematics.Vector2(4.8f, 7.7f)) &&
        !PlaceableObjectCatalog.ContainsPoint(
            workbenchDefinition,
            new OpenTK.Mathematics.Vector2(4.25f, 7.75f),
            new OpenTK.Mathematics.Vector2(5.3f, 7.7f)) &&
        WorldPlacementGrid.CellsPerTerrainTile == 4 &&
        WorldPlacementGrid.CellCenter(
            WorldPlacementGrid.Cell(3.41f)) == 3.375f,
    "placeable objects and navigation must use a deterministic quarter-tile grid");
var dummyGroundContact = PlaceableObjectCatalog.GroundContactCenter(
    ItemIds.TrainingDummy,
    new OpenTK.Mathematics.Vector2(10, 20));
Require(
    (dummyGroundContact -
     new OpenTK.Mathematics.Vector2(
         10.4125f, 20.4125f)).Length < .0001f,
    "navigation must follow the same forward ground anchor used to render placed objects");
var navigationObstacle = new NavigationObstacle(
    new OpenTK.Mathematics.Vector2(4.25f, 7.75f), 2, 1);
Require(
    navigationObstacle.Contains(
        new OpenTK.Mathematics.Vector2(3.2f, 7.75f)) &&
    !navigationObstacle.Contains(
        new OpenTK.Mathematics.Vector2(3.0f, 7.75f)) &&
    navigationObstacle.Contains(
        new OpenTK.Mathematics.Vector2(4.25f, 8.4f)),
    "navigation obstacles must block the full item footprint plus player clearance");
var groundContactPixels = new byte[20 * 20 * 4];
for (var y = 10; y <= 16; y++)
for (var x = 8; x <= 11; x++)
    groundContactPixels[(y * 20 + x) * 4 + 3] = 255;
var measuredGroundContact = SpriteGroundContactCalculator.Measure(
    new SpriteFrame(20, 20, 9, 16, groundContactPixels));
Require(
    measuredGroundContact.Width == .16f &&
    measuredGroundContact.Depth == .16f &&
    measuredGroundContact.LateralOffset > 0,
    "resource navigation footprints must be measured from each sprite's opaque ground contact");
const long navigationPathSeed = 78193021;
var navigationLandTile = (
    from y in Enumerable.Range(-16, 33)
    from x in Enumerable.Range(-16, 33)
    where InfiniteWorldGenerator.BiomeAt(
        navigationPathSeed, x, y) != Biome.DeepWater
    select new OpenTK.Mathematics.Vector2i(x, y)).First();
var navigationStart = new OpenTK.Mathematics.Vector2(
    navigationLandTile.X + .125f,
    navigationLandTile.Y + .125f);
var exactNavigationTarget = new OpenTK.Mathematics.Vector2(
    navigationLandTile.X + .73f,
    navigationLandTile.Y + .66f);
var exactNavigationPath = GridPathfinder.Find(
    navigationPathSeed,
    navigationStart,
    exactNavigationTarget);
Require(
    exactNavigationPath.Count > 0 &&
    exactNavigationPath[^1] == exactNavigationTarget,
    "valid movement clicks must preserve their exact world endpoint");
var blockedNavigationTarget = new OpenTK.Mathematics.Vector2(
    navigationLandTile.X + .875f,
    navigationLandTile.Y + .875f);
var blockingNavigationObstacle = new NavigationObstacle(
    blockedNavigationTarget, .1f, .1f);
var resolvedNavigationPath = GridPathfinder.Find(
    navigationPathSeed,
    navigationStart,
    blockedNavigationTarget,
    obstacles: [blockingNavigationObstacle]);
Require(
    resolvedNavigationPath.Count > 0 &&
    !blockingNavigationObstacle.Contains(resolvedNavigationPath[^1]) &&
    (resolvedNavigationPath[^1] - blockedNavigationTarget).Length <= .26f,
    "blocked movement clicks must resolve to the nearest clear quarter-cell");
var farActionTarget = navigationStart + new OpenTK.Mathematics.Vector2(
    ActionPathSearchPolicy.AlternativeApproachDistance + 1, 0);
Require(
    ActionPathSearchPolicy.MaximumVisited < 65536 &&
    ActionPathSearchPolicy.ShouldTryAlternativeApproach(
        navigationStart, exactNavigationTarget) &&
    !ActionPathSearchPolicy.ShouldTryAlternativeApproach(
        navigationStart, farActionTarget),
    "action paths must bound expensive searches while retaining nearby alternate interaction sides");
Require(PlaceableObjectCatalog.TryGet(
            ItemIds.Campfire, out var campfireDefinition) &&
        campfireDefinition.FootprintWidth == 1 &&
        campfireDefinition.FootprintDepth == 1 &&
        campfireDefinition.HotspotX == 29 &&
        campfireDefinition.HotspotY == 54,
    "the campfire must be registered as a compact one-tile placeable");
Require(
    PlaceableObjectCatalog.TryGet(
        ItemIds.Bloomery, out var bloomeryDefinition) &&
    bloomeryDefinition.FootprintWidth == 1.5f &&
    bloomeryDefinition.HotspotX == 58 &&
    bloomeryDefinition.HotspotY == 98 &&
    PlaceableObjectCatalog.TryGet(
        ItemIds.SmithingAnvil, out var anvilDefinition) &&
    anvilDefinition.FootprintWidth == 1 &&
    anvilDefinition.HotspotX == 28 &&
    anvilDefinition.HotspotY == 48 &&
    ItemCatalog.Get(ItemIds.Bloomery)
        .HasTag(ItemTag.PlaceableObject) &&
    ItemCatalog.Get(ItemIds.SmithingAnvil)
        .HasTag(ItemTag.PlaceableObject) &&
    PlaceableObjectCatalog.TryGet(
        ItemIds.CookingPot, out var cookingPotDefinition) &&
    cookingPotDefinition.SpriteFile == "cooking-pot.png" &&
    cookingPotDefinition.FootprintWidth < 1 &&
    ItemCatalog.Get(ItemIds.CookingPot)
        .HasTag(ItemTag.PlaceableObject),
    "metalworking and cooking stations must use generated placeable-object footprints");
var nearbyStations = new[]
{
    new WorldGroundObject(
        Guid.NewGuid(), ItemIds.Workbench, 11, 10),
    new WorldGroundObject(
        Guid.NewGuid(), ItemIds.Bloomery, 12, 10),
    new WorldGroundObject(
        Guid.NewGuid(), ItemIds.SmithingAnvil, 30, 30)
};
Require(
    CraftingStationService.IsStation(ItemIds.Workbench) &&
    CraftingStationService.IsWithinRange(
        nearbyStations, ItemIds.Workbench, new(10, 10)) &&
    CraftingStationService.IsWithinRange(
        nearbyStations, ItemIds.Bloomery, new(10, 10)) &&
    !CraftingStationService.IsWithinRange(
        nearbyStations, ItemIds.SmithingAnvil, new(10, 10)) &&
    CraftingStationService.ActionLabel(ItemIds.Workbench) == "Craft" &&
    CraftingStationService.ActionLabel(ItemIds.Bloomery) == "Smelt" &&
    CraftingStationService.ActionLabel(ItemIds.SmithingAnvil) == "Smith",
    "crafting stations must require the matching placed object within local interaction range");
var modalScreen = new ModalScreenState();
modalScreen.Open(ModalScreenKind.Crafting);
Require(modalScreen.IsOpen &&
        modalScreen.BlursBackground &&
        modalScreen.HidesGameUi &&
        modalScreen.CapturesAllInput &&
        !modalScreen.PausesSimulation,
    "crafting must be an exclusive blurred modal without pausing simulation");
modalScreen.Open(ModalScreenKind.Pause);
Require(modalScreen.PausesSimulation,
    "the pause menu must use the same modal standard and pause simulation");
modalScreen.Close(ModalScreenKind.Pause);
Require(!modalScreen.IsOpen,
    "closing a modal must restore the normal game screen");
modalScreen.Open(ModalScreenKind.SkillGuide);
Require(modalScreen.CapturesAllInput &&
        modalScreen.BlursBackground &&
        modalScreen.HidesGameUi &&
        !modalScreen.PausesSimulation,
    "the skill guide must use the reusable non-pausing modal standard");
modalScreen.Close(ModalScreenKind.SkillGuide);

var startingInventory = PlayerInventory.CreateStartingInventory();
Require(startingInventory.Length == PlayerInventory.Capacity &&
        PlayerInventory.Count(startingInventory) == 0 &&
        !PlayerInventory.HasAxe(startingInventory),
    "a new character must start with an empty fixed 28-slot inventory");
Require(PlayerInventory.CanDrop(ItemIds.IronAxe) &&
        PlayerInventory.CanDrop(ItemIds.Logs),
    "all inventory items must be droppable into the world");
Require(ItemCatalog.Get(ItemIds.IronAxe) is var axeDefinition &&
        axeDefinition.Name == "iron axe" &&
        axeDefinition.SpriteCell == 5 &&
        axeDefinition.HasTag(ItemTag.Axe) &&
        axeDefinition.HasTag(ItemTag.Tool) &&
        axeDefinition.WoodcuttingPower == 3 &&
        ItemCatalog.Get(ItemIds.BronzeAxe).WoodcuttingPower == 2 &&
        ItemCatalog.Get(ItemIds.StoneAxe).WoodcuttingPower == 1 &&
        PlayerInventory.BestAxe(
            [ItemIds.StoneAxe, ItemIds.BronzeAxe, ItemIds.IronAxe])?.Id ==
        ItemIds.IronAxe &&
        axeDefinition.Droppable &&
        ItemCatalog.Get(ItemIds.OakLogs).HasTag(ItemTag.Log) &&
        ItemCatalog.All.Select(item => item.Id).Distinct().Count() ==
        ItemCatalog.All.Count,
    "the item catalogue must own axe/log gameplay and presentation metadata");
Require(ItemCatalog.Get(ItemIds.StonePickaxe) is var pickaxeDefinition &&
        pickaxeDefinition.Name == "stone pickaxe" &&
        pickaxeDefinition.SpriteCell == 2 &&
        pickaxeDefinition.HasTag(ItemTag.Tool) &&
        pickaxeDefinition.HasTag(ItemTag.StoneToolSprite) &&
        !pickaxeDefinition.HasTag(ItemTag.Axe),
    "the stone pickaxe must use the third stone-tool sprite without acting as an axe");
var catalogItemIds = ItemCatalog.All
    .Select(item => item.Id)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
Require(
    CraftingSkill.Recipes.Select(recipe => recipe.Id)
        .Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
    CraftingSkill.Recipes.Count &&
    CraftingSkill.Recipes.All(recipe =>
        catalogItemIds.Contains(recipe.ResultItemId) &&
        recipe.Ingredients.All(ingredient =>
            ingredient.Count > 0 &&
            catalogItemIds.Contains(ingredient.ItemId) &&
            (ingredient.AlternativeItemIds?.All(
                catalogItemIds.Contains) ?? true))),
    "every crafting recipe must have a unique id and reference registered positive-count items");
Require(
    ReferenceEquals(
        CraftingSkill.RecipesFor(CraftingCategory.All),
        CraftingSkill.RecipesFor(CraftingCategory.All)) &&
    CraftingSkill.RecipesFor(CraftingCategory.Tools)
        .All(recipe => recipe.Category == CraftingCategory.Tools),
    "crafting category views must reuse cached recipe lists instead of allocating every frame");
var workbenchRecipes = CraftingSkill.RecipesFor(
    CraftingCategory.All, ItemIds.Workbench);
var bloomeryRecipes = CraftingSkill.RecipesFor(
    CraftingCategory.All, ItemIds.Bloomery);
var anvilRecipes = CraftingSkill.RecipesFor(
    CraftingCategory.All, ItemIds.SmithingAnvil);
Require(
    ReferenceEquals(
        workbenchRecipes,
        CraftingSkill.RecipesFor(
            CraftingCategory.All, ItemIds.Workbench)) &&
    workbenchRecipes.Count == 5 &&
    workbenchRecipes.All(recipe =>
        recipe.RequiredStationItemId == ItemIds.Workbench) &&
    bloomeryRecipes.Count == 2 &&
    bloomeryRecipes.All(recipe =>
        recipe.RequiredStationItemId == ItemIds.Bloomery) &&
    anvilRecipes.Count == 15 &&
    anvilRecipes.All(recipe =>
        recipe.RequiredStationItemId == ItemIds.SmithingAnvil),
    "station recipe views must be cached and contain only recipes for the station used");
var woodenWallRecipe = CraftingSkill.Recipes.First(value =>
    value.ResultItemId == ItemIds.WoodenWall);
Require(
    woodenWallRecipe.RequiredLevel == 1 &&
    woodenWallRecipe.RequiredStationItemId is null &&
    woodenWallRecipe.Ingredients is [{ ItemId: ItemIds.Logs, Count: 1 }],
    "player wall construction must be available at Crafting level 1 without a workbench and consume one log per segment");
var wallPlacementInventory = new InventoryContainer(PlayerInventory.Capacity);
wallPlacementInventory.TryAdd(ItemIds.Logs);
wallPlacementInventory.TryAdd(ItemIds.StoneHammer);
var wallPlacementResult = CraftingService.TryConsumeForPlacement(
    woodenWallRecipe, 1, wallPlacementInventory,
    out var afterWallPlacement);
Require(
    wallPlacementResult == CraftingService.CraftResult.Success &&
    wallPlacementInventory.Count(ItemIds.Logs) == 1 &&
    afterWallPlacement.Count(ItemIds.Logs) == 0 &&
    afterWallPlacement.Count(ItemIds.StoneHammer) == 1 &&
    afterWallPlacement.Count(ItemIds.WoodenWall) == 0,
    "placing a wall foundation must atomically consume one log without crafting a wall into inventory or consuming the hammer");
var noLogPlacement = CraftingService.TryConsumeForPlacement(
    woodenWallRecipe, 1, afterWallPlacement,
    out var unchangedWallInventory);
Require(
    noLogPlacement == CraftingService.CraftResult.MissingResources &&
    unchangedWallInventory.ItemIds().SequenceEqual(
        afterWallPlacement.ItemIds()) &&
    unchangedWallInventory.Quantities().SequenceEqual(
        afterWallPlacement.Quantities()),
    "a wall foundation without resources must fail without mutating inventory");
var buildingPlacement = new PlaceableObjectPlacementController();
buildingPlacement.BeginConstruction(ItemIds.WoodenWall);
Require(
    buildingPlacement.Active &&
    !buildingPlacement.ConsumesInventoryItem &&
    buildingPlacement.InventorySlot == -1,
    "building placement must remain active without requiring a crafted inventory slot");
var horizontalWallLine = WallPlacementPlanner.Line(
    new Vector2(2.5f, 4.5f), new Vector2(6.5f, .5f));
var verticalWallLine = WallPlacementPlanner.Line(
    new Vector2(2.5f, 2.5f), new Vector2(5.5f, 5.5f));
var horizontalFirstWall = WallPlacementPlanner.Generate(
    new Vector2(.5f, .5f), new Vector2(4.5f, -1.5f));
var retainedHorizontalWall = WallPlacementPlanner.Generate(
    new Vector2(.5f, .5f), new Vector2(4.5f, .5f),
    WallDragOrientation.HorizontalFirst);
var switchedVerticalWall = WallPlacementPlanner.Generate(
    new Vector2(.5f, .5f), new Vector2(1.5f, 3.5f),
    WallDragOrientation.HorizontalFirst);
var negativeWall = WallPlacementPlanner.Generate(
    new Vector2(5.5f, 5.5f), new Vector2(2.5f, 3.5f));
Require(
    horizontalWallLine.Count == 5 &&
    horizontalWallLine[0] == new Vector2(2.5f, 4.5f) &&
    horizontalWallLine[^1] == new Vector2(6.5f, .5f) &&
    verticalWallLine.Count == 4 &&
    WallPlacementPlanner.FrameAt(horizontalWallLine, 1) == 3 &&
    WallPlacementPlanner.FrameAt(verticalWallLine, 1) == 4 &&
    horizontalFirstWall.Orientation == WallDragOrientation.HorizontalFirst &&
    horizontalFirstWall.Tiles.Count == 7 &&
    horizontalFirstWall.Tiles[4] == new Vector2(4.5f, .5f) &&
    horizontalFirstWall.Tiles.Distinct().Count() == 7 &&
    WallPlacementPlanner.FrameAt(horizontalFirstWall.Tiles, 4) == 2 &&
    retainedHorizontalWall.Orientation == WallDragOrientation.HorizontalFirst &&
    switchedVerticalWall.Orientation == WallDragOrientation.VerticalFirst &&
    negativeWall.Tiles[0] == new Vector2(5.5f, 5.5f) &&
    negativeWall.Tiles[^1] == new Vector2(2.5f, 3.5f) &&
    WallPlacementPlanner.FrameAt([new Vector2(.5f, .5f)], 0) == 2,
    "wall dragging must generate stable straight or L-shaped paths with endpoint and corner pieces");
var threeWallInventory = new InventoryContainer(PlayerInventory.Capacity);
threeWallInventory.TryAdd(ItemIds.Logs, 3);
threeWallInventory.TryAdd(ItemIds.StoneHammer);
var threeWallResult = CraftingService.TryConsumeForPlacement(
    woodenWallRecipe, 1, threeWallInventory,
    out var afterThreeWalls, placements: 3);
Require(
    threeWallResult == CraftingService.CraftResult.Success &&
    afterThreeWalls.Count(ItemIds.Logs) == 0 &&
    afterThreeWalls.Count(ItemIds.StoneHammer) == 1,
    "confirming a wall line must atomically consume one log for every green foundation");
Require(
    ConstructionService.DemolitionRefund(wallFoundation) == ItemIds.Logs &&
    ConstructionService.DemolitionRefund(finishedWall) is null,
    "demolishing an unfinished palisade must refund its log while completed walls cannot use the construction refund");
Require(
    new[] { ItemIds.StorageChest, ItemIds.StorageBarrel }
        .Select(itemId => CraftingSkill.Recipes.Single(recipe =>
            recipe.ResultItemId == itemId))
        .All(recipe =>
            recipe.RequiredStationItemId == ItemIds.Workbench &&
            recipe.RequiredTools?.Any(tool =>
                tool.Tag == ItemTag.Hammer) == true),
    "storage furniture must be built at the workbench with a hammer");
var stationCraftingWindow = new CraftingWindowState();
stationCraftingWindow.Open(ItemIds.Bloomery);
Require(
    stationCraftingWindow.VisibleRecipes().SequenceEqual(
        bloomeryRecipes) &&
    stationCraftingWindow.SelectedRecipe is null &&
    stationCraftingWindow.ScrollRow == 0,
    "opening a crafting station must show only that station's recipes with no implicit selection or stale scroll state");
stationCraftingWindow.Close();
var bronzeSickle = ItemCatalog.Get(ItemIds.BronzeSickle);
Require(
    PlayerInventory.BestSickle(
        [ItemIds.StoneAxe, ItemIds.BronzeSickle])?.Id ==
    ItemIds.BronzeSickle &&
    FarmingSkill.GatherSeconds(bronzeSickle) <
    FarmingSkill.GatherSeconds(null) &&
    FarmingSkill.BonusBerryCount(
        9, bronzeSickle, 0) == 1 &&
    FarmingSkill.BonusBerryCount(
        9, null, 0) == 0,
    "the bronze sickle must speed berry harvesting and enable bonus yield");
Require(
    new[]
    {
        ItemIds.BronzeAxe,
        ItemIds.BronzeSickle,
        ItemIds.Charcoal,
        ItemIds.FishBerryStew
    }.Select(ItemCatalog.Get)
        .All(item => item.HasTag(ItemTag.ProgressionSprite)) &&
    ItemCatalog.Get(ItemIds.BronzeAxe).SpriteCell == 0 &&
    ItemCatalog.Get(ItemIds.FishBerryStew).SpriteCell == 2 &&
    ItemCatalog.Get(ItemIds.Charcoal).SpriteCell == 3 &&
    ItemCatalog.Get(ItemIds.BronzeSickle).SpriteCell == 4,
    "new progression items must map to their authored atlas icons");
Require(
    new[]
    {
        ItemIds.BronzeAxe,
        ItemIds.BronzeSickle,
        ItemIds.CookingPot
    }.Select(itemId => CraftingSkill.Recipes.Single(recipe =>
        recipe.ResultItemId == itemId))
        .All(recipe =>
            recipe.RequiredStationItemId == ItemIds.SmithingAnvil &&
            recipe.RequiredTools?.Any(tool =>
                tool.Tag == ItemTag.Hammer) == true),
    "bronze tools and the cooking pot must follow the anvil-and-hammer recipe design");
Require(
    StewCookingService.HasIngredients(
        [ItemIds.RawRiverPerch, ItemIds.WildBerries]) &&
    StewCookingService.TryPrepare(
        [ItemIds.RawRiverPerch, ItemIds.WildBerries],
        out var stewInventory,
        out var stewFish,
        out var stewBerries) &&
    stewFish == ItemIds.RawRiverPerch &&
    stewBerries == ItemIds.WildBerries &&
    stewInventory.Count(item =>
        item == ItemIds.FishBerryStew) == 1 &&
    !StewCookingService.HasIngredients(
        [ItemIds.CookedRiverPerch, ItemIds.WildBerries]),
    "pot cooking must consume one raw fish and one raw berry item into stew");
var expiredLogFire = new WorldGroundObject(
    Guid.NewGuid(), ItemIds.Campfire, 4, 5,
    ItemIds.OakLogs, LitUntilGameSeconds: 10);
Require(
    CharcoalService.IsReady(expiredLogFire, 10) &&
    !CharcoalService.IsReady(expiredLogFire, 9) &&
    ItemCatalog.Get(ItemIds.Charcoal)
        .HasTag(ItemTag.MiningMaterial),
    "an expired log-fueled campfire must produce usable charcoal");
var ironBloomWithCharcoal = CraftingSkill.Recipes.Single(
    recipe => recipe.ResultItemId == ItemIds.IronBloom);
Require(
    CraftingSkill.Availability(
        ironBloomWithCharcoal,
        ironBloomWithCharcoal.RequiredLevel,
        [
            ItemIds.IronOre, ItemIds.IronOre, ItemIds.IronOre,
            ItemIds.Charcoal, ItemIds.Charcoal
        ]) == RecipeAvailability.Ready &&
    CraftingService.TryCraft(
        ironBloomWithCharcoal,
        ironBloomWithCharcoal.RequiredLevel,
        [
            ItemIds.IronOre, ItemIds.IronOre, ItemIds.IronOre,
            ItemIds.Coal, ItemIds.Charcoal
        ],
        out var charcoalSmelt) &&
    charcoalSmelt.Contains(ItemIds.IronBloom),
    "bloomery recipes must accept mined coal, charcoal, or a mixture");
var availabilityInventory = PlayerInventory.Normalize(
    [
        ItemIds.CopperOre, ItemIds.CopperOre, ItemIds.TinOre,
        ItemIds.IronOre, ItemIds.IronOre, ItemIds.IronOre,
        ItemIds.Coal, ItemIds.Coal, ItemIds.Sticks,
        ItemIds.StoneHammer
    ]);
_ = CraftingSkill.Availability(
    CraftingSkill.Recipes[0], SkillService.MaximumLevel,
    availabilityInventory);
var availabilityAllocationsBefore =
    GC.GetAllocatedBytesForCurrentThread();
var availabilityChecksum = 0;
for (var iteration = 0; iteration < 1_000; iteration++)
    for (var recipeIndex = 0;
         recipeIndex < CraftingSkill.Recipes.Count;
         recipeIndex++)
        availabilityChecksum += (int)CraftingSkill.Availability(
            CraftingSkill.Recipes[recipeIndex],
            SkillService.MaximumLevel,
            availabilityInventory);
var availabilityAllocated =
    GC.GetAllocatedBytesForCurrentThread() -
    availabilityAllocationsBefore;
Require(
    availabilityChecksum > 0 && availabilityAllocated <= 256,
    "render-time crafting availability checks must not clone or allocate inventory state");
foreach (var recipe in CraftingSkill.Recipes)
{
    var exactIngredients = new List<string?>();
    foreach (var ingredient in recipe.Ingredients)
        for (var count = 0; count < ingredient.Count; count++)
            exactIngredients.Add(ingredient.ItemId);
    foreach (var tool in recipe.RequiredTools ?? [])
    {
        var toolItem = ItemCatalog.All.First(item =>
            item.HasTag(tool.Tag));
        for (var count = 0; count < tool.Count; count++)
            exactIngredients.Add(toolItem.Id);
    }
    var exactInventory = PlayerInventory.Normalize(
        exactIngredients.ToArray());
    Require(
        CraftingSkill.Availability(
            recipe, recipe.RequiredLevel, exactInventory) ==
        RecipeAvailability.Ready &&
        CraftingService.TryCraft(
            recipe, recipe.RequiredLevel, exactInventory, out _),
        $"recipe {recipe.Id} must be craftable from its displayed ingredients and required tools");
}
Require(ItemCatalog.Get(ItemIds.StoneKnife) is var knifeDefinition &&
        knifeDefinition.SpriteCell == 3 &&
        knifeDefinition.HasTag(ItemTag.Tool) &&
        knifeDefinition.HasTag(ItemTag.Knife) &&
        knifeDefinition.HasTag(ItemTag.Weapon) &&
        knifeDefinition.HasTag(ItemTag.StoneToolSprite),
    "the stone knife must use the fourth stone-tool sprite and knife capability");
Require(ItemCatalog.Get(ItemIds.PlantFibres) is var fibreDefinition &&
        fibreDefinition.HasTag(ItemTag.NaturalMaterial) &&
        fibreDefinition.HasTag(ItemTag.FibreNetSprite) &&
        ItemCatalog.Get(ItemIds.PrimitiveFishingNet) is var netDefinition &&
        netDefinition.HasTag(ItemTag.Tool) &&
        netDefinition.HasTag(ItemTag.FishingNet) &&
        PlayerInventory.BestFishingNet(
            [ItemIds.PlantFibres, ItemIds.PrimitiveFishingNet])?.Id ==
        ItemIds.PrimitiveFishingNet,
    "fibres and the primitive fishing net must have distinct resource/tool behaviour");
Require(ItemCatalog.Get(ItemIds.Workbench) is var workbenchItem &&
        workbenchItem.HasTag(ItemTag.PlaceableObject) &&
        !workbenchItem.Droppable,
    "the packed workbench must be placeable once rather than droppable");
Require(PlayerInventory.TrySwap(
            ["axe", "logs", "oak_logs"], 0, 2,
            out var swappedInventory) &&
        swappedInventory[0] == "oak_logs" &&
        swappedInventory[1] == "logs" &&
        swappedInventory[2] == "axe",
    "dragging between occupied inventory slots must swap their items");
Require(PlayerInventory.TrySwap(
            swappedInventory, 0, 5, out var movedToEmptySlot) &&
        movedToEmptySlot[0] is null &&
        movedToEmptySlot[5] == "oak_logs",
    "inventory items must move into empty fixed slots without compacting");
var gameUi = new GameUiControlState();
gameUi.Layout(new(0, 0, 1280, 720));
var inventoryGridBottom =
    GameUiControlState.InventoryGridTop +
    GameUiControlState.InventoryRows *
    GameUiControlState.InventorySlotSize +
    (GameUiControlState.InventoryRows - 1) *
    GameUiControlState.InventoryRowGap;
Require(gameUi.Panel.Bounds.W > inventoryGridBottom,
    "the inventory panel must include padding beneath all seven grid rows");
Require(
    gameUi.QuestButton.Bounds.Z > gameUi.QuestButton.Bounds.W &&
    gameUi.CraftingButton.Bounds.Z >
    gameUi.CraftingButton.Bounds.W &&
    gameUi.QuestButton.Bounds.X +
    gameUi.QuestButton.Bounds.Z < gameUi.BuildButton.Bounds.X &&
    gameUi.BuildButton.Bounds.X + gameUi.BuildButton.Bounds.Z <
    gameUi.CraftingButton.Bounds.X &&
    gameUi.CraftingButton.Bounds.X + gameUi.CraftingButton.Bounds.Z <
    gameUi.CombatButton.Bounds.X,
    "the bottom toolbar must expose non-overlapping quest, build and crafting actions");
var questToolbarClicked = false;
var buildToolbarClicked = false;
gameUi.QuestButton.Clicked += () => questToolbarClicked = true;
gameUi.BuildButton.Clicked += () => buildToolbarClicked = true;
var skillsButtonCenter = new Vector2(
    gameUi.SkillsButton.Bounds.X +
    gameUi.SkillsButton.Bounds.Z * .5f,
    gameUi.SkillsButton.Bounds.Y +
    gameUi.SkillsButton.Bounds.W * .5f);
gameUi.UpdatePointer(skillsButtonCenter, leftDown: true);
gameUi.UpdatePointer(skillsButtonCenter, leftDown: false);
var questButtonCenter = new Vector2(
    gameUi.QuestButton.Bounds.X +
    gameUi.QuestButton.Bounds.Z * .5f,
    gameUi.QuestButton.Bounds.Y +
    gameUi.QuestButton.Bounds.W * .5f);
gameUi.UpdatePointer(questButtonCenter, leftDown: true);
gameUi.UpdatePointer(questButtonCenter, leftDown: false);
var buildButtonCenter = new Vector2(
    gameUi.BuildButton.Bounds.X + gameUi.BuildButton.Bounds.Z * .5f,
    gameUi.BuildButton.Bounds.Y + gameUi.BuildButton.Bounds.W * .5f);
gameUi.UpdatePointer(buildButtonCenter, leftDown: true);
gameUi.UpdatePointer(buildButtonCenter, leftDown: false);
Require(
    questToolbarClicked && buildToolbarClicked &&
    gameUi.ActivePanel == GameUiPanel.Skills,
    "quest and crafting actions must activate without closing the selected gameplay panel");
var skillBack = SkillPanelLayout.BackButtonBounds(gameUi.Panel.Bounds);
var skillTitle = SkillPanelLayout.TitleBounds(gameUi.Panel.Bounds);
var skillLevel = SkillPanelLayout.LevelCardBounds(gameUi.Panel.Bounds);
var skillProgress = SkillPanelLayout.ProgressBounds(gameUi.Panel.Bounds);
var skillInfo = SkillPanelLayout.InformationBounds(gameUi.Panel.Bounds);
var skillAction = SkillPanelLayout.ActionButtonBounds(gameUi.Panel.Bounds);
Require(skillBack.X + skillBack.Z < skillTitle.X &&
        skillLevel.Y > skillBack.Y + skillBack.W &&
        skillProgress.Y > skillLevel.Y + skillLevel.W &&
        skillInfo.Y > skillProgress.Y + skillProgress.W &&
        skillAction.Y > skillInfo.Y + skillInfo.W &&
        skillAction.Y + skillAction.W <
        gameUi.Panel.Bounds.Y + gameUi.Panel.Bounds.W,
    "the reusable skill detail layout must keep navigation, progress, information, and actions aligned without overlap");
var reusableInventory = new InventoryPanelState(
    gameUi.Panel.Bounds, [ItemIds.Logs],
    allowDragOutsideToGame: false);
var configurableInventory = new InventoryPanelState(
    new(0, 0, 420, 260),
    new string?[12],
    title: "Chest",
    columns: 6,
    rows: 2,
    quantities: Enumerable.Repeat(100, 12).ToArray());
Require(
    configurableInventory.Title == "Chest" &&
    configurableInventory.Capacity == 12 &&
    configurableInventory.QuantityAt(0) == 100 &&
    configurableInventory.SlotBounds(6).Y >
        configurableInventory.SlotBounds(0).Y,
    "the reusable inventory panel must support custom titles, dimensions, and stack quantities");
var stackingContainer = new ItemContainerState(
    new(
        Guid.NewGuid(), "Test bank", 2, 1,
        AllowStacking: true));
Require(
    stackingContainer.TryAdd(ItemIds.SlimeGel, 99) &&
    stackingContainer.TryAdd(ItemIds.SlimeGel) &&
    stackingContainer.Quantities[0] == 100,
    "stacking containers must merge equal item IDs and retain their quantity");
var playerBagContainer = PlayerInventory.CreateContainer();
Require(
    PlayerInventory.Capacity == 28 &&
    ItemCatalog.Get(ItemIds.SlimeGel).CanStack &&
    !ItemCatalog.Get(ItemIds.Logs).CanStack &&
    !ItemCatalog.Get(ItemIds.StoneAxe).CanStack &&
    playerBagContainer.TryAdd(ItemIds.SlimeGel, 50) &&
    playerBagContainer.UsedSlots == 1 &&
    playerBagContainer[0] is
    {
        ItemId: ItemIds.SlimeGel,
        Quantity: 50
    } &&
    playerBagContainer.TryAdd(ItemIds.Logs, 2) &&
    playerBagContainer.TryAdd(ItemIds.StoneAxe, 2) &&
    playerBagContainer.UsedSlots == 5 &&
    playerBagContainer[1]?.Quantity == 1 &&
    playerBagContainer[2]?.Quantity == 1 &&
    playerBagContainer[3]?.Quantity == 1 &&
    playerBagContainer[4]?.Quantity == 1,
    "the reusable 28-slot player container must stack only slime drops while ordinary resources and tools consume individual slots");
var atomicBag = new InventoryContainer(2);
Require(
    !atomicBag.TryAdd(ItemIds.StoneAxe, 3) &&
    atomicBag.UsedSlots == 0 &&
    atomicBag.TryAdd(ItemIds.StoneAxe, 2) &&
    !atomicBag.CanAdd(ItemIds.StoneAxe) &&
    atomicBag.TryTake(0, 1, out var takenAxe) &&
    takenAxe.ItemId == ItemIds.StoneAxe &&
    atomicBag.UsedSlots == 1,
    "inventory-container additions must be atomic and removals must preserve slot accounting");
var restoredStackingContainer = new ItemContainerState(
    stackingContainer.Definition,
    stackingContainer.Save());
Require(
    restoredStackingContainer.Definition.Id ==
        stackingContainer.Definition.Id &&
    restoredStackingContainer.Items[0] == ItemIds.SlimeGel &&
    restoredStackingContainer.Quantities[0] == 100,
    "container snapshots must reload quantities against their stable container ID");
var chestObject = new WorldGroundObject(
    Guid.NewGuid(), ItemIds.StorageChest, 8.5f, 9.5f);
var chestContainer = StorageContainerService.Open(chestObject);
Require(
    chestContainer.Definition.Id == chestObject.Id &&
    chestContainer.Definition.Title == "Wooden Chest" &&
    chestContainer.Definition.Capacity == 48 &&
    chestContainer.TryAdd(ItemIds.OakLogs, 25) &&
    chestContainer.Quantities.Count(quantity => quantity == 1) == 25,
    "a placed wooden chest must create a 48-slot container while ordinary logs remain unstacked");
var storedChest = StorageContainerService.Save(
    chestObject, chestContainer);
var reopenedChest = StorageContainerService.Open(storedChest);
Require(
    reopenedChest.Items[0] == ItemIds.OakLogs &&
    reopenedChest.Quantities.Count(quantity => quantity == 1) == 25 &&
    StorageContainerService.Definition(
        Guid.NewGuid(), ItemIds.StorageBarrel).Capacity == 40,
    "world storage snapshots must reopen by object ID while barrels retain their smaller layout");
var lootBagObject = LootBagService.Create(
    Guid.NewGuid(), new Vector2(4, 7),
    [new(ItemIds.SlimeGel, 3), new(ItemIds.SaltCrystals, 1)]);
var lootBagContainer = WorldItemContainerService.Open(lootBagObject);
var depositProbe = new string?[] { ItemIds.Logs };
Require(
    WorldItemContainerService.IsContainer(ItemIds.LootBag) &&
    lootBagContainer.Definition.Access == ItemContainerAccess.WithdrawOnly &&
    !lootBagContainer.Definition.AllowsDeposit &&
    !lootBagContainer.TryAdd(ItemIds.Logs) &&
    lootBagContainer.TransferAllFrom(depositProbe) == 0 &&
    depositProbe[0] == ItemIds.Logs &&
    lootBagContainer.Quantities.Sum() == 4,
    "loot bags must reuse persistent containers while rejecting every deposit path");
Require(
    lootBagContainer.TryTake(0, 3, out var firstLoot) &&
    firstLoot == ItemIds.SlimeGel &&
    lootBagContainer.TryTake(1, 1, out var secondLoot) &&
    secondLoot == ItemIds.SaltCrystals &&
    lootBagContainer.IsEmpty &&
    LootBagService.FadeOpacity(10, 10) == 1 &&
    LootBagService.FadeOpacity(10, 10 + LootBagService.FadeSeconds) == 0 &&
    LootBagService.FadeFinished(10, 10 + LootBagService.FadeSeconds),
    "taking the final loot item must empty the bag and drive its bounded fade lifecycle");
var lootReceipt = new LootReceiptItem[]
{
    new(ItemIds.SlimeGel, 3),
    new(ItemIds.SaltCrystals, 1)
};
Require(
    LootReceiptService.Summary(lootReceipt) ==
        "Looted 3\u00d7 slime gel and 1\u00d7 salt crystals." &&
    LootReceiptService.DiscoveryHint(ItemIds.SlimeGel) ==
        "Slime gel can replace rope in selected recipes." &&
    LootReceiptService.DiscoveryHint(ItemIds.SaltCrystals) ==
        "Salt crystals can preserve cooked fish." &&
    LootReceiptService.DiscoveryHint(ItemIds.Logs) is null,
    "loot receipts must summarize rewards and expose concise hints only for special combat materials");
var villagerTransferContainer = new ItemContainerState(
    new(
        Guid.NewGuid(), "Villager chest", 2, 1,
        AllowStacking: false));
var villagerTransferInventory = PlayerInventory.CreateStartingInventory();
villagerTransferInventory[0] = ItemIds.StoneAxe;
villagerTransferInventory[1] = ItemIds.Logs;
villagerTransferInventory[2] = ItemIds.Sticks;
var villagerDeposit = VillagerStorageTransfer.DepositAll(
    villagerTransferContainer,
    villagerTransferInventory,
    "mira");
Require(
    villagerDeposit.ItemsMoved == 2 &&
    villagerDeposit.Inventory[0] is null &&
    villagerDeposit.Inventory[1] is null &&
    villagerDeposit.Inventory[2] == ItemIds.Sticks &&
    villagerTransferContainer.OwnerIds.Take(2).All(value => value == "mira"),
    "villager deposits must move only what fits and preserve actor ownership");
Require(
    VillagerStorageTransfer.TryWithdrawFirst(
        villagerTransferContainer,
        villagerDeposit.Inventory,
        itemId => VillagerStorageTransfer.IsWorkItemForRole(
            VillagerWorkRole.Wood, itemId),
        out var villagerWithdrawInventory,
        out var withdrawnWorkItem) &&
    withdrawnWorkItem == ItemIds.StoneAxe &&
    PlayerInventory.BestAxe(villagerWithdrawInventory)?.Id ==
        ItemIds.StoneAxe &&
    villagerTransferContainer.Items.All(value => value != ItemIds.StoneAxe),
    "wood workers must withdraw a usable axe through the shared transfer path");
var occupiedVillagerInventory = Enumerable
    .Repeat<string?>(ItemIds.SmallRocks, PlayerInventory.Capacity)
    .ToArray();
var storedBeforeFailedWithdraw = villagerTransferContainer.Save();
Require(
    !VillagerStorageTransfer.TryWithdrawFirst(
        villagerTransferContainer,
        occupiedVillagerInventory,
        _ => true,
        out _,
        out _) &&
    villagerTransferContainer.Save().Items.SequenceEqual(
        storedBeforeFailedWithdraw.Items) &&
    villagerTransferContainer.Save().Quantities.SequenceEqual(
        storedBeforeFailedWithdraw.Quantities),
    "a failed villager withdrawal must leave full inventories and storage unchanged");
var retainedToolContainer = new ItemContainerState(
    new(
        Guid.NewGuid(), "Role chest", 2, 1,
        AllowStacking: false));
var retainedToolInventory = PlayerInventory.CreateStartingInventory();
retainedToolInventory[0] = ItemIds.StonePickaxe;
retainedToolInventory[1] = ItemIds.Coal;
var retainedToolDeposit = VillagerStorageTransfer.DepositAll(
    retainedToolContainer,
    retainedToolInventory,
    "rowan",
    itemId => VillagerStorageTransfer.IsWorkItemForRole(
        VillagerWorkRole.Exploration, itemId));
Require(
    retainedToolDeposit.ItemsMoved == 1 &&
    retainedToolDeposit.Inventory[0] == ItemIds.StonePickaxe &&
    retainedToolDeposit.Inventory[1] is null &&
    retainedToolContainer.Items.Contains(ItemIds.Coal),
    "villager deposits must retain tools required by the assigned work role");
var individualContainer = new ItemContainerState(
    new(
        Guid.NewGuid(), "Test chest", 2, 1,
        AllowStacking: false));
Require(
    individualContainer.TryAdd(ItemIds.Logs, 2) &&
    !individualContainer.TryAdd(ItemIds.Sticks) &&
    individualContainer.Quantities.SequenceEqual([1, 1]),
    "non-stacking containers must use one slot per unit and reject over-capacity transfers atomically");
var transferInventory =
    new string?[] { ItemIds.Logs, ItemIds.Sticks, ItemIds.Coal };
var limitedContainer = new ItemContainerState(
    new(
        Guid.NewGuid(), "Limited chest", 2, 1,
        AllowStacking: false));
Require(
    limitedContainer.TransferAllFrom(transferInventory) == 2 &&
    transferInventory[0] is null &&
    transferInventory[1] is null &&
    transferInventory[2] == ItemIds.Coal,
    "deposit-all must stop safely when a container fills and leave unmoved bag items intact");
var matchingInventory = new string?[]
{
    ItemIds.SlimeGel, ItemIds.Coal, ItemIds.SlimeGel,
    ItemIds.SlimeGel, ItemIds.Sticks
};
var quantityContainer = new ItemContainerState(
    new(
        Guid.NewGuid(), "Quantity bank", 2, 1,
        AllowStacking: true));
Require(
    quantityContainer.TransferMatchingFrom(
        matchingInventory, ItemIds.SlimeGel, 2) == 2 &&
    quantityContainer.Quantities[0] == 2 &&
    matchingInventory.Count(item => item == ItemIds.SlimeGel) == 1 &&
    quantityContainer.TryTake(
        0, 2, out var withdrawnItemId) &&
    withdrawnItemId == ItemIds.SlimeGel &&
    quantityContainer.Items[0] is null,
    "amount menus must deposit matching bag items and withdraw the requested stack quantity");
var allItemsContainer = ItemContainerState.CreateAllItemsTest();
Require(
    ItemCatalog.All.All(item =>
    {
        var slot = Array.IndexOf(allItemsContainer.Items, item.Id);
        return slot >= 0 && allItemsContainer.Quantities[slot] ==
            (item.CanStack ? 100 : 1);
    }) &&
    Enumerable.Range(
            0,
            allItemsContainer.Definition.Capacity -
            allItemsContainer.Definition.ColumnCount + 1)
        .Any(start => Enumerable.Range(
                start, allItemsContainer.Definition.ColumnCount)
            .All(allItemsContainer.IsSpacer)),
    "the developer item bank must contain one tool or placeable and 100 of every stackable catalog item with category spacing");
var itemBankWindow = ItemContainerWindowState.WindowBounds(
    new(0, 0, 1280, 720), allItemsContainer.Definition);
var itemBankPanel = ItemContainerWindowState.ContainerBounds(
    itemBankWindow, allItemsContainer.Definition);
var itemBankState = new ItemContainerWindowState();
itemBankState.Open(allItemsContainer);
itemBankState.LayoutRows(itemBankWindow);
Require(
    itemBankState.Rows.ScrollTrack.Visible &&
    itemBankState.Rows.VisibleRows <
        allItemsContainer.Definition.RowCount,
    "item containers must enable the shared row scrollbar when their grid exceeds the viewport");
itemBankState.Rows.ScrollToIndex(
    allItemsContainer.Definition.RowCount - 1);
var itemBankGrid = new InventoryPanelState(
    itemBankPanel,
    allItemsContainer.Items,
    columns: allItemsContainer.Definition.ColumnCount,
    rows: allItemsContainer.Definition.RowCount,
    quantities: allItemsContainer.Quantities,
    firstVisibleRow: itemBankState.Rows.FirstVisibleIndex,
    visibleRows: itemBankState.Rows.VisibleRows);
var finalBankSlot = itemBankGrid.SlotBounds(
    allItemsContainer.Definition.Capacity - 1);
Require(
    itemBankGrid.VisibleSlots.Contains(
        allItemsContainer.Definition.Capacity - 1) &&
    finalBankSlot.Y + finalBankSlot.W <=
        itemBankWindow.Y + itemBankWindow.W - 54,
    "scrolling to the final container row must keep its slots above the footer");
var clickThroughState = new ItemContainerWindowState();
clickThroughState.Open(
    allItemsContainer, leftDown: true);
var bagPanel = ItemContainerWindowState.PlayerInventoryBounds(
    itemBankWindow);
var bagSlot = new InventoryPanelState(
    bagPanel, new string?[PlayerInventory.Capacity]).SlotBounds(0);
var bagPointer = bagSlot.Xy + new Vector2(4, 4);
Require(
    clickThroughState.UpdatePointer(
        new(0, 0, 1280, 720),
        bagPointer,
        leftDown: true,
        rightDown: false).Type ==
        ItemContainerActionType.None,
    "the click that opens a container must not pass through into an overlapping bag slot");
clickThroughState.UpdatePointer(
    new(0, 0, 1280, 720),
    bagPointer,
    leftDown: false,
    rightDown: false);
Require(
    clickThroughState.UpdatePointer(
        new(0, 0, 1280, 720),
        bagPointer,
        leftDown: true,
        rightDown: false).Type ==
        ItemContainerActionType.DepositOne,
    "container slots must accept a fresh click after the opening press is released");
var offCenterPixels = new byte[4 * 4 * 4];
offCenterPixels[(0 * 4 + 0) * 4 + 3] = 255;
var centeredOpaqueSprite = SpritePixelLayout.CenterOpaquePixels(
    new SpriteFrame(4, 4, 0, 0, offCenterPixels),
    new(10, 20, 32, 32));
Require(
    centeredOpaqueSprite.X > 10 &&
    centeredOpaqueSprite.Y > 20,
    "item layout must center visible pixels instead of transparent cell padding");
var elevatedChunkBounds = WorldChunkProjection.TerrainBounds(
    [
        0, 500, 0, 0,
        100, -300, 0, 0
    ],
    stride: 4);
Require(
    elevatedChunkBounds.Y == -300 &&
    elevatedChunkBounds.W == 800 &&
    WorldChunkProjection.IsVisible(
        elevatedChunkBounds,
        new(0, 300),
        1,
        new(1280, 720)),
    "chunk visibility must use the complete elevated vertex bounds instead of a fixed flat height");
foreach (var height in new[] { 0f, 2.5f, 4f, 17f })
{
    var expectedMap = new Vector2(37.25f, -19.75f);
    var projectedMap = IsometricTerrainProjection.Project(
        expectedMap.X, expectedMap.Y, height);
    var unprojectedMap = IsometricTerrainProjection.Unproject(
        projectedMap, _ => height);
    Require(
        (unprojectedMap - expectedMap).LengthSquared < .000001f,
        "isometric click mapping must round-trip terrain at every world level height");
}
var logicalPointer = new Vector2(320, 180);
var nativePointer = SceneCoordinateMapper.ClientToScene(
    logicalPointer,
    new(1280, 720),
    new(1280, 720),
    new(1280, 720));
var fullscreenPointer = SceneCoordinateMapper.ClientToScene(
    new(480, 270),
    new(1920, 1080),
    new(1920, 1080),
    new(1280, 720));
var dpiScaledPointer = SceneCoordinateMapper.ClientToScene(
    new(240, 135),
    new(960, 540),
    new(1920, 1080),
    new(1280, 720));
var letterboxedPointer = SceneCoordinateMapper.ClientToScene(
    new(480, 330),
    new(1920, 1200),
    new(1920, 1200),
    new(1280, 720));
Require(
    (nativePointer - logicalPointer).LengthSquared < .000001f &&
    (fullscreenPointer - logicalPointer).LengthSquared < .000001f &&
    (dpiScaledPointer - logicalPointer).LengthSquared < .000001f &&
    (letterboxedPointer - logicalPointer).LengthSquared < .000001f,
    "world pointer mapping must remain invariant across fullscreen, DPI scaling, and letterboxing");
var craftingWindowBounds =
    CraftingWindowState.WindowBounds(new(0, 0, 1280, 720));
Require(
    UnlimitedZoomFogPolicy.Amount(
        true, true, false, true, .16f) > 0 &&
    UnlimitedZoomFogPolicy.Amount(
        true, true, true, true, .16f) == 0 &&
    UnlimitedZoomFogPolicy.Amount(
        true, true, false, true, .22f) == 0,
    "zoom-scaled world loading must disable only the extreme-zoom edge fog while normal unlimited zoom retains it");
Require(
    ZoomScaledWorldLoadingPolicy.Radius(false, .05f) == 5 &&
    ZoomScaledWorldLoadingPolicy.Radius(true, .1f) == 9 &&
    ZoomScaledWorldLoadingPolicy.Radius(true, .001f) == 32,
    "zoom-scaled loading and rendering must share the expanded radius with a bounded developer-mode ceiling");
var craftingButton =
    CraftingWindowState.CraftButtonBounds(craftingWindowBounds);
var craftingClose =
    CraftingWindowState.CloseBounds(craftingWindowBounds);
var scrollingCraftingWindow = new CraftingWindowState();
scrollingCraftingWindow.Open();
var recipeListBounds =
    CraftingWindowState.RecipeListBounds(craftingWindowBounds);
var visibleCraftingRecipesBefore =
    scrollingCraftingWindow.VisibleRecipeCount(craftingWindowBounds);
var firstCraftingRecipeBefore = visibleCraftingRecipesBefore > 0
    ? scrollingCraftingWindow.VisibleRecipeAt(craftingWindowBounds, 0)
    : null;
var craftingScrollHandled = scrollingCraftingWindow.Scroll(
    new(0, 0, 1280, 720),
    new(recipeListBounds.X + 10, recipeListBounds.Y + 10),
    -1);
Require(
    craftingScrollHandled &&
    (CraftingSkill.Recipes.Count <= visibleCraftingRecipesBefore ||
     scrollingCraftingWindow.ScrollRow == 1 &&
     scrollingCraftingWindow.VisibleRecipeAt(craftingWindowBounds, 0) !=
     firstCraftingRecipeBefore),
    "the crafting recipe grid must own bounded mouse-wheel scrolling and advance its visible recipe page when content overflows");
Require(
    craftingButton.X + craftingButton.Z <= craftingClose.X,
    "the reusable crafting action and close buttons must not overlap");
var craftingDetails =
    CraftingWindowState.DetailsBounds(craftingWindowBounds);
Require(
    craftingDetails.Contains(new Vector2(
        craftingButton.X, craftingButton.Y)) &&
    craftingButton.Y > craftingDetails.Y + 40,
    "the Craft button must live in the recipe details area instead of the window title");
var settingsMenu = new SettingsMenuState();
var visibleSettingsTabs = settingsMenu.VisibleTabs;
Require(
    visibleSettingsTabs.Contains(SettingsTab.Display) &&
    visibleSettingsTabs.Contains(SettingsTab.Game) &&
    visibleSettingsTabs.Contains(SettingsTab.Sound) &&
    visibleSettingsTabs.Contains(SettingsTab.AI),
    "the settings menu must expose Display, Game, and Sound tabs");
var volumeSlider = new SliderControlState();
var volumeCommitted = -1f;
volumeSlider.DragCompleted += value => volumeCommitted = value;
volumeSlider.Layout(new(100, 100, 400, 44));
var volumePointer = new Vector2(
    volumeSlider.TrackBounds.X +
    volumeSlider.TrackBounds.Z * .75f,
    volumeSlider.TrackBounds.Y + 4);
Require(
    volumeSlider.UpdatePointer(volumePointer, true) &&
    volumeSlider.Pressed &&
    MathF.Abs(volumeSlider.Value - .75f) < .001f &&
    volumeSlider.UpdatePointer(volumePointer, false) &&
    !volumeSlider.Pressed &&
    MathF.Abs(volumeCommitted - .75f) < .001f,
    "the reusable slider must drag, clamp, and commit its value");
if (!System.Diagnostics.Debugger.IsAttached)
    Require(!visibleSettingsTabs.Contains(SettingsTab.Dev),
        "the Dev settings tab must stay hidden without an attached debugger");
settingsMenu.EnableDeveloperMode();
Require(settingsMenu.DeveloperModeEnabled &&
        settingsMenu.VisibleTabs.Contains(SettingsTab.Dev),
    "the hidden chat command must be able to enable the Dev settings tab");
var settingsPanel = new Vector4(360, 80, 560, 560);
settingsMenu.SelectAt(
    settingsPanel,
    SettingsMenuState.TabBounds(
        settingsPanel,
        settingsMenu.VisibleTabs.Count - 1,
        settingsMenu.VisibleTabs.Count).Xy);
settingsMenu.LayoutContent(settingsPanel);
var settingsList = settingsMenu.ContentList;
Require(
    DeveloperSettingsController.MaxBounds(
        settingsList, SkillType.Woodcutting).Z <= 60 &&
    DeveloperSettingsController.MaxBounds(
        settingsList, SkillType.Woodcutting).X >
    DeveloperSettingsController.SkillRowBounds(
        settingsList, SkillType.Woodcutting).X + 80,
    "developer skill rows must reserve most of their width for icon-led skill information");
var settingsContent = SettingsMenuState.ContentBounds(settingsPanel);
var settingsBack = SettingsMenuState.BackButtonBounds(settingsPanel);
var tallerSettingsPanel = new Vector4(
    settingsPanel.X,
    settingsPanel.Y,
    settingsPanel.Z,
    settingsPanel.W + 120);
var tallerSettingsBack =
    SettingsMenuState.BackButtonBounds(tallerSettingsPanel);
Require(
    Math.Abs(
        tallerSettingsBack.Y - settingsBack.Y - 120) < .001f &&
    Math.Abs(
        tallerSettingsPanel.Y + tallerSettingsPanel.W -
        (tallerSettingsBack.Y + tallerSettingsBack.W) -
        (settingsPanel.Y + settingsPanel.W -
         settingsBack.Y - settingsBack.W)) < .001f,
    "the settings Back button must remain anchored to a resized panel footer");
Require(
    settingsList.ScrollTrack.Visible &&
    settingsList.Count ==
        DeveloperSettingsController.SkillStartIndex +
        DeveloperSettingsController.Skills.Length,
    "the developer page must use the shared scroll control for all tool and skill rows");
Require(
    !DeveloperSettingsController.MapToolBounds(settingsList)
        .Contains(settingsBack.Xy),
    "the developer map-tool button must not overlap settings navigation");
Require(
    DeveloperSettingsController.MapToolBounds(settingsList).X +
        DeveloperSettingsController.MapToolBounds(settingsList).Z <=
    DeveloperSettingsController.ItemBankBounds(settingsList).X &&
    DeveloperSettingsController.AdvanceTimeBounds(settingsList).X +
        DeveloperSettingsController.AdvanceTimeBounds(settingsList).Z <=
    DeveloperSettingsController.WorldLevelBounds(settingsList).X &&
    !DeveloperSettingsController.AdvanceTimeBounds(settingsList)
        .Contains(settingsBack.Xy),
    "developer tools must form non-overlapping two-column rows above the skill list");
settingsList.ScrollToIndex(
    DeveloperSettingsController.NavigationBlocksIndex);
settingsMenu.LayoutContent(settingsPanel);
Require(
    settingsList.VisibleIndices.Contains(
        DeveloperSettingsController.NavigationBlocksIndex) &&
    !DeveloperSettingsController.NavigationBlocksBounds(settingsList)
        .Contains(settingsBack.Xy),
    "developer diagnostics must remain inside the scrolling content area");
var toggleControl = new ToggleControlState(
    "Pathing blocks", "Draw navigation blockers.");
toggleControl.Layout(
    DeveloperSettingsController.NavigationBlocksBounds(settingsList),
    horizontalInset: 0);
Require(
    toggleControl.Bounds.X ==
        DeveloperSettingsController.NavigationBlocksBounds(settingsList).X &&
    toggleControl.Bounds.Z ==
        DeveloperSettingsController.NavigationBlocksBounds(settingsList).Z &&
    !toggleControl.IsChecked &&
    toggleControl.ToggleAt(
        toggleControl.Bounds.Xy +
        new Vector2(toggleControl.Bounds.Z * .5f,
                    toggleControl.Bounds.W * .5f)) &&
    toggleControl.IsChecked &&
    !toggleControl.ToggleAt(settingsBack.Xy),
    "the reusable toggle control must change only from an enabled hit inside its bounds");
var npcCountDropdown = new DropdownControlState();
var npcCountOptions = new[]
{
    new DropdownOption("0", "0 — Solo"),
    new DropdownOption("1", "1 survivor"),
    new DropdownOption("2", "2 survivors"),
    new DropdownOption("3", "3 survivors")
};
npcCountDropdown.Layout(
    new(100, 100, 140, 42),
    npcCountOptions,
    new(0, 0, 400, 300));
npcCountDropdown.Toggle();
Require(
    npcCountDropdown.IsOpen &&
    npcCountDropdown.VisibleCount == 4 &&
    npcCountDropdown.TrySelect(
        npcCountDropdown.OptionBounds(3).Xy +
        new Vector2(8, 8),
        out var selectedNpcCount) &&
    selectedNpcCount.Id == "3" &&
    !npcCountDropdown.IsOpen,
    "the AI NPC count dropdown must expose and select all supported 0-3 population choices");
var developerMap = new DeveloperMapWindow();
developerMap.Open();
Require(developerMap.IsOpen,
    "the in-game developer map must track its open state");
developerMap.ToggleTreeDensity();
Require(developerMap.Layer == WorldAtlasLayer.TreeDensity,
    "the developer map must expose a tree-density layer");
developerMap.ToggleTreeDensity();
Require(developerMap.Layer == WorldAtlasLayer.Terrain,
    "the developer map must toggle back to terrain");
var developerFallback = Enumerable.Range(-160, 321)
    .SelectMany(y => Enumerable.Range(-160, 321)
        .Select(x => new Vector2(x + .5f, y + .5f)))
    .First(position =>
        InfiniteWorldGenerator.BiomeAt(
            2187,
            (int)MathF.Floor(position.X),
            (int)MathF.Floor(position.Y)) is not
            (Biome.DeepWater or Biome.ShallowWater or
             Biome.RiverWater or Biome.MangroveShallows));
const long navigationSeed = 2187;
var shorelineWater = Enumerable.Range(-160, 321)
    .SelectMany(y => Enumerable.Range(-160, 321)
        .Select(x => new Vector2(x + .5f, y + .5f)))
    .First(position =>
        !WorldLevelNavigation.IsWalkable(
            navigationSeed,
            (int)MathF.Floor(position.X),
            (int)MathF.Floor(position.Y),
            (int)WorldLevel.Overworld) &&
        Enumerable.Range(-2, 5).Any(offsetY =>
            Enumerable.Range(-2, 5).Any(offsetX =>
                WorldLevelNavigation.IsWalkable(
                    navigationSeed,
                    (int)MathF.Floor(position.X) + offsetX,
                    (int)MathF.Floor(position.Y) + offsetY,
                    (int)WorldLevel.Overworld))));
var resolvedShoreline = WorldLevelNavigation.NearestWalkable(
    navigationSeed,
    shorelineWater,
    developerFallback,
    (int)WorldLevel.Overworld,
    maximumRadius: 2);
var nearestShoreDistance = Enumerable.Range(-2, 5)
    .SelectMany(offsetY => Enumerable.Range(-2, 5)
        .Select(offsetX => new Vector2(
            MathF.Floor(shorelineWater.X) + offsetX + .5f,
            MathF.Floor(shorelineWater.Y) + offsetY + .5f)))
    .Where(candidate => WorldLevelNavigation.IsWalkable(
        navigationSeed,
        (int)MathF.Floor(candidate.X),
        (int)MathF.Floor(candidate.Y),
        (int)WorldLevel.Overworld))
    .Min(candidate =>
        Vector2.DistanceSquared(shorelineWater, candidate));
Require(
    MathF.Abs(
        Vector2.DistanceSquared(
            shorelineWater, resolvedShoreline) -
        nearestShoreDistance) < .0001f &&
    resolvedShoreline ==
    WorldLevelNavigation.NearestWalkable(
        navigationSeed,
        shorelineWater,
        developerFallback,
        (int)WorldLevel.Overworld,
        maximumRadius: 2),
    "shoreline fallback must choose a genuinely nearest tile with a stable tie-break, not a north-first scan");
var developerDestination = DeveloperMapWindow.ResolveDestination(
    Vector2.Zero, Vector2.Zero, Vector2.Zero, 1, 2187,
    developerFallback);
Require(
    InfiniteWorldGenerator.BiomeAt(
        2187,
        (int)MathF.Floor(developerDestination.X),
        (int)MathF.Floor(developerDestination.Y)) is not
        (Biome.DeepWater or Biome.ShallowWater or
         Biome.RiverWater or Biome.MangroveShallows),
    "developer-map teleport destinations must resolve onto walkable land");
var undergroundDeveloperDestination =
    DeveloperMapWindow.ResolveDestination(
        Vector2.Zero,
        Vector2.Zero,
        Vector2.Zero,
        1,
        2187,
        developerFallback,
        (int)WorldLevel.Underground);
Require(
    CaveHydrologyField.Density(
        2187,
        undergroundDeveloperDestination.X,
        undergroundDeveloperDestination.Y) >=
    CaveHydrologyField.Boundary,
    "underground developer-map teleports must resolve onto cave floor");
developerMap.Close();
Require(!developerMap.IsOpen,
    "the in-game developer map must close cleanly");
var atlasRiverPixels = new byte[7 * 7 * 4];
var atlasRiverMask = new bool[7 * 7];
var atlasRiverLand = Enumerable.Repeat(true, 7 * 7).ToArray();
atlasRiverMask[1 * 7 + 1] = true;
atlasRiverMask[3 * 7 + 3] = true;
atlasRiverMask[5 * 7 + 5] = true;
WorldAtlasGenerator.SmoothRiverContinuity(
    atlasRiverPixels, atlasRiverMask, atlasRiverLand, 7);
Require(
    atlasRiverMask[2 * 7 + 2] &&
    atlasRiverMask[4 * 7 + 4] &&
    atlasRiverPixels[(2 * 7 + 2) * 4 + 2] > 0,
    "the atlas must bridge short sampling gaps in diagonal river channels");
Require(
    settingsContent.Y + settingsContent.W < settingsBack.Y &&
    settingsBack.X + settingsBack.Z <=
    settingsPanel.X + settingsPanel.Z - 20 &&
    settingsBack.Y + settingsBack.W <=
    settingsPanel.Y + settingsPanel.W - 20,
    "the settings Back button must sit inside a separate aligned footer without overlapping content");
var inventoryInteraction = new InventoryInteractionController();
var firstSlotCenter = new Vector2(
    reusableInventory.SlotBounds(0).X + 16,
    reusableInventory.SlotBounds(0).Y + 16);
inventoryInteraction.Update(
    reusableInventory, firstSlotCenter,
    leftDown: true, rightDown: false);
inventoryInteraction.Update(
    reusableInventory, firstSlotCenter + new Vector2(8, 0),
    leftDown: true, rightDown: false);
Require(!inventoryInteraction.AllowsCurrentDragOutsideToGame,
    "an embedded inventory drag must not enable the world-drop cursor");
var containedDrag = inventoryInteraction.Update(
    reusableInventory, Vector2.Zero,
    leftDown: false, rightDown: false);
Require(containedDrag.Type == InventoryInteractionType.None,
    "an embedded inventory must reject dragging items outside to the game");
var worldDropInventory = new InventoryPanelState(
    gameUi.Panel.Bounds, [ItemIds.Logs],
    allowDragOutsideToGame: true);
inventoryInteraction.Update(
    worldDropInventory, firstSlotCenter,
    leftDown: true, rightDown: false);
inventoryInteraction.Update(
    worldDropInventory, firstSlotCenter + new Vector2(8, 0),
    leftDown: true, rightDown: false);
Require(inventoryInteraction.AllowsCurrentDragOutsideToGame,
    "an opted-in inventory drag must enable the world-drop cursor");
var outsideDrag = inventoryInteraction.Update(
    worldDropInventory, Vector2.Zero,
    leftDown: false, rightDown: false);
Require(outsideDrag.Type ==
        InventoryInteractionType.DropOutsideToGame &&
        outsideDrag.SourceSlot == 0,
    "the normal inventory must opt into dragging items into the game world");

string?[] inventory = [];
for (var slot = 0; slot < PlayerInventory.Capacity; slot++)
    Require(PlayerInventory.TryAdd(inventory, "logs", out inventory),
        $"inventory slot {slot + 1} must accept an item");
Require(PlayerInventory.Count(inventory) == 28 &&
        PlayerInventory.IsFull(inventory) &&
        !PlayerInventory.TryAdd(inventory, "logs", out var unchanged) &&
        unchanged.Length == 28,
    "inventory must have exactly 28 non-stacking slots");
Require(PlayerInventory.TryBreakRock(
        [ItemIds.LargeRock, ItemIds.LargeRock],
        0, 1, out var splitLarge) &&
        splitLarge.Count(item => item == ItemIds.MediumRock) == 2 &&
        splitLarge[0] == ItemIds.LargeRock,
    "a large rock tool must split another large rock into two medium rocks");
Require(PlayerInventory.TryBreakRock(
        [ItemIds.LargeRock, ItemIds.MediumRock],
        0, 1, out var splitMedium) &&
        splitMedium.Count(item => item == ItemIds.SmallRocks) == 2,
    "a large rock tool must split a medium rock into two pebble items");
Require(PlayerInventory.TryBreakRock(
        [ItemIds.StoneHammer, ItemIds.LargeRock],
        0, 1, out var hammerSplit) &&
        hammerSplit[0] == ItemIds.StoneHammer &&
        hammerSplit.Count(item => item == ItemIds.MediumRock) == 2,
    "a stone hammer must split rocks without being consumed");
Require(!PlayerInventory.TryBreakRock(
        Enumerable.Repeat<string?>(ItemIds.LargeRock, PlayerInventory.Capacity)
            .ToArray(),
        0, 1, out _),
    "rock splitting must require an empty inventory slot");
Require(PlayerInventory.TrySharpenRock(
        [ItemIds.MediumRock, ItemIds.MediumRock],
        0, 1, out var sharpenedRock) &&
        sharpenedRock[0] is null &&
        sharpenedRock[1] == ItemIds.SharpenedRock &&
        PlayerInventory.Count(sharpenedRock) == 1,
    "using a medium rock on another must consume both and create a sharp rock");
Require(!PlayerInventory.TrySharpenRock(
        [ItemIds.MediumRock, ItemIds.LargeRock],
        0, 1, out _),
    "creating a sharp rock must require two medium rocks");
Require(PlayerInventory.TryCraftStoneAxe(
        [ItemIds.SharpenedRock, ItemIds.Sticks],
        0, 1, out var craftedAxe) &&
        craftedAxe[0] is null &&
        craftedAxe[1] == ItemIds.StoneAxe &&
        ItemCatalog.Get(craftedAxe[1]!).HasTag(ItemTag.Axe) &&
        PlayerInventory.Count(craftedAxe) == 1,
    "using a sharp rock on sticks must consume both and create a stone axe");
Require(!PlayerInventory.TryCraftStoneAxe(
        [ItemIds.SharpenedRock, ItemIds.Logs],
        0, 1, out _),
    "crafting an axe must require sticks");
Require(PlayerInventory.TryCraftStoneKnife(
        [ItemIds.SharpenedRock, ItemIds.PlantFibres],
        0, 1, out var craftedKnife) &&
        craftedKnife[0] is null &&
        craftedKnife[1] == ItemIds.StoneKnife &&
        PlayerInventory.Count(craftedKnife) == 1 &&
        PlayerInventory.TryCraftStoneKnife(
            [ItemIds.PlantFibres, ItemIds.SharpenedRock],
            0, 1, out var reverseCraftedKnife) &&
        reverseCraftedKnife[1] == ItemIds.StoneKnife,
    "using fibre and a sharp rock in either order must create a stone knife");
Require(!PlayerInventory.TryCraftStoneKnife(
        [ItemIds.SharpenedRock, ItemIds.Sticks],
        0, 1, out _),
    "crafting a stone knife must require plant fibre");
Require(PlayerInventory.TryCraftStoneHammer(
        [ItemIds.MediumRock, ItemIds.Sticks],
        0, 1, out var craftedHammer) &&
        craftedHammer[0] is null &&
        craftedHammer[1] == ItemIds.StoneHammer &&
        ItemCatalog.Get(craftedHammer[1]!).HasTag(ItemTag.Tool) &&
        !ItemCatalog.Get(craftedHammer[1]!).HasTag(ItemTag.Axe),
    "using a medium rock on sticks must consume both and create a stone hammer");
Require(!PlayerInventory.TryCraftStoneHammer(
        [ItemIds.MediumRock, ItemIds.Logs],
        0, 1, out _),
    "crafting a stone hammer must require sticks");
Require(PlayerInventory.TryBluntStoneTool(
        [ItemIds.StoneAxe], ItemIds.StoneAxe, .009f,
        out var bluntAxe) &&
        bluntAxe[0] == ItemIds.BluntStoneAxe &&
        !PlayerInventory.HasAxe(bluntAxe) &&
        PlayerInventory.HasAnyAxe(bluntAxe),
    "a stone axe must become unusably blunt on the one-percent roll");
Require(!PlayerInventory.TryBluntStoneTool(
        [ItemIds.StoneHammer], ItemIds.StoneHammer, .01f,
        out var unchangedHammer) &&
        unchangedHammer[0] == ItemIds.StoneHammer,
    "the stone-tool blunt chance must be exactly one percent");
Require(PlayerInventory.TrySharpenStoneTool(
        [ItemIds.SmallRocks, ItemIds.BluntStoneAxe],
        0, 1, out var resharpenedAxe) &&
        resharpenedAxe[0] is null &&
        resharpenedAxe[1] == ItemIds.StoneAxe &&
        PlayerInventory.HasAxe(resharpenedAxe),
    "using small rocks on a blunt stone axe must consume them and restore it");
Require(PlayerInventory.TrySharpenStoneTool(
        [ItemIds.SmallRocks, ItemIds.BluntStoneHammer],
        0, 1, out var resharpenedHammer) &&
        resharpenedHammer[0] is null &&
        resharpenedHammer[1] == ItemIds.StoneHammer,
    "using small rocks on a blunt stone hammer must restore it");
Require(
    EntityInteractionService.TryAutoSharpenStoneTool(
        [ItemIds.BluntStoneAxe, ItemIds.SmallRocks, ItemIds.Sticks],
        ItemIds.BluntStoneAxe,
        out var autoSharpenedAxe) &&
    autoSharpenedAxe[0] == ItemIds.StoneAxe &&
    autoSharpenedAxe[1] is null &&
    !EntityInteractionService.TryAutoSharpenStoneTool(
        [ItemIds.BluntStoneAxe, ItemIds.Sticks],
        ItemIds.BluntStoneAxe,
        out _),
    "entity actions must automatically sharpen a blunt stone axe only when small rocks are carried");
Require(PlayerInventory.BestAxe([ItemIds.StoneAxe])?.Id ==
            ItemIds.StoneAxe &&
        PlayerInventory.BestAxe(
            [ItemIds.StoneAxe, ItemIds.IronAxe])?.Id ==
            ItemIds.IronAxe,
    "woodcutting must inspect every tool axe and choose the highest-power one");
var stoneAxeRecipe = CraftingSkill.Recipes.First(
    recipe => recipe.Id == "stone-axe");
Require(CraftingService.TryCraft(
        stoneAxeRecipe,
        stoneAxeRecipe.RequiredLevel,
        [ItemIds.SharpenedRock, ItemIds.Sticks],
        out var menuCraftedAxe) &&
        menuCraftedAxe.Count(item => item == ItemIds.StoneAxe) == 1 &&
        !menuCraftedAxe.Contains(ItemIds.SharpenedRock) &&
        !menuCraftedAxe.Contains(ItemIds.Sticks),
    "recipe crafting must consume its ingredients and add its result");
Require(!CraftingService.TryCraft(
        stoneAxeRecipe,
        stoneAxeRecipe.RequiredLevel,
        [ItemIds.SharpenedRock],
        out _),
    "recipe crafting must fail when any ingredient is missing");
var mediumRockRecipe = CraftingSkill.Recipes.First(
    recipe => recipe.Id == "medium-rock");
Require(
    CraftingSkill.Outputs(mediumRockRecipe).Count == 2 &&
    CraftingSkill.Outputs(mediumRockRecipe).Any(output =>
        output.ItemId == ItemIds.LargeRock &&
        output.Count == 1 &&
        CraftingSkill.IsReturnedIngredient(
            mediumRockRecipe, output.ItemId)) &&
    CraftingSkill.Outputs(mediumRockRecipe).Any(output =>
        output.ItemId == ItemIds.MediumRock && output.Count == 2) &&
    CraftingSkill.Outputs(ropeRecipe).Single().ItemId == ItemIds.Rope,
    "crafting output summaries must expose returned tools and final products");
Require(CraftingService.TryCraft(
        mediumRockRecipe,
        1,
        [ItemIds.LargeRock, ItemIds.LargeRock],
        out var craftedMediumRocks) &&
        craftedMediumRocks.Count(
            item => item == ItemIds.LargeRock) == 1 &&
        craftedMediumRocks.Count(
            item => item == ItemIds.MediumRock) == 2,
    "the level-one medium-rock recipe must retain its striking rock and produce two medium rocks");
var smallRockRecipe = CraftingSkill.Recipes.First(
    recipe => recipe.Id == "small-rocks");
Require(CraftingService.TryCraft(
        smallRockRecipe,
        1,
        [ItemIds.MediumRock, ItemIds.MediumRock],
        out var craftedSmallRocks) &&
        craftedSmallRocks.Count(
            item => item == ItemIds.MediumRock) == 1 &&
        craftedSmallRocks.Count(
            item => item == ItemIds.SmallRocks) == 2,
    "the level-one small-rock recipe must retain its striking rock and produce two small-rock items");
var stonePickaxeRecipe = CraftingSkill.Recipes.First(
    recipe => recipe.Id == "stone-pickaxe");
Require(CraftingService.TryCraft(
        stonePickaxeRecipe,
        stonePickaxeRecipe.RequiredLevel,
        [
            ItemIds.SharpenedRock,
            ItemIds.MediumRock,
            ItemIds.Sticks
        ],
        out var menuCraftedPickaxe) &&
        menuCraftedPickaxe.Count(
            item => item == ItemIds.StonePickaxe) == 1 &&
        !menuCraftedPickaxe.Contains("stone_pickaxe_head"),
    "stone pickaxe crafting must consume its temporary head during the next inventory step");
var bronzeBarRecipe = CraftingSkill.Recipes.First(
    recipe => recipe.Id == "bronze-bar");
var bronzePickaxeRecipe = CraftingSkill.Recipes.First(
    recipe => recipe.Id == "bronze-pickaxe");
var bronzeBarIngredients = PlayerInventory.Normalize(
    [
        ItemIds.CopperOre, ItemIds.CopperOre,
        ItemIds.TinOre, ItemIds.Coal
    ]);
Require(
    CraftingSkill.Availability(
        bronzeBarRecipe, bronzeBarRecipe.RequiredLevel,
        bronzeBarIngredients,
        requiredStationAvailable: false) ==
    RecipeAvailability.MissingStation &&
    CraftingService.TryCraftDetailed(
        bronzeBarRecipe, bronzeBarRecipe.RequiredLevel,
        bronzeBarIngredients,
        out var blockedBronzeSmelt,
        requiredStationAvailable: false) ==
    CraftingService.CraftResult.MissingStation &&
    blockedBronzeSmelt.SequenceEqual(bronzeBarIngredients),
    "bronze cannot be smelted without a nearby placed bloomery");
Require(
    CraftingService.TryCraft(
        bronzeBarRecipe, bronzeBarRecipe.RequiredLevel,
        [
            ItemIds.CopperOre, ItemIds.CopperOre,
            ItemIds.TinOre, ItemIds.Coal,
            ItemIds.Sticks, ItemIds.StoneHammer
        ],
        out var castBronze) &&
    CraftingService.TryCraft(
        bronzePickaxeRecipe, bronzePickaxeRecipe.RequiredLevel,
        castBronze, out var forgedBronzePickaxe) &&
    forgedBronzePickaxe.Contains(ItemIds.BronzePickaxe) &&
    forgedBronzePickaxe.Contains(ItemIds.StoneHammer) &&
    !forgedBronzePickaxe.Contains(ItemIds.BronzeBar),
    "bronze ore must cast into a bar and then forge into a pickaxe without consuming the hammer");
var ironBloomRecipe = CraftingSkill.Recipes.First(
    recipe => recipe.Id == "iron-bloom");
var ironBarRecipe = CraftingSkill.Recipes.First(
    recipe => recipe.Id == "iron-bar");
var ironPickaxeRecipe = CraftingSkill.Recipes.First(
    recipe => recipe.Id == "iron-pickaxe");
Require(
    CraftingService.TryCraft(
        ironBloomRecipe, ironBloomRecipe.RequiredLevel,
        [
            ItemIds.IronOre, ItemIds.IronOre, ItemIds.IronOre,
            ItemIds.Coal, ItemIds.Coal, ItemIds.Coal,
            ItemIds.Sticks, ItemIds.StoneHammer
        ],
        out var smeltedBloom) &&
    CraftingService.TryCraft(
        ironBarRecipe, ironBarRecipe.RequiredLevel,
        smeltedBloom, out var forgedIronBar) &&
    CraftingService.TryCraft(
        ironPickaxeRecipe, ironPickaxeRecipe.RequiredLevel,
        forgedIronBar, out var forgedIronPickaxe) &&
    forgedIronPickaxe.Contains(ItemIds.IronPickaxe) &&
    forgedIronPickaxe.Contains(ItemIds.StoneHammer) &&
    !forgedIronPickaxe.Contains(ItemIds.IronBloom) &&
    !forgedIronPickaxe.Contains(ItemIds.IronBar),
    "bloomery iron must be consolidated into a bar and then forged into a pickaxe without consuming the hammer");
var overflowingRecipe = new CraftingRecipe(
    "overflow-test",
    ItemIds.StonePickaxe,
    CraftingCategory.Tools,
    1,
    0,
    [new(ItemIds.Sticks, 1)],
    ["Test inventory capacity."],
    [
        new(
            [new(ItemIds.Sticks, 1)],
            [new(ItemIds.StonePickaxe, PlayerInventory.Capacity + 1)])
    ]);
var inventoryBeforeOverflow = PlayerInventory.Normalize([ItemIds.Sticks]);
Require(CraftingService.TryCraftDetailed(
        overflowingRecipe,
        1,
        inventoryBeforeOverflow,
        out var inventoryAfterOverflow) ==
        CraftingService.CraftResult.InventoryFull &&
        inventoryAfterOverflow.SequenceEqual(inventoryBeforeOverflow),
    "crafting must check every step's outputs and leave inventory unchanged when a step has insufficient space");
var stoneAxeStrike = WoodcuttingSkill.Roll(0, 0, 0, 1);
var ironAxeStrike = WoodcuttingSkill.Roll(0, 0, 0, 2);
Require(ironAxeStrike.Damage > stoneAxeStrike.Damage,
    "an axe's woodcutting power must improve its chopping damage");
Require(
    Enum.GetValues<SkillType>().Length == 12 &&
    SkillService.LevelForExperience(
        SkillService.ExperienceForLevel(10)) == 10 &&
    WoodcuttingSkill.ExperienceForLevel(10) ==
    FarmingSkill.ExperienceForLevel(10) &&
    FarmingSkill.ExperienceForLevel(10) ==
    CraftingSkill.ExperienceForLevel(10) &&
    CraftingSkill.ExperienceForLevel(10) ==
    FishingSkill.ExperienceForLevel(10) &&
    FishingSkill.ExperienceForLevel(10) ==
    CookingSkill.ExperienceForLevel(10) &&
    CookingSkill.ExperienceForLevel(10) ==
    FiremakingSkill.ExperienceForLevel(10) &&
    FiremakingSkill.ExperienceForLevel(10) ==
    DiggingSkill.ExperienceForLevel(10) &&
    DiggingSkill.ExperienceForLevel(10) ==
    MiningSkill.ExperienceForLevel(10),
    "all registered skills must reuse the shared level and experience progression service");
Require(
    FishingSkill.CanCatch(WorldFishSpecies.ShoreMinnows, 1) &&
    FishingSkill.CanCatch(WorldFishSpecies.RiverPerch, 1) &&
    !FishingSkill.CanCatch(WorldFishSpecies.BluefinTuna, 16) &&
    FishingSkill.CanCatch(WorldFishSpecies.BluefinTuna, 17),
    "fishing progression must unlock difficult catches without changing the authored net action");
Require(
    FishingSkill.CanCatch(WorldFishSpecies.ShoreMinnows, 1, 1) &&
    !FishingSkill.CanCatch(WorldFishSpecies.SilverHerring, 5, 1) &&
    FishingSkill.CanCatch(WorldFishSpecies.SilverHerring, 5, 2) &&
    !FishingSkill.CanCatch(WorldFishSpecies.BluefinTuna, 17, 2) &&
    FishingSkill.CanCatch(WorldFishSpecies.BluefinTuna, 17, 3),
    "fishing net tiers must support primitive, coastal, and ocean catches in order");
Require(
    FishingSkill.CycleSeconds(3f, 3) <
    FishingSkill.CycleSeconds(3f, 2) &&
    FishingSkill.CycleSeconds(3f, 2) <
    FishingSkill.CycleSeconds(3f, 1) &&
    Math.Abs(FishingSkill.CycleSeconds(3f, 1) - 3f) < .001f,
    "stronger fishing nets must shorten catch cycles without changing primitive-net timing");
var fishingNetProgression = new[]
{
    ItemIds.PrimitiveFishingNet,
    ItemIds.ReinforcedFishingNet,
    ItemIds.AdvancedFishingNet
}.Select(ItemCatalog.Get).ToArray();
Require(
    fishingNetProgression.Select(item => item.FishingPower)
        .SequenceEqual([1, 2, 3]) &&
    PlayerInventory.BestFishingNet(
        [ItemIds.PrimitiveFishingNet, ItemIds.AdvancedFishingNet])?.Id ==
        ItemIds.AdvancedFishingNet &&
    CraftingSkill.Recipes.Any(recipe =>
        recipe.ResultItemId == ItemIds.ReinforcedFishingNet) &&
    CraftingSkill.Recipes.Any(recipe =>
        recipe.ResultItemId == ItemIds.AdvancedFishingNet),
    "net definitions, selection, and recipes must expose the full fishing progression");
var fishingGuide = SkillGuideService.Definition(SkillType.Fishing);
Require(
    fishingGuide.Entries.Select(entry => entry.Level)
        .SequenceEqual([1, 5, 9, 13, 17]) &&
    fishingGuide.Entries.Single(entry => entry.Level == 1)
        .Description.Contains("shore minnows") &&
    fishingGuide.Entries.Single(entry => entry.Level == 17)
        .Description.Contains("bluefin tuna"),
    "the fishing guide must show only meaningful catch-unlock levels derived from fishing profiles");
var cookingGuide = SkillGuideService.Definition(SkillType.Cooking);
Require(
    CookingSkill.CookProfiles.Select(profile => profile.RequiredLevel)
        .SequenceEqual([1, 1, 1, 3, 5, 9, 13, 17]) &&
    cookingGuide.Entries.Select(entry => entry.Level)
        .SequenceEqual([1, 3, 5, 9, 13, 17]) &&
    cookingGuide.Entries.Single(entry => entry.Level == 1)
        .Description.Contains("raw minnows") &&
    cookingGuide.Entries.Single(entry => entry.Level == 5)
        .Description.Contains("fish and berry stew") &&
    cookingGuide.Entries.Single(entry => entry.Level == 17)
        .Description.Contains("raw bluefin tuna"),
    "cooking unlocks must connect forage rewards and fish while omitting levels without a new recipe");
var firemakingGuide =
    SkillGuideService.Definition(SkillType.Firemaking);
Require(
    firemakingGuide.Entries[0].Description.Contains("charcoal") &&
    FarmingSkill.ExperienceMessage(18) ==
        "+18 Farming XP." &&
    FarmingSkill.LevelUpMessage(9) ==
        "Your Farming level is now 9.",
    "skill guides and feedback must explain the new cross-skill progression");
var roastedBerries = CookingSkill.Roll(
    ItemIds.WildBerries, 1, .99f);
Require(
    roastedBerries.ItemId == ItemIds.RoastedWildBerries &&
    !roastedBerries.Burnt &&
    CookingSkill.CanCook(ItemIds.TropicalBerries, 3),
    "foraged berries must connect to the reusable campfire cooking pipeline");
Require(
    firemakingGuide.Entries.Count == SkillService.MaximumLevel &&
    firemakingGuide.Entries[0].Description.Contains("48.0 hours") &&
    firemakingGuide.Entries[^1].Description.Contains("96.0 hours") &&
    firemakingGuide.Entries.Single(entry => entry.Level == 16)
        .Description.Contains("Flame size 4") &&
    CampfirePresentation.LitAtlasKey(
        ItemIds.Logs, 3, FiremakingSkill.FlameTier(16)) !=
    CampfirePresentation.LitAtlasKey(ItemIds.Logs, 3, 0),
    "the Firemaking guide and atlas keys must expose every duration level and four distinct flame tiers");
Require(
    CookingSkill.CanCook(ItemIds.RawMinnows, 1) &&
    !CookingSkill.CanCook(ItemIds.RawBluefinTuna, 16) &&
    CookingSkill.CanCook(ItemIds.RawBluefinTuna, 17) &&
    CookingSkill.BurnChance(ItemIds.RawRedSnapper, 20) <
    CookingSkill.BurnChance(ItemIds.RawRedSnapper, 9),
    "cooking must enforce unlock levels while higher levels meaningfully reduce burning");
var burntMinnows =
    CookingSkill.Roll(ItemIds.RawMinnows, 1, 0f);
var cookedMinnows =
    CookingSkill.Roll(ItemIds.RawMinnows, 1, .99f);
Require(
    burntMinnows.Burnt &&
    burntMinnows.ItemId == ItemIds.BurntMinnows &&
    burntMinnows.Experience == 0 &&
    !cookedMinnows.Burnt &&
    cookedMinnows.ItemId == ItemIds.CookedMinnows &&
    cookedMinnows.Experience > 0,
    "cooking rolls must deterministically map failures to shader-derived burnt fish and successes to cooked sprites");
Require(
    SurvivalService.TryFoodEffect(
        ItemIds.BurntMinnows, out var burntMinnowsFood) &&
    SurvivalService.TryFoodEffect(
        ItemIds.CookedMinnows, out var cookedMinnowsFood) &&
    burntMinnowsFood.HungerRestored > 0 &&
    burntMinnowsFood.HungerRestored <
        cookedMinnowsFood.HungerRestored &&
    burntMinnowsFood.WellFedSeconds == 0,
    "burnt fish must remain weak desperation food without the well-fed benefit of a successful cook");
var woodcuttingGuide =
    SkillGuideService.Definition(SkillType.Woodcutting);
Require(
    woodcuttingGuide.Entries.Count == SkillService.MaximumLevel &&
    woodcuttingGuide.Entries[0].Description.Contains(
        $"{WoodcuttingSkill.MinimumDamage(1)}–" +
        $"{WoodcuttingSkill.MaximumDamage(1)}") &&
    WoodcuttingSkill.MinimumDamage(20) >
    WoodcuttingSkill.MinimumDamage(1),
    "the woodcutting guide must show the shared accuracy and damage effects at every level");
var farmingGuide =
    SkillGuideService.Definition(SkillType.Farming);
var craftingGuide =
    SkillGuideService.Definition(SkillType.Crafting);
var diggingGuide =
    SkillGuideService.Definition(SkillType.Digging);
Require(
    Enum.GetValues<SkillType>().All(SkillGuideService.IsSupported) &&
    farmingGuide.Entries.Any(entry =>
        entry.Level == 1 &&
        entry.Description.Contains("berry")) &&
    farmingGuide.Entries.Any(entry =>
        entry.Level == 9 &&
        entry.Description.Contains("bronze sickle")) &&
    craftingGuide.Entries.Any(entry =>
        entry.Level == 6 &&
        entry.Description.Contains("bronze bar")) &&
    craftingGuide.Entries.Any(entry =>
        entry.Level == 10 &&
        entry.Description.Contains("iron bloom")) &&
    diggingGuide.Entries.Count == DiggingSkill.MaximumLevel &&
    diggingGuide.Entries[^1].Description.Contains(
        DiggingSkill.Damage(
            DiggingSkill.ExperienceForLevel(
                DiggingSkill.MaximumLevel)).ToString()),
    "all eight skills must expose data-driven guides for their rewards and level effects");
var skillGuideWindow = new SkillGuideWindowState();
skillGuideWindow.Open(fishingGuide, 20);
skillGuideWindow.Layout(new(0, 0, 1280, 720));
Require(
    skillGuideWindow.Visible &&
    skillGuideWindow.CurrentLevel == 20 &&
    skillGuideWindow.List.VisibleIndices.Last() ==
        fishingGuide.Entries.Count - 1 &&
    !skillGuideWindow.List.ScrollTrack.Visible,
    "the reusable skill guide must retain the highest meaningful unlock without empty rows");
skillGuideWindow.Close();
var fishingAward = FishingSkill.AwardExperience(
    0, WorldFishSpecies.RiverPerch);
Require(
    fishingAward.Experience ==
    FishingSkill.Profile(WorldFishSpecies.RiverPerch).Experience &&
    fishingAward.Gained > 0 &&
    FishingSkill.AnimationFrameSeconds(.1f) < .1f,
    "fishing XP and animation pacing must be owned by the fishing skill service");
var sharedAward = SkillService.AwardExperience(0, 25);
Require(
    WoodcuttingSkill.AwardExperience(0, 25) == sharedAward &&
    FarmingSkill.AwardExperience(0, 25) == sharedAward &&
    CraftingSkill.AwardExperience(
        0,
        new CraftingRecipe(
            "shared-xp-test", ItemIds.Sticks,
            CraftingCategory.Resources, 1, 25, [], [])) == sharedAward &&
    CookingSkill.AwardExperience(0, 25) == sharedAward,
    "every skill award path must delegate shared XP arithmetic to SkillService");
Require(
    FiremakingSkill.AwardExperience(0) ==
    SkillService.AwardExperience(
        0, FiremakingSkill.ExperiencePerFire),
    "Firemaking XP must delegate its level transition arithmetic to SkillService");
Require(
    PlayerInventory.TryAddAtPreferredSlot(
        [ItemIds.Sticks, null, ItemIds.Logs],
        ItemIds.CookedMinnows,
        1,
        out var preferredCookingSlot) &&
    preferredCookingSlot[1] == ItemIds.CookedMinnows &&
    PlayerInventory.TryAddAtPreferredSlot(
        [ItemIds.Sticks, ItemIds.Logs, null],
        ItemIds.CookedMinnows,
        1,
        out var fallbackCookingSlot) &&
    fallbackCookingSlot[2] == ItemIds.CookedMinnows,
    "cooked food must return to its original slot when possible and safely fall back to another free slot");
Require(
    ItemCatalog.Get(ItemIds.RawRedSnapper).SpriteCell == 6 &&
    ItemCatalog.Get(ItemIds.CookedRedSnapper).SpriteCell == 7 &&
    ItemCatalog.Get(ItemIds.CookedRedSnapper)
        .HasTag(ItemTag.CookedFood) &&
    ItemCatalog.Get(ItemIds.BurntRedSnapper).SpriteCell == 7 &&
    ItemCatalog.Get(ItemIds.BurntRedSnapper)
        .HasTag(ItemTag.BurntFood),
    "fish states must use authored raw/cooked pairs and reuse the cooked icon for shader-derived burnt fish");
Require(PlayerInventory.TryCarvePlank(
        [ItemIds.StoneKnife, ItemIds.Logs],
        0, 1, out var carvedPlank) &&
        carvedPlank[0] == ItemIds.StoneKnife &&
        carvedPlank[1] == ItemIds.Plank &&
        PlayerInventory.TryCarvePlank(
            [ItemIds.StoneKnife, ItemIds.OakLogs],
            0, 1, out var carvedOakPlank) &&
        carvedOakPlank[0] == ItemIds.StoneKnife &&
        carvedOakPlank[1] == ItemIds.Plank &&
        !PlayerInventory.TryCarvePlank(
            [ItemIds.SharpenedRock, ItemIds.Logs],
            0, 1, out _),
    "a knife must carve any log into a plank without being consumed");
Require(ItemCatalog.Get(ItemIds.Plank) is var plankDefinition &&
        plankDefinition.SpriteCell == 7 &&
        plankDefinition.HasTag(ItemTag.WoodcuttingMaterial),
    "the crafted plank must be registered in the item catalogue");

var contextMenu = new ContextMenuControlState();
var selectedContextItem = -1;
contextMenu.Selected += index => selectedContextItem = index;
contextMenu.Open(
    new(100, 100), ["Use", "Drop", "Examine"],
    new(0, 0, 300, 240));
Require(contextMenu.Items[^1] == "Examine" &&
        contextMenu.ItemBounds(2).Y > contextMenu.ItemBounds(1).Y,
    "Examine must be the final inventory context-menu action");
var dropBounds = contextMenu.ItemBounds(1);
var dropPoint = new Vector2(
    dropBounds.X + dropBounds.Z / 2,
    dropBounds.Y + dropBounds.W / 2);
contextMenu.UpdatePointer(dropPoint, leftDown: true);
contextMenu.UpdatePointer(dropPoint, leftDown: false);
Require(selectedContextItem == 1 && !contextMenu.Visible,
    "inventory context menu must select Drop and close");
contextMenu.Open(new(100, 100), ["Use", "Drop"], new(0, 0, 300, 200));
contextMenu.UpdatePointer(new(0, 0), leftDown: false);
Require(!contextMenu.Visible,
    "context menu must close when the pointer moves away");

var listControl = new ListControlState();
listControl.Layout(
    new(20, 30, 420, 140), ["first", "second"],
    rowHeight: 48, rowGap: 6, deleteWidth: 100);
var deleteControl = listControl.DeleteBounds(0);
Require(listControl.TryHit(
            new(
                deleteControl.X + deleteControl.Z / 2,
                deleteControl.Y + deleteControl.W / 2),
            out var listIndex,
            out var hitDelete) &&
        listIndex == 0 &&
        hitDelete &&
        !listControl.ApproveDelete("first") &&
        listControl.IsDeletePending("first") &&
        listControl.ApproveDelete("first"),
    "list deletion must require a separate confirmation click");
var scrollingList = new ListControlState();
scrollingList.Layout(
    new(20, 30, 420, 110),
    Enumerable.Range(0, 10)
        .Select(index => $"item-{index}")
        .ToArray(),
    rowHeight: 48,
    rowGap: 6,
    deleteWidth: 100);
Require(scrollingList.Scroll(new(30, 40), -1) &&
        scrollingList.FirstVisibleIndex == 3 &&
        scrollingList.VisibleIndices.First() == 3 &&
        scrollingList.ScrollTrack.Visible,
    "list controls must wheel-scroll their visible row window");
var visibleRowsWarmup = 0;
foreach (var index in scrollingList.VisibleIndices)
    visibleRowsWarmup += index;
var visibleRowsAllocationsBefore =
    GC.GetAllocatedBytesForCurrentThread();
var visibleRowsChecksum = visibleRowsWarmup;
for (var iteration = 0; iteration < 10_000; iteration++)
    foreach (var index in scrollingList.VisibleIndices)
        visibleRowsChecksum += index;
var visibleRowsAllocated =
    GC.GetAllocatedBytesForCurrentThread() -
    visibleRowsAllocationsBefore;
Require(
    visibleRowsChecksum > 0 && visibleRowsAllocated <= 128,
    "render-time visible list iteration must not allocate range iterators");

var boundedChat = new ChatUiControlState();
boundedChat.Layout(new(0, 0, 1280, 720));
boundedChat.SetInputText("hello");
boundedChat.Submit();
Require(
    boundedChat.Messages.Single() is
        { Text: "hello", Style: ChatMessageStyle.Player },
    "submitted player dialogue must have a distinct chat style");
for (var index = 0; index < 225; index++)
    boundedChat.AddMessage($"message {index}");
Require(boundedChat.Messages.Count == 200 &&
        boundedChat.Messages[0].Text == "message 25" &&
        boundedChat.IsAtBottom,
    "chat must discard its oldest messages while following the bottom");

const long seed = 8675309;
var origin = InfiniteWorldGenerator.Generate(seed, new(0, 0));
var repeated = InfiniteWorldGenerator.Generate(seed, new(0, 0));
Require(origin.GroundObjects.Count(item =>
            !CoastalCollectibleSpawner.IsCoastal(item.ItemId)) <= 8 &&
        origin.GroundObjects.Count(item =>
            CoastalCollectibleSpawner.IsCoastal(item.ItemId)) <=
            CoastalCollectibleSpawner.MaximumPerChunk &&
        origin.GroundObjects.SequenceEqual(repeated.GroundObjects) &&
        origin.GroundObjects.All(item =>
            item.ItemId is ItemIds.Sticks or ItemIds.LargeRock ||
            CoastalCollectibleSpawner.IsCoastal(item.ItemId)),
    "natural and coastal ground objects must be deterministic and independently capped");
var coastalDefinitions = new[]
{
    ItemIds.ClamShell, ItemIds.CockleShell, ItemIds.SpiralShell,
    ItemIds.ScallopShell, ItemIds.MoonShell, ItemIds.ConchShell,
    ItemIds.CowrieShell, ItemIds.PearlOysterShell, ItemIds.Seaweed
};
Require(coastalDefinitions.All(itemId =>
        ItemCatalog.Get(itemId).HasTag(ItemTag.CoastalSprite)),
    "all shell and seaweed items must use the coastal sprite sheet");
var shellGroundFrame = SpriteFrameTransforms.Resize(
    new SpriteFrame(32, 32, 16, 28, new byte[32 * 32 * 4]), .5f);
var seaweedGroundFrame = SpriteFrameTransforms.Resize(
    new SpriteFrame(32, 32, 16, 28, new byte[32 * 32 * 4]), .75f);
Require(shellGroundFrame is { Width: 16, Height: 16, HotspotX: 8 } &&
        seaweedGroundFrame is { Width: 24, Height: 24, HotspotX: 12 },
    "shells must render at half scale and seaweed at three-quarter scale on the ground");
var beachTiles = origin.Tiles.Select(tile => tile with
{
    Biome = Biome.Beach,
    Region = WorldBiome.Coast,
    North = 1,
    East = 1,
    South = 1,
    West = 1
}).ToArray();
var initialCoastal = CoastalCollectibleSpawner.GenerateInitial(
    seed, beachTiles, [], []);
Require(initialCoastal.Count is > 0 and <=
            CoastalCollectibleSpawner.MaximumPerChunk &&
        initialCoastal.All(item =>
            CoastalCollectibleSpawner.IsCoastal(item.ItemId)),
    "beach generation must create only capped coastal collectibles");
var respawnChunk = new WorldChunk
{
    Coordinate = origin.Coordinate,
    Tiles = beachTiles,
    Trees = [],
    BiomeWeightsA = origin.BiomeWeightsA,
    BiomeWeightsB = origin.BiomeWeightsB,
    BiomeWeightsC = origin.BiomeWeightsC,
    BiomeWeightsD = origin.BiomeWeightsD,
    ShoreDistance = origin.ShoreDistance,
    Cliffs = []
};
for (var attempt = 0;
     attempt < CoastalCollectibleSpawner.MaximumPerChunk + 4;
     attempt++)
    CoastalCollectibleSpawner.TryRespawn(
        respawnChunk, new(10000, 10000), out _);
Require(respawnChunk.GroundObjects.Count ==
            CoastalCollectibleSpawner.MaximumPerChunk,
    "coastal respawning must fill but never exceed its per-chunk cap");
Require(origin.Tiles.SequenceEqual(repeated.Tiles), "same seed and coordinate must reproduce tiles");
Require(origin.Trees.SequenceEqual(repeated.Trees), "same seed and coordinate must reproduce trees");
Require(origin.Trees.All(tree =>
        tree.FrameIndex >= 0 &&
        tree.FrameIndex < WorldTreeCatalog.FrameCount(tree.GraphicName)),
    "generated trees must select a valid authored visual variant");
Require(origin.Trees.All(tree =>
        tree.FrameIndex == WorldTreeCatalog.SelectFrame(
            seed, tree.X, tree.Y, tree.GraphicName)),
    "tree visual variants must be deterministic from seed and position");
Require(origin.Trees.Any(tree => tree.FrameIndex > 0),
    "generated woodland must use more than the first authored tree frame");
Require(origin.Vegetation.SequenceEqual(repeated.Vegetation),
    "same seed and coordinate must reproduce vegetation");
Require(origin.Fish.SequenceEqual(repeated.Fish) &&
        origin.Fish.Length <= WorldFishGenerator.MaximumPerChunk,
    "fish generation must be deterministic and capped per chunk");
Require(WorldFishGenerator.Profiles.Length == 6 &&
        WorldFishGenerator.RequiredGraphicNames.Distinct().Count() == 6 &&
        WorldFishGenerator.Profiles.All(profile =>
            profile.FrameCount > 1 &&
            !string.IsNullOrWhiteSpace(profile.DisplayName) &&
            !string.IsNullOrWhiteSpace(profile.Rarity) &&
            !string.IsNullOrWhiteSpace(profile.Habitat)),
    "all six authored fish sets must define animation, name, rarity, and habitat");
Require(origin.Fish.All(fish =>
    {
        var tileX = (int)MathF.Floor(fish.X) -
                    origin.Coordinate.X * WorldChunk.Size;
        var tileY = (int)MathF.Floor(fish.Y) -
                    origin.Coordinate.Y * WorldChunk.Size;
        var tile = origin.Tiles[tileY * WorldChunk.Size + tileX];
        if (!WorldFishGenerator.IsValidHabitat(fish.Species, tile))
            return false;
        var shoreDistance = WorldFishGenerator.DistanceFromShore(
            seed,
            (int)MathF.Floor(fish.X),
            (int)MathF.Floor(fish.Y));
        if (fish.Species == WorldFishSpecies.ShoreMinnows)
            return shoreDistance is >=
                    WorldFishGenerator.MinimumBeginnerShoreDistance and <=
                    WorldFishGenerator.MaximumBeginnerShoreDistance;
        for (var offsetY = -2; offsetY <= 2; offsetY++)
        for (var offsetX = -2; offsetX <= 2; offsetX++)
            if (InfiniteWorldGenerator.SampleTile(
                    seed,
                    (int)MathF.Floor(fish.X) + offsetX,
                    (int)MathF.Floor(fish.Y) + offsetY).Biome is
                Biome.Beach or Biome.DesertSand)
                return false;
        return true;
    }),
    "advanced fish must keep sand clearance while beginner minnows remain within casting distance of shore");
var guaranteedBeginnerChunk = Enumerable.Range(-4, 9)
    .SelectMany(chunkY => Enumerable.Range(-4, 9)
        .Select(chunkX => InfiniteWorldGenerator.Generate(
            seed, new(chunkX, chunkY))))
    .FirstOrDefault(chunk => chunk.Tiles.Any(tile =>
        tile.Biome is Biome.DeepWater or Biome.ShallowWater or
            Biome.RiverWater or Biome.MangroveShallows &&
        WorldFishGenerator.DistanceFromShore(
            seed, tile.X, tile.Y) is >=
                WorldFishGenerator.MinimumBeginnerShoreDistance and <=
                WorldFishGenerator.MaximumBeginnerShoreDistance));
Require(
    guaranteedBeginnerChunk is not null &&
    guaranteedBeginnerChunk.Fish.Any(fish =>
        fish.Species == WorldFishSpecies.ShoreMinnows &&
        WorldFishGenerator.DistanceFromShore(
            seed,
            (int)MathF.Floor(fish.X),
            (int)MathF.Floor(fish.Y)) <=
        WorldFishGenerator.MaximumBeginnerShoreDistance),
    "a chunk containing suitable shoreline shallows must guarantee beginner fish within casting distance");
var fishBlockedStartChunks = Enumerable.Range(-1, 3)
    .SelectMany(chunkY => Enumerable.Range(-1, 3)
        .Select(chunkX => InfiniteWorldGenerator.Generate(
            88421, new(chunkX, chunkY))))
    .ToArray();
Require(
    fishBlockedStartChunks.Any(chunk => chunk.Fish.Any(fish =>
        fish.Species == WorldFishSpecies.ShoreMinnows)),
    "a previously fish-blocked beach start must provide beginner minnows in its nearby chunks");
var animationFish = new WorldFish(
    0, 0, WorldFishSpecies.ShoreMinnows,
    "FISHS_NN", 3, "fish:test");
var positionedFish = animationFish with { X = 7.25f, Y = 11.5f };
var positionedFishRender = WorldFishRenderCache.Build(
    seed, [positionedFish]).Single();
Require(
    positionedFishRender.Grid == new Vector2(7.25f, 11.5f),
    "fish range and navigation checks must use grid coordinates instead of the projected render anchor");
Require(WorldFishAnimation.FrameAt(animationFish, 0) == 3 &&
        WorldFishAnimation.FrameAt(
            animationFish,
            WorldFishAnimation.SecondsPerFrame - .001) == 3 &&
        WorldFishAnimation.FrameAt(
            animationFish, WorldFishAnimation.SecondsPerFrame) == 4,
    "fish animations must advance once per real-time frame interval");
var fishDepth = WorldFishPresentation.CreateDepthFrame();
Require(fishDepth.Rgba[
            ((fishDepth.Height / 2 * fishDepth.Width) +
             fishDepth.Width / 2) * 4 + 3] > 80 &&
        fishDepth.Rgba[3] == 0,
    "fish depth effect must have an opaque blue centre and soft transparent edge");
Require(WorldFishPresentation.BaseHitTest(
            new(100, 100), new(100, 100), 1) &&
        !WorldFishPresentation.BaseHitTest(
            new(130, 100), new(100, 100), 1),
    "fish hover must use a compact rectangle around the water-level base");
Require(
    FishingSkill.CatchChance(WorldFishSpecies.ShoreMinnows, 1, 1) >= .72f &&
    FishingSkill.CatchChance(WorldFishSpecies.ShoreMinnows, 20, 3) <= .95f &&
    FishingSkill.CatchChance(WorldFishSpecies.ShoreMinnows, 20, 3) >
    FishingSkill.CatchChance(WorldFishSpecies.ShoreMinnows, 1, 1),
    "fishing catch chance must remain bounded while rewarding skill and stronger nets");
var fishingFeedback = new EntityFeedbackState();
fishingFeedback.ShowLabel("fish:test", "Miss", false, 10);
Require(
    fishingFeedback.TryGet("fish:test", out var missedFishFeedback) &&
    missedFishFeedback.Label == "Miss" &&
    !missedFishFeedback.LabelSuccess &&
    !fishingFeedback.HealthVisible("fish:test", 10),
    "fishing outcomes must reuse entity feedback without showing a health bar");
fishingFeedback.ShowLabel("fish:test", "Caught", true, 11);
Require(
    fishingFeedback.TryGet("fish:test", out var caughtFishFeedback) &&
    caughtFishFeedback.Label == "Caught" &&
    caughtFishFeedback.LabelSuccess,
    "fishing feedback must distinguish a green catch from a blue miss");
var zeroDamageFeedback = new EntityFeedbackState();
zeroDamageFeedback.ShowImpact("enemy:test", 0, true, 12);
Require(
    zeroDamageFeedback.TryGet(
        "enemy:test", out var zeroDamageImpact) &&
    zeroDamageImpact.Label == "Miss" &&
    !zeroDamageImpact.Hit &&
    zeroDamageFeedback.HealthVisible("enemy:test", 12),
    "every zero-damage impact must render as a blue Miss instead of the number zero");
Require(origin.Vegetation.All(item =>
            !item.CanBecomeInstance ||
            item.Kind is WorldVegetationKind.BerryBush or
                WorldVegetationKind.Shrub) &&
        origin.Vegetation
            .Where(item => item.Kind == WorldVegetationKind.BerryBush)
            .All(item => item.CanBecomeInstance),
    "berry bushes and green fibre shrubs should be flagged for interaction");
Require(new[]
    {
        "PLANTS", "BUSH_NN", "BUSH_N0", "BUSH2_NN", "BUSH2_N0",
        "BUSH3_NN", "BUSH3_N0", "FORAG_NN", "FORAGM_NN"
    }.All(WorldVegetationGenerator.RequiredGraphicNames.Contains),
    "the world graphics whitelist must include every vegetation and shadow asset");
Require(origin.Vegetation.All(item =>
    {
        var tileX = (int)MathF.Floor(item.X) - origin.Coordinate.X * WorldChunk.Size;
        var tileY = (int)MathF.Floor(item.Y) - origin.Coordinate.Y * WorldChunk.Size;
        var tile = origin.Tiles[tileY * WorldChunk.Size + tileX];
        var relief = new[] { tile.North, tile.East, tile.South, tile.West };
        var coastalFibre = tile.Biome == Biome.Beach &&
            item.Kind == WorldVegetationKind.Shrub &&
            item.CanBecomeInstance;
        return (coastalFibre ||
                tile.Biome is not (Biome.DeepWater or Biome.ShallowWater or
                    Biome.RiverWater or Biome.MangroveShallows or
                    Biome.Beach or Biome.DesertSand)) &&
               relief.Max() - relief.Min() <= 2 &&
               origin.Trees.All(tree => tree.X != tile.X || tree.Y != tile.Y);
    }),
    "vegetation must avoid water, desert sand, steep ground and trees while allowing interactive coastal scrub");
var fibreBlockedStartChunk = InfiniteWorldGenerator.Generate(
    88421, new(-1, 0));
Require(
    fibreBlockedStartChunk.Vegetation.Count(item =>
        item.CanBecomeInstance &&
        item.Kind == WorldVegetationKind.Shrub &&
        fibreBlockedStartChunk.Tiles.Any(tile =>
            tile.X == (int)MathF.Floor(item.X) &&
            tile.Y == (int)MathF.Floor(item.Y) &&
            tile.Biome == Biome.Beach)) >=
        WorldVegetationGenerator.MinimumCoastalFibreSourcesPerChunk,
    "a beach chunk from a previously blocked opening seed must contain enough interactive coastal scrub for the fibre quest");
Require(origin.Vegetation
        .Where(item => item.GraphicName == "BUSH2_NN")
        .All(item =>
        {
            var tileX = (int)MathF.Floor(item.X) -
                        origin.Coordinate.X * WorldChunk.Size;
            var tileY = (int)MathF.Floor(item.Y) -
                        origin.Coordinate.Y * WorldChunk.Size;
            var tile = origin.Tiles[tileY * WorldChunk.Size + tileX];
            return (item.FrameIndex >= 12) == (tile.Biome == Biome.Snow);
        }),
    "snow-covered bush frames must only appear on snow material");
Require(origin.Vegetation
        .Where(item => item.GraphicName == "BUSH3_NN")
        .All(item =>
        {
            var tileX = (int)MathF.Floor(item.X) -
                        origin.Coordinate.X * WorldChunk.Size;
            var tileY = (int)MathF.Floor(item.Y) -
                        origin.Coordinate.Y * WorldChunk.Size;
            return origin.Tiles[
                tileY * WorldChunk.Size + tileX].Biome == Biome.Snow;
        }),
    "white flowering shrubs must be treated as snow-covered");
Require(origin.Cliffs.SequenceEqual(repeated.Cliffs),
    "same seed and coordinate must reproduce cliff faces");
Require(origin.BiomeWeightsA.SequenceEqual(repeated.BiomeWeightsA),
    "same seed and coordinate must reproduce primary biome weights");
Require(origin.BiomeWeightsB.SequenceEqual(repeated.BiomeWeightsB),
    "same seed and coordinate must reproduce secondary biome and coastline weights");
Require(origin.BiomeWeightsC.SequenceEqual(repeated.BiomeWeightsC) &&
        origin.BiomeWeightsD.SequenceEqual(repeated.BiomeWeightsD),
    "same seed and coordinate must reproduce extended material weights");
Require(origin.ShoreDistance.SequenceEqual(repeated.ShoreDistance),
    "same seed and coordinate must reproduce shoreline distance");

var east = InfiniteWorldGenerator.Generate(seed, new(1, 0));
for (var y = 0; y < WorldChunk.Size; y++)
{
    var westEdge = origin.Tiles[y * WorldChunk.Size + WorldChunk.Size - 1];
    var eastEdge = east.Tiles[y * WorldChunk.Size];
    Require(westEdge.East == eastEdge.North,
        $"east height seam differs on row {y}: {westEdge.East} != {eastEdge.North}");
    Require(westEdge.South == eastEdge.West,
        $"south-east height seam differs on row {y}: {westEdge.South} != {eastEdge.West}");
}

var macroBiomes = new Dictionary<WorldBiome, int>();
var snowSamples = 0;
var hillSamples = 0;
var mountainSamples = 0;
var maximumElevation = 0f;
var deepOceanSamples = 0;
var shallowOceanSamples = 0;
var drainageSamples = 0;
var accumulatedRiverFlow = 0f;
var surfaceMaterials = new HashSet<Biome>();
for (var sampleY = -1000; sampleY <= 1000; sampleY += 40)
for (var sampleX = -1000; sampleX <= 1000; sampleX += 40)
{
    var tile = InfiniteWorldGenerator.SampleTile(seed, sampleX, sampleY);
    surfaceMaterials.Add(tile.Biome);
    var drainage = MacroHydrology.At(seed, sampleX, sampleY);
    macroBiomes[tile.Region] = macroBiomes.GetValueOrDefault(tile.Region) + 1;
    if (tile.Biome == Biome.Snow) snowSamples++;
    if (tile.Biome == Biome.DeepWater) deepOceanSamples++;
    if (tile.Biome == Biome.ShallowWater && tile.Region == WorldBiome.Ocean)
        shallowOceanSamples++;
    if (drainage.River > .45f)
    {
        drainageSamples++;
        accumulatedRiverFlow += drainage.Flow;
    }
    var elevation = (tile.North + tile.East + tile.South + tile.West) / 4f;
    maximumElevation = Math.Max(maximumElevation, elevation);
    if (elevation is >= 2 and < 5) hillSamples++;
    if (elevation >= 5) mountainSamples++;
}
Require(macroBiomes.ContainsKey(WorldBiome.Ocean), "macro world must contain oceans");
Require(macroBiomes.ContainsKey(WorldBiome.River), "macro world must contain river corridors");
Require(macroBiomes.ContainsKey(WorldBiome.Alpine), "macro world must contain mountain ranges");
Require(macroBiomes.ContainsKey(WorldBiome.TemperateForest) ||
        macroBiomes.ContainsKey(WorldBiome.Rainforest) ||
        macroBiomes.ContainsKey(WorldBiome.Taiga),
    "macro world must contain regional forests");
Require(macroBiomes.Keys.Count >= 7,
    $"macro climate should produce at least seven biome types; found {macroBiomes.Keys.Count}");

var entity = new WorldEntity(Vector2.Zero);
entity.MoveTo(new Vector2(3, 0));
entity.Update(.5f);
Require(entity.Action == EntityAction.Move && entity.Position.X > 1,
    "moving entity should advance toward its target");
var walkingAnimationTime = entity.ActionTime;
var replacementPathPosition = entity.Position;
entity.PrepareForPathRequest();
Require(entity.Action == EntityAction.Move &&
        entity.ActionTime == walkingAnimationTime &&
        entity.Target == replacementPathPosition,
    "requesting a replacement path must discard the superseded route without restarting the walk cycle");
entity.Update(.1f);
Require(
    entity.Position == replacementPathPosition,
    "an actor must not continue away from the captured start while a replacement path is pending");
var densePathEntity = new WorldEntity(Vector2.Zero);
densePathEntity.FollowPath(
    [new(.1f, 0), new(.2f, 0), new(.3f, 0)]);
densePathEntity.Update(.2f);
Require(
    densePathEntity.Position == new Vector2(.3f, 0) &&
    densePathEntity.Action == EntityAction.Idle,
    "movement must spend its full frame budget across dense quarter-cell waypoints");
entity.SetGender(EntityGender.Female);
Require(entity.Gender == EntityGender.Female,
    "entity gender should switch without replacing the entity");
entity.GatherAt(new Vector2(0, 2));
Require(entity.Action == EntityAction.Gather && entity.Facing.Y > 0,
    "gathering should face the collectible and select the gather animation");
var rigFrame = VillagerDirectionRig.Resolve(new Vector2(-1, 0), 75, 5, 4);
entity.Stop();
Require(entity.Action == EntityAction.Idle,
    "stopping must select the dedicated idle action");
Require(rigFrame.Index is >= 0 and < 75,
    "directional rig should resolve a valid authored frame");
var northFrame = VillagerDirectionRig.Resolve(new Vector2(-1, -1), 75, 5, 0);
Require(northFrame.Index == 60 && !northFrame.Mirror,
    "north movement should select the authored upward-facing animation");
var nearSideFrame = VillagerDirectionRig.Resolve(new Vector2(.75f, -.25f), 75, 5, 0);
var exactSideFrame = VillagerDirectionRig.Resolve(new Vector2(1, -1), 75, 5, 0);
Require(nearSideFrame == exactSideFrame,
    "slightly angled routes should remain in the wider cardinal facing wedge");
Require(snowSamples > 0, "cold tundra or alpine terrain must produce visible snow");
Require(hillSamples > 0, "continental terrain must produce rolling hills and foothills");
Require(mountainSamples > 0, "continental terrain must produce mountain elevations");
Require(maximumElevation >= 10,
    $"continental ranges must include impactful high peaks; highest was {maximumElevation}");
Require(deepOceanSamples > 0 && shallowOceanSamples > 0,
    "oceans must contain both deep basins and shallow continental shelves");
Require(drainageSamples > 0 && accumulatedRiverFlow / drainageSamples > 5,
    "rivers must be selected from cells with accumulated upstream rainfall");
Require(surfaceMaterials.Count >= 12,
    $"macro climate must exercise the expanded natural material palette; found {surfaceMaterials.Count}");
for (var seamY = -384; seamY <= 384; seamY += 64)
{
    var westDrainage = MacroHydrology.At(seed, 511.99f, seamY);
    var eastDrainage = MacroHydrology.At(seed, 512.01f, seamY);
    Require(Math.Abs(westDrainage.River - eastDrainage.River) < .03f,
        $"macro river field must blend across region seams at y={seamY}");
    Require(Math.Abs(westDrainage.Lake - eastDrainage.Lake) < .03f,
        $"macro lake field must blend across region seams at y={seamY}");
}

var atlasProgress = new System.Collections.Concurrent.ConcurrentBag<(int Done, int Total)>();
Require(WorldAtlasGenerator.PixelSize == 512,
    "default atlas output must use the high-resolution 512x512 texture");
var atlas = WorldAtlasGenerator.Generate(
    seed, 128, -96,
    (done, total) => atlasProgress.Add((done, total)),
    chunksAcross: 2,
    pixelsPerChunk: 3);
var repeatedAtlas = WorldAtlasGenerator.Generate(
    seed, 128, -96, chunksAcross: 2, pixelsPerChunk: 3);
Require(atlas.Rgba.SequenceEqual(repeatedAtlas.Rgba),
    "atlas generation must be deterministic");
Require(atlasProgress.Count == 4 && atlasProgress.Max(value => value.Done) == 4 &&
        atlasProgress.All(value => value.Total == 4),
    "atlas progress must report every generated chunk");
Require(atlas.Width == 6 && atlas.Height == 6 && atlas.SpanTiles == 64,
    "atlas dimensions must follow its chunk and pixel resolution");
var isometricKey = new WorldAtlasTileKey(0, 0, 1);
var isometricTile = WorldAtlasGenerator.GenerateIsometricTile(seed, isometricKey);
var repeatedIsometricTile = WorldAtlasGenerator.GenerateIsometricTile(seed, isometricKey);
var undergroundIsometricKey = isometricKey with
{
    Level = (int)WorldLevel.Underground
};
var undergroundIsometricTile =
    WorldAtlasGenerator.GenerateIsometricTile(
        seed, undergroundIsometricKey);
Require(isometricTile.Width == 256 && isometricTile.Height == 256,
    "isometric map sections must render at high-resolution 256x256");
Require(isometricTile.Rgba.SequenceEqual(repeatedIsometricTile.Rgba),
    "isometric map section generation must be deterministic");
Require(
    undergroundIsometricKey != isometricKey &&
    undergroundIsometricTile.Rgba.Any(value => value != 0) &&
    !undergroundIsometricTile.Rgba.SequenceEqual(isometricTile.Rgba),
    "atlas cache keys and pixels must distinguish underground from overworld");
using (var cancelledAtlas = new CancellationTokenSource())
{
    cancelledAtlas.Cancel();
    var cancelled = false;
    try
    {
        WorldAtlasGenerator.GenerateIsometricTile(
            seed, isometricKey, cancelledAtlas.Token);
    }
    catch (OperationCanceledException)
    {
        cancelled = true;
    }
    Require(cancelled,
        "atlas generation must stop promptly when its session is cancelled");
    cancelled = false;
    try
    {
        InfiniteWorldGenerator.Generate(
            seed, new(500, 500), cancelledAtlas.Token);
    }
    catch (OperationCanceledException)
    {
        cancelled = true;
    }
    Require(cancelled,
        "teleporting must be able to cancel an obsolete detailed chunk load");
}
using (var atlasQueue = new WorldAtlasGenerationQueue())
{
    var movingKeys = new[]
    {
        new WorldAtlasTileKey(70, 70, 1),
        new WorldAtlasTileKey(71, 70, 1),
        new WorldAtlasTileKey(72, 70, 1)
    };
    atlasQueue.SetRequest(seed, movingKeys, _ => false);
    Require(
        atlasQueue.ActiveCount ==
        WorldAtlasGenerationQueue.ConcurrencyLimit,
        "atlas generation must obey one shared bounded concurrency limit");
    atlasQueue.SetRequest(seed, [], _ => false);
    Require(
        atlasQueue.ActiveCount == 0 &&
        atlasQueue.CancelledCount ==
        WorldAtlasGenerationQueue.ConcurrencyLimit,
        "moving or closing the atlas must cancel obsolete tile jobs");
}
var deletedAtlasTextures = new List<int>();
var atlasTextureCache = new WorldAtlasTextureCache();
var cacheA = new WorldAtlasTileKey(1, 1, 1);
var cacheB = new WorldAtlasTileKey(2, 1, 1);
var cacheC = new WorldAtlasTileKey(3, 1, 1);
atlasTextureCache.Set(cacheA, 11, 256, 256, deletedAtlasTextures.Add);
atlasTextureCache.Set(cacheB, 12, 256, 256, deletedAtlasTextures.Add);
atlasTextureCache.TryGet(cacheA, out _);
atlasTextureCache.Set(cacheC, 13, 256, 256, deletedAtlasTextures.Add);
atlasTextureCache.Trim(
    new HashSet<WorldAtlasTileKey> { cacheA },
    2,
    deletedAtlasTextures.Add);
Require(
    atlasTextureCache.Count == 2 &&
    atlasTextureCache.Contains(cacheA) &&
    !atlasTextureCache.Contains(cacheB) &&
    deletedAtlasTextures.SequenceEqual([12]),
    "atlas textures must use visible-aware LRU eviction");
atlasTextureCache.Clear(deletedAtlasTextures.Add);
Require(
    atlasTextureCache.Count == 0 &&
    atlasTextureCache.Bytes == 0 &&
    deletedAtlasTextures.Count == 3,
    "closing the atlas must release every retained GPU texture");
var gameplayHydrologyCount = MacroHydrology.GameplayCacheCount;
using (MacroHydrology.BeginAtlasSampling())
{
    _ = MacroHydrology.At(seed, 400_000, -400_000);
    Require(
        MacroHydrology.AtlasCacheCount > 0 &&
        MacroHydrology.GameplayCacheCount == gameplayHydrologyCount,
        "atlas exploration must not evict the gameplay hydrology working set");
}
MacroHydrology.ClearAtlasCache();
Require(MacroHydrology.AtlasCacheCount == 0,
    "closing the atlas must release its isolated hydrology cache");

var textureSize = WorldChunk.WeightTextureSize;
var halo = WorldChunk.WeightHaloTiles * WorldChunk.WeightSamplesPerTile;
var originEdgeX = halo + WorldChunk.Size * WorldChunk.WeightSamplesPerTile;
var eastEdgeX = halo;
for (var y = halo; y <= halo + WorldChunk.Size * WorldChunk.WeightSamplesPerTile; y++)
for (var channel = 0; channel < 4; channel++)
{
    Require(origin.BiomeWeightsA[(y * textureSize + originEdgeX) * 4 + channel] ==
            east.BiomeWeightsA[(y * textureSize + eastEdgeX) * 4 + channel],
        $"primary biome blend seam differs at sample {y}, channel {channel}");
    Require(origin.BiomeWeightsB[(y * textureSize + originEdgeX) * 4 + channel] ==
            east.BiomeWeightsB[(y * textureSize + eastEdgeX) * 4 + channel],
        $"secondary biome/coast blend seam differs at sample {y}, channel {channel}");
    Require(origin.BiomeWeightsC[(y * textureSize + originEdgeX) * 4 + channel] ==
            east.BiomeWeightsC[(y * textureSize + eastEdgeX) * 4 + channel] &&
            origin.BiomeWeightsD[(y * textureSize + originEdgeX) * 4 + channel] ==
            east.BiomeWeightsD[(y * textureSize + eastEdgeX) * 4 + channel],
        $"extended material blend seam differs at sample {y}, channel {channel}");
}
for (var y = halo; y <= halo + WorldChunk.Size * WorldChunk.WeightSamplesPerTile; y++)
    Require(origin.ShoreDistance[y * textureSize + originEdgeX] ==
            east.ShoreDistance[y * textureSize + eastEdgeX],
        $"shoreline distance seam differs at sample {y}");

var root = Path.Combine(Path.GetTempPath(), $"IslandRpg.WorldChecks.{Guid.NewGuid():N}");
long regionBytes = 0;
try
{
    var availableTree = new WorldTreeInstance(
        Guid.NewGuid(), 4, 7, "FSNO_NN", 20, 20,
        TreeLifecycleState.Standing);
    var felledTree = availableTree with
    {
        Id = Guid.NewGuid(),
        X = 8,
        State = TreeLifecycleState.Stump,
        Health = 0
    };
    WorldTreeInstance[] treeStates = [availableTree, felledTree];
    Require(
        TreeInteractionAvailability.CanUseStandingTree(treeStates, 4, 7) &&
        !TreeInteractionAvailability.CanUseStandingTree(treeStates, 8, 7) &&
        TreeInteractionAvailability.CanUseStandingTree(treeStates, 12, 7) &&
        TreeInteractionAvailability.StateAt(treeStates, 8, 7) ==
            TreeLifecycleState.Stump,
        "tree actions must distinguish untouched and standing trees from persisted stumps");
    Require(
        TreeInteractionAvailability.CanGatherSticks(
            Array.Empty<WorldTreeInstance>(), 12, 7),
        "NPCs must consider an untouched generated tree when seeking sticks");
    var exhaustedTree = availableTree with
    {
        Id = Guid.NewGuid(),
        X = 16,
        SticksRemaining = 0,
        InitialStickCount = 2
    };
    Require(
        !TreeInteractionAvailability.CanGatherSticks(
            [exhaustedTree], 16, 7) &&
        TreeInteractionAvailability.CanGatherSticks(
            [availableTree], 4, 7),
        "NPC stick targeting must reject exhausted trees while retaining unrolled standing trees");
    var testAssetRoot = Path.Combine(root, "test-assets");
    var testCatalog = TestAssetLoader.LoadAll(
        testAssetRoot,
        new Progress<(int Done, int Total, string Name)>(),
        GameHostWindow.RequiredGraphicsFor(
            GameHostWindow.PreviewMode.Game)!);
    Require(
        testCatalog.Graphics.Count > 0 &&
        testCatalog.TerrainTiles.Count == Enum.GetValues<Biome>().Length &&
        testCatalog.WaterTextures.Count == 4 &&
        testCatalog.Graphics.Values.All(graphic =>
            graphic.SourcePath.StartsWith(
                Path.Combine(testAssetRoot, "Graphics"),
                StringComparison.OrdinalIgnoreCase)),
        "an empty test-asset redirect must produce a complete placeholder catalogue without AoE files");

    var store = new WorldChunkStore(seed, root);
    var touchedTree = origin.Trees.First();
    origin.TreeInstances.Add(new(
        Guid.NewGuid(),
        touchedTree.X,
        touchedTree.Y,
        touchedTree.GraphicName,
        45,
        100,
        TreeLifecycleState.Standing));
    while (origin.GroundObjects.Count < 12)
    {
        var index = origin.GroundObjects.Count;
        origin.GroundObjects.Add(new(
            Guid.NewGuid(),
            index % 2 == 0
                ? ItemIds.Sticks
                : ItemIds.LargeRock,
            index + .25f,
            index + .65f));
    }
    origin.GroundObjects.Add(new(
        Guid.NewGuid(), ItemIds.Axe, 20.25f, 20.65f));
    origin.GroundObjects.Add(new(
        Guid.NewGuid(), ItemIds.OakLogs, 21.25f, 21.65f));
    origin.GroundObjects.Add(new(
        Guid.NewGuid(), ItemIds.DigSite, 22.5f, 22.5f,
        Health: 37, MaxHealth: 70));
    for (var regionY = 0; regionY < WorldChunkStore.RegionSize; regionY++)
    for (var regionX = 0; regionX < WorldChunkStore.RegionSize; regionX++)
        store.Save(CloneAt(origin, new(regionX, regionY)));
    var negative = CloneAt(origin, new(-1, -1));
    store.Save(negative);

    var loaded = store.LoadOrGenerate(origin.Coordinate);
    Require(origin.Tiles.SequenceEqual(loaded.Tiles), "saved tiles must round-trip");
    Require(origin.Trees.SequenceEqual(loaded.Trees), "saved trees must round-trip");
    Require(origin.Fish.SequenceEqual(loaded.Fish),
        "derived fish schools must regenerate when a chunk is loaded");
    Require(origin.TreeInstances.SequenceEqual(loaded.TreeInstances),
        "instantiated tree IDs, health, and lifecycle state must round-trip");
    Require(origin.GroundObjects.SequenceEqual(loaded.GroundObjects),
        "ground objects and collected-object removals must round-trip");
    Require(origin.Cliffs.SequenceEqual(loaded.Cliffs), "derived cliff faces must round-trip");
    Require(origin.BiomeWeightsA.SequenceEqual(loaded.BiomeWeightsA),
        "primary biome weights must round-trip");
    Require(origin.BiomeWeightsB.SequenceEqual(loaded.BiomeWeightsB),
        "secondary biome and coastline weights must round-trip");
    Require(origin.BiomeWeightsC.SequenceEqual(loaded.BiomeWeightsC) &&
            origin.BiomeWeightsD.SequenceEqual(loaded.BiomeWeightsD),
        "extended natural-material weights must round-trip");
    Require(origin.ShoreDistance.SequenceEqual(loaded.ShoreDistance),
        "shoreline distance must round-trip");
    Require(File.Exists(Path.Combine(store.WorldDirectory, "world.json")), "world metadata must be saved");
    var positiveRegion = store.RegionPathFor(new(7, 7));
    Require(File.Exists(positiveRegion), "positive region file must exist");
    Require(store.RegionPathFor(new(0, 0)) == positiveRegion,
        "all 64 chunks in an 8x8 range must share one region file");
    Require(store.RegionPathFor(new(-1, -1)) != positiveRegion,
        "negative chunk coordinates must map to the neighboring region");
    Require(Directory.GetFiles(Path.GetDirectoryName(positiveRegion)!, "*.irrg").Length == 2,
        "65 chunks spanning two regions must use exactly two region files");
    regionBytes = new FileInfo(positiveRegion).Length;
    store.Save(origin);
    Require(new FileInfo(positiveRegion).Length == regionBytes,
        "saving an unchanged chunk must not append duplicate region data");
    const int simulatedLevelTransitions = 4;
    const int visibleChunksPerTransition = 25;
    var transitionProcess =
        System.Diagnostics.Process.GetCurrentProcess();
    transitionProcess.Refresh();
    var transitionBaselineManaged = GC.GetTotalMemory(false);
    var transitionBaselineWorkingSet =
        transitionProcess.WorkingSet64;
    var transitionBaselinePrivate =
        transitionProcess.PrivateMemorySize64;
    var transitionBaselineHandles = transitionProcess.HandleCount;
    var transitionBaselineThreads = transitionProcess.Threads.Count;
    var transitionBaselineGen0 = GC.CollectionCount(0);
    var transitionBaselineGen1 = GC.CollectionCount(1);
    var transitionBaselineGen2 = GC.CollectionCount(2);
    var transitionSaveAllocatedBefore =
        GC.GetAllocatedBytesForCurrentThread();
    var transitionSaveTimer =
        System.Diagnostics.Stopwatch.StartNew();
    for (var transition = 0;
         transition < simulatedLevelTransitions;
         transition++)
    for (var chunk = 0;
         chunk < visibleChunksPerTransition;
         chunk++)
        store.Save(origin);
    transitionSaveTimer.Stop();
    var transitionSaveAllocated =
        GC.GetAllocatedBytesForCurrentThread() -
        transitionSaveAllocatedBefore;
    Console.WriteLine(
        $"Unchanged level-transition saves " +
        $"({simulatedLevelTransitions} x " +
        $"{visibleChunksPerTransition} chunks): " +
        $"{transitionSaveTimer.Elapsed.TotalMilliseconds:N1} ms / " +
        $"{transitionSaveAllocated:N0} B.");
    transitionProcess.Refresh();
    var transitionPeakManaged = GC.GetTotalMemory(false);
    var transitionPeakWorkingSet = transitionProcess.WorkingSet64;
    var transitionPeakPrivate =
        transitionProcess.PrivateMemorySize64;
    var transitionPeakHandles = transitionProcess.HandleCount;
    var transitionPeakThreads = transitionProcess.Threads.Count;
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    transitionProcess.Refresh();
    var transitionIdleManaged = GC.GetTotalMemory(true);
    Console.WriteLine(
        "Transition process metrics:" +
        $"\n  managed: {transitionBaselineManaged / 1048576d:N1} -> " +
        $"{transitionPeakManaged / 1048576d:N1} -> " +
        $"{transitionIdleManaged / 1048576d:N1} MiB (baseline/after/idle)" +
        $"\n  working: {transitionBaselineWorkingSet / 1048576d:N1} -> " +
        $"{transitionPeakWorkingSet / 1048576d:N1} -> " +
        $"{transitionProcess.WorkingSet64 / 1048576d:N1} MiB" +
        $"\n  private: {transitionBaselinePrivate / 1048576d:N1} -> " +
        $"{transitionPeakPrivate / 1048576d:N1} -> " +
        $"{transitionProcess.PrivateMemorySize64 / 1048576d:N1} MiB" +
        $"\n  handles: {transitionBaselineHandles} -> " +
        $"{transitionPeakHandles} -> {transitionProcess.HandleCount}" +
        $"\n  threads: {transitionBaselineThreads} -> " +
        $"{transitionPeakThreads} -> {transitionProcess.Threads.Count}" +
        $"\n  collections: gen0 +" +
        $"{GC.CollectionCount(0) - transitionBaselineGen0}, gen1 +" +
        $"{GC.CollectionCount(1) - transitionBaselineGen1}, gen2 +" +
        $"{GC.CollectionCount(2) - transitionBaselineGen2}");
    Require(
        transitionSaveAllocated >= 1_000_000 ||
        transitionSaveTimer.Elapsed >= TimeSpan.FromMilliseconds(50),
        "level-transition characterization must reproduce the current " +
        "unchanged-chunk save workload before it is optimized");
    Require(new FileInfo(positiveRegion).Length <
            (long)WorldChunkStore.RegionSize * WorldChunkStore.RegionSize *
            (origin.BiomeWeightsA.Length + origin.BiomeWeightsB.Length +
             origin.BiomeWeightsC.Length + origin.BiomeWeightsD.Length),
        "region storage must be smaller than persisting deterministic render textures");
    var farLoaded = store.LoadOrGenerate(new(7, 7));
    Require(farLoaded.Coordinate == new ChunkCoordinate(7, 7),
        "direct region lookup must load the requested slot");
    var negativeLoaded = store.LoadOrGenerate(new(-1, -1));
    Require(negativeLoaded.Coordinate == new ChunkCoordinate(-1, -1),
        "negative region coordinates must round-trip");
    var undergroundCoordinate = new ChunkCoordinate(
        0, 0, (int)WorldLevel.Underground);
    var undergroundAllocatedBefore =
        GC.GetAllocatedBytesForCurrentThread();
    var undergroundTimer =
        System.Diagnostics.Stopwatch.StartNew();
    var underground = store.LoadOrGenerate(undergroundCoordinate);
    undergroundTimer.Stop();
    var undergroundAllocated =
        GC.GetAllocatedBytesForCurrentThread() -
        undergroundAllocatedBefore;
    store.Save(underground);
    Require(!File.Exists(store.RegionPathFor(undergroundCoordinate)),
        "deterministic underground chunks must not produce unused save files");
    Require(
        underground.GroundObjects.Count > 0 &&
        underground.GroundObjects.All(value =>
            value.ItemId == ItemIds.LargeRock &&
            underground.Tiles[
                PositiveMod((int)MathF.Floor(value.Y), WorldChunk.Size) *
                WorldChunk.Size +
                PositiveMod((int)MathF.Floor(value.X), WorldChunk.Size)]
                .Biome is not (
                    Biome.DeepWater or Biome.ShallowWater or
                    Biome.RiverWater or Biome.MangroveShallows)) &&
        underground.InitialGroundObjectIds.SetEquals(
            underground.GroundObjects.Select(value => value.Id)),
        "underground natural generation must reuse rocks without spawning sticks");
    var persistedEntrance = new WorldGroundObject(
        Guid.NewGuid(), ItemIds.CaveEntrance, .5f, .5f);
    underground.GroundObjects.Add(persistedEntrance);
    store.Save(underground);
    var undergroundWithEntrance =
        store.LoadOrGenerate(undergroundCoordinate);
    Require(
        undergroundWithEntrance.GroundObjects.Any(value =>
            value.Id == persistedEntrance.Id &&
            CaveEntranceService.IsEntrance(value)),
        "rope-secured entrances must persist on the matching cave tile");
    var entranceIndex = undergroundWithEntrance.GroundObjects.FindIndex(
        value => value.Id == persistedEntrance.Id);
    undergroundWithEntrance.GroundObjects[entranceIndex] =
        persistedEntrance with { ItemId = ItemIds.CaveHole };
    store.Save(undergroundWithEntrance);
    var undergroundWithOpenShaft =
        store.LoadOrGenerate(undergroundCoordinate);
    Require(
        undergroundWithOpenShaft.GroundObjects.Any(value =>
            value.Id == persistedEntrance.Id &&
            CaveEntranceService.IsHole(value) &&
            CaveEntranceService.IsCaveShaft(value)),
        "open cave shafts must persist for underground light without a rope");
    var undergroundCampfire = CampfireService.Light(
        CampfireService.AddFuel(
            new(
                Guid.NewGuid(),
                ItemIds.Campfire,
                undergroundCoordinate.X * WorldChunk.Size + 12.5f,
                undergroundCoordinate.Y * WorldChunk.Size + 12.5f),
            ItemIds.Logs,
            120),
        120,
        8);
    undergroundWithOpenShaft.GroundObjects.Add(undergroundCampfire);
    store.Save(undergroundWithOpenShaft);
    undergroundWithOpenShaft =
        store.LoadOrGenerate(undergroundCoordinate);
    var reloadedUndergroundCampfire =
        undergroundWithOpenShaft.GroundObjects.Single(value =>
            value.Id == undergroundCampfire.Id);
    Require(
        reloadedUndergroundCampfire.FuelItemId == ItemIds.Logs &&
        reloadedUndergroundCampfire.LitUntilGameSeconds ==
            undergroundCampfire.LitUntilGameSeconds &&
        reloadedUndergroundCampfire.FiremakingLevel == 8 &&
        CampfireService.State(
            reloadedUndergroundCampfire, 121) == CampfireState.Lit,
        "underground campfire fuel, expiry, and Firemaking level must survive a chunk reload");
    var persistentChest = new WorldGroundObject(
        Guid.NewGuid(),
        ItemIds.StorageChest,
        undergroundCoordinate.X * WorldChunk.Size + 14.5f,
        undergroundCoordinate.Y * WorldChunk.Size + 14.5f,
        OwnerId: "mira");
    var persistentChestState =
        StorageContainerService.Open(persistentChest);
    Require(
        persistentChestState.TryAdd(
            ItemIds.SlimeGel, 100, ownerId: "mira") &&
        persistentChestState.TryAdd(
            ItemIds.SlimeGel, 2, ownerId: "player") &&
        persistentChestState.TryAdd(
            ItemIds.BronzeBar, 4, ownerId: "mira") &&
        persistentChestState.Items.Count(
            item => item == ItemIds.SlimeGel) == 2,
        "the persistence fixture must populate a storage chest");
    persistentChest = StorageContainerService.Save(
        persistentChest, persistentChestState);
    undergroundWithOpenShaft.GroundObjects.Add(persistentChest);
    store.Save(undergroundWithOpenShaft);
    undergroundWithOpenShaft =
        store.LoadOrGenerate(undergroundCoordinate);
    var reloadedChest =
        undergroundWithOpenShaft.GroundObjects.Single(value =>
            value.Id == persistentChest.Id);
    var reloadedChestState =
        StorageContainerService.Open(reloadedChest);
    Require(
        reloadedChestState.Quantities.Zip(
                reloadedChestState.OwnerIds)
            .Any(value =>
                value.First == 100 &&
                value.Second == "mira") &&
        reloadedChestState.Quantities.Zip(
                reloadedChestState.OwnerIds)
            .Any(value =>
                value.First == 2 &&
                value.Second == "player") &&
        reloadedChestState.Items.Count(
            item => item == ItemIds.BronzeBar) == 4 &&
        reloadedChestState.Quantities
            .Where((_, slot) =>
                reloadedChestState.Items[slot] == ItemIds.BronzeBar)
            .All(quantity => quantity == 1) &&
        reloadedChest.OwnerId == "mira",
        "container contents and placed-object ownership must survive a chunk reload");
    var collectedRock = undergroundWithOpenShaft.GroundObjects.First(
        value => value.ItemId == ItemIds.LargeRock);
    undergroundWithOpenShaft.GroundObjects.Remove(collectedRock);
    store.Save(undergroundWithOpenShaft);
    var undergroundReloaded =
        store.LoadOrGenerate(undergroundCoordinate);
    Require(
        undergroundReloaded.GroundObjects.All(value =>
            value.Id != collectedRock.Id) &&
        undergroundReloaded.GroundObjects.Count(value =>
            value.ItemId == ItemIds.LargeRock) ==
        underground.InitialGroundObjectIds.Count - 1,
        "collected underground rocks must remain absent after reloading");
    Require(
        underground.Coordinate.X == origin.Coordinate.X &&
        underground.Coordinate.Y == origin.Coordinate.Y &&
        underground.Coordinate.Level == (int)WorldLevel.Underground &&
        underground.RenderableTiles.Any(value => value) &&
        underground.RenderableTiles.Any(value => !value),
        "underground chunks must share overworld coordinates and contain carved floor plus void");
    Require(
        underground.RenderableTiles.SequenceEqual(
            undergroundReloaded.RenderableTiles) &&
        underground.UndergroundDensity.SequenceEqual(
            undergroundReloaded.UndergroundDensity) &&
        underground.Vegetation.SequenceEqual(
            undergroundReloaded.Vegetation),
        "transient underground generation must be deterministic");
    Require(
        underground.Vegetation.Length is > 0 and
            <= CaveFeaturePlacement.MaximumNodes &&
        underground.Vegetation.All(value =>
            UndergroundResourceGenerator.IsResourceGraphic(
                value.GraphicName) &&
            value.FrameIndex >= 0 &&
            value.FrameIndex <
            UndergroundResourceGenerator.VariantCount(value.GraphicName) &&
            !value.CanBecomeInstance),
        "underground scenery must stay sparse, decorative, and non-interactive");
    var contextualTiles = underground.Tiles
        .Select(tile => tile with
        {
            Biome = PositiveMod(tile.X, 8) is 3 or 4
                ? Biome.ShallowWater
                : Biome.Rock
        })
        .ToArray();
    var contextualScenery = CaveFeaturePlacement.Generate(
        store.Seed,
        undergroundCoordinate,
        contextualTiles,
        Enumerable.Repeat(true, WorldChunk.Size * WorldChunk.Size).ToArray());
    Require(
        contextualScenery.Any(value =>
            value.GraphicName == UndergroundResourceGenerator.Growth &&
            value.FrameIndex is >= 0 and <= 4) &&
        contextualScenery.Any(value =>
            value.GraphicName != UndergroundResourceGenerator.Growth),
        "cave features must combine water-aware growth with geological scenery");
    var shaftTile = Enumerable.Range(0, underground.RenderableTiles.Length)
        .First(index =>
        {
            var x = index % WorldChunk.Size;
            var y = index / WorldChunk.Size;
            return x is > 0 and < WorldChunk.Size - 1 &&
                   y is > 0 and < WorldChunk.Size - 1 &&
                   underground.RenderableTiles[index];
        });
    var testShaft = new WorldGroundObject(
        Guid.NewGuid(),
        ItemIds.CaveHole,
        shaftTile % WorldChunk.Size + .5f,
        shaftTile / WorldChunk.Size + .5f);
    underground.GroundObjects.Add(testShaft);
    var shaftRenderItems = WorldVegetationRenderCache.Build(
        underground,
        new float[(WorldChunk.Size + 1) * (WorldChunk.Size + 1)]);
    underground.GroundObjects.Remove(testShaft);
    Require(
        shaftRenderItems.Any(value =>
            value.StableKey.StartsWith(
                $"shaft-growth:{testShaft.Id}:",
                StringComparison.Ordinal) &&
            value.AtlasKey.StartsWith(
                UndergroundResourceGenerator.Growth,
                StringComparison.Ordinal)),
        "open cave shafts must create cached entrance-zone plant presentation");
    var caveWaterSamples = 0;
    for (var sampleY = -128; sampleY < 128; sampleY++)
    for (var sampleX = -128; sampleX < 128; sampleX++)
    {
        var material = UndergroundWorldGenerator.MaterialAt(
            store.Seed, sampleX, sampleY);
        if (material is Biome.ShallowWater or Biome.RiverWater)
            caveWaterSamples++;
        Require(
            material is not Biome.Beach and not Biome.DesertSand,
            "underground water must blend directly into cave materials");
    }
    Require(caveWaterSamples > 0,
        "underground generation must include cave water presentation");
    var undergroundWeightTextures = new[]
    {
        underground.BiomeWeightsA,
        underground.BiomeWeightsB,
        underground.BiomeWeightsC,
        underground.BiomeWeightsD
    };
    var blendedUndergroundPixels = 0;
    for (var pixel = 0;
         pixel < WorldChunk.WeightTextureSize *
         WorldChunk.WeightTextureSize;
         pixel++)
    {
        var activeWeights = 0;
        foreach (var texture in undergroundWeightTextures)
        for (var channel = 0; channel < 4; channel++)
            if (texture[pixel * 4 + channel] > 0)
                activeWeights++;
        if (activeWeights > 1)
            blendedUndergroundPixels++;
    }
    Require(
        blendedUndergroundPixels > 0,
        "underground material weights must blend rather than form hard tile edges");
    var undergroundMesh = underground.UndergroundMeshVertices;
    var hasInterpolatedContourVertex = false;
    for (var offset = 0; offset < undergroundMesh.Length; offset += 12)
    {
        var sampleX = undergroundMesh[offset + 2] * 8 * 4;
        var sampleY = undergroundMesh[offset + 3] * 8 * 4;
        if (MathF.Abs(sampleX - MathF.Round(sampleX)) > .001f ||
            MathF.Abs(sampleY - MathF.Round(sampleY)) > .001f)
        {
            hasInterpolatedContourVertex = true;
            break;
        }
    }
    Require(
        undergroundMesh.Length > 0 &&
        undergroundMesh.Length % 12 == 0 &&
        hasInterpolatedContourVertex,
        "underground terrain must clip triangles at an interpolated sub-tile contour");
    var darkestCaveVertex = 1f;
    var brightestCaveVertex = 0f;
    for (var offset = 11;
         offset < undergroundMesh.Length;
         offset += 12)
    {
        darkestCaveVertex = Math.Min(
            darkestCaveVertex, undergroundMesh[offset]);
        brightestCaveVertex = Math.Max(
            brightestCaveVertex, undergroundMesh[offset]);
    }
    Require(
        darkestCaveVertex <= .001f &&
        brightestCaveVertex >= .99f,
        "cave terrain must fade from black at its contour to full brightness inside");
    Require(
        undergroundMesh.Length / 12 < 10_000,
        "underground render meshes must not regress to full density-grid tessellation");
    var undergroundBounds =
        WorldChunkProjection.TerrainBounds(undergroundMesh, 12);
    Require(
        undergroundBounds.Z > 0 &&
        undergroundBounds.W > 0,
        "underground culling bounds must come from the prepared cave mesh");
    Require(
        underground.UndergroundDensity.Length ==
        UndergroundWorldGenerator.DensityStride *
        UndergroundWorldGenerator.DensityStride,
        "underground generation must retain one reusable sub-tile density field");
    Require(
        underground.UndergroundProjectedBounds == undergroundBounds,
        "underground generation must carry background-computed culling bounds");
    using (var cancelledUnderground =
           new CancellationTokenSource())
    {
        cancelledUnderground.Cancel();
        var cancellationObserved = false;
        try
        {
            UndergroundWorldGenerator.Generate(
                store.Seed,
                new(1, 0, (int)WorldLevel.Underground),
                cancelledUnderground.Token);
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        Require(
            cancellationObserved,
            "underground density, mesh, and bounds generation must observe cancellation");
    }
    Require(undergroundTimer.Elapsed < TimeSpan.FromSeconds(5) &&
            undergroundAllocated < 128L * 1024 * 1024,
        "underground chunk generation exceeded its performance budget");
    Console.WriteLine(
        $"Underground chunk benchmark: {undergroundTimer.Elapsed.TotalMilliseconds:N1} ms / " +
        $"{undergroundAllocated:N0} B, {undergroundMesh.Length / 12:N0} vertices.");
    for (var sample = 0; sample <= WorldChunk.Size * 4; sample++)
    {
        var y = sample / 4f;
        var seamFromWest = CaveHydrologyField.Density(
            store.Seed, WorldChunk.Size, y);
        var seamFromEast = CaveHydrologyField.Density(
            store.Seed,
            undergroundCoordinate.X * WorldChunk.Size +
            WorldChunk.Size,
            undergroundCoordinate.Y * WorldChunk.Size + y);
        Require(
            MathF.Abs(seamFromWest - seamFromEast) < .000001f,
            "underground contours must agree at chunk boundaries");
    }

    var saves = new GameSaveRepository(Path.Combine(root, "profiles"));
    var player = saves.CreatePlayer(
        "Test Hero", EntityGender.Female, 3, 5);
    player = player with
    {
        WoodcuttingExperience = 725,
        AdventureExperience = 1200,
        Health = 111,
        Hunger = 64,
        WellFedSeconds = 90,
        AttackExperience = 350,
        StrengthExperience = 225,
        DefenceExperience = 75,
        CombatStance = MeleeCombatStance.Defensive,
        Quests =
        [
            new(
                "washed-ashore",
                QuestStatus.Complete,
                new Dictionary<string, int>
                {
                    ["rocks"] = 1,
                    ["sticks"] = 1,
                    ["fibres"] = 1
                },
                DateTime.UtcNow)
        ],
        Inventory = PlayerInventory.Normalize(["logs", "oak_logs"])
    };
    saves.SavePlayer(player);
    var world = saves.CreateWorld("Test Realm", 4321, player.Id);
    Require(!world.AiNpcsEnabled && world.AiNpcCount == 0,
        "world creation must default to a solo world");
    saves.SaveSettlementGroup(world.Id, settlementGroup);
    Require(saves.LoadSettlementGroup(world.Id) is
        {
            Id: var loadedGroupId,
            LeaderId: "group-leader",
            MemberIds.Count: 3,
            CampX: 10,
            CampY: 12
        } && loadedGroupId == settlementGroup.Id,
        "settlement membership, leadership, camp and cache must persist with the world");
    var aiWorld = saves.CreateWorld(
        "AI Realm", 9876, player.Id,
        aiNpcsEnabled: true, aiNpcCount: 2,
        aiNpcPersonas:
        [
            VillagerSimulation.DefaultPersona(0),
            VillagerSimulation.DefaultPersona(1)
        ],
        aiModelOverride: "gemma4:12b",
        skipOpeningCouncil: true,
        islandStart: true);
    Require(aiWorld.AiNpcsEnabled && aiWorld.AiNpcCount == 2 &&
            aiWorld.AiNpcPersonas?.Count == 2 &&
            aiWorld.AiModelOverride == "gemma4:12b" &&
            aiWorld.SkipOpeningCouncil && aiWorld.IslandStart,
        "AI NPC world options, model override, council-skip and opening mode must be stored on the world profile");
    var clampedAiWorld = saves.CreateWorld(
        "Crowded Realm", 6789, player.Id,
        aiNpcsEnabled: true, aiNpcCount: 99);
    Require(clampedAiWorld.AiNpcCount ==
            VillagerSimulation.MaximumPopulation,
        "AI NPC population must be clamped to the supported maximum");
    saves.SaveWorldPlayer(
        world.Id, new(player.Id, 12.5f, -8.25f, DateTime.UtcNow));
    Require(saves.ListPlayers().Single() is var loadedPlayer &&
            loadedPlayer.Id == player.Id &&
            loadedPlayer.WoodcuttingExperience == 725 &&
            loadedPlayer.AdventureExperience == 1200 &&
            loadedPlayer.Health == 111 &&
            loadedPlayer.Hunger == 64 &&
            loadedPlayer.WellFedSeconds == 90 &&
            loadedPlayer.AttackExperience == 350 &&
            loadedPlayer.StrengthExperience == 225 &&
            loadedPlayer.DefenceExperience == 75 &&
            loadedPlayer.CombatStance ==
            MeleeCombatStance.Defensive &&
            loadedPlayer.Quests?.Single().Status ==
            QuestStatus.Complete &&
            loadedPlayer.Quests[0].ObjectiveCounts?["sticks"] == 1 &&
            loadedPlayer.Inventory?.Length == PlayerInventory.Capacity &&
            loadedPlayer.Inventory[0] == "logs" &&
            loadedPlayer.Inventory[1] == "oak_logs" &&
            PlayerInventory.Count(loadedPlayer.Inventory) == 2,
        "character skills, quest progress, and inventory must persist independently");
    var savedWorlds = saves.ListWorlds();
    Require(savedWorlds.Single(value => value.Id == world.Id) is
            { AiNpcsEnabled: false, AiNpcCount: 0 } &&
            savedWorlds.Single(value => value.Id == aiWorld.Id) is
            {
                AiNpcsEnabled: true,
                AiNpcCount: 2,
                AiNpcPersonas.Count: 2
            },
        "named world profiles, AI population settings, and generated cast must round-trip");
    var worldPlayer = saves.LoadWorldPlayer(world.Id, player.Id);
    Require(worldPlayer is not null &&
            worldPlayer.PositionX == 12.5f &&
            worldPlayer.PositionY == -8.25f,
        "character position must be stored per world");
    var persistedVillagers = VillagerSimulation.CreateInitial(
        world.Seed, new(12.5f, -8.25f));
    var rememberedVillager =
        VillagerSimulation.ObserveUnauthorizedTaking(
            persistedVillagers[0],
            Guid.NewGuid(),
            ItemIds.StoneAxe,
            persistedVillagers[0].Id,
            player.Id,
            600,
            1,
            15,
            out _);
    var persistedPromise =
        VillagerCommitmentService.TryAccept(
            rememberedVillager,
            player.Id,
            VillagerPromiseKind.GatherItem,
            ItemIds.Logs,
            2,
            700).Promise!;
    rememberedVillager =
        VillagerCommitmentService.AddPromise(
            rememberedVillager,
            persistedPromise) with
        {
            FollowingActorId = player.Id,
            Energy = 37.5f,
            LastEnergyGameSeconds = 812,
            LocationMemories =
            [
                new(
                    14,
                    9,
                    0,
                    VillagerLocationType.Storage,
                    .8f,
                    810)
            ],
            LastDeliberation = new(
                "Helping build shelter benefits both of us.",
                "accept",
                "help_build",
                85,
                30,
                10,
                90,
                750)
        };
    persistedVillagers[0] = rememberedVillager;
    for (var turn = 0; turn < 20; turn++)
        persistedVillagers[1] =
            VillagerSimulation.RecordDialogueTurn(
                persistedVillagers[1],
                player.Id,
                player.Name,
                $"persistent conversation fact {turn}",
                800 + turn);
    saves.SaveVillagers(world.Id, persistedVillagers);
    var loadedVillagers = saves.LoadVillagers(world.Id);
    Require(
        loadedVillagers.Count ==
            VillagerSimulation.InitialPopulation &&
        loadedVillagers[0].Id == rememberedVillager.Id &&
        loadedVillagers[0].Memories?.Single().SubjectId ==
            player.Id &&
        loadedVillagers[0].Relationships?.Single()
            .OwnershipOffences == 1 &&
        loadedVillagers[0].Goals?.Count == 2 &&
        loadedVillagers[0].Promises?.Single().Id ==
            persistedPromise.Id &&
        loadedVillagers[0].ActionPlan?.Single() is
        {
            Action: VillagerPromisePlanAction.Collect,
            ItemId: ItemIds.Logs,
            RemainingQuantity: 2
        } &&
        loadedVillagers[0].FollowingActorId == player.Id &&
        loadedVillagers[0].Energy == 37.5f &&
        loadedVillagers[0].LastEnergyGameSeconds == 812 &&
        loadedVillagers[0].LocationMemories?.Single() is
        {
            PositionX: 14,
            PositionY: 9,
            WorldLevel: 0,
            Type: VillagerLocationType.Storage,
            Confidence: .8f,
            LastObservedGameSeconds: 810
        } &&
        loadedVillagers[0].LastDeliberation is
        {
            Decision: "accept",
            Action: "help_build",
            Priority: 90
        } &&
        loadedVillagers[1].ConversationHistory?.Count ==
            VillagerSimulation.MaximumConversationTurns &&
        loadedVillagers[1].Memories?.Any(memory =>
            memory.Kind == "conversation-heard" &&
            memory.SubjectId == player.Id) == true,
        "villager identities, goals, promises, memories, and directional relationships must persist per world");
    var deathBase = DateTime.UtcNow.AddMinutes(-20);
    for (var index = 0;
         index < PlayerDeathService.MaximumRememberedDeaths + 3;
         index++)
        saves.AddPlayerDeath(
            world.Id,
            player.Id,
            new(
                index,
                -index,
                index % 2,
                index % 2 == 0
                    ? EntityGender.Male
                    : EntityGender.Female,
                deathBase.AddMinutes(index),
                FacingX: -1,
                FacingY: 1));
    var deaths = saves.LoadPlayerDeaths(world.Id, player.Id);
    Require(
        deaths.Count == PlayerDeathService.MaximumRememberedDeaths &&
        deaths[0].PositionX == 12 &&
        deaths[^1].PositionX == 3 &&
        deaths[0].WorldLevel == 0 &&
        deaths[1].Gender == EntityGender.Female &&
        deaths[0].FacingX == -1 &&
        deaths[0].FacingY == 1,
        "death markers must persist newest-first with position, layer, facing, gender, and a ten-marker cap");
    saves.AddVillagerDeath(
        world.Id,
        new(
            7, 8, 0, EntityGender.Female,
            DateTime.UtcNow,
            FacingX: -1,
            FacingY: 0,
            Name: "Margery",
            Cause: "Died from starvation."));
    Require(saves.LoadVillagerDeaths(world.Id).Single() is
        {
            PositionX: 7,
            PositionY: 8,
            Name: "Margery",
            Cause: "Died from starvation."
        },
        "permanent villager remains must persist identity, cause, position, facing, level, and gender");
    saves.DeletePlayer(player.Id);
    Require(saves.ListPlayers().Count == 0 &&
            saves.LoadWorldPlayer(world.Id, player.Id) is null &&
            saves.LoadPlayerDeaths(world.Id, player.Id).Count == 0,
        "deleting a character must remove its world-specific states");
    saves.DeleteWorld(world.Id);
    saves.DeleteWorld(aiWorld.Id);
    saves.DeleteWorld(clampedAiWorld.Id);
    Require(saves.ListWorlds().Count == 0,
        "confirmed world deletion must remove its saved world directory");
}
finally
{
    var resolvedRoot = Path.GetFullPath(root);
    var resolvedTemp = Path.GetFullPath(Path.GetTempPath());
    if (!resolvedRoot.StartsWith(resolvedTemp, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing to remove a test directory outside the temp folder.");
    if (Directory.Exists(resolvedRoot)) Directory.Delete(resolvedRoot, recursive: true);
}

var personalGoalIds = new[]
{
    ItemIds.WildGrainSeeds, ItemIds.WildGrain,
    ItemIds.BeanSeeds, ItemIds.Beans,
    ItemIds.RootSeeds, ItemIds.EdibleRoots,
    ItemIds.PortableTorch, ItemIds.GatheringBasket,
    ItemIds.Pearl, ItemIds.StoneSickle
};
Require(
    personalGoalIds.Select(ItemCatalog.Get).All(item =>
        item.HasTag(ItemTag.PersonalGoalSprite) &&
        item.SpriteCell is >= 0 and < 10) &&
    personalGoalIds.Select(ItemCatalog.Get)
        .Select(item => item.SpriteCell).Distinct().Count() == 10,
    "personal-goal items must use ten unique generated sprite cells");
var personalGoalSheetPath = Path.Combine(
    AppContext.BaseDirectory, "Resources", "Images",
    ItemSpriteSheetCatalog.PersonalGoals.FileName);
using (var personalGoalSheetStream = File.OpenRead(personalGoalSheetPath))
{
    var personalGoalSheet = ImageResult.FromStream(
        personalGoalSheetStream, ColorComponents.RedGreenBlueAlpha);
    Require(
        personalGoalSheet.Width ==
            ItemSpriteSheetCatalog.PersonalGoals.Width &&
        personalGoalSheet.Height ==
            ItemSpriteSheetCatalog.PersonalGoals.Height &&
        personalGoalSheet.Data.Where((_, index) => index % 4 == 3)
            .Any(alpha => alpha == 0),
        "converted personal-goal sprite sheet must have the catalog dimensions and transparency");
}
Require(
    CraftingSkill.Recipes.Any(recipe =>
        recipe.ResultItemId == ItemIds.StoneSickle) &&
    CraftingSkill.Recipes.Any(recipe =>
        recipe.ResultItemId == ItemIds.PortableTorch) &&
    CraftingSkill.Recipes.Any(recipe =>
        recipe.ResultItemId == ItemIds.GatheringBasket) &&
    CraftingSkill.Recipes.Any(recipe =>
        recipe.ResultItemId == ItemIds.Pearl &&
        recipe.RequiredTools?.Any(tool =>
            tool.Tag == ItemTag.Knife) == true),
    "new tools, basket, torch and pearl must have complete crafting recipes");
Require(
    PlayerInventory.BestSickle(
        [ItemIds.StoneSickle, ItemIds.IronSickle])?.Id ==
        ItemIds.IronSickle &&
    FarmingSkill.GatheringBasketBonus(
        [ItemIds.GatheringBasket]) == 1 &&
    FarmingSkill.GatheringBasketBonus([]) == 0,
    "stone sickles and gathering baskets must integrate with farming helpers");
Require(
    SurvivalService.TryFoodEffect(ItemIds.WildGrain, out var grainFood) &&
    SurvivalService.TryFoodEffect(ItemIds.Beans, out var beanFood) &&
    SurvivalService.TryFoodEffect(ItemIds.EdibleRoots, out var rootFood) &&
    grainFood.HungerRestored < beanFood.HungerRestored &&
    beanFood.HungerRestored < rootFood.HungerRestored,
    "all harvested crops must provide ordered survival food effects");
Require(
    SurvivalService.TryFoodEffect(ItemIds.Seaweed, out var seaweedFood) &&
    seaweedFood.HungerRestored == 6 &&
    seaweedFood.HealthRestored == 0 &&
    VillagerLocationMemoryService.LocationTypeForItem(ItemIds.Seaweed) ==
        VillagerLocationType.FoodSource &&
    ItemContainerState.Category(ItemCatalog.Get(ItemIds.Seaweed)) ==
        ItemContainerCategory.Food,
    "fresh shoreline seaweed must be a modest shared food source");
var plantedCrop = CropService.Plant(
    ItemIds.BeanSeeds, 4.5f, 8.5f, 1_000, "farmer");
Require(
    CropService.IsCrop(plantedCrop) &&
    plantedCrop.ItemId == ItemIds.BeanCrop &&
    ItemCatalog.Get(plantedCrop.ItemId).HasTag(ItemTag.CropSprite) &&
    !CropService.IsReady(
        plantedCrop, 1_000 + CropService.GrowthGameSeconds - 1) &&
    CropService.IsReady(
        plantedCrop, 1_000 + CropService.GrowthGameSeconds) &&
    plantedCrop.FuelItemId == ItemIds.Beans &&
    CropService.HarvestCount([ItemIds.GatheringBasket]) == 3,
    "crop planting must persist its harvest, maturity time, owner and basket yield");
var cropSheetPath = Path.Combine(
    AppContext.BaseDirectory, "Resources", "Images",
    ItemSpriteSheetCatalog.Crops.FileName);
using (var cropSheetStream = File.OpenRead(cropSheetPath))
{
    var cropSheet = ImageResult.FromStream(
        cropSheetStream, ColorComponents.RedGreenBlueAlpha);
    Require(
        cropSheet.Width == ItemSpriteSheetCatalog.Crops.Width &&
        cropSheet.Height == ItemSpriteSheetCatalog.Crops.Height,
        "planted crops must use the converted three-cell world sprite sheet");
}
var slimeLootSheetPath = Path.Combine(
    AppContext.BaseDirectory, "Resources", "Images",
    ItemSpriteSheetCatalog.SlimeLoot.FileName);
using (var slimeLootSheetStream = File.OpenRead(slimeLootSheetPath))
{
    var slimeLootSheet = ImageResult.FromStream(
        slimeLootSheetStream, ColorComponents.RedGreenBlueAlpha);
    Require(
        slimeLootSheet.Width == ItemSpriteSheetCatalog.SlimeLoot.Width &&
        slimeLootSheet.Height == ItemSpriteSheetCatalog.SlimeLoot.Height,
        "unique slime drops must use the converted four-cell item sprite sheet");
}
var slimeCraftedSheetPath = Path.Combine(
    AppContext.BaseDirectory, "Resources", "Images",
    ItemSpriteSheetCatalog.SlimeCrafted.FileName);
using (var slimeCraftedSheetStream = File.OpenRead(slimeCraftedSheetPath))
{
    var slimeCraftedSheet = ImageResult.FromStream(
        slimeCraftedSheetStream, ColorComponents.RedGreenBlueAlpha);
    Require(
        slimeCraftedSheet.Width == ItemSpriteSheetCatalog.SlimeCrafted.Width &&
        slimeCraftedSheet.Height == ItemSpriteSheetCatalog.SlimeCrafted.Height,
        "salted fish and poultices must use the converted two-cell item sprite sheet");
}

var slimeFrontPath = Path.Combine(
    AppContext.BaseDirectory, "Resources", "Images", "Combat",
    "slime-sprites.png");
var slimeBackPath = Path.Combine(
    AppContext.BaseDirectory, "Resources", "Images", "Combat",
    "slime-sprites-back.png");
var slimeRig = SlimeSpriteRig.Load(slimeFrontPath, slimeBackPath);
var slimeFront = SlimeSpriteRig.Resolve(
    EntityAction.Idle, new Vector2(1, 1), 0);
var slimeBack = SlimeSpriteRig.Resolve(
    EntityAction.Move, new Vector2(-1, -1), .31);
var slimeUpLeft = SlimeSpriteRig.Resolve(
    EntityAction.Move, new Vector2(-1, 0), 0);
var slimeUpRight = SlimeSpriteRig.Resolve(
    EntityAction.Move, new Vector2(0, -1), 0);
var slimeDownLeft = SlimeSpriteRig.Resolve(
    EntityAction.Move, new Vector2(0, 1), 0);
var slimeDownRight = SlimeSpriteRig.Resolve(
    EntityAction.Move, new Vector2(1, 0), 0);
var slimeAttackEnd = SlimeSpriteRig.Resolve(
    EntityAction.Attack, Vector2.UnitX, 10);
var slimeSpawnEnd = SlimeSpriteRig.Resolve(
    SlimeAnimationState.Spawn, Vector2.UnitY, 10);
var stableSlimeUpFacing = SlimeSpriteRig.StableTravelFacing(
    Vector2.Zero, new(-8, -8), Vector2.UnitY);
var stableSlimeDownFacing = SlimeSpriteRig.StableTravelFacing(
    Vector2.Zero, new(8, 8), -Vector2.UnitY);
var stableSlimeFallback = SlimeSpriteRig.StableTravelFacing(
    Vector2.One, Vector2.One, -Vector2.UnitX);
Require(
    slimeRig.Frame(slimeFront).Width == SlimeSpriteRig.CellSize &&
    !slimeFront.UsesBackSheet &&
    slimeBack.UsesBackSheet && slimeBack.FrameIndex == 1 &&
    slimeUpLeft.UsesBackSheet && slimeUpRight.UsesBackSheet &&
    !slimeDownLeft.UsesBackSheet && !slimeDownRight.UsesBackSheet &&
    SlimeSpriteRig.FacesAwayFromCamera(stableSlimeUpFacing) &&
    !SlimeSpriteRig.FacesAwayFromCamera(stableSlimeDownFacing) &&
    stableSlimeFallback == -Vector2.UnitX &&
    SlimePixelArtFilter.VirtualGrid == 48 &&
    SlimePixelArtFilter.QuantizeShade(.51f) == .5f &&
    SlimePixelArtFilter.QuantizeShade(-1) == 0 &&
    SlimePixelArtFilter.QuantizeShade(2) == 1 &&
    slimeAttackEnd.Completed && slimeAttackEnd.FrameIndex == 7 &&
    slimeSpawnEnd.Completed && slimeSpawnEnd.FrameIndex == 7 &&
    SlimeSpriteRig.SourceState(SlimeAnimationState.Move) ==
        SlimeAnimationState.Idle &&
    SlimeSpriteRig.SourceState(SlimeAnimationState.Attack) ==
        SlimeAnimationState.Idle &&
    Enumerable.Range(0, SlimeSpriteRig.Columns).All(frame =>
        SlimeSpriteRig.AuthoredFrame(
            SlimeAnimationState.Move, frame) is not (2 or 4) &&
        SlimeSpriteRig.HasSingleOpaqueComponent(slimeRig.FrameAt(
            SlimeAnimationState.Move, frame, back: false)) &&
        SlimeSpriteRig.HasSingleOpaqueComponent(slimeRig.FrameAt(
            SlimeAnimationState.Move, frame, back: true)) &&
        SlimeSpriteRig.IsAnchoredBelowOpaquePixels(slimeRig.FrameAt(
            SlimeAnimationState.Move, frame, back: false)) &&
        SlimeSpriteRig.IsAnchoredBelowOpaquePixels(slimeRig.FrameAt(
            SlimeAnimationState.Move, frame, back: true))),
    "the slime rig must validate both sheets and map direction, looping, attacks and spawning");

var slimeEffectProfiles = Enum.GetValues<EnemyKind>()
    .Select(SlimeAttackEffectProfile.For)
    .ToArray();
var slimeAttackEffects = new SlimeAttackEffects();
for (var burst = 0; burst < 20; burst++)
{
    slimeAttackEffects.Burst(
        (EnemyKind)(burst % slimeEffectProfiles.Length),
        Vector2.Zero,
        Vector2.UnitX * 10,
        burst);
}
var slimeAttackLights = slimeAttackEffects.Lights().ToArray();
Require(
    slimeEffectProfiles.Select(profile => profile.LightColor)
        .Distinct().Count() == slimeEffectProfiles.Length &&
    SlimeAttackEffects.Frames().Count() ==
        slimeEffectProfiles.Length * 2 &&
    slimeAttackEffects.Active &&
    slimeAttackEffects.ActiveParticleCount <=
        SlimeAttackEffects.ParticleCapacity &&
    slimeAttackEffects.ActiveLightCount ==
        SlimeAttackEffects.LightCapacity &&
    slimeAttackLights.Length == SlimeAttackEffects.LightCapacity &&
    slimeAttackLights.All(light =>
        light.Intensity > 0 && light.RadiusPixels > 0),
    "slime attacks must use distinct type profiles and bounded particle and light pools");
slimeAttackEffects.Update(2);
Require(
    !slimeAttackEffects.Active &&
    slimeAttackEffects.ActiveParticleCount == 0 &&
    slimeAttackEffects.ActiveLightCount == 0,
    "slime attack particles and their existing-scene lights must expire cleanly");

var grassSpawner = new EnemySpawnerState(
    Guid.NewGuid(), Vector2.Zero, (int)WorldLevel.Overworld,
    Biome.Grassland, [new(EnemyKind.GrassSlime)], MaximumAlive: 8);
var narrowBeachSpawnerFound = EnemySpawnerSiteSelector.TryFind(
    Vector2.Zero, 7319, (int)WorldLevel.Overworld, 0,
    static (_, position) =>
        position.Length <= 3.1f
            ? Biome.Beach
            : Biome.Tundra,
    static (_, _) => false,
    out var narrowBeachSpawner);
var dryBeachSpawnerFound = EnemySpawnerSiteSelector.TryFind(
    Vector2.Zero, 7319, (int)WorldLevel.Overworld, 0,
    static (_, position) =>
        position.Length is >= 6.9f and <= 7.1f
            ? Biome.Beach
            : Biome.Tundra,
    static (_, _) => false,
    out _);
Require(
    narrowBeachSpawnerFound && !dryBeachSpawnerFound &&
    narrowBeachSpawner.Kind == EnemyKind.WaterSlime &&
    narrowBeachSpawner.Biome == Biome.Beach &&
    Math.Abs(narrowBeachSpawner.Position.Length -
             EnemySpawnerSiteSelector.MinimumOverworldRadius) < .01f,
    "enemy spawner discovery must support an active narrow beach without selecting unconfirmed beach pockets from another biome");
var strandedSearchOrigin = new Vector2(12.5f, 10.5f);
var strandedSearchTarget = WorldLevelNavigation.ReachableExplorationTarget(
    7319,
    strandedSearchOrigin,
    strandedSearchOrigin + new Vector2(-8, 0),
    (int)WorldLevel.Overworld);
Require(
    Vector2.DistanceSquared(strandedSearchOrigin, strandedSearchTarget) > 1 &&
    WorldLevelNavigation.IsWalkable(
        7319,
        (int)MathF.Floor(strandedSearchTarget.X),
        (int)MathF.Floor(strandedSearchTarget.Y),
        (int)WorldLevel.Overworld),
    "urgent food exploration must choose a meaningful walkable route when the preferred shoreline ray is blocked");
var escapedInteractionFootprint = GridPathfinder.Find(
    7319,
    Vector2.Zero,
    new Vector2(5, 0),
    worldLevel: (int)WorldLevel.Overworld,
    obstacles:
    [
        new NavigationObstacle(Vector2.Zero, 4, 4)
    ]);
var blockedInteractionFootprint = GridPathfinder.Find(
    7319,
    new Vector2(-5, 0),
    new Vector2(5, 0),
    worldLevel: (int)WorldLevel.Overworld,
    obstacles:
    [
        new NavigationObstacle(Vector2.Zero, 4, 4)
    ]);
Require(
    escapedInteractionFootprint.Count > 0 &&
    escapedInteractionFootprint[^1] == new Vector2(5, 0) &&
    blockedInteractionFootprint.Count > 0 &&
    blockedInteractionFootprint.All(point =>
        !new NavigationObstacle(Vector2.Zero, 4, 4).Contains(point)),
    "pathfinding must let actors leave a starting obstacle overlap without making that obstacle passable to outside routes");
var invalidCaveInteractionStand = new Vector2(
    75.07983f, -15.079827f);
var caveEntranceStand = new Vector2(84.5f, -16.5f);
var recoveredCaveRoute = GridPathfinder.Find(
    7319,
    invalidCaveInteractionStand,
    caveEntranceStand,
    worldLevel: (int)WorldLevel.Underground);
Require(
    !GridPathfinder.CanStandAt(
        7319, invalidCaveInteractionStand,
        (int)WorldLevel.Underground) &&
    GridPathfinder.CanStandAt(
        7319, caveEntranceStand,
        (int)WorldLevel.Underground) &&
    recoveredCaveRoute.Count > 0 &&
    recoveredCaveRoute[^1] == caveEntranceStand,
    "cave routing must reject wall-side interaction endpoints and recover a route from an already-invalid stand point");
var distantSpawner = EnemySpawnerService.Update(
    grassSpawner, [],
    [new("player", new Vector2(100, 100), 0, true, 20, true)],
    0, 2187);
var firstWave = EnemySpawnerService.Update(
    grassSpawner, [],
    [new("player", Vector2.Zero, 0, true, 20, true),
     new("villager", Vector2.One, 0, true, 10)],
    0, 2187);
Require(
    !distantSpawner.Active && distantSpawner.Enemies.Count == 0 &&
    firstWave.Active && firstWave.SpawnedWave &&
    firstWave.Enemies.Count is >= 3 and <= 8 &&
    firstWave.Enemies.All(enemy =>
        enemy.Kind == EnemyKind.GrassSlime && enemy.PowerLevel >= 1),
    "enemy spawners must activate only near actors and scale a biome-compatible wave");
var enemyRecovery = EnemySpawnerService.Update(
    firstWave.Spawner, [],
    [new("player", Vector2.Zero, 0, true, 20, true)],
    10, 2187);
var waiting = EnemySpawnerService.Update(
    enemyRecovery.Spawner, [],
    [new("player", Vector2.Zero, 0, true, 20, true)],
    10 + EnemySpawnerService.RecoveryGameSeconds - .01, 2187);
var nextWave = EnemySpawnerService.Update(
    waiting.Spawner, [],
    [new("player", Vector2.Zero, 0, true, 20, true)],
    10 + EnemySpawnerService.RecoveryGameSeconds, 2187);
Require(
    enemyRecovery.StartedRecovery && !enemyRecovery.SpawnedWave &&
    waiting.Enemies.Count == 0 && nextWave.SpawnedWave &&
    nextWave.Spawner.Wave == 2 &&
    EnemySpawnerService.RecoveryGameSeconds ==
        EnemySpawnerService.RecoveryRealSeconds *
        VillagerSimulation.GameSecondsPerRealSecond,
    "a cleared enemy wave must remain empty through recovery before adapting the next wave");
Require(
    EnemyWavePresentation.Message(firstWave) ==
        "Wave 1: 5 grass slimes emerge nearby." &&
    EnemyWavePresentation.Message(enemyRecovery) ==
        "Wave 1 cleared. The area grows quiet for a short while." &&
    EnemyWavePresentation.Message(waiting) is null,
    "enemy wave presentation must announce one-shot starts and clears without repeating while waiting");
var passiveSlime = firstWave.Enemies[0];
var firstLootRoll = LootBagService.Roll(passiveSlime, 2187);
var repeatedLootRoll = LootBagService.Roll(passiveSlime, 2187);
Require(
    firstLootRoll.Count > 0 &&
    firstLootRoll.SequenceEqual(repeatedLootRoll) &&
    firstLootRoll.All(drop =>
        drop.Quantity > 0 && ItemCatalog.TryGet(drop.ItemId, out _)),
    "enemy loot rolls must be seeded, repeatable, non-empty, and contain only catalogued items");
var slimeLootIds = new[]
{
    ItemIds.SlimeGel,
    ItemIds.SlimeCore,
    ItemIds.SaltCrystals,
    ItemIds.MedicinalHerbs
};
Require(
    slimeLootIds.Select(ItemCatalog.Get).Select(item => item.SpriteCell)
        .SequenceEqual(new int?[] { 0, 1, 2, 3 }) &&
    slimeLootIds.All(itemId =>
    {
        var item = ItemCatalog.Get(itemId);
        return item.CanStack && item.HasTag(ItemTag.SlimeLootSprite) &&
               item.HasTag(ItemTag.NaturalMaterial) &&
               !string.IsNullOrWhiteSpace(item.Examine);
    }) &&
    firstLootRoll.Any(drop => drop.ItemId == ItemIds.SlimeGel) &&
    LootBagService.BiomeReagent(EnemyKind.WaterSlime) ==
        ItemIds.SaltCrystals &&
    LootBagService.BiomeReagent(EnemyKind.SandSlime) ==
        ItemIds.SaltCrystals &&
    LootBagService.BiomeReagent(EnemyKind.GrassSlime) ==
        ItemIds.MedicinalHerbs,
    "all unique slime drops must be stackable, examinable, sprite-backed, and integrated into their biome loot roles");
var reinforcedNetRecipe = CraftingSkill.Recipes.Single(recipe =>
    recipe.ResultItemId == ItemIds.ReinforcedFishingNet);
var advancedNetRecipe = CraftingSkill.Recipes.Single(recipe =>
    recipe.ResultItemId == ItemIds.AdvancedFishingNet);
var portableTorchRecipe = CraftingSkill.Recipes.Single(recipe =>
    recipe.ResultItemId == ItemIds.PortableTorch);
var saltedFishRecipe = CraftingSkill.Recipes.Single(recipe =>
    recipe.ResultItemId == ItemIds.SaltedFish);
var herbalPoulticeRecipe = CraftingSkill.Recipes.Single(recipe =>
    recipe.ResultItemId == ItemIds.HerbalPoultice);
Require(
    reinforcedNetRecipe.Ingredients.Single(ingredient =>
        ingredient.ItemId == ItemIds.Rope).Accepts(ItemIds.SlimeGel) &&
    portableTorchRecipe.Ingredients.Single(ingredient =>
        ingredient.ItemId == ItemIds.PlantFibres).Accepts(ItemIds.SlimeGel) &&
    advancedNetRecipe.Ingredients.Any(ingredient =>
        ingredient.ItemId == ItemIds.SlimeCore) &&
    saltedFishRecipe.Ingredients.Any(ingredient =>
        ingredient.ItemId == ItemIds.SaltCrystals) &&
    saltedFishRecipe.Ingredients[0].Accepts(ItemIds.CookedBluefinTuna) &&
    herbalPoulticeRecipe.Ingredients.Any(ingredient =>
        ingredient.ItemId == ItemIds.MedicinalHerbs) &&
    VillagerCraftPlanner.PriorityFor(VillagerWorkRole.Food)
        .Contains(ItemIds.SaltedFish) &&
    VillagerCraftPlanner.PriorityFor(VillagerWorkRole.Crafting)
        .Contains(ItemIds.HerbalPoultice),
    "slime materials must feed reusable recipe alternatives, advanced progression, preservation, medicine, and NPC planning");
SurvivalService.TryFoodEffect(ItemIds.MedicinalHerbs, out var herbMedicine);
SurvivalService.TryFoodEffect(ItemIds.HerbalPoultice, out var poulticeMedicine);
SurvivalService.TryFoodEffect(ItemIds.SaltedFish, out var saltedFood);
Require(
    herbMedicine.TimedHealing == 8 &&
    poulticeMedicine.TimedHealing > herbMedicine.TimedHealing &&
    saltedFood.HungerRestored > 0 &&
    saltedFood.WellFedSeconds > 220 &&
    !ItemCatalog.Get(ItemIds.HerbalPoultice).CanStack &&
    ItemCatalog.Get(ItemIds.HerbalPoultice).HasTag(ItemTag.Medicine) &&
    ItemCatalog.Get(ItemIds.SaltedFish).HasTag(ItemTag.SlimeCraftedSprite),
    "medicine and preserved fish must expose their survival effects without becoming stackable slime drops");
var healingStart = TimedHealingService.Start(poulticeMedicine);
var healingHalf = TimedHealingService.Advance(
    40, 100, poulticeMedicine.TimedHealingSeconds / 2, healingStart);
var healingComplete = TimedHealingService.Advance(
    healingHalf.Health, 100,
    poulticeMedicine.TimedHealingSeconds / 2, healingHalf.State);
Require(
    healingStart.Active &&
    healingHalf.Health > 40 && healingHalf.Health < 58 &&
    healingComplete.Health == 58 && !healingComplete.State.Active,
    "medicine must recover health progressively through the reusable timed-healing service");
var healingVillager = new VillagerState(
    "healing-villager", "Leofric", EntityGender.Male,
    0, 0, 0, 0, 0, 40, 50, new string?[PlayerInventory.Capacity],
    LastSimulatedGameSeconds: 1_000,
    TimedHealingRemaining: poulticeMedicine.TimedHealing,
    TimedHealingSeconds: poulticeMedicine.TimedHealingSeconds);
var caughtUpHealingVillager = VillagerSimulation.CatchUp(
    healingVillager,
    1_000 + poulticeMedicine.TimedHealingSeconds *
    VillagerSimulation.GameSecondsPerRealSecond);
var interruptedHealingVillager = VillagerSimulation.RecordAttack(
    healingVillager, "attacker", "Raider", 1, 1_001);
Require(
    caughtUpHealingVillager.Health > healingVillager.Health &&
    caughtUpHealingVillager.TimedHealingRemaining == 0 &&
    interruptedHealingVillager.TimedHealingRemaining == 0 &&
    interruptedHealingVillager.TimedHealingSeconds == 0,
    "NPC catch-up must preserve timed medicine while incoming damage cancels the treatment");
var damagedSlime = EnemyCombatService.ApplyHit(
    passiveSlime, 3, "player");
var ignoredEnemyDamage = EnemyCombatService.ApplyHit(
    passiveSlime, 0, "player");
var killedSlime = EnemyCombatService.ApplyHit(
    passiveSlime, passiveSlime.Health, "player", 12.5);
var deathFrame = SlimeSpriteRig.Resolve(
    killedSlime.VisualAction, Vector2.UnitY, .25);
var retainedDeadWave = EnemySpawnerService.Update(
    firstWave.Spawner,
    [killedSlime],
    [new("player", Vector2.Zero, 0, true, 20, true)],
    10,
    2187);
var nearbyActor = new EnemyActorPresence(
    "player", passiveSlime.Position + Vector2.UnitX, 0, true, 10, true);
var passiveUpdate = EnemySpawnerService.UpdateController(
    passiveSlime, [nearbyActor], 1, .1f, 2187);
var provokedUpdate = EnemySpawnerService.UpdateController(
    EnemySpawnerService.Provoke(passiveSlime, "player"),
    [nearbyActor], 1, .1f, 2187);
var reactedUpdate = EnemySpawnerService.UpdateController(
    provokedUpdate, [nearbyActor], provokedUpdate.AggroReadyAt, .1f, 2187);
var hitWhileReacting = EnemyCombatService.ApplyHit(
    provokedUpdate, 1, "player");
var caveEnemy = passiveSlime with
{
    Kind = EnemyKind.CaveSlime,
    WorldLevel = (int)WorldLevel.Underground,
    SpawnPosition = Vector2.Zero,
    Position = Vector2.Zero,
    Destination = Vector2.Zero
};
var caveUpdate = EnemySpawnerService.UpdateController(
    caveEnemy,
    [new("villager", Vector2.UnitX * 3,
        (int)WorldLevel.Underground, true)],
    1, .1f, 2187);
var caveReactedUpdate = EnemySpawnerService.UpdateController(
    caveUpdate,
    [new("villager", Vector2.UnitX * 3,
        (int)WorldLevel.Underground, true)],
    caveUpdate.AggroReadyAt, .1f, 2187);
var caveArrivalGrace = EnemySpawnerService.UpdateController(
    caveEnemy with
    {
        TargetId = "player",
        Behavior = EnemyBehavior.Attack
    },
    [new("player", Vector2.UnitX,
        (int)WorldLevel.Underground, true, 10, true,
        CanBeTargeted: false)],
    2, .1f, 2187);
var caveWaveDuringArrivalGrace = EnemySpawnerService.Update(
    new EnemySpawnerState(
        Guid.NewGuid(), Vector2.Zero,
        (int)WorldLevel.Underground, Biome.Rock,
        [new(EnemyKind.CaveSlime)]),
    [],
    [new("player", Vector2.Zero,
        (int)WorldLevel.Underground, true, 10, true,
        CanBeTargeted: false)],
    2, 2187);
var activeCaveThreat = EnemyThreatService.HasActiveThreat(
    [caveEnemy with
    {
        TargetId = "player",
        Behavior = EnemyBehavior.Attack
    }],
    "player");
var harmlessCavePresence = EnemyThreatService.HasActiveThreat(
    [caveEnemy with
    {
        TargetId = null,
        Behavior = EnemyBehavior.Roam
    }],
    "player");
var leashed = EnemySpawnerService.UpdateController(
    EnemySpawnerService.Provoke(
        passiveSlime with
        {
            Position = passiveSlime.SpawnPosition + Vector2.UnitX * 11
        }, "player"),
    [new("player",
        passiveSlime.SpawnPosition + Vector2.UnitX * 30, 0, true)],
    1, .1f, 2187);
var disengaged = EnemySpawnerService.UpdateController(
    EnemySpawnerService.Provoke(passiveSlime, "player") with
    {
        TargetId = "player",
        Behavior = EnemyBehavior.Chase,
        Path = [passiveSlime.SpawnPosition + Vector2.UnitX]
    },
    [new("player",
        passiveSlime.SpawnPosition + Vector2.UnitX *
        (EnemySpawnerService.LeashRadius + 1), 0, true)],
    2, .1f, 2187);
Require(
    new SlimeEnemyController() is EnemyController &&
    EnemyInteractionMenu.Options.SequenceEqual(
        ["Walk Here", "Attack", "Examine"]) &&
    damagedSlime.Health == passiveSlime.Health - 3 &&
    damagedSlime.ProvokedById == "player" &&
    ignoredEnemyDamage == passiveSlime &&
    !killedSlime.Alive &&
    killedSlime.Behavior == EnemyBehavior.Dead &&
    killedSlime.VisualAction == EntityAction.Die &&
    killedSlime.VisualActionStartedAt == 12.5 &&
    killedSlime.Path is null && killedSlime.TargetId is null &&
    deathFrame.State == SlimeAnimationState.Die &&
    retainedDeadWave.StartedRecovery &&
    retainedDeadWave.Enemies.Contains(killedSlime) &&
    !SlimeSpriteRig.DeathAnimationComplete(
        SlimeSpriteRig.DeathAnimationSeconds - .01) &&
    SlimeSpriteRig.DeathAnimationComplete(
        SlimeSpriteRig.DeathAnimationSeconds) &&
    passiveUpdate.TargetId is null &&
    provokedUpdate.TargetId == "player" &&
    provokedUpdate.Behavior == EnemyBehavior.Idle &&
    provokedUpdate.AggroReadyAt == 1.8 &&
    hitWhileReacting.AggroReadyAt == provokedUpdate.AggroReadyAt &&
    reactedUpdate.Behavior == EnemyBehavior.Attack &&
    caveUpdate.TargetId == "villager" &&
    caveUpdate.Behavior == EnemyBehavior.Idle &&
    caveUpdate.AggroReadyAt == 1.25 &&
    caveReactedUpdate.Behavior == EnemyBehavior.Chase &&
    caveArrivalGrace.TargetId is null &&
    caveArrivalGrace.Behavior == EnemyBehavior.Return &&
    caveWaveDuringArrivalGrace.Active &&
    caveWaveDuringArrivalGrace.SpawnedWave &&
    caveWaveDuringArrivalGrace.Enemies.Count > 0 &&
    EnemySpawnerService.WorldTransitionGraceSeconds == 5 &&
    activeCaveThreat && !harmlessCavePresence &&
    leashed.TargetId is null && leashed.Behavior == EnemyBehavior.Return &&
    disengaged.TargetId is null && disengaged.ProvokedById is null &&
    disengaged.Behavior == EnemyBehavior.Return &&
    disengaged.Path is null &&
    EnemySpawnerService.Supports(
        EnemyKind.WaterSlime, Biome.Beach, 0) &&
    EnemySpawnerService.Supports(
        EnemyKind.SandSlime, Biome.DesertSand, 0) &&
    EnemySpawnerService.Supports(
        EnemyKind.CaveSlime, Biome.Rock, -1),
    "passive slimes must require provocation while cave slimes aggro and every enemy obeys its leash");

Console.WriteLine(
    $"World checks passed: {macroBiomes.Count} macro biomes, deterministic generation, seams, " +
    $"persistence, and 64-slot region storage ({regionBytes:N0} bytes for the test region). " +
    $"Assertions passed: {worldCheckAssertions:N0}.");

void Require(bool condition, string message)
{
    worldCheckAssertions++;
    if (!condition) throw new InvalidOperationException(message);
}

static int PositiveMod(int value, int divisor)
{
    var result = value % divisor;
    return result < 0 ? result + divisor : result;
}

static WorldChunk CloneAt(WorldChunk source, ChunkCoordinate coordinate) => new()
{
    Coordinate = coordinate,
    Tiles = source.Tiles,
    Trees = source.Trees,
    BiomeWeightsA = source.BiomeWeightsA,
    BiomeWeightsB = source.BiomeWeightsB,
    BiomeWeightsC = source.BiomeWeightsC,
    BiomeWeightsD = source.BiomeWeightsD,
    ShoreDistance = source.ShoreDistance,
    Cliffs = source.Cliffs,
    RenderableTiles = source.RenderableTiles,
    TreeInstances = source.TreeInstances.ToList(),
    GroundObjects = source.GroundObjects.ToList(),
    Vegetation = source.Vegetation,
    Fish = source.Fish
};

static async Task<bool> RunLiveArrivalScenario(string model)
{
    var failures = new List<string>();
    var settings = new NpcAiSettings(Model: model);
    using var ai = new NpcAiService();
    var state = await ai.CheckAsync(settings);
    if (!state.Ready)
    {
        Console.Error.WriteLine(
            $"ARRIVAL SCENARIO FAIL [{model}]: {state.Message}");
        return false;
    }

    var history = new List<VillagerConversationTurn>();
    var memories = new List<string>();
    var replies = new List<string>();
    var actors = new[]
    {
        new NpcAiActor(
            "samuel", "Samuel", 1, 82, "unknown survivor"),
        new NpcAiActor(
            "mira", "Mira", 0, 78, "unknown survivor")
    };
    const string background =
        "A carpenter from a small harbour town.";
    const string personality =
        "Careful, observant, practical, and willing to cooperate.";
    const string arrivalMemory =
        "Woke on the beach after rough water, cold and confused, " +
        "with no clear memory of the wreck.";

    var opening = await ai.ComposeDialogueAsync(
        settings,
        new(
            "Mira",
            "Samuel",
            "ArrivalOrientation",
            "I just woke on this beach. Are you hurt, and what is your name?",
            78,
            "unknown survivor",
            [],
            background,
            personality,
            "Carpenter",
            [ItemIds.StoneAxe, ItemIds.StoneHammer],
            arrivalMemory,
            0));
    var openingReply = opening ?? "";
    Console.WriteLine(
        $"ARRIVAL LIVE [{model}] 00:00 Mira => " +
        (openingReply.Length == 0 ? "<null>" : openingReply));
    if (openingReply.Length == 0 ||
        !ContainsAny(
            openingReply,
            "woke", "beach", "hurt", "name", "where"))
        failures.Add(
            $"00:00 failed to orient toward the nearby survivor: {openingReply}");
    history.Add(new(
        "mira", "Mira", openingReply, 0));
    replies.Add(openingReply);

    async Task Ask(
        int elapsedSeconds,
        string speech,
        string label,
        params string[] expectedTerms)
    {
        var gameSeconds =
            elapsedSeconds *
            VillagerSimulation.GameSecondsPerRealSecond;
        history.Add(new(
            "samuel", "Samuel", speech, gameSeconds));
        var interpretation = await ai.InterpretAsync(
            settings,
            new(
                "samuel",
                "Samuel",
                "mira",
                "Mira",
                speech,
                actors,
                ["Stay alive", "Learn who can be trusted"],
                memories,
                background,
                personality,
                "Carpenter",
                [ItemIds.StoneAxe, ItemIds.StoneHammer],
                arrivalMemory,
                elapsedSeconds / 3600d,
                history));
        var reply = interpretation?.Reply ?? "";
        var normalizedReply = reply
            .ToLowerInvariant()
            .Replace('’', '\'');
        var claimsSamuelIdentity =
            normalizedReply.Contains("i'm samuel") ||
            normalizedReply.Contains("i am samuel") ||
            normalizedReply.Contains("my name is samuel");
        Console.WriteLine(
            $"ARRIVAL LIVE [{model}] " +
            $"{elapsedSeconds / 60:00}:{elapsedSeconds % 60:00} " +
            $"Samuel: {speech} => Mira: " +
            (reply.Length == 0 ? "<null>" : reply));
        if (reply.Length == 0 ||
            reply.StartsWith(
                "I heard you", StringComparison.OrdinalIgnoreCase) ||
            replies.Any(previous =>
                string.Equals(
                    previous,
                    reply,
                    StringComparison.OrdinalIgnoreCase)) ||
            claimsSamuelIdentity ||
            !ContainsAny(reply, expectedTerms))
            failures.Add(
                $"{label} was empty, generic, repeated, identity-confused, or off-topic: {reply}");
        history.Add(new(
            "mira", "Mira", reply, gameSeconds + 1));
        replies.Add(reply);
        if (!string.IsNullOrWhiteSpace(
                interpretation?.Memory))
            memories.Add(interpretation.Memory);
    }

    await Ask(
        30,
        "I'm Samuel. Are you hurt?",
        "00:30 injury and identity response",
        "Mira", "hurt", "fine", "okay", "cold", "confused",
        "injured", "Samuel");
    await Ask(
        60,
        "Do you remember how we got here?",
        "01:00 arrival-memory response",
        "remember", "water", "beach", "wreck", "woke",
        "storm", "ship");
    await Ask(
        90,
        "We need to work together.",
        "01:30 cooperation response",
        "together", "agree", "help", "survive", "safe",
        "shelter");
    await Ask(
        120,
        "We should find food, fresh water, and shelter.",
        "02:00 survival-priority response",
        "food", "water", "shelter", "supplies", "agree",
        "first");
    await Ask(
        150,
        "Let's gather rocks and wood for a shelter.",
        "02:30 shared-task response",
        "rock", "stone", "wood", "gather", "collect",
        "shelter", "axe");
    await Ask(
        180,
        "I'll look nearby. Stay close.",
        "03:00 coordinated-action response",
        "close", "stay", "careful", "nearby", "together",
        "watch", "safe");

    if (history.Count != 13)
        failures.Add(
            $"context brain lost arrival turns: expected 13, got {history.Count}");
    var compact = NpcAiService.CompactConversation(history);
    if (compact.Count != 8 ||
        compact[^1].Text != replies[^1])
        failures.Add(
            "three-minute transcript did not compact to the newest coherent turns");

    if (failures.Count == 0)
    {
        Console.WriteLine(
            $"ARRIVAL SCENARIO PASS [{model}] " +
            $"(7 NPC turns, {history.Count} total turns, " +
            $"{compact.Count} prompt turns)");
        return true;
    }
    Console.Error.WriteLine(
        $"ARRIVAL SCENARIO FAIL [{model}]");
    foreach (var failure in failures)
        Console.Error.WriteLine($"- {failure}");
    return false;
}

static async Task<bool> RunLiveAiContract(string model)
{
    var failures = new List<string>();
    var settings = new NpcAiSettings(Model: model);
    using var ai = new NpcAiService();
    var state = await ai.CheckAsync(settings);
    if (!state.Ready)
    {
        Console.Error.WriteLine(
            $"AI CONTRACT FAIL [{model}]: {state.Message}");
        return false;
    }

    var introduction = await ai.ComposeDialogueAsync(
        settings,
        new(
            "Mira",
            "Samuel",
            "Introduce",
            "I'm Mira. What's your name?",
            80,
            "neutral",
            [],
            "A carpenter from a harbour town.",
            "Careful and curious.",
            "Carpenter",
            [ItemIds.StoneAxe, ItemIds.StoneHammer],
            "Woke on the beach this morning.",
            .25));
    if (introduction is null ||
        !introduction.Contains(
            "Mira", StringComparison.OrdinalIgnoreCase) ||
        !(introduction.Contains(
              "name", StringComparison.OrdinalIgnoreCase) ||
          introduction.Contains('?')))
        failures.Add(
            $"introduction lost required meaning: {introduction ?? "<null>"}");

    var toolQuestion = await ai.ComposeDialogueAsync(
        settings,
        new(
            "Mira",
            "Samuel",
            "AskTools",
            "I was a carpenter. Do you know how to use a stone hammer?",
            80,
            "neutral",
            [],
            "A carpenter from a harbour town.",
            "Careful and curious.",
            "Carpenter",
            [ItemIds.StoneAxe, ItemIds.StoneHammer],
            "Woke on the beach this morning.",
            2));
    if (toolQuestion is null ||
        !toolQuestion.Contains(
            "hammer", StringComparison.OrdinalIgnoreCase) ||
        !toolQuestion.Contains('?'))
        failures.Add(
            $"tool question lost grounded action: {toolQuestion ?? "<null>"}");

    var sharedActors = new[]
    {
        new NpcAiActor(
            "speaker", "Samuel", 1, 80, "new acquaintance"),
        new NpcAiActor(
            "mira", "Mira", 0, 80, "new acquaintance")
    };
    var planning = await ai.InterpretAsync(
        settings,
        new(
            "speaker",
            "Samuel",
            "mira",
            "Mira",
            "what should we do?",
            sharedActors,
            ["Survive together"],
            ["Samuel and Mira have just met on the beach."],
            "A carpenter from a harbour town.",
            "Careful, practical, and curious.",
            "Carpenter",
            [ItemIds.StoneAxe, ItemIds.StoneHammer],
            "Woke on the beach after rough water and remembers no clear wreck.",
            .25,
            [
                new(
                    "speaker", "Samuel",
                    "what should we do?", 900)
            ]));
    var planningReply = planning?.Reply ?? "";
    Console.WriteLine(
        $"AI LIVE OUTPUT [{model}] what should we do? => " +
        (planningReply.Length == 0 ? "<null>" : planningReply));
    if (planning is not null)
        Console.WriteLine(
            $"AI LIVE STRUCTURED [{model}] planning => " +
            System.Text.Json.JsonSerializer.Serialize(planning));
    if (planningReply.Length == 0 ||
        planningReply.StartsWith(
            "I heard you", StringComparison.OrdinalIgnoreCase) ||
        !ContainsAny(
            planningReply,
            "food", "water", "shelter", "supplies", "safe",
            "together", "explore", "wood", "fire"))
        failures.Add(
            $"planning reply was generic or irrelevant: {planningReply}");

    var storm = await ai.InterpretAsync(
        settings,
        new(
            "speaker",
            "Samuel",
            "mira",
            "Mira",
            "I think there was a storm",
            sharedActors,
            ["Survive together"],
            [
                "Samuel asked what they should do next.",
                $"Mira replied: {planningReply}"
            ],
            "A carpenter from a harbour town.",
            "Careful, practical, and curious.",
            "Carpenter",
            [ItemIds.StoneAxe, ItemIds.StoneHammer],
            "Woke on the beach after rough water and remembers no clear wreck.",
            .25,
            [
                new(
                    "speaker", "Samuel",
                    "what should we do?", 900),
                new(
                    "mira", "Mira",
                    planningReply, 910),
                new(
                    "speaker", "Samuel",
                    "I think there was a storm", 920)
            ]));
    var stormReply = storm?.Reply ?? "";
    Console.WriteLine(
        $"AI LIVE OUTPUT [{model}] I think there was a storm => " +
        (stormReply.Length == 0 ? "<null>" : stormReply));
    if (storm is not null)
        Console.WriteLine(
            $"AI LIVE STRUCTURED [{model}] storm => " +
            System.Text.Json.JsonSerializer.Serialize(storm));
    if (stormReply.Length == 0 ||
        stormReply.StartsWith(
            "I heard you", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            stormReply.Trim().TrimEnd('.', '!', '?'),
            "I think there was a storm",
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            stormReply, planningReply,
            StringComparison.OrdinalIgnoreCase) ||
        !ContainsAny(
            stormReply,
            "storm", "wreck", "water", "wave", "weather",
            "remember", "possible", "could", "might"))
        failures.Add(
            $"storm reply was generic, repeated, or irrelevant: {stormReply}");

    var cooperation = await ai.InterpretAsync(
        settings,
        new(
            "speaker",
            "Samuel",
            "mira",
            "Mira",
            "we need to work together",
            sharedActors,
            ["Survive together"],
            [
                "Samuel and Mira agreed that a storm may have caused the wreck."
            ],
            "A carpenter from a harbour town.",
            "Careful, practical, and curious.",
            "Carpenter",
            [ItemIds.StoneAxe, ItemIds.StoneHammer],
            "Woke on the beach after rough water and remembers no clear wreck.",
            .3,
            [
                new(
                    "speaker", "Samuel",
                    "Do you remember your family?", 1_000),
                new(
                    "mira", "Mira",
                    "I don't remember my family, only the ship breaking apart before I woke here.",
                    1_010),
                new(
                    "speaker", "Samuel",
                    "we need to work together", 1_020)
            ]));
    var cooperationReply = cooperation?.Reply ?? "";
    Console.WriteLine(
        $"AI LIVE OUTPUT [{model}] we need to work together => " +
        (cooperationReply.Length == 0
            ? "<null>"
            : cooperationReply));
    if (cooperationReply.Length == 0 ||
        cooperationReply.StartsWith(
            "I heard you", StringComparison.OrdinalIgnoreCase) ||
        !ContainsAny(
            cooperationReply,
            "together", "agree", "help", "survive", "plan",
            "shelter", "supplies", "safe"))
        failures.Add(
            $"cooperation reply was generic or irrelevant: {cooperationReply}");

    var rocks = await ai.InterpretAsync(
        settings,
        new(
            "speaker",
            "Samuel",
            "mira",
            "Mira",
            "lets get rocks",
            sharedActors,
            ["Survive together"],
            [
                "Samuel and Mira agreed that a storm may have caused the wreck.",
                "Samuel proposed working together."
            ],
            "A carpenter from a harbour town.",
            "Careful, practical, and curious.",
            "Carpenter",
            [ItemIds.StoneAxe, ItemIds.StoneHammer],
            "Woke on the beach after rough water and remembers no clear wreck.",
            .3,
            [
                new(
                    "speaker", "Samuel",
                    "Do you remember your family?", 1_000),
                new(
                    "mira", "Mira",
                    "I don't remember my family, only the ship breaking apart before I woke here.",
                    1_010),
                new(
                    "speaker", "Samuel",
                    "we need to work together", 1_020),
                new(
                    "mira", "Mira",
                    cooperationReply, 1_030),
                new(
                    "speaker", "Samuel",
                    "lets get rocks", 1_040)
            ]));
    var rocksReply = rocks?.Reply ?? "";
    Console.WriteLine(
        $"AI LIVE OUTPUT [{model}] lets get rocks => " +
        (rocksReply.Length == 0 ? "<null>" : rocksReply));
    if (rocksReply.Length == 0 ||
        rocksReply.StartsWith(
            "I heard you", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            rocksReply,
            cooperationReply,
            StringComparison.OrdinalIgnoreCase) ||
        !ContainsAny(
            rocksReply,
            "rock", "stone", "gather", "collect", "find",
            "get them", "good idea", "all right", "okay"))
        failures.Add(
            $"rock-gathering proposal was generic, repeated, or irrelevant: {rocksReply}");

    var deliberation = await ai.InterpretAsync(
        settings,
        new(
            "speaker",
            "Samuel",
            "mira",
            "Mira",
            "Can you gather 3 logs for our shelter?",
            sharedActors,
            ["HelpPerson:speaker", "StockpileWood:4"],
            ["Samuel and Mira agreed to build shelter together."],
            "A carpenter from a harbour town.",
            "Careful, practical, and curious.",
            "Carpenter",
            [ItemIds.StoneAxe, ItemIds.StoneHammer],
            "Woke on the beach after rough water.",
            .35,
            [
                new(
                    "speaker", "Samuel",
                    "Can you gather 3 logs for our shelter?", 1_080)
            ],
            [
                new(
                    "Samuel proposed a shared shelter.",
                    "speaker",
                    .95f,
                    20,
                    1_070)
            ],
            new(
                100,
                78,
                [ItemIds.StoneAxe],
                "Explore",
                "Idle",
                ["StockpileWood:0/4"],
                [],
                ""),
            [
                new(
                    "logs-nearby",
                    ItemIds.Logs,
                    "ground_item",
                    2.5f,
                    "",
                    true),
                new(
                    "logs-owned",
                    ItemIds.Logs,
                    "ground_item",
                    5,
                    "rowan",
                    true)
            ]));
    Console.WriteLine(
        $"AI LIVE DELIBERATION [{model}] => " +
        System.Text.Json.JsonSerializer.Serialize(deliberation));
    if (deliberation is null ||
        deliberation.PrivateThought.Length == 0 ||
        deliberation.Action != "gather" ||
        deliberation.ItemId != ItemIds.Logs ||
        deliberation.Decision is not
            ("accept" or "negotiate" or "refuse") ||
        deliberation.Willingness is < 0 or > 100 ||
        deliberation.EstimatedCost is < 0 or > 100 ||
        deliberation.Risk is < 0 or > 100 ||
        deliberation.Priority is < 0 or > 100 ||
        deliberation.Reply.Length == 0)
        failures.Add(
            "resource deliberation did not return a grounded private cost-benefit decision");

    var rude = await ai.InterpretAsync(
        settings,
        new(
            "speaker",
            "Samuel",
            "mira",
            "Mira",
            "you are rude",
            sharedActors,
            [],
            ["Mira stopped following after Samuel told her to go away."],
            "A carpenter from a harbour town.",
            "Careful, practical, and curious.",
            "Carpenter",
            [ItemIds.StoneAxe, ItemIds.StoneHammer],
            "Woke on the beach after rough water.",
            .4,
            [
                new(
                    "speaker", "Samuel",
                    "follow me", 1_100),
                new(
                    "mira", "Mira",
                    "All right, I'll stay with you.", 1_110),
                new(
                    "speaker", "Samuel",
                    "go away", 1_120),
                new(
                    "mira", "Mira",
                    "All right. I'll give you some space.", 1_130),
                new(
                    "speaker", "Samuel",
                    "you are rude", 1_140)
            ]));
    var rudeReply = rude?.Reply ?? "";
    Console.WriteLine(
        $"AI LIVE OUTPUT [{model}] you are rude => " +
        (rudeReply.Length == 0 ? "<null>" : rudeReply));
    if (rudeReply.Length == 0 ||
        rudeReply.StartsWith(
            "I heard you", StringComparison.OrdinalIgnoreCase) ||
        !ContainsAny(
            rudeReply,
            "rude", "speak", "talk", "insult", "sorry",
            "leave", "alone", "space", "need", "stop",
            "tolerate", "respect", "subordinate"))
        failures.Add(
            $"rudeness reply lacked a relevant social boundary: {rudeReply}");

    var ugly = await ai.InterpretAsync(
        settings,
        new(
            "speaker",
            "Samuel",
            "mira",
            "Mira",
            "and ugly",
            sharedActors,
            [],
            ["Samuel dismissed and insulted Mira."],
            "A carpenter from a harbour town.",
            "Careful, practical, and curious.",
            "Carpenter",
            [ItemIds.StoneAxe, ItemIds.StoneHammer],
            "Woke on the beach after rough water.",
            .4,
            [
                new(
                    "speaker", "Samuel",
                    "go away", 1_120),
                new(
                    "mira", "Mira",
                    "All right. I'll give you some space.", 1_130),
                new(
                    "speaker", "Samuel",
                    "you are rude", 1_140),
                new(
                    "mira", "Mira",
                    rudeReply, 1_150),
                new(
                    "speaker", "Samuel",
                    "and ugly", 1_160)
            ]));
    var uglyReply = ugly?.Reply ?? "";
    Console.WriteLine(
        $"AI LIVE OUTPUT [{model}] and ugly => " +
        (uglyReply.Length == 0 ? "<null>" : uglyReply));
    if (uglyReply.Length == 0 ||
        uglyReply.StartsWith(
            "I heard you", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            uglyReply, rudeReply,
            StringComparison.OrdinalIgnoreCase) ||
        !ContainsAny(
            uglyReply,
            "rude", "speak", "talk", "insult", "leave",
            "alone", "space", "stop", "won't", "need",
            "tolerate", "respect"))
        failures.Add(
            $"continued insult reply was generic or repeated: {uglyReply}");

    var personas = await ai.GeneratePersonasAsync(
        settings, "Contract Island", 741, ["Mira"]);
    var persona = personas?.SingleOrDefault();
    if (persona is null)
        failures.Add("persona generation returned no valid cast");
    else
    {
        var background = persona.BackgroundStory.ToLowerInvariant();
        if (background.Contains("born on the island") ||
            background.Contains("grew up on the island") ||
            background.Contains("native to the island"))
            failures.Add(
                $"persona violated the day-one arrival timeline: {persona.BackgroundStory}");
        var arrival = persona.ArrivalMemory.ToLowerInvariant();
        if (!(arrival.Contains("wok") ||
              arrival.Contains("wreck") ||
              arrival.Contains("shore") ||
              arrival.Contains("beach") ||
              arrival.Contains("water") ||
              arrival.Contains("ship") ||
              arrival.Contains("overboard") ||
              arrival.Contains("boat") ||
              arrival.Contains("ocean") ||
              arrival.Contains("sea") ||
              arrival.Contains("crash") ||
              arrival.Contains("vessel") ||
              arrival.Contains("coral") ||
              arrival.Contains("wave") ||
              arrival.Contains("salt")))
            failures.Add(
                $"arrival memory was not about arriving: {persona.ArrivalMemory}");
    }

    if (failures.Count == 0)
    {
        Console.WriteLine($"AI CONTRACT PASS [{model}]");
        return true;
    }
    Console.Error.WriteLine($"AI CONTRACT FAIL [{model}]");
    foreach (var failure in failures)
        Console.Error.WriteLine($"- {failure}");
    return false;
}

static bool ContainsAny(string value, params string[] terms) =>
    terms.Any(term =>
        value.Contains(term, StringComparison.OrdinalIgnoreCase));

static class WorldCheckProcess
{
    private const uint SemNoGpFaultErrorBox = 0x0002;
    private const uint SemFailCriticalErrors = 0x0001;

    public static void DisableWindowsCrashDialogs()
    {
        if (!OperatingSystem.IsWindows()) return;
        _ = SetErrorMode(
            SemNoGpFaultErrorBox | SemFailCriticalErrors);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}

sealed class StubHttpHandler(
    Func<HttpRequestMessage, HttpResponseMessage> response)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(response(request));

    public static HttpResponseMessage Json(string json) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                System.Text.Encoding.UTF8,
                "application/json")
        };
}
