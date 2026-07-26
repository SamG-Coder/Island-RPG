using IslandRpg.Assets;
using FontStashSharp;
using FontStashSharp.Interfaces;
using IslandRpg.Persistence;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using IslandRpg.Gameplay;
using IslandRpg.World;
using IslandRpg.Rendering.Ui;
using StbImageSharp;

namespace IslandRpg.Rendering;

// MAINTENANCE RULE:
// GameHostWindow is the application coordinator, not a feature container.
// New features must put most rendering, input, state, and gameplay logic in a
// dedicated file and class. Only minimal wiring and lifecycle calls belong here.
internal sealed partial class GameHostWindow : GameWindow
{
    private const int ReferenceWidth = 1280;
    private const int ReferenceHeight = 720;
    internal enum PreviewMode { Assets, Island, World, Game }
    private enum ScreenState { LoadingAssets, PreparingGpu, MainMenu, WorldPreview }
    private enum FrontendPage
    {
        Main,
        CharacterSelect,
        CharacterCreate,
        NewWorld,
        LoadWorld,
        Settings
    }
    private sealed class GpuWorldChunk(
        WorldChunk chunk, int vbo, int vertexCount,
        int weightsA, int weightsB, int weightsC, int weightsD,
        int shoreDistance, Vector4 projectedBounds,
        float[] renderedHeights)
    {
        public WorldChunk Chunk { get; } = chunk;
        public int Vbo { get; } = vbo;
        public int VertexCount { get; } = vertexCount;
        public int WeightsA { get; } = weightsA;
        public int WeightsB { get; } = weightsB;
        public int WeightsC { get; } = weightsC;
        public int WeightsD { get; } = weightsD;
        public int ShoreDistance { get; } = shoreDistance;
        public Vector4 ProjectedBounds { get; } = projectedBounds;
        public float[] RenderedHeights { get; } = renderedHeights;
        public float Opacity { get; set; }
        public WorldVegetationRenderItem[] VegetationRenderItems { get; set; } = [];
        public WorldFishRenderItem[] FishRenderItems { get; set; } = [];
    }
    private sealed record SpriteAtlasEntry(
        SpriteFrame Frame, float U0, float V0, float U1, float V1);
    private sealed record EntityAnimation(
        LoadedGraphic Graphic, int[] Textures, float SecondsPerFrame);
    private sealed record PlayerVisual(
        SpriteFrame Frame, int Texture, Vector2 World, bool Mirror, bool Wading);
    private sealed record GroundToolSprite(
        SpriteFrame Frame,
        int Texture,
        SpriteFrame Shadow);
    private sealed record MoveMarker(
        Vector2 Position, double Time, bool Action = false);
    private sealed record WaterRipple(Vector2 Position, double StartedAt);
    private enum WorldActionType
    {
        CutTree,
        GatherTreeSticks,
        PickUpGroundObject,
        DropGroundObject,
        LightCampfire,
        TakeCampfireFuel,
        CookOnCampfire,
        Fish,
        GatherFibres
    }
    private sealed record QueuedWorldAction(
        WorldActionType Type, Vector2 Target, float Range,
        Guid? GroundObjectId = null,
        int InventorySlot = -1,
        string? ItemId = null,
        string? FishKey = null,
        string? VegetationKey = null);
    private sealed record PathResult(
        int RequestId,
        IReadOnlyList<Vector2> Path,
        QueuedWorldAction? Action = null);
    private sealed record NewWorldPreviewResult(
        string SeedText, long Seed, Vector2 Spawn, byte[] Pixels);
    private readonly string _install;
    private readonly WorldRenderQueue _worldRenderQueue = new();
    private readonly ShaderUniformCache _shaderUniforms = new();
    private readonly PreviewMode _mode;
    private long _worldSeed;
    private readonly GameSaveRepository _saves = new();
    private WorldProfile? _activeWorld;
    private PlayerProfile? _activePlayer;
    private PlayerProfile? _selectedPlayer;
    private FrontendPage _frontendPage;
    private readonly TextBoxControlState _worldNameTextBox = new("New World");
    private readonly TextBoxControlState _seedTextBox =
        new(Random.Shared.NextInt64().ToString());
    private readonly TextBoxControlState _playerNameTextBox = new();
    private EntityGender _newPlayerGender = EntityGender.Male;
    private int _newTeamColor;
    private bool _menuLeftWasDown;
    private string? _frontendError;
    private readonly ModalScreenState _modalScreen = new();
    private readonly PauseMenuController _pauseMenu;
    private readonly ListControlState _characterList = new();
    private readonly ListControlState _worldList = new();
    private WorldChunkStore? _worldStore;
    private readonly Dictionary<ChunkCoordinate, GpuWorldChunk> _worldChunks = [];
    private Task<WorldChunk>? _pendingChunkTask;
    private ChunkCoordinate _pendingChunkCoordinate;
    private Task _saveTail = Task.CompletedTask;
    private bool _atlasOpen;
    private int _atlasDone;
    private int _atlasTotal = 1;
    private int _atlasChunksAcross = WorldAtlasGenerator.ChunksAcross;
    private Vector2 _atlasLastMouse;
    private bool _atlasDragging;
    private bool _atlasLeftWasDown;
    private double _clock;
    private double _atlasLastClickTime = -1;
    private Vector2 _atlasLastClickPosition;
    private Vector2 _atlasCenterIso;
    private readonly Dictionary<WorldAtlasTileKey, int> _atlasTileTextures = [];
    private readonly Dictionary<WorldAtlasTileKey, Task<WorldAtlasTileSnapshot>> _atlasTileTasks = [];
    private HashSet<WorldAtlasTileKey> _visibleAtlasTiles = [];
    private IslandMap? _island;
    private Task<AssetCatalog>? _loadTask;
    private AssetCatalog? _catalog;
    private List<LoadedGraphic> _worldAssets = [];
    private readonly List<int> _textures = [];
    private readonly List<int> _terrainTextures = [];
    private volatile int _done;
    private volatile int _total = 1;
    private string _current = "Reading game data";
    private ScreenState _screen = ScreenState.LoadingAssets;
    private int _uploadIndex;
    private int _terrainUploadIndex;
    private int _program;
    private int _terrainProgram;
    private int _cliffProgram;
    private int _islandVbo;
    private int _islandVertexCount;
    private int _terrainArray;
    private int _biomeWeightsA;
    private int _biomeWeightsB;
    private int _biomeWeightsC;
    private int _biomeWeightsD;
    private int _shoreDistance;
    private int _waterNormalArray;
    private int _streamVbo;
    private int _sceneFramebuffer;
    private int _sceneColor;
    private int _pauseBlurProgram;
    private int _pauseBlurTexture;
    private int _pauseBlurIntermediate;
    private int _pauseBlurFramebuffer;
    private Vector2i _pauseBlurSize;
    private int _treeBatchVbo;
    private int _treeAtlasTexture;
    private int _treeAtlasWidth;
    private int _treeAtlasHeight;
    private int _cliffBatchVbo;
    private int _cliffTexture;
    private readonly Dictionary<string, SpriteAtlasEntry> _treeAtlas =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(EntityGender Gender, EntityAction Action), EntityAnimation>
        _entityAnimations = [];
    private EntityAnimation? _moveMarkerAnimation;
    private MouseCursor? _defaultNativeCursor;
    private MouseCursor? _cutNativeCursor;
    private MouseCursor? _pickupNativeCursor;
    private MouseCursor? _dropNativeCursor;
    private enum GameCursorKind
    {
        Default,
        CutTree,
        PickUpItem,
        DropItem
    }
    private GameCursorKind _gameCursorKind;
    private int _uiPanelFillTexture;
    private int _uiSolidTexture;
    private SpriteFrame? _uiTabFrame;
    private int _uiTabTexture;
    private int _uiActiveTabTexture;
    private int _woodcuttingItemsTexture;
    private readonly int[] _woodcuttingItemTextures = new int[8];
    private readonly SpriteFrame?[] _woodcuttingItemFrames = new SpriteFrame?[8];
    private readonly SpriteFrame?[] _woodcuttingInventoryFrames =
        new SpriteFrame?[8];
    private readonly SpriteFrame?[] _woodcuttingShadowFrames = new SpriteFrame?[8];
    private readonly int[] _naturalItemTextures = new int[4];
    private readonly SpriteFrame?[] _naturalItemFrames = new SpriteFrame?[4];
    private readonly SpriteFrame?[] _naturalShadowFrames = new SpriteFrame?[4];
    private readonly int[] _supplementalItemTextures = new int[12];
    private readonly SpriteFrame?[] _supplementalItemFrames =
        new SpriteFrame?[12];
    private readonly SpriteFrame?[] _supplementalShadowFrames =
        new SpriteFrame?[12];
    private readonly int[] _stoneToolTextures = new int[4];
    private readonly SpriteFrame?[] _stoneToolFrames = new SpriteFrame?[4];
    private readonly SpriteFrame?[] _stoneToolShadowFrames = new SpriteFrame?[4];
    private readonly Dictionary<string, GroundToolSprite> _groundToolSprites =
        new(StringComparer.OrdinalIgnoreCase);
    private PlaceableObjectSprites _placeableObjectSprites = new();
    private CoastalCollectibleSprites _coastalSprites = new();
    private FibreNetItemSprites _fibreNetSprites = new();
    private readonly CoastalCollectibleRespawnController _coastalRespawns = new();
    private static readonly SpriteFrame WoodcuttingItemsFrame =
        new(128, 64, 0, 0, []);
    private readonly MinimapControlState _minimapUi = new();
    private SpriteFrame? _minimapFrame;
    private int _minimapTexture;
    private int _newWorldPreviewTexture;
    private SpriteFrame? _newWorldPreviewFrame;
    private Task<NewWorldPreviewResult>? _newWorldPreviewTask;
    private string? _newWorldPreviewSeedText;
    private Vector2i _minimapCenter = new(int.MinValue, int.MinValue);
    private byte[]? _minimapTerrain;
    private Task<MinimapBuildResult>? _minimapBuildTask;
    private sealed record MinimapBuildResult(
        Vector2i Center, byte[] Terrain, byte[] Pixels);
    private static readonly SpriteFrame SolidUiFrame = new(1, 1, 0, 0, []);
    private MoveMarker? _moveMarker;
    private Task<PathResult>? _pendingPathTask;
    private CancellationTokenSource? _pathCancellation;
    private int _pathRequestId;
    private QueuedWorldAction? _queuedAction;
    private Guid? _activeTreeId;
    private Guid? _activeTreeStickGatherId;
    private Guid? _activeGroundPickupId;
    private GroundDropPreview? _groundDropPreview;
    private ActiveGroundDrop? _activeGroundDrop;
    private int _lastTreeStrike;
    private readonly List<WaterRipple> _waterRipples = [];
    private int _lastWaterFootfall = -1;
    private WorldEntity? _player;
    private bool _gameLeftWasDown;
    private bool _gameRightWasDown;
    private readonly InventoryInteractionController _inventoryInteraction =
        new();
    private readonly PlaceableObjectPlacementController
        _placeableObjectPlacement = new();
    private bool _skillsLeftWasDown;
    private readonly ListControlState _skillsList = new();
    private int _selectedSkill = -1;
    private readonly GameUiControlState _gameUi = new();
    private readonly ChatUiControlState _chatUi = new();
    private readonly RepeatedActionMonologue _repeatedActions = new();
    private readonly SettingsMenuState _settingsMenu = new();
    private readonly DeveloperSettingsController _developerSettings = new();
    private readonly DeveloperMapWindow _developerMap = new();
    private readonly SkillGuideWindowState _skillGuideWindow = new();
    private readonly WorldActionController _worldActions;
    private string? _overheadSpeech;
    private double _overheadSpeechExpiresAt;
    private readonly ContextMenuControlState _inventoryContext = new();
    private readonly ContextMenuControlState _treeContext = new();
    private readonly ContextMenuControlState _groundObjectContext = new();
    private IslandTree? _treeContextTarget;
    private Vector2 _treeContextWalkTarget;
    private WorldGroundObject? _groundObjectContextTarget;
    private Vector2 _groundObjectContextWalkTarget;
    private int _inventoryContextSlot = -1;
    private int _activeInventorySlot = -1;
    private int _inventoryDraggingSlot => _inventoryInteraction.DraggingSlot;
    private FontSystem? _fontSystem;
    private DynamicSpriteFont? _chatFont;
    private OpenGlFontRenderer? _fontRenderer;
    private float _chatLineHeight = 16;
    private float _uiOpacity = 1;
    private int _vao;
    private bool _dragging;
    private Vector2 _lastMouse;
    private Vector2 _camera;
    private float _zoom = 1;
    private float _waterTime;
    private double _worldGameSeconds;

    public AssetCatalog? Catalog => _catalog;

    public GameHostWindow(string install, PreviewMode mode = PreviewMode.Assets, long worldSeed = 2187) : base(
        GameWindowSettings.Default,
        new NativeWindowSettings
        {
            ClientSize = new Vector2i(1280, 720),
            Title = "Island RPG"
        })
    {
        _install = install;
        _mode = mode;
        _worldSeed = worldSeed;
        _pauseMenu = new(this);
        _worldActions = new(this);
        InitializeFishing();
        InitializeFibreGathering();
        _inventoryContext.Selected += HandleInventoryContextSelection;
        _treeContext.Selected += HandleTreeContextSelection;
        _groundObjectContext.Selected +=
            HandleGroundObjectContextSelection;
        _chatUi.Submitted += HandleChatSubmission;
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _program = GameShaderPrograms.CreateSpriteProgram();
        _pauseBlurProgram =
            GameShaderPrograms.CreateModalBlurProgram();
        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);
        _streamVbo = GL.GenBuffer();
        CreateSceneTarget();
        PrepareGameUi();
        InitializeCampfireLighting();
        var settings = _saves.LoadSettings();
        ApplyDisplaySettings(settings);
        var progress = new Progress<(int Done, int Total, string Name)>(value =>
        {
            _done = value.Done;
            _total = Math.Max(1, value.Total);
            _current = value.Name;
        });
        var requiredGraphics = RequiredGraphicsFor(_mode);
        _loadTask = Task.Run(() =>
            AssetLoader.LoadAll(_install, progress, requiredGraphics));
    }

    private static IReadOnlySet<string>? RequiredGraphicsFor(PreviewMode mode)
    {
        if (mode == PreviewMode.Assets) return null;

        var names = Enumerable.Range(0, 12)
            .Select(index => $"TREE{(char)('A' + index)}_NN")
            .Concat([
                "FPAL_NN", "FPIN_NN", "FOAK_NN", "FJUN_NN",
                "FSNO_NN", "FBAM_NN", "FCAC_NN"
            ])
            .SelectMany(name => new[] { name, name[..^2] + "N0" })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        names.Add("STUMP_NN");
        names.Add("STUMB_NN");

        if (mode is PreviewMode.World or PreviewMode.Game)
        {
            foreach (var name in Enumerable.Range(1, 9)
                         .Select(index => $"CLF{index:00}_NN"))
                names.Add(name);
            foreach (var name in
                     WorldVegetationGenerator.RequiredGraphicNames)
                names.Add(name);
            foreach (var name in
                     WorldFishGenerator.RequiredGraphicNames)
                names.Add(name);
        }

        if (mode == PreviewMode.Game)
        {
            foreach (var name in new[]
            {
                "VMBAS_WN", "VMBAS_AN", "VMBAS_DN",
                "VFBAS_WN", "VFBAS_AN", "VFBAS_DN",
                "VMLUM_AN", "VFLUM_AN",
                "VMFOR_TN", "VFFOR_TN",
                "VMFIS_TN", "VFFIS_TN", "MOVEX_NN"
            })
                names.Add(name);
        }

        return names;
    }

    protected override void OnUpdateFrame(FrameEventArgs e)
    {
        base.OnUpdateFrame(e);
        _clock += e.Time;
        _waterTime = (_waterTime + (float)e.Time) % 10000f;
        if (KeyboardState.IsKeyPressed(Keys.Escape))
        {
            if (_placeableObjectPlacement.Active)
                CancelPlaceableObjectPlacement();
            else if (_screen == ScreenState.MainMenu &&
                _frontendPage != FrontendPage.Main)
            {
                _frontendPage = FrontendPage.Main;
                BlurTextBoxes();
                _frontendError = null;
            }
            else if (_screen == ScreenState.WorldPreview &&
                     _mode == PreviewMode.Game)
            {
                if (_chatUi.Input.Focused)
                    _chatUi.BlurInput();
                else if (_developerMap.IsOpen)
                    CloseDeveloperMap();
                else if (_skillGuideWindow.Visible)
                    CloseSkillGuideWindow();
                else if (_craftingWindowOpen)
                    CloseCraftingWindow();
                else
                    _pauseMenu.HandleEscapeKey();
            }
            else Close();
        }
        if (_developerMap.IsOpen &&
            KeyboardState.IsKeyPressed(Keys.T))
        {
            _developerMap.ToggleTreeDensity();
            RequestVisibleAtlasTiles();
        }
        if (_screen == ScreenState.WorldPreview && _mode == PreviewMode.World &&
            KeyboardState.IsKeyPressed(Keys.M))
        {
            _atlasOpen = !_atlasOpen;
            if (_atlasOpen) StartAtlasAtCamera();
        }

        if (_screen == ScreenState.LoadingAssets && _loadTask is { IsCompleted: true })
        {
            if (_loadTask.IsFaulted)
                throw _loadTask.Exception?.GetBaseException() ?? new InvalidOperationException("Asset loading failed.");
            _catalog = _loadTask.Result;
            if (_mode == PreviewMode.Island)
                _island = IslandGenerator.Generate();
            var islandGraphics = _mode is PreviewMode.World or PreviewMode.Game
                ? Enumerable.Range(0, 12).Select(index => $"TREE{(char)('A' + index)}_NN")
                    .Concat([
                        "FPAL_NN", "FPIN_NN", "FOAK_NN", "FJUN_NN",
                        "FSNO_NN", "FBAM_NN", "FCAC_NN"
                    ])
                    .Concat(Enumerable.Range(1, 9).Select(index => $"CLF{index:00}_NN"))
                    .SelectMany(name => new[] { name, name[..^2] + "N0" })
                    .Concat(["STUMP_NN", "STUMB_NN"])
                    .Concat(WorldVegetationGenerator.RequiredGraphicNames)
                    .Concat(WorldFishGenerator.RequiredGraphicNames)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : _island?.Trees
                    .SelectMany(tree => new[] { tree.GraphicName, tree.GraphicName[..^2] + "N0" })
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _worldAssets = _catalog.Graphics.Values
                .Where(asset => asset.Kind is not (GraphicKind.Interface or GraphicKind.Unknown))
                .Where(asset => islandGraphics is null || islandGraphics.Contains(asset.Definition.Name))
                .OrderBy(asset => asset.Definition.GraphicId)
                .ToList();
            _total = _worldAssets.Count +
                     (_mode == PreviewMode.Assets ? _catalog.TerrainTiles.Count : 0);
            _done = 0;
            _current = "Preparing world graphics";
            _screen = ScreenState.PreparingGpu;
        }

        if (_screen == ScreenState.PreparingGpu)
        {
            // Spread texture creation across frames so the loading screen remains responsive.
            var stop = Math.Min(_uploadIndex + 24, _worldAssets.Count);
            while (_uploadIndex < stop)
            {
                if (_mode is not (PreviewMode.World or PreviewMode.Game))
                    _textures.Add(Upload(_worldAssets[_uploadIndex].Sprite.Frames[0]));
                _current = _worldAssets[_uploadIndex].Definition.Name;
                _uploadIndex++;
            }
            _done = _uploadIndex;
            if (_uploadIndex == _worldAssets.Count)
            {
                if (_mode is PreviewMode.Island or PreviewMode.World or PreviewMode.Game)
                {
                    if (_mode == PreviewMode.Island)
                    {
                        PrepareIslandTerrain();
                        _camera = new Vector2(0, -IslandMap.Size * 24);
                        _zoom = .65f;
                    }
                    else
                    {
                        PrepareWorldTerrain();
                        _camera = Vector2.Zero;
                        _zoom = .8f;
                        if (_mode == PreviewMode.Game)
                        {
                            PrepareEntityAnimations();
                            BeginMenuPreview();
                            _screen = ScreenState.MainMenu;
                            return;
                        }
                        _worldStore = new WorldChunkStore(_worldSeed);
                        StreamWorld();
                    }
                    _screen = ScreenState.WorldPreview;
                    return;
                }
                var terrainStop = Math.Min(_terrainUploadIndex + 8, _catalog!.TerrainTiles.Count);
                while (_terrainUploadIndex < terrainStop)
                {
                    var tile = _catalog.TerrainTiles[_terrainUploadIndex];
                    _terrainTextures.Add(Upload(tile.Width, tile.Height, tile.Rgba));
                    _current = tile.Name;
                    _terrainUploadIndex++;
                }
                _done = _worldAssets.Count + _terrainUploadIndex;
                if (_terrainUploadIndex == _catalog.TerrainTiles.Count)
                    _screen = ScreenState.WorldPreview;
            }
        }

        if (_screen == ScreenState.MainMenu)
        {
            UpdateMenuPreview((float)e.Time);
            UpdateFrontend();
        }

        if (_screen == ScreenState.WorldPreview)
        {
            if (_mode == PreviewMode.Game &&
                _modalScreen.PausesSimulation)
                _pauseMenu.Update();
            else if (_mode == PreviewMode.Game &&
                     _skillGuideWindow.Visible)
            {
                UpdateSkillGuideWindowInput(
                    MouseState.Position,
                    MouseState.IsButtonDown(MouseButton.Left));
                UpdateGame((float)e.Time);
            }
            else if (_mode == PreviewMode.Game && _craftingWindowOpen)
            {
                UpdateCraftingWindowInput(
                    MouseState.Position,
                    MouseState.IsButtonDown(MouseButton.Left));
                UpdateGame((float)e.Time);
            }
            else if (_atlasOpen)
                UpdateAtlas();
            else
            {
                if (_mode == PreviewMode.Game)
                {
                    UpdateGameUi();
                    UpdateGame((float)e.Time);
                }
                else
                    UpdateCamera((float)e.Time);
                if (_mode is PreviewMode.World or PreviewMode.Game)
                {
                    foreach (var chunk in _worldChunks.Values)
                        chunk.Opacity = Math.Min(1, chunk.Opacity + (float)e.Time / .38f);
                    StreamWorld();
                }
            }
        }
    }

    private void UpdateCamera(float elapsed)
    {
        var mouse = SceneMousePosition();
        if (MouseState.IsButtonDown(MouseButton.Left))
        {
            if (_dragging) _camera += mouse - _lastMouse;
            _lastMouse = mouse;
            _dragging = true;
        }
        else _dragging = false;

        var direction = Vector2.Zero;
        if (KeyboardState.IsKeyDown(Keys.A) || KeyboardState.IsKeyDown(Keys.Left)) direction.X += 1;
        if (KeyboardState.IsKeyDown(Keys.D) || KeyboardState.IsKeyDown(Keys.Right)) direction.X -= 1;
        if (KeyboardState.IsKeyDown(Keys.W) || KeyboardState.IsKeyDown(Keys.Up)) direction.Y += 1;
        if (KeyboardState.IsKeyDown(Keys.S) || KeyboardState.IsKeyDown(Keys.Down)) direction.Y -= 1;
        if (direction.LengthSquared > 0)
            _camera += Vector2.Normalize(direction) * 720f * elapsed;
    }

    private void BeginMenuPreview()
    {
        _worldSeed = 78193021;
        var previewRoot = Path.Combine(
            Path.GetTempPath(), "IslandRpg", "MenuPreview");
        _worldStore = new WorldChunkStore(
            _worldSeed, previewRoot, "terrain");
        _camera = new Vector2(120, -80);
        _zoom = .9f;
        _selectedPlayer = _saves.ListPlayers().FirstOrDefault();
        _frontendPage = _selectedPlayer is null
            ? FrontendPage.CharacterCreate
            : FrontendPage.Main;
        if (_selectedPlayer is null) FocusTextBoxAtEnd(_playerNameTextBox);
        else BlurTextBoxes();
        StreamWorld();
    }

    private void UpdateMenuPreview(float elapsed)
    {
        _camera.X -= 7f * elapsed;
        _camera.Y += 2f * elapsed;
        foreach (var chunk in _worldChunks.Values)
            chunk.Opacity = Math.Min(1, chunk.Opacity + elapsed / .38f);
        StreamWorld();
    }

    private void UpdateFrontend()
    {
        if (_frontendPage == FrontendPage.NewWorld)
            UpdateNewWorldPreview();
        var textBox = FocusedTextBox();
        textBox?.UpdateKeyboard(
            KeyboardState,
            () => ClipboardString,
            value => ClipboardString = value);

        var leftDown = MouseState.IsButtonDown(MouseButton.Left);
        if (_frontendPage == FrontendPage.CharacterSelect)
        {
            var players = _saves.ListPlayers().ToArray();
            LayoutCharacterList(players);
            _characterList.UpdatePointer(
                MouseState.Position, leftDown);
        }
        else if (_frontendPage == FrontendPage.LoadWorld)
        {
            var worlds = _saves.ListWorlds().ToArray();
            LayoutWorldList(worlds);
            _worldList.UpdatePointer(
                MouseState.Position, leftDown);
        }
        textBox?.UpdatePointer(
            MouseState.Position, leftDown, MeasureUiText, 14);
        var clicked = leftDown && !_menuLeftWasDown;
        _menuLeftWasDown = leftDown;
        if (!clicked) return;

        var pointer = MouseState.Position;
        _frontendError = null;
        if (_frontendPage != FrontendPage.Main &&
            FrontendCloseButtonBounds().Contains(pointer))
        {
            _frontendPage = FrontendPage.Main;
            BlurTextBoxes();
            _characterList.ClearDeleteApproval();
            _worldList.ClearDeleteApproval();
            return;
        }
        switch (_frontendPage)
        {
            case FrontendPage.Main:
                if (MenuButton(0).Contains(pointer))
                    _frontendPage = FrontendPage.NewWorld;
                else if (MenuButton(1).Contains(pointer))
                    _frontendPage = FrontendPage.LoadWorld;
                else if (MenuButton(2).Contains(pointer))
                    _frontendPage = FrontendPage.CharacterSelect;
                else if (MenuButton(3).Contains(pointer))
                {
                    _frontendPage = FrontendPage.Settings;
                    _settingsMenu.EnsureVisible();
                }
                else if (MenuButton(4).Contains(pointer))
                    Close();
                break;
            case FrontendPage.CharacterSelect:
                UpdateCharacterSelectClick(pointer);
                break;
            case FrontendPage.CharacterCreate:
                UpdateCharacterCreateClick(pointer);
                break;
            case FrontendPage.NewWorld:
                UpdateNewWorldClick(pointer);
                break;
            case FrontendPage.LoadWorld:
                UpdateLoadWorldClick(pointer);
                break;
            case FrontendPage.Settings:
                var settingsPanel = SettingsPanel();
                if (_settingsMenu.SelectAt(settingsPanel, pointer))
                    break;
                if (SettingsMenuState.BackButtonBounds(
                        settingsPanel).Contains(pointer))
                {
                    _frontendPage = FrontendPage.Main;
                    break;
                }
                if (_settingsMenu.SelectedTab == SettingsTab.Display &&
                    UpdateDisplaySettings(pointer, settingsPanel))
                    break;
                else if (_settingsMenu.SelectedTab == SettingsTab.Dev &&
                         UpdateDeveloperSettings(pointer, settingsPanel))
                    break;
                break;
        }
    }

    private void UpdateNewWorldClick(Vector2 pointer)
    {
        if (NewWorldFieldBounds(0).Contains(pointer))
            FocusTextBox(
                _worldNameTextBox, NewWorldFieldBounds(0), pointer);
        else if (NewWorldFieldBounds(1).Contains(pointer))
            FocusTextBox(
                _seedTextBox, NewWorldFieldBounds(1), pointer);
        else if (RandomSeedButtonBounds().Contains(pointer))
        {
            _seedTextBox.SetText(Random.Shared.NextInt64().ToString());
            FocusTextBoxAtEnd(_seedTextBox);
        }
        else if (CreateWorldButtonBounds().Contains(pointer))
            CreateAndEnterWorld();
        else if (BackButtonBounds().Contains(pointer))
        {
            _frontendPage = FrontendPage.Main;
            BlurTextBoxes();
        }
        else
            BlurTextBoxes();
    }

    private void UpdateCharacterCreateClick(Vector2 pointer)
    {
        if (CharacterNameBounds().Contains(pointer))
            FocusTextBox(
                _playerNameTextBox, CharacterNameBounds(), pointer);
        else if (GenderButtonBounds().Contains(pointer))
            _newPlayerGender = _newPlayerGender == EntityGender.Male
                ? EntityGender.Female
                : EntityGender.Male;
        else if (CreateCharacterButtonBounds().Contains(pointer))
            CreateCharacter();
        else
        {
            for (var index = 0; index < 8; index++)
                if (TeamSwatchBounds(index).Contains(pointer))
                {
                    _newTeamColor = index;
                    return;
                }
            if (_saves.ListPlayers().Count > 0 &&
                BackButtonBounds().Contains(pointer))
            {
                _frontendPage = FrontendPage.CharacterSelect;
                BlurTextBoxes();
            }
        }
    }

    private void CreateCharacter()
    {
        if (string.IsNullOrWhiteSpace(_playerNameTextBox.Text))
        {
            _frontendError = "Enter a character name.";
            return;
        }
        _selectedPlayer = _saves.CreatePlayer(
            _playerNameTextBox.Text, _newPlayerGender,
            skinTone: 2, teamColor: _newTeamColor);
        _playerNameTextBox.SetText("");
        BlurTextBoxes();
        _frontendPage = FrontendPage.Main;
    }

    private void UpdateCharacterSelectClick(Vector2 pointer)
    {
        var players = _saves.ListPlayers().ToArray();
        LayoutCharacterList(players);
        if (_characterList.TryHit(
                pointer, out var index, out var delete))
        {
            var player = players[index];
            if (!delete)
            {
                _selectedPlayer = player;
                _characterList.SelectedId = player.Id;
                _characterList.ClearDeleteApproval();
                return;
            }
            if (!_characterList.ApproveDelete(player.Id))
                return;
            var deletingSelected = _selectedPlayer?.Id == player.Id;
            _saves.DeletePlayer(player.Id);
            var remaining = _saves.ListPlayers();
            if (deletingSelected) _selectedPlayer = remaining.FirstOrDefault();
            if (remaining.Count == 0)
            {
                _frontendPage = FrontendPage.CharacterCreate;
                FocusTextBoxAtEnd(_playerNameTextBox);
            }
            return;
        }

        if (NewCharacterButtonBounds().Contains(pointer))
        {
            _playerNameTextBox.SetText("");
            _frontendPage = FrontendPage.CharacterCreate;
            FocusTextBoxAtEnd(_playerNameTextBox);
        }
        else if (_selectedPlayer is not null &&
                 ContinueCharacterButtonBounds().Contains(pointer))
            _frontendPage = FrontendPage.Main;
        else if (BackButtonBounds().Contains(pointer))
            _frontendPage = FrontendPage.Main;
    }

    private void UpdateLoadWorldClick(Vector2 pointer)
    {
        var worlds = _saves.ListWorlds().ToArray();
        LayoutWorldList(worlds);
        if (_worldList.TryHit(pointer, out var index, out var delete))
        {
            var world = worlds[index];
            if (delete)
            {
                if (_worldList.ApproveDelete(world.Id))
                    _saves.DeleteWorld(world.Id);
                return;
            }
            _worldList.ClearDeleteApproval();
            if (_selectedPlayer is null)
            {
                _frontendPage = FrontendPage.CharacterCreate;
                FocusTextBoxAtEnd(_playerNameTextBox);
            }
            else
                EnterWorld(world, _selectedPlayer);
            return;
        }
        if (BackButtonBounds().Contains(pointer))
            _frontendPage = FrontendPage.Main;
    }

    private void CreateAndEnterWorld()
    {
        if (_selectedPlayer is null)
        {
            _frontendPage = FrontendPage.CharacterCreate;
            FocusTextBoxAtEnd(_playerNameTextBox);
            return;
        }
        if (string.IsNullOrWhiteSpace(_worldNameTextBox.Text))
        {
            _frontendError = "A world name is required.";
            return;
        }

        var seed = SeedFromText(_seedTextBox.Text);
        _worldSeed = seed;
        var spawn = FindPlayableSpawn();
        var player = _selectedPlayer;
        var world = _saves.CreateWorld(
            _worldNameTextBox.Text, seed, player.Id);
        _saves.SaveWorldPlayer(
            world.Id,
            new(player.Id, spawn.X, spawn.Y, DateTime.UtcNow));
        EnterWorld(world, player);
    }

    private void EnterWorld(WorldProfile world, PlayerProfile? player = null)
    {
        player ??= _selectedPlayer;
        player ??= _saves.ListPlayers().FirstOrDefault();
        _worldSeed = world.Seed;
        var spawn = FindPlayableSpawn();
        player ??= _saves.CreatePlayer(
            "Adventurer", EntityGender.Male, 2, 0);
        var normalizedInventory = PlayerInventory.Normalize(player.Inventory);
        var inventoryMigrated =
            player.Inventory is null ||
            !normalizedInventory.SequenceEqual(player.Inventory);
        if (inventoryMigrated)
        {
            player = player with
            {
                Inventory = normalizedInventory,
                UpdatedUtc = DateTime.UtcNow
            };
            _saves.SavePlayer(player);
            if (_selectedPlayer?.Id == player.Id)
                _selectedPlayer = player;
        }
        var worldPlayer = _saves.LoadWorldPlayer(world.Id, player.Id)
            ?? new WorldPlayerState(
                player.Id, spawn.X, spawn.Y, DateTime.UtcNow);

        FinishPendingMenuChunk();
        foreach (var coordinate in _worldChunks.Keys.ToArray())
            UnloadWorldChunk(coordinate, save: false);

        _activeWorld = world with { LastPlayerId = player.Id };
        _worldGameSeconds = Math.Max(
            0, _activeWorld.ElapsedGameSeconds);
        _activePlayer = player;
        _saves.SaveWorld(_activeWorld);
        _worldStore = new WorldChunkStore(
            world.Seed, _saves.WorldsRoot, world.Id);
        _player = new WorldEntity(
            new Vector2(worldPlayer.PositionX, worldPlayer.PositionY),
            player.Gender);
        _camera = Vector2.Zero;
        _zoom = .8f;
        FollowPlayer();
        StreamWorld();
        BlurTextBoxes();
        _screen = ScreenState.WorldPreview;
    }

    private void ReturnToMainMenu()
    {
        SaveActivePlayerState();
        _pathCancellation?.Cancel();
        _pathCancellation?.Dispose();
        _pathCancellation = null;
        _pendingPathTask = null;
        foreach (var coordinate in _worldChunks.Keys.ToArray())
            UnloadWorldChunk(coordinate, save: true);
        _saveTail.GetAwaiter().GetResult();

        _activeWorld = null;
        _activePlayer = null;
        _player = null;
        _queuedAction = null;
        _activeTreeId = null;
        _moveMarker = null;
        _atlasOpen = false;
        _pauseMenu.SetPaused(false);
        BeginMenuPreview();
        _screen = ScreenState.MainMenu;
    }

    private void SaveActivePlayerState()
    {
        if (_activePlayer is null || _player is null) return;
        _activePlayer = _activePlayer with
        {
            Gender = _player.Gender,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        if (_selectedPlayer?.Id == _activePlayer.Id)
            _selectedPlayer = _activePlayer;
        if (_activeWorld is not null)
        {
            _activeWorld = _activeWorld with
            {
                ElapsedGameSeconds = _worldGameSeconds,
                UpdatedUtc = DateTime.UtcNow
            };
            _saves.SaveWorld(_activeWorld);
            _saves.SaveWorldPlayer(
                _activeWorld.Id,
                new(
                    _activePlayer.Id,
                    _player.Position.X,
                    _player.Position.Y,
                    DateTime.UtcNow));
        }
    }

    private void FinishPendingMenuChunk()
    {
        if (_pendingChunkTask is null) return;
        try
        {
            _pendingChunkTask.GetAwaiter().GetResult();
        }
        finally
        {
            _pendingChunkTask = null;
        }
    }

    private static long SeedFromText(string value)
    {
        if (long.TryParse(value.Trim(), out var numeric)) return numeric;
        unchecked
        {
            const long offset = 1469598103934665603;
            const long prime = 1099511628211;
            var hash = offset;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= prime;
            }
            return hash;
        }
    }

    private void UpdateNewWorldPreview()
    {
        if (_newWorldPreviewTask is { IsCompleted: true })
        {
            var result = _newWorldPreviewTask.GetAwaiter().GetResult();
            _newWorldPreviewTask = null;
            if (result.SeedText == _seedTextBox.Text &&
                _newWorldPreviewTexture != 0)
            {
                GL.BindTexture(
                    TextureTarget.Texture2D, _newWorldPreviewTexture);
                GL.TexSubImage2D(
                    TextureTarget.Texture2D, 0, 0, 0,
                    128, 128, PixelFormat.Rgba,
                    PixelType.UnsignedByte, result.Pixels);
                _newWorldPreviewSeedText = result.SeedText;
            }
        }

        var seedText = _seedTextBox.Text;
        if (_newWorldPreviewTask is not null ||
            seedText == _newWorldPreviewSeedText)
            return;
        _newWorldPreviewTask = Task.Run(
            () => BuildNewWorldPreview(seedText));
    }

    private static NewWorldPreviewResult BuildNewWorldPreview(
        string seedText)
    {
        const int width = 128;
        const int height = 128;
        const float tilesPerPixel = .75f;
        var seed = SeedFromText(seedText);
        var spawn = FindPlayableSpawn(seed);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var worldX = (int)MathF.Floor(
                spawn.X + (x - width * .5f) * tilesPerPixel);
            var worldY = (int)MathF.Floor(
                spawn.Y + (y - height * .5f) * tilesPerPixel);
            var (red, green, blue) = ReliefMinimapColor(
                seed, worldX, worldY);
            var index = (y * width + x) * 4;
            pixels[index] = red;
            pixels[index + 1] = green;
            pixels[index + 2] = blue;
            pixels[index + 3] = 255;
        }

        for (var y = height / 2 - 4; y <= height / 2 + 4; y++)
        for (var x = width / 2 - 4; x <= width / 2 + 4; x++)
        {
            var distance = Math.Abs(x - width / 2) +
                           Math.Abs(y - height / 2);
            if (distance > 5) continue;
            var index = (y * width + x) * 4;
            pixels[index] = 205;
            pixels[index + 1] = distance == 5 ? (byte)170 : (byte)35;
            pixels[index + 2] = 28;
        }
        return new(seedText, seed, spawn, pixels);
    }

    private TextBoxControlState? FocusedTextBox() =>
        new[] { _worldNameTextBox, _seedTextBox, _playerNameTextBox }
            .FirstOrDefault(control => control.Focused);

    private void FocusTextBox(
        TextBoxControlState control, Vector4 bounds, Vector2 pointer)
    {
        BlurTextBoxes();
        control.Bounds = bounds;
        control.Focus(pointer, MeasureUiText, 14);
    }

    private void BlurTextBoxes()
    {
        _worldNameTextBox.Blur();
        _seedTextBox.Blur();
        _playerNameTextBox.Blur();
    }

    private void FocusTextBoxAtEnd(TextBoxControlState control)
    {
        BlurTextBoxes();
        control.FocusAtEnd();
    }

    private float MeasureUiText(string text) =>
        _chatFont?.MeasureString(text).X ?? text.Length * 7;

    private Vector2 SceneMousePosition()
    {
        var scene = SceneClientBounds();
        var scale = scene.Z / ReferenceWidth;
        return new Vector2(
            (MouseState.Position.X - scene.X) / Math.Max(scale, .001f),
            (MouseState.Position.Y - scene.Y) / Math.Max(scale, .001f));
    }

    private Vector4 SceneClientBounds()
    {
        var clientWidth = Math.Max(1, ClientSize.X);
        var clientHeight = Math.Max(1, ClientSize.Y);
        var scale = Math.Min(
            clientWidth / (float)ReferenceWidth,
            clientHeight / (float)ReferenceHeight);
        var width = ReferenceWidth * scale;
        var height = ReferenceHeight * scale;
        return new Vector4(
            (clientWidth - width) * .5f,
            (clientHeight - height) * .5f,
            width,
            height);
    }

    private void UpdateGame(float elapsed)
    {
        if (_player is null) return;
        _worldGameSeconds = WorldTime.Advance(
            _worldGameSeconds, elapsed);
        UpdateExpiredCampfires();
        _worldActions.ProcessPendingPath();

        var rightDown = MouseState.IsButtonDown(MouseButton.Right);
        var placingObject = UpdatePlaceableObjectPlacementInput(
            MouseState.IsButtonDown(MouseButton.Left), rightDown);
        if (!placingObject &&
            rightDown && !_gameRightWasDown &&
            !IsPointerOverGameUi(MouseState.Position))
        {
            var target = ScreenToTerrain(SceneMousePosition());
            if (TryGetGroundObjectUnderMouse(
                    SceneMousePosition(), out var contextObject, out _))
            {
                _groundObjectContextTarget = contextObject;
                _groundObjectContextWalkTarget = target;
                _inventoryContext.Close();
                _treeContext.Close();
                _fishContext.Close();
                _vegetationContext.Close();
                var fixedObject =
                    PlaceableObjectCatalog.IsPlaceable(
                        contextObject.ItemId);
                var campfireState = CampfireService.IsCampfire(contextObject)
                    ? CampfireService.State(
                        contextObject, _worldGameSeconds)
                    : (CampfireState?)null;
                var canCookSelected =
                    campfireState == CampfireState.Lit &&
                    TrySelectedRawCookingItem(
                        out _, out _);
                _groundObjectContext.Open(
                    MouseState.Position,
                    canCookSelected
                        ? ["Cook", "Walk Here", "Examine"]
                        : campfireState == CampfireState.Fueled
                        ? ["Light", "Take log", "Walk Here", "Examine"]
                        : fixedObject
                        ? ["Walk Here", "Examine"]
                        : ["Pick up", "Walk Here", "Examine"],
                    SceneClientBounds(), 142);
            }
            else if (TryGetFishUnderMouse(
                         SceneMousePosition(), out var contextFish))
            {
                OpenFishContext(contextFish, target);
            }
            else if (TryGetFibreShrubUnderMouse(
                         SceneMousePosition(),
                         out var contextVegetation,
                         out var vegetationKey))
            {
                OpenVegetationContext(
                    contextVegetation, vegetationKey, target);
            }
            else if (TryGetTreeUnderMouse(
                    SceneMousePosition(), out var contextTree))
            {
                _treeContextTarget = contextTree;
                _treeContextWalkTarget = target;
                _inventoryContext.Close();
                _groundObjectContext.Close();
                _fishContext.Close();
                _vegetationContext.Close();
                _treeContext.Open(
                    MouseState.Position,
                    ["Chop tree", "Gather sticks", "Walk Here", "Examine"],
                    SceneClientBounds(), 142);
            }
            else
                QueueWalk(target);
        }
        _gameRightWasDown = rightDown;

        var leftDown = MouseState.IsButtonDown(MouseButton.Left);
        if (!placingObject &&
            leftDown && !_gameLeftWasDown &&
            !IsPointerOverGameUi(MouseState.Position))
        {
            if (TryGetGroundObjectUnderMouse(
                    SceneMousePosition(), out var groundObject, out _))
            {
                if (!PlaceableObjectCatalog.IsPlaceable(
                        groundObject.ItemId))
                    QueueGroundObjectPickup(groundObject);
            }
            else if (TryGetFishUnderMouse(
                         SceneMousePosition(), out var fish))
            {
                QueueFishing(fish);
            }
            else if (TryGetFibreShrubUnderMouse(
                         SceneMousePosition(), out _, out var vegetationKey))
            {
                QueueFibreGather(vegetationKey);
            }
            else if (!TryGetTreeUnderMouse(SceneMousePosition(), out var actionTree))
            {
                _gameLeftWasDown = leftDown;
                return;
            }
            else
            {
                _worldActions.QueueTree(
                    actionTree, WorldActionType.CutTree);
            }
        }
        _gameLeftWasDown = leftDown;

        var currentTerrain = SamplePlayerTerrain(
            _player.Position.X, _player.Position.Y);
        var nextTerrain = SamplePlayerTerrain(
            _player.Target.X, _player.Target.Y);
        var uphill = Math.Max(
            0, nextTerrain.Height - currentTerrain.Height);
        var playerBiome = currentTerrain.Biome;
        var wading = playerBiome is Biome.ShallowWater or
            Biome.RiverWater or Biome.MangroveShallows;
        _player.TerrainSpeedMultiplier =
            (wading ? .62f : 1f) / (1f + uphill * .18f);
        _player.Update(elapsed);
        UpdateNativeCursor();
        _worldActions.CompleteQueuedAction();
        _worldActions.Update();
        UpdateWaterRipples(wading);
        _coastalRespawns.Update(
            elapsed,
            _worldChunks.Values.Select(gpu => gpu.Chunk),
            _player.Position,
            QueueChunkSave);
        if (_moveMarker is not null)
        {
            var nextTime = _moveMarker.Time + elapsed;
            var duration = _moveMarkerAnimation is null
                ? 0
                : _moveMarkerAnimation.Textures.Length *
                  _moveMarkerAnimation.SecondsPerFrame;
            _moveMarker = nextTime < duration
                ? _moveMarker with { Time = nextTime }
                : null;
        }
        FollowPlayer();
    }

    private void UpdateGameUi()
    {
        var scene = SceneClientBounds();
        _gameUi.Layout(scene);
        _chatUi.Layout(scene);
        _minimapUi.Layout(scene);
        _gameUi.UpdatePointer(
            MouseState.Position,
            MouseState.IsButtonDown(MouseButton.Left));
        _chatUi.UpdatePointer(
            MouseState.Position,
            MouseState.IsButtonDown(MouseButton.Left));
        _inventoryContext.UpdatePointer(
            MouseState.Position,
            MouseState.IsButtonDown(MouseButton.Left));
        _treeContext.UpdatePointer(
            MouseState.Position,
            MouseState.IsButtonDown(MouseButton.Left));
        _groundObjectContext.UpdatePointer(
            MouseState.Position,
            MouseState.IsButtonDown(MouseButton.Left));
        _fishContext.UpdatePointer(
            MouseState.Position,
            MouseState.IsButtonDown(MouseButton.Left));
        _vegetationContext.UpdatePointer(
            MouseState.Position,
            MouseState.IsButtonDown(MouseButton.Left));
        var leftDown = MouseState.IsButtonDown(MouseButton.Left);
        UpdateCraftingWindowInput(MouseState.Position, leftDown);
        UpdateSkillsPanelInput(MouseState.Position, leftDown);
        var rightDown = MouseState.IsButtonDown(MouseButton.Right);
        if (_gameUi.ActivePanel == GameUiPanel.Inventory)
            UpdateInventoryInteraction(
                new(
                    _gameUi.Panel.Bounds,
                    _activePlayer?.Inventory ?? [],
                    _activeInventorySlot,
                    _inventoryDraggingSlot,
                    allowDragOutsideToGame: true),
                MouseState.Position, leftDown, rightDown);
        else
        {
            _inventoryInteraction.Cancel();
            _groundDropPreview = null;
        }
        if (KeyboardState.IsKeyPressed(Keys.Enter))
        {
            if (_chatUi.Input.Focused)
                _chatUi.Submit();
            else
                _chatUi.FocusInput();
        }
        if (_chatUi.Input.Focused &&
            KeyboardState.IsKeyPressed(Keys.Backspace))
            _chatUi.Backspace();
    }

    private bool IsPointerOverGameUi(Vector2 mouse) =>
        _gameUi.BlocksWorldInput(mouse) ||
        _chatUi.BlocksWorldInput(mouse) ||
        _inventoryContext.HitTest(mouse) ||
        _treeContext.HitTest(mouse) ||
        _groundObjectContext.HitTest(mouse) ||
        _fishContext.HitTest(mouse) ||
        _vegetationContext.HitTest(mouse) ||
        _inventoryDraggingSlot >= 0 ||
        _modalScreen.CapturesAllInput ||
        _minimapUi.HitTest(mouse);

    private PathResult FindActionPath(
        int requestId,
        Vector2 start,
        Vector2 target,
        float standOff,
        WorldActionType actionType,
        CancellationToken cancellationToken,
        Guid? groundObjectId = null,
        int inventorySlot = -1,
        string? itemId = null,
        string? fishKey = null,
        string? vegetationKey = null)
    {
        var targetCell = new Vector2i(
            (int)MathF.Floor(target.X),
            (int)MathF.Floor(target.Y));
        var searchRadius = actionType == WorldActionType.Fish
            ? (int)MathF.Ceiling(standOff)
            : 1;
        var candidates = new List<Vector2>(
            (searchRadius * 2 + 1) * (searchRadius * 2 + 1));
        var canStandInTargetCell = actionType is
            WorldActionType.PickUpGroundObject or
            WorldActionType.DropGroundObject;
        for (var y = -searchRadius; y <= searchRadius; y++)
        for (var x = -searchRadius; x <= searchRadius; x++)
        {
            if (x == 0 && y == 0 && !canStandInTargetCell)
                continue;
            var candidate = new Vector2(
                targetCell.X + x + .5f,
                targetCell.Y + y + .5f);
            if (actionType != WorldActionType.Fish ||
                (candidate - target).Length <= standOff + .25f)
                candidates.Add(candidate);
        }

        List<Vector2>? bestPath = null;
        float bestScore = float.MaxValue;
        foreach (var candidate in candidates.OrderBy(candidate =>
                     (candidate - start).LengthSquared))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sameCell =
                (int)MathF.Floor(candidate.X) == (int)MathF.Floor(start.X) &&
                (int)MathF.Floor(candidate.Y) == (int)MathF.Floor(start.Y);
            var path = GridPathfinder.Find(
                _worldSeed, start, candidate,
                maximumVisited: 8192,
                cancellationToken: cancellationToken);
            if (!sameCell && path.Count == 0) continue;
            var approach = candidate - target;
            var diagonal = MathF.Abs(approach.X) > .5f &&
                           MathF.Abs(approach.Y) > .5f;
            var finalDistance = diagonal
                ? Math.Max(standOff, .72f)
                : standOff;
            var standPosition = actionType == WorldActionType.Fish
                ? candidate
                : target + approach.Normalized() * finalDistance;
            var score = path.Count + (diagonal ? .35f : 0f);
            if (score >= bestScore) continue;

            bestScore = score;
            bestPath = path.ToList();
            if (bestPath.Count == 0)
                bestPath.Add(standPosition);
            else
                bestPath[^1] = standPosition;
        }

        return bestPath is null
            ? new PathResult(requestId, [])
            : new PathResult(
                requestId,
                bestPath,
                new QueuedWorldAction(
                    actionType, target,
                    Math.Max(standOff, .72f) + .08f,
                    groundObjectId,
                    inventorySlot,
                    itemId,
                    fishKey,
                    vegetationKey));
    }

    private float TreeInteractionDistance(string graphicName)
    {
        if (!_treeAtlas.TryGetValue(graphicName, out var entry)) return .58f;
        var frame = entry.Frame;
        var groundY = Math.Clamp(frame.HotspotY, 1, frame.Height);
        var startY = Math.Max(0, groundY - Math.Min(32, frame.Height / 3));
        var trunkRadiusPixels = 0;
        for (var y = startY; y < groundY; y++)
        for (var x = 0; x < frame.Width; x++)
        {
            if (frame.Rgba[(y * frame.Width + x) * 4 + 3] <= 24) continue;
            trunkRadiusPixels = Math.Max(
                trunkRadiusPixels, Math.Abs(x - frame.HotspotX));
        }

        // Include the villager's body/axe clearance while keeping the final
        // point just outside the tree's occupied tile.
        return Math.Clamp(.50f + trunkRadiusPixels / 120f, .54f, .78f);
    }

    private void QueueWalk(Vector2 target)
        => _worldActions.QueueWalk(target);

    private void QueueTreeAction(
        IslandTree tree, WorldActionType actionType)
        => _worldActions.QueueTree(tree, actionType);

    internal void TryStartTreeCutting(Vector2 target)
    {
        if (_player is null) return;
        var x = (int)MathF.Floor(target.X);
        var y = (int)MathF.Floor(target.Y);
        var coordinate = new ChunkCoordinate(
            FloorDiv(x, WorldChunk.Size), FloorDiv(y, WorldChunk.Size));
        if (!_worldChunks.TryGetValue(coordinate, out var gpu)) return;
        var source = gpu.Chunk.Trees.FirstOrDefault(tree => tree.X == x && tree.Y == y);
        if (source is null) return;
        if (!PlayerInventory.HasAxe(_activePlayer?.Inventory))
        {
            var hasBluntAxe =
                PlayerInventory.HasAnyAxe(_activePlayer?.Inventory);
            ReportBlockedAction(
                hasBluntAxe ? "chop-with-blunt-axe" : "chop-without-axe",
                hasBluntAxe
                    ? "Your axe is too blunt. Use small rocks on it to sharpen it."
                    : "You need an axe to chop down this tree.");
            _player.Stop();
            return;
        }
        if (PlayerInventory.IsFull(_activePlayer?.Inventory))
        {
            ReportBlockedAction(
                "chop-inventory-full",
                "Your inventory is full. You cannot begin woodcutting.");
            _player.Stop();
            return;
        }

        var instanceIndex = gpu.Chunk.TreeInstances.FindIndex(
            tree => tree.X == x && tree.Y == y);
        WorldTreeInstance instance;
        if (instanceIndex < 0)
        {
            var maximumHealth = TreeMaximumHealth(source.GraphicName);
            instance = new(
                Guid.NewGuid(), x, y, source.GraphicName,
                maximumHealth, maximumHealth, TreeLifecycleState.Standing);
            gpu.Chunk.TreeInstances.Add(instance);
            QueueChunkSave(gpu.Chunk);
            _chatUi.AddMessage(
                $"You begin cutting the {TreeDisplayName(source.GraphicName)}.",
                ChatMessageStyle.Action);
        }
        else
        {
            instance = gpu.Chunk.TreeInstances[instanceIndex];
            if (instance.State == TreeLifecycleState.Stump) return;
        }

        _activeTreeId = instance.Id;
        _lastTreeStrike = 0;
        _player.WorkAt(target);
    }

    internal void TryStartTreeStickGather(Vector2 target)
    {
        if (_player is null || _activePlayer is null) return;
        var x = (int)MathF.Floor(target.X);
        var y = (int)MathF.Floor(target.Y);
        var coordinate = new ChunkCoordinate(
            FloorDiv(x, WorldChunk.Size), FloorDiv(y, WorldChunk.Size));
        if (!_worldChunks.TryGetValue(coordinate, out var gpu)) return;
        var source = gpu.Chunk.Trees.FirstOrDefault(
            tree => tree.X == x && tree.Y == y);
        if (source is null) return;

        var index = gpu.Chunk.TreeInstances.FindIndex(
            tree => tree.X == x && tree.Y == y);
        WorldTreeInstance instance;
        if (index < 0)
        {
            var maximumHealth = TreeMaximumHealth(source.GraphicName);
            var stickCount = RollTreeStickCount(maximumHealth);
            instance = new(
                Guid.NewGuid(), x, y, source.GraphicName,
                maximumHealth, maximumHealth,
                TreeLifecycleState.Standing,
                stickCount, stickCount);
            gpu.Chunk.TreeInstances.Add(instance);
            index = gpu.Chunk.TreeInstances.Count - 1;
            QueueChunkSave(gpu.Chunk);
        }
        else
        {
            instance = gpu.Chunk.TreeInstances[index];
            if (instance.State == TreeLifecycleState.Stump)
            {
                ReportBlockedAction(
                    "gather-from-stump",
                    "There are no branches to gather from this stump.");
                return;
            }
            if (instance.SticksRemaining < 0)
            {
                var stickCount =
                    RollTreeStickCount(instance.MaxHealth);
                instance = instance with
                {
                    SticksRemaining = stickCount,
                    InitialStickCount = stickCount
                };
                gpu.Chunk.TreeInstances[index] = instance;
                QueueChunkSave(gpu.Chunk);
            }
            else if (instance.InitialStickCount < 0)
            {
                instance = instance with
                {
                    InitialStickCount = instance.SticksRemaining
                };
                gpu.Chunk.TreeInstances[index] = instance;
                QueueChunkSave(gpu.Chunk);
            }
        }

        if (instance.SticksRemaining == 0)
        {
            ReportBlockedAction(
                "gather-empty-tree",
                "You find no loose sticks beneath the tree.");
            return;
        }
        if (PlayerInventory.IsFull(_activePlayer.Inventory))
        {
            ReportBlockedAction(
                "gather-inventory-full",
                "Your inventory is too full to gather a stick.");
            return;
        }

        _activeTreeId = null;
        _activeTreeStickGatherId = instance.Id;
        _player.GatherAt(target);
    }

    private static int RollTreeStickCount(int maximumHealth)
    {
        var rolls = maximumHealth >= 90 ? 3 :
            maximumHealth >= 55 ? 2 : 1;
        var sticks = 0;
        for (var roll = 0; roll < rolls; roll++)
            sticks += Random.Shared.Next(2);
        return Math.Min(sticks, 3);
    }

    internal void UpdateActiveTreeStickGather()
    {
        if (_player is null || _activeTreeStickGatherId is null)
            return;
        if (_player.Action != EntityAction.Gather)
        {
            _activeTreeStickGatherId = null;
            return;
        }
        if (_player.ActionTime < GroundItemActionSeconds) return;

        var id = _activeTreeStickGatherId.Value;
        _activeTreeStickGatherId = null;
        foreach (var gpu in _worldChunks.Values)
        {
            var index = gpu.Chunk.TreeInstances.FindIndex(
                tree => tree.Id == id);
            if (index < 0) continue;
            var instance = gpu.Chunk.TreeInstances[index];
            if (instance.State != TreeLifecycleState.Standing ||
                instance.SticksRemaining <= 0)
                break;
            if (!PlayerInventory.TryAdd(
                    _activePlayer?.Inventory, ItemIds.Sticks,
                    out var inventory))
            {
                ReportBlockedAction(
                    "gather-inventory-full",
                    "Your inventory is too full to gather a stick.");
                break;
            }
            var remaining = instance.SticksRemaining - 1;
            var seedCount = remaining == 0
                ? RollTreeSeedCount()
                : 0;
            var seedItemId = TreeSeedItem(instance.TreeType);
            var seedsReceived = 0;
            for (var seed = 0; seed < seedCount; seed++)
            {
                if (!PlayerInventory.TryAdd(
                        inventory, seedItemId, out var withSeed))
                    break;
                inventory = withSeed;
                seedsReceived++;
            }
            var firstSeed = seedsReceived > 0 &&
                            !_activePlayer!.HasDiscoveredTreeSeed;
            _activePlayer = _activePlayer! with
            {
                Inventory = inventory,
                HasDiscoveredTreeSeed =
                    _activePlayer.HasDiscoveredTreeSeed ||
                    seedsReceived > 0,
                UpdatedUtc = DateTime.UtcNow
            };
            gpu.Chunk.TreeInstances[index] = instance with
            {
                SticksRemaining = remaining
            };
            _saves.SavePlayer(_activePlayer);
            QueueChunkSave(gpu.Chunk);
            _chatUi.AddMessage(
                $"You gather a stick from beneath the tree " +
                $"({instance.InitialStickCount -
                    remaining} / " +
                $"{instance.InitialStickCount} gathered).",
                ChatMessageStyle.Action);
            if (seedsReceived > 0)
            {
                var seedName = ItemCatalog.Get(seedItemId).Name;
                _chatUi.AddMessage(
                    seedsReceived == 1
                        ? $"You find a {seedName[..^1]} with the last stick!"
                        : $"You find {seedsReceived} {seedName} with the last stick!",
                    ChatMessageStyle.Reward);
            }
            if (seedCount > seedsReceived)
                _chatUi.AddMessage(
                    "You do not have enough inventory space for every seed.",
                    ChatMessageStyle.Warning);
            if (firstSeed)
            {
                const string thought =
                    "I wonder what I can do with this...";
                _chatUi.AddMessage(
                    thought, ChatMessageStyle.Monologue);
                ShowOverheadSpeech(thought);
            }
            break;
        }
        _player.Stop();
    }

    private static int RollTreeSeedCount()
    {
        var roll = Random.Shared.NextSingle();
        if (roll < .10f) return 2;
        return roll < .35f ? 1 : 0;
    }

    private static string TreeSeedItem(string treeType)
    {
        if (treeType.StartsWith(
                "FPAL", StringComparison.OrdinalIgnoreCase))
            return ItemIds.PalmSeeds;
        if (treeType.StartsWith(
                "FPIN", StringComparison.OrdinalIgnoreCase))
            return ItemIds.PineSeeds;
        if (treeType.StartsWith(
                "FOAK", StringComparison.OrdinalIgnoreCase))
            return ItemIds.OakSeeds;
        if (treeType.StartsWith(
                "FJUN", StringComparison.OrdinalIgnoreCase))
            return ItemIds.JungleTreeSeeds;
        if (treeType.StartsWith(
                "FSNO", StringComparison.OrdinalIgnoreCase))
            return ItemIds.SnowTreeSeeds;
        if (treeType.StartsWith(
                "FBAM", StringComparison.OrdinalIgnoreCase))
            return ItemIds.BambooSeeds;
        if (treeType.StartsWith(
                "FCAC", StringComparison.OrdinalIgnoreCase))
            return ItemIds.CactusSeeds;
        return ItemIds.TreeSeeds;
    }

    internal void UpdateActiveTreeCutting()
    {
        if (_player is null || _activeTreeId is null ||
            _player.Action != EntityAction.Work ||
            !_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Work), out var animation))
            return;

        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / 5);
        var cycleDuration = Math.Max(
            framesPerAngle * animation.SecondsPerFrame, .1f);
        var strike = (int)(_player.ActionTime / cycleDuration);
        if (strike <= _lastTreeStrike) return;
        _lastTreeStrike = strike;

        foreach (var gpu in _worldChunks.Values)
        {
            var index = gpu.Chunk.TreeInstances.FindIndex(
                tree => tree.Id == _activeTreeId.Value);
            if (index < 0) continue;
            var instance = gpu.Chunk.TreeInstances[index];
            var axe = PlayerInventory.BestAxe(_activePlayer?.Inventory);
            if (axe is null)
            {
                ReportBlockedAction(
                    "chop-without-axe",
                    "You need an axe to chop down this tree.");
                _activeTreeId = null;
                _player.Stop();
                return;
            }
            if (axe.Id == ItemIds.StoneAxe &&
                PlayerInventory.TryBluntStoneTool(
                    _activePlayer?.Inventory,
                    axe.Id,
                    Random.Shared.NextSingle(),
                    out var bluntedInventory))
            {
                _activePlayer = _activePlayer! with
                {
                    Inventory = bluntedInventory,
                    UpdatedUtc = DateTime.UtcNow
                };
                _saves.SavePlayer(_activePlayer);
                _chatUi.AddMessage(
                    "Your stone axe becomes blunt. Use small rocks on it to sharpen it.",
                    ChatMessageStyle.Warning);
                AddBluntToolMonologue(ItemIds.StoneAxe);
                _activeTreeId = null;
                _player.Stop();
                return;
            }
            var experience = _activePlayer?.WoodcuttingExperience ?? 0;
            var strikeResult = WoodcuttingSkill.Roll(
                experience,
                Random.Shared.NextSingle(),
                Random.Shared.NextSingle(),
                axe.WoodcuttingPower);
            if (!strikeResult.Hit)
            {
                _chatUi.AddMessage(
                    $"Woodcutting {strikeResult.Level}: you miss the tree.",
                    ChatMessageStyle.Miss);
                return;
            }

            var damage = Math.Min(instance.Health, strikeResult.Damage);
            var health = Math.Max(0, instance.Health - damage);
            var state = health == 0
                ? TreeLifecycleState.Stump
                : TreeLifecycleState.Standing;
            gpu.Chunk.TreeInstances[index] = instance with
            {
                Health = health,
                State = state
            };
            QueueChunkSave(gpu.Chunk);
            _chatUi.AddMessage(
                $"You hit the {TreeDisplayName(instance.TreeType)} for {damage} damage " +
                $"({health}/{instance.MaxHealth}).",
                ChatMessageStyle.Damage);
            AwardWoodcuttingExperience(
                damage + (state == TreeLifecycleState.Stump
                    ? Math.Max(10, instance.MaxHealth / 5)
                    : 0));
            if (state == TreeLifecycleState.Stump)
            {
                AddWoodcuttingLog(instance.TreeType);
                _chatUi.AddMessage(
                    $"The {TreeDisplayName(instance.TreeType)} falls.",
                    ChatMessageStyle.Action);
                _activeTreeId = null;
                _player.Stop();
            }
            return;
        }

        _activeTreeId = null;
        _player.Stop();
    }

    private static int TreeMaximumHealth(string graphicName)
    {
        if (graphicName.StartsWith("FPAL", StringComparison.OrdinalIgnoreCase))
            return 75;
        if (graphicName.StartsWith("FPIN", StringComparison.OrdinalIgnoreCase))
            return 125;
        if (graphicName.StartsWith("FOAK", StringComparison.OrdinalIgnoreCase))
            return 150;
        if (graphicName.StartsWith("FJUN", StringComparison.OrdinalIgnoreCase))
            return 175;
        if (graphicName.StartsWith("FSNO", StringComparison.OrdinalIgnoreCase))
            return 135;
        if (graphicName.StartsWith("FBAM", StringComparison.OrdinalIgnoreCase))
            return 80;
        if (graphicName.StartsWith("FCAC", StringComparison.OrdinalIgnoreCase))
            return 65;
        if (graphicName.StartsWith("TREE", StringComparison.OrdinalIgnoreCase) &&
            graphicName.Length > 4)
        {
            int[] healthByVariant =
                [100, 125, 90, 150, 110, 175, 95, 135, 105, 160, 120, 145];
            var variant = char.ToUpperInvariant(graphicName[4]) - 'A';
            if ((uint)variant < (uint)healthByVariant.Length)
                return healthByVariant[variant];
        }
        return 100;
    }

    private void AwardWoodcuttingExperience(int amount)
    {
        if (_activePlayer is null || amount <= 0) return;
        var award = WoodcuttingSkill.AwardExperience(
            _activePlayer.WoodcuttingExperience, amount);
        _activePlayer = _activePlayer with
        {
            WoodcuttingExperience = award.Experience,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        _chatUi.AddMessage(
            $"+{award.Gained} Woodcutting XP.",
            ChatMessageStyle.Experience);
        if (award.LevelledUp)
            _chatUi.AddMessage(
                $"Your Woodcutting level is now {award.Level}.",
                ChatMessageStyle.LevelUp);
    }

    private void AddWoodcuttingLog(string treeType)
    {
        if (_activePlayer is null) return;
        var item = TreeLogItem(treeType);
        if (!PlayerInventory.TryAdd(
                _activePlayer.Inventory, item.Id, out var inventory))
        {
            _chatUi.AddMessage(
                "Your inventory is full. The logs were left behind.",
                ChatMessageStyle.Warning);
            return;
        }
        _activePlayer = _activePlayer with
        {
            Inventory = inventory,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        _chatUi.AddMessage(
            $"You add {item.Name} to your inventory.",
            ChatMessageStyle.Experience);
    }

    private static ItemDefinition TreeLogItem(string treeType)
    {
        if (treeType.StartsWith("FOAK", StringComparison.OrdinalIgnoreCase))
            return ItemCatalog.Get(ItemIds.OakLogs);
        if (treeType.StartsWith("FPIN", StringComparison.OrdinalIgnoreCase))
            return ItemCatalog.Get(ItemIds.PineLogs);
        if (treeType.StartsWith("FPAL", StringComparison.OrdinalIgnoreCase))
            return ItemCatalog.Get(ItemIds.PalmLogs);
        if (treeType.StartsWith("FBAM", StringComparison.OrdinalIgnoreCase))
            return ItemCatalog.Get(ItemIds.Bamboo);
        return ItemCatalog.Get(ItemIds.Logs);
    }

    private static string TreeDisplayName(string graphicName)
    {
        if (graphicName.StartsWith("FPAL", StringComparison.OrdinalIgnoreCase))
            return "palm";
        if (graphicName.StartsWith("FPIN", StringComparison.OrdinalIgnoreCase))
            return "pine";
        if (graphicName.StartsWith("FOAK", StringComparison.OrdinalIgnoreCase))
            return "oak";
        if (graphicName.StartsWith("FBAM", StringComparison.OrdinalIgnoreCase))
            return "bamboo";
        if (graphicName.StartsWith("FCAC", StringComparison.OrdinalIgnoreCase))
            return "cactus";
        return "tree";
    }

    private Vector2 ScreenToTerrain(Vector2 screen)
    {
        var projected = (screen - new Vector2(ReferenceWidth, ReferenceHeight) * .5f - _camera) /
                        Math.Max(_zoom, .001f);
        var map = ScreenWorldToMap(projected);
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var elevation = InfiniteWorldGenerator.SampleRenderedHeight(
                _worldSeed, map.X, map.Y);
            map = ScreenWorldToMap(new(projected.X, projected.Y + elevation * 20));
        }
        return map;
    }

    private void FollowPlayer()
    {
        if (_player is null) return;
        var elevation = InfiniteWorldGenerator.SampleRenderedHeight(
            _worldSeed, _player.Position.X, _player.Position.Y);
        var projected = new Vector2(
            (_player.Position.X - _player.Position.Y) * 48,
            (_player.Position.X + _player.Position.Y) * 24 - elevation * 20);
        _camera = -projected * _zoom;
    }

    private void UpdateWaterRipples(bool wading)
    {
        const double lifetime = 1.35;
        _waterRipples.RemoveAll(ripple => _clock - ripple.StartedAt > lifetime);
        if (_player is null || !wading || _player.Action != EntityAction.Move)
        {
            _lastWaterFootfall = -1;
            return;
        }

        if (!_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Move), out var animation))
            return;

        const int authoredAngles = 5;
        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / authoredAngles);
        var cycleDuration = Math.Max(
            animation.SecondsPerFrame * framesPerAngle, .1f);

        // Two contacts per walk cycle. Offset the phase so the first ripple
        // occurs as a foot plants, rather than immediately upon entering water.
        var footfall = (int)Math.Floor(
            (_player.ActionTime / cycleDuration + .34) * 2.0);
        if (footfall == _lastWaterFootfall) return;

        var facing = _player.Facing.LengthSquared > .0001f
            ? _player.Facing.Normalized()
            : Vector2.UnitX;
        var sideways = new Vector2(-facing.Y, facing.X);
        var side = (footfall & 1) == 0 ? -1f : 1f;
        var contact = _player.Position + sideways * (.065f * side) -
                      facing * .025f;
        _waterRipples.Add(new(contact, _clock));
        if (_waterRipples.Count > 4) _waterRipples.RemoveAt(0);
        _lastWaterFootfall = footfall;
    }

    private void UploadWaterRipples()
    {
        const int maximumRipples = 4;
        var count = Math.Min(maximumRipples, _waterRipples.Count);
        GL.Uniform1(
            _shaderUniforms.Get(_terrainProgram, "rippleCount"), count);
        for (var index = 0; index < count; index++)
        {
            var ripple = _waterRipples[_waterRipples.Count - count + index];
            GL.Uniform2(
                _shaderUniforms.Get(
                    _terrainProgram, $"ripplePositions[{index}]"),
                ripple.Position.X / 8f,
                ripple.Position.Y / 8f);
            GL.Uniform1(
                _shaderUniforms.Get(
                    _terrainProgram, $"rippleAges[{index}]"),
                (float)(_clock - ripple.StartedAt));
        }
    }

    private Vector2 FindPlayableSpawn() =>
        FindPlayableSpawn(_worldSeed);

    private static Vector2 FindPlayableSpawn(long seed)
    {
        for (var radius = 0; radius <= 160; radius++)
        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
        {
            if (Math.Max(Math.Abs(x), Math.Abs(y)) != radius) continue;
            var biome = InfiniteWorldGenerator.BiomeAt(seed, x, y);
            if (biome is Biome.DeepWater or Biome.ShallowWater or
                Biome.RiverWater or Biome.MangroveShallows)
                continue;
            return new Vector2(x + .5f, y + .5f);
        }
        throw new InvalidOperationException("No playable land was found near the world origin.");
    }

    private void StartAtlasAtCamera()
    {
        var mapCenter = ScreenToTerrain(
            new(ReferenceWidth * .5f, ReferenceHeight * .5f));
        _atlasCenterIso = new(
            (mapCenter.X - mapCenter.Y) * .5f,
            (mapCenter.X + mapCenter.Y) * .5f);
        RequestVisibleAtlasTiles();
    }

    private void UpdateAtlas()
    {
        foreach (var pair in _atlasTileTasks.Where(pair => pair.Value.IsCompleted).ToArray())
        {
            if (pair.Value.IsFaulted)
                throw pair.Value.Exception?.GetBaseException() ??
                      new InvalidOperationException("Isometric map tile generation failed.");
            var result = pair.Value.Result;
            var texture = Upload(result.Width, result.Height, result.Rgba);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            if (_atlasTileTextures.Remove(pair.Key, out var previous)) GL.DeleteTexture(previous);
            _atlasTileTextures[pair.Key] = texture;
            _atlasTileTasks.Remove(pair.Key);
        }

        var mouse = SceneMousePosition();
        var leftDown = MouseState.IsButtonDown(MouseButton.Left);
        if (leftDown && !_atlasLeftWasDown)
        {
            if (_clock - _atlasLastClickTime <= .36 &&
                (mouse - _atlasLastClickPosition).LengthSquared <= 18 * 18)
            {
                TravelToAtlasPosition(mouse);
                _atlasLeftWasDown = true;
                return;
            }
            _atlasLastClickTime = _clock;
            _atlasLastClickPosition = mouse;
            _atlasLastMouse = mouse;
            _atlasDragging = true;
        }
        else if (leftDown && _atlasDragging)
        {
            var delta = mouse - _atlasLastMouse;
            _atlasCenterIso -= delta / AtlasPixelsPerTile();
            _atlasLastMouse = mouse;
            RequestVisibleAtlasTiles();
        }
        else if (!leftDown && _atlasDragging)
            _atlasDragging = false;
        _atlasLeftWasDown = leftDown;
        RequestVisibleAtlasTiles();
    }

    private void TravelToAtlasPosition(Vector2 mouse)
    {
        if (_developerMap.IsOpen)
        {
            TeleportFromDeveloperMap(mouse);
            return;
        }
        var apparent = _atlasCenterIso +
                       (mouse - new Vector2(ReferenceWidth, ReferenceHeight) * .5f) /
                       AtlasPixelsPerTile();
        var terrainIsoY = apparent.Y;
        float tileX = 0;
        float tileY = 0;
        for (var iteration = 0; iteration < 3; iteration++)
        {
            tileX = apparent.X + terrainIsoY;
            tileY = terrainIsoY - apparent.X;
            var tile = InfiniteWorldGenerator.SampleTile(
                _worldSeed, (int)MathF.Floor(tileX), (int)MathF.Floor(tileY));
            var elevation = (tile.North + tile.East + tile.South + tile.West) / 4f;
            terrainIsoY = apparent.Y + elevation * 1.35f;
        }
        var projected = new Vector2((tileX - tileY) * 48, (tileX + tileY) * 24);
        _zoom = .8f;
        _camera = -projected * _zoom;
        _atlasOpen = false;
        StreamWorld();
    }

    private static float AtlasDisplaySize() =>
        Math.Max(ReferenceWidth, ReferenceHeight);
    private float AtlasPixelsPerTile() =>
        AtlasDisplaySize() / (_atlasChunksAcross * WorldChunk.Size);

    private void ZoomAtlas(float wheelOffset)
    {
        if (wheelOffset == 0) return;
        var nextChunksAcross = wheelOffset > 0
            ? Math.Max(4, _atlasChunksAcross / 2)
            : Math.Min(64, _atlasChunksAcross * 2);
        if (nextChunksAcross == _atlasChunksAcross) return;

        var screenOffset = SceneMousePosition() -
                           new Vector2(ReferenceWidth, ReferenceHeight) * .5f;
        var isoUnderCursor = _atlasCenterIso + screenOffset / AtlasPixelsPerTile();
        _atlasChunksAcross = nextChunksAcross;
        _atlasCenterIso = isoUnderCursor - screenOffset / AtlasPixelsPerTile();
        RequestVisibleAtlasTiles();
    }

    private void RequestVisibleAtlasTiles()
    {
        var chunksPerTile = Math.Max(1, _atlasChunksAcross / 4);
        var span = chunksPerTile * WorldChunk.Size;
        var scale = AtlasPixelsPerTile();
        var halfWidth = ReferenceWidth * .5f / scale;
        var halfHeight = ReferenceHeight * .5f / scale;
        var firstX = (int)MathF.Floor((_atlasCenterIso.X - halfWidth) / span);
        var lastX = (int)MathF.Floor((_atlasCenterIso.X + halfWidth) / span);
        var firstY = (int)MathF.Floor((_atlasCenterIso.Y - halfHeight) / span);
        var lastY = (int)MathF.Floor((_atlasCenterIso.Y + halfHeight) / span);
        var visible = new HashSet<WorldAtlasTileKey>();
        for (var y = firstY; y <= lastY; y++)
        for (var x = firstX; x <= lastX; x++)
            visible.Add(new(
                x, y, chunksPerTile,
                _developerMap.IsOpen
                    ? _developerMap.Layer
                    : WorldAtlasLayer.Terrain));
        _visibleAtlasTiles = visible;

        foreach (var key in visible
                     .OrderBy(key => Math.Abs((key.X + .5f) * span - _atlasCenterIso.X) +
                                     Math.Abs((key.Y + .5f) * span - _atlasCenterIso.Y)))
        {
            if (_atlasTileTextures.ContainsKey(key) || _atlasTileTasks.ContainsKey(key)) continue;
            if (_atlasTileTasks.Count >= 3) break;
            _atlasTileTasks[key] = Task.Run(
                () => WorldAtlasGenerator.GenerateIsometricTile(_worldSeed, key));
        }

        if (_atlasTileTextures.Count > 48)
        {
            foreach (var key in _atlasTileTextures.Keys.Where(key => !visible.Contains(key)).ToArray())
            {
                GL.DeleteTexture(_atlasTileTextures[key]);
                _atlasTileTextures.Remove(key);
                if (_atlasTileTextures.Count <= 48) break;
            }
        }
        Volatile.Write(ref _atlasTotal, visible.Count);
        Interlocked.Exchange(ref _atlasDone,
            visible.Count(key => _atlasTileTextures.ContainsKey(key)));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_screen == ScreenState.MainMenu && e.OffsetY != 0)
        {
            if (_frontendPage == FrontendPage.CharacterSelect)
            {
                LayoutCharacterList(
                    _saves.ListPlayers().ToArray());
                _characterList.Scroll(
                    MouseState.Position, e.OffsetY);
            }
            else if (_frontendPage == FrontendPage.LoadWorld)
            {
                LayoutWorldList(
                    _saves.ListWorlds().ToArray());
                _worldList.Scroll(
                    MouseState.Position, e.OffsetY);
            }
            return;
        }
        if (_screen != ScreenState.WorldPreview || e.OffsetY == 0) return;
        if (_mode == PreviewMode.Game && _pauseMenu.IsPaused) return;
        if (_mode == PreviewMode.Game && _skillGuideWindow.Visible)
        {
            _skillGuideWindow.Scroll(
                MouseState.Position, e.OffsetY);
            return;
        }
        if (_mode == PreviewMode.Game &&
            _gameUi.ActivePanel == GameUiPanel.Skills &&
            _selectedSkill < 0)
        {
            LayoutSkillsList();
            if (_skillsList.Scroll(MouseState.Position, e.OffsetY))
                return;
        }
        if (_mode == PreviewMode.Game)
        {
            _chatUi.Layout(SceneClientBounds());
            if (_chatUi.Scroll(MouseState.Position, e.OffsetY)) return;
        }
        if (_atlasOpen)
        {
            ZoomAtlas(e.OffsetY);
            return;
        }
        var old = _zoom;
        _zoom = Math.Clamp(old * MathF.Pow(1.12f, e.OffsetY), 0.45f, 1.75f);
        var cursor = SceneMousePosition() -
                     new Vector2(ReferenceWidth / 2f, ReferenceHeight / 2f);
        _camera = cursor - (cursor - _camera) * (_zoom / old);
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_screen == ScreenState.MainMenu)
            FocusedTextBox()?.Insert(e.AsString);
        else if (_screen == ScreenState.WorldPreview && _mode == PreviewMode.Game)
            _chatUi.AppendText(e.AsString);
    }

    private void CreateSceneTarget()
    {
        _sceneColor = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _sceneColor);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
            ReferenceWidth, ReferenceHeight, 0, PixelFormat.Rgba,
            PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        _sceneFramebuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFramebuffer);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            _sceneColor,
            0);
        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException($"Scene framebuffer is incomplete: {status}");
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void PresentScene()
    {
        var framebufferWidth = Math.Max(1, FramebufferSize.X);
        var framebufferHeight = Math.Max(1, FramebufferSize.Y);
        GL.Viewport(0, 0, framebufferWidth, framebufferHeight);
        GL.ClearColor(0.025f, 0.028f, 0.025f, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit);

        var scale = Math.Min(
            framebufferWidth / (float)ReferenceWidth,
            framebufferHeight / (float)ReferenceHeight);
        var outputWidth = ReferenceWidth * scale;
        var outputHeight = ReferenceHeight * scale;
        var left = (framebufferWidth - outputWidth) * .5f;
        var top = (framebufferHeight - outputHeight) * .5f;
        var leftNdc = left * 2 / framebufferWidth - 1;
        var rightNdc = (left + outputWidth) * 2 / framebufferWidth - 1;
        var topNdc = 1 - top * 2 / framebufferHeight;
        var bottomNdc = 1 - (top + outputHeight) * 2 / framebufferHeight;
        var integerScale = Math.Max(1, (int)MathF.Round(scale));
        var exactInteger = MathF.Abs(scale - integerScale) < .001f;

        GL.UseProgram(_program);
        GL.Uniform1(_shaderUniforms.Get(_program, "image"), 0);
        GL.Uniform1(_shaderUniforms.Get(_program, "opacity"), 1f);
        GL.Uniform1(_shaderUniforms.Get(_program, "outlineOnly"), 0);
        GL.Uniform1(_shaderUniforms.Get(_program, "wading"), 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _sceneColor);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)(exactInteger ? TextureMinFilter.Nearest : TextureMinFilter.Linear));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)(exactInteger ? TextureMagFilter.Nearest : TextureMagFilter.Linear));
        Draw([
            leftNdc,topNdc,0,1,
            leftNdc,bottomNdc,0,0,
            rightNdc,bottomNdc,1,0,
            rightNdc,topNdc,1,1
        ]);
    }

    protected override void OnRenderFrame(FrameEventArgs e)
    {
        base.OnRenderFrame(e);
        _performanceMetrics.RecordFrame(e.Time);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFramebuffer);
        GL.Viewport(0, 0, ReferenceWidth, ReferenceHeight);
        GL.ClearColor(0.08f, 0.09f, 0.08f, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        if (_screen is ScreenState.WorldPreview or ScreenState.MainMenu)
        {
            if (_screen == ScreenState.WorldPreview && _atlasOpen) RenderAtlas();
            else if (_mode == PreviewMode.Island) RenderIsland();
            else if (_mode is PreviewMode.World or PreviewMode.Game)
            {
                RenderWorld();
            }
            else RenderWorldPreview();
        }
        else RenderLoading();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        PresentScene();
        if (_screen == ScreenState.WorldPreview &&
            _mode == PreviewMode.Game && !_atlasOpen)
        {
            RenderDayNightOverlay();
            RenderCampfireLights();
        }
        if (_screen is ScreenState.LoadingAssets or ScreenState.PreparingGpu)
        {
            GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
            RenderLoadingUi();
        }
        else if (_screen == ScreenState.MainMenu)
        {
            GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
            RenderFrontend();
        }
        else if (_screen == ScreenState.WorldPreview &&
            _mode == PreviewMode.Game && !_atlasOpen)
        {
            GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
            if (_modalScreen.BlursBackground) BlurComposedFrame();
            if (!_modalScreen.HidesGameUi) RenderGameUi();
            if (_pauseMenu.IsPaused) RenderPauseMenu();
            else if (_craftingWindowOpen) RenderCraftingWindow();
            else if (_skillGuideWindow.Visible) RenderSkillGuideWindow();
        }
        else if (_screen == ScreenState.WorldPreview &&
                 _mode == PreviewMode.Game &&
                 _atlasOpen && _developerMap.IsOpen)
        {
            GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
            RenderDeveloperMapOverlay();
        }
        RenderPerformanceMetrics();
        SwapBuffers();
    }

    private void RenderLoading()
    {
        GL.ClearColor(.025f, .027f, .024f, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit);
    }

    private void RenderLoadingUi()
    {
        var panel = FrontendPanel(560, 190);
        DrawAoEPanelBorder(panel);
        DrawCenteredUiText(
            "ISLAND RPG", new(panel.X, panel.Y + 24, panel.Z, 40),
            new(232, 217, 166, 255));
        DrawCenteredUiText(
            _current, new(panel.X + 32, panel.Y + 73, panel.Z - 64, 25),
            new(183, 173, 143, 255));
        var track = new Vector4(
            panel.X + 42, panel.Y + 119, panel.Z - 84, 24);
        DrawUiColor(track, new(.025f, .024f, .020f, 1));
        DrawPanelOutline(track, 0, new(.24f, .20f, .12f, 1));
        var ratio = Math.Clamp(_done / (float)Math.Max(1, _total), 0, 1);
        if (ratio > 0)
            DrawUiColor(
                new(track.X + 3, track.Y + 3, (track.Z - 6) * ratio, track.W - 6),
                new(.32f, .25f, .11f, 1));
        DrawCenteredUiText(
            $"{(int)(ratio * 100)}%",
            new(track.X, track.Y, track.Z, track.W),
            new(235, 221, 179, 255));
    }

    private void RenderFrontend()
    {
        DrawUiColor(
            new(0, 0, ClientSize.X, ClientSize.Y),
            new(0, 0, 0, .22f));
        switch (_frontendPage)
        {
            case FrontendPage.Main:
                RenderMainMenu();
                break;
            case FrontendPage.CharacterSelect:
                RenderCharacterSelectMenu();
                break;
            case FrontendPage.CharacterCreate:
                RenderCharacterCreateMenu();
                break;
            case FrontendPage.NewWorld:
                RenderNewWorldMenu();
                break;
            case FrontendPage.LoadWorld:
                RenderLoadWorldMenu();
                break;
            case FrontendPage.Settings:
                RenderSettingsMenu();
                break;
        }
        if (_frontendPage != FrontendPage.Main)
            DrawMenuButton(FrontendCloseButtonBounds(), "X");
    }

    private void RenderMainMenu()
    {
        var panel = FrontendPanel(400, 470);
        DrawAoEPanelBorder(panel);
        DrawCenteredUiText("ISLAND RPG", new(panel.X, panel.Y + 34, panel.Z, 42),
            new(232, 217, 166, 255));
        if (_selectedPlayer is not null)
            DrawCenteredUiText(
                $"Playing as {_selectedPlayer.Name}",
                new(panel.X + 24, panel.Y + 78, panel.Z - 48, 26),
                new(184, 174, 143, 255));
        var captions = new[]
        {
            "New World", "Load World", "Change Character",
            "Settings", "Exit / Quit"
        };
        for (var index = 0; index < captions.Length; index++)
            DrawMenuButton(MenuButton(index), captions[index]);
    }

    private void RenderCharacterCreateMenu()
    {
        var panel = FrontendPanel(760, 640);
        DrawAoEPanelBorder(panel);
        DrawCenteredUiText(
            "CREATE CHARACTER", new(panel.X, panel.Y + 16, panel.Z, 38),
            new(232, 217, 166, 255));
        DrawCenteredUiText(
            "Choose your adventurer's identity and appearance",
            new(panel.X + 40, panel.Y + 52, panel.Z - 80, 24),
            new(169, 159, 130, 255));

        var preview = CharacterPreviewBounds();
        DrawUiColor(preview, new(.025f, .027f, .024f, .72f));
        DrawPanelOutline(preview, 0, new(.28f, .23f, .14f, 1));
        DrawPanelOutline(preview, 1, new(.10f, .085f, .055f, 1));
        DrawCenteredUiText(
            "CHARACTER PREVIEW",
            new(preview.X, preview.Y + 10, preview.Z, 24),
            new(204, 190, 150, 255));
        DrawCharacterPreview(
            _newPlayerGender, _newTeamColor, preview);

        var form = CharacterFormBounds();
        DrawUiColor(form, new(.038f, .036f, .030f, .82f));
        DrawPanelOutline(form, 0, new(.28f, .23f, .14f, 1));
        DrawPanelOutline(form, 1, new(.10f, .085f, .055f, 1));
        DrawUiText(
            "IDENTITY & APPEARANCE",
            new(form.X + 18, form.Y + 16),
            new(218, 202, 158, 255));
        DrawUiColor(
            new(form.X + 18, form.Y + 45, form.Z - 36, 1),
            new(.25f, .20f, .11f, 1));

        var name = CharacterNameBounds();
        _playerNameTextBox.Bounds = name;
        DrawUiText("Name", new(name.X, name.Y - 23),
            new(204, 190, 150, 255));
        DrawTextField(_playerNameTextBox);

        DrawUiText("Gender", new(
            GenderButtonBounds().X, GenderButtonBounds().Y - 23),
            new(204, 190, 150, 255));
        DrawMenuButton(
            GenderButtonBounds(),
            _newPlayerGender == EntityGender.Male ? "Male" : "Female");

        DrawUiText("Clothing Colour", new(
            TeamSwatchBounds(0).X, TeamSwatchBounds(0).Y - 25),
            new(204, 190, 150, 255));
        for (var index = 0; index < 8; index++)
            DrawColorSwatch(
                TeamSwatchBounds(index), TeamColor(index),
                index == _newTeamColor);

        if (_frontendError is not null)
            DrawCenteredUiText(
                _frontendError,
                new(form.X + 18, form.Y + form.W - 48,
                    form.Z - 36, 24),
                new(220, 104, 82, 255));
        DrawUiColor(
            new(panel.X + 36, panel.Y + panel.W - 108,
                panel.Z - 72, 1),
            new(.25f, .20f, .11f, 1));
        DrawMenuButton(CreateCharacterButtonBounds(), "Create Character");
        if (_saves.ListPlayers().Count > 0)
            DrawMenuButton(BackButtonBounds(), "Back");
    }

    private void RenderCharacterSelectMenu()
    {
        var panel = FrontendPanel(660, 600);
        DrawAoEPanelBorder(panel);
        DrawCenteredUiText(
            "SELECT CHARACTER", new(panel.X, panel.Y + 20, panel.Z, 38),
            new(232, 217, 166, 255));
        var players = _saves.ListPlayers().ToArray();
        LayoutCharacterList(players);
        foreach (var index in _characterList.VisibleIndices)
        {
            var player = players[index];
            var row = _characterList.RowBounds(index);
            DrawMenuButton(
                row, player.Name,
                TeamColor(player.TeamColor));
            if (_selectedPlayer?.Id == player.Id)
                DrawPanelOutline(row, 3, new(.62f, .48f, .20f, 1));
            DrawMenuButton(
                _characterList.DeleteBounds(index),
                _characterList.IsDeletePending(player.Id)
                    ? "Confirm?"
                    : "Delete");
        }
        RenderListScrollbar(_characterList);
        DrawMenuButton(NewCharacterButtonBounds(), "New Character");
        if (_selectedPlayer is not null)
            DrawMenuButton(ContinueCharacterButtonBounds(), "Use Character");
        DrawMenuButton(BackButtonBounds(), "Back");
    }

    private void RenderNewWorldMenu()
    {
        var panel = FrontendPanel(760, 640);
        DrawAoEPanelBorder(panel);
        DrawCenteredUiText(
            "CREATE NEW WORLD", new(panel.X, panel.Y + 16, panel.Z, 38),
            new(232, 217, 166, 255));
        DrawCenteredUiText(
            "Configure your world and review the adventurer entering it",
            new(panel.X + 40, panel.Y + 52, panel.Z - 80, 24),
            new(169, 159, 130, 255));

        var details = NewWorldDetailsBounds();
        DrawUiColor(details, new(.038f, .036f, .030f, .82f));
        DrawPanelOutline(details, 0, new(.28f, .23f, .14f, 1));
        DrawPanelOutline(details, 1, new(.10f, .085f, .055f, 1));
        DrawUiText(
            "WORLD SETTINGS",
            new(details.X + 18, details.Y + 16),
            new(218, 202, 158, 255));
        DrawUiColor(
            new(details.X + 18, details.Y + 45, details.Z - 36, 1),
            new(.25f, .20f, .11f, 1));

        var labels = new[] { "World Name", "Seed" };
        var controls = new[] { _worldNameTextBox, _seedTextBox };
        for (var index = 0; index < labels.Length; index++)
        {
            var bounds = NewWorldFieldBounds(index);
            controls[index].Bounds = bounds;
            DrawUiText(labels[index], new(bounds.X, bounds.Y - 22),
                new(204, 190, 150, 255));
            DrawTextField(controls[index]);
        }
        DrawMenuButton(RandomSeedButtonBounds(), "Random");

        var character = NewWorldCharacterBounds();
        DrawUiColor(character, new(.025f, .027f, .024f, .72f));
        DrawPanelOutline(character, 0, new(.28f, .23f, .14f, 1));
        DrawPanelOutline(character, 1, new(.10f, .085f, .055f, 1));
        DrawCenteredUiText(
            "PLAYING CHARACTER",
            new(character.X, character.Y + 10, character.Z, 24),
            new(204, 190, 150, 255));
        if (_selectedPlayer is not null)
        {
            DrawCharacterPreview(
                _selectedPlayer.Gender,
                _selectedPlayer.TeamColor,
                new(
                    character.X + 16,
                    character.Y + 48,
                    character.Z - 32,
                    character.W - 104));
            DrawCenteredUiText(
                _selectedPlayer.Name,
                new(
                    character.X + 14,
                    character.Y + character.W - 48,
                    character.Z - 28,
                    28),
                new(232, 217, 166, 255));
        }
        else
            DrawCenteredUiText(
                "No character selected",
                new(
                    character.X + 14,
                    character.Y + character.W * .5f - 14,
                    character.Z - 28,
                    28),
                new(220, 104, 82, 255));
        DrawUiText(
            "Spawn Preview",
            new(NewWorldPreviewBounds().X, NewWorldPreviewBounds().Y - 22),
            new(204, 190, 150, 255));
        if (_newWorldPreviewFrame is not null &&
            _newWorldPreviewTexture != 0)
        {
            DrawUiSprite(
                _newWorldPreviewFrame,
                _newWorldPreviewTexture,
                NewWorldPreviewBounds());
            DrawPanelOutline(
                NewWorldPreviewBounds(), 0,
                new(.28f, .23f, .14f, 1));
        }

        if (_frontendError is not null)
            DrawCenteredUiText(
                _frontendError,
                new(panel.X + 40, panel.Y + panel.W - 132,
                    panel.Z - 80, 24),
                new(220, 104, 82, 255));
        DrawUiColor(
            new(panel.X + 36, panel.Y + panel.W - 108,
                panel.Z - 72, 1),
            new(.25f, .20f, .11f, 1));
        DrawMenuButton(CreateWorldButtonBounds(), "Create World");
        DrawMenuButton(BackButtonBounds(), "Back");
    }

    private void RenderLoadWorldMenu()
    {
        var panel = FrontendPanel(600, 560);
        DrawAoEPanelBorder(panel);
        DrawCenteredUiText(
            "SELECT WORLD", new(panel.X, panel.Y + 22, panel.Z, 38),
            new(232, 217, 166, 255));
        var worlds = _saves.ListWorlds().ToArray();
        LayoutWorldList(worlds);
        if (worlds.Length == 0)
        {
            DrawCenteredUiText(
                "No worlds have been created yet.",
                new(panel.X + 30, panel.Y + 150, panel.Z - 60, 30),
                new(204, 190, 150, 255));
        }
        foreach (var index in _worldList.VisibleIndices)
        {
            var world = worlds[index];
            var row = _worldList.RowBounds(index);
            DrawMenuButton(row, world.Name);
            DrawUiText(
                $"Seed {world.Seed}",
                new(row.X + 16, row.Y + row.W - 17),
                new(158, 148, 120, 255));
            DrawMenuButton(
                _worldList.DeleteBounds(index),
                _worldList.IsDeletePending(world.Id)
                    ? "Confirm?"
                    : "Delete");
        }
        RenderListScrollbar(_worldList);
        DrawMenuButton(BackButtonBounds(), "Back");
    }

    private void RenderSettingsMenu()
    {
        var panel = SettingsPanel();
        DrawAoEPanelBorder(panel);
        DrawCenteredUiText(
            "SETTINGS", new(panel.X, panel.Y + 24, panel.Z, 38),
            new(232, 217, 166, 255));
        RenderSettingsTabs(panel);
        RenderSelectedSettingsTab(panel);
        DrawMenuButton(
            SettingsMenuState.BackButtonBounds(panel), "Back");
    }

    private void DrawTextField(TextBoxControlState control)
    {
        var bounds = control.Bounds;
        var value = control.Text;
        var focused = control.Focused;
        DrawAoEPanelBorder(bounds);
        if (focused)
            DrawPanelOutline(bounds, 3, new(.46f, .37f, .18f, 1));
        var position = VerticallyCenteredTextPosition(value, bounds, 14);
        if (focused && control.HasSelection)
        {
            var startWidth = _chatFont?
                .MeasureString(value[..control.SelectionStart]).X ?? 0;
            var endWidth = _chatFont?
                .MeasureString(value[..control.SelectionEnd]).X ?? startWidth;
            DrawUiColor(
                new(
                    MathF.Round(position.X + startWidth),
                    MathF.Round(position.Y),
                    Math.Max(1, MathF.Round(endWidth - startWidth)),
                    _chatLineHeight),
                new(.24f, .36f, .50f, .78f));
        }
        DrawUiText(value, position, new(226, 214, 175, 255));
        if (focused && (int)(_clock * 2) % 2 == 0)
        {
            var caret = Math.Clamp(control.Caret, 0, value.Length);
            var width = _chatFont?.MeasureString(value[..caret]).X ?? 0;
            DrawUiColor(
                new(
                    MathF.Round(position.X + width),
                    MathF.Round(position.Y), 1, _chatLineHeight),
                new(.78f, .72f, .54f, 1));
        }
    }

    private void DrawMenuButton(
        Vector4 bounds, string caption, Vector3? tint = null)
    {
        var hovered = bounds.Contains(MouseState.Position);
        DrawUiColor(
            new(bounds.X + 2, bounds.Y + 2, bounds.Z - 4, bounds.W - 4),
            new(.075f, .068f, .052f, .96f));
        DrawPanelOutline(bounds, 0, new(.035f, .032f, .026f, 1));
        DrawPanelOutline(
            bounds, 1,
            hovered ? new(.52f, .42f, .22f, 1) : new(.25f, .21f, .13f, 1));
        DrawPanelOutline(bounds, 2, new(.075f, .07f, .058f, 1));
        if (tint is not null)
            DrawUiColor(
                new(bounds.X + 5, bounds.Y + 5, 8, bounds.W - 10),
                new(tint.Value.X, tint.Value.Y, tint.Value.Z, 1));
        DrawCenteredUiText(
            caption, bounds,
            hovered
                ? new(255, 239, 184, 255)
                : new(224, 211, 170, 255));
    }

    private void RenderListScrollbar(ListControlState list)
    {
        if (!list.ScrollTrack.Visible) return;
        DrawUiColor(
            list.ScrollTrack.Bounds,
            new(.035f, .032f, .027f, .95f));
        DrawUiColor(
            list.ScrollThumb.Bounds,
            list.ScrollThumb.Pressed ||
            list.ScrollThumb.Hovered
                ? new(.34f, .30f, .20f, 1)
                : new(.22f, .20f, .15f, 1));
        DrawPanelOutline(
            list.ScrollTrack.Bounds,
            0,
            new(.22f, .19f, .12f, 1));
    }

    private void DrawCenteredUiText(
        string text, Vector4 bounds, FSColor color) =>
        DrawUiText(text, CenteredTextPosition(text, bounds), color);

    private Vector4 FrontendPanel(float width, float height) =>
        new(
            MathF.Round((ClientSize.X - width) * .5f),
            MathF.Round((ClientSize.Y - height) * .5f),
            width, height);

    private Vector4 SettingsPanel() =>
        FrontendPanel(
            560,
            _settingsMenu.DeveloperModeEnabled ? 620 : 500);

    private Vector4 FrontendCloseButtonBounds()
    {
        var panel = _frontendPage switch
        {
            FrontendPage.CharacterCreate => FrontendPanel(760, 640),
            FrontendPage.CharacterSelect => FrontendPanel(660, 600),
            FrontendPage.NewWorld => FrontendPanel(760, 640),
            FrontendPage.LoadWorld => FrontendPanel(600, 560),
            FrontendPage.Settings => SettingsPanel(),
            _ => FrontendPanel(400, 470)
        };
        return new(panel.X + panel.Z - 40, panel.Y + 10, 28, 28);
    }

    private Vector4 MenuButton(int index)
    {
        var panel = FrontendPanel(400, 470);
        return new(panel.X + 48, panel.Y + 112 + index * 62, panel.Z - 96, 48);
    }

    private Vector4 NewWorldFieldBounds(int index)
    {
        var details = NewWorldDetailsBounds();
        const float groupWidth = 360;
        const float gap = 12;
        const float nameWidth = 150;
        var left = MathF.Round(
            details.X + (details.Z - groupWidth) * .5f);
        return index == 0
            ? new(left, details.Y + 82, nameWidth, 42)
            : new(
                left + nameWidth + gap,
                details.Y + 82,
                groupWidth - nameWidth - gap,
                42);
    }

    private Vector4 RandomSeedButtonBounds()
    {
        var seed = NewWorldFieldBounds(1);
        return new(
            seed.X + seed.Z - 58,
            seed.Y - 27,
            58,
            20);
    }

    private Vector4 NewWorldPreviewBounds()
    {
        var details = NewWorldDetailsBounds();
        const float previewSize = 270;
        return new(
            MathF.Round(
                details.X + (details.Z - previewSize) * .5f),
            details.Y + 150,
            previewSize,
            previewSize);
    }

    private Vector4 NewWorldOptionBounds(int index)
    {
        var panel = FrontendPanel(760, 640);
        const float gap = 8;
        var width = (panel.Z - 96 - gap * 2) / 3;
        return new(
            panel.X + 48 + index * (width + gap),
            panel.Y + 362, width, 46);
    }

    private Vector4 CreateWorldButtonBounds()
    {
        var panel = FrontendPanel(760, 640);
        return new(
            panel.X + 340,
            panel.Y + panel.W - 92,
            228,
            48);
    }

    private Vector4 NewWorldDetailsBounds()
    {
        var panel = FrontendPanel(760, 640);
        return new(panel.X + 40, panel.Y + 84, 408, 438);
    }

    private Vector4 NewWorldCharacterBounds()
    {
        var panel = FrontendPanel(760, 640);
        return new(panel.X + 470, panel.Y + 84, 250, 438);
    }

    private Vector4 BackButtonBounds()
    {
        var panel = _frontendPage switch
        {
            FrontendPage.CharacterCreate => FrontendPanel(760, 640),
            FrontendPage.CharacterSelect => FrontendPanel(660, 600),
            FrontendPage.NewWorld => FrontendPanel(760, 640),
            FrontendPage.LoadWorld => FrontendPanel(600, 560),
            FrontendPage.Settings => SettingsPanel(),
            _ => FrontendPanel(480, 360)
        };
        return new(panel.X + panel.Z - 156, panel.Y + panel.W - 92, 108, 48);
    }

    private void LayoutWorldList(IReadOnlyList<WorldProfile> worlds)
    {
        var panel = FrontendPanel(600, 560);
        _worldList.Layout(
            new(panel.X + 48, panel.Y + 88, panel.Z - 96, 364),
            worlds.Select(world => world.Id).ToArray(),
            rowHeight: 54,
            rowGap: 8);
    }

    private Vector4 CharacterPreviewBounds()
    {
        var panel = FrontendPanel(760, 640);
        return new(panel.X + 40, panel.Y + 84, 278, 438);
    }

    private Vector4 CharacterFormBounds()
    {
        var panel = FrontendPanel(760, 640);
        return new(panel.X + 340, panel.Y + 84, 380, 438);
    }

    private Vector4 CharacterNameBounds()
    {
        var form = CharacterFormBounds();
        return new(form.X + 22, form.Y + 92, form.Z - 44, 46);
    }

    private Vector4 GenderButtonBounds()
    {
        var form = CharacterFormBounds();
        return new(form.X + 22, form.Y + 190, form.Z - 44, 46);
    }

    private Vector4 TeamSwatchBounds(int index)
    {
        var form = CharacterFormBounds();
        return new(
            form.X + 22 + index % 4 * 68,
            form.Y + 296 + index / 4 * 58,
            48, 48);
    }

    private Vector4 CreateCharacterButtonBounds()
    {
        var panel = FrontendPanel(760, 640);
        return new(
            panel.X + 340,
            panel.Y + panel.W - 92,
            228,
            48);
    }

    private void LayoutCharacterList(
        IReadOnlyList<PlayerProfile> players)
    {
        var panel = FrontendPanel(660, 600);
        _characterList.SelectedId = _selectedPlayer?.Id;
        _characterList.Layout(
            new(panel.X + 48, panel.Y + 82, panel.Z - 96, 384),
            players.Select(player => player.Id).ToArray(),
            rowHeight: 54,
            rowGap: 12);
    }

    private Vector4 NewCharacterButtonBounds()
    {
        var panel = FrontendPanel(660, 600);
        return new(panel.X + 48, panel.Y + panel.W - 92, 176, 48);
    }

    private Vector4 ContinueCharacterButtonBounds()
    {
        var panel = FrontendPanel(660, 600);
        return new(panel.X + 238, panel.Y + panel.W - 92, 190, 48);
    }

    private void DrawColorSwatch(
        Vector4 bounds, Vector3 color, bool selected)
    {
        DrawUiColor(
            new(bounds.X + 4, bounds.Y + 4, bounds.Z - 8, bounds.W - 8),
            new(color.X, color.Y, color.Z, 1));
        DrawPanelOutline(bounds, 0, new(.035f, .032f, .026f, 1));
        DrawPanelOutline(
            bounds, selected ? 2 : 1,
            selected
                ? new(.76f, .59f, .25f, 1)
                : new(.25f, .21f, .13f, 1));
    }

    private void DrawCharacterPreview(
        EntityGender gender, int teamColor, Vector4 bounds)
    {
        if (!_entityAnimations.TryGetValue(
                (gender, EntityAction.Idle), out var animation))
            return;
        const int authoredAngles = 5;
        var framesPerAngle = Math.Max(
            1,
            animation.Graphic.Sprite.Frames.Count / authoredAngles);
        var frameIndex = VillagerDirectionRig.NeutralIdleFrame(
            framesPerAngle);
        var frame = animation.Graphic.Sprite.Frames[frameIndex];
        var texture = animation.Textures[frameIndex];
        var scale = Math.Min(
            (bounds.Z - 36) / frame.Width,
            (bounds.W - 46) / frame.Height);
        var width = frame.Width * scale;
        var height = frame.Height * scale;
        var target = new Vector4(
            MathF.Round(bounds.X + (bounds.Z - width) * .5f),
            MathF.Round(bounds.Y + bounds.W - height - 18),
            MathF.Round(width),
            MathF.Round(height));
        DrawUiSprite(
            frame, texture, target,
            teamColor: teamColor);
    }

    private static Vector3 TeamColor(int index) => index switch
    {
        0 => new(.16f, .35f, .75f),
        1 => new(.72f, .13f, .11f),
        2 => new(.18f, .55f, .24f),
        3 => new(.78f, .68f, .13f),
        4 => new(.48f, .18f, .62f),
        5 => new(.18f, .67f, .69f),
        6 => new(.82f, .40f, .13f),
        _ => new(.68f, .68f, .68f)
    };

    private void SetPlayerRecolor(int teamColor)
    {
        GL.Uniform1(
            GL.GetUniformLocation(_program, "recolorPlayer"),
            teamColor == 0 ? 0 : 1);
        if (teamColor == 0) return;
        GL.Uniform3(
            GL.GetUniformLocation(_program, "playerColor"),
            TeamColor(teamColor));
    }

    private void RenderGameUi()
    {
        _uiOpacity = _pauseMenu.IsPaused ? .28f : 1f;
        var scene = SceneClientBounds();
        _gameUi.Layout(scene);
        _chatUi.Layout(scene);
        _minimapUi.Layout(scene);
        RenderTreeHealthBars(scene);
        RenderOverheadSpeech(scene);
        RenderMinimap();
        RenderChatUi();
        RenderWorldClock(scene);
        if (_gameUi.Panel.Visible)
            DrawAoEPanelBorder(_gameUi.Panel.Bounds);
        if (_gameUi.ActivePanel == GameUiPanel.Skills)
            RenderSkillsPanel();
        else if (_gameUi.ActivePanel == GameUiPanel.Inventory)
            RenderInventoryPanel();
        DrawAoEUiTile(_gameUi.SkillsButton);
        DrawAoEUiTile(_gameUi.InventoryButton);
        DrawUiButtonCaption("Skills", _gameUi.SkillsButton.Bounds);
        DrawUiButtonCaption("Bag", _gameUi.InventoryButton.Bounds);
        RenderInventoryContextMenu();
        _uiOpacity = 1;
    }

    private void RenderDayNightOverlay()
    {
        var time = WorldTime.At(_worldGameSeconds);
        var darkness = 1 - time.Daylight;
        if (darkness <= .01f) return;
        var scene = SceneClientBounds();
        DrawUiColor(
            scene,
            new Vector4(
                .018f, .035f, .10f,
                .60f * darkness * darkness));
    }

    private void RenderWorldClock(Vector4 scene)
    {
        var time = WorldTime.At(_worldGameSeconds);
        var bounds = new Vector4(
            scene.X + scene.Z * .5f - 88,
            scene.Y + 10, 176, 29);
        DrawUiColor(bounds, new(.025f, .023f, .019f, .82f));
        DrawPanelOutline(bounds, 1, new(.34f, .27f, .14f, .92f));
        DrawCenteredUiText(
            $"Day {time.Day}  {time.Hour:00}:{time.Minute:00}",
            bounds, new(232, 219, 177, 255));
    }

    private void ShowOverheadSpeech(string message)
    {
        _overheadSpeech = message;
        _overheadSpeechExpiresAt = _clock + 5;
    }

    private void ReportBlockedAction(string action, string message)
    {
        _chatUi.AddMessage(message, ChatMessageStyle.Warning);
        var thought = _repeatedActions.RecordFailure(action, _clock);
        if (thought is null) return;
        _chatUi.AddMessage(thought, ChatMessageStyle.Monologue);
        ShowOverheadSpeech(thought);
    }

    private void RenderOverheadSpeech(Vector4 scene)
    {
        if (_player is null ||
            string.IsNullOrWhiteSpace(_overheadSpeech) ||
            _clock >= _overheadSpeechExpiresAt ||
            _chatFont is null)
        {
            if (_clock >= _overheadSpeechExpiresAt)
                _overheadSpeech = null;
            return;
        }

        var player = GetPlayerVisual();
        if (player is null) return;
        var sprite = SpriteBounds(
            player.Frame, player.World, player.Mirror);
        var scale = scene.Z / ReferenceWidth;
        var centerX = scene.X +
                      (sprite.Left + sprite.Right) * .5f * scale;
        const float horizontalPadding = 9;
        const float verticalPadding = 6;
        var speech = _overheadSpeech;
        var maximumTextWidth = Math.Max(
            40, scene.Z - horizontalPadding * 2 - 12);
        while (speech.Length > 1 &&
               _chatFont.MeasureString(speech).X > maximumTextWidth)
            speech = speech[..^1];
        if (speech.Length < _overheadSpeech.Length)
            speech = speech.TrimEnd() + "…";
        var size = _chatFont.MeasureString(speech);
        var bubbleWidth = size.X + horizontalPadding * 2;
        var bubbleHeight = size.Y + verticalPadding * 2;
        var bubbleX = Math.Clamp(
            centerX - bubbleWidth * .5f,
            scene.X + 4,
            scene.X + scene.Z - bubbleWidth - 4);
        var bubbleY = Math.Max(
            scene.Y + 4,
            scene.Y + sprite.Top * scale - bubbleHeight - 12);
        var bubble = new Vector4(
            MathF.Round(bubbleX), MathF.Round(bubbleY),
            MathF.Ceiling(bubbleWidth), MathF.Ceiling(bubbleHeight));
        DrawRoundedUiColor(bubble, 6, new(.68f, .68f, .66f, .9f));
        DrawRoundedUiColor(
            new(bubble.X + 1, bubble.Y + 1,
                bubble.Z - 2, bubble.W - 2),
            5, new(.98f, .98f, .97f, .98f));

        var tailCenter = Math.Clamp(
            centerX, bubble.X + 10, bubble.X + bubble.Z - 10);
        DrawUiColor(
            new(MathF.Round(tailCenter - 4), bubble.Y + bubble.W - 1, 8, 3),
            new(.68f, .68f, .66f, .9f));
        DrawUiColor(
            new(MathF.Round(tailCenter - 3), bubble.Y + bubble.W - 1, 6, 3),
            new(.98f, .98f, .97f, .98f));
        DrawUiColor(
            new(MathF.Round(tailCenter - 3), bubble.Y + bubble.W + 2, 6, 3),
            new(.68f, .68f, .66f, .9f));
        DrawUiColor(
            new(MathF.Round(tailCenter - 2), bubble.Y + bubble.W + 2, 4, 3),
            new(.98f, .98f, .97f, .98f));
        DrawUiColor(
            new(MathF.Round(tailCenter - 1), bubble.Y + bubble.W + 5, 2, 2),
            new(.68f, .68f, .66f, .9f));

        if (_fontRenderer is null) return;
        var position = new System.Numerics.Vector2(
            bubble.X + horizontalPadding,
            bubble.Y + verticalPadding);
        _chatFont.DrawText(
            _fontRenderer, speech, position,
            new FSColor(20, 20, 18, 255));
    }

    private void RenderInventoryContextMenu()
    {
        RenderContextMenu(
            _inventoryContext,
            _inventoryContext.Items.Count == 3 ? 1 : -1);
        RenderContextMenu(_treeContext);
        RenderContextMenu(_groundObjectContext);
        RenderContextMenu(_fishContext);
        RenderContextMenu(_vegetationContext);
    }

    private void RenderContextMenu(
        ContextMenuControlState context, int dangerIndex = -1)
    {
        if (!context.Visible) return;
        var shadow = context.Bounds;
        shadow.X += 3;
        shadow.Y += 3;
        DrawUiColor(shadow, new(0, 0, 0, .55f));
        DrawUiColor(
            context.Bounds,
            new(.045f, .040f, .031f, .99f));
        DrawPanelOutline(
            context.Bounds, 0, new(.018f, .016f, .013f, 1));
        DrawPanelOutline(
            context.Bounds, 1, new(.40f, .31f, .15f, 1));
        DrawPanelOutline(
            context.Bounds, 2, new(.10f, .085f, .055f, 1));

        var header = new Vector4(
            context.Bounds.X + 3,
            context.Bounds.Y + 3,
            context.Bounds.Z - 6,
            ContextMenuControlState.HeaderHeight - 3);
        DrawUiColor(header, new(.075f, .064f, .043f, 1));
        DrawUiText(
            "Choose option",
            VerticallyCenteredTextPosition("Choose option", header, 7),
            new(224, 209, 165, 255));
        DrawUiColor(
            new(header.X, header.Y + header.W - 1, header.Z, 1),
            new(.35f, .27f, .13f, 1));

        for (var index = 0; index < context.Items.Count; index++)
        {
            var bounds = context.ItemBounds(index);
            if (context.HoveredIndex == index)
                DrawUiColor(bounds, new(.27f, .21f, .105f, .92f));
            DrawUiText(
                context.Items[index],
                VerticallyCenteredTextPosition(
                    context.Items[index], bounds, 9),
                index == dangerIndex
                    ? new(224, 151, 124, 255)
                    : new(224, 213, 175, 255));
            if (index + 1 < context.Items.Count)
                DrawUiColor(
                    new(bounds.X + 5, bounds.Y + bounds.W - 1,
                        bounds.Z - 10, 1),
                    new(.12f, .10f, .065f, 1));
        }
    }

    private void HandleTreeContextSelection(int option)
    {
        var tree = _treeContextTarget;
        _treeContextTarget = null;
        if (tree is null) return;
        switch (option)
        {
            case 0:
                QueueTreeAction(tree, WorldActionType.CutTree);
                break;
            case 1:
                QueueTreeAction(tree, WorldActionType.GatherTreeSticks);
                break;
            case 2:
                QueueWalk(_treeContextWalkTarget);
                break;
            case 3:
                _chatUi.AddMessage(
                    $"A {TreeDisplayName(tree.GraphicName).ToLowerInvariant()}.",
                    ChatMessageStyle.Normal);
                break;
        }
    }

    private void HandleGroundObjectContextSelection(int option)
    {
        var groundObject = _groundObjectContextTarget;
        _groundObjectContextTarget = null;
        if (groundObject is null) return;
        if (PlaceableObjectCatalog.IsPlaceable(
                groundObject.ItemId))
        {
            if (CampfireService.IsCampfire(groundObject) &&
                CampfireService.State(
                    groundObject, _worldGameSeconds) ==
                CampfireState.Fueled)
            {
                if (option == 0)
                    QueueCampfireLight(groundObject);
                else if (option == 1)
                    QueueCampfireFuelPickup(groundObject);
                else if (option == 2)
                    QueueWalk(_groundObjectContextWalkTarget);
                else if (option == 3)
                    ExamineCampfire(groundObject);
                return;
            }
            if (CampfireService.IsCampfire(groundObject) &&
                CampfireService.State(
                    groundObject, _worldGameSeconds) ==
                CampfireState.Lit &&
                TrySelectedRawCookingItem(
                    out var cookingSlot,
                    out var cookingItemId))
            {
                if (option == 0)
                    QueueCampfireCooking(
                        groundObject,
                        cookingSlot,
                        cookingItemId);
                else if (option == 1)
                    QueueWalk(_groundObjectContextWalkTarget);
                else if (option == 2)
                    ExamineCampfire(groundObject);
                return;
            }
            if (option == 0)
                QueueWalk(_groundObjectContextWalkTarget);
            else if (option == 1)
            {
                if (CampfireService.IsCampfire(groundObject))
                    ExamineCampfire(groundObject);
                else
                    _chatUi.AddMessage(
                        ItemCatalog.Get(groundObject.ItemId).Examine,
                        ChatMessageStyle.Normal);
            }
            return;
        }
        switch (option)
        {
            case 0:
                QueueGroundObjectPickup(groundObject);
                break;
            case 1:
                QueueWalk(_groundObjectContextWalkTarget);
                break;
            case 2:
                _chatUi.AddMessage(
                    ItemCatalog.Get(groundObject.ItemId).Examine,
                    ChatMessageStyle.Normal);
                break;
        }
    }

    private void TryPlantSeed(int slot, string itemId)
    {
        if (_activePlayer is null || _player is null) return;
        var treeType = SeedTreeType(itemId);
        if (treeType is null) return;

        var originX = (int)MathF.Floor(_player.Position.X);
        var originY = (int)MathF.Floor(_player.Position.Y);
        (int X, int Y, GpuWorldChunk Gpu)? planting = null;
        (int X, int Y)[] offsets =
        [
            (0, 1), (1, 0), (0, -1), (-1, 0),
            (1, 1), (1, -1), (-1, 1), (-1, -1)
        ];
        foreach (var (offsetX, offsetY) in offsets)
        {
            var x = originX + offsetX;
            var y = originY + offsetY;
            var coordinate = new ChunkCoordinate(
                FloorDiv(x, WorldChunk.Size),
                FloorDiv(y, WorldChunk.Size));
            if (!_worldChunks.TryGetValue(coordinate, out var gpu))
                continue;
            var tile = gpu.Chunk.Tiles.FirstOrDefault(
                candidate => candidate.X == x && candidate.Y == y);
            if (tile is null ||
                tile.Biome is Biome.DeepWater or Biome.ShallowWater or
                    Biome.RiverWater or Biome.MangroveShallows ||
                gpu.Chunk.Trees.Any(tree => tree.X == x && tree.Y == y) ||
                gpu.Chunk.GroundObjects.Any(item =>
                    (int)MathF.Floor(item.X) == x &&
                    (int)MathF.Floor(item.Y) == y))
                continue;
            planting = (x, y, gpu);
            break;
        }
        if (planting is null)
        {
            ReportBlockedAction(
                "seed-no-planting-space",
                "There is no clear patch of land nearby for that seed.");
            return;
        }

        var inventory = PlayerInventory.Normalize(_activePlayer.Inventory);
        if ((uint)slot >= (uint)inventory.Length ||
            inventory[slot] != itemId)
            return;
        inventory[slot] = null;
        var (plantX, plantY, targetGpu) = planting.Value;
        var frameIndex = WorldTreeCatalog.SelectFrame(
            _worldSeed, plantX, plantY, treeType);
        targetGpu.Chunk.Trees =
        [
            .. targetGpu.Chunk.Trees,
            new IslandTree(plantX, plantY, treeType, frameIndex)
        ];
        var maximumHealth = TreeMaximumHealth(treeType);
        targetGpu.Chunk.TreeInstances.Add(new(
            Guid.NewGuid(), plantX, plantY, treeType,
            maximumHealth, maximumHealth,
            TreeLifecycleState.Standing, 0, 0));
        QueueChunkSave(targetGpu.Chunk);
        _activePlayer = _activePlayer with
        {
            Inventory = inventory,
            UpdatedUtc = DateTime.UtcNow
        };
        AwardFarmingExperience(FarmingSkill.PlantingExperience);
        _chatUi.AddMessage(
            $"You plant the {ItemCatalog.Get(itemId).Name}.",
            ChatMessageStyle.Action);
    }

    private void AwardFarmingExperience(int amount)
    {
        if (_activePlayer is null || amount <= 0) return;
        var award = FarmingSkill.AwardExperience(
            _activePlayer.FarmingExperience, amount);
        _activePlayer = _activePlayer with
        {
            FarmingExperience = award.Experience,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        _chatUi.AddMessage(
            $"+{award.Gained} Farming XP.",
            ChatMessageStyle.Experience);
        if (award.LevelledUp)
            _chatUi.AddMessage(
                $"Your Farming level is now {award.Level}.",
                ChatMessageStyle.LevelUp);
    }

    internal static string? SeedTreeType(string itemId) => itemId switch
    {
        ItemIds.TreeSeeds => "TREEA_NN",
        ItemIds.PalmSeeds => "FPAL_NN",
        ItemIds.PineSeeds => "FPIN_NN",
        ItemIds.OakSeeds => "FOAK_NN",
        ItemIds.JungleTreeSeeds => "FJUN_NN",
        ItemIds.SnowTreeSeeds => "FSNO_NN",
        ItemIds.BambooSeeds => "FBAM_NN",
        ItemIds.CactusSeeds => "FCAC_NN",
        _ => null
    };

    private void DrawPanelCaption(string caption, Vector4 panel)
    {
        var header = new Vector4(
            panel.X + 8, panel.Y + 8, panel.Z - 16, 30);
        DrawUiColor(header, new(.055f, .050f, .040f, .82f));
        DrawPanelOutline(header, 0, new(.035f, .032f, .026f, 1));
        DrawPanelOutline(header, 1, new(.24f, .205f, .13f, 1));
        DrawUiButtonCaption(caption, header);
    }

    private static string InventoryItemCaption(string itemId) =>
        ItemCatalog.Get(itemId).Caption;

    private static Vector4? InventoryItemUv(string itemId)
    {
        var item = ItemCatalog.Get(itemId);
        var cell = item.SpriteCell;
        if (item.HasTag(ItemTag.NaturalMaterial) ||
            item.HasTag(ItemTag.SupplementalSprite) ||
            item.HasTag(ItemTag.StoneToolSprite) ||
            item.HasTag(ItemTag.Fish) ||
            item.HasTag(ItemTag.FibreNetSprite) ||
            item.HasTag(ItemTag.PlaceableObject))
            return cell is null ? null : new Vector4(0, 0, 1, 1);
        return cell is null
            ? null
            : new Vector4(
                (cell.Value % 4) * .25f,
                (cell.Value / 4) * .5f,
                .25f, .5f);
    }

    private int InventoryItemTexture(string itemId)
    {
        var item = ItemCatalog.Get(itemId);
        if (item.HasTag(ItemTag.StoneToolSprite))
            return item.SpriteCell is { } stoneCell &&
                   (uint)stoneCell < (uint)_stoneToolTextures.Length
                ? _stoneToolTextures[stoneCell]
                : 0;
        if (item.HasTag(ItemTag.CoastalSprite))
            return item.SpriteCell is { } coastalCell &&
                   (uint)coastalCell <
                   (uint)_coastalSprites.Textures.Length
                ? _coastalSprites.Textures[coastalCell]
                : 0;
        if (item.HasTag(ItemTag.Fish))
            return item.SpriteCell is { } fishCell &&
                   (uint)fishCell < (uint)_fishItemTextures.Length
                ? _fishItemTextures[fishCell]
                : 0;
        if (item.HasTag(ItemTag.FibreNetSprite))
            return item.SpriteCell is { } fibreCell &&
                   (uint)fibreCell <
                   (uint)_fibreNetSprites.Textures.Length
                ? _fibreNetSprites.Textures[fibreCell]
                : 0;
        if (item.HasTag(ItemTag.PlaceableObject))
            return _placeableObjectSprites.TryGet(
                item.Id, out var placeable)
                ? placeable.Texture
                : 0;
        if (item.HasTag(ItemTag.SupplementalSprite))
            return item.SpriteCell is { } supplementalCell &&
                   (uint)supplementalCell <
                   (uint)_supplementalItemTextures.Length
                ? _supplementalItemTextures[supplementalCell]
                : 0;
        if (!item.HasTag(ItemTag.NaturalMaterial))
            return _woodcuttingItemsTexture;
        return item.SpriteCell is { } cell &&
               (uint)cell < (uint)_naturalItemTextures.Length
            ? _naturalItemTextures[cell]
            : 0;
    }

    private SpriteFrame InventoryItemFrame(string itemId)
    {
        var item = ItemCatalog.Get(itemId);
        if (item.SpriteCell is not { } cell)
            return WoodcuttingItemsFrame;
        if (item.HasTag(ItemTag.StoneToolSprite) &&
            (uint)cell < (uint)_stoneToolFrames.Length)
            return _stoneToolFrames[cell] ?? WoodcuttingItemsFrame;
        if (item.HasTag(ItemTag.CoastalSprite) &&
            (uint)cell < (uint)_coastalSprites.Frames.Length)
            return _coastalSprites.Frames[cell] ?? WoodcuttingItemsFrame;
        if (item.HasTag(ItemTag.Fish) &&
            (uint)cell < (uint)_fishItemFrames.Length)
            return _fishItemFrames[cell] ?? WoodcuttingItemsFrame;
        if (item.HasTag(ItemTag.FibreNetSprite) &&
            (uint)cell < (uint)_fibreNetSprites.Frames.Length)
            return _fibreNetSprites.Frames[cell] ??
                   WoodcuttingItemsFrame;
        if (item.HasTag(ItemTag.PlaceableObject) &&
            _placeableObjectSprites.TryGet(
                item.Id, out var placeable))
            return placeable.Frame;
        if (item.HasTag(ItemTag.SupplementalSprite) &&
            (uint)cell < (uint)_supplementalItemFrames.Length)
            return _supplementalItemFrames[cell] ?? WoodcuttingItemsFrame;
        if (item.HasTag(ItemTag.NaturalMaterial) &&
            (uint)cell < (uint)_naturalItemFrames.Length)
            return _naturalItemFrames[cell] ?? WoodcuttingItemsFrame;
        return WoodcuttingItemsFrame;
    }

    private SpriteFrame InventoryItemPixelFrame(string itemId)
    {
        var item = ItemCatalog.Get(itemId);
        if (item.SpriteCell is not { } cell ||
            item.HasTag(ItemTag.StoneToolSprite) ||
            item.HasTag(ItemTag.SupplementalSprite) ||
            item.HasTag(ItemTag.NaturalMaterial) ||
            item.HasTag(ItemTag.Fish) ||
            item.HasTag(ItemTag.FibreNetSprite) ||
            item.HasTag(ItemTag.PlaceableObject))
            return InventoryItemFrame(itemId);
        return (uint)cell < (uint)_woodcuttingInventoryFrames.Length
            ? _woodcuttingInventoryFrames[cell] ?? WoodcuttingItemsFrame
            : WoodcuttingItemsFrame;
    }

    private static float InventoryItemBrightness(string itemId) =>
        ItemCatalog.Get(itemId).HasTag(ItemTag.BurntFish) ? -.48f : 0f;

    private static float InventoryItemGrayscale(string itemId) =>
        ItemCatalog.Get(itemId).HasTag(ItemTag.BurntFish) ? .92f : 0f;

    private void RenderTreeHealthBars(Vector4 scene)
    {
        var scale = scene.Z / ReferenceWidth;
        foreach (var gpu in _worldChunks.Values.Where(IsChunkVisible))
        foreach (var instance in gpu.Chunk.TreeInstances)
        {
            if (instance.State != TreeLifecycleState.Standing ||
                instance.Health >= instance.MaxHealth &&
                instance.Id != _activeTreeId)
                continue;
            var sourceTree = gpu.Chunk.Trees.FirstOrDefault(tree =>
                tree.X == instance.X && tree.Y == instance.Y);
            var atlasKey = sourceTree is null
                ? instance.TreeType
                : WorldTreeCatalog.AtlasKey(sourceTree);
            if (!_treeAtlas.TryGetValue(atlasKey, out var entry))
                continue;
            var elevation = InfiniteWorldGenerator.SampleRenderedHeight(
                _worldSeed, instance.X + .5f, instance.Y + .5f);
            var world = new Vector2(
                (instance.X - instance.Y) * 48,
                (instance.X + instance.Y + 1) * 24 - elevation * 20);
            var bounds = SpriteBounds(entry.Frame, world);
            var top = bounds.Top - 9;
            var width = Math.Clamp(42 * _zoom, 28, 64);
            var bar = new Vector4(
                scene.X + ((bounds.Left + bounds.Right) * .5f -
                           width * .5f) * scale,
                scene.Y + top * scale,
                width * scale,
                Math.Max(5, 7 * scale));
            if (bar.X + bar.Z < scene.X || bar.X > scene.X + scene.Z ||
                bar.Y + bar.W < scene.Y || bar.Y > scene.Y + scene.W)
                continue;
            DrawUiColor(bar, new(.035f, .028f, .022f, .96f));
            var ratio = instance.Health / (float)instance.MaxHealth;
            DrawUiColor(
                new(bar.X + 2, bar.Y + 2,
                    Math.Max(0, (bar.Z - 4) * ratio),
                    Math.Max(1, bar.W - 4)),
                ratio > .5f
                    ? new(.24f, .62f, .18f, 1)
                    : ratio > .25f
                        ? new(.74f, .55f, .12f, 1)
                        : new(.70f, .14f, .09f, 1));
            DrawPanelOutline(bar, 0, new(.10f, .08f, .05f, 1));
        }
    }

    private void DrawUiButtonCaption(string caption, Vector4 bounds)
    {
        DrawUiText(
            caption,
            CenteredTextPosition(caption, bounds),
            new FSColor(229, 218, 177, 255));
    }

    private void RenderMinimap()
    {
        if (_player is null || _minimapFrame is null || _minimapTexture == 0) return;
        var center = new Vector2i(
            (int)MathF.Floor(_player.Position.X),
            (int)MathF.Floor(_player.Position.Y));
        if (_minimapBuildTask is { IsCompleted: true })
        {
            var result = _minimapBuildTask.GetAwaiter().GetResult();
            _minimapBuildTask = null;
            _minimapCenter = result.Center;
            _minimapTerrain = result.Terrain;
            GL.BindTexture(TextureTarget.Texture2D, _minimapTexture);
            GL.TexSubImage2D(
                TextureTarget.Texture2D,
                0,
                0,
                0,
                _minimapFrame.Width,
                _minimapFrame.Height,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                result.Pixels);
        }

        if (_minimapBuildTask is null && center != _minimapCenter)
        {
            var previousCenter = _minimapCenter;
            var previousTerrain = _minimapTerrain;
            _minimapBuildTask = Task.Run(() =>
                BuildMinimap(center, previousCenter, previousTerrain));
        }
        DrawUiSprite(_minimapFrame, _minimapTexture, _minimapUi.Bounds);
    }

    private MinimapBuildResult BuildMinimap(
        Vector2i center, Vector2i previousCenter, byte[]? previousTerrain)
    {
        const int terrainSize = 105;
        const int terrainRadius = terrainSize / 2;
        var terrain = new byte[terrainSize * terrainSize * 3];
        var delta = previousTerrain is null
            ? Vector2i.Zero
            : center - previousCenter;
        var canShift = previousTerrain is not null &&
                       Math.Abs(delta.X) < terrainSize &&
                       Math.Abs(delta.Y) < terrainSize;

        for (var y = 0; y < terrainSize; y++)
        for (var x = 0; x < terrainSize; x++)
        {
            var sourceX = x + delta.X;
            var sourceY = y + delta.Y;
            var targetIndex = (y * terrainSize + x) * 3;
            if (canShift &&
                sourceX >= 0 && sourceX < terrainSize &&
                sourceY >= 0 && sourceY < terrainSize)
            {
                var sourceIndex = (sourceY * terrainSize + sourceX) * 3;
                terrain[targetIndex] = previousTerrain![sourceIndex];
                terrain[targetIndex + 1] = previousTerrain[sourceIndex + 1];
                terrain[targetIndex + 2] = previousTerrain[sourceIndex + 2];
                continue;
            }

            var (red, green, blue) = ReliefMinimapColor(
                _worldSeed,
                center.X + x - terrainRadius,
                center.Y + y - terrainRadius);
            terrain[targetIndex] = red;
            terrain[targetIndex + 1] = green;
            terrain[targetIndex + 2] = blue;
        }

        return new MinimapBuildResult(
            center, terrain, ComposeMinimapPixels(terrain, terrainSize));
    }

    private static byte[] ComposeMinimapPixels(byte[] terrain, int terrainSize)
    {
        const int size = 160;
        const float radius = 79;
        const float mapRadius = 73;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var deltaX = x + .5f - size * .5f;
            var deltaY = y + .5f - size * .5f;
            var distance = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
            if (distance > radius) continue;

            byte red;
            byte green;
            byte blue;
            if (distance > 78)
            {
                (red, green, blue) = (9, 8, 7);
            }
            else if (distance > 77)
            {
                (red, green, blue) = (61, 52, 33);
            }
            else if (distance > 75)
            {
                (red, green, blue) = (19, 18, 15);
            }
            else if (distance > 74)
            {
                (red, green, blue) = (41, 38, 30);
            }
            else if (distance > mapRadius)
            {
                (red, green, blue) = (7, 7, 6);
            }
            else
            {
                var sampleX = deltaX / mapRadius * 52 + 52;
                var sampleY = deltaY / mapRadius * 52 + 52;
                (red, green, blue) = SampleMinimapTerrain(
                    terrain, terrainSize, sampleX, sampleY);
            }

            var output = (y * size + x) * 4;
            pixels[output] = red;
            pixels[output + 1] = green;
            pixels[output + 2] = blue;
            pixels[output + 3] = 255;
        }

        // Fixed north-up player arrow at the center.
        for (var y = 72; y <= 87; y++)
        for (var x = 75; x <= 84; x++)
        {
            var arrow = y <= 80
                ? Math.Abs(x - 79.5f) <= (y - 71) * .48f
                : x is >= 78 and <= 81;
            if (!arrow) continue;
            var index = (y * size + x) * 4;
            var outline = x is 75 or 84 || y is 72 or 87;
            pixels[index] = outline ? (byte)35 : (byte)205;
            pixels[index + 1] = outline ? (byte)24 : (byte)30;
            pixels[index + 2] = outline ? (byte)18 : (byte)24;
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    private static (byte Red, byte Green, byte Blue) SampleMinimapTerrain(
        byte[] terrain, int size, float x, float y)
    {
        var x0 = Math.Clamp((int)MathF.Floor(x), 0, size - 1);
        var y0 = Math.Clamp((int)MathF.Floor(y), 0, size - 1);
        var x1 = Math.Min(size - 1, x0 + 1);
        var y1 = Math.Min(size - 1, y0 + 1);
        var fractionX = x - x0;
        var fractionY = y - y0;

        byte Channel(int channel)
        {
            var top = MathHelper.Lerp(
                terrain[(y0 * size + x0) * 3 + channel],
                terrain[(y0 * size + x1) * 3 + channel],
                fractionX);
            var bottom = MathHelper.Lerp(
                terrain[(y1 * size + x0) * 3 + channel],
                terrain[(y1 * size + x1) * 3 + channel],
                fractionX);
            return (byte)Math.Clamp(
                (int)MathF.Round(MathHelper.Lerp(top, bottom, fractionY)),
                0,
                255);
        }
        return (Channel(0), Channel(1), Channel(2));
    }

    private static (byte Red, byte Green, byte Blue) MinimapColor(Biome biome) =>
        biome switch
        {
            Biome.DeepWater => (43, 77, 120),
            Biome.ShallowWater or Biome.RiverWater => (55, 104, 142),
            Biome.MangroveShallows => (68, 114, 119),
            Biome.Beach or Biome.DesertSand => (184, 165, 101),
            Biome.Grassland => (104, 139, 65),
            Biome.DryGrass => (142, 139, 72),
            Biome.Forest or Biome.JungleFloor => (48, 93, 47),
            Biome.Highland => (103, 111, 69),
            Biome.Rock or Biome.CrackedEarth => (105, 99, 83),
            Biome.Mud => (91, 75, 54),
            Biome.Tundra => (128, 137, 119),
            Biome.Snow => (205, 213, 207),
            _ => (92, 116, 69)
        };

    private static (byte Red, byte Green, byte Blue) ReliefMinimapColor(
        long seed, int x, int y)
    {
        var biome = InfiniteWorldGenerator.BiomeAt(seed, x, y);
        var (red, green, blue) = MinimapColor(biome);
        var center = InfiniteWorldGenerator.SampleSurfaceHeight(
            seed, x, y);
        var west = InfiniteWorldGenerator.SampleSurfaceHeight(
            seed, x - 1, y);
        var east = InfiniteWorldGenerator.SampleSurfaceHeight(
            seed, x + 1, y);
        var north = InfiniteWorldGenerator.SampleSurfaceHeight(
            seed, x, y - 1);
        var south = InfiniteWorldGenerator.SampleSurfaceHeight(
            seed, x, y + 1);
        var slopeX = east - west;
        var slopeY = south - north;
        var relief = MathF.Abs(slopeX) + MathF.Abs(slopeY);
        var lighting = (west - east) * .055f +
                       (north - south) * .04f;
        var elevation = biome is Biome.DeepWater or
            Biome.ShallowWater or Biome.RiverWater or
            Biome.MangroveShallows
                ? 0
                : Math.Min(center, (byte)14) * .012f;
        var contour = center >= 3 && center % 3 == 0
            ? -.08f
            : 0;
        var cliff = relief >= 5 ? -.13f : 0;
        var factor = Math.Clamp(
            1 + lighting + elevation + contour + cliff,
            .62f,
            1.28f);

        byte Shade(byte channel) => (byte)Math.Clamp(
            (int)MathF.Round(channel * factor), 0, 255);
        return (Shade(red), Shade(green), Shade(blue));
    }

    private void RenderChatUi()
    {
        DrawAoEPanelBorder(_chatUi.LogPanel.Bounds);
        DrawAoEPanelBorder(_chatUi.Input.Bounds);

        if (_uiTabFrame is not null && _uiTabTexture != 0)
        {
            var tint = _chatUi.Channel switch
            {
                ChatChannel.Combat => new Vector3(.30f, .035f, .025f),
                ChatChannel.Story => new Vector3(.06f, .11f, .28f),
                ChatChannel.Debug => new Vector3(.10f, .24f, .08f),
                _ => new Vector3(.20f, .17f, .10f)
            };
            DrawUiSprite(
                _uiTabFrame,
                _uiTabTexture,
                _chatUi.ChannelButton.Bounds,
                _chatUi.ChannelButton.Pressed
                    ? -.16f
                    : _chatUi.ChannelButton.Hovered ? .14f : 0,
                tint: tint,
                tintAmount: .42f);
        }

        DrawUiColor(_chatUi.ScrollTrack.Bounds, new(.035f, .032f, .027f, .95f));
        DrawUiColor(
            _chatUi.ScrollThumb.Bounds,
            _chatUi.ScrollThumb.Pressed || _chatUi.ScrollThumb.Hovered
                ? new(.34f, .30f, .20f, 1)
                : new(.22f, .20f, .15f, 1));
        DrawPanelOutline(
            _chatUi.ScrollTrack.Bounds, 0, new(.22f, .19f, .12f, 1));

        if (_chatFont is not null && _fontRenderer is not null)
        {
            var textLeft = _chatUi.LogPanel.Bounds.X + 9;
            var textTop = _chatUi.LogPanel.Bounds.Y + 6;
            var visible = _chatUi.Messages
                .Skip(_chatUi.FirstVisibleLine)
                .Take(8);
            var row = 0;
            foreach (var message in visible)
            {
                var color = message.Style switch
                {
                    ChatMessageStyle.Action => new FSColor(215, 202, 158, 255),
                    ChatMessageStyle.Damage => new FSColor(232, 157, 118, 255),
                    ChatMessageStyle.Miss => new FSColor(176, 179, 169, 255),
                    ChatMessageStyle.Experience => new FSColor(145, 204, 154, 255),
                    ChatMessageStyle.LevelUp => new FSColor(238, 211, 104, 255),
                    ChatMessageStyle.Reward => new FSColor(130, 224, 142, 255),
                    ChatMessageStyle.Monologue => new FSColor(196, 202, 218, 255),
                    ChatMessageStyle.Warning => new FSColor(236, 145, 112, 255),
                    _ => new FSColor(218, 207, 166, 255)
                };
                DrawUiText(
                    message.Text,
                    new(textLeft, textTop + row * _chatLineHeight),
                    color);
                row++;
            }

            DrawUiText(
                _chatUi.Channel.ToString(),
                CenteredTextPosition(
                    _chatUi.Channel.ToString(), _chatUi.ChannelButton.Bounds),
                new(229, 218, 177, 255));
            DrawUiText(
                _chatUi.InputText,
                VerticallyCenteredTextPosition(
                    _chatUi.InputText, _chatUi.Input.Bounds, 9),
                new(218, 207, 166, 255));
        }

        if (_chatUi.Input.Focused)
        {
            DrawPanelOutline(
                _chatUi.Input.Bounds, 3, new(.32f, .27f, .16f, 1));
            if ((int)(_clock * 2) % 2 == 0)
            {
                var textWidth = _chatFont?.MeasureString(_chatUi.InputText).X
                    ?? _chatUi.InputText.Length * 6;
                var caretX = Math.Min(
                    _chatUi.Input.Bounds.X + _chatUi.Input.Bounds.Z - 10,
                    _chatUi.Input.Bounds.X + 9 + textWidth);
                var caretHeight = MathF.Min(
                    18, MathF.Ceiling(_chatFont?.MeasureString("Ag").Y ?? 16));
                var caretY = MathF.Round(
                    _chatUi.Input.Bounds.Y +
                    (_chatUi.Input.Bounds.W - caretHeight) * .5f);
                DrawUiColor(
                    new(MathF.Round(caretX), caretY, 1, caretHeight),
                    new(.72f, .68f, .55f, 1));
            }
        }
    }

    private System.Numerics.Vector2 CenteredTextPosition(
        string text, Vector4 bounds)
    {
        var size = _chatFont?.MeasureString(text) ?? System.Numerics.Vector2.Zero;
        return new(
            bounds.X + (bounds.Z - size.X) * .5f,
            bounds.Y + (bounds.W - size.Y) * .5f);
    }

    private System.Numerics.Vector2 VerticallyCenteredTextPosition(
        string text, Vector4 bounds, float leftPadding)
    {
        var measuredText = string.IsNullOrEmpty(text) ? "Ag" : text;
        var size = _chatFont?.MeasureString(measuredText)
            ?? new System.Numerics.Vector2(0, _chatLineHeight);
        return new(
            bounds.X + leftPadding,
            MathF.Round(bounds.Y + (bounds.W - size.Y) * .5f));
    }

    private void DrawUiText(
        string text, System.Numerics.Vector2 position, FSColor color)
    {
        if (string.IsNullOrEmpty(text) ||
            _chatFont is null || _fontRenderer is null) return;
        _chatFont.DrawText(
            _fontRenderer, text, position + System.Numerics.Vector2.One,
            new FSColor(0, 0, 0, 190));
        _chatFont.DrawText(_fontRenderer, text, position, color);
    }

    private void DrawFontQuad(
        int texture,
        VertexPositionColorTexture topLeft,
        VertexPositionColorTexture topRight,
        VertexPositionColorTexture bottomLeft,
        VertexPositionColorTexture bottomRight)
    {
        var left = MathF.Round(topLeft.Position.X);
        var top = MathF.Round(topLeft.Position.Y);
        var right = MathF.Round(bottomRight.Position.X);
        var bottom = MathF.Round(bottomRight.Position.Y);
        var color = topLeft.Color;
        DrawUiSprite(
            SolidUiFrame,
            texture,
            new(left, top, right - left, bottom - top),
            uvRectangle: new(
                topLeft.TextureCoordinate.X,
                topLeft.TextureCoordinate.Y,
                topRight.TextureCoordinate.X - topLeft.TextureCoordinate.X,
                bottomLeft.TextureCoordinate.Y - topLeft.TextureCoordinate.Y),
            tint: new(color.R / 255f, color.G / 255f, color.B / 255f),
            tintAmount: 1,
            drawOpacity: color.A / 255f);
    }

    private void DrawAoEUiTile(TabControlState control)
    {
        if (_uiTabFrame is null || _uiTabTexture == 0) return;
        DrawUiSprite(
            _uiTabFrame,
            control.Selected ? _uiActiveTabTexture : _uiTabTexture,
            control.Bounds,
            control.Pressed ? -.16f : control.Hovered ? .14f : 0);
    }

    private void DrawAoEPanelBorder(Vector4 box)
    {
        if (_uiPanelFillTexture != 0)
            DrawUiSprite(
                SolidUiFrame,
                _uiPanelFillTexture,
                new(box.X + 5, box.Y + 5, box.Z - 10, box.W - 10));
        // Five native pixels, built in the same material language as the tabs.
        DrawPanelOutline(box, 0, new(.035f, .032f, .026f, 1));
        DrawPanelOutline(box, 1, new(.24f, .205f, .13f, 1));
        DrawPanelOutline(box, 2, new(.075f, .07f, .058f, 1));
        DrawPanelOutline(box, 3, new(.16f, .15f, .12f, 1));
        DrawPanelOutline(box, 4, new(.028f, .026f, .022f, 1));
    }

    private void DrawPanelOutline(Vector4 box, float inset, Vector4 color)
    {
        var width = box.Z - inset * 2;
        var height = box.W - inset * 2;
        if (width <= 0 || height <= 0) return;
        DrawUiColor(new(box.X + inset, box.Y + inset, width, 1), color);
        DrawUiColor(new(box.X + inset, box.Y + box.W - inset - 1, width, 1), color);
        DrawUiColor(new(box.X + inset, box.Y + inset, 1, height), color);
        DrawUiColor(new(box.X + box.Z - inset - 1, box.Y + inset, 1, height), color);
    }

    private void DrawUiColor(Vector4 rectangle, Vector4 color)
    {
        if (_uiSolidTexture == 0) return;
        DrawUiSprite(
            SolidUiFrame,
            _uiSolidTexture,
            rectangle,
            tint: new Vector3(color.X, color.Y, color.Z),
            tintAmount: 1,
            drawOpacity: color.W);
    }

    private void DrawRoundedUiColor(
        Vector4 rectangle, float radius, Vector4 color)
    {
        var height = Math.Max(1, (int)MathF.Ceiling(rectangle.W));
        var roundedRadius = Math.Clamp(
            radius, 0, MathF.Min(rectangle.Z, rectangle.W) * .5f);
        for (var row = 0; row < height; row++)
        {
            var edgeDistance = MathF.Min(
                row + .5f, height - row - .5f);
            var inset = edgeDistance >= roundedRadius ||
                        roundedRadius <= 0
                ? 0
                : roundedRadius - MathF.Sqrt(
                    Math.Max(
                        0,
                        roundedRadius * roundedRadius -
                        (roundedRadius - edgeDistance) *
                        (roundedRadius - edgeDistance)));
            DrawUiColor(
                new(
                    rectangle.X + MathF.Ceiling(inset),
                    rectangle.Y + row,
                    rectangle.Z - MathF.Ceiling(inset) * 2,
                    1),
                color);
        }
    }

    private void DrawUiSprite(
        SpriteFrame frame,
        int texture,
        Vector4 rectangle,
        float brightness = 0,
        Vector4? uvRectangle = null,
        Vector3? tint = null,
        float tintAmount = 0,
        float grayscaleAmount = 0,
        float drawOpacity = 1,
        int teamColor = 0,
        Vector3? spriteOutline = null)
    {
        var viewportWidth = Math.Max(1, ClientSize.X);
        var viewportHeight = Math.Max(1, ClientSize.Y);
        var left = (rectangle.X - viewportWidth * .5f) * 2 / viewportWidth;
        var right = (rectangle.X + rectangle.Z - viewportWidth * .5f) * 2 / viewportWidth;
        var top = -(rectangle.Y - viewportHeight * .5f) * 2 / viewportHeight;
        var bottom = -(rectangle.Y + rectangle.W - viewportHeight * .5f) * 2 / viewportHeight;
        GL.UseProgram(_program);
        GL.Uniform1(GL.GetUniformLocation(_program, "image"), 0);
        GL.Uniform1(
            GL.GetUniformLocation(_program, "opacity"),
            drawOpacity * _uiOpacity);
        GL.Uniform1(GL.GetUniformLocation(_program, "outlineOnly"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "wading"), 0);
        GL.Uniform1(
            GL.GetUniformLocation(_program, "spriteOutline"),
            spriteOutline is null ? 0 : 1);
        GL.Uniform3(
            GL.GetUniformLocation(_program, "spriteOutlineColor"),
            spriteOutline ?? Vector3.Zero);
        GL.Uniform1(GL.GetUniformLocation(_program, "brightness"), brightness);
        var tintColor = tint ?? Vector3.Zero;
        GL.Uniform3(GL.GetUniformLocation(_program, "colorTint"), tintColor);
        GL.Uniform1(GL.GetUniformLocation(_program, "tintAmount"), tintAmount);
        GL.Uniform1(
            GL.GetUniformLocation(_program, "grayscaleAmount"),
            grayscaleAmount);
        GL.Uniform2(GL.GetUniformLocation(_program, "texelSize"),
            1f / frame.Width, 1f / frame.Height);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        SetPlayerRecolor(teamColor);
        var uv = uvRectangle ?? new Vector4(0, 0, 1, 1);
        var u0 = uv.X;
        var v0 = uv.Y;
        var u1 = uv.X + uv.Z;
        var v1 = uv.Y + uv.W;
        Draw([
            left,top,u0,v0, left,bottom,u0,v1,
            right,bottom,u1,v1, right,top,u1,v0
        ]);
        GL.Uniform1(GL.GetUniformLocation(_program, "brightness"), 0f);
        GL.Uniform1(GL.GetUniformLocation(_program, "tintAmount"), 0f);
        GL.Uniform1(GL.GetUniformLocation(_program, "grayscaleAmount"), 0f);
        GL.Uniform1(GL.GetUniformLocation(_program, "recolorPlayer"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "opacity"), 1f);
        GL.Uniform1(GL.GetUniformLocation(_program, "spriteOutline"), 0);
    }

    private void RenderAtlas()
    {
        var width = ReferenceWidth;
        var height = ReferenceHeight;
        var scale = AtlasPixelsPerTile();
        foreach (var key in _visibleAtlasTiles.OrderBy(key => key.Y).ThenBy(key => key.X))
        {
            if (!_atlasTileTextures.TryGetValue(key, out var texture)) continue;
            var span = key.SpanTiles;
            var pixelLeft = width * .5f + (key.X * span - _atlasCenterIso.X) * scale;
            var pixelTop = height * .5f + (key.Y * span - _atlasCenterIso.Y) * scale;
            var pixelRight = pixelLeft + span * scale;
            var pixelBottom = pixelTop + span * scale;
            var left = (pixelLeft - width * .5f) * 2 / width;
            var right = (pixelRight - width * .5f) * 2 / width;
            var top = -(pixelTop - height * .5f) * 2 / height;
            var bottom = -(pixelBottom - height * .5f) * 2 / height;
            GL.UseProgram(_program);
            GL.Uniform1(GL.GetUniformLocation(_program, "image"), 0);
            GL.Uniform1(GL.GetUniformLocation(_program, "opacity"), 1f);
            GL.Uniform1(GL.GetUniformLocation(_program, "outlineOnly"), 0);
            GL.Uniform1(GL.GetUniformLocation(_program, "wading"), 0);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            Draw([left,top,0,0, left,bottom,0,1, right,bottom,1,1, right,top,1,0]);
        }

        GL.Enable(EnableCap.ScissorTest);
        GL.Scissor(width / 2 - 1, height / 2 - 10, 3, 21);
        GL.ClearColor(.95f, .82f, .24f, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.Scissor(width / 2 - 10, height / 2 - 1, 21, 3);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.Disable(EnableCap.ScissorTest);

        if (_atlasTileTasks.Count > 0)
        {
            const int margin = 90;
            const int barHeight = 18;
            var barWidth = Math.Max(0, ReferenceWidth - margin * 2);
            var atlasDone = Volatile.Read(ref _atlasDone);
            var atlasTotal = Volatile.Read(ref _atlasTotal);
            var filled = (int)(barWidth * Math.Clamp(
                atlasDone / (float)Math.Max(1, atlasTotal), 0, 1));
            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(margin, 32, barWidth, barHeight);
            GL.ClearColor(.12f, .14f, .12f, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.Scissor(margin, 32, filled, barHeight);
            GL.ClearColor(.35f, .68f, .28f, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.Disable(EnableCap.ScissorTest);
        }
    }

    private void RenderWorldPreview()
    {
        const int columns = 20;
        const float cellWidth = 190;
        const float cellHeight = 180;
        for (var i = 0; i < _worldAssets.Count; i++)
        {
            var world = new Vector2(
                (i % columns - (columns - 1) / 2f) * cellWidth,
                (i / columns) * cellHeight);
            var screen = new Vector2(ReferenceWidth / 2f, ReferenceHeight / 2f) +
                         _camera + world * _zoom;
            if (screen.X < -250 || screen.Y < -250 ||
                screen.X > ReferenceWidth + 250 || screen.Y > ReferenceHeight + 250)
                continue;
            DrawSprite(_worldAssets[i].Sprite.Frames[0], _textures[i], world);
        }

        const int terrainColumns = 6;
        const float terrainCell = 145;
        const float terrainStartX = 2250;
        for (var i = 0; i < _catalog!.TerrainTiles.Count; i++)
        {
            var world = new Vector2(
                terrainStartX + i % terrainColumns * terrainCell,
                i / terrainColumns * terrainCell);
            var screen = new Vector2(ReferenceWidth / 2f, ReferenceHeight / 2f) +
                         _camera + world * _zoom;
            if (screen.X < -150 || screen.Y < -150 ||
                screen.X > ReferenceWidth + 150 || screen.Y > ReferenceHeight + 150)
                continue;
            DrawTerrain(_catalog.TerrainTiles[i], _terrainTextures[i], world);
        }
    }

    private void RenderIsland()
    {
        var tiles = _island!.Tiles;
        DrawIslandTerrainBatch();
        var treesByDepth = _island.Trees.GroupBy(tree => tree.X + tree.Y)
            .ToDictionary(group => group.Key, group => group);
        var graphicIndex = _worldAssets
            .Select((asset, index) => (asset.Definition.Name, index))
            .GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);

        for (var depth = 0; depth <= (IslandMap.Size - 1) * 2; depth++)
        {
            if (!treesByDepth.TryGetValue(depth, out var trees)) continue;
            foreach (var tree in trees)
            {
                if (!graphicIndex.TryGetValue(tree.GraphicName, out var index)) continue;
                var tile = tiles[tree.Y * IslandMap.Size + tree.X];
                var height = (tile.North + tile.East + tile.South + tile.West) / 4f;
                var world = new Vector2(
                    (tree.X - tree.Y) * 48,
                    (tree.X + tree.Y + 1) * 24 - height * 12);
                var shadowName = tree.GraphicName[..^2] + "N0";
                if (graphicIndex.TryGetValue(shadowName, out var shadowIndex))
                    DrawSprite(
                        _worldAssets[shadowIndex].Sprite.Frames[0],
                        _textures[shadowIndex],
                        world);
                DrawSprite(_worldAssets[index].Sprite.Frames[0], _textures[index], world);
            }
        }
    }

    private void RenderWorld()
    {
        GL.UseProgram(_terrainProgram);
        UploadWaterRipples();
        BeginWorldTerrainBatch();
        var visibleChunks = _worldChunks.Values.Where(IsChunkVisible).ToArray();
        foreach (var gpu in visibleChunks
                     .Where(gpu => IsChunkVisibleWithPadding(gpu, 0))
                     .OrderBy(gpu => gpu.Chunk.Coordinate.X + gpu.Chunk.Coordinate.Y))
            DrawWorldChunkTerrain(gpu);
        if (_mode == PreviewMode.Game) DrawMoveMarker();

        var player = _mode == PreviewMode.Game ? GetPlayerVisual() : null;
        var playerDepth = player?.World.Y ?? float.MaxValue;
        var vegetationCapacity = visibleChunks.Sum(
            gpu => gpu.VegetationRenderItems.Length);
        _worldRenderQueue.Reset(vegetationCapacity);
        var shadows = _worldRenderQueue.Shadows;
        var objects = _worldRenderQueue.Objects;
        var playerOccluded = false;
        foreach (var item in visibleChunks
                     .SelectMany(gpu => gpu.Chunk.Cliffs.Select(face => (Face: face, Gpu: gpu)))
                     .OrderBy(item => item.Face.X1 + item.Face.Y1))
        {
            var world = CliffWorld(item.Face);
            var key = $"CLF01_NN#{(item.Face.X1 == item.Face.X2 ? 6 : 0)}";
            if (!IsAtlasItemVisible(key, world))
                continue;
            _worldRenderQueue.AddObject(
                world,
                item.Gpu.Opacity,
                $"cliff:{item.Face.X1}:{item.Face.Y1}:{item.Face.X2}:{item.Face.Y2}",
                key);
        }
        foreach (var item in visibleChunks
                     .SelectMany(gpu => gpu.Chunk.Trees.Select(tree => (Tree: tree, Gpu: gpu)))
                     .OrderBy(item => item.Tree.X + item.Tree.Y))
        {
            var tree = item.Tree;
            var treeInstance = item.Gpu.Chunk.TreeInstances.FirstOrDefault(
                instance => instance.X == tree.X && instance.Y == tree.Y);
            var isStump = treeInstance?.State == TreeLifecycleState.Stump;
            var visibleName = isStump
                ? StumpAtlasKey(tree.GraphicName, shadow: false)
                : WorldTreeCatalog.AtlasKey(tree);
            var tile = _worldChunks[new(
                FloorDiv(tree.X, WorldChunk.Size), FloorDiv(tree.Y, WorldChunk.Size))]
                .Chunk.Tiles[
                    PositiveMod(tree.Y, WorldChunk.Size) * WorldChunk.Size +
                    PositiveMod(tree.X, WorldChunk.Size)];
            var height = (tile.North + tile.East + tile.South + tile.West) / 4f;
            var world = new Vector2(
                (tree.X - tree.Y) * 48,
                (tree.X + tree.Y + 1) * 24 - height * 20);
            var shadowName = isStump
                ? StumpAtlasKey(tree.GraphicName, shadow: true)
                : WorldTreeCatalog.AtlasKey(
                    tree.GraphicName[..^2] + "N0",
                    tree.FrameIndex);
            if (!IsAtlasItemVisible(visibleName, world) &&
                (string.IsNullOrEmpty(shadowName) ||
                 !IsAtlasItemVisible(shadowName, world)))
                continue;
            var stableKey = $"tree:{tree.X}:{tree.Y}";
            if (!string.IsNullOrEmpty(shadowName))
                _worldRenderQueue.AddShadow(
                    world, item.Gpu.Opacity, stableKey,
                    shadowName);
            _worldRenderQueue.AddObject(
                world, item.Gpu.Opacity, stableKey,
                visibleName);
        }

        foreach (var item in visibleChunks
                     .SelectMany(gpu => gpu.Chunk.GroundObjects.Select(
                         groundObject => (Object: groundObject, Gpu: gpu))))
        {
            if (!TryGroundItemVisual(
                    item.Object.ItemId,
                    out _,
                    out _,
                    out var itemAtlasKey,
                    out var shadowAtlasKey))
                continue;
            if (CampfireService.IsCampfire(item.Object))
                itemAtlasKey = CampfirePresentation.AtlasKey(
                    item.Object, _worldGameSeconds, _clock);
            var world = GroundObjectWorld(item.Object);
            if (!IsAtlasItemVisible(itemAtlasKey, world) &&
                (shadowAtlasKey is null ||
                 !IsAtlasItemVisible(shadowAtlasKey, world)))
                continue;
            if (shadowAtlasKey is not null)
                _worldRenderQueue.AddShadow(
                    world,
                    item.Gpu.Opacity,
                    $"ground-shadow:{item.Object.Id:N}",
                    shadowAtlasKey);
            _worldRenderQueue.AddObject(
                world,
                item.Gpu.Opacity,
                $"ground:{item.Object.Id:N}",
                itemAtlasKey);
        }

        foreach (var gpu in visibleChunks)
        foreach (var vegetation in gpu.VegetationRenderItems)
        {
            if (!IsAtlasItemVisible(vegetation.AtlasKey, vegetation.World))
                continue;
            if (vegetation.ShadowAtlasKey is { } shadowAtlasKey)
                _worldRenderQueue.AddShadow(
                    vegetation.World, gpu.Opacity, vegetation.StableKey,
                    shadowAtlasKey);
            _worldRenderQueue.AddObject(
                vegetation.World, gpu.Opacity, vegetation.StableKey,
                vegetation.AtlasKey);
        }

        foreach (var gpu in visibleChunks)
        foreach (var cachedFish in gpu.FishRenderItems)
        {
            var fish = cachedFish.Fish;
            if (IsFishDepleted(fish)) continue;
            var world = cachedFish.World;
            var atlasKey = WorldFishAnimation.AtlasKey(
                fish, _clock);
            if (!IsAtlasItemVisible(atlasKey, world))
                continue;
            _worldRenderQueue.AddShadow(
                world, gpu.Opacity, fish.StableKey,
                WorldFishPresentation.DepthAtlasKey);
            _worldRenderQueue.AddObject(
                world, gpu.Opacity, fish.StableKey, atlasKey);
        }

        _worldRenderQueue.Sort();
        var shadowVertices = _worldRenderQueue.ShadowVertices;
        foreach (var shadow in shadows)
            AddAtlasQuad(
                shadow.AtlasKey!, shadow.World,
                shadow.Opacity, shadowVertices);
        DrawTreeBatch(shadowVertices);

        var atlasVertices = _worldRenderQueue.AtlasVertices;
        var playerDrawn = player is null;
        foreach (var item in objects)
        {
            if (!playerDrawn && item.World.Y > playerDepth)
            {
                FlushAtlas();
                DrawPlayer();
                playerDrawn = true;
            }

            AddAtlasQuad(
                item.AtlasKey, item.World,
                item.Opacity, atlasVertices);
            if (playerDrawn && player is not null &&
                AtlasOverlapsPlayer(item.AtlasKey, item.World, player))
                playerOccluded = true;
        }
        FlushAtlas();
        if (!playerDrawn) DrawPlayer();
        if (player is not null && playerOccluded)
            DrawSprite(
                player.Frame, player.Texture, player.World,
                mirror: player.Mirror, outlineOnly: true,
                wading: player.Wading,
                outlineColor: TeamColor(_activePlayer?.TeamColor ?? 0));
        if (KeyboardState.IsKeyDown(Keys.LeftAlt) ||
            KeyboardState.IsKeyDown(Keys.RightAlt))
            RenderGroundItemOutlines();
        RenderGroundDropPreview();

        void FlushAtlas()
        {
            if (atlasVertices.Count == 0) return;
            DrawTreeBatch(atlasVertices);
            atlasVertices.Clear();
        }

        void DrawPlayer()
        {
            if (player is null) return;
            DrawSprite(
                player.Frame, player.Texture, player.World,
                mirror: player.Mirror, wading: player.Wading,
                teamColor: _activePlayer?.TeamColor ?? 0);
        }
    }

    private bool IsAtlasItemVisible(string atlasKey, Vector2 world)
    {
        if (!_treeAtlas.TryGetValue(atlasKey, out var entry))
            return false;
        var screen = SpriteAnchor(world);
        var scale = SpritePixelScale();
        var left = screen.X - entry.Frame.HotspotX * scale;
        var top = screen.Y - entry.Frame.HotspotY * scale;
        return left + entry.Frame.Width * scale >= 0 &&
               top + entry.Frame.Height * scale >= 0 &&
               left <= ReferenceWidth &&
               top <= ReferenceHeight;
    }

    private void RenderGroundItemOutlines()
    {
        var color = TeamColor(_activePlayer?.TeamColor ?? 0);
        var vertices = new List<float>();
        foreach (var item in _worldChunks.Values
                     .Where(IsChunkVisible)
                     .SelectMany(gpu =>
                         gpu.Chunk.GroundObjects.Select(
                             groundObject => (Object: groundObject, Gpu: gpu)))
                     .OrderBy(item => item.Object.X + item.Object.Y))
        {
            if (!TryGroundItemVisual(
                    item.Object.ItemId,
                    out _,
                    out _,
                    out var atlasKey,
                    out _))
                continue;
            if (CampfireService.IsCampfire(item.Object))
                atlasKey = CampfirePresentation.AtlasKey(
                    item.Object, _worldGameSeconds, _clock);
            var world = GroundObjectWorld(item.Object);
            if (!IsAtlasItemVisible(atlasKey, world))
                continue;
            AddAtlasQuad(
                atlasKey, world, item.Gpu.Opacity, vertices);
        }
        DrawTreeOutlineBatch(vertices, color);
    }

    private void DrawCliffBatch()
    {
        if (_cliffTexture == 0) return;
        var vertices = new List<float>();
        foreach (var item in _worldChunks.Values.Where(IsChunkVisible)
                     .SelectMany(gpu => gpu.Chunk.Cliffs.Select(face => (Face: face, Gpu: gpu)))
                     .OrderBy(item => item.Face.X1 + item.Face.Y1))
        {
            var face = item.Face;
            var top1 = Project(face.X1, face.Y1, face.Top);
            var top2 = Project(face.X2, face.Y2, face.Top);
            var bottom1 = Project(face.X1, face.Y1, face.Bottom);
            var bottom2 = Project(face.X2, face.Y2, face.Bottom);
            var repeat = Math.Max(1, (face.Top - face.Bottom) * .5f);
            Add(top1, 0, 0); Add(bottom1, 0, repeat); Add(bottom2, 1, repeat);
            Add(top1, 0, 0); Add(bottom2, 1, repeat); Add(top2, 1, 0);

            void Add(Vector2 point, float u, float v)
            {
                vertices.Add(point.X); vertices.Add(point.Y);
                vertices.Add(u); vertices.Add(v); vertices.Add(item.Gpu.Opacity);
            }
        }
        if (vertices.Count == 0) return;
        GL.UseProgram(_cliffProgram);
        GL.Uniform2(GL.GetUniformLocation(_cliffProgram, "viewport"),
            (float)ReferenceWidth, (float)ReferenceHeight);
        GL.Uniform2(GL.GetUniformLocation(_cliffProgram, "camera"), _camera.X, _camera.Y);
        GL.Uniform1(GL.GetUniformLocation(_cliffProgram, "zoom"), _zoom);
        GL.Uniform1(GL.GetUniformLocation(_cliffProgram, "cliffTexture"), 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _cliffTexture);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _cliffBatchVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float),
            vertices.ToArray(), BufferUsageHint.StreamDraw);
        const int stride = 5 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, 4 * sizeof(float));
        GL.DisableVertexAttribArray(3);
        GL.DisableVertexAttribArray(4);
        GL.DrawArrays(PrimitiveType.Triangles, 0, vertices.Count / 5);

        static Vector2 Project(float x, float y, float z) =>
            new((x - y) * 48, (x + y) * 24 - z * 20);
    }

    private void AddTreeQuad(string graphicName, Vector2 world, float opacity, List<float> vertices)
        => AddAtlasQuad(graphicName, world, opacity, vertices);

    private static Vector2 CliffWorld(CliffFace face)
    {
        var midpointX = (face.X1 + face.X2) * .5f;
        var midpointY = (face.Y1 + face.Y2) * .5f;
        var midpointHeight = (face.Top + face.Bottom) * .5f;
        return new Vector2(
            (midpointX - midpointY) * 48,
            (midpointX + midpointY) * 24 - midpointHeight * 20);
    }

    private (float Left, float Top, float Right, float Bottom) SpriteBounds(
        SpriteFrame frame, Vector2 world, bool mirror = false)
    {
        var anchor = SpriteAnchor(world);
        var spriteScale = SpritePixelScale();
        var hotspotX = mirror ? frame.Width - frame.HotspotX : frame.HotspotX;
        var left = anchor.X - hotspotX * spriteScale;
        var top = anchor.Y - frame.HotspotY * spriteScale;
        return (left, top,
            left + frame.Width * spriteScale,
            top + frame.Height * spriteScale);
    }

    private float SpritePixelScale() => _zoom;

    private Vector2 SpriteAnchor(Vector2 world)
    {
        return new Vector2(ReferenceWidth, ReferenceHeight) * .5f +
               _camera + world * _zoom;
    }

    private void AddAtlasQuad(string atlasKey, Vector2 world, float opacity, List<float> vertices)
    {
        if (!_treeAtlas.TryGetValue(atlasKey, out var entry)) return;
        var frame = entry.Frame;
        var width = ReferenceWidth;
        var height = ReferenceHeight;
        var screen = SpriteAnchor(world);
        var spriteScale = SpritePixelScale();
        var margin = Math.Max(frame.Width, frame.Height) * spriteScale;
        if (screen.X < -margin || screen.Y < -margin ||
            screen.X > width + margin || screen.Y > height + margin)
            return;
        var left = screen.X - frame.HotspotX * spriteScale;
        var top = screen.Y - frame.HotspotY * spriteScale;
        var right = left + frame.Width * spriteScale;
        var bottom = top + frame.Height * spriteScale;
        var leftNdc = (left - width * .5f) * 2 / width;
        var rightNdc = (right - width * .5f) * 2 / width;
        var topNdc = -(top - height * .5f) * 2 / height;
        var bottomNdc = -(bottom - height * .5f) * 2 / height;
        Add(leftNdc, topNdc, entry.U0, entry.V0);
        Add(leftNdc, bottomNdc, entry.U0, entry.V1);
        Add(rightNdc, bottomNdc, entry.U1, entry.V1);
        Add(leftNdc, topNdc, entry.U0, entry.V0);
        Add(rightNdc, bottomNdc, entry.U1, entry.V1);
        Add(rightNdc, topNdc, entry.U1, entry.V0);

        void Add(float px, float py, float u, float v)
        {
            vertices.Add(px); vertices.Add(py); vertices.Add(u); vertices.Add(v);
            vertices.Add(opacity);
        }
    }

    private void DrawTreeBatch(List<float> vertices)
    {
        if (vertices.Count == 0 || _treeAtlasTexture == 0) return;
        GL.UseProgram(_program);
        GL.Uniform1(GL.GetUniformLocation(_program, "image"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "opacity"), 1f);
        GL.Uniform1(GL.GetUniformLocation(_program, "outlineOnly"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "wading"), 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _treeAtlasTexture);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _treeBatchVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float),
            _worldRenderQueue.CopyVertices(vertices),
            BufferUsageHint.StreamDraw);
        const int stride = 5 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, 4 * sizeof(float));
        GL.DisableVertexAttribArray(3);
        GL.DisableVertexAttribArray(4);
        GL.DrawArrays(PrimitiveType.Triangles, 0, vertices.Count / 5);
    }

    private void DrawTreeOutlineBatch(
        List<float> vertices, Vector3 color)
    {
        if (vertices.Count == 0 ||
            _treeAtlasTexture == 0 ||
            _treeAtlasWidth <= 0 ||
            _treeAtlasHeight <= 0)
            return;
        GL.UseProgram(_program);
        GL.Uniform1(_shaderUniforms.Get(_program, "image"), 0);
        GL.Uniform1(_shaderUniforms.Get(_program, "opacity"), 1f);
        GL.Uniform1(_shaderUniforms.Get(_program, "outlineOnly"), 1);
        GL.Uniform3(
            _shaderUniforms.Get(_program, "outlineColor"), color);
        GL.Uniform2(
            _shaderUniforms.Get(_program, "texelSize"),
            1f / _treeAtlasWidth,
            1f / _treeAtlasHeight);
        GL.Uniform1(_shaderUniforms.Get(_program, "wading"), 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _treeAtlasTexture);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _treeBatchVbo);
        GL.BufferData(
            BufferTarget.ArrayBuffer,
            vertices.Count * sizeof(float),
            _worldRenderQueue.CopyVertices(vertices),
            BufferUsageHint.StreamDraw);
        const int stride = 5 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(
            0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(
            1, 2, VertexAttribPointerType.Float, false, stride,
            2 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(
            2, 1, VertexAttribPointerType.Float, false, stride,
            4 * sizeof(float));
        GL.DisableVertexAttribArray(3);
        GL.DisableVertexAttribArray(4);
        GL.DrawArrays(
            PrimitiveType.Triangles, 0, vertices.Count / 5);
        GL.Uniform1(_shaderUniforms.Get(_program, "outlineOnly"), 0);
    }

    private void StreamWorld()
    {
        if (_worldStore is null) return;
        if (_saveTail.IsFaulted)
            throw _saveTail.Exception?.GetBaseException() ??
                  new IOException("Background chunk save failed.");
        if (_pendingChunkTask is { IsCompleted: true })
        {
            if (_pendingChunkTask.IsFaulted)
                throw _pendingChunkTask.Exception?.GetBaseException() ??
                      new InvalidOperationException("Chunk loading failed.");
            var loaded = _pendingChunkTask.Result;
            if (!_worldChunks.ContainsKey(loaded.Coordinate))
                _worldChunks.Add(loaded.Coordinate, UploadWorldChunk(loaded));
            _pendingChunkTask = null;
        }
        var mapCenter = ScreenToTerrain(
            new(ReferenceWidth * .5f, ReferenceHeight * .5f));
        var center = new ChunkCoordinate(
            FloorDiv((int)MathF.Floor(mapCenter.X), WorldChunk.Size),
            FloorDiv((int)MathF.Floor(mapCenter.Y), WorldChunk.Size));
        const int loadRadius = 2;
        const int unloadRadius = 3;

        var wanted = new List<ChunkCoordinate>();
        for (var y = center.Y - loadRadius; y <= center.Y + loadRadius; y++)
        for (var x = center.X - loadRadius; x <= center.X + loadRadius; x++)
            if (!_worldChunks.ContainsKey(new(x, y)) &&
                (_pendingChunkTask is null || _pendingChunkCoordinate != new ChunkCoordinate(x, y)))
                wanted.Add(new(x, y));
        if (wanted.Count > 0 && _pendingChunkTask is null)
        {
            _pendingChunkCoordinate = wanted.OrderBy(value =>
                (value.X - center.X) * (value.X - center.X) +
                (value.Y - center.Y) * (value.Y - center.Y)).First();
            var store = _worldStore;
            var coordinate = _pendingChunkCoordinate;
            _pendingChunkTask = Task.Run(() => store.LoadOrGenerate(coordinate));
        }

        foreach (var coordinate in _worldChunks.Keys
                     .Where(value => Math.Abs(value.X - center.X) > unloadRadius ||
                                     Math.Abs(value.Y - center.Y) > unloadRadius)
                     .ToArray())
            UnloadWorldChunk(coordinate, save: true);
    }

    private void PrepareWorldTerrain()
    {
        _terrainArray = UploadTerrainArray();
        _waterNormalArray = UploadWaterNormalArray();
        _terrainProgram = GameShaderPrograms.CreateTerrainProgram();
        _cliffProgram = GameShaderPrograms.CreateCliffProgram();
        var rock = _catalog!.TerrainTiles.First(tile =>
            tile.Name.Equals(TerrainName(Biome.Rock), StringComparison.OrdinalIgnoreCase));
        _cliffTexture = Upload(rock.Width, rock.Height, rock.Rgba);
        GL.BindTexture(TextureTarget.Texture2D, _cliffTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.Repeat);
        _cliffBatchVbo = GL.GenBuffer();
        PrepareTreeAtlas();
    }

    private void PrepareEntityAnimations()
    {
        var suffixes = new Dictionary<EntityAction, string>
        {
            // BAS_SN is the final skeleton/decay sheet. AoE holds a neutral
            // frame from the living walk sheet when the basic villager is idle.
            [EntityAction.Idle] = "WN",
            [EntityAction.Move] = "WN",
            [EntityAction.Attack] = "AN",
            [EntityAction.Work] = "AN",
            [EntityAction.Gather] = "TN",
            [EntityAction.Fish] = "TN",
            [EntityAction.Die] = "DN"
        };
        var uploaded = new Dictionary<string, EntityAnimation>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var gender in Enum.GetValues<EntityGender>())
        foreach (var pair in suffixes)
        {
            var prefix = pair.Key switch
            {
                EntityAction.Work =>
                    gender == EntityGender.Male ? "VMLUM_" : "VFLUM_",
                EntityAction.Gather =>
                    gender == EntityGender.Male ? "VMFOR_" : "VFFOR_",
                EntityAction.Fish =>
                    gender == EntityGender.Male ? "VMFIS_" : "VFFIS_",
                _ => gender == EntityGender.Male ? "VMBAS_" : "VFBAS_"
            };
            var name = prefix + pair.Value;
            if (uploaded.TryGetValue(name, out var existing))
            {
                _entityAnimations[(gender, pair.Key)] = existing;
                continue;
            }
            var graphic = _catalog!.Graphics.Values.FirstOrDefault(value =>
                value.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (graphic is null) continue;
            var textures = graphic.Sprite.Frames.Select(Upload).ToArray();
            var rate = graphic.Definition.FrameRate is > .015f and < 2f
                ? graphic.Definition.FrameRate
                : .09f;
            if (pair.Key == EntityAction.Fish)
                rate = FishingSkill.AnimationFrameSeconds(rate);
            var animation = new EntityAnimation(graphic, textures, rate);
            uploaded[name] = animation;
            _entityAnimations[(gender, pair.Key)] = animation;
        }
        foreach (var gender in Enum.GetValues<EntityGender>())
        {
            if (!_entityAnimations.ContainsKey((gender, EntityAction.Idle)) ||
                !_entityAnimations.ContainsKey((gender, EntityAction.Move)) ||
                !_entityAnimations.ContainsKey((gender, EntityAction.Gather)))
                throw new InvalidOperationException(
                    $"The installed assets do not contain the complete {gender} villager rig.");
        }

        var markerGraphic = _catalog!.Graphics.Values.FirstOrDefault(value =>
            value.Definition.Name.Equals("MOVEX_NN", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "The installed assets do not contain the MOVEX_NN movement marker.");
        _moveMarkerAnimation = new(
            markerGraphic,
            markerGraphic.Sprite.Frames.Select(Upload).ToArray(),
            markerGraphic.Definition.FrameRate is > .015f and < 2f
                ? markerGraphic.Definition.FrameRate
                : .08f);
        PrepareGameCursors();
    }

    private void PrepareGameUi()
    {
        PrepareFishingItemSprites();
        _uiPanelFillTexture = Upload(1, 1, [20, 20, 19, 148]);
        _uiSolidTexture = Upload(1, 1, [255, 255, 255, 255]);
        _uiTabFrame = new SpriteFrame(
            42, 42, 0, 0, CreateTabPixels(active: false));
        _uiTabTexture = Upload(_uiTabFrame);
        _uiActiveTabTexture = Upload(
            42, 42, CreateTabPixels(active: true));
        _minimapFrame = new SpriteFrame(160, 160, 0, 0, new byte[160 * 160 * 4]);
        _minimapTexture = Upload(_minimapFrame);
        _newWorldPreviewFrame = new SpriteFrame(
            128, 128, 0, 0, new byte[128 * 128 * 4]);
        _newWorldPreviewTexture = Upload(_newWorldPreviewFrame);
        var itemSheetPath = Path.Combine(
            AppContext.BaseDirectory, "Resources", "Images",
            "woodcutting-items.png");
        if (File.Exists(itemSheetPath))
        {
            using var itemSheetStream = File.OpenRead(itemSheetPath);
            var itemSheet = ImageResult.FromStream(
                itemSheetStream, ColorComponents.RedGreenBlueAlpha);
            _woodcuttingItemsTexture = Upload(
                itemSheet.Width, itemSheet.Height, itemSheet.Data);
            const int cellSize = 32;
            for (var cell = 0; cell < _woodcuttingItemFrames.Length; cell++)
            {
                var pixels = new byte[cellSize * cellSize * 4];
                var cellX = cell % 4 * cellSize;
                var cellY = cell / 4 * cellSize;
                for (var row = 0; row < cellSize; row++)
                    System.Buffer.BlockCopy(
                        itemSheet.Data,
                        ((cellY + row) * itemSheet.Width + cellX) * 4,
                        pixels,
                        row * cellSize * 4,
                        cellSize * 4);
                var sourceFrame = new SpriteFrame(
                    cellSize, cellSize, cellSize / 2, 28, pixels);
                _woodcuttingInventoryFrames[cell] = sourceFrame;
                var frame = sourceFrame;
                _woodcuttingItemFrames[cell] = frame;
                _woodcuttingShadowFrames[cell] =
                    ItemShadowGenerator.Create(frame);
                _woodcuttingItemTextures[cell] = Upload(frame);
            }
        }
        var naturalItemSheetPath = Path.Combine(
            AppContext.BaseDirectory, "Resources", "Images",
            "rocks-sticks-items.png");
        if (File.Exists(naturalItemSheetPath))
        {
            using var stream = File.OpenRead(naturalItemSheetPath);
            var sheet = ImageResult.FromStream(
                stream, ColorComponents.RedGreenBlueAlpha);
            const int cellSize = 32;
            for (var cell = 0; cell < _naturalItemTextures.Length; cell++)
            {
                var pixels = new byte[cellSize * cellSize * 4];
                for (var row = 0; row < cellSize; row++)
                    System.Buffer.BlockCopy(
                        sheet.Data,
                        (row * sheet.Width + cell * cellSize) * 4,
                        pixels,
                        row * cellSize * 4,
                        cellSize * 4);
                var frame = new SpriteFrame(
                    cellSize, cellSize, cellSize / 2, 28, pixels);
                _naturalItemFrames[cell] = frame;
                _naturalShadowFrames[cell] =
                    ItemShadowGenerator.Create(frame);
                _naturalItemTextures[cell] = Upload(frame);
            }
        }
        var supplementalSheetPath = Path.Combine(
            AppContext.BaseDirectory, "Resources", "Images",
            "seeds-materials-items.png");
        if (File.Exists(supplementalSheetPath))
        {
            using var stream = File.OpenRead(supplementalSheetPath);
            var sheet = ImageResult.FromStream(
                stream, ColorComponents.RedGreenBlueAlpha);
            const int cellSize = 32;
            for (var cell = 0;
                 cell < _supplementalItemTextures.Length;
                 cell++)
            {
                var pixels = new byte[cellSize * cellSize * 4];
                var cellX = cell % 4 * cellSize;
                var cellY = cell / 4 * cellSize;
                for (var row = 0; row < cellSize; row++)
                    System.Buffer.BlockCopy(
                        sheet.Data,
                        ((cellY + row) * sheet.Width + cellX) * 4,
                        pixels,
                        row * cellSize * 4,
                        cellSize * 4);
                var frame = new SpriteFrame(
                    cellSize, cellSize, cellSize / 2, 28, pixels);
                _supplementalItemFrames[cell] = frame;
                _supplementalShadowFrames[cell] =
                    ItemShadowGenerator.Create(frame);
                _supplementalItemTextures[cell] = Upload(frame);
            }
        }
        var stoneToolSheetPath = Path.Combine(
            AppContext.BaseDirectory, "Resources", "Images",
            "stone-tools-items.png");
        if (File.Exists(stoneToolSheetPath))
        {
            using var stream = File.OpenRead(stoneToolSheetPath);
            var sheet = ImageResult.FromStream(
                stream, ColorComponents.RedGreenBlueAlpha);
            const int cellSize = 32;
            for (var cell = 0; cell < 2; cell++)
            {
                var pixels = new byte[cellSize * cellSize * 4];
                for (var row = 0; row < cellSize; row++)
                    System.Buffer.BlockCopy(
                        sheet.Data,
                        (row * sheet.Width + cell * cellSize) * 4,
                        pixels,
                        row * cellSize * 4,
                        cellSize * 4);
                var frame = new SpriteFrame(
                    cellSize, cellSize, cellSize / 2, 28, pixels);
                _stoneToolFrames[cell] = frame;
                _stoneToolShadowFrames[cell] =
                    ItemShadowGenerator.Create(frame);
                _stoneToolTextures[cell] = Upload(frame);
            }
        }
        var stonePickaxePath = Path.Combine(
            AppContext.BaseDirectory, "Resources", "Images",
            "stone-pickaxe-item.png");
        if (File.Exists(stonePickaxePath))
        {
            using var stream = File.OpenRead(stonePickaxePath);
            var image = ImageResult.FromStream(
                stream, ColorComponents.RedGreenBlueAlpha);
            var frame = new SpriteFrame(
                image.Width, image.Height, image.Width / 2, 28, image.Data);
            _stoneToolFrames[2] = frame;
            _stoneToolShadowFrames[2] =
                ItemShadowGenerator.Create(frame);
            _stoneToolTextures[2] = Upload(frame);
        }
        var stoneKnifePath = Path.Combine(
            AppContext.BaseDirectory, "Resources", "Images",
            "stone-knife-item.png");
        if (File.Exists(stoneKnifePath))
        {
            using var stream = File.OpenRead(stoneKnifePath);
            var image = ImageResult.FromStream(
                stream, ColorComponents.RedGreenBlueAlpha);
            var frame = new SpriteFrame(
                image.Width, image.Height, image.Width / 2, 28, image.Data);
            _stoneToolFrames[3] = frame;
            _stoneToolShadowFrames[3] =
                ItemShadowGenerator.Create(frame);
            _stoneToolTextures[3] = Upload(frame);
        }
        _coastalSprites = CoastalCollectibleSprites.Load(
            Path.Combine(
                AppContext.BaseDirectory, "Resources", "Images",
                "coastal-collectibles.png"),
            Upload);
        _fibreNetSprites = FibreNetItemSprites.Load(
            Path.Combine(
                AppContext.BaseDirectory, "Resources", "Images",
                "fibre-net-items.png"),
            Upload);
        _placeableObjectSprites = PlaceableObjectSprites.Load(
            Path.Combine(
                AppContext.BaseDirectory, "Resources", "Images"),
            Upload);
        PrepareGroundToolSprites();
        GL.BindTexture(TextureTarget.Texture2D, _minimapTexture);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        foreach (var texture in new[]
                 {
                     _uiPanelFillTexture, _uiSolidTexture,
                     _uiTabTexture, _uiActiveTabTexture,
                     _newWorldPreviewTexture
                 })
        {
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
        }

        var fontPath = Path.Combine(
            AppContext.BaseDirectory, "Resources", "Fonts",
            "Arimo-Variable.ttf");
        _fontRenderer = new OpenGlFontRenderer(DrawFontQuad);
        _fontSystem = new FontSystem(new FontSystemSettings
        {
            TextureWidth = 512,
            TextureHeight = 512,
            FontResolutionFactor = 2
        });
        _fontSystem.AddFont(File.ReadAllBytes(fontPath));
        _chatFont = _fontSystem.GetFont(14);
        _chatLineHeight = MathF.Ceiling(
            Math.Max(16, _chatFont.MeasureString("Ag").Y));
    }

    private void PrepareGroundToolSprites()
    {
        foreach (var item in ItemCatalog.All.Where(item =>
                     item.HasTag(ItemTag.Tool) &&
                     item.SpriteCell is not null))
        {
            var source = InventoryItemPixelFrame(item.Id);
            var frame = SpriteFrameTransforms.Rotate(source, 45);
            _groundToolSprites[item.Id] = new(
                frame,
                Upload(frame),
                ItemShadowGenerator.Create(frame));
        }
    }

    private static byte[] CreateTabPixels(bool active)
    {
        const int size = 42;
        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            if (!InsideRoundedTab(x, y, 0, 8)) continue;
            byte red;
            byte green;
            byte blue;
            if (!InsideRoundedTab(x, y, 1, 7))
            {
                red = 13; green = 12; blue = 10;
            }
            else if (!InsideRoundedTab(x, y, 2, 6))
            {
                red = 91; green = 78; blue = 49;
            }
            else if (!InsideRoundedTab(x, y, 4, 5))
            {
                red = 24; green = 23; blue = 19;
            }
            else
            {
                var noise = ((x * 37 + y * 57 + x * y * 3) & 7) - 3;
                var topLight = Math.Max(0, 7 - y / 5);
                red = (byte)Math.Clamp(
                    (active ? 61 : 39) + noise + topLight, 0, 255);
                green = (byte)Math.Clamp(
                    (active ? 15 : 38) + noise + topLight, 0, 255);
                blue = (byte)Math.Clamp(
                    (active ? 16 : 34) + noise + topLight, 0, 255);
            }

            var index = (y * size + x) * 4;
            rgba[index] = red;
            rgba[index + 1] = green;
            rgba[index + 2] = blue;
            rgba[index + 3] = 255;
        }
        return rgba;

        static bool InsideRoundedTab(int x, int y, int inset, int radius)
        {
            const int extent = 41;
            var left = inset;
            var top = inset;
            var right = extent - inset;
            var bottom = extent - inset;
            if (x < left || x > right || y < top || y > bottom) return false;
            var nearestX = Math.Clamp(x, left + radius, right - radius);
            var nearestY = Math.Clamp(y, top + radius, bottom - radius);
            var deltaX = x - nearestX;
            var deltaY = y - nearestY;
            return deltaX * deltaX + deltaY * deltaY <= radius * radius;
        }
    }

    private void DrawMoveMarker()
    {
        if (_moveMarker is null || _moveMarkerAnimation is null) return;
        var animation = _moveMarkerAnimation;
        var frameIndex = Math.Min(
            animation.Textures.Length - 1,
            (int)(_moveMarker.Time / animation.SecondsPerFrame));
        var elevation = SamplePlayerTerrain(
            _moveMarker.Position.X,
            _moveMarker.Position.Y).Height;
        var world = new Vector2(
            (_moveMarker.Position.X - _moveMarker.Position.Y) * 48,
            (_moveMarker.Position.X + _moveMarker.Position.Y) * 24 - elevation * 20);
        DrawSprite(
            animation.Graphic.Sprite.Frames[frameIndex],
            animation.Textures[frameIndex],
            world,
            tint: _moveMarker.Action
                ? new Vector3(.18f, 1f, .24f)
                : null,
            tintAmount: _moveMarker.Action ? 1f : 0,
            preserveDarkTint: _moveMarker.Action);
    }

    private PlayerVisual? GetPlayerVisual()
    {
        const int storedVillagerAngles = 5;
        if (_player is null ||
            !_entityAnimations.TryGetValue((_player.Gender, _player.Action), out var animation))
            return null;
        var graphic = animation.Graphic;
        var framesPerAngle = Math.Max(
            1, graphic.Sprite.Frames.Count / storedVillagerAngles);
        var rawFrame = _player.Action == EntityAction.Idle
            ? VillagerDirectionRig.NeutralIdleFrame(framesPerAngle)
            : (int)(_player.ActionTime / animation.SecondsPerFrame);
        if (_player.Action == EntityAction.Gather &&
            (_activeGroundDrop is not null ||
             _activeCooking is { ReadyAt: null }))
        {
            var progress = Math.Clamp(
                (float)(_player.ActionTime /
                        GroundItemActionSeconds), 0, 1);
            rawFrame = (int)MathF.Round(
                (framesPerAngle - 1) * (1 - progress));
        }
        else if (_player.Action == EntityAction.Gather &&
                 (_activeGroundPickupId is not null ||
                  _activeCampfireFuelPickupId is not null))
        {
            var progress = Math.Clamp(
                (float)(_player.ActionTime /
                        GroundItemActionSeconds), 0, 1);
            rawFrame = (int)MathF.Round(
                (framesPerAngle - 1) * progress);
        }
        if (_player.Action == EntityAction.Die)
            rawFrame = Math.Min(rawFrame, framesPerAngle - 1);
        var directional = VillagerDirectionRig.Resolve(
            _player.Facing,
            graphic.Sprite.Frames.Count,
            storedVillagerAngles,
            rawFrame);
        var terrain = SamplePlayerTerrain(
            _player.Position.X, _player.Position.Y);
        var elevation = terrain.Height;
        var world = new Vector2(
            (_player.Position.X - _player.Position.Y) * 48,
            (_player.Position.X + _player.Position.Y) * 24 - elevation * 20);
        var biome = terrain.Biome;
        return new(
            graphic.Sprite.Frames[directional.Index],
            animation.Textures[directional.Index],
            world,
            directional.Mirror,
            biome is Biome.ShallowWater or
                Biome.RiverWater or Biome.MangroveShallows);
    }

    private void PrepareTreeAtlas()
    {
        const int atlasWidth = 2048;
        const int padding = 1;
        var placements = new List<(
            string Key, string? Alias, SpriteFrame Frame, int X, int Y)>();
        var x = padding;
        var y = padding;
        var rowHeight = 0;
        foreach (var asset in _worldAssets)
        {
            var cliff = asset.Definition.Name.StartsWith("CLF", StringComparison.OrdinalIgnoreCase);
            var stump = asset.Definition.Name.StartsWith(
                "STUMP", StringComparison.OrdinalIgnoreCase) ||
                asset.Definition.Name.StartsWith(
                    "STUMB", StringComparison.OrdinalIgnoreCase);
            var vegetation = WorldVegetationGenerator.IsVegetationGraphic(
                asset.Definition.Name);
            var treeVariants = WorldTreeCatalog.HasVariants(
                asset.Definition.Name);
            var fish = WorldFishGenerator.IsFishGraphic(
                asset.Definition.Name);
            var frames = cliff || stump || vegetation || treeVariants || fish
                ? asset.Sprite.Frames
                : [asset.Sprite.Frames[0]];
            for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                var frame = frames[frameIndex];
                var key = cliff || stump || vegetation || treeVariants || fish
                    ? $"{asset.Definition.Name}#{frameIndex}"
                    : asset.Definition.Name;
                Place(
                    key,
                    (cliff || stump || vegetation || treeVariants || fish) &&
                    frameIndex == 0
                        ? asset.Definition.Name
                        : null,
                    frame);
            }
        }
        for (var cell = 0; cell < _naturalItemFrames.Length; cell++)
        {
            if (_naturalItemFrames[cell] is { } itemFrame)
                Place(NaturalAtlasKey(cell, shadow: false), null, itemFrame);
            if (_naturalShadowFrames[cell] is { } shadowFrame)
                Place(NaturalAtlasKey(cell, shadow: true), null, shadowFrame);
        }
        for (var cell = 0; cell < _woodcuttingItemFrames.Length; cell++)
        {
            if (_woodcuttingItemFrames[cell] is { } itemFrame)
                Place(ItemAtlasKey(cell, shadow: false), null, itemFrame);
            if (_woodcuttingShadowFrames[cell] is { } shadowFrame)
                Place(ItemAtlasKey(cell, shadow: true), null, shadowFrame);
        }
        for (var cell = 0; cell < _supplementalItemFrames.Length; cell++)
        {
            if (_supplementalItemFrames[cell] is { } itemFrame)
                Place(
                    SupplementalAtlasKey(cell, shadow: false),
                    null, itemFrame);
            if (_supplementalShadowFrames[cell] is { } shadowFrame)
                Place(
                    SupplementalAtlasKey(cell, shadow: true),
                    null, shadowFrame);
        }
        for (var cell = 0; cell < _stoneToolFrames.Length; cell++)
        {
            if (_stoneToolFrames[cell] is { } itemFrame)
                Place(StoneToolAtlasKey(cell, shadow: false), null, itemFrame);
            if (_stoneToolShadowFrames[cell] is { } shadowFrame)
                Place(StoneToolAtlasKey(cell, shadow: true), null, shadowFrame);
        }
        for (var cell = 0;
             cell < _coastalSprites.GroundFrames.Length;
             cell++)
        {
            if (_coastalSprites.GroundFrames[cell] is { } itemFrame)
                Place(CoastalAtlasKey(cell, shadow: false), null, itemFrame);
            if (_coastalSprites.GroundShadows[cell] is { } shadowFrame)
                Place(CoastalAtlasKey(cell, shadow: true), null, shadowFrame);
        }
        for (var cell = 0; cell < _fishItemFrames.Length; cell++)
        {
            if (_fishItemFrames[cell] is { } itemFrame)
                Place(
                    FishItemAtlasKey(cell, shadow: false),
                    null, itemFrame);
            if (_fishItemShadowFrames[cell] is { } shadowFrame)
                Place(
                    FishItemAtlasKey(cell, shadow: true),
                    null, shadowFrame);
        }
        for (var cell = 0;
             cell < _fibreNetSprites.Frames.Length;
             cell++)
        {
            if (_fibreNetSprites.Frames[cell] is { } itemFrame)
                Place(
                    FibreNetAtlasKey(cell, shadow: false),
                    null, itemFrame);
            if (_fibreNetSprites.Shadows[cell] is { } shadowFrame)
                Place(
                    FibreNetAtlasKey(cell, shadow: true),
                    null, shadowFrame);
        }
        foreach (var tool in _groundToolSprites)
        {
            Place(
                GroundToolAtlasKey(tool.Key, shadow: false),
                null, tool.Value.Frame);
            Place(
                GroundToolAtlasKey(tool.Key, shadow: true),
                null, tool.Value.Shadow);
        }
        foreach (var placeable in _placeableObjectSprites.All)
        {
            Place(
                PlaceableObjectAtlasKey(
                    placeable.Key, shadow: false),
                null, placeable.Value.Frame);
            Place(
                PlaceableObjectAtlasKey(
                    placeable.Key, shadow: true),
                null, placeable.Value.Shadow);
        }
        foreach (var campfire in
                 _placeableObjectSprites.CampfireAtlasFrames)
            Place(campfire.Key, null, campfire.Frame);
        Place(
            WorldFishPresentation.DepthAtlasKey,
            null,
            WorldFishPresentation.CreateDepthFrame());
        var requiredHeight = y + rowHeight + padding;
        var atlasHeight = 1;
        while (atlasHeight < requiredHeight) atlasHeight *= 2;
        var rgba = new byte[atlasWidth * atlasHeight * 4];
        foreach (var placement in placements)
        {
            for (var row = 0; row < placement.Frame.Height; row++)
                System.Buffer.BlockCopy(
                    placement.Frame.Rgba, row * placement.Frame.Width * 4,
                    rgba, ((placement.Y + row) * atlasWidth + placement.X) * 4,
                    placement.Frame.Width * 4);
            _treeAtlas[placement.Key] = new(
                placement.Frame,
                placement.X / (float)atlasWidth,
                placement.Y / (float)atlasHeight,
                (placement.X + placement.Frame.Width) / (float)atlasWidth,
                (placement.Y + placement.Frame.Height) / (float)atlasHeight);
            if (placement.Alias is not null)
                _treeAtlas[placement.Alias] = _treeAtlas[placement.Key];
        }
        _treeAtlasTexture = Upload(atlasWidth, atlasHeight, rgba);
        _treeAtlasWidth = atlasWidth;
        _treeAtlasHeight = atlasHeight;
        _treeBatchVbo = GL.GenBuffer();

        void Place(string key, string? alias, SpriteFrame frame)
        {
            if (x + frame.Width + padding > atlasWidth)
            {
                x = padding;
                y += rowHeight + padding;
                rowHeight = 0;
            }
            placements.Add((key, alias, frame, x, y));
            x += frame.Width + padding;
            rowHeight = Math.Max(rowHeight, frame.Height);
        }
    }

    private static string NaturalAtlasKey(int cell, bool shadow) =>
        shadow ? $"NATURAL_SHADOW#{cell}" : $"NATURAL#{cell}";

    private static string ItemAtlasKey(int cell, bool shadow) =>
        shadow ? $"ITEM_SHADOW#{cell}" : $"ITEM#{cell}";

    private static string SupplementalAtlasKey(int cell, bool shadow) =>
        shadow
            ? $"SUPPLEMENTAL_SHADOW#{cell}"
            : $"SUPPLEMENTAL#{cell}";

    private static string StoneToolAtlasKey(int cell, bool shadow) =>
        shadow ? $"STONE_TOOL_SHADOW#{cell}" : $"STONE_TOOL#{cell}";

    private static string CoastalAtlasKey(int cell, bool shadow) =>
        shadow ? $"COASTAL_SHADOW#{cell}" : $"COASTAL#{cell}";

    private static string GroundToolAtlasKey(string itemId, bool shadow) =>
        shadow ? $"GROUND_TOOL_SHADOW#{itemId}" : $"GROUND_TOOL#{itemId}";

    private static string FibreNetAtlasKey(int cell, bool shadow) =>
        shadow ? $"FIBRE_NET_SHADOW#{cell}" : $"FIBRE_NET#{cell}";

    private static string PlaceableObjectAtlasKey(
        string itemId, bool shadow) =>
        shadow
            ? $"PLACEABLE_OBJECT_SHADOW#{itemId}"
            : $"PLACEABLE_OBJECT#{itemId}";

    private GpuWorldChunk UploadWorldChunk(WorldChunk chunk)
    {
        Vector2 Project(float x, float y, float z) =>
            new((x - y) * 48, (x + y) * 24 - z * 20);
        var layers = Enum.GetValues<Biome>().ToDictionary(biome => biome, biome => (float)(int)biome);
        var vertices = new List<float>(WorldChunk.Size * WorldChunk.Size * 6 * 12);
        var shadeCache = new Dictionary<(int X, int Y), float>();
        var heightCache = new Dictionary<(int X, int Y), float>();
        var rawHeightCache = new Dictionary<(int X, int Y), byte>();
        foreach (var tile in chunk.Tiles.OrderBy(tile => tile.X + tile.Y))
        {
            var localX = PositiveMod(tile.X, WorldChunk.Size);
            var localY = PositiveMod(tile.Y, WorldChunk.Size);
            var points = new[]
            {
                Project(tile.X, tile.Y, SmoothedHeightAt(tile.X, tile.Y)),
                Project(tile.X + 1, tile.Y, SmoothedHeightAt(tile.X + 1, tile.Y)),
                Project(tile.X + 1, tile.Y + 1, SmoothedHeightAt(tile.X + 1, tile.Y + 1)),
                Project(tile.X, tile.Y + 1, SmoothedHeightAt(tile.X, tile.Y + 1))
            };
            var local = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            var north = LayerAt(localX, localY - 1, tile.Biome);
            var east = LayerAt(localX + 1, localY, tile.Biome);
            var south = LayerAt(localX, localY + 1, tile.Biome);
            var west = LayerAt(localX - 1, localY, tile.Biome);
            var cornerShades = new[]
            {
                ShadeAt(tile.X, tile.Y),
                ShadeAt(tile.X + 1, tile.Y),
                ShadeAt(tile.X + 1, tile.Y + 1),
                ShadeAt(tile.X, tile.Y + 1)
            };
            foreach (var corner in new[] { 0, 1, 2, 0, 2, 3 })
            {
                var uv = local[corner];
                vertices.Add(points[corner].X); vertices.Add(points[corner].Y);
                vertices.Add((tile.X + uv.X) / 8f); vertices.Add((tile.Y + uv.Y) / 8f);
                var haloSamples = WorldChunk.WeightHaloTiles * WorldChunk.WeightSamplesPerTile;
                vertices.Add((haloSamples + (localX + uv.X) * WorldChunk.WeightSamplesPerTile) /
                             (WorldChunk.WeightTextureSize - 1f));
                vertices.Add((haloSamples + (localY + uv.Y) * WorldChunk.WeightSamplesPerTile) /
                             (WorldChunk.WeightTextureSize - 1f));
                vertices.Add(layers[tile.Biome]);
                vertices.Add(north); vertices.Add(east); vertices.Add(south); vertices.Add(west);
                vertices.Add(cornerShades[corner]);
            }
        }
        var vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float),
            vertices.ToArray(), BufferUsageHint.StaticDraw);
        var weights = UploadChunkBiomeWeights(chunk);
        var renderedHeights = new float[
            (WorldChunk.Size + 1) * (WorldChunk.Size + 1)];
        for (var vertexY = 0; vertexY <= WorldChunk.Size; vertexY++)
        for (var vertexX = 0; vertexX <= WorldChunk.Size; vertexX++)
            renderedHeights[
                vertexY * (WorldChunk.Size + 1) + vertexX] =
                SmoothedHeightAt(
                    chunk.Coordinate.X * WorldChunk.Size + vertexX,
                    chunk.Coordinate.Y * WorldChunk.Size + vertexY);
        var gpu = new GpuWorldChunk(
            chunk, vbo, vertices.Count / 12,
            weights.A, weights.B, weights.C, weights.D, weights.Shore,
            WorldChunkProjection.TerrainBounds(vertices, 12),
            renderedHeights);
        gpu.VegetationRenderItems = WorldVegetationRenderCache.Build(
            _worldSeed, chunk.Vegetation);
        gpu.FishRenderItems = WorldFishRenderCache.Build(
            _worldSeed, chunk.Fish);
        return gpu;

        float LayerAt(int x, int y, Biome fallback) =>
            layers[x < 0 || y < 0 || x >= WorldChunk.Size || y >= WorldChunk.Size
                ? fallback
                : chunk.Tiles[y * WorldChunk.Size + x].Biome];

        float ShadeAt(int x, int y)
        {
            if (shadeCache.TryGetValue((x, y), out var cached)) return cached;
            var slopeX = (SmoothedHeightAt(x + 1, y) - SmoothedHeightAt(x - 1, y)) * .5f;
            var slopeY = (SmoothedHeightAt(x, y + 1) - SmoothedHeightAt(x, y - 1)) * .5f;
            var normal = Vector3.Normalize(new Vector3(-slopeX * .55f, -slopeY * .55f, 1));
            var light = Vector3.Normalize(new Vector3(.42f, -.42f, .80f));
            var shade = Math.Clamp(
                1f + (Vector3.Dot(normal, light) - light.Z) * .72f, .70f, 1.18f);
            shadeCache[(x, y)] = shade;
            return shade;
        }

        float SmoothedHeightAt(int x, int y)
        {
            if (heightCache.TryGetValue((x, y), out var cached)) return cached;
            var weightedHeight = 0f;
            var totalWeight = 0f;
            for (var offsetY = -1; offsetY <= 1; offsetY++)
            for (var offsetX = -1; offsetX <= 1; offsetX++)
            {
                var weight = (offsetX == 0 ? 2 : 1) * (offsetY == 0 ? 2 : 1);
                var sample = (X: x + offsetX, Y: y + offsetY);
                if (!rawHeightCache.TryGetValue(sample, out var rawHeight))
                {
                    rawHeight = InfiniteWorldGenerator.SampleSurfaceHeight(
                        _worldSeed, sample.X, sample.Y);
                    rawHeightCache[sample] = rawHeight;
                }
                weightedHeight += rawHeight * weight;
                totalWeight += weight;
            }
            var height = weightedHeight / totalWeight;
            heightCache[(x, y)] = height;
            return height;
        }
    }

    private static (int A, int B, int C, int D, int Shore) UploadChunkBiomeWeights(WorldChunk chunk)
    {
        return (Upload(chunk.BiomeWeightsA, PixelInternalFormat.Rgba8, PixelFormat.Rgba),
            Upload(chunk.BiomeWeightsB, PixelInternalFormat.Rgba8, PixelFormat.Rgba),
            Upload(chunk.BiomeWeightsC, PixelInternalFormat.Rgba8, PixelFormat.Rgba),
            Upload(chunk.BiomeWeightsD, PixelInternalFormat.Rgba8, PixelFormat.Rgba),
            Upload(chunk.ShoreDistance, PixelInternalFormat.R8, PixelFormat.Red));

        static int Upload(byte[] data, PixelInternalFormat internalFormat, PixelFormat format)
        {
            var texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, internalFormat,
                WorldChunk.WeightTextureSize, WorldChunk.WeightTextureSize, 0, format,
                PixelType.UnsignedByte, data);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
            return texture;
        }
    }

    private bool IsChunkVisible(GpuWorldChunk gpu) =>
        IsChunkVisibleWithPadding(gpu, 96);

    private bool IsChunkVisibleWithPadding(
        GpuWorldChunk gpu, float padding)
        => WorldChunkProjection.IsVisible(
            gpu.ProjectedBounds,
            _camera,
            _zoom,
            new(ReferenceWidth, ReferenceHeight),
            padding);

    private void DrawWorldChunkTerrain(GpuWorldChunk gpu)
    {
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, gpu.WeightsA);
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, gpu.WeightsB);
        GL.ActiveTexture(TextureUnit.Texture3);
        GL.BindTexture(TextureTarget.Texture2D, gpu.WeightsC);
        GL.ActiveTexture(TextureUnit.Texture4);
        GL.BindTexture(TextureTarget.Texture2D, gpu.WeightsD);
        GL.ActiveTexture(TextureUnit.Texture6);
        GL.BindTexture(TextureTarget.Texture2D, gpu.ShoreDistance);
        GL.Uniform1(
            _shaderUniforms.Get(_terrainProgram, "opacity"), gpu.Opacity);
        GL.BindBuffer(BufferTarget.ArrayBuffer, gpu.Vbo);
        const int stride = 12 * sizeof(float);
        for (var attribute = 0; attribute < 6; attribute++) GL.EnableVertexAttribArray(attribute);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 4 * sizeof(float));
        GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.VertexAttribPointer(4, 2, VertexAttribPointerType.Float, false, stride, 9 * sizeof(float));
        GL.VertexAttribPointer(5, 1, VertexAttribPointerType.Float, false, stride, 11 * sizeof(float));
        GL.DrawArrays(PrimitiveType.Triangles, 0, gpu.VertexCount);
    }

    private void BeginWorldTerrainBatch()
    {
        GL.UseProgram(_terrainProgram);
        GL.Uniform2(
            _shaderUniforms.Get(_terrainProgram, "viewport"),
            (float)ReferenceWidth, (float)ReferenceHeight);
        GL.Uniform2(
            _shaderUniforms.Get(_terrainProgram, "camera"),
            _camera.X, _camera.Y);
        GL.Uniform1(
            _shaderUniforms.Get(_terrainProgram, "zoom"), _zoom);
        GL.Uniform1(
            _shaderUniforms.Get(_terrainProgram, "time"), _waterTime);
        GL.Uniform1(
            _shaderUniforms.Get(_terrainProgram, "terrain"), 0);
        GL.Uniform1(
            _shaderUniforms.Get(_terrainProgram, "biomeWeightsA"), 1);
        GL.Uniform1(
            _shaderUniforms.Get(_terrainProgram, "biomeWeightsB"), 2);
        GL.Uniform1(
            _shaderUniforms.Get(_terrainProgram, "biomeWeightsC"), 3);
        GL.Uniform1(
            _shaderUniforms.Get(_terrainProgram, "biomeWeightsD"), 4);
        GL.Uniform1(
            _shaderUniforms.Get(_terrainProgram, "waterNormals"), 5);
        GL.Uniform1(
            _shaderUniforms.Get(_terrainProgram, "shoreDistance"), 6);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2DArray, _terrainArray);
        GL.ActiveTexture(TextureUnit.Texture5);
        GL.BindTexture(
            TextureTarget.Texture2DArray, _waterNormalArray);
    }

    private void UnloadWorldChunk(ChunkCoordinate coordinate, bool save)
    {
        if (!_worldChunks.Remove(coordinate, out var gpu)) return;
        if (save) QueueChunkSave(gpu.Chunk);
        GL.DeleteBuffer(gpu.Vbo);
        GL.DeleteTexture(gpu.WeightsA);
        GL.DeleteTexture(gpu.WeightsB);
        GL.DeleteTexture(gpu.WeightsC);
        GL.DeleteTexture(gpu.WeightsD);
        GL.DeleteTexture(gpu.ShoreDistance);
    }

    private void QueueChunkSave(WorldChunk source)
    {
        if (_worldStore is null) return;
        var store = _worldStore;
        var snapshot = new WorldChunk
        {
            Coordinate = source.Coordinate,
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
            Vegetation = source.Vegetation,
            Fish = source.Fish,
            FishRemaining = new(
                source.FishRemaining, StringComparer.Ordinal),
            VegetationFibreStates =
                source.VegetationFibreStates.ToList()
        };
        var previous = _saveTail;
        _saveTail = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            store.Save(snapshot);
        });
    }

    private static Vector2 ScreenWorldToMap(Vector2 world) => new(
        (world.Y / 24f + world.X / 48f) * .5f,
        (world.Y / 24f - world.X / 48f) * .5f);

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
    }

    private static int PositiveMod(int value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static string TerrainName(Biome biome)
    {
        return biome switch
        {
            Biome.DeepWater => "g_wt4_00_COLOR",
            Biome.ShallowWater => "g_wt3_00_color",
            Biome.RiverWater => "g_sha_00_color",
            Biome.MangroveShallows => "g_sh3_00_color",
            Biome.Beach => "g_bch_00_color",
            Biome.Forest => "g_for_00_color",
            Biome.JungleFloor => "g_fo2_00_color",
            Biome.DryGrass => "g_gr5_00_color",
            Biome.Mud => "g_gr4_00_color",
            Biome.Highland => "g_gr3_00_color",
            Biome.Rock => "g_rck_00_COLOR",
            Biome.Tundra => "g_sng_00_color",
            Biome.Snow => "g_sno_00_color",
            Biome.DesertSand => "g_pal_00_color",
            Biome.CrackedEarth => "g_pal1_00_COLOR",
            _ => "g_grs_00_color"
        };
    }

    private void PrepareIslandTerrain()
    {
        Vector2 Project(float x, float y, float z) =>
            new((x - y) * 48, (x + y) * 24 - z * 12);
        var layers = Enum.GetValues<Biome>().ToDictionary(b => b, b => (float)(int)b);
        var vertices = new List<float>(IslandMap.Size * IslandMap.Size * 6 * 11);
        foreach (var tile in _island!.Tiles)
        {
            var points = new[]
            {
                Project(tile.X, tile.Y, tile.North),
                Project(tile.X + 1, tile.Y, tile.East),
                Project(tile.X + 1, tile.Y + 1, tile.South),
                Project(tile.X, tile.Y + 1, tile.West)
            };
            var local = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            var north = LayerAt(tile.X, tile.Y - 1, tile.Biome);
            var east = LayerAt(tile.X + 1, tile.Y, tile.Biome);
            var south = LayerAt(tile.X, tile.Y + 1, tile.Biome);
            var west = LayerAt(tile.X - 1, tile.Y, tile.Biome);
            foreach (var corner in new[] { 0, 1, 2, 0, 2, 3 })
            {
                var uv = local[corner];
                vertices.Add(points[corner].X); vertices.Add(points[corner].Y);
                vertices.Add((tile.X + uv.X) / 8f); vertices.Add((tile.Y + uv.Y) / 8f);
                vertices.Add((tile.X + uv.X) / IslandMap.Size);
                vertices.Add((tile.Y + uv.Y) / IslandMap.Size);
                vertices.Add(layers[tile.Biome]);
                vertices.Add(north); vertices.Add(east); vertices.Add(south); vertices.Add(west);
            }
        }
        _islandVertexCount = vertices.Count / 11;
        _islandVbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _islandVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float), vertices.ToArray(), BufferUsageHint.StaticDraw);
        _terrainArray = UploadTerrainArray();
        _waterNormalArray = UploadWaterNormalArray();
        (_biomeWeightsA, _biomeWeightsB, _biomeWeightsC, _biomeWeightsD, _shoreDistance) =
            UploadBiomeWeights();
        _terrainProgram = GameShaderPrograms.CreateTerrainProgram();

        float LayerAt(int x, int y, Biome fallback) =>
            layers[x < 0 || y < 0 || x >= IslandMap.Size || y >= IslandMap.Size
                ? fallback
                : _island.Tiles[y * IslandMap.Size + x].Biome];
    }

    private void DrawIslandTerrainBatch()
    {
        GL.UseProgram(_terrainProgram);
        GL.Uniform2(GL.GetUniformLocation(_terrainProgram, "viewport"),
            (float)ReferenceWidth, (float)ReferenceHeight);
        GL.Uniform2(GL.GetUniformLocation(_terrainProgram, "camera"), _camera.X, _camera.Y);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "zoom"), _zoom);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2DArray, _terrainArray);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "terrain"), 0);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, _biomeWeightsA);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "biomeWeightsA"), 1);
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, _biomeWeightsB);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "biomeWeightsB"), 2);
        GL.ActiveTexture(TextureUnit.Texture3);
        GL.BindTexture(TextureTarget.Texture2D, _biomeWeightsC);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "biomeWeightsC"), 3);
        GL.ActiveTexture(TextureUnit.Texture4);
        GL.BindTexture(TextureTarget.Texture2D, _biomeWeightsD);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "biomeWeightsD"), 4);
        GL.ActiveTexture(TextureUnit.Texture5);
        GL.BindTexture(TextureTarget.Texture2DArray, _waterNormalArray);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "waterNormals"), 5);
        GL.ActiveTexture(TextureUnit.Texture6);
        GL.BindTexture(TextureTarget.Texture2D, _shoreDistance);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "shoreDistance"), 6);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "time"), _waterTime);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "opacity"), 1f);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _islandVbo);
        const int stride = 11 * sizeof(float);
        for (var attribute = 0; attribute < 5; attribute++) GL.EnableVertexAttribArray(attribute);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 4 * sizeof(float));
        GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.VertexAttribPointer(4, 2, VertexAttribPointerType.Float, false, stride, 9 * sizeof(float));
        GL.DisableVertexAttribArray(5);
        GL.VertexAttrib1(5, 1f);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _islandVertexCount);
    }

    private void DrawSprite(
        SpriteFrame frame,
        int texture,
        Vector2 world,
        float opacity = 1,
        bool mirror = false,
        bool outlineOnly = false,
        bool wading = false,
        Vector3? tint = null,
        float tintAmount = 0,
        bool preserveDarkTint = false,
        int teamColor = 0,
        Vector3? outlineColor = null)
    {
        var width = ReferenceWidth;
        var height = ReferenceHeight;
        var screen = SpriteAnchor(world);
        var spriteScale = SpritePixelScale();
        var margin = Math.Max(frame.Width, frame.Height) * spriteScale;
        if (screen.X < -margin || screen.Y < -margin ||
            screen.X > width + margin || screen.Y > height + margin)
            return;
        var hotspotX = mirror ? frame.Width - frame.HotspotX : frame.HotspotX;
        var left = screen.X - hotspotX * spriteScale;
        var top = screen.Y - frame.HotspotY * spriteScale;
        var right = left + frame.Width * spriteScale;
        var bottom = top + frame.Height * spriteScale;
        var leftNdc = (left - width * .5f) * 2 / width;
        var rightNdc = (right - width * .5f) * 2 / width;
        var topNdc = -(top - height * .5f) * 2 / height;
        var bottomNdc = -(bottom - height * .5f) * 2 / height;
        GL.UseProgram(_program);
        GL.Uniform1(GL.GetUniformLocation(_program, "image"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "opacity"), opacity);
        GL.Uniform1(GL.GetUniformLocation(_program, "outlineOnly"), outlineOnly ? 1 : 0);
        GL.Uniform3(
            GL.GetUniformLocation(_program, "outlineColor"),
            outlineColor ?? new Vector3(1f, .82f, .18f));
        GL.Uniform1(GL.GetUniformLocation(_program, "wading"),
            wading && !outlineOnly ? 1 : 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "waterlineUv"),
            Math.Clamp((frame.HotspotY - 13f) / frame.Height, .45f, .88f));
        GL.Uniform1(GL.GetUniformLocation(_program, "brightness"), 0f);
        var tintColor = tint ?? Vector3.Zero;
        GL.Uniform3(GL.GetUniformLocation(_program, "colorTint"), tintColor);
        GL.Uniform1(GL.GetUniformLocation(_program, "tintAmount"), tintAmount);
        GL.Uniform1(
            GL.GetUniformLocation(_program, "preserveDarkTint"),
            preserveDarkTint ? 1 : 0);
        GL.Uniform2(GL.GetUniformLocation(_program, "texelSize"),
            1f / frame.Width, 1f / frame.Height);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        SetPlayerRecolor(outlineOnly ? 0 : teamColor);
        var leftU = mirror ? 1f : 0f;
        var rightU = mirror ? 0f : 1f;
        Draw([
            leftNdc,topNdc,leftU,0,
            leftNdc,bottomNdc,leftU,1,
            rightNdc,bottomNdc,rightU,1,
            rightNdc,topNdc,rightU,0
        ]);
        GL.Uniform1(GL.GetUniformLocation(_program, "tintAmount"), 0f);
        GL.Uniform1(GL.GetUniformLocation(_program, "preserveDarkTint"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "recolorPlayer"), 0);
    }

    private void DrawTerrain(TerrainTile tile, int texture, Vector2 world)
    {
        const float previewSize = 128;
        var width = ReferenceWidth;
        var height = ReferenceHeight;
        var halfW = previewSize * _zoom / width;
        var halfH = previewSize * _zoom / height;
        var x = (world.X * _zoom + _camera.X) * 2 / width;
        var y = (-world.Y * _zoom - _camera.Y) * 2 / height;
        GL.UseProgram(_program);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        Draw([x-halfW,y+halfH,0,0, x-halfW,y-halfH,0,1, x+halfW,y-halfH,1,1, x+halfW,y+halfH,1,0]);
    }

    private void Draw(float[] vertices)
    {
        GL.BindBuffer(BufferTarget.ArrayBuffer, _streamVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StreamDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 16, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 16, 8);
        GL.DisableVertexAttribArray(2);
        GL.VertexAttrib1(2, 1f);
        GL.DisableVertexAttribArray(3);
        GL.DisableVertexAttribArray(4);
        GL.DrawArrays(PrimitiveType.TriangleFan, 0, 4);
    }

    private static int Upload(SpriteFrame frame)
        => Upload(frame.Width, frame.Height, frame.Rgba);

    private static int Upload(int width, int height, byte[] rgba)
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return texture;
    }

    private int UploadTerrainArray()
    {
        var selected = Enum.GetValues<Biome>()
            .Select(biome => _catalog!.TerrainTiles.FirstOrDefault(
                tile => tile.Name.Equals(TerrainName(biome), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Required terrain texture {TerrainName(biome)} was not found."))
            .ToArray();
        var width = selected[0].Width;
        var height = selected[0].Height;
        if (selected.Any(tile => tile.Width != width || tile.Height != height))
            throw new InvalidOperationException("Terrain texture-array layers must have matching dimensions.");

        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2DArray, texture);
        GL.TexStorage3D(TextureTarget3d.Texture2DArray, 1, SizedInternalFormat.Rgba8, width, height, selected.Length);
        for (var layer = 0; layer < selected.Length; layer++)
            GL.TexSubImage3D(TextureTarget.Texture2DArray, 0, 0, 0, layer, width, height, 1,
                PixelFormat.Rgba, PixelType.UnsignedByte, selected[layer].Rgba);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        return texture;
    }

    private int UploadWaterNormalArray()
    {
        var selected = _catalog!.WaterTextures
            .Where(texture => texture.Name.StartsWith("normal", StringComparison.OrdinalIgnoreCase))
            .OrderBy(texture => texture.Name, StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        if (selected.Length < 4)
            throw new InvalidOperationException("Age2HD water normal0.png through normal3.png are required.");
        var width = selected[0].Width;
        var height = selected[0].Height;
        if (selected.Any(texture => texture.Width != width || texture.Height != height))
            throw new InvalidOperationException("Water normal-map layers must have matching dimensions.");

        var textureArray = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2DArray, textureArray);
        GL.TexStorage3D(TextureTarget3d.Texture2DArray, 1, SizedInternalFormat.Rgba8,
            width, height, selected.Length);
        for (var layer = 0; layer < selected.Length; layer++)
            GL.TexSubImage3D(TextureTarget.Texture2DArray, 0, 0, 0, layer,
                width, height, 1, PixelFormat.Rgba, PixelType.UnsignedByte, selected[layer].Rgba);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.Repeat);
        return textureArray;
    }

    private (int A, int B, int C, int D, int Shore) UploadBiomeWeights()
    {
        const int samplesPerTile = 4;
        const int radius = 10;
        var size = IslandMap.Size * samplesPerTile;
        var channels = Enum.GetValues<Biome>().Length;
        var weights = new float[size * size * channels];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var tileX = Math.Min(IslandMap.Size - 1, x / samplesPerTile);
            var tileY = Math.Min(IslandMap.Size - 1, y / samplesPerTile);
            var biome = (int)_island!.Tiles[tileY * IslandMap.Size + tileX].Biome;
            weights[(y * size + x) * channels + biome] = 1;
        }

        var kernel = new float[radius * 2 + 1];
        var sum = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            var value = MathF.Exp(-(i * i) / (2f * 4.6f * 4.6f));
            kernel[i + radius] = value;
            sum += value;
        }
        for (var i = 0; i < kernel.Length; i++) kernel[i] /= sum;

        var scratch = new float[weights.Length];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        for (var channel = 0; channel < channels; channel++)
        {
            var value = 0f;
            for (var k = -radius; k <= radius; k++)
            {
                var sx = Math.Clamp(x + k, 0, size - 1);
                value += weights[(y * size + sx) * channels + channel] * kernel[k + radius];
            }
            scratch[(y * size + x) * channels + channel] = value;
        }
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        for (var channel = 0; channel < channels; channel++)
        {
            var value = 0f;
            for (var k = -radius; k <= radius; k++)
            {
                var sy = Math.Clamp(y + k, 0, size - 1);
                value += scratch[(sy * size + x) * channels + channel] * kernel[k + radius];
            }
            weights[(y * size + x) * channels + channel] = value;
        }

        var a = new byte[size * size * 4];
        var b = new byte[size * size * 4];
        var c = new byte[size * size * 4];
        var d = new byte[size * size * 4];
        var shore = new byte[size * size];
        for (var pixel = 0; pixel < size * size; pixel++)
        {
            var total = 0f;
            for (var channel = 0; channel < channels; channel++)
                total += weights[pixel * channels + channel];
            for (var channel = 0; channel < channels; channel++)
            {
                var value = (byte)Math.Clamp(
                    MathF.Round(weights[pixel * channels + channel] / Math.Max(total, .0001f) * 255),
                    0, 255);
                if (channel < 4) a[pixel * 4 + channel] = value;
                else if (channel < 8) b[pixel * 4 + channel - 4] = value;
                else if (channel < 12) c[pixel * 4 + channel - 8] = value;
                else d[pixel * 4 + channel - 12] = value;
            }
        }

        // The spare alpha channel carries signed distance to the generated
        // coastline. Positive values are water, negative values are land.
        // This lets shoreline animation travel beyond the narrow biome blend.
        var waterPixels = new bool[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var tile = _island!.Tiles[
                Math.Min(IslandMap.Size - 1, y / samplesPerTile) * IslandMap.Size +
                Math.Min(IslandMap.Size - 1, x / samplesPerTile)];
            waterPixels[y * size + x] = tile.Biome is
                Biome.DeepWater or Biome.ShallowWater or
                Biome.RiverWater or Biome.MangroveShallows;
        }
        var distanceToWater = DistanceTo(targetWater: true);
        var distanceToLand = DistanceTo(targetWater: false);
        const float encodedRangeTiles = 8;
        for (var pixel = 0; pixel < size * size; pixel++)
        {
            var signedSamples = waterPixels[pixel] ? distanceToLand[pixel] : -distanceToWater[pixel];
            var signedTiles = signedSamples / samplesPerTile;
            shore[pixel] = (byte)Math.Clamp(
                MathF.Round((signedTiles / encodedRangeTiles * .5f + .5f) * 255), 0, 255);
        }

        return (UploadWeightTexture(a, PixelInternalFormat.Rgba8, PixelFormat.Rgba),
            UploadWeightTexture(b, PixelInternalFormat.Rgba8, PixelFormat.Rgba),
            UploadWeightTexture(c, PixelInternalFormat.Rgba8, PixelFormat.Rgba),
            UploadWeightTexture(d, PixelInternalFormat.Rgba8, PixelFormat.Rgba),
            UploadWeightTexture(shore, PixelInternalFormat.R8, PixelFormat.Red));

        float[] DistanceTo(bool targetWater)
        {
            const float diagonal = 1.41421356f;
            var distance = new float[size * size];
            for (var i = 0; i < distance.Length; i++)
                distance[i] = waterPixels[i] == targetWater ? 0 : 100000;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var index = y * size + x;
                if (x > 0) distance[index] = Math.Min(distance[index], distance[index - 1] + 1);
                if (y > 0) distance[index] = Math.Min(distance[index], distance[index - size] + 1);
                if (x > 0 && y > 0)
                    distance[index] = Math.Min(distance[index], distance[index - size - 1] + diagonal);
                if (x + 1 < size && y > 0)
                    distance[index] = Math.Min(distance[index], distance[index - size + 1] + diagonal);
            }
            for (var y = size - 1; y >= 0; y--)
            for (var x = size - 1; x >= 0; x--)
            {
                var index = y * size + x;
                if (x + 1 < size) distance[index] = Math.Min(distance[index], distance[index + 1] + 1);
                if (y + 1 < size) distance[index] = Math.Min(distance[index], distance[index + size] + 1);
                if (x + 1 < size && y + 1 < size)
                    distance[index] = Math.Min(distance[index], distance[index + size + 1] + diagonal);
                if (x > 0 && y + 1 < size)
                    distance[index] = Math.Min(distance[index], distance[index + size - 1] + diagonal);
            }
            return distance;
        }

        int UploadWeightTexture(
            byte[] data, PixelInternalFormat internalFormat, PixelFormat format)
        {
            var texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, size, size, 0,
                format, PixelType.UnsignedByte, data);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            return texture;
        }
    }

    protected override void OnUnload()
    {
        SaveActivePlayerState();
        _pathCancellation?.Cancel();
        _pathCancellation?.Dispose();
        foreach (var coordinate in _worldChunks.Keys.ToArray())
            UnloadWorldChunk(coordinate, save: true);
        Exception? saveFailure = null;
        try
        {
            _saveTail.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            saveFailure = ex;
        }
        foreach (var texture in _textures) GL.DeleteTexture(texture);
        foreach (var texture in _terrainTextures) GL.DeleteTexture(texture);
        foreach (var texture in _entityAnimations.Values
                     .SelectMany(value => value.Textures).Distinct())
            GL.DeleteTexture(texture);
        if (_moveMarkerAnimation is not null)
        foreach (var texture in _moveMarkerAnimation.Textures)
            GL.DeleteTexture(texture);
        Cursor = MouseCursor.Default;
        if (_uiPanelFillTexture != 0) GL.DeleteTexture(_uiPanelFillTexture);
        if (_uiSolidTexture != 0) GL.DeleteTexture(_uiSolidTexture);
        if (_woodcuttingItemsTexture != 0)
            GL.DeleteTexture(_woodcuttingItemsTexture);
        foreach (var texture in _woodcuttingItemTextures)
            if (texture != 0) GL.DeleteTexture(texture);
        foreach (var texture in _naturalItemTextures)
            if (texture != 0) GL.DeleteTexture(texture);
        foreach (var texture in _stoneToolTextures)
            if (texture != 0) GL.DeleteTexture(texture);
        foreach (var texture in _coastalSprites.Textures)
            if (texture != 0) GL.DeleteTexture(texture);
        foreach (var texture in _coastalSprites.GroundTextures)
            if (texture != 0) GL.DeleteTexture(texture);
        foreach (var texture in _fishItemTextures)
            if (texture != 0) GL.DeleteTexture(texture);
        foreach (var tool in _groundToolSprites.Values)
            if (tool.Texture != 0) GL.DeleteTexture(tool.Texture);
        foreach (var texture in _fibreNetSprites.Textures)
            if (texture != 0) GL.DeleteTexture(texture);
        foreach (var placeable in _placeableObjectSprites.All)
            if (placeable.Value.Texture != 0)
                GL.DeleteTexture(placeable.Value.Texture);
        if (_uiTabTexture != 0) GL.DeleteTexture(_uiTabTexture);
        if (_uiActiveTabTexture != 0) GL.DeleteTexture(_uiActiveTabTexture);
        if (_minimapTexture != 0) GL.DeleteTexture(_minimapTexture);
        if (_newWorldPreviewTexture != 0)
            GL.DeleteTexture(_newWorldPreviewTexture);
        _fontSystem?.Dispose();
        _fontRenderer?.Dispose();
        if (_terrainArray != 0) GL.DeleteTexture(_terrainArray);
        if (_biomeWeightsA != 0) GL.DeleteTexture(_biomeWeightsA);
        if (_biomeWeightsB != 0) GL.DeleteTexture(_biomeWeightsB);
        if (_biomeWeightsC != 0) GL.DeleteTexture(_biomeWeightsC);
        if (_biomeWeightsD != 0) GL.DeleteTexture(_biomeWeightsD);
        if (_shoreDistance != 0) GL.DeleteTexture(_shoreDistance);
        if (_waterNormalArray != 0) GL.DeleteTexture(_waterNormalArray);
        foreach (var texture in _atlasTileTextures.Values) GL.DeleteTexture(texture);
        if (_islandVbo != 0) GL.DeleteBuffer(_islandVbo);
        if (_streamVbo != 0) GL.DeleteBuffer(_streamVbo);
        if (_sceneFramebuffer != 0) GL.DeleteFramebuffer(_sceneFramebuffer);
        if (_sceneColor != 0) GL.DeleteTexture(_sceneColor);
        if (_pauseBlurTexture != 0) GL.DeleteTexture(_pauseBlurTexture);
        if (_pauseBlurIntermediate != 0)
            GL.DeleteTexture(_pauseBlurIntermediate);
        if (_pauseBlurFramebuffer != 0)
            GL.DeleteFramebuffer(_pauseBlurFramebuffer);
        if (_treeBatchVbo != 0) GL.DeleteBuffer(_treeBatchVbo);
        if (_treeAtlasTexture != 0) GL.DeleteTexture(_treeAtlasTexture);
        if (_cliffBatchVbo != 0) GL.DeleteBuffer(_cliffBatchVbo);
        if (_cliffTexture != 0) GL.DeleteTexture(_cliffTexture);
        if (_terrainProgram != 0) GL.DeleteProgram(_terrainProgram);
        if (_cliffProgram != 0) GL.DeleteProgram(_cliffProgram);
        GL.DeleteVertexArray(_vao);
        if (_pauseBlurProgram != 0) GL.DeleteProgram(_pauseBlurProgram);
        GL.DeleteProgram(_program);
        base.OnUnload();
        if (saveFailure is not null)
            throw new IOException("One or more chunks could not be saved during shutdown.", saveFailure);
    }
}
