# Island RPG v0.1.0

**First playable prototype — 27 July 2026**

Island RPG `v0.1.0` is the first playable release of an isometric survival and
skilling RPG built with OpenTK and inspired by the visual character of classic
Age of Empires II.

Explore a persistent procedurally generated world, gather natural resources,
craft primitive tools, develop skills, fish along the coast and rivers, and
build a campfire for cooking and light.

> Island RPG is an unofficial, non-commercial project. It is not affiliated
> with or endorsed by Microsoft. Age of Empires is a trademark of Microsoft.
> No Age of Empires assets are distributed with this project. A legally owned
> Age of Empires II HD installation is required.

## Highlights

### A persistent generated world

- Deterministic worlds created from a seed.
- Infinite 32×32 chunk streaming with asynchronous loading and unloading.
- Compressed region-based world saves and persistent player data.
- Continuous elevation, mountain ranges, cliffs, rivers and coastlines.
- Grassland, forest, rainforest, savanna, desert, taiga, tundra, alpine,
  wetland, beach and ocean environments.
- Day-and-night lighting with animated water, shoreline effects and shadows.
- Biome-aware trees, tree variants, shrubs, flowers, bushes and vegetation.
- Coastal shells, seaweed and animated freshwater, shore and ocean fish.

### Character and interaction

- Character creation, selection and persistent world position.
- Eight-direction movement with walking, idle, gathering and action animation.
- Terrain elevation, slopes, shallow-water movement and depth-sorted sprites.
- Native context menus, interaction cursors, pathfinding and queued actions.
- Inventory dragging, dropping, item use and reusable inventory screens.
- Chat messages, action feedback, XP notifications and level-up messages.

### Six skills

- **Woodcutting** — choose the best available axe, damage trees and gather
  logs, sticks and seeds.
- **Farming** — plant biome-aware tree seeds and grow persistent trees.
- **Crafting** — learn level-based recipes and create resources, tools,
  furniture and campfires.
- **Fishing** — use a primitive net to catch fish from appropriate freshwater,
  shore and ocean habitats.
- **Cooking** — cook raw fish over a lit campfire, with level-based burn
  chances and distinct cooked and burnt results.
- **Firemaking** — light fueled campfires and improve their duration, flame
  size, brightness and light radius through levels 1–20.

Each skill uses shared XP progression, a reusable skill panel and a level guide
showing its unlocks or gameplay improvements.

### Crafting and equipment

- Stone axe, iron axe, stone hammer, stone pickaxe and stone knife.
- Primitive fishing net woven from gathered plant fibres.
- Tool statistics, woodcutting power, blunting and sharpening.
- Multi-step recipes that safely simulate inventory changes before crafting.
- Recipe categories, level requirements and clear resource availability states.
- Reusable workbench and campfire placeable objects.

### Campfires and cooking

- Craft and place a persistent stone campfire.
- Add any supported log type while preserving the exact fuel used.
- Light fires using small rocks and a knife.
- Cook by dragging raw food onto a fire or using it through the context menu.
- Successful food returns to the inventory when the player remains nearby.
- Food left unattended drops naturally around the campfire when completed.
- Burnt food reuses cooked artwork with a grayscale and darkening shader.
- Fires illuminate the world and expire after their level-scaled duration.

### Interface and developer tools

- Pause, settings, skills, crafting and guide windows with modal input handling.
- Display settings for VSync, adaptive VSync, frame limits and metrics.
- Optional FPS and frame-time performance graph.
- Developer-only XP controls, time advancement and teleportable world map.
- Scrollable reusable list controls and consistent menu navigation.

### Performance foundations

- GPU terrain blending and atlas-batched world sprites.
- Visibility culling for terrain, objects, vegetation and shadows.
- Cached terrain sampling and gated world-hover queries.
- Asynchronous chunk generation, persistence and map generation.
- Reusable render queues and vertex staging buffers with minimal allocations.

## Running the release

Requirements:

- Windows
- A legally owned Age of Empires II HD installation
- .NET 10 SDK
- OpenGL 3.3-compatible graphics hardware

Run from the repository root:

```powershell
dotnet run --project src/IslandRpg -c Release
```

If the game cannot automatically locate Age of Empires II HD:

```powershell
dotnet run --project src/IslandRpg -c Release -- `
  --age2-path "D:\SteamLibrary\steamapps\common\Age2HD"
```

The `AGE2HD_PATH` environment variable is also supported.

## Known limitations

- This is an early prototype, not a content-complete game.
- There is currently no combat, health, hunger, construction system or NPC
  simulation.
- Farming currently focuses on planting and growing trees.
- Cooking currently supports the available raw fish; additional meat can be
  registered as those resources and sprites are introduced.
- Furniture placement begins with the workbench and campfire.
- Audio and several settings categories are not implemented yet.
- Balancing, progression rates and world-generation density may change in later
  releases.
- Existing saves are intended to remain compatible, but save migration is still
  considered experimental during early development.

## Validation

The release is validated with a clean Release build and automated world checks
covering deterministic generation, chunk seams, persistence, region storage,
skills, crafting, inventory behavior, fishing, cooking, campfires, Firemaking
progression and sprite composition.

Thank you for trying the first playable Island RPG release.
