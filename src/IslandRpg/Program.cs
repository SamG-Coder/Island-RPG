using IslandRpg.Assets;
using IslandRpg.Gameplay;
using IslandRpg.Persistence;
using IslandRpg.Rendering;

try
{
    var options = AppOptions.Parse(args);
    if (options.Observe && options.ObserveOutputFolder is { } outputFolder)
        ObserveEventLog.ConfigureOutputFolder(outputFolder);
    if (options.Observe)
        ObserveConsole.AttachToParent();
    var saves = new GameSaveRepository();
    ObserveModeOptions? observeMode = null;
    if (options.Observe)
    {
        var settings = saves.LoadSettings();
        var aiSettings = settings.EffectiveAi with { Enabled = true };
        saves.SaveSettings(settings with { Ai = aiSettings });
        using var ai = new NpcAiService();
        var availability = ai.CheckAsync(aiSettings)
            .GetAwaiter().GetResult();
        ObserveEventLog.Write(
            Console.Out, 0, 8 * 60 * 60, "Day 1 08:00", null,
            "ai_availability_response", new
            {
                Availability = availability.Availability.ToString(),
                availability.Message,
                aiSettings.BaseUrl,
                aiSettings.Model
            });
        if (!availability.Ready)
            throw new InvalidOperationException(
                "Observe mode requires a responding AI model: " +
                availability.Message);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var observer = saves.CreatePlayer(
            $"Observer-{stamp}", EntityGender.Male, 2, 0);
        ObserveEventLog.Write(
            Console.Out, 0, 8 * 60 * 60, "Day 1 08:00", null,
            "ai_persona_request", new
            {
                WorldName = $"Observation-{stamp}",
                WorldSeed = options.Seed,
                Names = VillagerSimulation.NamesForPopulation(
                    ObserveModePolicy.RequiredVillagerCount(
                        options.ObserveScenario)),
                aiSettings.Model
            });
        var personas = ai.GeneratePersonasAsync(
                aiSettings,
                $"Observation-{stamp}",
                options.Seed,
                VillagerSimulation.NamesForPopulation(
                    ObserveModePolicy.RequiredVillagerCount(
                        options.ObserveScenario)))
            .GetAwaiter().GetResult() ?? [];
        ObserveEventLog.Write(
            Console.Out, 0, 8 * 60 * 60, "Day 1 08:00", null,
            "ai_persona_response", new
            {
                Count = personas.Count,
                Personas = personas
            });
        var world = saves.CreateWorld(
            $"Observation-{stamp}",
            options.Seed,
            observer.Id,
            aiNpcsEnabled: true,
            aiNpcCount: ObserveModePolicy.RequiredVillagerCount(
                options.ObserveScenario),
            aiNpcPersonas: personas);
        observeMode = new(
            world.Id,
            observer.Id,
            options.ObserveSeconds,
            options.ObserveLogIntervalSeconds,
            options.ObserveScenario,
            options.ObserveHungerRateMultiplier,
            options.ObserveStartingFoodCount);
        ObserveEventLog.Write(
            Console.Out, 0, world.ElapsedGameSeconds, "Day 1 08:00", null,
            "world_created", new
            {
                WorldId = world.Id,
                ObserverId = observer.Id,
                AiEnabled = world.AiNpcsEnabled,
                NpcCount = world.AiNpcCount,
                Scenario = options.ObserveScenario,
                options.ObserveHungerRateMultiplier,
                options.ObserveStartingFoodCount,
                aiSettings.Model
            });
    }
    var gameMode = options.Catalog && options.Game;
    var foundAoeAssets = Age2InstallLocator.TryFind(
        options.Age2Path,
        out var age2Install);
    var cannotLocateAoeAssets = gameMode && !foundAoeAssets;
    var useTestAssets = gameMode &&
                        (saves.LoadSettings().UseTestAssets ||
                         cannotLocateAoeAssets);
    var install = useTestAssets
        ? Path.Combine(
            AppContext.BaseDirectory,
            "Resources",
            "Images",
            "TestAssets")
        : foundAoeAssets
            ? age2Install
            : Age2InstallLocator.Find(options.Age2Path);
    if (useTestAssets)
    {
        Directory.CreateDirectory(install);
        Console.WriteLine($"Test assets: {install}");
    }
    else
        Console.WriteLine($"Age2HD: {install}");

    var datPath = Path.Combine(install, "resources", "_common", "dat", "empires2_x2_p1.dat");
    if (options.Catalog)
    {
        AssetCatalog assetCatalog;
        if (options.ValidateOnly)
        {
            assetCatalog = AssetLoader.LoadAll(
                install,
                new Progress<(int Done, int Total, string Name)>());
        }
        else
        {
            using var host = new GameHostWindow(
                install,
                options.World
                    ? GameHostWindow.PreviewMode.World
                    : options.Game
                    ? GameHostWindow.PreviewMode.Game
                    : options.Island
                    ? GameHostWindow.PreviewMode.Island
                    : GameHostWindow.PreviewMode.Assets,
                options.Seed,
                useTestAssets,
                cannotLocateAoeAssets,
                observeMode);
            host.Run();
            assetCatalog = host.Catalog ??
                           throw new InvalidOperationException("The asset catalogue did not finish loading.");
        }
        Console.WriteLine(
            $"Asset inventory: {assetCatalog.Graphics.Count} loose graphics loaded into memory");
        Console.WriteLine($"Terrain textures loaded: {assetCatalog.TerrainTiles.Count}");
        Console.WriteLine($"Water normal maps loaded: {assetCatalog.WaterTextures.Count}");
        Console.WriteLine(
            $"DAT references without a loose SLP: {assetCatalog.Missing.Count} " +
            "(legacy, superseded, unused, or unavailable; not an installation error)");
        Console.WriteLine($"Full asset report: {AssetAudit.Save(assetCatalog)}");
        return;
    }

    var graphicId = options.GraphicId;
    GenieGraphic? graphic = null;
    if (graphicId is null)
    {
        graphic = GenieDatReader.FindGraphic(datPath, options.GraphicName);
        graphicId = graphic.SlpId;
        Console.WriteLine(
            $"DAT graphic {graphic.Name}: SLP {graphic.SlpId}, " +
            $"{graphic.FrameCount} frame(s)/angle, {graphic.AngleCount} angle(s), " +
            $"{graphic.FrameRate:0.###} sec/frame");
    }

    var spritePath = Path.Combine(install, "resources", "_common", "drs", "graphics", $"{graphicId}.slp");
    if (!File.Exists(spritePath))
        throw new FileNotFoundException($"Graphic {graphicId} was not found.", spritePath);

    var resolvedPalette = Age2PaletteResolver.Resolve(install, spritePath);
    Console.WriteLine($"Palette: {resolvedPalette.Description} ({Path.GetFileName(resolvedPalette.Path)})");
    var palette = JascPalette.Load(resolvedPalette.Path);
    var sprite = SlpDecoder.Decode(spritePath, palette);
    Console.WriteLine($"Decoded SLP {graphicId}: {sprite.Frames.Count} frame(s)");

    if (graphic?.Name.EndsWith("_NN", StringComparison.OrdinalIgnoreCase) == true)
    {
        var shadowName = graphic.Name[..^2] + "N0";
        try
        {
            var shadowGraphic = GenieDatReader.FindGraphic(datPath, shadowName);
            var shadowPath = Path.Combine(install, "resources", "_common", "drs", "graphics", $"{shadowGraphic.SlpId}.slp");
            var shadow = SlpDecoder.Decode(shadowPath, palette);
            sprite = SpriteCompositor.Layer(shadow, sprite);
            Console.WriteLine(
                $"Composited shadow {shadowName} (SLP {shadowGraphic.SlpId}) using shared SLP hotspots");
        }
        catch (KeyNotFoundException)
        {
            Console.WriteLine($"No companion shadow record found for {graphic.Name}");
        }
    }

    if (options.ValidateOnly) return;
    using var game = new DemoWindow(sprite, graphicId.Value, graphic?.FrameRate, graphic?.FrameCount);
    game.Run();
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(
        "Usage: IslandRpg [--game | --world] [--seed <number>] [--island | --catalog] " +
        "[--observe [--observe-seconds <seconds>] [--observe-log-interval <seconds>] [--observe-output <folder>] [--observe-scenario <name>] [--observe-hunger-rate <multiplier>] [--observe-food-count <count>]] " +
        "[--age2-path <folder>] [--graphic <SLP id> | --graphic-name <DAT name>]");
    Environment.ExitCode = 1;
}

internal sealed record AppOptions(
    string? Age2Path,
    int? GraphicId,
    string GraphicName,
    bool ValidateOnly,
    bool Catalog,
    bool Island,
    bool World,
    bool Game,
    long Seed,
    bool Observe,
    double ObserveSeconds,
    double ObserveLogIntervalSeconds,
    string ObserveScenario,
    float ObserveHungerRateMultiplier,
    int ObserveStartingFoodCount,
    string? ObserveOutputFolder)
{
    public static AppOptions Parse(string[] args)
    {
        string? path = null;
        int? graphic = null;
        // TREEA_N0 is the ground-shadow layer. TREEA_NN is the visible,
        // full-colour tree sprite.
        var graphicName = "TREEA_NN";
        var validateOnly = false;
        var catalog = true;
        var island = false;
        var world = false;
        var game = true;
        var observe = false;
        double observeSeconds = 0;
        double observeLogIntervalSeconds = 2;
        var observeScenario = ObserveScenarioService.Default;
        float observeHungerRateMultiplier = 1;
        var observeStartingFoodCount = 20;
        string? observeOutputFolder = null;
        long seed = 2187;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--age2-path" && i + 1 < args.Length) path = args[++i];
            else if (args[i] == "--graphic" && i + 1 < args.Length && int.TryParse(args[++i], out var id))
            {
                graphic = id;
                catalog = false;
                world = false;
                game = false;
            }
            else if (args[i] == "--graphic-name" && i + 1 < args.Length)
            {
                graphicName = args[++i];
                catalog = false;
                world = false;
                game = false;
            }
            else if (args[i] == "--catalog")
            {
                catalog = true;
                island = false;
                world = false;
                game = false;
            }
            else if (args[i] == "--island")
            {
                catalog = true;
                island = true;
                world = false;
                game = false;
            }
            else if (args[i] == "--world")
            {
                catalog = true;
                island = false;
                world = true;
                game = false;
            }
            else if (args[i] == "--game")
            {
                catalog = true;
                island = false;
                world = false;
                game = true;
            }
            else if (args[i] == "--observe")
            {
                observe = true;
                catalog = true;
                island = false;
                world = false;
                game = true;
            }
            else if (args[i] == "--observe-seconds" &&
                     i + 1 < args.Length &&
                     double.TryParse(
                         args[++i],
                         System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture,
                         out var parsedObserveSeconds) &&
                     parsedObserveSeconds >= 0)
                observeSeconds = parsedObserveSeconds;
            else if (args[i] == "--observe-log-interval" &&
                     i + 1 < args.Length &&
                     double.TryParse(
                         args[++i],
                         System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture,
                         out var parsedLogInterval) &&
                     parsedLogInterval > 0)
                observeLogIntervalSeconds = parsedLogInterval;
            else if (args[i] == "--observe-output" &&
                     i + 1 < args.Length &&
                     !string.IsNullOrWhiteSpace(args[i + 1]))
                observeOutputFolder = args[++i];
            else if (args[i] == "--observe-scenario" &&
                     i + 1 < args.Length &&
                     ObserveScenarioService.IsSupported(args[i + 1]))
                observeScenario = args[++i];
            else if (args[i] == "--observe-hunger-rate" &&
                     i + 1 < args.Length &&
                     float.TryParse(
                         args[++i],
                         System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture,
                         out var parsedHungerRate) &&
                     parsedHungerRate > 0)
                observeHungerRateMultiplier = parsedHungerRate;
            else if (args[i] == "--observe-food-count" &&
                     i + 1 < args.Length &&
                     int.TryParse(args[++i], out var parsedFoodCount) &&
                     parsedFoodCount is >= 0 and <= PlayerInventory.Capacity)
                observeStartingFoodCount = parsedFoodCount;
            else if (args[i] == "--seed" && i + 1 < args.Length &&
                     long.TryParse(args[++i], out var parsedSeed)) seed = parsedSeed;
            else if (args[i] == "--validate") validateOnly = true;
            else throw new ArgumentException($"Unknown or incomplete argument: {args[i]}");
        }
        return new(
            path, graphic, graphicName, validateOnly,
            catalog, island, world, game, seed,
            observe, observeSeconds, observeLogIntervalSeconds,
            observeScenario, observeHungerRateMultiplier,
            observeStartingFoodCount, observeOutputFolder);
    }
}
