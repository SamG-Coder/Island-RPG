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

internal sealed class GameHostWindow : GameWindow
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
        int weightsA, int weightsB, int weightsC, int weightsD, int shoreDistance)
    {
        public WorldChunk Chunk { get; } = chunk;
        public int Vbo { get; } = vbo;
        public int VertexCount { get; } = vertexCount;
        public int WeightsA { get; } = weightsA;
        public int WeightsB { get; } = weightsB;
        public int WeightsC { get; } = weightsC;
        public int WeightsD { get; } = weightsD;
        public int ShoreDistance { get; } = shoreDistance;
        public float Opacity { get; set; }
    }
    private sealed record SpriteAtlasEntry(
        SpriteFrame Frame, float U0, float V0, float U1, float V1);
    private sealed record EntityAnimation(
        LoadedGraphic Graphic, int[] Textures, float SecondsPerFrame);
    private sealed record PlayerVisual(
        SpriteFrame Frame, int Texture, Vector2 World, bool Mirror, bool Wading);
    private sealed record MoveMarker(Vector2 Position, double Time);
    private sealed record WaterRipple(Vector2 Position, double StartedAt);
    private enum WorldActionType { CutTree }
    private sealed record QueuedWorldAction(
        WorldActionType Type, Vector2 Target, float Range);
    private sealed record PathResult(
        int RequestId,
        IReadOnlyList<Vector2> Path,
        QueuedWorldAction? Action = null);
    private sealed record NewWorldPreviewResult(
        string SeedText, long Seed, Vector2 Spawn, byte[] Pixels);

    private readonly string _install;
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
    private string? _pendingDeletePlayerId;
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
    private int _treeBatchVbo;
    private int _treeAtlasTexture;
    private int _cliffBatchVbo;
    private int _cliffTexture;
    private readonly Dictionary<string, SpriteAtlasEntry> _treeAtlas =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(EntityGender Gender, EntityAction Action), EntityAnimation>
        _entityAnimations = [];
    private EntityAnimation? _moveMarkerAnimation;
    private MouseCursor? _defaultNativeCursor;
    private MouseCursor? _cutNativeCursor;
    private bool _usingCutCursor;
    private int _uiPanelFillTexture;
    private int _uiSolidTexture;
    private SpriteFrame? _uiTabFrame;
    private int _uiTabTexture;
    private int _uiActiveTabTexture;
    private int _woodcuttingItemsTexture;
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
    private int _lastTreeStrike;
    private readonly List<WaterRipple> _waterRipples = [];
    private int _lastWaterFootfall = -1;
    private WorldEntity? _player;
    private bool _gameLeftWasDown;
    private bool _gameRightWasDown;
    private bool _inventoryRightWasDown;
    private bool _inventoryLeftWasDown;
    private readonly GameUiControlState _gameUi = new();
    private readonly ChatUiControlState _chatUi = new();
    private readonly ContextMenuControlState _inventoryContext = new();
    private int _inventoryContextSlot = -1;
    private int _activeInventorySlot = -1;
    private int _inventoryPressedSlot = -1;
    private int _inventoryDraggingSlot = -1;
    private Vector2 _inventoryPressPosition;
    private FontSystem? _fontSystem;
    private DynamicSpriteFont? _chatFont;
    private OpenGlFontRenderer? _fontRenderer;
    private float _chatLineHeight = 16;
    private int _vao;
    private bool _dragging;
    private Vector2 _lastMouse;
    private Vector2 _camera;
    private float _zoom = 1;
    private float _waterTime;

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
        _inventoryContext.Selected += HandleInventoryContextSelection;
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _program = CreateProgram();
        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);
        _streamVbo = GL.GenBuffer();
        CreateSceneTarget();
        PrepareGameUi();
        var settings = _saves.LoadSettings();
        if (settings.Fullscreen) WindowState = WindowState.Fullscreen;
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
        }

        if (mode == PreviewMode.Game)
        {
            foreach (var name in new[]
            {
                "VMBAS_WN", "VMBAS_AN", "VMBAS_DN",
                "VFBAS_WN", "VFBAS_AN", "VFBAS_DN",
                "VMLUM_AN", "VFLUM_AN", "MOVEX_NN"
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
            if (_screen == ScreenState.MainMenu &&
                _frontendPage != FrontendPage.Main)
            {
                _frontendPage = FrontendPage.Main;
                BlurTextBoxes();
                _frontendError = null;
            }
            else if (_chatUi.Input.Focused) _chatUi.BlurInput();
            else Close();
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
                            Title = "Island RPG";
                            return;
                        }
                        _worldStore = new WorldChunkStore(_worldSeed);
                        StreamWorld();
                    }
                    _screen = ScreenState.WorldPreview;
                    Title = _mode == PreviewMode.Island
                        ? "Island RPG - Generated Island"
                        : _mode == PreviewMode.Game
                            ? $"Island RPG - Game {_worldSeed}"
                            : $"Island RPG - World {_worldSeed}";
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
                {
                    _screen = ScreenState.WorldPreview;
                    Title = $"Island RPG - {_worldAssets.Count} world graphics + {_catalog.TerrainTiles.Count} terrain tiles";
                }
            }
        }

        if (_screen == ScreenState.MainMenu)
        {
            UpdateMenuPreview((float)e.Time);
            UpdateFrontend();
        }

        if (_screen == ScreenState.WorldPreview)
        {
            if (_atlasOpen)
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
            _pendingDeletePlayerId = null;
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
                    _frontendPage = FrontendPage.Settings;
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
                if (SettingsToggleBounds().Contains(pointer))
                {
                    var settings = _saves.LoadSettings();
                    var fullscreen = !settings.Fullscreen;
                    _saves.SaveSettings(settings with { Fullscreen = fullscreen });
                    WindowState = fullscreen
                        ? WindowState.Fullscreen
                        : WindowState.Normal;
                }
                else if (BackButtonBounds().Contains(pointer))
                    _frontendPage = FrontendPage.Main;
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
        var players = _saves.ListPlayers();
        for (var index = 0; index < Math.Min(6, players.Count); index++)
        {
            if (CharacterRowBounds(index).Contains(pointer))
            {
                _selectedPlayer = players[index];
                return;
            }
            if (!DeleteCharacterBounds(index).Contains(pointer)) continue;
            if (_pendingDeletePlayerId != players[index].Id)
            {
                _pendingDeletePlayerId = players[index].Id;
                return;
            }
            var deletingSelected = _selectedPlayer?.Id == players[index].Id;
            _saves.DeletePlayer(players[index].Id);
            _pendingDeletePlayerId = null;
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
        var worlds = _saves.ListWorlds();
        for (var index = 0; index < Math.Min(6, worlds.Count); index++)
        {
            if (!LoadWorldRowBounds(index).Contains(pointer)) continue;
            if (_selectedPlayer is null)
            {
                _frontendPage = FrontendPage.CharacterCreate;
                FocusTextBoxAtEnd(_playerNameTextBox);
            }
            else
                EnterWorld(worlds[index], _selectedPlayer);
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
        if (!PlayerInventory.HasAxe(normalizedInventory) &&
            PlayerInventory.TryAdd(
                normalizedInventory, PlayerInventory.AxeItemId,
                out var migratedInventory))
        {
            normalizedInventory = migratedInventory;
            inventoryMigrated = true;
        }
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
        Title = $"Island RPG - {world.Name}";
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
                    192, 120, PixelFormat.Rgba,
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
        const int width = 192;
        const int height = 120;
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
            var (red, green, blue) = MinimapColor(
                InfiniteWorldGenerator.BiomeAt(seed, worldX, worldY));
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
        if (_pendingPathTask is { IsCompleted: true })
        {
            if (_pendingPathTask.IsFaulted)
                throw _pendingPathTask.Exception?.GetBaseException() ??
                      new InvalidOperationException("Path calculation failed.");
            if (_pendingPathTask.IsCompletedSuccessfully)
            {
                var result = _pendingPathTask.Result;
                if (result.RequestId == _pathRequestId)
                {
                    _queuedAction = result.Action;
                    _player.FollowPath(result.Path);
                }
            }
            _pendingPathTask = null;
        }

        var rightDown = MouseState.IsButtonDown(MouseButton.Right);
        if (rightDown && !_gameRightWasDown &&
            !IsPointerOverGameUi(MouseState.Position))
        {
            var target = ScreenToTerrain(SceneMousePosition());
            _queuedAction = null;
            _activeTreeId = null;
            _player.Stop();
            _pathCancellation?.Cancel();
            _pathCancellation?.Dispose();
            _pathCancellation = new CancellationTokenSource();
            var token = _pathCancellation.Token;
            var requestId = ++_pathRequestId;
            var start = _player.Position;
            _pendingPathTask = Task.Run(
                () => new PathResult(
                    requestId,
                    GridPathfinder.Find(_worldSeed, start, target, cancellationToken: token)),
                token);
            _moveMarker = new(target, 0);
        }
        _gameRightWasDown = rightDown;

        var leftDown = MouseState.IsButtonDown(MouseButton.Left);
        if (leftDown && !_gameLeftWasDown &&
            !IsPointerOverGameUi(MouseState.Position) &&
            TryGetTreeUnderMouse(SceneMousePosition(), out var actionTree))
        {
            var actionTarget = new Vector2(actionTree.X + .5f, actionTree.Y + .5f);
            var standOff = TreeInteractionDistance(actionTree.GraphicName);
            _activeTreeId = null;
            _player.Stop();
            _pathCancellation?.Cancel();
            _pathCancellation?.Dispose();
            _pathCancellation = new CancellationTokenSource();
            var token = _pathCancellation.Token;
            var requestId = ++_pathRequestId;
            var start = _player.Position;
            _queuedAction = null;
            _pendingPathTask = Task.Run(
                () => FindActionPath(
                    requestId, start, actionTarget, standOff,
                    WorldActionType.CutTree, token),
                token);
            _moveMarker = null;
        }
        _gameLeftWasDown = leftDown;

        var currentHeight = InfiniteWorldGenerator.SampleRenderedHeight(
            _worldSeed, _player.Position.X, _player.Position.Y);
        var nextHeight = InfiniteWorldGenerator.SampleRenderedHeight(
            _worldSeed, _player.Target.X, _player.Target.Y);
        var uphill = Math.Max(0, nextHeight - currentHeight);
        var playerBiome = InfiniteWorldGenerator.BiomeAt(
            _worldSeed,
            (int)MathF.Floor(_player.Position.X),
            (int)MathF.Floor(_player.Position.Y));
        var wading = playerBiome is Biome.ShallowWater or
            Biome.RiverWater or Biome.MangroveShallows;
        _player.TerrainSpeedMultiplier =
            (wading ? .62f : 1f) / (1f + uphill * .18f);
        _player.Update(elapsed);
        UpdateNativeCursor();
        CompleteQueuedAction();
        UpdateTreeCutting();
        UpdateWaterRipples(wading);
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
        Title = $"Island RPG - {_player.Gender} villager - {_player.Action} - " +
                (_pendingPathTask is null ? "" : "calculating path - ") +
                "right-click to move, left-click to act, Up/Down changes villager";
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
        var leftDown = MouseState.IsButtonDown(MouseButton.Left);
        UpdateInventoryDrag(MouseState.Position, leftDown);
        _inventoryLeftWasDown = leftDown;
        var rightDown = MouseState.IsButtonDown(MouseButton.Right);
        if (rightDown && !_inventoryRightWasDown &&
            _gameUi.ActivePanel == GameUiPanel.Inventory)
        {
            var inventory = _activePlayer?.Inventory ?? [];
            for (var slot = 0; slot < PlayerInventory.Capacity; slot++)
            {
                if (slot >= inventory.Length ||
                    inventory[slot] is null)
                    continue;
                if (!InventorySlotBounds(_gameUi.Panel.Bounds, slot)
                        .Contains(MouseState.Position))
                    continue;
                _inventoryContextSlot = slot;
                _inventoryContext.Open(
                    MouseState.Position,
                    ["Use", "Drop", "Examine"],
                    scene);
                break;
            }
        }
        _inventoryRightWasDown = rightDown;
        if (_chatUi.Input.Focused)
        {
            if (KeyboardState.IsKeyPressed(Keys.Backspace)) _chatUi.Backspace();
            if (KeyboardState.IsKeyPressed(Keys.Enter)) _chatUi.Submit();
        }
    }

    private bool IsPointerOverGameUi(Vector2 mouse) =>
        _gameUi.BlocksWorldInput(mouse) ||
        _chatUi.BlocksWorldInput(mouse) ||
        _inventoryContext.HitTest(mouse) ||
        _inventoryDraggingSlot >= 0 ||
        _minimapUi.HitTest(mouse);

    private PathResult FindActionPath(
        int requestId,
        Vector2 start,
        Vector2 target,
        float standOff,
        WorldActionType actionType,
        CancellationToken cancellationToken)
    {
        var targetCell = new Vector2i(
            (int)MathF.Floor(target.X),
            (int)MathF.Floor(target.Y));
        var candidates = new List<Vector2>(8);
        for (var y = -1; y <= 1; y++)
        for (var x = -1; x <= 1; x++)
        {
            if (x == 0 && y == 0) continue;
            candidates.Add(new Vector2(
                targetCell.X + x + .5f,
                targetCell.Y + y + .5f));
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
            var standPosition = target + approach.Normalized() * finalDistance;
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
                    Math.Max(standOff, .72f) + .08f));
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

    private void CompleteQueuedAction()
    {
        if (_player is null || _queuedAction is null ||
            _player.Action == EntityAction.Move)
            return;

        var action = _queuedAction;
        _queuedAction = null;
        if ((_player.Position - action.Target).Length > action.Range)
            return;

        if (action.Type == WorldActionType.CutTree)
            BeginTreeCutting(action.Target);
    }

    private void BeginTreeCutting(Vector2 target)
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
            _chatUi.AddMessage(
                "You need an axe to chop down this tree.",
                ChatMessageStyle.Warning);
            _player.Stop();
            return;
        }
        if (PlayerInventory.IsFull(_activePlayer?.Inventory))
        {
            _chatUi.AddMessage(
                "Your inventory is full. You cannot begin woodcutting.",
                ChatMessageStyle.Warning);
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

    private void UpdateTreeCutting()
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
            var experience = _activePlayer?.WoodcuttingExperience ?? 0;
            var strikeResult = WoodcuttingSkill.Roll(
                experience,
                Random.Shared.NextSingle(),
                Random.Shared.NextSingle());
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
        var previousLevel = WoodcuttingSkill.LevelForExperience(
            _activePlayer.WoodcuttingExperience);
        var maximumExperience = WoodcuttingSkill.ExperienceForLevel(
            WoodcuttingSkill.MaximumLevel);
        var experience = Math.Min(
            maximumExperience,
            _activePlayer.WoodcuttingExperience + amount);
        _activePlayer = _activePlayer with
        {
            WoodcuttingExperience = experience,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        var level = WoodcuttingSkill.LevelForExperience(experience);
        _chatUi.AddMessage(
            $"+{amount} Woodcutting XP.", ChatMessageStyle.Experience);
        if (level > previousLevel)
            _chatUi.AddMessage(
                $"Your Woodcutting level is now {level}.",
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

    private static (string Id, string Name) TreeLogItem(string treeType)
    {
        if (treeType.StartsWith("FOAK", StringComparison.OrdinalIgnoreCase))
            return ("oak_logs", "oak logs");
        if (treeType.StartsWith("FPIN", StringComparison.OrdinalIgnoreCase))
            return ("pine_logs", "pine logs");
        if (treeType.StartsWith("FPAL", StringComparison.OrdinalIgnoreCase))
            return ("palm_logs", "palm logs");
        if (treeType.StartsWith("FBAM", StringComparison.OrdinalIgnoreCase))
            return ("bamboo", "bamboo");
        return ("logs", "logs");
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
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "rippleCount"), count);
        for (var index = 0; index < count; index++)
        {
            var ripple = _waterRipples[_waterRipples.Count - count + index];
            GL.Uniform2(
                GL.GetUniformLocation(_terrainProgram, $"ripplePositions[{index}]"),
                ripple.Position.X / 8f,
                ripple.Position.Y / 8f);
            GL.Uniform1(
                GL.GetUniformLocation(_terrainProgram, $"rippleAges[{index}]"),
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
        var mapCenter = ScreenWorldToMap(-_camera / Math.Max(_zoom, .001f));
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
            visible.Add(new(x, y, chunksPerTile));
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
        if (_screen != ScreenState.WorldPreview || e.OffsetY == 0) return;
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
        GL.Uniform1(GL.GetUniformLocation(_program, "image"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "opacity"), 1f);
        GL.Uniform1(GL.GetUniformLocation(_program, "outlineOnly"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "wading"), 0);
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
            RenderGameUi();
        }
        SwapBuffers();
    }

    private void RenderLoading()
    {
        GL.ClearColor(.025f, .027f, .024f, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        Title = $"Island RPG - Loading {_done}/{_total}: {_current}";
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
            "CREATE CHARACTER", new(panel.X, panel.Y + 20, panel.Z, 38),
            new(232, 217, 166, 255));

        var preview = CharacterPreviewBounds();
        DrawUiColor(preview, new(.025f, .027f, .024f, .72f));
        DrawPanelOutline(preview, 0, new(.28f, .23f, .14f, 1));
        DrawCharacterPreview(
            _newPlayerGender, _newTeamColor, preview);

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
                new(panel.X + 350, panel.Y + panel.W - 116, 340, 24),
                new(220, 104, 82, 255));
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
        var players = _saves.ListPlayers();
        for (var index = 0; index < Math.Min(6, players.Count); index++)
        {
            var player = players[index];
            var row = CharacterRowBounds(index);
            DrawMenuButton(
                row, player.Name,
                TeamColor(player.TeamColor));
            if (_selectedPlayer?.Id == player.Id)
                DrawPanelOutline(row, 3, new(.62f, .48f, .20f, 1));
            DrawMenuButton(
                DeleteCharacterBounds(index),
                _pendingDeletePlayerId == player.Id ? "Confirm?" : "Delete");
        }
        DrawMenuButton(NewCharacterButtonBounds(), "New Character");
        if (_selectedPlayer is not null)
            DrawMenuButton(ContinueCharacterButtonBounds(), "Use Character");
        DrawMenuButton(BackButtonBounds(), "Back");
    }

    private void RenderNewWorldMenu()
    {
        var panel = FrontendPanel(560, 590);
        DrawAoEPanelBorder(panel);
        DrawCenteredUiText(
            "CREATE NEW WORLD", new(panel.X, panel.Y + 22, panel.Z, 38),
            new(232, 217, 166, 255));

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

        if (_selectedPlayer is not null)
            DrawCenteredUiText(
                $"Character: {_selectedPlayer.Name}",
                new(panel.X + 48, panel.Y + 285, panel.Z - 96, 42),
                new(204, 190, 150, 255));
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
                new(panel.X + 30, panel.Y + panel.W - 112, panel.Z - 60, 24),
                new(220, 104, 82, 255));
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
        var worlds = _saves.ListWorlds();
        if (worlds.Count == 0)
        {
            DrawCenteredUiText(
                "No worlds have been created yet.",
                new(panel.X + 30, panel.Y + 150, panel.Z - 60, 30),
                new(204, 190, 150, 255));
        }
        for (var index = 0; index < Math.Min(6, worlds.Count); index++)
        {
            var world = worlds[index];
            var row = LoadWorldRowBounds(index);
            DrawMenuButton(row, world.Name);
            DrawUiText(
                $"Seed {world.Seed}",
                new(row.X + 16, row.Y + row.W - 17),
                new(158, 148, 120, 255));
        }
        DrawMenuButton(BackButtonBounds(), "Back");
    }

    private void RenderSettingsMenu()
    {
        var panel = FrontendPanel(480, 360);
        DrawAoEPanelBorder(panel);
        DrawCenteredUiText(
            "SETTINGS", new(panel.X, panel.Y + 24, panel.Z, 38),
            new(232, 217, 166, 255));
        var fullscreen = _saves.LoadSettings().Fullscreen;
        DrawMenuButton(
            SettingsToggleBounds(),
            $"Fullscreen: {(fullscreen ? "On" : "Off")}");
        DrawCenteredUiText(
            "More audio and display settings can be added here.",
            new(panel.X + 20, panel.Y + 190, panel.Z - 40, 28),
            new(174, 164, 134, 255));
        DrawMenuButton(BackButtonBounds(), "Back");
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

    private void DrawCenteredUiText(
        string text, Vector4 bounds, FSColor color) =>
        DrawUiText(text, CenteredTextPosition(text, bounds), color);

    private Vector4 FrontendPanel(float width, float height) =>
        new(
            MathF.Round((ClientSize.X - width) * .5f),
            MathF.Round((ClientSize.Y - height) * .5f),
            width, height);

    private Vector4 FrontendCloseButtonBounds()
    {
        var panel = _frontendPage switch
        {
            FrontendPage.CharacterCreate => FrontendPanel(760, 640),
            FrontendPage.CharacterSelect => FrontendPanel(660, 600),
            FrontendPage.NewWorld => FrontendPanel(560, 590),
            FrontendPage.LoadWorld => FrontendPanel(600, 560),
            FrontendPage.Settings => FrontendPanel(480, 360),
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
        var panel = FrontendPanel(560, 590);
        return new(
            panel.X + 48,
            panel.Y + 98 + index * 82,
            index == 1 ? 340 : panel.Z - 96,
            44);
    }

    private Vector4 RandomSeedButtonBounds()
    {
        var seed = NewWorldFieldBounds(1);
        return new(seed.X + seed.Z + 8, seed.Y, 116, seed.W);
    }

    private Vector4 NewWorldPreviewBounds()
    {
        var panel = FrontendPanel(560, 590);
        return new(panel.X + 48, panel.Y + 350, panel.Z - 96, 136);
    }

    private Vector4 NewWorldOptionBounds(int index)
    {
        var panel = FrontendPanel(560, 590);
        const float gap = 8;
        var width = (panel.Z - 96 - gap * 2) / 3;
        return new(
            panel.X + 48 + index * (width + gap),
            panel.Y + 362, width, 46);
    }

    private Vector4 CreateWorldButtonBounds()
    {
        var panel = FrontendPanel(560, 590);
        return new(panel.X + 48, panel.Y + panel.W - 92, 290, 48);
    }

    private Vector4 BackButtonBounds()
    {
        var panel = _frontendPage switch
        {
            FrontendPage.CharacterCreate => FrontendPanel(760, 640),
            FrontendPage.CharacterSelect => FrontendPanel(660, 600),
            FrontendPage.NewWorld => FrontendPanel(560, 590),
            FrontendPage.LoadWorld => FrontendPanel(600, 560),
            _ => FrontendPanel(480, 360)
        };
        return new(panel.X + panel.Z - 156, panel.Y + panel.W - 92, 108, 48);
    }

    private Vector4 LoadWorldRowBounds(int index)
    {
        var panel = FrontendPanel(600, 560);
        return new(panel.X + 48, panel.Y + 88 + index * 62, panel.Z - 96, 54);
    }

    private Vector4 SettingsToggleBounds()
    {
        var panel = FrontendPanel(480, 360);
        return new(panel.X + 60, panel.Y + 104, panel.Z - 120, 50);
    }

    private Vector4 CharacterPreviewBounds()
    {
        var panel = FrontendPanel(760, 640);
        return new(panel.X + 42, panel.Y + 76, 278, 470);
    }

    private Vector4 CharacterNameBounds()
    {
        var panel = FrontendPanel(760, 640);
        return new(panel.X + 358, panel.Y + 105, 350, 46);
    }

    private Vector4 GenderButtonBounds()
    {
        var panel = FrontendPanel(760, 640);
        return new(panel.X + 358, panel.Y + 193, 168, 46);
    }

    private Vector4 TeamSwatchBounds(int index)
    {
        var panel = FrontendPanel(760, 640);
        return new(
            panel.X + 358 + index % 4 * 56,
            panel.Y + 306 + index / 4 * 56,
            44, 44);
    }

    private Vector4 CreateCharacterButtonBounds()
    {
        var panel = FrontendPanel(760, 640);
        return new(panel.X + 358, panel.Y + panel.W - 78, 210, 48);
    }

    private Vector4 CharacterRowBounds(int index)
    {
        var panel = FrontendPanel(660, 600);
        return new(panel.X + 48, panel.Y + 82 + index * 66, 430, 54);
    }

    private Vector4 DeleteCharacterBounds(int index)
    {
        var row = CharacterRowBounds(index);
        return new(row.X + row.Z + 10, row.Y, 124, row.W);
    }

    private Vector4 NewCharacterButtonBounds()
    {
        var panel = FrontendPanel(660, 600);
        return new(panel.X + 48, panel.Y + panel.W - 82, 176, 48);
    }

    private Vector4 ContinueCharacterButtonBounds()
    {
        var panel = FrontendPanel(660, 600);
        return new(panel.X + 238, panel.Y + panel.W - 82, 190, 48);
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
        var frame = animation.Graphic.Sprite.Frames[0];
        var texture = animation.Textures[0];
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
        var scene = SceneClientBounds();
        _gameUi.Layout(scene);
        _chatUi.Layout(scene);
        _minimapUi.Layout(scene);
        RenderTreeHealthBars(scene);
        RenderMinimap();
        RenderChatUi();
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
    }

    private void RenderSkillsPanel()
    {
        var panel = _gameUi.Panel.Bounds;
        var experience = _activePlayer?.WoodcuttingExperience ?? 0;
        var level = WoodcuttingSkill.LevelForExperience(experience);
        var currentFloor = WoodcuttingSkill.ExperienceForLevel(level);
        var nextFloor = level >= WoodcuttingSkill.MaximumLevel
            ? currentFloor
            : WoodcuttingSkill.ExperienceForLevel(level + 1);
        var progress = level >= WoodcuttingSkill.MaximumLevel
            ? 1f
            : (experience - currentFloor) /
              (float)Math.Max(1, nextFloor - currentFloor);

        DrawPanelCaption("Skills", panel);
        DrawUiText(
            "Woodcutting",
            new System.Numerics.Vector2(panel.X + 18, panel.Y + 58),
            new(229, 218, 177, 255));
        DrawUiText(
            $"Level {level}/{WoodcuttingSkill.MaximumLevel}",
            new System.Numerics.Vector2(panel.X + 18, panel.Y + 82),
            new(205, 194, 158, 255));

        var track = new Vector4(panel.X + 18, panel.Y + 112, panel.Z - 36, 18);
        DrawUiColor(track, new(.035f, .032f, .027f, .98f));
        if (progress > 0)
            DrawUiColor(
                new(track.X + 2, track.Y + 2,
                    MathF.Round((track.Z - 4) * progress), track.W - 4),
                new(.37f, .50f, .18f, 1));
        DrawPanelOutline(track, 0, new(.25f, .21f, .13f, 1));
        var progressText = level >= WoodcuttingSkill.MaximumLevel
            ? "Maximum level"
            : $"{experience - currentFloor}/{nextFloor - currentFloor} XP";
        DrawCenteredUiText(
            progressText, track, new(234, 224, 186, 255));
        DrawUiText(
            level >= WoodcuttingSkill.MaximumLevel
                ? $"Total XP: {experience}"
                : $"{WoodcuttingSkill.ExperienceToNextLevel(experience)} XP to next level",
            new System.Numerics.Vector2(panel.X + 18, panel.Y + 145),
            new(190, 181, 150, 255));
        DrawUiText(
            $"Hit chance: {WoodcuttingSkill.HitChance(level) * 100:0}%",
            new System.Numerics.Vector2(panel.X + 18, panel.Y + 174),
            new(190, 181, 150, 255));
    }

    private void RenderInventoryPanel()
    {
        var panel = _gameUi.Panel.Bounds;
        var inventory = _activePlayer?.Inventory ?? [];
        var count = PlayerInventory.Count(inventory);
        DrawPanelCaption("Bag", panel);
        DrawUiText(
            $"{count}/{PlayerInventory.Capacity}",
            new System.Numerics.Vector2(panel.X + panel.Z - 48, panel.Y + 13),
            count >= PlayerInventory.Capacity
                ? new(228, 135, 108, 255)
                : new(184, 177, 149, 255));

        for (var slot = 0; slot < PlayerInventory.Capacity; slot++)
        {
            var bounds = InventorySlotBounds(panel, slot);
            if (slot >= inventory.Length ||
                inventory[slot] is not { } itemId ||
                slot == _inventoryDraggingSlot)
                continue;
            var itemUv = InventoryItemUv(itemId);
            var hasSprite =
                _woodcuttingItemsTexture != 0 && itemUv is not null;
            if (hasSprite)
                DrawUiSprite(
                    WoodcuttingItemsFrame,
                    _woodcuttingItemsTexture,
                    bounds,
                    uvRectangle: itemUv,
                    spriteOutline: slot == _activeInventorySlot
                        ? Vector3.One
                        : Vector3.Zero);
            else
                DrawCenteredUiText(
                    InventoryItemCaption(itemId),
                    bounds, new(211, 198, 158, 255));
            if (slot == _activeInventorySlot && !hasSprite)
            {
                DrawPanelOutline(bounds, 0, new(.96f, .95f, .88f, 1));
                DrawPanelOutline(bounds, 1, new(.74f, .72f, .65f, 1));
            }
        }

        if ((uint)_inventoryDraggingSlot < (uint)inventory.Length &&
            inventory[_inventoryDraggingSlot] is { } draggedItemId)
        {
            var itemUv = InventoryItemUv(draggedItemId);
            var dragBounds = new Vector4(
                MouseState.Position.X - 16,
                MouseState.Position.Y - 16, 32, 32);
            if (_woodcuttingItemsTexture != 0 && itemUv is not null)
                DrawUiSprite(
                    WoodcuttingItemsFrame,
                    _woodcuttingItemsTexture,
                    dragBounds,
                    uvRectangle: itemUv,
                    drawOpacity: .62f,
                    spriteOutline:
                    _inventoryDraggingSlot == _activeInventorySlot
                        ? Vector3.One
                        : Vector3.Zero);
            else
            {
                DrawCenteredUiText(
                    InventoryItemCaption(draggedItemId), dragBounds,
                    new(211, 198, 158, 180));
                DrawPanelOutline(
                    dragBounds, 0, new(.96f, .95f, .88f, .7f));
            }
        }
    }

    private void UpdateInventoryDrag(Vector2 pointer, bool leftDown)
    {
        if (_gameUi.ActivePanel != GameUiPanel.Inventory)
        {
            _inventoryPressedSlot = -1;
            _inventoryDraggingSlot = -1;
            return;
        }

        if (leftDown && !_inventoryLeftWasDown)
        {
            if (_inventoryContext.Visible) return;
            _inventoryPressedSlot = InventorySlotAt(
                pointer, includeEmpty: false);
            _inventoryPressPosition = pointer;
            return;
        }

        if (leftDown)
        {
            if (_inventoryPressedSlot >= 0 &&
                _inventoryDraggingSlot < 0 &&
                (pointer - _inventoryPressPosition).LengthSquared >= 16)
                _inventoryDraggingSlot = _inventoryPressedSlot;
            return;
        }

        if (_inventoryLeftWasDown)
        {
            if (_inventoryDraggingSlot >= 0)
            {
                var target = InventorySlotAt(pointer, includeEmpty: true);
                SwapInventorySlots(_inventoryDraggingSlot, target);
            }
            else if (_inventoryPressedSlot >= 0 &&
                     InventorySlotAt(pointer, includeEmpty: false) ==
                     _inventoryPressedSlot)
                ActivateInventorySlot(_inventoryPressedSlot);
            else if (_inventoryPressedSlot < 0 &&
                     _gameUi.Panel.Bounds.Contains(pointer))
                _activeInventorySlot = -1;
        }

        _inventoryPressedSlot = -1;
        _inventoryDraggingSlot = -1;
    }

    private int InventorySlotAt(Vector2 pointer, bool includeEmpty)
    {
        var inventory = _activePlayer?.Inventory ?? [];
        for (var slot = 0; slot < PlayerInventory.Capacity; slot++)
        {
            if (!includeEmpty &&
                (slot >= inventory.Length || inventory[slot] is null))
                continue;
            if (InventorySlotBounds(_gameUi.Panel.Bounds, slot)
                    .Contains(pointer))
                return slot;
        }
        return -1;
    }

    private void SwapInventorySlots(int source, int target)
    {
        if (_activePlayer is null || source == target || target < 0)
            return;
        if (!PlayerInventory.TrySwap(
                _activePlayer.Inventory, source, target,
                out var inventory))
            return;
        _activePlayer = _activePlayer with
        {
            Inventory = inventory,
            UpdatedUtc = DateTime.UtcNow
        };
        if (_activeInventorySlot == source)
            _activeInventorySlot = target;
        else if (_activeInventorySlot == target)
            _activeInventorySlot = source;
        _saves.SavePlayer(_activePlayer);
    }

    private void ActivateInventorySlot(int slot)
    {
        var inventory = _activePlayer?.Inventory ?? [];
        if ((uint)slot >= (uint)inventory.Length ||
            inventory[slot] is null)
            return;
        if (_activeInventorySlot < 0 ||
            (uint)_activeInventorySlot >= (uint)inventory.Length ||
            inventory[_activeInventorySlot] is null)
        {
            _activeInventorySlot = slot;
            return;
        }

        var source = inventory[_activeInventorySlot]!;
        var target = inventory[slot]!;
        _chatUi.AddMessage(
            $"You try to use {InventoryItemName(source)} with " +
            $"{InventoryItemName(target)}, but nothing happens.",
            ChatMessageStyle.Action);
        _activeInventorySlot = -1;
    }

    private static Vector4 InventorySlotBounds(Vector4 panel, int slot)
    {
        var gridWidth =
            GameUiControlState.InventorySlotSize *
            GameUiControlState.InventoryColumns +
            GameUiControlState.InventoryColumnGap *
            (GameUiControlState.InventoryColumns - 1);
        var left = MathF.Round((panel.Z - gridWidth) / 2);
        return new(
            panel.X + left +
            slot % GameUiControlState.InventoryColumns *
            (GameUiControlState.InventorySlotSize +
             GameUiControlState.InventoryColumnGap),
            panel.Y + GameUiControlState.InventoryGridTop +
            slot / GameUiControlState.InventoryColumns *
            (GameUiControlState.InventorySlotSize +
             GameUiControlState.InventoryRowGap),
            GameUiControlState.InventorySlotSize,
            GameUiControlState.InventorySlotSize);
    }

    private void RenderInventoryContextMenu()
    {
        if (!_inventoryContext.Visible) return;
        var shadow = _inventoryContext.Bounds;
        shadow.X += 3;
        shadow.Y += 3;
        DrawUiColor(shadow, new(0, 0, 0, .55f));
        DrawUiColor(
            _inventoryContext.Bounds,
            new(.045f, .040f, .031f, .99f));
        DrawPanelOutline(
            _inventoryContext.Bounds, 0, new(.018f, .016f, .013f, 1));
        DrawPanelOutline(
            _inventoryContext.Bounds, 1, new(.40f, .31f, .15f, 1));
        DrawPanelOutline(
            _inventoryContext.Bounds, 2, new(.10f, .085f, .055f, 1));

        var header = new Vector4(
            _inventoryContext.Bounds.X + 3,
            _inventoryContext.Bounds.Y + 3,
            _inventoryContext.Bounds.Z - 6,
            ContextMenuControlState.HeaderHeight - 3);
        DrawUiColor(header, new(.075f, .064f, .043f, 1));
        DrawUiText(
            "Choose option",
            VerticallyCenteredTextPosition("Choose option", header, 7),
            new(224, 209, 165, 255));
        DrawUiColor(
            new(header.X, header.Y + header.W - 1, header.Z, 1),
            new(.35f, .27f, .13f, 1));

        for (var index = 0; index < _inventoryContext.Items.Count; index++)
        {
            var bounds = _inventoryContext.ItemBounds(index);
            if (_inventoryContext.HoveredIndex == index)
                DrawUiColor(bounds, new(.27f, .21f, .105f, .92f));
            DrawUiText(
                _inventoryContext.Items[index],
                VerticallyCenteredTextPosition(
                    _inventoryContext.Items[index], bounds, 9),
                index == 1
                    ? new(224, 151, 124, 255)
                    : new(224, 213, 175, 255));
            if (index + 1 < _inventoryContext.Items.Count)
                DrawUiColor(
                    new(bounds.X + 5, bounds.Y + bounds.W - 1,
                        bounds.Z - 10, 1),
                    new(.12f, .10f, .065f, 1));
        }
    }

    private void HandleInventoryContextSelection(int option)
    {
        if (_activePlayer is null) return;
        var inventory = _activePlayer.Inventory ?? [];
        var slot = _inventoryContextSlot;
        _inventoryContextSlot = -1;
        if ((uint)slot >= (uint)inventory.Length ||
            inventory[slot] is not { } itemId)
            return;
        var itemName = InventoryItemName(itemId);
        if (option == 0)
        {
            ActivateInventorySlot(slot);
            return;
        }
        if (option == 2)
        {
            _chatUi.AddMessage(
                InventoryItemExamine(itemId),
                ChatMessageStyle.Normal);
            return;
        }
        if (option != 1) return;
        if (!PlayerInventory.CanDrop(itemId))
        {
            _chatUi.AddMessage(
                "I shouldn't get rid of that. I need it for woodcutting.",
                ChatMessageStyle.Warning);
            return;
        }
        if (!PlayerInventory.TryRemove(inventory, slot, out var updated))
            return;
        _activePlayer = _activePlayer with
        {
            Inventory = updated,
            UpdatedUtc = DateTime.UtcNow
        };
        if (_activeInventorySlot == slot)
            _activeInventorySlot = -1;
        _saves.SavePlayer(_activePlayer);
        _chatUi.AddMessage(
            $"You drop the {itemName}. It is destroyed.",
            ChatMessageStyle.Warning);
    }

    private static string InventoryItemName(string itemId) => itemId switch
    {
        PlayerInventory.AxeItemId => "axe",
        "oak_logs" => "oak logs",
        "pine_logs" => "pine logs",
        "palm_logs" => "palm logs",
        "bamboo" => "bamboo",
        _ => "logs"
    };

    private static string InventoryItemExamine(string itemId) => itemId switch
    {
        PlayerInventory.AxeItemId => "A sturdy axe for chopping down trees.",
        "oak_logs" => "Logs cut from a sturdy oak tree.",
        "pine_logs" => "Fresh pine logs with a sharp woodland scent.",
        "palm_logs" => "Fibrous logs cut from a palm tree.",
        "bamboo" => "A strong, lightweight length of bamboo.",
        _ => "Logs cut from a tree."
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

    private static string InventoryItemCaption(string itemId) => itemId switch
    {
        PlayerInventory.AxeItemId => "Axe",
        "oak_logs" => "Oak",
        "pine_logs" => "Pine",
        "palm_logs" => "Palm",
        "bamboo" => "Bamb",
        _ => "Logs"
    };

    private static Vector4? InventoryItemUv(string itemId)
    {
        var cell = itemId switch
        {
            "logs" => 0,
            "oak_logs" => 1,
            "pine_logs" => 2,
            "palm_logs" => 3,
            "bamboo" => 4,
            PlayerInventory.AxeItemId => 5,
            _ => -1
        };
        return cell < 0
            ? null
            : new Vector4((cell % 4) * .25f, (cell / 4) * .5f, .25f, .5f);
    }

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
            if (!_treeAtlas.TryGetValue(instance.TreeType, out var entry))
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

            var (red, green, blue) = MinimapColor(
                InfiniteWorldGenerator.BiomeAt(
                    _worldSeed,
                    center.X + x - terrainRadius,
                    center.Y + y - terrainRadius));
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

    private void DrawUiSprite(
        SpriteFrame frame,
        int texture,
        Vector4 rectangle,
        float brightness = 0,
        Vector4? uvRectangle = null,
        Vector3? tint = null,
        float tintAmount = 0,
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
        GL.Uniform1(GL.GetUniformLocation(_program, "opacity"), drawOpacity);
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
            Title = $"Island RPG - Mapping visible sections {atlasDone}/{atlasTotal}";
        }
        else
            Title = $"Island RPG - Isometric map - {_atlasChunksAcross} chunks across - " +
                    "drag, zoom, or double-click to travel";
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
        foreach (var gpu in _worldChunks.Values.Where(IsChunkVisible)
                     .OrderBy(gpu => gpu.Chunk.Coordinate.X + gpu.Chunk.Coordinate.Y))
            DrawWorldChunkTerrain(gpu);
        if (_mode == PreviewMode.Game) DrawMoveMarker();

        var player = _mode == PreviewMode.Game ? GetPlayerVisual() : null;
        var playerDepth = player?.World.Y ?? float.MaxValue;
        var behind = new List<float>();
        var foregroundShadows = new List<float>();
        var foregroundObjects = new List<float>();
        var playerOccluded = false;
        foreach (var item in _worldChunks.Values.Where(IsChunkVisible)
                     .SelectMany(gpu => gpu.Chunk.Cliffs.Select(face => (Face: face, Gpu: gpu)))
                     .OrderBy(item => item.Face.X1 + item.Face.Y1))
        {
            var world = CliffWorld(item.Face);
            var target = world.Y <= playerDepth ? behind : foregroundObjects;
            AddCliffSprite(item.Face, item.Gpu.Opacity, target);
            if (player is not null && world.Y > playerDepth &&
                AtlasOverlapsPlayer($"CLF01_NN#{(item.Face.X1 == item.Face.X2 ? 6 : 0)}",
                    world, player))
                playerOccluded = true;
        }
        foreach (var item in _worldChunks.Values
                     .SelectMany(gpu => gpu.Chunk.Trees.Select(tree => (Tree: tree, Gpu: gpu)))
                     .OrderBy(item => item.Tree.X + item.Tree.Y))
        {
            var tree = item.Tree;
            var treeInstance = item.Gpu.Chunk.TreeInstances.FirstOrDefault(
                instance => instance.X == tree.X && instance.Y == tree.Y);
            var isStump = treeInstance?.State == TreeLifecycleState.Stump;
            var visibleName = isStump
                ? StumpAtlasKey(tree.GraphicName, shadow: false)
                : tree.GraphicName;
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
                : tree.GraphicName[..^2] + "N0";
            if (world.Y <= playerDepth)
            {
                AddTreeQuad(shadowName, world, item.Gpu.Opacity, behind);
                AddTreeQuad(visibleName, world, item.Gpu.Opacity, behind);
            }
            else
            {
                // A foreground object's ground shadow stays beneath the entity
                // and never participates in occlusion outlining.
                AddTreeQuad(shadowName, world, item.Gpu.Opacity, foregroundShadows);
                AddTreeQuad(visibleName, world, item.Gpu.Opacity, foregroundObjects);
                if (player is not null &&
                    AtlasOverlapsPlayer(visibleName, world, player))
                    playerOccluded = true;
            }
        }
        DrawTreeBatch(behind);
        DrawTreeBatch(foregroundShadows);
        if (player is not null)
            DrawSprite(
                player.Frame, player.Texture, player.World,
                mirror: player.Mirror, wading: player.Wading,
                teamColor: _activePlayer?.TeamColor ?? 0);
        DrawTreeBatch(foregroundObjects);
        if (player is not null && playerOccluded)
            DrawSprite(
                player.Frame, player.Texture, player.World,
                mirror: player.Mirror, outlineOnly: true, wading: player.Wading);
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

    private void AddCliffSprite(CliffFace face, float opacity, List<float> vertices)
    {
        var verticalEdge = face.X1 == face.X2;
        var frame = verticalEdge ? 6 : 0;
        var name = $"CLF01_NN#{frame}";
        AddAtlasQuad(name, CliffWorld(face), opacity, vertices);
    }

    private static Vector2 CliffWorld(CliffFace face)
    {
        var midpointX = (face.X1 + face.X2) * .5f;
        var midpointY = (face.Y1 + face.Y2) * .5f;
        var midpointHeight = (face.Top + face.Bottom) * .5f;
        return new Vector2(
            (midpointX - midpointY) * 48,
            (midpointX + midpointY) * 24 - midpointHeight * 20);
    }

    private bool AtlasOverlapsPlayer(string atlasKey, Vector2 world, PlayerVisual player)
    {
        if (!_treeAtlas.TryGetValue(atlasKey, out var entry)) return false;
        var objectBounds = SpriteBounds(entry.Frame, world);
        var playerBounds = SpriteBounds(player.Frame, player.World, player.Mirror);
        return objectBounds.Left < playerBounds.Right &&
               objectBounds.Right > playerBounds.Left &&
               objectBounds.Top < playerBounds.Bottom &&
               objectBounds.Bottom > playerBounds.Top;
    }

    private bool TryGetTreeUnderMouse(Vector2 mouse, out IslandTree hoveredTree)
    {
        foreach (var gpu in _worldChunks.Values.Where(IsChunkVisible)
                     .OrderByDescending(gpu =>
                         gpu.Chunk.Coordinate.X + gpu.Chunk.Coordinate.Y))
        foreach (var tree in gpu.Chunk.Trees.OrderByDescending(tree => tree.X + tree.Y))
        {
            if (gpu.Chunk.TreeInstances.Any(instance =>
                    instance.X == tree.X && instance.Y == tree.Y &&
                    instance.State == TreeLifecycleState.Stump))
                continue;
            if (!_treeAtlas.TryGetValue(tree.GraphicName, out var entry)) continue;
            var tileX = PositiveMod(tree.X, WorldChunk.Size);
            var tileY = PositiveMod(tree.Y, WorldChunk.Size);
            var tile = gpu.Chunk.Tiles[tileY * WorldChunk.Size + tileX];
            var height = (tile.North + tile.East + tile.South + tile.West) / 4f;
            var world = new Vector2(
                (tree.X - tree.Y) * 48,
                (tree.X + tree.Y + 1) * 24 - height * 20);
            var bounds = SpriteBounds(entry.Frame, world);
            if (mouse.X < bounds.Left || mouse.X >= bounds.Right ||
                mouse.Y < bounds.Top || mouse.Y >= bounds.Bottom)
                continue;

            var scale = Math.Max(SpritePixelScale(), .001f);
            var x = (int)((mouse.X - bounds.Left) / scale);
            var y = (int)((mouse.Y - bounds.Top) / scale);
            if ((uint)x >= (uint)entry.Frame.Width ||
                (uint)y >= (uint)entry.Frame.Height)
                continue;
            if (entry.Frame.Rgba[(y * entry.Frame.Width + x) * 4 + 3] > 24)
            {
                hoveredTree = tree;
                return true;
            }
        }
        hoveredTree = null!;
        return false;
    }

    private static string StumpAtlasKey(string treeType, bool shadow)
    {
        if (shadow) return "";
        if (treeType.StartsWith("FBAM", StringComparison.OrdinalIgnoreCase))
            return "STUMB_NN#0";
        if (treeType.StartsWith("FPIN", StringComparison.OrdinalIgnoreCase) ||
            treeType.StartsWith("FSNO", StringComparison.OrdinalIgnoreCase))
            return "STUMP_NN#1";
        if (treeType.StartsWith("FPAL", StringComparison.OrdinalIgnoreCase) ||
            treeType.StartsWith("FJUN", StringComparison.OrdinalIgnoreCase))
            return "STUMP_NN#2";
        return "STUMP_NN#0";
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
        var mapCenter = ScreenWorldToMap(-_camera / Math.Max(_zoom, .001f));
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
        Title = _mode == PreviewMode.Game
            ? $"Island RPG - {_player?.Gender} villager - {_player?.Action} - " +
              $"{_worldChunks.Count} chunks" +
              (_pendingPathTask is null ? "" : " - calculating path") +
              (_pendingChunkTask is null ? "" : " - streaming")
            : $"Island RPG - World {_worldSeed} - {_worldChunks.Count} chunks" +
              (_pendingChunkTask is null ? "" : " - streaming");
    }

    private void PrepareWorldTerrain()
    {
        _terrainArray = UploadTerrainArray();
        _waterNormalArray = UploadWaterNormalArray();
        _terrainProgram = CreateTerrainProgram();
        _cliffProgram = CreateCliffProgram();
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
            [EntityAction.Die] = "DN"
        };
        var uploaded = new Dictionary<string, EntityAnimation>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var gender in Enum.GetValues<EntityGender>())
        foreach (var pair in suffixes)
        {
            var prefix = pair.Key == EntityAction.Work
                ? gender == EntityGender.Male ? "VMLUM_" : "VFLUM_"
                : gender == EntityGender.Male ? "VMBAS_" : "VFBAS_";
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
            var animation = new EntityAnimation(graphic, textures, rate);
            uploaded[name] = animation;
            _entityAnimations[(gender, pair.Key)] = animation;
        }
        foreach (var gender in Enum.GetValues<EntityGender>())
        {
            if (!_entityAnimations.ContainsKey((gender, EntityAction.Idle)) ||
                !_entityAnimations.ContainsKey((gender, EntityAction.Move)))
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
            192, 120, 0, 0, new byte[192 * 120 * 4]);
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
        }
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

    private void PrepareGameCursors()
    {
        var path = Path.Combine(
            _install, "resources", "_common", "drs", "interface", "51000.slp");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "The installed interface assets do not contain the AoE cursor sheet.", path);
        var palette = JascPalette.Load(Age2PaletteResolver.Resolve(_install, path).Path);
        var cursorSheet = SlpDecoder.Decode(path, palette);
        if (cursorSheet.Frames.Count <= 8)
            throw new InvalidDataException(
                "The installed AoE cursor sheet does not contain the tree-cut cursor.");

        var defaultFrame = cursorSheet.Frames[0];
        var cutFrame = cursorSheet.Frames[8];
        _defaultNativeCursor = CreateNativeCursor(defaultFrame);
        _cutNativeCursor = CreateNativeCursor(cutFrame);
        Cursor = _defaultNativeCursor;
        CursorState = CursorState.Normal;
    }

    private static MouseCursor CreateNativeCursor(SpriteFrame frame)
    {
        var pixels = (byte[])frame.Rgba.Clone();
        // GLFW's Windows backend expects premultiplied RGB for translucent
        // custom-cursor pixels.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            pixels[i] = (byte)(pixels[i] * alpha / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] * alpha / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] * alpha / 255);
        }
        return new MouseCursor(
            Math.Clamp(frame.HotspotX, 0, frame.Width - 1),
            Math.Clamp(frame.HotspotY, 0, frame.Height - 1),
            frame.Width,
            frame.Height,
            pixels);
    }

    private void UpdateNativeCursor()
    {
        if (_defaultNativeCursor is null || _cutNativeCursor is null) return;
        var cut = !IsPointerOverGameUi(MouseState.Position) &&
                  TryGetTreeUnderMouse(SceneMousePosition(), out _);
        if (cut == _usingCutCursor) return;
        _usingCutCursor = cut;
        Cursor = cut ? _cutNativeCursor : _defaultNativeCursor;
    }

    private void DrawMoveMarker()
    {
        if (_moveMarker is null || _moveMarkerAnimation is null) return;
        var animation = _moveMarkerAnimation;
        var frameIndex = Math.Min(
            animation.Textures.Length - 1,
            (int)(_moveMarker.Time / animation.SecondsPerFrame));
        var elevation = InfiniteWorldGenerator.SampleRenderedHeight(
            _worldSeed, _moveMarker.Position.X, _moveMarker.Position.Y);
        var world = new Vector2(
            (_moveMarker.Position.X - _moveMarker.Position.Y) * 48,
            (_moveMarker.Position.X + _moveMarker.Position.Y) * 24 - elevation * 20);
        DrawSprite(
            animation.Graphic.Sprite.Frames[frameIndex],
            animation.Textures[frameIndex],
            world);
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
            ? 0
            : (int)(_player.ActionTime / animation.SecondsPerFrame);
        if (_player.Action == EntityAction.Die)
            rawFrame = Math.Min(rawFrame, framesPerAngle - 1);
        var directional = VillagerDirectionRig.Resolve(
            _player.Facing,
            graphic.Sprite.Frames.Count,
            storedVillagerAngles,
            rawFrame);
        var elevation = InfiniteWorldGenerator.SampleRenderedHeight(
            _worldSeed, _player.Position.X, _player.Position.Y);
        var world = new Vector2(
            (_player.Position.X - _player.Position.Y) * 48,
            (_player.Position.X + _player.Position.Y) * 24 - elevation * 20);
        var biome = InfiniteWorldGenerator.BiomeAt(
            _worldSeed,
            (int)MathF.Floor(_player.Position.X),
            (int)MathF.Floor(_player.Position.Y));
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
            LoadedGraphic Asset, SpriteFrame Frame, int FrameIndex, int X, int Y)>();
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
            var frames = cliff || stump
                ? asset.Sprite.Frames
                : [asset.Sprite.Frames[0]];
            for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                var frame = frames[frameIndex];
                if (x + frame.Width + padding > atlasWidth)
                {
                    x = padding;
                    y += rowHeight + padding;
                    rowHeight = 0;
                }
                placements.Add((asset, frame, frameIndex, x, y));
                x += frame.Width + padding;
                rowHeight = Math.Max(rowHeight, frame.Height);
            }
        }
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
            var multiFrame = placement.Asset.Definition.Name.StartsWith(
                                 "CLF", StringComparison.OrdinalIgnoreCase) ||
                             placement.Asset.Definition.Name.StartsWith(
                                 "STUMP", StringComparison.OrdinalIgnoreCase) ||
                             placement.Asset.Definition.Name.StartsWith(
                                 "STUMB", StringComparison.OrdinalIgnoreCase);
            var key = multiFrame
                ? $"{placement.Asset.Definition.Name}#{placement.FrameIndex}"
                : placement.Asset.Definition.Name;
            _treeAtlas[key] = new(
                placement.Frame,
                placement.X / (float)atlasWidth,
                placement.Y / (float)atlasHeight,
                (placement.X + placement.Frame.Width) / (float)atlasWidth,
                (placement.Y + placement.Frame.Height) / (float)atlasHeight);
            if (multiFrame && placement.FrameIndex == 0)
                _treeAtlas[placement.Asset.Definition.Name] = _treeAtlas[key];
        }
        _treeAtlasTexture = Upload(atlasWidth, atlasHeight, rgba);
        _treeBatchVbo = GL.GenBuffer();
    }

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
        return new(chunk, vbo, vertices.Count / 12,
            weights.A, weights.B, weights.C, weights.D, weights.Shore);

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

    private bool IsChunkVisible(GpuWorldChunk gpu)
    {
        var originX = gpu.Chunk.Coordinate.X * WorldChunk.Size;
        var originY = gpu.Chunk.Coordinate.Y * WorldChunk.Size;
        var centerX = originX + WorldChunk.Size * .5f;
        var centerY = originY + WorldChunk.Size * .5f;
        var projected = new Vector2(
            (centerX - centerY) * 48,
            (centerX + centerY) * 24 - 4.5f * 12);
        var screen = new Vector2(ReferenceWidth * .5f, ReferenceHeight * .5f) +
                     _camera + projected * _zoom;
        var halfWidth = WorldChunk.Size * 48 * _zoom + 96;
        var halfHeight = WorldChunk.Size * 24 * _zoom + 128;
        return screen.X + halfWidth >= 0 && screen.X - halfWidth <= ReferenceWidth &&
               screen.Y + halfHeight >= 0 && screen.Y - halfHeight <= ReferenceHeight;
    }

    private void DrawWorldChunkTerrain(GpuWorldChunk gpu)
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
        GL.BindTexture(TextureTarget.Texture2D, gpu.WeightsA);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "biomeWeightsA"), 1);
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, gpu.WeightsB);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "biomeWeightsB"), 2);
        GL.ActiveTexture(TextureUnit.Texture3);
        GL.BindTexture(TextureTarget.Texture2D, gpu.WeightsC);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "biomeWeightsC"), 3);
        GL.ActiveTexture(TextureUnit.Texture4);
        GL.BindTexture(TextureTarget.Texture2D, gpu.WeightsD);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "biomeWeightsD"), 4);
        GL.ActiveTexture(TextureUnit.Texture5);
        GL.BindTexture(TextureTarget.Texture2DArray, _waterNormalArray);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "waterNormals"), 5);
        GL.ActiveTexture(TextureUnit.Texture6);
        GL.BindTexture(TextureTarget.Texture2D, gpu.ShoreDistance);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "shoreDistance"), 6);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "time"), _waterTime);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "opacity"), gpu.Opacity);
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
            TreeInstances = source.TreeInstances.ToList()
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
        _terrainProgram = CreateTerrainProgram();

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
        int teamColor = 0)
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
        GL.Uniform1(GL.GetUniformLocation(_program, "wading"),
            wading && !outlineOnly ? 1 : 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "waterlineUv"),
            Math.Clamp((frame.HotspotY - 13f) / frame.Height, .45f, .88f));
        GL.Uniform1(GL.GetUniformLocation(_program, "brightness"), 0f);
        var tintColor = tint ?? Vector3.Zero;
        GL.Uniform3(GL.GetUniformLocation(_program, "colorTint"), tintColor);
        GL.Uniform1(GL.GetUniformLocation(_program, "tintAmount"), tintAmount);
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

    private static int CreateCliffProgram()
    {
        int Compile(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var ok);
            if (ok == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
            return shader;
        }

        const string vertex = """
            #version 330 core
            layout(location=0) in vec2 world;
            layout(location=1) in vec2 textureUv;
            layout(location=2) in float opacity;
            uniform vec2 viewport;
            uniform vec2 camera;
            uniform float zoom;
            out vec2 uv;
            out float alpha;
            void main() {
                vec2 pixel = world * zoom + camera;
                gl_Position = vec4(pixel.x * 2.0 / viewport.x,
                                  -pixel.y * 2.0 / viewport.y, 0.0, 1.0);
                uv = textureUv;
                alpha = opacity;
            }
            """;
        const string fragment = """
            #version 330 core
            in vec2 uv;
            in float alpha;
            out vec4 color;
            void main() {
                color = vec4(0.20, 0.15, 0.10, alpha);
            }
            """;
        var vs = Compile(ShaderType.VertexShader, vertex);
        var fs = Compile(ShaderType.FragmentShader, fragment);
        var program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
        if (linked == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(program));
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        return program;
    }

    private static int CreateTerrainProgram()
    {
        int Compile(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var ok);
            if (ok == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
            return shader;
        }

        const string vertex = """
            #version 330 core
            layout(location=0) in vec2 world;
            layout(location=1) in vec2 textureUv;
            layout(location=2) in vec2 tileUv;
            layout(location=3) in vec3 layerPNE;
            layout(location=4) in vec2 layerSW;
            layout(location=5) in float slopeShade;
            uniform vec2 viewport;
            uniform vec2 camera;
            uniform float zoom;
            out vec2 uv;
            out vec2 mapUv;
            out float terrainShade;
            void main() {
                vec2 pixel = world * zoom + camera;
                gl_Position = vec4(pixel.x * 2.0 / viewport.x,
                                  -pixel.y * 2.0 / viewport.y, 0.0, 1.0);
                uv = textureUv;
                mapUv = clamp(tileUv, 0.0, 1.0);
                terrainShade = slopeShade;
            }
            """;
        const string fragment = """
            #version 330 core
            in vec2 uv;
            in vec2 mapUv;
            in float terrainShade;
            uniform sampler2DArray terrain;
            uniform sampler2D biomeWeightsA;
            uniform sampler2D biomeWeightsB;
            uniform sampler2D biomeWeightsC;
            uniform sampler2D biomeWeightsD;
            uniform sampler2D shoreDistance;
            uniform sampler2DArray waterNormals;
            uniform float time;
            uniform float opacity;
            uniform int rippleCount;
            uniform vec2 ripplePositions[8];
            uniform float rippleAges[8];
            out vec4 color;
            vec2 hash22(vec2 p) {
                vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
                p3 += dot(p3, p3.yzx + 33.33);
                return fract((p3.xx + p3.yz) * p3.zy);
            }
            float valueNoise(vec2 p) {
                vec2 cell = floor(p);
                vec2 f = fract(p);
                f = f*f*(3.0-2.0*f);
                float a = hash22(cell).x;
                float b = hash22(cell+vec2(1,0)).x;
                float c = hash22(cell+vec2(0,1)).x;
                float d = hash22(cell+vec2(1,1)).x;
                return mix(mix(a,b,f.x), mix(c,d,f.x), f.y);
            }
            vec4 sampleLayerAt(float layer, vec2 coordinates) {
                // Minecraft-style deterministic block variation, softened into
                // stochastic tiling so repeated sheets do not form a grid.
                vec2 cell = floor(coordinates);
                vec2 f = fract(coordinates);
                vec2 blend = f*f*(3.0-2.0*f);
                vec2 a = hash22(cell);
                vec2 b = hash22(cell+vec2(1,0));
                vec2 c = hash22(cell+vec2(0,1));
                vec2 d = hash22(cell+vec2(1,1));
                vec4 ca = texture(terrain, vec3(f+a, layer));
                vec4 cb = texture(terrain, vec3(f+b, layer));
                vec4 cc = texture(terrain, vec3(f+c, layer));
                vec4 cd = texture(terrain, vec3(f+d, layer));
                vec4 result = mix(mix(ca,cb,blend.x), mix(cc,cd,blend.x), blend.y);
                float macro = valueNoise(coordinates * 0.20) - 0.5;
                float strength = layer < 1.5 ? 0.025 : 0.07;
                result.rgb *= 1.0 + macro * strength;
                return result;
            }
            vec4 sampleLayer(float layer) { return sampleLayerAt(layer, uv); }
            vec4 sampleWaterLayer(float layer, vec2 coordinates) {
                // Retain some of the soft, cloudy variation from the stochastic
                // terrain blend, but keep the original water sheet dominant.
                vec4 primary = texture(terrain, vec3(coordinates, layer));
                vec4 organic = sampleLayerAt(layer, coordinates * 0.72);
                return mix(primary, organic, 0.32);
            }
            vec2 normalXY(float layer, vec2 coordinates) {
                return texture(waterNormals, vec3(coordinates, layer)).xy * 2.0 - 1.0;
            }
            void main() {
                vec4 a = texture(biomeWeightsA, mapUv);
                vec4 b = texture(biomeWeightsB, mapUv);
                vec4 c = texture(biomeWeightsC, mapUv);
                vec4 d = texture(biomeWeightsD, mapUv);
                float shorelineDistance = (texture(shoreDistance, mapUv).r * 2.0 - 1.0) * 8.0;
                float shorelineProximity = 1.0 - smoothstep(0.0, 3.0, abs(shorelineDistance));
                float total = max(dot(a, vec4(1.0)) + dot(b, vec4(1.0)) +
                                  dot(c, vec4(1.0)) + dot(d, vec4(1.0)), 0.001);
                color = vec4(0.0);
                float waterWeight = dot(a, vec4(1.0));
                float waterCoverage = clamp(waterWeight / total, 0.0, 1.0);
                float surfaceEffect = smoothstep(0.38, 0.72, waterCoverage);
                vec3 waterNormal = vec3(0.0, 0.0, 1.0);
                vec2 waterDistortion = vec2(0.0);
                vec2 primaryFlow = vec2(1.0, 0.0);
                vec2 secondaryFlow = vec2(0.0, 1.0);
                float waveSlope = 0.0;
                if (waterWeight > 0.002) {
                    // IslandMap-style regional currents, evaluated in global
                    // world UVs so neighbouring streamed chunks remain seamless.
                    float flowAngle = (valueNoise(uv * 0.11) - 0.5) * 5.2;
                    primaryFlow = vec2(cos(flowAngle), sin(flowAngle));
                    float secondAngle = flowAngle + 1.75 +
                        (valueNoise(uv * 0.17 + 19.7) - 0.5) * 0.8;
                    secondaryFlow = vec2(cos(secondAngle), sin(secondAngle));
                    vec2 deepA = normalXY(
                        0.0, uv * 1.35 + primaryFlow * time * 0.034);
                    vec2 deepB = normalXY(
                        1.0, uv * 0.73 + secondaryFlow * time * 0.025);
                    vec2 shoreA = normalXY(
                        2.0, uv * 1.65 + primaryFlow * time * 0.021);
                    vec2 shoreB = normalXY(
                        3.0, uv * 0.92 - secondaryFlow * time * 0.019);
                    float shallow = (a.g + a.b + a.a) / max(waterWeight, 0.001);
                    vec2 waves = mix(deepA * 0.62 + deepB * 0.38,
                                     shoreA * 0.62 + shoreB * 0.38, shallow);
                    waveSlope = length(waves);
                    // Shallow water keeps its own wave pattern, but uses the
                    // same normal strength so its moving shine does not disappear.
                    waterNormal = normalize(vec3(waves * 1.12, 1.0));
                    waterDistortion =
                        waterNormal.xy * mix(0.027, 0.014, shallow);
                }
                vec4 deepWaterSample = vec4(0.0);
                if (a.r > 0.002 || a.g > 0.002) {
                    deepWaterSample = sampleWaterLayer(0.0, uv + waterDistortion);
                }
                if (a.r > 0.002) color += deepWaterSample * a.r;
                if (a.g > 0.002) {
                    vec4 lightWaterSample =
                        sampleWaterLayer(1.0, uv + waterDistortion);
                    // Three-stage ocean falloff: dark open sea, a related
                    // mid-blue shelf, then the lighter blue immediately offshore.
                    float coastalStage = 1.0 - smoothstep(0.35, 7.0,
                        max(shorelineDistance, 0.0));
                    // Keep the middle shelf related to the deep ocean, while
                    // allowing the actual coastal strip to reach the light sheet.
                    float shelfLight = mix(0.72, 1.0, coastalStage);
                    vec4 stagedShelf = mix(deepWaterSample, lightWaterSample, shelfLight);
                    color += stagedShelf * a.g;
                }
                if (a.b > 0.002) color += sampleWaterLayer(2.0, uv + waterDistortion) * a.b;
                if (a.a > 0.002) color += sampleWaterLayer(3.0, uv + waterDistortion) * a.a;
                if (b.r > 0.002) color += sampleLayer(4.0) * b.r;
                if (b.g > 0.002) color += sampleLayer(5.0) * b.g;
                if (b.b > 0.002) color += sampleLayer(6.0) * b.b;
                if (b.a > 0.002) color += sampleLayer(7.0) * b.a;
                if (c.r > 0.002) color += sampleLayer(8.0) * c.r;
                if (c.g > 0.002) color += sampleLayer(9.0) * c.g;
                if (c.b > 0.002) color += sampleLayer(10.0) * c.b;
                if (c.a > 0.002) color += sampleLayer(11.0) * c.a;
                if (d.r > 0.002) color += sampleLayer(12.0) * d.r;
                if (d.g > 0.002) color += sampleLayer(13.0) * d.g;
                if (d.b > 0.002) color += sampleLayer(14.0) * d.b;
                if (d.a > 0.002) color += sampleLayer(15.0) * d.a;
                color /= total;
                // Directional relief lighting affects land only. The upper-right
                // light direction matches the classic isometric hill treatment.
                float snowCoverage = clamp(d.g / total, 0.0, 1.0);
                float softenedShade = mix(terrainShade, 1.0, snowCoverage * 0.62);
                color.rgb *= mix(softenedShade, 1.0, waterCoverage);
                if (snowCoverage > 0.001 && softenedShade < 1.0) {
                    // Snow shadows retain cool skylight instead of turning grey.
                    color.rgb += vec3(0.025, 0.040, 0.065) *
                                 (1.0 - softenedShade) * snowCoverage;
                }
                if (waterWeight > 0.002) {
                    vec3 lightDirection = normalize(vec3(-0.38, -0.48, 0.79));
                    float sparkle = pow(max(dot(waterNormal, lightDirection), 0.0), 30.0);
                    float broadHighlight = pow(max(dot(waterNormal, lightDirection), 0.0), 7.0);
                    float crest = smoothstep(0.22, 0.62, length(waterNormal.xy));
                    vec3 reflection = vec3(0.38, 0.70, 0.84) * broadHighlight * 0.11 +
                                      vec3(0.86, 0.97, 1.0) * sparkle * 0.38 +
                                      vec3(0.24, 0.58, 0.67) * crest * 0.075;
                    color.rgb = mix(color.rgb,
                        color.rgb * (0.96 + broadHighlight * 0.11), surfaceEffect);
                    color.rgb += reflection * surfaceEffect;

                    // Whitecaps only form where an animated wave is steep and
                    // the moving breakup field selects a short-lived crest.
                    float breakupA = valueNoise(uv * 2.2 + primaryFlow * time * 0.18);
                    float breakupB = valueNoise(uv * 5.4 - secondaryFlow * time * 0.11 + 31.4);
                    float breakup = breakupA * 0.62 + breakupB * 0.38;
                    float steepCrest = smoothstep(0.48, 0.88, waveSlope);
                    float sparsePatch = smoothstep(0.61, 0.79, breakup);
                    float shallowBoost = smoothstep(
                        0.10, 0.62, (a.g + a.b + a.a) / max(waterWeight, 0.001));
                    float shoreBoost = max(shallowBoost,
                        shorelineProximity * step(0.0, shorelineDistance));
                    float foam = steepCrest * sparsePatch * mix(0.42, 0.82, shoreBoost);
                    vec3 foamColor = vec3(0.84, 0.94, 0.95);
                    color.rgb = mix(color.rgb, foamColor, foam * surfaceEffect * 0.48);

                    // Each planted foot emits a circular world-space impulse.
                    // Isometric projection turns the circle into the correct
                    // screen-space ellipse; overlapping impulses superpose.
                    float rippleWave = 0.0;
                    for (int rippleIndex = 0; rippleIndex < 8; rippleIndex++) {
                        if (rippleIndex >= rippleCount) break;
                        float age = rippleAges[rippleIndex];
                        vec2 rippleDelta =
                            (uv - ripplePositions[rippleIndex]) * 8.0;
                        float distanceFromFoot = length(rippleDelta);
                        float radius = 0.035 + age * 0.18;
                        float fade = (1.0 - smoothstep(0.72, 1.35, age)) *
                                     smoothstep(0.0, 0.08, age);
                        float crest = 1.0 - smoothstep(
                            0.012, 0.030, abs(distanceFromFoot - radius));
                        float troughRadius = max(0.0, radius - 0.040);
                        float trough = 1.0 - smoothstep(
                            0.010, 0.025,
                            abs(distanceFromFoot - troughRadius));
                        rippleWave += (crest - trough * 0.62) * fade;
                    }
                    // Quantized crest/trough bands preserve the reference
                    // resolution's pixel-art character.
                    float rippleLight =
                        smoothstep(0.24, 0.48, rippleWave) * surfaceEffect;
                    float rippleShadow =
                        smoothstep(0.18, 0.38, -rippleWave) * surfaceEffect;
                    color.rgb *= 1.0 - rippleShadow * 0.035;
                    color.rgb += vec3(0.42, 0.70, 0.77) * rippleLight * 0.115;
                }
                color.a *= opacity;

            }
            """;
        var vs = Compile(ShaderType.VertexShader, vertex);
        var fs = Compile(ShaderType.FragmentShader, fragment);
        var program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
        if (linked == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(program));
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        return program;
    }

    private static int CreateProgram()
    {
        int Compile(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type); GL.ShaderSource(shader, source); GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var ok);
            if (ok == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
            return shader;
        }
        var vs = Compile(ShaderType.VertexShader,
            "#version 330 core\nlayout(location=0) in vec2 p;layout(location=1) in vec2 u;" +
            "layout(location=2) in float vertexOpacity;out vec2 uv;out float alpha;" +
            "void main(){uv=u;alpha=vertexOpacity;gl_Position=vec4(p,0,1);}");
        var fs = Compile(ShaderType.FragmentShader,
            "#version 330 core\nin vec2 uv;in float alpha;out vec4 c;uniform sampler2D image;" +
            "uniform int recolorPlayer;uniform vec3 playerColor;" +
            "uniform float opacity;uniform float brightness;uniform float tintAmount;" +
            "uniform vec3 colorTint;uniform int outlineOnly;uniform int wading;" +
            "uniform int spriteOutline;uniform vec3 spriteOutlineColor;" +
            "uniform float waterlineUv;uniform vec2 texelSize;" +
            "void main(){vec4 source=texture(image,uv);" +
            "if(outlineOnly==1){float around=0.0;" +
            "around=max(around,texture(image,uv+vec2(texelSize.x,0)).a);" +
            "around=max(around,texture(image,uv-vec2(texelSize.x,0)).a);" +
            "around=max(around,texture(image,uv+vec2(0,texelSize.y)).a);" +
            "around=max(around,texture(image,uv-vec2(0,texelSize.y)).a);" +
            "float ring=around*(1.0-source.a);if(ring<0.05)discard;" +
            "c=vec4(1.0,0.82,0.18,ring*opacity*alpha);}" +
            "else{c=source;" +
            "if(spriteOutline==1&&source.a<0.05){float around=0.0;" +
            "around=max(around,texture(image,uv+vec2(texelSize.x,0)).a);" +
            "around=max(around,texture(image,uv-vec2(texelSize.x,0)).a);" +
            "around=max(around,texture(image,uv+vec2(0,texelSize.y)).a);" +
            "around=max(around,texture(image,uv-vec2(0,texelSize.y)).a);" +
            "around=max(around,texture(image,uv+texelSize).a);" +
            "around=max(around,texture(image,uv-texelSize).a);" +
            "around=max(around,texture(image,uv+vec2(texelSize.x,-texelSize.y)).a);" +
            "around=max(around,texture(image,uv+vec2(-texelSize.x,texelSize.y)).a);" +
            "if(around>=0.05){c=vec4(spriteOutlineColor,around*opacity*alpha);return;}}" +
            "if(recolorPlayer==1&&source.a>0.01){" +
            "float strongestOther=max(source.r,source.g);" +
            "bool authoredBlue=source.b>0.16&&source.b>strongestOther*1.22&&" +
            "(source.b-strongestOther)>0.055;" +
            "if(authoredBlue){float shade=dot(source.rgb,vec3(0.2126,0.7152,0.0722));" +
            "float targetLuma=max(dot(playerColor,vec3(0.2126,0.7152,0.0722)),0.001);" +
            "c.rgb=clamp(playerColor*(shade/targetLuma),0.0,1.0);}}" +
            "if(wading==1&&uv.y>=waterlineUv&&source.a>0.01){" +
            "float surface=1.0-smoothstep(waterlineUv,waterlineUv+0.035,uv.y);" +
            "c.rgb=mix(c.rgb,vec3(0.08,0.34,0.53),0.43);" +
            "c.rgb+=vec3(0.16,0.42,0.55)*surface*0.22;c.a*=0.68;}" +
            "c.rgb*=1.0+brightness;c.rgb=mix(c.rgb,colorTint,tintAmount);" +
            "c.a*=opacity*alpha;}}");
        var program = GL.CreateProgram(); GL.AttachShader(program, vs); GL.AttachShader(program, fs); GL.LinkProgram(program);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        return program;
    }

    protected override void OnUnload()
    {
        if (_activePlayer is not null && _player is not null)
        {
            _activePlayer = _activePlayer with
            {
                Gender = _player.Gender,
                UpdatedUtc = DateTime.UtcNow
            };
            _saves.SavePlayer(_activePlayer);
            if (_activeWorld is not null)
                _saves.SaveWorldPlayer(
                    _activeWorld.Id,
                    new(
                        _activePlayer.Id,
                        _player.Position.X,
                        _player.Position.Y,
                        DateTime.UtcNow));
        }
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
        if (_treeBatchVbo != 0) GL.DeleteBuffer(_treeBatchVbo);
        if (_treeAtlasTexture != 0) GL.DeleteTexture(_treeAtlasTexture);
        if (_cliffBatchVbo != 0) GL.DeleteBuffer(_cliffBatchVbo);
        if (_cliffTexture != 0) GL.DeleteTexture(_cliffTexture);
        if (_terrainProgram != 0) GL.DeleteProgram(_terrainProgram);
        if (_cliffProgram != 0) GL.DeleteProgram(_cliffProgram);
        GL.DeleteVertexArray(_vao);
        GL.DeleteProgram(_program);
        base.OnUnload();
        if (saveFailure is not null)
            throw new IOException("One or more chunks could not be saved during shutdown.", saveFailure);
    }
}
