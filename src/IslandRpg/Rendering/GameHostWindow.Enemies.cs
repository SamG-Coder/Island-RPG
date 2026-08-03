using IslandRpg.Assets;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private readonly List<EnemySpawnerState> _enemySpawners = [];
    private readonly List<EnemyState> _enemies = [];
    private readonly SlimeAttackEffects _slimeAttackEffects = new();
    private SlimeSpriteRig? _slimeRig;
    private int _softActorShadowProgram;
    private Guid? _enemyContextTargetId;
    private Vector2 _enemyContextWalkTarget;
    private readonly int[] _slimeFrontTextures =
        new int[SlimeSpriteRig.FrameCount];
    private readonly int[] _slimeBackTextures =
        new int[SlimeSpriteRig.FrameCount];
    private double _nextEnemySpawnerProbe;
    private readonly Dictionary<Guid, Task<EnemyPathResult>>
        _enemyPathTasks = [];
    private CancellationTokenSource _enemyPathCancellation = new();
    private sealed record EnemyPathResult(
        Guid EnemyId,
        Vector2 Destination,
        IReadOnlyList<Vector2> Path);

    private void PrepareSlimeAnimations()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory, "Resources", "Images", "Combat");
        _slimeRig = SlimeSpriteRig.Load(
            Path.Combine(directory, "slime-sprites.png"),
            Path.Combine(directory, "slime-sprites-back.png"));
        _softActorShadowProgram =
            GameShaderPrograms.CreateSoftShadowProgram();
        foreach (var state in Enum.GetValues<SlimeAnimationState>())
        for (var frame = 0; frame < SlimeSpriteRig.Columns; frame++)
        {
            var index = (int)state * SlimeSpriteRig.Columns + frame;
            _slimeFrontTextures[index] = Upload(
                _slimeRig.FrameAt(state, frame, back: false));
            _slimeBackTextures[index] = Upload(
                _slimeRig.FrameAt(state, frame, back: true));
        }
    }

    private void ResetEnemies()
    {
        _enemyPathCancellation.Cancel();
        _enemyPathCancellation.Dispose();
        _enemyPathCancellation = new();
        _enemyPathTasks.Clear();
        _enemySpawners.Clear();
        _enemies.Clear();
        _slimeAttackEffects.Clear();
        _nextEnemySpawnerProbe = 0;
    }

    private void UpdateEnemies(float elapsed)
    {
        if (_mode != PreviewMode.Game || _player is null ||
            _activeWorld is null) return;
        _enemies.RemoveAll(enemy =>
            !enemy.Alive && SlimeSpriteRig.DeathAnimationComplete(
                _clock - enemy.VisualActionStartedAt));
        for (var index = 0; index < _enemies.Count; index++)
        {
            var enemy = _enemies[index];
            var regeneration = EntityHealthRegenerationService.Advance(
                enemy.Health,
                enemy.MaximumHealth,
                elapsed,
                remainder: enemy.HealthRegenerationRemainder);
            if (regeneration.Health == enemy.Health &&
                regeneration.Remainder ==
                enemy.HealthRegenerationRemainder)
                continue;
            _enemies[index] = enemy with
            {
                Health = regeneration.Health,
                HealthRegenerationRemainder = regeneration.Remainder
            };
        }
        if (_clock >= _nextEnemySpawnerProbe)
        {
            EnsureEnemySpawnerNear(_player.Position);
            _nextEnemySpawnerProbe = _clock + 3;
        }

        var actors = EnemyActors();
        var nextEnemies = new List<EnemyState>(_enemies.Count + 8);
        for (var index = 0; index < _enemySpawners.Count; index++)
        {
            var spawner = _enemySpawners[index];
            var spawned = EnemySpawnerService.Update(
                spawner,
                _enemies.Where(enemy => enemy.SpawnerId == spawner.Id)
                    .ToArray(),
                actors,
                _worldGameSeconds,
                unchecked((int)_worldSeed));
            _enemySpawners[index] = spawned.Spawner;
            if (!spawned.Active)
            {
                nextEnemies.AddRange(spawned.Enemies);
                continue;
            }
            foreach (var enemy in spawned.Enemies)
            {
                var placed = EnsureEnemyOnHabitat(
                    enemy, spawned.Spawner.Position, actors);
                var controlled = EnemySpawnerService.UpdateController(
                    placed, actors, _clock, elapsed,
                    unchecked((int)_worldSeed));
                controlled = ResolveEnemyAttack(controlled);
                nextEnemies.Add(AdvanceEnemyPath(controlled, elapsed));
            }
        }
        _enemies.Clear();
        _enemies.AddRange(nextEnemies);
        _slimeAttackEffects.Update(elapsed);
    }

    private EnemyState ResolveEnemyAttack(EnemyState enemy)
    {
        if (enemy.Behavior != EnemyBehavior.Attack ||
            enemy.TargetId != "player" || _activePlayer is null ||
            _playerDefeated) return enemy;
        var experience = Math.Max(1, enemy.PowerLevel * 100);
        var interaction = EntityInteractionService.TryMeleeAttack(
            _actionCooldowns,
            $"enemy:{enemy.Id:N}",
            _clock,
            experience,
            experience,
            experience,
            Random.Shared.NextSingle(),
            Random.Shared.NextSingle());
        if (!interaction.Succeeded) return enemy;
        enemy = enemy with
        {
            VisualAction = EntityAction.Attack,
            VisualActionStartedAt = _clock
        };
        var sourceWorld = EnemyEffectWorld(enemy.Position) +
                          new Vector2(0, -7);
        var targetWorld = GetPlayerVisual()?.World ??
                          EnemyEffectWorld(_player!.Position);
        _slimeAttackEffects.Burst(
            enemy.Kind,
            sourceWorld,
            targetWorld + new Vector2(0, -18),
            HashCode.Combine(enemy.Id, (int)(_clock * 1000)));
        if (interaction.Attack.Hit)
            ApplyPlayerDamage(
                interaction.Attack.Damage,
                EnemyDisplayName(enemy.Kind));
        else
            ShowEntityImpact(
                PlayerFeedbackKey(_activePlayer.Id), 0, false);
        TryAutoRetaliate(enemy);
        return enemy;
    }

    private Vector2 EnemyEffectWorld(Vector2 position)
    {
        var terrain = SamplePlayerTerrain(position.X, position.Y);
        return IsometricTerrainProjection.Project(
            position.X, position.Y, terrain.Height);
    }

    private void OpenEnemyContext(EnemyState enemy, Vector2 walkTarget)
    {
        _enemyContextTargetId = enemy.Id;
        _enemyContextWalkTarget = walkTarget;
        _inventoryContext.Close();
        _treeContext.Close();
        _groundObjectContext.Close();
        _villagerContext.Close();
        _fishContext.Close();
        _vegetationContext.Close();
        _miningContext.Close();
        _enemyContext.Open(
            MouseState.Position,
            EnemyInteractionMenu.Options,
            SceneClientBounds(), 148);
    }

    private void HandleEnemyContextSelection(int option)
    {
        var targetId = _enemyContextTargetId;
        _enemyContextTargetId = null;
        if (option == EnemyInteractionMenu.WalkHereIndex)
        {
            QueueWalk(_enemyContextWalkTarget);
            return;
        }
        if (targetId is not { } id) return;
        var enemy = _enemies.FirstOrDefault(value =>
            value.Id == id && value.Alive &&
            value.WorldLevel == _activeWorldLevel);
        if (enemy is null) return;
        if (option == EnemyInteractionMenu.AttackIndex)
            _worldActions.QueueEnemyAttack(enemy);
        else if (option == EnemyInteractionMenu.ExamineIndex)
            _chatUi.AddMessage(
                $"A {EnemyDisplayName(enemy.Kind).ToLowerInvariant()}. " +
                $"Health {enemy.Health}/{enemy.MaximumHealth}.",
                ChatMessageStyle.Normal);
    }

    private bool TryGetEnemyUnderMouse(
        Vector2 mouse, out EnemyState enemy)
    {
        for (var index = _enemies.Count - 1; index >= 0; index--)
        {
            var candidate = _enemies[index];
            if (!candidate.Alive ||
                candidate.WorldLevel != _activeWorldLevel ||
                _slimeRig is null) continue;
            var visual = GetEnemyVisual(candidate);
            if (visual is null) continue;
            var bounds = EnemySpriteBounds(candidate, visual.Frame);
            if (mouse.X < bounds.Left || mouse.X > bounds.Right ||
                mouse.Y < bounds.Top || mouse.Y > bounds.Bottom) continue;
            enemy = candidate;
            return true;
        }
        enemy = null!;
        return false;
    }

    private (float Left, float Top, float Right, float Bottom)
        EnemySpriteBounds(EnemyState enemy, SpriteFrame frame)
    {
        var terrain = SamplePlayerTerrain(enemy.Position.X, enemy.Position.Y);
        var world = IsometricTerrainProjection.Project(
            enemy.Position.X, enemy.Position.Y, terrain.Height);
        var anchor = SpriteAnchor(world);
        var scale = SpritePixelScale() * SlimeSpriteRig.WorldScale;
        return (
            anchor.X - frame.HotspotX * scale,
            anchor.Y - frame.HotspotY * scale,
            anchor.X + (frame.Width - frame.HotspotX) * scale,
            anchor.Y + (frame.Height - frame.HotspotY) * scale);
    }

    private static string EnemyDisplayName(EnemyKind kind) => kind switch
    {
        EnemyKind.WaterSlime => "Water slime",
        EnemyKind.GrassSlime => "Grass slime",
        EnemyKind.SandSlime => "Sand slime",
        EnemyKind.CaveSlime => "Cave slime",
        _ => "Slime"
    };

    private void DrawSoftActorShadow(Vector2 world, float renderScale)
    {
        if (_softActorShadowProgram == 0) return;
        var screen = SpriteAnchor(world);
        var scale = SpritePixelScale() * renderScale;
        var halfWidth = 46f * scale;
        var halfHeight = 12f * scale;
        var centerY = screen.Y - 1.5f * scale;
        var left = (screen.X - halfWidth - ReferenceWidth * .5f) *
                   2 / ReferenceWidth;
        var right = (screen.X + halfWidth - ReferenceWidth * .5f) *
                    2 / ReferenceWidth;
        var top = -(centerY - halfHeight - ReferenceHeight * .5f) *
                  2 / ReferenceHeight;
        var bottom = -(centerY + halfHeight - ReferenceHeight * .5f) *
                     2 / ReferenceHeight;
        GL.UseProgram(_softActorShadowProgram);
        GL.Uniform1(
            _shaderUniforms.Get(_softActorShadowProgram, "opacity"), .42f);
        Draw([
            left, top, 0, 0,
            left, bottom, 0, 1,
            right, bottom, 1, 1,
            right, top, 1, 0
        ]);
    }

    private EnemyState AdvanceEnemyPath(EnemyState enemy, float elapsed)
    {
        if (enemy.Behavior is EnemyBehavior.Idle or EnemyBehavior.Attack or
            EnemyBehavior.Dead)
            return enemy with { Path = null, PathIndex = 0 };
        var destination = enemy.Destination;
        if (enemy.Behavior == EnemyBehavior.Roam &&
            !IsSlimeHabitat(enemy.Kind, destination, enemy.WorldLevel))
            destination = enemy.SpawnPosition;
        var destinationChanged = enemy.RoutedDestination is not { } routed ||
            Vector2.DistanceSquared(routed, destination) > .65f * .65f;
        var chaseRefresh = enemy.Behavior == EnemyBehavior.Chase &&
                           _clock >= enemy.NextPathAt;
        if (_enemyPathTasks.TryGetValue(enemy.Id, out var pending))
        {
            if (!pending.IsCompleted)
                return AdvanceEnemyWaypoints(enemy, elapsed);
            _enemyPathTasks.Remove(enemy.Id);
            if (pending.IsCompletedSuccessfully)
            {
                var result = pending.Result;
                var stale = Vector2.DistanceSquared(
                    result.Destination, destination) > .65f * .65f;
                if (!stale && result.Path.Count > 0)
                    enemy = enemy with
                    {
                        Destination = destination,
                        Path = result.Path,
                        PathIndex = 0,
                        RoutedDestination = destination,
                        NextPathAt = _clock + .65
                    };
            }
        }
        destinationChanged = enemy.RoutedDestination is not { } currentRoute ||
            Vector2.DistanceSquared(currentRoute, destination) > .65f * .65f;
        chaseRefresh = enemy.Behavior == EnemyBehavior.Chase &&
                       _clock >= enemy.NextPathAt;
        if ((enemy.Path is null || destinationChanged || chaseRefresh) &&
            !_enemyPathTasks.ContainsKey(enemy.Id))
        {
            var start = enemy.Position;
            var level = enemy.WorldLevel;
            var obstacles = ActiveNavigationObstacles();
            var token = _enemyPathCancellation.Token;
            var enemyId = enemy.Id;
            var seed = _worldSeed;
            _enemyPathTasks[enemy.Id] = Task.Run(() =>
                new EnemyPathResult(
                    enemyId,
                    destination,
                    GridPathfinder.Find(
                        seed,
                        start,
                        destination,
                        maximumVisited: 4096,
                        cancellationToken: token,
                        worldLevel: level,
                        obstacles: obstacles)), token);
            enemy = enemy with { NextPathAt = _clock + .65 };
        }
        return AdvanceEnemyWaypoints(enemy, elapsed);
    }

    private EnemyState AdvanceEnemyWaypoints(
        EnemyState enemy, float elapsed)
    {
        if (enemy.Path is null || enemy.Path.Count == 0)
            return enemy;
        var speed = enemy.Behavior switch
        {
            EnemyBehavior.Chase => 1.35f,
            EnemyBehavior.Return => 1.05f,
            _ => .68f
        };
        var remaining = Math.Max(0, elapsed) * speed;
        var position = enemy.Position;
        var pathIndex = enemy.PathIndex;
        while (remaining > 0 && pathIndex < enemy.Path.Count)
        {
            if (enemy.Behavior is EnemyBehavior.Roam or EnemyBehavior.Return &&
                !IsSlimeHabitat(
                    enemy.Kind, enemy.Path[pathIndex], enemy.WorldLevel))
                return enemy with
                {
                    Destination = enemy.Position,
                    Behavior = EnemyBehavior.Idle,
                    Path = null,
                    PathIndex = 0,
                    NextDecisionAt = 0
                };
            var delta = enemy.Path[pathIndex] - position;
            var distance = delta.Length;
            if (distance <= .04f)
            {
                position = enemy.Path[pathIndex++];
                continue;
            }
            var travel = Math.Min(distance, remaining);
            position += delta / distance * travel;
            remaining -= travel;
            if (travel >= distance - .001f) pathIndex++;
        }
        var finished = pathIndex >= enemy.Path.Count;
        return enemy with
        {
            Position = position,
            PathIndex = pathIndex,
            Behavior = finished && enemy.Behavior is
                EnemyBehavior.Roam or EnemyBehavior.Return
                    ? EnemyBehavior.Idle
                    : enemy.Behavior,
            Path = finished ? null : enemy.Path,
            NextDecisionAt = finished
                ? _clock + 1.5 + Math.Abs(enemy.Id.GetHashCode() % 100) / 100f
                : enemy.NextDecisionAt
        };
    }

    private bool IsSlimeHabitat(
        EnemyKind kind, Vector2 position, int worldLevel)
    {
        if (kind == EnemyKind.CaveSlime)
            return worldLevel == (int)WorldLevel.Underground;
        var biome = SamplePlayerTerrain(position.X, position.Y).Biome;
        return kind switch
        {
            EnemyKind.WaterSlime =>
                biome == Biome.Beach,
            EnemyKind.GrassSlime =>
                biome is Biome.Grassland or Biome.DryGrass,
            EnemyKind.SandSlime =>
                biome is Biome.DesertSand or Biome.CrackedEarth,
            _ => false
        };
    }

    private EnemyState EnsureEnemyOnHabitat(
        EnemyState enemy,
        Vector2 spawnerPosition,
        IReadOnlyList<EnemyActorPresence> actors)
    {
        if (!enemy.Alive) return enemy;
        if (IsSlimeHabitat(enemy.Kind, enemy.Position, enemy.WorldLevel))
            return enemy;
        var random = new Random(HashCode.Combine(_worldSeed, enemy.Id));
        var position = spawnerPosition;
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var angle = random.NextSingle() * MathF.Tau;
            var radius = 1.5f + random.NextSingle() * 5;
            var candidate = spawnerPosition + new Vector2(
                MathF.Cos(angle), MathF.Sin(angle)) * radius;
            if (!IsSlimeHabitat(enemy.Kind, candidate, enemy.WorldLevel) ||
                actors.Any(actor => Vector2.DistanceSquared(
                    actor.Position, candidate) < 5 * 5))
                continue;
            position = candidate;
            break;
        }
        return enemy with
        {
            SpawnPosition = position,
            Position = position,
            Destination = position,
            Behavior = EnemyBehavior.Idle,
            Path = null,
            PathIndex = 0,
            RoutedDestination = null,
            NextDecisionAt = 0
        };
    }

    private EnemyActorPresence[] EnemyActors()
    {
        var actors = new List<EnemyActorPresence>(_villagers.Count + 1);
        if (_player is not null && !_playerDefeated)
            actors.Add(new(
                "player", _player.Position, _activeWorldLevel, true,
                PlayerCombatPower(), true));
        foreach (var villager in _villagers)
            if (villager.Health > 0)
                actors.Add(new(
                    villager.Id,
                    new(villager.PositionX, villager.PositionY),
                    villager.WorldLevel,
                    true,
                    Math.Max(1, (villager.AttackExperience +
                                 villager.StrengthExperience) / 200)));
        return actors.ToArray();
    }

    private int PlayerCombatPower()
    {
        if (_activePlayer is null) return 1;
        return Math.Max(1,
            (_activePlayer.AttackExperience +
             _activePlayer.StrengthExperience +
             _activePlayer.DefenceExperience) / 300);
    }

    private void EnsureEnemySpawnerNear(Vector2 focus)
    {
        if (_enemySpawners.Any(spawner =>
                spawner.WorldLevel == _activeWorldLevel &&
                Vector2.DistanceSquared(spawner.Position, focus) < 55 * 55))
            return;
        if (!TryFindSpawnerSite(focus, out var position, out var biome,
                out var kind)) return;
        _enemySpawners.Add(new(
            Guid.NewGuid(),
            position,
            _activeWorldLevel,
            biome,
            [new(kind)],
            MaximumAlive: 7));
    }

    private bool TryFindSpawnerSite(
        Vector2 focus,
        out Vector2 position,
        out Biome biome,
        out EnemyKind kind)
    {
        if (!EnemySpawnerSiteSelector.TryFind(
                focus, _worldSeed, _activeWorldLevel, this,
                static (window, candidate) =>
                    window.SamplePlayerTerrain(
                        candidate.X, candidate.Y).Biome,
                static (window, candidate) =>
                    window.IsNearShallowWater(candidate),
                out var site))
        {
            position = default;
            biome = default;
            kind = default;
            return false;
        }
        position = site.Position;
        biome = site.Biome;
        kind = site.Kind;
        return true;
    }

    private bool IsNearShallowWater(Vector2 position)
    {
        ReadOnlySpan<Vector2> offsets =
        [
            new(3, 0), new(-3, 0), new(0, 3), new(0, -3),
            new(2, 2), new(-2, 2), new(2, -2), new(-2, -2)
        ];
        foreach (var offset in offsets)
        {
            var sample = position + offset;
            if (SamplePlayerTerrain(sample.X, sample.Y).Biome is
                Biome.ShallowWater or Biome.MangroveShallows)
                return true;
        }
        return false;
    }

    private ActorVisual? GetEnemyVisual(EnemyState enemy)
    {
        if (_slimeRig is null ||
            enemy.WorldLevel != _activeWorldLevel) return null;
        var deathAge = _clock - enemy.VisualActionStartedAt;
        if (!enemy.Alive &&
            SlimeSpriteRig.DeathAnimationComplete(deathAge)) return null;
        // Grid paths alternate map-axis steps to describe a single visual
        // diagonal. Looking at the next step made the slime swap sheets every
        // waypoint, so orient it toward the stable routed goal instead.
        var facing = SlimeSpriteRig.StableTravelFacing(
            enemy.Position,
            enemy.Path is not null && enemy.RoutedDestination is { } routed
                ? routed
                : enemy.Destination,
            enemy.Destination - enemy.Position);
        var attackAge = _clock - enemy.VisualActionStartedAt;
        var activeAttack = enemy.Alive &&
                           enemy.VisualAction == EntityAction.Attack &&
                           attackAge >= 0 && attackAge < 1.12;
        var action = !enemy.Alive
            ? EntityAction.Die
            : activeAttack
            ? EntityAction.Attack
            : enemy.Behavior switch
        {
            EnemyBehavior.Chase or EnemyBehavior.Return or EnemyBehavior.Roam =>
                EntityAction.Move,
            _ => EntityAction.Idle
        };
        var pose = SlimeSpriteRig.Resolve(
            action, facing,
            !enemy.Alive
                ? deathAge
                : activeAttack
                ? attackAge
                : _clock + Math.Abs(enemy.Id.GetHashCode() % 100) * .013);
        var frame = _slimeRig.Frame(pose);
        var textureIndex = (int)pose.State * SlimeSpriteRig.Columns +
                           pose.FrameIndex;
        var terrain = SamplePlayerTerrain(enemy.Position.X, enemy.Position.Y);
        var world = IsometricTerrainProjection.Project(
            enemy.Position.X, enemy.Position.Y, terrain.Height);
        return new(
            frame,
            pose.UsesBackSheet
                ? _slimeBackTextures[textureIndex]
                : _slimeFrontTextures[textureIndex],
            world,
            pose.Mirror,
            terrain.Biome is Biome.ShallowWater or Biome.RiverWater,
            0,
            SlimeTint(enemy.Kind),
            .28f,
            SlimeSpriteRig.WorldScale,
            Opacity: .88f,
            SoftShadow: true,
            PixelArtFilter: true);
    }

    private static Vector3 SlimeTint(EnemyKind kind) => kind switch
    {
        EnemyKind.WaterSlime => new(.30f, .65f, 1f),
        EnemyKind.GrassSlime => new(.22f, .82f, .24f),
        EnemyKind.SandSlime => new(.92f, .68f, .18f),
        EnemyKind.CaveSlime => new(.58f, .22f, .82f),
        _ => Vector3.One
    };
}
