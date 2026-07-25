using IslandRpg.World;
using IslandRpg.Gameplay;
using IslandRpg.Persistence;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

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

var startingInventory = PlayerInventory.CreateStartingInventory();
Require(startingInventory.Length == PlayerInventory.Capacity &&
        startingInventory[0] == ItemIds.Axe &&
        PlayerInventory.Count(startingInventory) == 1 &&
        PlayerInventory.HasAxe(startingInventory),
    "a new character must start with an axe in a fixed 28-slot inventory");
Require(PlayerInventory.CanDrop(ItemIds.Axe) &&
        PlayerInventory.CanDrop(ItemIds.Logs),
    "all inventory items must be droppable into the world");
Require(ItemCatalog.Get(ItemIds.Axe) is var axeDefinition &&
        axeDefinition.SpriteCell == 5 &&
        axeDefinition.HasTag(ItemTag.Axe) &&
        axeDefinition.Droppable &&
        ItemCatalog.Get(ItemIds.OakLogs).HasTag(ItemTag.Log) &&
        ItemCatalog.All.Select(item => item.Id).Distinct().Count() ==
        ItemCatalog.All.Count,
    "the item catalogue must own axe/log gameplay and presentation metadata");
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
Require(!PlayerInventory.TryBreakRock(
        Enumerable.Repeat<string?>(ItemIds.LargeRock, PlayerInventory.Capacity)
            .ToArray(),
        0, 1, out _),
    "rock splitting must require an empty inventory slot");

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
    GroundObjects = source.GroundObjects.ToList()
};
