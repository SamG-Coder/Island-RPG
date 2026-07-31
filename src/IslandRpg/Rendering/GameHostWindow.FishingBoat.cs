using IslandRpg.Assets;
using IslandRpg.Gameplay;
using IslandRpg.Persistence;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;
using StbImageSharp;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const string FishingBoatGraphicName = "SHIPF5SF";
    private const int FishingRaftCellWidth = 160;
    private const int FishingRaftCellHeight = 128;
    private const int FishingRaftDirectionCount = 5;
    private const float FishingBoatDeckRadius = .45f;
    private const float FishingBoatRiderMoveSpeed = 1.25f;
    private const int FishingBoatRiderApproachSteps = 5;
    private static readonly bool RenderFishingBoatSails = true;
    private const float FishingBoatSailScale = .75f;
    private const float FishingBoatSailOpacity = .75f;
    private const float FishingBoatFishingSailOpacity = .28f;
    private string? _queuedBoatFishKey;
    private Vector2 _queuedBoatFishTarget;
    private Vector2? _queuedBoatDisembarkTarget;
    private Vector2? _queuedBoatDisembarkLanding;
    private bool _fishingBoatDisembarkTargeting;
    private Vector2 _fishingBoatRiderOffset;
    private Vector2 _fishingBoatRiderTargetOffset;
    private static readonly Vector2i[] FishingBoatRiderOffsets =
    [
        new(0, -29),
        new(0, -29),
        new(0, -29),
        new(0, -29),
        new(0, -29)
    ];
    private static readonly Vector2i[] FishingBoatFishingRiderOffsets =
    [
        new(0, -14),
        new(-22, -18),
        new(-31, -29),
        new(-22, -40),
        new(0, -44)
    ];
    private static readonly Vector2i[] FishingBoatSailOffsets =
    [
        new(-33, -12),
        new(-15, -14),
        new(0, -5),
        new(-15, -17),
        new(19, -34)
    ];

    private void InitializeFishingBoat(WorldPlayerState state)
    {
        _queuedBoatFishKey = null;
        _queuedBoatDisembarkTarget = null;
        _queuedBoatDisembarkLanding = null;
        _fishingBoatDisembarkTargeting = false;
        _fishingBoatRiderOffset = Vector2.Zero;
        _fishingBoatRiderTargetOffset = Vector2.Zero;
        if (_player is null)
        {
            _fishingBoat = null;
            _fishingBoatBoarded = false;
            return;
        }

        var saved = state.FishingBoatX is { } x &&
                    state.FishingBoatY is { } y
            ? new Vector2(x, y)
            : FishingBoatTravel.FindInitialPosition(
                _worldSeed, _player.Position);
        var terrain = InfiniteWorldGenerator.BiomeAt(
            _worldSeed,
            (int)MathF.Floor(saved.X),
            (int)MathF.Floor(saved.Y));
        if (!FishingBoatTravel.IsNavigable(terrain))
            saved = FishingBoatTravel.FindInitialPosition(
                _worldSeed, _player.Position);

        _fishingBoat = new WorldEntity(saved) { MoveSpeed = 3.4f };
        _fishingBoat.Face(new(
            state.FishingBoatFacingX,
            state.FishingBoatFacingY));
        _fishingBoatBoarded =
            _activeWorldLevel == (int)WorldLevel.Overworld &&
            state.FishingBoatBoarded &&
            (_player.Position - saved).Length <= 3;
        if (_fishingBoatBoarded)
            _player.TeleportTo(saved);
        _fishingBoatAnimationTime = 0;
    }

    private void PrepareFishingBoatAnimation()
    {
        var graphic = _catalog!.Graphics.Values.FirstOrDefault(value =>
            value.Definition.Name.Equals(
                FishingBoatGraphicName,
                StringComparison.OrdinalIgnoreCase));
        if (graphic is null) return;
        _fishingBoatAnimation = new(
            graphic,
            graphic.Sprite.Frames.Select(Upload).ToArray(),
            graphic.Definition.FrameRate is > .015f and < 2f
                ? graphic.Definition.FrameRate
                : .1f);
        PrepareFishingRaftSprites();
        PrepareFishingBoatComposites();
    }

    private void PrepareFishingRaftSprites()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Resources",
            "Images",
            "fishing-raft-directions.png");
        using var stream = File.OpenRead(path);
        var sheet = ImageResult.FromStream(
            stream, ColorComponents.RedGreenBlueAlpha);
        if (sheet.Width != FishingRaftCellWidth *
                FishingRaftDirectionCount ||
            sheet.Height != FishingRaftCellHeight)
            throw new InvalidOperationException(
                "The fishing raft sheet must contain five 160x128 cells.");

        _fishingRaftFrames = new SpriteFrame[FishingRaftDirectionCount];
        _fishingRaftTextures = new int[FishingRaftDirectionCount];
        for (var cell = 0; cell < FishingRaftDirectionCount; cell++)
        {
            var pixels = new byte[
                FishingRaftCellWidth * FishingRaftCellHeight * 4];
            for (var row = 0; row < FishingRaftCellHeight; row++)
                Buffer.BlockCopy(
                    sheet.Data,
                    (row * sheet.Width +
                     cell * FishingRaftCellWidth) * 4,
                    pixels,
                    row * FishingRaftCellWidth * 4,
                    FishingRaftCellWidth * 4);
            pixels = CenterFishingRaftPixels(pixels);
            var frame = new SpriteFrame(
                FishingRaftCellWidth,
                FishingRaftCellHeight,
                FishingRaftCellWidth / 2,
                FishingRaftCellHeight - 1,
                pixels);
            _fishingRaftFrames[cell] = frame;
            _fishingRaftTextures[cell] = Upload(frame);
        }
    }

    private static byte[] CenterFishingRaftPixels(byte[] source)
    {
        var minX = FishingRaftCellWidth;
        var maxX = -1;
        var minY = FishingRaftCellHeight;
        var maxY = -1;
        for (var y = 0; y < FishingRaftCellHeight; y++)
        for (var x = 0; x < FishingRaftCellWidth; x++)
        {
            if (source[
                    (y * FishingRaftCellWidth + x) * 4 + 3] < 16)
                continue;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }
        if (maxX < minX) return source;

        const int waterlineY = 117;
        var shiftX = FishingRaftCellWidth / 2 -
                     (minX + maxX + 1) / 2;
        var shiftY = waterlineY - maxY;
        if (shiftX == 0 && shiftY == 0) return source;

        var centered = new byte[source.Length];
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var targetX = x + shiftX;
            var targetY = y + shiftY;
            if ((uint)targetX >= FishingRaftCellWidth ||
                (uint)targetY >= FishingRaftCellHeight)
                continue;
            Buffer.BlockCopy(
                source,
                (y * FishingRaftCellWidth + x) * 4,
                centered,
                (targetY * FishingRaftCellWidth + targetX) * 4,
                4);
        }
        return centered;
    }

    private void PrepareFishingBoatComposites()
    {
        if (_fishingBoatAnimation is null ||
            _fishingRaftFrames.Length != FishingRaftDirectionCount)
            return;
        const int canvasWidth = 256;
        const int canvasHeight = 256;
        const int anchorX = canvasWidth / 2;
        const int anchorY = 230;
        var sailFrames = _fishingBoatAnimation.Graphic.Sprite.Frames;
        var sailFramesPerAngle = Math.Max(
            1, sailFrames.Count / FishingRaftDirectionCount);

        foreach (var gender in Enum.GetValues<EntityGender>())
        {
            if (!_entityAnimations.TryGetValue(
                    (gender, EntityAction.Idle), out var idle))
                continue;
            var idleFrames = idle.Graphic.Sprite.Frames;
            var idleFramesPerAngle = Math.Max(
                1, idleFrames.Count / FishingRaftDirectionCount);
            foreach (var boarded in new[] { false, true })
            {
                var frames = new SpriteFrame[sailFrames.Count];
                var textures = new int[sailFrames.Count];
                for (var index = 0; index < sailFrames.Count; index++)
                {
                    var angle = Math.Min(
                        FishingRaftDirectionCount - 1,
                        index / sailFramesPerAngle);
                    var pixels = new byte[
                        canvasWidth * canvasHeight * 4];
                    CompositeFishingBoatLayer(
                        _fishingRaftFrames[angle],
                        pixels, canvasWidth, canvasHeight,
                        anchorX, anchorY);
                    if (boarded)
                    {
                        var riderOffset =
                            FishingBoatRiderOffsets[angle];
                        CompositeFishingBoatLayer(
                            idleFrames[
                                angle * idleFramesPerAngle],
                            pixels, canvasWidth, canvasHeight,
                            anchorX + riderOffset.X,
                            anchorY + riderOffset.Y);
                    }
                    if (RenderFishingBoatSails)
                        CompositeFishingBoatSail(
                            sailFrames[index],
                            pixels, canvasWidth, canvasHeight,
                            anchorX, anchorY, angle,
                            FishingBoatSailOpacity);
                    var frame = new SpriteFrame(
                        canvasWidth, canvasHeight,
                        anchorX, anchorY, pixels);
                    frames[index] = frame;
                    textures[index] = Upload(frame);
                }
                _fishingBoatComposites[(gender, boarded)] =
                    new(frames, textures);
            }
            PrepareFishingBoatFishingComposite(
                gender, sailFrames, sailFramesPerAngle);
            PrepareFishingBoatApproachComposites(
                gender,
                idle.Graphic.Sprite.Frames,
                sailFrames,
                sailFramesPerAngle);
        }
    }

    private void PrepareFishingBoatApproachComposites(
        EntityGender gender,
        IReadOnlyList<SpriteFrame> idleFrames,
        IReadOnlyList<SpriteFrame> sailFrames,
        int sailFramesPerAngle)
    {
        const int canvasWidth = 256;
        const int canvasHeight = 256;
        const int anchorX = canvasWidth / 2;
        const int anchorY = 230;
        var idleFramesPerAngle = Math.Max(
            1, idleFrames.Count / FishingRaftDirectionCount);
        for (var step = 0;
             step < FishingBoatRiderApproachSteps;
             step++)
        {
            var progress = step /
                (float)(FishingBoatRiderApproachSteps - 1);
            var frames = new SpriteFrame[FishingRaftDirectionCount];
            var textures = new int[FishingRaftDirectionCount];
            for (var angle = 0;
                 angle < FishingRaftDirectionCount;
                 angle++)
            {
                var pixels = new byte[canvasWidth * canvasHeight * 4];
                CompositeFishingBoatLayer(
                    _fishingRaftFrames[angle],
                    pixels, canvasWidth, canvasHeight,
                    anchorX, anchorY);
                var center = FishingBoatRiderOffsets[angle];
                var edge = FishingBoatFishingRiderOffsets[angle];
                var riderOffset = new Vector2i(
                    (int)MathF.Round(
                        float.Lerp(center.X, edge.X, progress)),
                    (int)MathF.Round(
                        float.Lerp(center.Y, edge.Y, progress)));
                CompositeFishingBoatLayer(
                    idleFrames[angle * idleFramesPerAngle],
                    pixels, canvasWidth, canvasHeight,
                    anchorX + riderOffset.X,
                    anchorY + riderOffset.Y);
                if (RenderFishingBoatSails)
                    CompositeFishingBoatSail(
                        sailFrames[angle * sailFramesPerAngle],
                        pixels, canvasWidth, canvasHeight,
                        anchorX, anchorY, angle,
                        FishingBoatSailOpacity);
                var frame = new SpriteFrame(
                    canvasWidth, canvasHeight,
                    anchorX, anchorY, pixels);
                frames[angle] = frame;
                textures[angle] = Upload(frame);
            }
            _fishingBoatApproachComposites[(gender, step)] =
                new(frames, textures);
        }
    }

    private void PrepareFishingBoatFishingComposite(
        EntityGender gender,
        IReadOnlyList<SpriteFrame> sailFrames,
        int sailFramesPerAngle)
    {
        if (!_entityAnimations.TryGetValue(
                (gender, EntityAction.Fish), out var fishing))
            return;
        const int canvasWidth = 256;
        const int canvasHeight = 256;
        const int anchorX = canvasWidth / 2;
        const int anchorY = 230;
        var fishingFrames = fishing.Graphic.Sprite.Frames;
        var framesPerAngle = Math.Max(
            1, fishingFrames.Count / FishingRaftDirectionCount);
        var frames = new SpriteFrame[fishingFrames.Count];
        var textures = new int[fishingFrames.Count];
        for (var index = 0; index < fishingFrames.Count; index++)
        {
            var angle = Math.Min(
                FishingRaftDirectionCount - 1,
                index / framesPerAngle);
            var pixels = new byte[canvasWidth * canvasHeight * 4];
            CompositeFishingBoatLayer(
                _fishingRaftFrames[angle],
                pixels, canvasWidth, canvasHeight,
                anchorX, anchorY);
            var riderOffset =
                FishingBoatFishingRiderOffsets[angle];
            CompositeFishingBoatLayer(
                fishingFrames[index],
                pixels, canvasWidth, canvasHeight,
                anchorX + riderOffset.X,
                anchorY + riderOffset.Y);
            if (RenderFishingBoatSails)
            {
                var sailFrame = angle * sailFramesPerAngle +
                    index % framesPerAngle % sailFramesPerAngle;
                CompositeFishingBoatSail(
                    sailFrames[sailFrame],
                    pixels, canvasWidth, canvasHeight,
                    anchorX, anchorY, angle,
                    FishingBoatFishingSailOpacity);
            }
            var frame = new SpriteFrame(
                canvasWidth, canvasHeight,
                anchorX, anchorY, pixels);
            frames[index] = frame;
            textures[index] = Upload(frame);
        }
        _fishingBoatFishingComposites[gender] =
            new(frames, textures);
    }

    private static void CompositeFishingBoatSail(
        SpriteFrame source,
        byte[] destination,
        int destinationWidth,
        int destinationHeight,
        int anchorX,
        int anchorY,
        int angle,
        float opacity = 1)
    {
        var minX = source.Width;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            if (source.Rgba[(y * source.Width + x) * 4 + 3] == 0)
                continue;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        if (maxX < minX || maxY < 0)
            return;

        var bottomCentered = ScaleFishingBoatSail(source) with
        {
            HotspotX = (int)MathF.Round(
                (minX + maxX) * .5f * FishingBoatSailScale),
            HotspotY = (int)MathF.Round(
                maxY * FishingBoatSailScale)
        };
        var offset = FishingBoatSailOffsets[Math.Clamp(
            angle, 0, FishingBoatSailOffsets.Length - 1)];
        CompositeFishingBoatLayer(
            bottomCentered,
            destination,
            destinationWidth,
            destinationHeight,
            anchorX + offset.X,
            anchorY + offset.Y,
            opacity);
    }

    private static SpriteFrame ScaleFishingBoatSail(SpriteFrame source)
    {
        var width = Math.Max(
            1, (int)MathF.Round(source.Width * FishingBoatSailScale));
        var height = Math.Max(
            1, (int)MathF.Round(source.Height * FishingBoatSailScale));
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sourceX = Math.Min(
                source.Width - 1,
                (int)(x / FishingBoatSailScale));
            var sourceY = Math.Min(
                source.Height - 1,
                (int)(y / FishingBoatSailScale));
            Buffer.BlockCopy(
                source.Rgba,
                (sourceY * source.Width + sourceX) * 4,
                pixels,
                (y * width + x) * 4,
                4);
        }
        return new(width, height, 0, 0, pixels);
    }

    private static void CompositeFishingBoatLayer(
        SpriteFrame source,
        byte[] destination,
        int destinationWidth,
        int destinationHeight,
        int anchorX,
        int anchorY,
        float opacity = 1)
    {
        var left = anchorX - source.HotspotX;
        var top = anchorY - source.HotspotY;
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var targetX = left + x;
            var targetY = top + y;
            if ((uint)targetX >= destinationWidth ||
                (uint)targetY >= destinationHeight)
                continue;
            var sourceOffset = (y * source.Width + x) * 4;
            var alpha = (byte)Math.Clamp(
                MathF.Round(
                    source.Rgba[sourceOffset + 3] *
                    Math.Clamp(opacity, 0, 1)),
                byte.MinValue,
                byte.MaxValue);
            if (alpha == 0) continue;
            var targetOffset =
                (targetY * destinationWidth + targetX) * 4;
            for (var channel = 0; channel < 3; channel++)
                destination[targetOffset + channel] = (byte)(
                    (source.Rgba[sourceOffset + channel] * alpha +
                     destination[targetOffset + channel] *
                     (255 - alpha)) / 255);
            destination[targetOffset + 3] = (byte)Math.Min(
                255,
                alpha + destination[targetOffset + 3] *
                (255 - alpha) / 255);
        }
    }

    private bool UpdateFishingBoatInput(bool leftDown, bool rightDown)
    {
        var leftPressed = leftDown && !_gameLeftWasDown;
        var rightPressed = rightDown && !_gameRightWasDown;
        if (_fishingBoat is null ||
            _activeWorldLevel != (int)WorldLevel.Overworld ||
            (!leftPressed && !rightPressed) ||
            IsPointerOverGameUi(MouseState.Position))
            return false;

        var pointer = SceneMousePosition();
        var target = ScreenToTerrain(pointer);
        if (!_fishingBoatBoarded)
        {
            if (!FishingBoatHitTest(pointer)) return false;
            _worldActions.QueuePath(
                _fishingBoat.Position,
                1.25f,
                WorldActionType.BoardFishingBoat,
                clearTreeActions: true);
            return true;
        }

        if (!_fishingBoatDisembarkTargeting &&
            TryGetFishUnderMouse(pointer, out _))
            return false;

        if (_fishingBoatDisembarkTargeting)
        {
            return TryChooseFishingBoatShore(target);
        }
        if (!rightPressed) return false;

        CancelFishingBoatAction();
        var biome = InfiniteWorldGenerator.BiomeAt(
            _worldSeed,
            (int)MathF.Floor(target.X),
            (int)MathF.Floor(target.Y));
        if (!FishingBoatTravel.IsNavigable(biome))
        {
            ReportBlockedAction(
                "boat-use-disembark",
                "Use the disembark action, then choose a shore.");
            return true;
        }

        QueueFishingBoatTravel(target);
        return true;
    }

    private void QueueFishingBoatTravel(Vector2 target)
    {
        if (_fishingBoat is null) return;
        CancelFishingBoatAction();
        var path = FishingBoatTravel.FindPath(
            _worldSeed, _fishingBoat.Position, target);
        if (path.Count == 0)
        {
            ReportBlockedAction(
                "boat-no-route",
                "The fishing boat cannot reach that water.");
            return;
        }
        _fishingBoat.FollowPath(path);
        _moveMarker = new(target, 0);
    }

    private void BeginFishingBoatDisembarkTargeting()
    {
        if (!_fishingBoatBoarded) return;
        CancelFishingBoatAction();
        _fishingBoatDisembarkTargeting =
            !_fishingBoatDisembarkTargeting;
        _chatUi.AddMessage(
            _fishingBoatDisembarkTargeting
                ? "Choose a shore to disembark."
                : "Disembark cancelled.",
            ChatMessageStyle.Action);
    }

    private bool TryChooseFishingBoatShore(Vector2 target)
    {
        if (_fishingBoat is null) return true;
        var biome = InfiniteWorldGenerator.BiomeAt(
            _worldSeed,
            (int)MathF.Floor(target.X),
            (int)MathF.Floor(target.Y));
        if (FishingBoatTravel.IsNavigable(biome))
        {
            ReportBlockedAction(
                "boat-disembark-water",
                "Choose dry land along the shore.");
            return true;
        }

        var immediateLanding = FishingBoatTravel.FindDisembarkLanding(
            _worldSeed, _fishingBoat.Position, target);
        if (immediateLanding is { } nearbyShore)
        {
            DisembarkFishingBoat(nearbyShore, target);
            return true;
        }

        var path = FishingBoatTravel.FindPath(
            _worldSeed, _fishingBoat.Position, target);
        if (path.Count == 0)
        {
            ReportBlockedAction(
                "boat-disembark-no-route",
                "The fishing boat cannot reach that shore.");
            return true;
        }
        var boatDestination = path[^1];
        var landing = FishingBoatTravel.FindDisembarkLanding(
            _worldSeed, boatDestination, target);
        if (landing is null)
        {
            ReportBlockedAction(
                "boat-disembark-no-landing",
                "There is no safe place to step ashore there.");
            return true;
        }
        _fishingBoatDisembarkTargeting = false;
        _queuedBoatDisembarkTarget = target;
        _queuedBoatDisembarkLanding = landing;
        _fishingBoat.FollowPath(path);
        _moveMarker = new(landing.Value, 0, Action: true);
        return true;
    }

    private void QueueFishingFromBoat(WorldFish fish)
    {
        if (_fishingBoat is null || _player is null) return;
        var target = new Vector2(fish.X, fish.Y);
        CancelFishingBoatAction();
        if ((_fishingBoat.Position - target).Length <=
            FishingNetReach() + FishingBoatDeckRadius)
        {
            _fishingBoat.Stop();
            _queuedBoatFishKey = fish.StableKey;
            _queuedBoatFishTarget = target;
            SetFishingBoatRiderTarget(target);
            return;
        }

        var path = FishingBoatTravel.FindPath(
            _worldSeed, _fishingBoat.Position, target);
        if (path.Count == 0)
        {
            ReportBlockedAction(
                "boat-fishing-no-route",
                "The fishing boat cannot reach that school.");
            return;
        }
        _queuedBoatFishKey = fish.StableKey;
        _queuedBoatFishTarget = target;
        _player.Stop();
        _fishingBoat.FollowPath(path);
        _moveMarker = new(target, 0, Action: true);
    }

    private void UpdateFishingBoatAction(float elapsed)
    {
        AdvanceFishingBoatRider(elapsed);
        if (_queuedBoatDisembarkTarget is { } walkTarget &&
            _queuedBoatDisembarkLanding is { } shore &&
            _fishingBoat is not null &&
            _fishingBoat.Action != EntityAction.Move)
        {
            _queuedBoatDisembarkTarget = null;
            _queuedBoatDisembarkLanding = null;
            if (FishingBoatTravel.CanDisembark(
                    _worldSeed, _fishingBoat.Position, shore))
                DisembarkFishingBoat(shore, walkTarget);
            else
                ReportBlockedAction(
                    "boat-disembark-range",
                    "The fishing boat could not reach that shore.");
            return;
        }
        if (_queuedBoatFishKey is not { } fishKey ||
            _fishingBoat is null)
            return;
        var fish = FindFish(fishKey);
        if (fish is null || IsFishDepleted(fish))
        {
            CancelFishingBoatAction();
            return;
        }
        if (_fishingBoat.Action == EntityAction.Move &&
            (FishingBoatRiderPosition(_queuedBoatFishTarget) -
             _queuedBoatFishTarget).Length > FishingNetReach())
            return;

        _fishingBoat.Stop();
        SetFishingBoatRiderTarget(_queuedBoatFishTarget);
        AdvanceFishingBoatRider(elapsed);
        if ((_fishingBoatRiderOffset -
             _fishingBoatRiderTargetOffset).Length > .015f)
            return;
        _queuedBoatFishKey = null;
        BeginFishing(fishKey, _queuedBoatFishTarget);
    }

    private void CancelFishingBoatAction()
    {
        _queuedBoatFishKey = null;
        _queuedBoatDisembarkTarget = null;
        _queuedBoatDisembarkLanding = null;
        CenterFishingBoatRider();
        if (_activeFishKey is null) return;
        _activeFishKey = null;
        _player?.Stop();
    }

    private bool FishingBoatHitTest(Vector2 pointer)
    {
        var visual = GetFishingRaftVisual() ??
                     GetFishingBoatVisual();
        if (visual is null) return false;
        var bounds = SpriteBounds(
            visual.Frame, visual.World, visual.Mirror);
        return pointer.X >= bounds.Left && pointer.X < bounds.Right &&
               pointer.Y >= bounds.Top && pointer.Y < bounds.Bottom;
    }

    internal void BoardFishingBoat()
    {
        if (_fishingBoat is null || _player is null ||
            (_player.Position - _fishingBoat.Position).Length > 1.4f)
            return;
        CancelMeleeCombat();
        _player.Stop();
        _player.TeleportTo(_fishingBoat.Position);
        _fishingBoatRiderOffset = Vector2.Zero;
        _fishingBoatRiderTargetOffset = Vector2.Zero;
        _fishingBoatBoarded = true;
        _chatUi.AddMessage(
            "You board the fishing boat.",
            ChatMessageStyle.Action);
    }

    private void DisembarkFishingBoat(
        Vector2 landing,
        Vector2 walkTarget)
    {
        if (_player is null || _fishingBoat is null) return;
        CancelFishingBoatAction();
        _fishingBoatDisembarkTargeting = false;
        _fishingBoat.Stop();
        _fishingBoatBoarded = false;
        _fishingBoatRiderOffset = Vector2.Zero;
        _fishingBoatRiderTargetOffset = Vector2.Zero;
        _player.TeleportTo(landing);
        if ((landing - walkTarget).Length > .15f)
            _worldActions.QueueWalk(walkTarget);
        _chatUi.AddMessage(
            "You step ashore.",
            ChatMessageStyle.Action);
    }

    private Vector2 FishingBoatRiderPosition(Vector2 target)
    {
        if (_fishingBoat is null) return target;
        var direction = target - _fishingBoat.Position;
        if (direction.LengthSquared <= .0001f)
            return _fishingBoat.Position;
        return _fishingBoat.Position +
               direction.Normalized() * FishingBoatDeckRadius;
    }

    private void SetFishingBoatRiderTarget(Vector2 target)
    {
        if (_fishingBoat is null || _player is null) return;
        var position = FishingBoatRiderPosition(target);
        _fishingBoatRiderTargetOffset =
            position - _fishingBoat.Position;
        _fishingBoat.Face(target - _fishingBoat.Position);
        _player.Face(target - (
            _fishingBoat.Position + _fishingBoatRiderOffset));
    }

    private void AdvanceFishingBoatRider(float elapsed)
    {
        if (_fishingBoat is null || _player is null) return;
        var displacement =
            _fishingBoatRiderTargetOffset -
            _fishingBoatRiderOffset;
        var distance = displacement.Length;
        if (distance > .0001f)
            _fishingBoatRiderOffset +=
                displacement / distance *
                Math.Min(
                    distance,
                    FishingBoatRiderMoveSpeed * elapsed);
        _player.SyncPosition(
            _fishingBoat.Position + _fishingBoatRiderOffset);
        if (_queuedBoatFishKey is not null)
            _player.Face(
                _queuedBoatFishTarget - _player.Position);
    }

    internal void CenterFishingBoatRider()
    {
        _fishingBoatRiderTargetOffset = Vector2.Zero;
    }

    private FishingBoatVisual? GetFishingBoatVisual()
    {
        if (_fishingBoat is null || _fishingBoatAnimation is null ||
            _activeWorldLevel != (int)WorldLevel.Overworld)
            return null;
        const int authoredAngles = 5;
        var animation = _fishingBoatAnimation;
        var rawFrame = _fishingBoat.Action == EntityAction.Move
            ? (int)(_fishingBoatAnimationTime /
                    animation.SecondsPerFrame)
            : 0;
        var directional = VillagerDirectionRig.Resolve(
            _fishingBoat.Facing,
            animation.Graphic.Sprite.Frames.Count,
            authoredAngles,
            rawFrame);
        var terrain = SamplePlayerTerrain(
            _fishingBoat.Position.X, _fishingBoat.Position.Y);
        var world = IsometricTerrainProjection.Project(
            _fishingBoat.Position.X,
            _fishingBoat.Position.Y,
            terrain.Height);
        return new(
            animation.Graphic.Sprite.Frames[directional.Index],
            animation.Textures[directional.Index],
            world,
            directional.Mirror);
    }

    private FishingBoatVisual? GetFishingRaftVisual()
    {
        if (_fishingBoat is null ||
            _fishingRaftFrames.Length != FishingRaftDirectionCount ||
            _activeWorldLevel != (int)WorldLevel.Overworld)
            return null;
        var directional = VillagerDirectionRig.Resolve(
            _fishingBoat.Facing,
            FishingRaftDirectionCount,
            FishingRaftDirectionCount,
            0);
        return new(
            _fishingRaftFrames[directional.Index],
            _fishingRaftTextures[directional.Index],
            FishingBoatWorld(),
            directional.Mirror);
    }

    private PlayerVisual? GetFishingBoatRiderVisual()
    {
        if (_fishingBoat is null || !_fishingBoatBoarded ||
            _player is null ||
            !_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Idle), out var animation))
            return null;
        const int authoredAngles = 5;
        var directional = VillagerDirectionRig.Resolve(
            _fishingBoat.Facing,
            animation.Graphic.Sprite.Frames.Count,
            authoredAngles,
            0);
        return new(
            animation.Graphic.Sprite.Frames[directional.Index],
            animation.Textures[directional.Index],
            FishingBoatWorld() + new Vector2(0, -25),
            directional.Mirror,
            false);
    }

    private Vector2 FishingBoatWorld()
    {
        if (_fishingBoat is null) return Vector2.Zero;
        var terrain = SamplePlayerTerrain(
            _fishingBoat.Position.X, _fishingBoat.Position.Y);
        var world = IsometricTerrainProjection.Project(
            _fishingBoat.Position.X,
            _fishingBoat.Position.Y,
            terrain.Height);
        return world;
    }

    private void DrawFishingBoat()
    {
        if (_fishingBoat is null || _activePlayer is null)
            return;
        var fishing = _fishingBoatBoarded &&
                      _player?.Action == EntityAction.Fish;
        var approaching = _fishingBoatBoarded &&
                          _queuedBoatFishKey is not null &&
                          _fishingBoat.Action != EntityAction.Move;
        var returning = _fishingBoatBoarded &&
                        _queuedBoatFishKey is null &&
                        _activeFishKey is null &&
                        _fishingBoatRiderOffset.Length > .01f;
        FishingBoatComposite? composite;
        float secondsPerFrame;
        if (fishing &&
            _fishingBoatFishingComposites.TryGetValue(
                _activePlayer.Gender, out var fishingComposite) &&
            _entityAnimations.TryGetValue(
                (_activePlayer.Gender, EntityAction.Fish),
                out var fishingAnimation))
        {
            composite = fishingComposite;
            secondsPerFrame = fishingAnimation.SecondsPerFrame;
        }
        else if (approaching || returning)
        {
            var progress = Math.Clamp(
                _fishingBoatRiderOffset.Length /
                FishingBoatDeckRadius,
                0, 1);
            var step = Math.Clamp(
                (int)MathF.Round(
                    progress *
                    (FishingBoatRiderApproachSteps - 1)),
                0,
                FishingBoatRiderApproachSteps - 1);
            if (!_fishingBoatApproachComposites.TryGetValue(
                    (_activePlayer.Gender, step),
                    out composite))
                return;
            secondsPerFrame = .1f;
        }
        else
        {
            if (!_fishingBoatComposites.TryGetValue(
                    (_activePlayer.Gender, _fishingBoatBoarded),
                    out composite))
                return;
            secondsPerFrame =
                _fishingBoatAnimation?.SecondsPerFrame ?? .1f;
        }
        const int authoredAngles = 5;
        var rawFrame = fishing
            ? (int)((_player?.ActionTime ?? 0) / secondsPerFrame)
            : _fishingBoat.Action == EntityAction.Move
                ? (int)(_fishingBoatAnimationTime / secondsPerFrame)
                : 0;
        var renderFacing = (fishing || approaching || returning) &&
                           _player is not null
            ? _player.Facing
            : _fishingBoat.Facing;
        var directional = VillagerDirectionRig.Resolve(
            renderFacing,
            composite.Frames.Length,
            authoredAngles,
            rawFrame);
        DrawSprite(
            composite.Frames[directional.Index],
            composite.Textures[directional.Index],
            FishingBoatWorld(),
            mirror: directional.Mirror,
            teamColor: _activePlayer.TeamColor);
    }
}
