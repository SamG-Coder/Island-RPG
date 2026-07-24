using IslandRpg.Assets;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using IslandRpg.World;

namespace IslandRpg.Rendering;

internal sealed class GameHostWindow : GameWindow
{
    internal enum PreviewMode { Assets, Island, World }
    private enum ScreenState { LoadingAssets, PreparingGpu, WorldPreview }
    private sealed class GpuWorldChunk(
        WorldChunk chunk, int vbo, int vertexCount, int weightsA, int weightsB, int shoreDistance)
    {
        public WorldChunk Chunk { get; } = chunk;
        public int Vbo { get; } = vbo;
        public int VertexCount { get; } = vertexCount;
        public int WeightsA { get; } = weightsA;
        public int WeightsB { get; } = weightsB;
        public int ShoreDistance { get; } = shoreDistance;
        public float Opacity { get; set; }
    }
    private sealed record SpriteAtlasEntry(
        SpriteFrame Frame, float U0, float V0, float U1, float V1);

    private readonly string _install;
    private readonly PreviewMode _mode;
    private readonly long _worldSeed;
    private WorldChunkStore? _worldStore;
    private readonly Dictionary<ChunkCoordinate, GpuWorldChunk> _worldChunks = [];
    private Task<WorldChunk>? _pendingChunkTask;
    private ChunkCoordinate _pendingChunkCoordinate;
    private Task _saveTail = Task.CompletedTask;
    private bool _atlasOpen;
    private Task<WorldAtlasSnapshot>? _atlasTask;
    private WorldAtlasSnapshot? _atlas;
    private int _atlasTexture;
    private int _atlasDone;
    private int _atlasTotal = 1;
    private int _atlasChunksAcross = WorldAtlasGenerator.ChunksAcross;
    private Vector2 _atlasPan;
    private Vector2 _atlasLastMouse;
    private bool _atlasDragging;
    private bool _atlasLeftWasDown;
    private double _clock;
    private double _atlasLastClickTime = -1;
    private Vector2 _atlasLastClickPosition;
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
    private int _islandVbo;
    private int _islandVertexCount;
    private int _terrainArray;
    private int _biomeWeightsA;
    private int _biomeWeightsB;
    private int _shoreDistance;
    private int _waterNormalArray;
    private int _streamVbo;
    private int _treeBatchVbo;
    private int _treeAtlasTexture;
    private readonly Dictionary<string, SpriteAtlasEntry> _treeAtlas =
        new(StringComparer.OrdinalIgnoreCase);
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
        var progress = new Progress<(int Done, int Total, string Name)>(value =>
        {
            _done = value.Done;
            _total = Math.Max(1, value.Total);
            _current = value.Name;
        });
        _loadTask = Task.Run(() => AssetLoader.LoadAll(_install, progress));
    }

    protected override void OnUpdateFrame(FrameEventArgs e)
    {
        base.OnUpdateFrame(e);
        _clock += e.Time;
        _waterTime = (_waterTime + (float)e.Time) % 10000f;
        if (KeyboardState.IsKeyDown(Keys.Escape)) Close();
        if (_screen == ScreenState.WorldPreview && _mode == PreviewMode.World &&
            KeyboardState.IsKeyPressed(Keys.M))
        {
            _atlasOpen = !_atlasOpen;
            if (_atlasOpen && _atlasTask is null && _atlas is null)
                StartAtlasAtCamera();
            if (!_atlasOpen) _atlasPan = Vector2.Zero;
        }

        if (_screen == ScreenState.LoadingAssets && _loadTask is { IsCompleted: true })
        {
            if (_loadTask.IsFaulted)
                throw _loadTask.Exception?.GetBaseException() ?? new InvalidOperationException("Asset loading failed.");
            _catalog = _loadTask.Result;
            if (_mode == PreviewMode.Island)
                _island = IslandGenerator.Generate();
            var islandGraphics = _mode == PreviewMode.World
                ? Enumerable.Range(0, 12).Select(index => $"TREE{(char)('A' + index)}_NN")
                    .Concat(["FPAL_NN", "FPIN_NN"])
                    .SelectMany(name => new[] { name, name[..^2] + "N0" })
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
                if (_mode != PreviewMode.World)
                    _textures.Add(Upload(_worldAssets[_uploadIndex].Sprite.Frames[0]));
                _current = _worldAssets[_uploadIndex].Definition.Name;
                _uploadIndex++;
            }
            _done = _uploadIndex;
            if (_uploadIndex == _worldAssets.Count)
            {
                if (_mode is PreviewMode.Island or PreviewMode.World)
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
                        _worldStore = new WorldChunkStore(_worldSeed);
                        _camera = Vector2.Zero;
                        _zoom = .8f;
                        StreamWorld();
                    }
                    _screen = ScreenState.WorldPreview;
                    Title = _mode == PreviewMode.Island
                        ? "Island RPG - Generated Island"
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

        if (_screen == ScreenState.WorldPreview)
        {
            if (_atlasOpen)
                UpdateAtlas();
            else
            {
                UpdateCamera((float)e.Time);
                if (_mode == PreviewMode.World)
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
        var mouse = MouseState.Position;
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

    private void StartAtlasAtCamera()
    {
        var mapCenter = ScreenWorldToMap(-_camera / Math.Max(_zoom, .001f));
        StartAtlas((int)MathF.Round(mapCenter.X), (int)MathF.Round(mapCenter.Y));
    }

    private void StartAtlas(int centerTileX, int centerTileY)
    {
        if (_atlasTask is { IsCompleted: false }) return;
        Interlocked.Exchange(ref _atlasDone, 0);
        Volatile.Write(ref _atlasTotal, _atlasChunksAcross * _atlasChunksAcross);
        var chunksAcross = _atlasChunksAcross;
        var pixelsPerChunk = WorldAtlasGenerator.PixelSize / chunksAcross;
        _atlasTask = Task.Run(() => WorldAtlasGenerator.Generate(
            _worldSeed, centerTileX, centerTileY,
            ReportAtlasProgress,
            chunksAcross,
            pixelsPerChunk));
    }

    private void ReportAtlasProgress(int done, int total)
    {
        Volatile.Write(ref _atlasTotal, total);
        var current = Volatile.Read(ref _atlasDone);
        while (done > current)
        {
            var observed = Interlocked.CompareExchange(ref _atlasDone, done, current);
            if (observed == current) break;
            current = observed;
        }
    }

    private void UpdateAtlas()
    {
        if (_atlasTask is { IsCompleted: true })
        {
            if (_atlasTask.IsFaulted)
                throw _atlasTask.Exception?.GetBaseException() ??
                      new InvalidOperationException("World atlas generation failed.");
            _atlas = _atlasTask.Result;
            if (_atlasTexture != 0) GL.DeleteTexture(_atlasTexture);
            _atlasTexture = Upload(_atlas.Width, _atlas.Height, _atlas.Rgba);
            GL.BindTexture(TextureTarget.Texture2D, _atlasTexture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            _atlasTask = null;
            _atlasPan = Vector2.Zero;
        }

        var mouse = MouseState.Position;
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
            _atlasPan += mouse - _atlasLastMouse;
            _atlasLastMouse = mouse;
        }
        else if (!leftDown && _atlasDragging)
        {
            _atlasDragging = false;
            RecenterAtlasAfterDrag();
        }
        _atlasLeftWasDown = leftDown;
    }

    private void RecenterAtlasAfterDrag()
    {
        if (_atlas is null || _atlasPan.LengthSquared < 24 * 24) return;
        var mapSize = AtlasDisplaySize();
        var centerX = _atlas.CenterTileX -
                      (int)MathF.Round(_atlasPan.X / mapSize * _atlas.SpanTiles);
        var centerY = _atlas.CenterTileY -
                      (int)MathF.Round(_atlasPan.Y / mapSize * _atlas.SpanTiles);
        StartAtlas(centerX, centerY);
    }

    private void TravelToAtlasPosition(Vector2 mouse)
    {
        if (_atlas is null) return;
        var mapSize = AtlasDisplaySize();
        var topLeft = new Vector2(
            (Size.X - mapSize) * .5f + _atlasPan.X,
            (Size.Y - mapSize) * .5f + _atlasPan.Y);
        var uv = (mouse - topLeft) / mapSize;
        if (uv.X < 0 || uv.Y < 0 || uv.X > 1 || uv.Y > 1) return;
        var tileX = _atlas.CenterTileX + (uv.X - .5f) * _atlas.SpanTiles;
        var tileY = _atlas.CenterTileY + (uv.Y - .5f) * _atlas.SpanTiles;
        var projected = new Vector2((tileX - tileY) * 48, (tileX + tileY) * 24);
        _zoom = .8f;
        _camera = -projected * _zoom;
        _atlasOpen = false;
        _atlasPan = Vector2.Zero;
        StreamWorld();
    }

    private float AtlasDisplaySize() => Math.Max(1f, Math.Max(Size.X, Size.Y));

    private void ZoomAtlas(float wheelOffset)
    {
        if (_atlas is null || _atlasTask is { IsCompleted: false } || wheelOffset == 0) return;
        var nextChunksAcross = wheelOffset > 0
            ? Math.Max(4, _atlasChunksAcross / 2)
            : Math.Min(64, _atlasChunksAcross * 2);
        if (nextChunksAcross == _atlasChunksAcross) return;

        var mapSize = AtlasDisplaySize();
        var topLeft = new Vector2(
            (Size.X - mapSize) * .5f + _atlasPan.X,
            (Size.Y - mapSize) * .5f + _atlasPan.Y);
        var uv = (MouseState.Position - topLeft) / mapSize;
        var tileUnderCursorX = _atlas.CenterTileX + (uv.X - .5f) * _atlas.SpanTiles;
        var tileUnderCursorY = _atlas.CenterTileY + (uv.Y - .5f) * _atlas.SpanTiles;
        var nextSpan = nextChunksAcross * WorldChunk.Size;
        var nextCenterX = tileUnderCursorX - (uv.X - .5f) * nextSpan;
        var nextCenterY = tileUnderCursorY - (uv.Y - .5f) * nextSpan;
        _atlasChunksAcross = nextChunksAcross;
        StartAtlas((int)MathF.Round(nextCenterX), (int)MathF.Round(nextCenterY));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_screen != ScreenState.WorldPreview || e.OffsetY == 0) return;
        if (_atlasOpen)
        {
            ZoomAtlas(e.OffsetY);
            return;
        }
        var old = _zoom;
        _zoom = Math.Clamp(old * MathF.Pow(1.12f, e.OffsetY), 0.45f, 1.75f);
        var cursor = MouseState.Position - new Vector2(Size.X / 2f, Size.Y / 2f);
        _camera = cursor - (cursor - _camera) * (_zoom / old);
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
    }

    protected override void OnRenderFrame(FrameEventArgs e)
    {
        base.OnRenderFrame(e);
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
        GL.ClearColor(0.08f, 0.09f, 0.08f, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        if (_screen == ScreenState.WorldPreview)
        {
            if (_atlasOpen) RenderAtlas();
            else if (_mode == PreviewMode.Island) RenderIsland();
            else if (_mode == PreviewMode.World) RenderWorld();
            else RenderWorldPreview();
        }
        else RenderLoading();
        SwapBuffers();
    }

    private void RenderLoading()
    {
        var margin = 90;
        var width = Math.Max(0, FramebufferSize.X - margin * 2);
        var filled = (int)(width * Math.Clamp(_done / (float)_total, 0, 1));
        GL.Enable(EnableCap.ScissorTest);
        GL.Scissor(margin, FramebufferSize.Y / 2 - 14, filled, 28);
        GL.ClearColor(0.32f, 0.62f, 0.25f, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.Disable(EnableCap.ScissorTest);
        Title = $"Island RPG - Loading {_done}/{_total}: {_current}";
    }

    private void RenderAtlas()
    {
        if (_atlasTexture != 0)
        {
            var width = Math.Max(1, Size.X);
            var height = Math.Max(1, Size.Y);
            var mapSize = Math.Max(width, height);
            var center = new Vector2(width * .5f, height * .5f) + _atlasPan;
            var left = (center.X - mapSize * .5f - width * .5f) * 2 / width;
            var right = (center.X + mapSize * .5f - width * .5f) * 2 / width;
            var top = -(center.Y - mapSize * .5f - height * .5f) * 2 / height;
            var bottom = -(center.Y + mapSize * .5f - height * .5f) * 2 / height;
            GL.UseProgram(_program);
            GL.Uniform1(GL.GetUniformLocation(_program, "image"), 0);
            GL.Uniform1(GL.GetUniformLocation(_program, "opacity"), 1f);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _atlasTexture);
            Draw([left,top,0,0, left,bottom,0,1, right,bottom,1,1, right,top,1,0]);

            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor((int)center.X - 1, height - (int)center.Y - 10, 3, 21);
            GL.ClearColor(.95f, .82f, .24f, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.Scissor((int)center.X - 10, height - (int)center.Y - 1, 21, 3);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.Disable(EnableCap.ScissorTest);
        }

        if (_atlasTask is not null)
        {
            const int margin = 90;
            const int barHeight = 18;
            var width = Math.Max(0, FramebufferSize.X - margin * 2);
            var atlasDone = Volatile.Read(ref _atlasDone);
            var atlasTotal = Volatile.Read(ref _atlasTotal);
            var filled = (int)(width * Math.Clamp(atlasDone / (float)Math.Max(1, atlasTotal), 0, 1));
            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(margin, 32, width, barHeight);
            GL.ClearColor(.12f, .14f, .12f, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.Scissor(margin, 32, filled, barHeight);
            GL.ClearColor(.35f, .68f, .28f, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.Disable(EnableCap.ScissorTest);
            Title = $"Island RPG - Mapping {atlasDone}/{atlasTotal} chunks";
        }
        else if (_atlas is not null)
            Title = $"Island RPG - Atlas centered {_atlas.CenterTileX}, {_atlas.CenterTileY} - " +
                    "drag or double-click to travel";
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
            var screen = new Vector2(Size.X / 2f, Size.Y / 2f) + _camera + world * _zoom;
            if (screen.X < -250 || screen.Y < -250 || screen.X > Size.X + 250 || screen.Y > Size.Y + 250)
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
            var screen = new Vector2(Size.X / 2f, Size.Y / 2f) + _camera + world * _zoom;
            if (screen.X < -150 || screen.Y < -150 || screen.X > Size.X + 150 || screen.Y > Size.Y + 150)
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
        foreach (var gpu in _worldChunks.Values.Where(IsChunkVisible))
            DrawWorldChunkTerrain(gpu);

        var vertices = new List<float>();
        foreach (var item in _worldChunks.Values
                     .SelectMany(gpu => gpu.Chunk.Trees.Select(tree => (Tree: tree, Gpu: gpu)))
                     .OrderBy(item => item.Tree.X + item.Tree.Y))
        {
            var tree = item.Tree;
            var tile = _worldChunks[new(
                FloorDiv(tree.X, WorldChunk.Size), FloorDiv(tree.Y, WorldChunk.Size))]
                .Chunk.Tiles[
                    PositiveMod(tree.Y, WorldChunk.Size) * WorldChunk.Size +
                    PositiveMod(tree.X, WorldChunk.Size)];
            var height = (tile.North + tile.East + tile.South + tile.West) / 4f;
            var world = new Vector2(
                (tree.X - tree.Y) * 48,
                (tree.X + tree.Y + 1) * 24 - height * 12);
            var shadowName = tree.GraphicName[..^2] + "N0";
            AddTreeQuad(shadowName, world, item.Gpu.Opacity, vertices);
            AddTreeQuad(tree.GraphicName, world, item.Gpu.Opacity, vertices);
        }
        DrawTreeBatch(vertices);
    }

    private void AddTreeQuad(string graphicName, Vector2 world, float opacity, List<float> vertices)
    {
        if (!_treeAtlas.TryGetValue(graphicName, out var entry)) return;
        var frame = entry.Frame;
        var width = Math.Max(1, Size.X);
        var height = Math.Max(1, Size.Y);
        var screen = new Vector2(width / 2f, height / 2f) + _camera + world * _zoom;
        var margin = Math.Max(frame.Width, frame.Height) * _zoom;
        if (screen.X < -margin || screen.Y < -margin ||
            screen.X > width + margin || screen.Y > height + margin)
            return;
        var halfW = frame.Width * _zoom / width;
        var halfH = frame.Height * _zoom / height;
        var centerX = (((frame.Width / 2f - frame.HotspotX) + world.X) * _zoom + _camera.X) *
                      2 / width;
        var centerY = ((frame.HotspotY - frame.Height / 2f) * _zoom -
                       _camera.Y - world.Y * _zoom) * 2 / height;
        Add(centerX - halfW, centerY + halfH, entry.U0, entry.V0);
        Add(centerX - halfW, centerY - halfH, entry.U0, entry.V1);
        Add(centerX + halfW, centerY - halfH, entry.U1, entry.V1);
        Add(centerX - halfW, centerY + halfH, entry.U0, entry.V0);
        Add(centerX + halfW, centerY - halfH, entry.U1, entry.V1);
        Add(centerX + halfW, centerY + halfH, entry.U1, entry.V0);

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
        Title = $"Island RPG - World {_worldSeed} - {_worldChunks.Count} chunks" +
                (_pendingChunkTask is null ? "" : " - streaming");
    }

    private void PrepareWorldTerrain()
    {
        _terrainArray = UploadTerrainArray();
        _waterNormalArray = UploadWaterNormalArray();
        _terrainProgram = CreateTerrainProgram();
        PrepareTreeAtlas();
    }

    private void PrepareTreeAtlas()
    {
        const int atlasWidth = 2048;
        const int padding = 1;
        var placements = new List<(LoadedGraphic Asset, SpriteFrame Frame, int X, int Y)>();
        var x = padding;
        var y = padding;
        var rowHeight = 0;
        foreach (var asset in _worldAssets)
        {
            var frame = asset.Sprite.Frames[0];
            if (x + frame.Width + padding > atlasWidth)
            {
                x = padding;
                y += rowHeight + padding;
                rowHeight = 0;
            }
            placements.Add((asset, frame, x, y));
            x += frame.Width + padding;
            rowHeight = Math.Max(rowHeight, frame.Height);
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
            _treeAtlas[placement.Asset.Definition.Name] = new(
                placement.Frame,
                placement.X / (float)atlasWidth,
                placement.Y / (float)atlasHeight,
                (placement.X + placement.Frame.Width) / (float)atlasWidth,
                (placement.Y + placement.Frame.Height) / (float)atlasHeight);
        }
        _treeAtlasTexture = Upload(atlasWidth, atlasHeight, rgba);
        _treeBatchVbo = GL.GenBuffer();
    }

    private GpuWorldChunk UploadWorldChunk(WorldChunk chunk)
    {
        Vector2 Project(float x, float y, float z) =>
            new((x - y) * 48, (x + y) * 24 - z * 12);
        var layers = Enum.GetValues<Biome>().ToDictionary(biome => biome, biome => (float)(int)biome);
        var vertices = new List<float>(WorldChunk.Size * WorldChunk.Size * 6 * 11);
        foreach (var tile in chunk.Tiles)
        {
            var localX = PositiveMod(tile.X, WorldChunk.Size);
            var localY = PositiveMod(tile.Y, WorldChunk.Size);
            var points = new[]
            {
                Project(tile.X, tile.Y, tile.North),
                Project(tile.X + 1, tile.Y, tile.East),
                Project(tile.X + 1, tile.Y + 1, tile.South),
                Project(tile.X, tile.Y + 1, tile.West)
            };
            var local = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            var north = LayerAt(localX, localY - 1, tile.Biome);
            var east = LayerAt(localX + 1, localY, tile.Biome);
            var south = LayerAt(localX, localY + 1, tile.Biome);
            var west = LayerAt(localX - 1, localY, tile.Biome);
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
            }
        }
        var vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float),
            vertices.ToArray(), BufferUsageHint.StaticDraw);
        var weights = UploadChunkBiomeWeights(chunk);
        return new(chunk, vbo, vertices.Count / 11, weights.A, weights.B, weights.Shore);

        float LayerAt(int x, int y, Biome fallback) =>
            layers[x < 0 || y < 0 || x >= WorldChunk.Size || y >= WorldChunk.Size
                ? fallback
                : chunk.Tiles[y * WorldChunk.Size + x].Biome];
    }

    private static (int A, int B, int Shore) UploadChunkBiomeWeights(WorldChunk chunk)
    {
        return (Upload(chunk.BiomeWeightsA, PixelInternalFormat.Rgba8, PixelFormat.Rgba),
            Upload(chunk.BiomeWeightsB, PixelInternalFormat.Rgba8, PixelFormat.Rgba),
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
        var screen = new Vector2(Size.X * .5f, Size.Y * .5f) + _camera + projected * _zoom;
        var halfWidth = WorldChunk.Size * 48 * _zoom + 96;
        var halfHeight = WorldChunk.Size * 24 * _zoom + 128;
        return screen.X + halfWidth >= 0 && screen.X - halfWidth <= Size.X &&
               screen.Y + halfHeight >= 0 && screen.Y - halfHeight <= Size.Y;
    }

    private void DrawWorldChunkTerrain(GpuWorldChunk gpu)
    {
        GL.UseProgram(_terrainProgram);
        GL.Uniform2(GL.GetUniformLocation(_terrainProgram, "viewport"),
            (float)Math.Max(1, Size.X), (float)Math.Max(1, Size.Y));
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
        GL.BindTexture(TextureTarget.Texture2DArray, _waterNormalArray);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "waterNormals"), 3);
        GL.ActiveTexture(TextureUnit.Texture4);
        GL.BindTexture(TextureTarget.Texture2D, gpu.ShoreDistance);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "shoreDistance"), 4);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "time"), _waterTime);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "opacity"), gpu.Opacity);
        GL.BindBuffer(BufferTarget.ArrayBuffer, gpu.Vbo);
        const int stride = 11 * sizeof(float);
        for (var attribute = 0; attribute < 5; attribute++) GL.EnableVertexAttribArray(attribute);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 4 * sizeof(float));
        GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.VertexAttribPointer(4, 2, VertexAttribPointerType.Float, false, stride, 9 * sizeof(float));
        GL.DrawArrays(PrimitiveType.Triangles, 0, gpu.VertexCount);
    }

    private void UnloadWorldChunk(ChunkCoordinate coordinate, bool save)
    {
        if (!_worldChunks.Remove(coordinate, out var gpu)) return;
        if (save && _worldStore is not null)
        {
            var store = _worldStore;
            var chunk = gpu.Chunk;
            var previous = _saveTail;
            _saveTail = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                store.Save(chunk);
            });
        }
        GL.DeleteBuffer(gpu.Vbo);
        GL.DeleteTexture(gpu.WeightsA);
        GL.DeleteTexture(gpu.WeightsB);
        GL.DeleteTexture(gpu.ShoreDistance);
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
            Biome.DeepWater => "g_wtr_00_color",
            Biome.ShallowWater => "g_sha_00_color",
            Biome.Beach => "g_bch_00_color",
            Biome.Forest => "g_for_00_color",
            Biome.Highland => "g_gr3_00_color",
            Biome.Rock => "g_rck_00_COLOR",
            Biome.Snow => "g_sno_00_color",
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
        (_biomeWeightsA, _biomeWeightsB, _shoreDistance) = UploadBiomeWeights();
        _terrainProgram = CreateTerrainProgram();

        float LayerAt(int x, int y, Biome fallback) =>
            layers[x < 0 || y < 0 || x >= IslandMap.Size || y >= IslandMap.Size
                ? fallback
                : _island.Tiles[y * IslandMap.Size + x].Biome];
    }

    private void DrawIslandTerrainBatch()
    {
        GL.UseProgram(_terrainProgram);
        GL.Uniform2(GL.GetUniformLocation(_terrainProgram, "viewport"), (float)Math.Max(1, Size.X), (float)Math.Max(1, Size.Y));
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
        GL.BindTexture(TextureTarget.Texture2DArray, _waterNormalArray);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "waterNormals"), 3);
        GL.ActiveTexture(TextureUnit.Texture4);
        GL.BindTexture(TextureTarget.Texture2D, _shoreDistance);
        GL.Uniform1(GL.GetUniformLocation(_terrainProgram, "shoreDistance"), 4);
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
        GL.DrawArrays(PrimitiveType.Triangles, 0, _islandVertexCount);
    }

    private void DrawSprite(SpriteFrame frame, int texture, Vector2 world, float opacity = 1)
    {
        var width = Math.Max(1, Size.X);
        var height = Math.Max(1, Size.Y);
        var screen = new Vector2(width / 2f, height / 2f) + _camera + world * _zoom;
        var margin = Math.Max(frame.Width, frame.Height) * _zoom;
        if (screen.X < -margin || screen.Y < -margin ||
            screen.X > width + margin || screen.Y > height + margin)
            return;
        var halfW = frame.Width * _zoom / width;
        var halfH = frame.Height * _zoom / height;
        var x = (((frame.Width / 2f - frame.HotspotX) + world.X) * _zoom + _camera.X) * 2 / width;
        var y = ((frame.HotspotY - frame.Height / 2f) * _zoom - _camera.Y - world.Y * _zoom) * 2 / height;
        GL.UseProgram(_program);
        GL.Uniform1(GL.GetUniformLocation(_program, "image"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "opacity"), opacity);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        Draw([x-halfW,y+halfH,0,0, x-halfW,y-halfH,0,1, x+halfW,y-halfH,1,1, x+halfW,y+halfH,1,0]);
    }

    private void DrawTerrain(TerrainTile tile, int texture, Vector2 world)
    {
        const float previewSize = 128;
        var width = Math.Max(1, Size.X);
        var height = Math.Max(1, Size.Y);
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

    private (int A, int B, int Shore) UploadBiomeWeights()
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
                else b[pixel * 4 + channel - 4] = value;
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
            waterPixels[y * size + x] = tile.Biome is Biome.DeepWater or Biome.ShallowWater;
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
            uniform vec2 viewport;
            uniform vec2 camera;
            uniform float zoom;
            out vec2 uv;
            out vec2 mapUv;
            void main() {
                vec2 pixel = world * zoom + camera;
                gl_Position = vec4(pixel.x * 2.0 / viewport.x,
                                  -pixel.y * 2.0 / viewport.y, 0.0, 1.0);
                uv = textureUv;
                mapUv = clamp(tileUv, 0.0, 1.0);
            }
            """;
        const string fragment = """
            #version 330 core
            in vec2 uv;
            in vec2 mapUv;
            uniform sampler2DArray terrain;
            uniform sampler2D biomeWeightsA;
            uniform sampler2D biomeWeightsB;
            uniform sampler2D shoreDistance;
            uniform sampler2DArray waterNormals;
            uniform float time;
            uniform float opacity;
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
            vec2 normalXY(float layer, vec2 coordinates) {
                return texture(waterNormals, vec3(coordinates, layer)).xy * 2.0 - 1.0;
            }
            void main() {
                vec4 a = texture(biomeWeightsA, mapUv);
                vec4 b = texture(biomeWeightsB, mapUv);
                float shorelineDistance = (texture(shoreDistance, mapUv).r * 2.0 - 1.0) * 8.0;
                float shorelineProximity = 1.0 - smoothstep(0.0, 3.0, abs(shorelineDistance));
                float total = max(dot(a, vec4(1.0)) + dot(b, vec4(1.0)), 0.001);
                color = vec4(0.0);
                float waterWeight = a.r + a.g;
                float waterCoverage = clamp(waterWeight / total, 0.0, 1.0);
                float surfaceEffect = smoothstep(0.38, 0.72, waterCoverage);
                vec3 waterNormal = vec3(0.0, 0.0, 1.0);
                vec2 waterDistortion = vec2(0.0);
                vec2 primaryFlow = vec2(1.0, 0.0);
                vec2 secondaryFlow = vec2(0.0, 1.0);
                float waveSlope = 0.0;
                if (waterWeight > 0.002) {
                    // Smooth regional flow cells prevent the entire ocean from
                    // travelling in one visibly uniform direction.
                    float flowAngle = (valueNoise(mapUv * 7.0) - 0.5) * 5.2;
                    primaryFlow = vec2(cos(flowAngle), sin(flowAngle));
                    float secondAngle = flowAngle + 1.75 +
                                        (valueNoise(mapUv * 11.0 + 19.7) - 0.5) * 0.8;
                    secondaryFlow = vec2(cos(secondAngle), sin(secondAngle));
                    vec2 deepA = normalXY(0.0, uv * 1.35 + primaryFlow * time * 0.034);
                    vec2 deepB = normalXY(1.0, uv * 0.73 + secondaryFlow * time * 0.025);
                    vec2 shoreA = normalXY(2.0, uv * 1.65 + primaryFlow * time * 0.021);
                    vec2 shoreB = normalXY(3.0, uv * 0.92 - secondaryFlow * time * 0.019);
                    float shallow = a.g / max(waterWeight, 0.001);
                    vec2 waves = mix(deepA * 0.62 + deepB * 0.38,
                                     shoreA * 0.42 + shoreB * 0.25, shallow);
                    waveSlope = length(waves);
                    waterNormal = normalize(vec3(waves * mix(1.12, 0.68, shallow), 1.0));
                    waterDistortion = waterNormal.xy * mix(0.027, 0.014, shallow);
                }
                if (a.r > 0.002) color += sampleLayerAt(0.0, uv + waterDistortion) * a.r;
                if (a.g > 0.002) color += sampleLayerAt(1.0, uv + waterDistortion) * a.g;
                if (a.b > 0.002) color += sampleLayer(2.0) * a.b;
                if (a.a > 0.002) color += sampleLayer(3.0) * a.a;
                if (b.r > 0.002) color += sampleLayer(4.0) * b.r;
                if (b.g > 0.002) color += sampleLayer(5.0) * b.g;
                if (b.b > 0.002) color += sampleLayer(6.0) * b.b;
                if (b.a > 0.002) color += sampleLayer(7.0) * b.a;
                color /= total;
                if (waterWeight > 0.002) {
                    vec3 lightDirection = normalize(vec3(-0.38, -0.48, 0.79));
                    float sparkle = pow(max(dot(waterNormal, lightDirection), 0.0), 30.0);
                    float broadHighlight = pow(max(dot(waterNormal, lightDirection), 0.0), 7.0);
                    float crest = smoothstep(0.22, 0.62, length(waterNormal.xy));
                    vec3 reflection = vec3(0.38, 0.70, 0.84) * broadHighlight * 0.11 +
                                      vec3(0.86, 0.97, 1.0) * sparkle * 0.38 +
                                      vec3(0.24, 0.58, 0.67) * crest * 0.075;
                    color.rgb = mix(color.rgb, color.rgb * (0.96 + broadHighlight * 0.11), surfaceEffect);
                    color.rgb += reflection * surfaceEffect;

                    // Whitecaps only form where an animated wave is steep and
                    // the moving breakup field selects a short-lived crest.
                    float breakupA = valueNoise(uv * 2.2 + primaryFlow * time * 0.18);
                    float breakupB = valueNoise(uv * 5.4 - secondaryFlow * time * 0.11 + 31.4);
                    float breakup = breakupA * 0.62 + breakupB * 0.38;
                    float steepCrest = smoothstep(0.48, 0.88, waveSlope);
                    float sparsePatch = smoothstep(0.61, 0.79, breakup);
                    float shallowBoost = smoothstep(0.10, 0.62, a.g / max(waterWeight, 0.001));
                    float shoreBoost = max(shallowBoost,
                        shorelineProximity * step(0.0, shorelineDistance));
                    float foam = steepCrest * sparsePatch * mix(0.42, 0.82, shoreBoost);
                    vec3 foamColor = vec3(0.84, 0.94, 0.95);
                    color.rgb = mix(color.rgb, foamColor, foam * surfaceEffect * 0.48);
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
            "uniform float opacity;void main(){c=texture(image,uv);c.a*=opacity*alpha;}");
        var program = GL.CreateProgram(); GL.AttachShader(program, vs); GL.AttachShader(program, fs); GL.LinkProgram(program);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        return program;
    }

    protected override void OnUnload()
    {
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
        if (_terrainArray != 0) GL.DeleteTexture(_terrainArray);
        if (_biomeWeightsA != 0) GL.DeleteTexture(_biomeWeightsA);
        if (_biomeWeightsB != 0) GL.DeleteTexture(_biomeWeightsB);
        if (_shoreDistance != 0) GL.DeleteTexture(_shoreDistance);
        if (_waterNormalArray != 0) GL.DeleteTexture(_waterNormalArray);
        if (_atlasTexture != 0) GL.DeleteTexture(_atlasTexture);
        if (_islandVbo != 0) GL.DeleteBuffer(_islandVbo);
        if (_streamVbo != 0) GL.DeleteBuffer(_streamVbo);
        if (_treeBatchVbo != 0) GL.DeleteBuffer(_treeBatchVbo);
        if (_treeAtlasTexture != 0) GL.DeleteTexture(_treeAtlasTexture);
        if (_terrainProgram != 0) GL.DeleteProgram(_terrainProgram);
        GL.DeleteVertexArray(_vao);
        GL.DeleteProgram(_program);
        base.OnUnload();
        if (saveFailure is not null)
            throw new IOException("One or more chunks could not be saved during shutdown.", saveFailure);
    }
}
