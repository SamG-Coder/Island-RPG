using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private bool _buildingPanelOpen;
    private bool _buildingPanelLeftWasDown;
    private int _buildingBrowserScrollRow;
    private Guid? _activePlayerConstructionId;
    private int _lastPlayerConstructionStrike;
    private readonly Queue<Guid> _playerConstructionQueue = [];
    private CraftingRecipe? _activeBuildingRecipe;
    private int _buildingPlacementRotation;
    private bool _buildingPlacementAwaitingRelease;
    private Vector2? _wallPlacementAnchor;
    private WallDragOrientation? _wallDragOrientation;
    private readonly List<WallPlacementPreviewNode> _wallPlacementPreview = [];

    private sealed record WallPlacementPreviewNode(
        Vector2 Target, bool Valid, int Frame);

    private enum BuildingBrowserCategory
    {
        Defences,
        Housing
    }

    private sealed record BuildingBrowserEntry(
        BuildingBrowserCategory Category,
        string Name,
        string Description,
        CraftingRecipe Recipe,
        string ItemId);

    private static IReadOnlyList<BuildingBrowserEntry> AllBuildingBrowserEntries =>
    new BuildingBrowserEntry[]
    {
        new(
            BuildingBrowserCategory.Defences,
            "Wooden fence",
            "Light boundary · 3 sticks",
            BuildingRecipe(ItemIds.WoodenFence),
            ItemIds.WoodenFence),
        new(
            BuildingBrowserCategory.Defences,
            "Wooden wall",
            "Palisade defence · 1 log",
            BuildingRecipe(ItemIds.WoodenWall),
            ItemIds.WoodenWall),
        new(
            BuildingBrowserCategory.Defences,
            "Fortified wooden wall",
            "Reinforced timber · 2 logs",
            BuildingRecipe(ItemIds.FortifiedWoodenWall),
            ItemIds.FortifiedWoodenWall),
        new(
            BuildingBrowserCategory.Defences,
            "Stone wall",
            "Strong defence · 3 large rocks",
            BuildingRecipe(ItemIds.StoneWall),
            ItemIds.StoneWall),
        new(
            BuildingBrowserCategory.Defences,
            "Fortified wall",
            "Heavy defence · 5 large rocks",
            BuildingRecipe(ItemIds.FortifiedWall),
            ItemIds.FortifiedWall)
    }.Concat(WallCatalog.All.Where(wall => wall.ItemId.StartsWith(
            "wall_variant_", StringComparison.Ordinal)).Select(wall =>
        new BuildingBrowserEntry(
            BuildingBrowserCategory.Defences,
            wall.Name,
            $"{wall.Architecture} {wall.Family}",
            BuildingRecipe(wall.ItemId),
            wall.ItemId)))
    .Concat(GateCatalog.All.Select(gate => new BuildingBrowserEntry(
        BuildingBrowserCategory.Defences,
        gate.Name,
        $"Tier 1 wooden gate - {gate.LogCost} logs",
        BuildingRecipe(gate.ItemId),
        gate.ItemId)))
    .Concat(DefenceBuildingCatalog.All.Select(defence =>
        new BuildingBrowserEntry(
            BuildingBrowserCategory.Defences,
            defence.Kind == DefenceBuildingKind.Outpost
                ? "Scout tower"
                : defence.Name,
            $"{defence.Kind} - {defence.LogCost} logs" +
            (defence.RockCost > 0
                ? $" - {defence.RockCost} large rocks"
                : ""),
            BuildingRecipe(defence.ItemId),
            defence.ItemId)))
    .Concat(HouseCatalog.All.Select(house => new BuildingBrowserEntry(
        BuildingBrowserCategory.Housing,
        HouseTileName(house),
        $"{house.Name} - {house.LogCost} logs" +
        (house.RockCost > 0 ? $" - {house.RockCost} large rocks" : ""),
        BuildingRecipe(house.ItemId),
        house.ItemId))).ToArray();

    // Keep later buildings registered for saves and NPC projects, but hide
    // them from the player-facing browser until their individual placement
    // and DAT geometry passes are complete.
    private static IReadOnlyList<BuildingBrowserEntry> BuildingBrowserEntries =>
        AllBuildingBrowserEntries
            .Where(value =>
                GateCatalog.IsGate(value.ItemId) ||
                HouseCatalog.IsHouse(value.ItemId) &&
                HouseCatalog.Get(value.ItemId).Tier == 1 ||
                value.ItemId is ItemIds.WoodenFence or
                    ItemIds.WoodenWall or ItemIds.FortifiedWoodenWall ||
                DefenceBuildingCatalog.IsDefence(value.ItemId) &&
                DefenceBuildingCatalog.Get(value.ItemId).Kind ==
                    DefenceBuildingKind.Outpost)
            .ToArray();

    private static CraftingRecipe BuildingRecipe(string itemId) =>
        CraftingSkill.Recipes.First(value => value.ResultItemId == itemId);

    private static string HouseTileName(HouseDefinition house)
    {
        var architecture = house.Architecture
            .Replace("Advanced ", "Adv. ", StringComparison.Ordinal)
            .Replace("Central European", "Central", StringComparison.Ordinal)
            .Replace("Western European", "Western", StringComparison.Ordinal)
            .Replace("Middle Eastern", "Middle East", StringComparison.Ordinal)
            .Replace("Early shelter", "Shelter", StringComparison.Ordinal);
        return $"{architecture} {house.Frame + 1}";
    }

    private static CraftingRecipe WoodenWallRecipe =>
        BuildingRecipe(ItemIds.WoodenWall);

    private void ToggleBuildingPanel()
    {
        _buildingPanelOpen = !_buildingPanelOpen;
        if (_buildingPanelOpen)
        {
            _buildingBrowserScrollRow = 0;
        }
        CancelPlaceableObjectPlacement();
    }

    private Vector4 BuildingPanelBounds()
    {
        var inventory = _gameUi.Panel.Bounds;
        const float gap = 6;
        var right = inventory.X - gap;
        var width = MathF.Min(560, right - SceneClientBounds().X - 8);
        return new(right - width, inventory.Y, width, inventory.W);
    }

    private const int BuildingGridColumns = 5;
    private const int BuildingGridRows = 3;
    private const float BuildingGridGap = 8;
    private const float BuildingTileHeight = 72;

    private static Vector4 BuildingGridBounds(Vector4 panel) =>
        new(panel.X + 12, panel.Y + 45, panel.Z - 24, panel.W - 57);

    private static Vector4 BuildingGridTileBounds(Vector4 panel, int index)
    {
        var grid = BuildingGridBounds(panel);
        var width = (grid.Z - (BuildingGridColumns - 1) * BuildingGridGap) /
                    BuildingGridColumns;
        return new(
            grid.X + index % BuildingGridColumns * (width + BuildingGridGap),
            grid.Y + index / BuildingGridColumns *
            (BuildingTileHeight + BuildingGridGap),
            width,
            BuildingTileHeight);
    }

    private void UpdateBuildingPanelInput(Vector2 pointer, bool leftDown)
    {
        if (!_buildingPanelOpen)
        {
            _buildingPanelLeftWasDown = leftDown;
            return;
        }
        if (leftDown && !_buildingPanelLeftWasDown)
        {
            var panel = BuildingPanelBounds();
            var entries = VisibleBuildingEntries();
            for (var index = 0; index < entries.Count; index++)
            {
                if (!BuildingGridTileBounds(panel, index).Contains(pointer))
                    continue;
                BeginPlayerBuildingPlacement(entries[index].Recipe);
                break;
            }
        }
        _buildingPanelLeftWasDown = leftDown;
    }

    private IReadOnlyList<BuildingBrowserEntry> VisibleBuildingEntries()
    {
        var first = _buildingBrowserScrollRow * BuildingGridColumns;
        var capacity = BuildingGridColumns * BuildingGridRows;
        return BuildingBrowserEntries.Skip(first).Take(capacity).ToArray();
    }

    private bool ScrollBuildingBrowser(Vector2 pointer, float offset)
    {
        if (!_buildingPanelOpen ||
            !BuildingPanelBounds().Contains(pointer))
            return false;
        var count = BuildingBrowserEntries.Count;
        var visibleSlots = BuildingGridColumns * BuildingGridRows;
        var maximumScrollRow = Math.Max(0,
            (count - visibleSlots + BuildingGridColumns - 1) /
            BuildingGridColumns);
        _buildingBrowserScrollRow = Math.Clamp(
            _buildingBrowserScrollRow - Math.Sign(offset),
            0, maximumScrollRow);
        return true;
    }

    private void BeginPlayerBuildingPlacement(CraftingRecipe recipe)
    {
        if (_activePlayer is null) return;
        var level = CraftingSkill.LevelForExperience(
            _activePlayer.CraftingExperience);
        if (level < recipe.RequiredLevel)
        {
            ReportBlockedAction(
                $"building-level-{recipe.Id}",
                $"You need Crafting level {recipe.RequiredLevel} to build this.");
            return;
        }
        _buildingPanelOpen = false;
        _activeBuildingRecipe = recipe;
        _buildingPlacementRotation = 0;
        _placeableObjectPlacement.BeginConstruction(recipe.ResultItemId);
        _buildingPlacementAwaitingRelease = true;
        _wallPlacementAnchor = null;
        _wallDragOrientation = null;
        _wallPlacementPreview.Clear();
        _groundDropPreview = null;
        _chatUi.AddMessage(
            $"Click once to start the {ItemCatalog.Get(recipe.ResultItemId).Name}, " +
            "move to preview its route, then click again to place it. " +
            "Resources are used only when you place the foundation. " +
            (PlaceableObjectCatalog.RotationCount(recipe.ResultItemId) > 1
                ? "Use Left/Right Arrow to rotate. "
                : string.Empty) +
            "Right-click to cancel.",
            ChatMessageStyle.Action);
    }

    private bool UnlimitedBuildModeEnabled =>
        _settingsMenu.DeveloperModeEnabled &&
        _saves.LoadSettings().UnlimitedBuildMode;

    private CraftingService.CraftResult TryConsumeBuildingMaterials(
        CraftingRecipe recipe,
        out InventoryContainer inventory,
        int placements = 1)
    {
        if (_activePlayer is not { } player)
        {
            inventory = new(PlayerInventory.Capacity);
            return CraftingService.CraftResult.MissingResources;
        }
        if (UnlimitedBuildModeEnabled)
        {
            inventory = ActivePlayerInventory();
            return CraftingService.CraftResult.Success;
        }
        return CraftingService.TryConsumeForPlacement(
            recipe,
            CraftingSkill.LevelForExperience(
                player.CraftingExperience),
            ActivePlayerInventory(), out inventory,
            HasRequiredCraftingStation(recipe), placements);
    }

    private bool CanAffordBuilding(CraftingRecipe recipe) =>
        _activePlayer is not null &&
        TryConsumeBuildingMaterials(recipe, out _) ==
            CraftingService.CraftResult.Success;

    private int AffordableBuildingCount(CraftingRecipe recipe)
    {
        if (_activePlayer is null) return 0;
        if (UnlimitedBuildModeEnabled)
            return WallPlacementPlanner.MaximumSegments;
        var inventory = ActivePlayerInventory();
        var count = 0;
        while (count < WallPlacementPlanner.MaximumSegments &&
               CraftingService.TryConsumeForPlacement(
                   recipe,
                   CraftingSkill.LevelForExperience(
                       _activePlayer.CraftingExperience),
                   inventory, out var updated,
                   HasRequiredCraftingStation(recipe)) ==
               CraftingService.CraftResult.Success)
        {
            inventory = updated;
            count++;
        }
        return count;
    }

    private void UpdateWallPlacementPreview(Vector2 target)
    {
        _wallPlacementPreview.Clear();
        if (_wallPlacementAnchor is not { } anchor ||
            _activeBuildingRecipe is not { } recipe)
            return;
        var affordable = AffordableBuildingCount(recipe);
        var drag = WallPlacementPlanner.Generate(
            anchor, target, _wallDragOrientation);
        _wallDragOrientation = drag.Orientation;
        var line = drag.Tiles;
        for (var index = 0; index < line.Count; index++)
        {
            var snapped = PlaceableObjectCatalog.SnapBuildingToTile(
                line[index]);
            var clear = CanPlacePlaceableObjectAt(
                recipe.ResultItemId, snapped, out _, out _);
            var valid = clear && index < affordable;
            _wallPlacementPreview.Add(new(
                snapped, valid,
                WallPlacementPlanner.FrameAt(line, index)));
        }
    }

    private bool UpdateWallPlacementInput(bool leftDown, bool rightDown)
    {
        if (_activeBuildingRecipe is not { } recipe ||
            !WallCatalog.IsWall(recipe.ResultItemId))
            return false;
        if (rightDown && !_gameRightWasDown)
        {
            CancelPlaceableObjectPlacement();
            return true;
        }
        if (IsPointerOverGameUi(MouseState.Position) ||
            _minimapUi.HitTest(MouseState.Position))
        {
            _wallPlacementPreview.Clear();
            return true;
        }

        var target = PlaceableObjectCatalog.SnapBuildingToTile(
            ScreenToTerrain(SceneMousePosition()));
        if (_wallPlacementAnchor is null)
        {
            var clear = CanPlacePlaceableObjectAt(
                recipe.ResultItemId, target, out _, out _);
            _wallPlacementPreview.Clear();
            _wallPlacementPreview.Add(new(
                target,
                clear && CanAffordBuilding(recipe),
                PalisadeWallVisuals.FrontFrame));
        }
        if (leftDown && !_gameLeftWasDown)
        {
            if (_wallPlacementAnchor is null)
            {
                if (!_wallPlacementPreview[0].Valid)
                {
                    ReportBlockedAction(
                        "building-wall-start-invalid",
                        CanAffordBuilding(recipe)
                            ? "The wall must start on a clear square."
                            : $"You need {DescribeBuildingMaterials(recipe)} to start this wall.");
                    return true;
                }
                _wallPlacementAnchor = target;
                UpdateWallPlacementPreview(target);
                return true;
            }
            if (ConfirmWallPlacement(recipe))
                CancelPlaceableObjectPlacement();
            return true;
        }
        if (_wallPlacementAnchor is not null)
            UpdateWallPlacementPreview(target);
        return true;
    }

    private bool ConfirmWallPlacement(CraftingRecipe recipe)
    {
        if (_activePlayer is null) return false;
        if (_wallPlacementPreview.Count == 0 ||
            !_wallPlacementPreview[0].Valid ||
            !_wallPlacementPreview[^1].Valid)
        {
            ReportBlockedAction(
                $"building-placement-endpoint-{recipe.Id}",
                "Both ends of the wall must be green before it can be placed.");
            return false;
        }
        var valid = _wallPlacementPreview
            .Where(value => value.Valid)
            .DistinctBy(value => value.Target)
            .ToArray();
        if (valid.Length == 0)
        {
            ReportBlockedAction(
                $"building-placement-{recipe.Id}",
                "No green wall segments can be placed on that line.");
            return false;
        }
        if (IsNetworkWorld)
        {
            StartNetworkConstructionPlacements(valid.Select(node =>
                new PendingNetworkWorldAction(
                    NetworkWorldActionKind.PlaceConstruction,
                    node.Target,
                    InventorySlot: 0,
                    DefinitionId: recipe.ResultItemId,
                    Rotation: node.Frame)));
            return true;
        }
        var resolved = new List<(
            GpuWorldChunk Chunk, Vector2 Target, int Frame)>();
        foreach (var node in valid)
        {
            if (!CanPlacePlaceableObjectAt(
                    recipe.ResultItemId, node.Target,
                    out var gpu, out _))
                return false;
            resolved.Add((gpu, node.Target, node.Frame));
        }

        var result = TryConsumeBuildingMaterials(
            recipe, out var inventory, valid.Length);
        if (result != CraftingService.CraftResult.Success) return false;

        var placed = new List<(GpuWorldChunk Chunk, WorldGroundObject Object)>(
            resolved.Count);
        foreach (var (gpu, target, frame) in resolved)
        {
            var wall = ConstructionService.Begin(new(
                Guid.NewGuid(), recipe.ResultItemId,
                target.X, target.Y,
                OwnerId: _activePlayer.Id,
                VisualFrame: frame));
            gpu.Chunk.GroundObjects.Add(wall);
            placed.Add((gpu, wall));
        }
        SaveActivePlayerInventory(inventory);
        foreach (var chunk in placed.Select(value => value.Chunk).Distinct())
            QueueChunkSave(chunk.Chunk);
        foreach (var _ in placed)
            RecordQuestEvent(new(
                QuestEventType.BuildObject, recipe.ResultItemId));
        _chatUi.AddMessage(
            $"You mark out {placed.Count} " +
            $"{ItemCatalog.Get(recipe.ResultItemId).Name} " +
            $"{(placed.Count == 1 ? "foundation" : "foundations")}.",
            ChatMessageStyle.Action);
        QueuePlayerConstructionSequence(
            placed.Select(value => value.Object));
        return true;
    }

    private void RenderWallPlacementPreview()
    {
        if (_wallPlacementPreview.Count == 0) return;
        var green = new AtlasDrawBatch();
        var red = new AtlasDrawBatch();
        foreach (var node in _wallPlacementPreview)
        {
            var world = GroundObjectWorld(new(
                Guid.Empty, _activeBuildingRecipe!.ResultItemId,
                node.Target.X, node.Target.Y));
            AddAtlasQuad(
                PalisadeWallVisuals.WallFrame(
                    _activeBuildingRecipe.ResultItemId, node.Frame),
                world, .58f,
                node.Valid ? green : red);
        }
        DrawTinted(green, new(.28f, 1f, .34f));
        DrawTinted(red, new(1f, .48f, .42f));
        GL.Uniform1(GL.GetUniformLocation(_program, "tintAmount"), 0f);
        GL.Uniform1(GL.GetUniformLocation(_program, "grayscaleAmount"), 0f);
        GL.Uniform1(GL.GetUniformLocation(_program, "preserveDarkTint"), 0);

        void DrawTinted(AtlasDrawBatch vertices, Vector3 tint)
        {
            if (vertices.Count == 0) return;
            GL.UseProgram(_program);
            GL.Uniform3(
                GL.GetUniformLocation(_program, "tint"),
                tint);
            GL.Uniform1(
                GL.GetUniformLocation(_program, "tintAmount"), .58f);
            GL.Uniform1(
                GL.GetUniformLocation(_program, "grayscaleAmount"), 1f);
            GL.Uniform1(
                GL.GetUniformLocation(_program, "preserveDarkTint"), 1);
            DrawTreeBatch(vertices);
        }
    }

    private void DemolishPlayerConstruction(WorldGroundObject site)
    {
        if (IsNetworkWorld)
        {
            QueueNetworkObjectAction(
                NetworkWorldActionKind.Demolish, site);
            return;
        }
        if (_activePlayer is null ||
            !ConstructionService.IsConstructionSite(site) ||
            !string.Equals(site.OwnerId, _activePlayer.Id,
                StringComparison.Ordinal))
            return;
        var refund = ConstructionService.DemolitionRefund(site);
        if (refund is null) return;
        var inventory = ActivePlayerInventory();
        if (!inventory.TryAdd(refund))
        {
            ReportBlockedAction(
                "demolish-inventory-full",
                $"You need inventory space to recover the " +
                $"{ItemCatalog.Get(refund).Name}.");
            return;
        }
        var location = FindGroundObjectLocation(site.Id);
        if (location is null) return;
        location.Value.Chunk.GroundObjects.RemoveAt(location.Value.Index);
        SaveActivePlayerInventory(inventory);
        QueueChunkSave(location.Value.Chunk);
        if (_activePlayerConstructionId == site.Id)
            _activePlayerConstructionId = null;
        _chatUi.AddMessage(
            $"You demolish the unfinished " +
            $"{ItemCatalog.Get(site.ItemId).Name} and recover one " +
            $"{ItemCatalog.Get(refund).Name}.",
            ChatMessageStyle.Action);
    }

    private bool PlacePlayerBuildingFoundation(
        GroundDropPreview preview, CraftingRecipe recipe)
    {
        if (IsNetworkWorld)
        {
            if (!preview.Valid) return false;
            StartNetworkConstructionPlacements([
                new PendingNetworkWorldAction(
                    NetworkWorldActionKind.PlaceConstruction,
                    preview.Target,
                    InventorySlot: preview.InventorySlot,
                    DefinitionId: recipe.ResultItemId,
                    Rotation: preview.Rotation)
            ]);
            return true;
        }
        if (_activePlayer is null) return false;
        if (!CanPlacePlaceableObjectAt(
                preview.ItemId, preview.Target, out var gpu, out var reason,
                preview.Rotation))
        {
            ReportBlockedAction("building-location-blocked", reason);
            return false;
        }
        var result = TryConsumeBuildingMaterials(
            recipe, out var inventory);
        if (result != CraftingService.CraftResult.Success)
        {
            ReportBlockedAction(
                $"building-resources-{recipe.Id}",
                $"You need {DescribeBuildingMaterials(recipe)} to place this foundation.");
            return false;
        }

        var placed = ConstructionService.Begin(new(
            Guid.NewGuid(), preview.ItemId,
            preview.Target.X, preview.Target.Y,
            VisualFrame: preview.Rotation,
            OwnerId: _activePlayer.Id,
            ResidentIds: HouseCatalog.IsHouse(preview.ItemId)
                ? [_activePlayer.Id]
                : null));
        gpu.Chunk.GroundObjects.Add(placed);
        SaveActivePlayerInventory(inventory);
        QueueChunkSave(gpu.Chunk);
        RecordQuestEvent(new(QuestEventType.BuildObject, preview.ItemId));
        _chatUi.AddMessage(
            $"You place the {ItemCatalog.Get(preview.ItemId).Name} foundation.",
            ChatMessageStyle.Action);

        QueuePlayerConstructionWork(placed);
        return true;
    }

    private static string DescribeBuildingMaterials(CraftingRecipe recipe) =>
        string.Join(" and ", recipe.Ingredients.Select(ingredient =>
            $"{ingredient.Count} {ItemCatalog.Get(ingredient.ItemId).Name.ToLowerInvariant()}"));

    private void BeginPlayerConstructionWork(Guid siteId)
    {
        var location = FindGroundObjectLocation(siteId);
        if (location is null ||
            !ConstructionService.IsConstructionSite(location.Value.Object))
            return;
        _activePlayerConstructionId = siteId;
        _lastPlayerConstructionStrike = 0;
        _player?.BuildAt(new(
            location.Value.Object.X, location.Value.Object.Y));
    }

    private void QueuePlayerConstructionWork(
        WorldGroundObject site, bool preserveSequence = false)
    {
        if (IsNetworkWorld)
        {
            QueueNetworkObjectAction(
                NetworkWorldActionKind.BuildConstruction, site);
            return;
        }
        if (_player is null ||
            !ConstructionService.IsConstructionSite(site))
            return;
        if (!preserveSequence) _playerConstructionQueue.Clear();
        var sitePosition = new Vector2(site.X, site.Y);
        var target = PlaceableObjectCatalog.ClosestInteractionPoint(
            site.ItemId, sitePosition, _player.Position,
            rotation: site.VisualFrame);
        const float interactionRange = .24f;
        if ((_player.Position - target).Length <= interactionRange)
            BeginPlayerConstructionWork(site.Id);
        else
            _worldActions.QueuePath(
                sitePosition, interactionRange,
                WorldActionType.BuildConstruction,
                groundObjectId: site.Id,
                itemId: site.ItemId);
    }

    private void QueuePlayerConstructionSequence(
        IEnumerable<WorldGroundObject> sites)
    {
        _playerConstructionQueue.Clear();
        foreach (var site in sites)
            if (ConstructionService.IsConstructionSite(site))
                _playerConstructionQueue.Enqueue(site.Id);
        AdvancePlayerConstructionSequence();
    }

    private void QueueAllPlayerConstructionWork(WorldGroundObject selected)
    {
        if (_player is null) return;
        var remaining = _worldChunks.Values
            .Where(IsActiveWorldChunk)
            .SelectMany(value => value.Chunk.GroundObjects)
            .Where(value =>
                ConstructionService.IsConstructionSite(value) &&
                string.Equals(
                    value.ItemId, selected.ItemId,
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(value =>
                (_player.Position - new Vector2(value.X, value.Y))
                .LengthSquared)
            .ToArray();
        QueuePlayerConstructionSequence(remaining);
    }

    private void AdvancePlayerConstructionSequence()
    {
        while (_playerConstructionQueue.TryDequeue(out var siteId))
        {
            var location = FindGroundObjectLocation(siteId);
            if (location is null ||
                !ConstructionService.IsConstructionSite(
                    location.Value.Object))
                continue;
            QueuePlayerConstructionWork(
                location.Value.Object, preserveSequence: true);
            return;
        }
    }

    private void RenderBuildingPanel()
    {
        if (!_buildingPanelOpen) return;
        var panel = BuildingPanelBounds();
        DrawAoEPanelBorder(panel);
        DrawPanelCaption("Feudal construction", panel);

        var entries = VisibleBuildingEntries();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            DrawBuildingBrowserTile(
                BuildingGridTileBounds(panel, index),
                entry.Name, entry.Description, entry.ItemId);
        }
        DrawBuildingBrowserScrollbar(panel);
    }

    private void DrawBuildingBrowserScrollbar(Vector4 panel)
    {
        var count = BuildingBrowserEntries.Count;
        var visibleSlots = BuildingGridColumns * BuildingGridRows;
        var maximumScrollRow = Math.Max(0,
            (count - visibleSlots + BuildingGridColumns - 1) /
            BuildingGridColumns);
        if (maximumScrollRow == 0) return;
        var grid = BuildingGridBounds(panel);
        var track = new Vector4(grid.X + grid.Z + 3, grid.Y, 4, grid.W);
        DrawUiColor(track, new(.05f, .043f, .03f, .9f));
        var thumbHeight = MathF.Max(18, track.W / (maximumScrollRow + 1));
        var progress = _buildingBrowserScrollRow / (float)maximumScrollRow;
        var thumb = new Vector4(
            track.X, track.Y + (track.W - thumbHeight) * progress,
            track.Z, thumbHeight);
        DrawUiColor(thumb, new(.62f, .48f, .19f, 1));
    }

    private static string HouseArchitectureTileName(string architecture) =>
        architecture
            .Replace("Advanced ", "Adv. ", StringComparison.Ordinal)
            .Replace("Central European", "Central", StringComparison.Ordinal)
            .Replace("Western European", "Western", StringComparison.Ordinal)
            .Replace("Middle Eastern", "Middle East", StringComparison.Ordinal)
            .Replace("Early shelter", "Shelter", StringComparison.Ordinal);

    private void DrawBuildingBrowserTile(
        Vector4 bounds, string name, string description, string? itemId)
    {
        var hovered = bounds.Contains(MouseState.Position);
        DrawUiColor(bounds, hovered
            ? new(.055f, .047f, .030f, .76f)
            : new(.012f, .011f, .009f, .58f));
        DrawPanelOutline(bounds, hovered ? 2 : 1, hovered
            ? new(.78f, .59f, .22f, 1)
            : new(.49f, .38f, .17f, 1));
        var icon = new Vector4(bounds.X + 5, bounds.Y + 4, bounds.Z - 10, 42);
        if (itemId is not null)
            DrawBuildingBrowserIcon(icon, itemId);
        else
            DrawCenteredUiText("<", icon, new(232, 219, 177, 255));
        DrawSmallCenteredUiText(
            name, new(bounds.X + 2, bounds.Y + 47, bounds.Z - 4, 17),
            new(236, 222, 178, 255));
        if (hovered)
            DrawUiHoverTooltip(
                name == "Back" ? "Back" : $"{name} · {description}",
                bounds);
    }

    private void DrawWallBuildIcon(Vector4 bounds, string itemId)
    {
        var frontWallKey = PalisadeWallVisuals.FrontFrameKeyFor(itemId);
        if (!_treeAtlas.TryGetValue(frontWallKey, out var wall))
            return;
        var scale = MathF.Min(
            bounds.Z / Math.Max(1, wall.Frame.Width),
            bounds.W / Math.Max(1, wall.Frame.Height));
        var width = wall.Frame.Width * scale;
        var height = wall.Frame.Height * scale;
        DrawUiSprite(
            wall.Frame,
            wall.Texture,
            new(
                bounds.X + (bounds.Z - width) * .5f,
                bounds.Y + (bounds.W - height) * .5f,
                width,
                height),
            brightness: .08f,
            spriteOutline: Vector3.Zero,
            spriteTexelSize: new(
                (wall.U1 - wall.U0) / Math.Max(1, wall.Frame.Width),
                (wall.V1 - wall.V0) / Math.Max(1, wall.Frame.Height)),
            uvRectangle: new(
                wall.U0, wall.V0,
                wall.U1 - wall.U0,
                wall.V1 - wall.V0));
    }

    private void DrawBuildingBrowserIcon(Vector4 bounds, string itemId)
    {
        if (WallCatalog.IsWall(itemId))
        {
            DrawWallBuildIcon(bounds, itemId);
            return;
        }
        var buildingKey = HouseCatalog.IsHouse(itemId)
            ? HouseVisuals.AtlasKey(itemId)
            : DefenceBuildingCatalog.IsDefence(itemId)
                ? DefenceBuildingVisuals.AtlasKey(itemId)
                : GateCatalog.IsGate(itemId)
                    ? GateVisuals.AtlasKey(itemId)
                    : null;
        if (buildingKey is null ||
            !_treeAtlas.TryGetValue(buildingKey, out var house))
            return;
        var scale = MathF.Min(
            bounds.Z / Math.Max(1, house.Frame.Width),
            bounds.W / Math.Max(1, house.Frame.Height));
        var width = house.Frame.Width * scale;
        var height = house.Frame.Height * scale;
        DrawUiSprite(
            house.Frame, house.Texture,
            new(
                bounds.X + (bounds.Z - width) * .5f,
                bounds.Y + (bounds.W - height) * .5f,
                width, height),
            brightness: .08f,
            spriteOutline: Vector3.Zero,
            spriteTexelSize: new(
                (house.U1 - house.U0) / Math.Max(1, house.Frame.Width),
                (house.V1 - house.V0) / Math.Max(1, house.Frame.Height)),
            uvRectangle: new(
                house.U0, house.V0,
                house.U1 - house.U0,
                house.V1 - house.V0));
    }

    internal void UpdatePlayerConstruction()
    {
        if (_player is null || _activePlayerConstructionId is not { } siteId)
            return;
        var location = FindGroundObjectLocation(siteId);
        if (location is null ||
            !ConstructionService.IsConstructionSite(location.Value.Object))
        {
            _activePlayerConstructionId = null;
            if (_player.Action == EntityAction.Build) _player.Stop();
            AdvancePlayerConstructionSequence();
            return;
        }
        if (_player.Action != EntityAction.Build)
        {
            _activePlayerConstructionId = null;
            return;
        }
        if (!_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Build), out var animation))
            return;
        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / 5);
        var cycleDuration = Math.Max(
            framesPerAngle * animation.SecondsPerFrame, .1f);
        var impactFrame = Math.Clamp(6, 0, framesPerAngle - 1);
        var impactTime = impactFrame * animation.SecondsPerFrame;
        if (_player.ActionTime < impactTime) return;
        var strike = 1 + (int)(
            (_player.ActionTime - impactTime) / cycleDuration);
        if (strike <= _lastPlayerConstructionStrike) return;
        _lastPlayerConstructionStrike = strike;
        var level = CraftingSkill.LevelForExperience(
            _activePlayer?.CraftingExperience ?? 0);
        var addedHealth = ConstructionService.WorkHealth(level, 100);
        var updated = ConstructionService.AddWork(
            location.Value.Object, addedHealth);
        location.Value.Chunk.GroundObjects[location.Value.Index] = updated;
        if (_activePlayer is not null)
            _activePlayer = _activePlayer with
            {
                CraftingExperience = SkillService.AwardExperience(
                    _activePlayer.CraftingExperience, 6).Experience,
                UpdatedUtc = DateTime.UtcNow
            };
        QueueChunkSave(location.Value.Chunk);
        if (ConstructionService.IsConstructionSite(updated))
            return;
        _activePlayerConstructionId = null;
        _player.Stop();
        _chatUi.AddMessage(
            $"You finish building {ItemCatalog.Get(updated.ItemId).Name}.",
            ChatMessageStyle.Action);
        if (_activePlayer is not null) _saves.SavePlayer(_activePlayer);
        AdvancePlayerConstructionSequence();
    }

    private void RenderConstructionHealthBars(Vector4 scene)
    {
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsChunkVisible(gpu)) continue;
            foreach (var site in gpu.Chunk.GroundObjects)
            {
                if (!ConstructionService.IsConstructionSite(site) ||
                    site.Health <= 1 ||
                    !TryGroundObjectVisual(
                        site, out var frame, out _, out _, out _))
                    continue;
                var bounds = SpriteBounds(frame, GroundObjectWorld(site));
                DrawEntityHealthBar(
                    scene,
                    bounds,
                    site.Health / (float)site.MaxHealth);
            }
        }
    }
}
