using IslandRpg.World;
using IslandRpg.Assets;
using IslandRpg.Gameplay;
using IslandRpg.Persistence;
using IslandRpg.Rendering;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

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
var midnight = WorldTime.At(0);
var nextDay = WorldTime.At(24 * 60 * 60);
Require(morning.Day == 1 && morning.Hour == 8 &&
        midnight.Daylight < morning.Daylight &&
        nextDay.Day == 2 && nextDay.Hour == 0,
    "world time must track day number, clock time, and daylight");
Require(WorldTime.Advance(0, WorldTime.RealSecondsPerGameDay) ==
        24 * 60 * 60,
    "one full game day must take 24 real minutes");

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
var pickaxeRecipe = CraftingSkill.Recipes.Single(
    recipe => recipe.ResultItemId == ItemIds.StonePickaxe);
Require(pickaxeRecipe.Category == CraftingCategory.Tools &&
        pickaxeRecipe.RequiredLevel == 6 &&
        pickaxeRecipe.Ingredients.Count == 3 &&
        pickaxeRecipe.Steps.Count == 3,
    "the stone pickaxe recipe must define its level, materials, and ordered steps");
Require(CraftingSkill.Availability(
            pickaxeRecipe, 5,
            [ItemIds.SharpenedRock, ItemIds.MediumRock, ItemIds.Sticks]) ==
        RecipeAvailability.Locked &&
        CraftingSkill.Availability(
            pickaxeRecipe, 6, []) ==
        RecipeAvailability.MissingResources &&
        CraftingSkill.Availability(
            pickaxeRecipe, 6,
            [ItemIds.SharpenedRock, ItemIds.MediumRock, ItemIds.Sticks]) ==
        RecipeAvailability.Ready,
    "recipe state must distinguish locked, missing-resource, and ready recipes");
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
        axeDefinition.WoodcuttingPower == 2 &&
        ItemCatalog.Get(ItemIds.StoneAxe).WoodcuttingPower == 1 &&
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
var craftingWindowBounds =
    CraftingWindowState.WindowBounds(new(0, 0, 1280, 720));
var craftingButton =
    CraftingWindowState.CraftButtonBounds(craftingWindowBounds);
var craftingClose =
    CraftingWindowState.CloseBounds(craftingWindowBounds);
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
    visibleSettingsTabs.Contains(SettingsTab.Sound),
    "the settings menu must expose Display, Game, and Sound tabs");
if (!System.Diagnostics.Debugger.IsAttached)
    Require(!visibleSettingsTabs.Contains(SettingsTab.Dev),
        "the Dev settings tab must stay hidden without an attached debugger");
var settingsPanel = new Vector4(360, 110, 560, 500);
Require(
    DeveloperSettingsController.GrantBounds(
        settingsPanel, SkillType.Woodcutting).X +
    DeveloperSettingsController.GrantBounds(
        settingsPanel, SkillType.Woodcutting).Z <=
    DeveloperSettingsController.MaxBounds(
        settingsPanel, SkillType.Woodcutting).X,
    "developer XP grant and max-level buttons must not overlap");
var settingsContent = SettingsMenuState.ContentBounds(settingsPanel);
var settingsBack = SettingsMenuState.BackButtonBounds(settingsPanel);
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
    Enum.GetValues<SkillType>().Length == 3 &&
    SkillService.LevelForExperience(
        SkillService.ExperienceForLevel(10)) == 10 &&
    WoodcuttingSkill.ExperienceForLevel(10) ==
    FarmingSkill.ExperienceForLevel(10) &&
    FarmingSkill.ExperienceForLevel(10) ==
    CraftingSkill.ExperienceForLevel(10),
    "all registered skills must reuse the shared level and experience progression service");
Require(PlayerInventory.TryCarvePlank(
        [ItemIds.SharpenedRock, ItemIds.Logs],
        0, 1, .25f, out var carvedPlank, out var sharpRockSurvived) &&
        carvedPlank[0] == ItemIds.SharpenedRock &&
        carvedPlank[1] == ItemIds.Plank &&
        !sharpRockSurvived,
    "using a sharp rock on a log must create a plank and usually keep the tool");
Require(PlayerInventory.TryCarvePlank(
        [ItemIds.SharpenedRock, ItemIds.OakLogs],
        0, 1, .249f, out var carvedOakPlank, out var sharpRockDestroyed) &&
        carvedOakPlank[0] is null &&
        carvedOakPlank[1] == ItemIds.Plank &&
        sharpRockDestroyed,
    "carving a plank must have a twenty-five percent sharp-rock break chance");
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

var boundedChat = new ChatUiControlState();
boundedChat.Layout(new(0, 0, 1280, 720));
for (var index = 0; index < 225; index++)
    boundedChat.AddMessage($"message {index}");
Require(boundedChat.Messages.Count == 200 &&
        boundedChat.Messages[0].Text == "message 25" &&
        boundedChat.IsAtBottom,
    "chat must discard its oldest messages while following the bottom");

const long seed = 8675309;
var origin = InfiniteWorldGenerator.Generate(seed, new(0, 0));
var repeated = InfiniteWorldGenerator.Generate(seed, new(0, 0));
Require(origin.GroundObjects.Count <= 8 &&
        origin.GroundObjects.SequenceEqual(repeated.GroundObjects) &&
        origin.GroundObjects.All(item =>
            item.ItemId is ItemIds.Sticks or ItemIds.LargeRock),
    "natural ground objects must be deterministic, capped, and limited to collectible types");
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
Require(origin.Vegetation.All(item =>
        item.CanBecomeInstance == (item.Kind == WorldVegetationKind.BerryBush)),
    "only harvestable berry vegetation should be flagged to become an instance");
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
        return tile.Biome is not (Biome.DeepWater or Biome.ShallowWater or
                   Biome.RiverWater or Biome.MangroveShallows or
                   Biome.Beach or Biome.DesertSand) &&
               relief.Max() - relief.Min() <= 2 &&
               origin.Trees.All(tree => tree.X != tile.X || tree.Y != tile.Y);
    }),
    "vegetation must avoid water, sand, steep ground, and occupied tree tiles");
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
entity.SetGender(EntityGender.Female);
Require(entity.Gender == EntityGender.Female,
    "entity gender should switch without replacing the entity");
entity.GatherAt(new Vector2(0, 2));
Require(entity.Action == EntityAction.Gather && entity.Facing.Y > 0,
    "gathering should face the collectible and select the gather animation");
var rigFrame = VillagerDirectionRig.Resolve(new Vector2(-1, 0), 75, 5, 4);
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
Require(isometricTile.Width == 256 && isometricTile.Height == 256,
    "isometric map sections must render at high-resolution 256x256");
Require(isometricTile.Rgba.SequenceEqual(repeatedIsometricTile.Rgba),
    "isometric map section generation must be deterministic");

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
    for (var regionY = 0; regionY < WorldChunkStore.RegionSize; regionY++)
    for (var regionX = 0; regionX < WorldChunkStore.RegionSize; regionX++)
        store.Save(CloneAt(origin, new(regionX, regionY)));
    var negative = CloneAt(origin, new(-1, -1));
    store.Save(negative);

    var loaded = store.LoadOrGenerate(origin.Coordinate);
    Require(origin.Tiles.SequenceEqual(loaded.Tiles), "saved tiles must round-trip");
    Require(origin.Trees.SequenceEqual(loaded.Trees), "saved trees must round-trip");
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

    var saves = new GameSaveRepository(Path.Combine(root, "profiles"));
    var player = saves.CreatePlayer(
        "Test Hero", EntityGender.Female, 3, 5);
    player = player with
    {
        WoodcuttingExperience = 725,
        Inventory = PlayerInventory.Normalize(["logs", "oak_logs"])
    };
    saves.SavePlayer(player);
    var world = saves.CreateWorld("Test Realm", 4321, player.Id);
    saves.SaveWorldPlayer(
        world.Id, new(player.Id, 12.5f, -8.25f, DateTime.UtcNow));
    Require(saves.ListPlayers().Single() is var loadedPlayer &&
            loadedPlayer.Id == player.Id &&
            loadedPlayer.WoodcuttingExperience == 725 &&
            loadedPlayer.Inventory?.Length == PlayerInventory.Capacity &&
            loadedPlayer.Inventory[0] == "logs" &&
            loadedPlayer.Inventory[1] == "oak_logs" &&
            PlayerInventory.Count(loadedPlayer.Inventory) == 2,
        "character skills and inventory must persist independently");
    Require(saves.ListWorlds().Single().Id == world.Id,
        "named world profiles must round-trip");
    var worldPlayer = saves.LoadWorldPlayer(world.Id, player.Id);
    Require(worldPlayer is not null &&
            worldPlayer.PositionX == 12.5f &&
            worldPlayer.PositionY == -8.25f,
        "character position must be stored per world");
    saves.DeletePlayer(player.Id);
    Require(saves.ListPlayers().Count == 0 &&
            saves.LoadWorldPlayer(world.Id, player.Id) is null,
        "deleting a character must remove its world-specific states");
    saves.DeleteWorld(world.Id);
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

Console.WriteLine(
    $"World checks passed: {macroBiomes.Count} macro biomes, deterministic generation, seams, " +
    $"persistence, and 64-slot region storage ({regionBytes:N0} bytes for the test region).");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
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
    TreeInstances = source.TreeInstances.ToList(),
    GroundObjects = source.GroundObjects.ToList(),
    Vegetation = source.Vegetation
};
