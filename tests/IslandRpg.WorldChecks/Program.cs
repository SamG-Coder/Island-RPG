using IslandRpg.World;
using IslandRpg.Assets;
using IslandRpg.Gameplay;
using IslandRpg.Persistence;
using IslandRpg.Rendering;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

WorldCheckProcess.DisableWindowsCrashDialogs();

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
Require(
    meleeHit is { Hit: true, Damage: 1, Experience: 4 } &&
    !meleeMiss.Hit &&
    MeleeCombatService.AttackIntervalSeconds == 2.4f,
    "unarmed melee must resolve deterministic hits on fixed combat ticks");
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
        defaultDisplaySettings.FrameRateLimit == 0,
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
var midnight = WorldTime.At(0);
var nextDay = WorldTime.At(24 * 60 * 60);
Require(morning.Day == 1 && morning.Hour == 8 &&
        midnight.Daylight < morning.Daylight &&
        nextDay.Day == 2 && nextDay.Hour == 0,
    "world time must track day number, clock time, and daylight");
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
            [ItemIds.StoneKnife, ItemIds.Logs],
            out var craftedPlank) &&
        craftedPlank.Contains(ItemIds.StoneKnife) &&
        craftedPlank.Contains(ItemIds.Plank),
    "the plank recipe must require and preserve any knife tool");
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
var workbenchRecipe = CraftingSkill.Recipes.Single(
    recipe => recipe.ResultItemId == ItemIds.Workbench);
Require(workbenchRecipe.Category == CraftingCategory.Furniture &&
        workbenchRecipe.RequiredLevel == 3 &&
        workbenchRecipe.Experience == 75 &&
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
            fueledCampfire, [ItemIds.SmallRocks], 100),
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
    workbenchRecipes.Count == 4 &&
    workbenchRecipes.All(recipe =>
        recipe.RequiredStationItemId == ItemIds.Workbench) &&
    bloomeryRecipes.Count == 2 &&
    bloomeryRecipes.All(recipe =>
        recipe.RequiredStationItemId == ItemIds.Bloomery) &&
    anvilRecipes.Count == 7 &&
    anvilRecipes.All(recipe =>
        recipe.RequiredStationItemId == ItemIds.SmithingAnvil),
    "station recipe views must be cached and contain only recipes for the station used");
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
    stationCraftingWindow.SelectedRecipe ==
        bloomeryRecipes.FirstOrDefault(),
    "opening a crafting station must select from only that station's recipes");
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
    stackingContainer.TryAdd(ItemIds.Logs, 99) &&
    stackingContainer.TryAdd(ItemIds.Logs) &&
    stackingContainer.Quantities[0] == 100,
    "stacking containers must merge equal item IDs and retain their quantity");
var restoredStackingContainer = new ItemContainerState(
    stackingContainer.Definition,
    stackingContainer.Save());
Require(
    restoredStackingContainer.Definition.Id ==
        stackingContainer.Definition.Id &&
    restoredStackingContainer.Items[0] == ItemIds.Logs &&
    restoredStackingContainer.Quantities[0] == 100,
    "container snapshots must reload quantities against their stable container ID");
var chestObject = new WorldGroundObject(
    Guid.NewGuid(), ItemIds.StorageChest, 8.5f, 9.5f);
var chestContainer = StorageContainerService.Open(chestObject);
Require(
    chestContainer.Definition.Id == chestObject.Id &&
    chestContainer.Definition.Title == "Wooden Chest" &&
    chestContainer.Definition.Capacity == 48 &&
    chestContainer.TryAdd(ItemIds.OakLogs, 25),
    "a placed wooden chest must create a 48-slot stacking container");
var storedChest = StorageContainerService.Save(
    chestObject, chestContainer);
var reopenedChest = StorageContainerService.Open(storedChest);
Require(
    reopenedChest.Items[0] == ItemIds.OakLogs &&
    reopenedChest.Quantities[0] == 25 &&
    StorageContainerService.Definition(
        Guid.NewGuid(), ItemIds.StorageBarrel).Capacity == 40,
    "world storage snapshots must reopen by object ID while barrels retain their smaller layout");
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
    ItemIds.Logs, ItemIds.Coal, ItemIds.Logs,
    ItemIds.Logs, ItemIds.Sticks
};
var quantityContainer = new ItemContainerState(
    new(
        Guid.NewGuid(), "Quantity bank", 2, 1,
        AllowStacking: true));
Require(
    quantityContainer.TransferMatchingFrom(
        matchingInventory, ItemIds.Logs, 2) == 2 &&
    quantityContainer.Quantities[0] == 2 &&
    matchingInventory.Count(item => item == ItemIds.Logs) == 1 &&
    quantityContainer.TryTake(
        0, 2, out var withdrawnItemId) &&
    withdrawnItemId == ItemIds.Logs &&
    quantityContainer.Items[0] is null,
    "amount menus must deposit matching bag items and withdraw the requested stack quantity");
var allItemsContainer = ItemContainerState.CreateAllItemsTest();
Require(
    ItemCatalog.All.All(item =>
    {
        var slot = Array.IndexOf(allItemsContainer.Items, item.Id);
        return slot >= 0 && allItemsContainer.Quantities[slot] == 100;
    }) &&
    Enumerable.Range(
            0,
            allItemsContainer.Definition.Capacity -
            allItemsContainer.Definition.ColumnCount + 1)
        .Any(start => Enumerable.Range(
                start, allItemsContainer.Definition.ColumnCount)
            .All(allItemsContainer.IsSpacer)),
    "the developer item bank must contain every catalog item at quantity 100 with category spacing");
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
settingsMenu.EnableDeveloperMode();
Require(settingsMenu.DeveloperModeEnabled &&
        settingsMenu.VisibleTabs.Contains(SettingsTab.Dev),
    "the hidden chat command must be able to enable the Dev settings tab");
var settingsPanel = new Vector4(360, 80, 560, 560);
settingsMenu.SelectAt(
    settingsPanel,
    SettingsMenuState.TabBounds(settingsPanel, 3, 4).Xy);
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
    "fish must use suitable water and maintain two tiles of sand clearance");
var animationFish = new WorldFish(
    0, 0, WorldFishSpecies.ShoreMinnows,
    "FISHS_NN", 3, "fish:test");
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
entity.PrepareForPathRequest();
Require(entity.Action == EntityAction.Move &&
        entity.ActionTime == walkingAnimationTime,
    "requesting a replacement path while walking must preserve the active walk cycle");
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
        undergroundCoordinate.Y * WorldChunk.Size + 14.5f);
    var persistentChestState =
        StorageContainerService.Open(persistentChest);
    Require(
        persistentChestState.TryAdd(ItemIds.Coal, 100) &&
        persistentChestState.TryAdd(ItemIds.BronzeBar, 4),
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
        reloadedChestState.Quantities[
            Array.IndexOf(
                reloadedChestState.Items, ItemIds.Coal)] == 100 &&
        reloadedChestState.Quantities[
            Array.IndexOf(
                reloadedChestState.Items, ItemIds.BronzeBar)] == 4,
        "container item IDs and stack quantities must survive a chunk reload");
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
        Inventory = PlayerInventory.Normalize(["logs", "oak_logs"])
    };
    saves.SavePlayer(player);
    var world = saves.CreateWorld("Test Realm", 4321, player.Id);
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
