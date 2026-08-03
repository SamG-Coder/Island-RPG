# Island RPG

Island RPG is an experimental isometric survival and skilling RPG built with
C#, .NET and OpenTK. It combines a persistent procedurally generated world with
classic point-and-click interactions, gathering, crafting, fishing, cooking and
placeable objects.

The project is currently at **v0.3.0**. It is playable, but it remains an early
prototype and its systems, balance and save format may continue to evolve.

Visit the [Island RPG website](https://samg-coder.github.io/Island-RPG/) for a
visual overview, release history and installation links.

See [the v0.3.0 release notes](release-notes/RELEASE_NOTES_0.3.md) for the complete feature
summary.

The long-term direction for autonomous characters is defined in the
[NPC AI design direction](docs/NPC_AI_DESIGN.md). NPCs are independent people:
they do not begin with privileged knowledge of the player, and any NPC may
organize or lead others.

## Important asset requirement

Island RPG reads compatible terrain, unit and environment graphics from the
player's own legally obtained **Age of Empires II HD** installation at runtime.

- No Age of Empires game assets are included in this repository.
- The game does not download those assets.
- You must supply a valid local Age of Empires II HD installation.
- Island RPG is unofficial, non-commercial and not affiliated with or endorsed
  by Microsoft.
- Age of Empires is a trademark of Microsoft.

The MIT license in this repository applies to Island RPG's original software
and documentation. It does not grant rights to Age of Empires or any other
third-party content.

## Current features

- Deterministic, persistent worlds generated from a numeric seed.
- Streamed 32×32 terrain chunks and compressed region-based saves.
- Mountains, elevation, cliffs, rivers, coasts, oceans and varied biomes.
- Biome-aware trees, vegetation, coastal collectibles and animated fish.
- Character creation, pathfinding and eight-direction character animation.
- Inventory use, dragging, dropping and context-sensitive interactions.
- Day and night, shadows, animated water, merged local lights and campfires.
- Connected overworld and underground cave layers with persistent entrances.
- Woodcutting, Farming, Crafting, Fishing, Cooking, Firemaking, Digging and
  Mining skills.
- Berry foraging and cooking, ore deposits, mining tools and level-up effects.
- Level-100 Adventure progression fed by every skilling action, with
  level-scaled maximum health.
- Persistent health and hunger, food-specific restoration, and timed
  well-fed bonuses that slow hunger loss.
- RuneScape-style unarmed melee targeting with Accurate, Aggressive and
  Defensive stances training Attack, Strength and Defence.
- Optional autonomous survivors with local knowledge, memory, personalities,
  leadership, group planning, promises and settlement projects.
- Observe mode with survivor status, inventory, skills, memories, free camera
  control and live simulation logging.
- Biome-specific slime enemies with reusable combat AI, local spawners,
  elemental attack effects and persistent loot bags.
- A skippable storm and shipwreck cinematic for shoreline starts.
- Level-based recipes, primitive tools, workbenches and campfires.
- Reusable skill guides, crafting screens, pause menus and settings.
- Optional performance metrics and developer world tools.

## Requirements

The currently supported development and release target is Windows.

- Windows 10 or later
- A legally obtained Age of Empires II HD installation
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An OpenGL 3.3-compatible graphics driver

Age of Empires II: Definitive Edition is not currently a substitute for the HD
installation because its data and asset layout differs.

## Quick start

Clone the repository, restore dependencies and run the Release configuration:

```powershell
git clone https://github.com/SamG-Coder/Island-RPG.git
cd Island-RPG
dotnet restore IslandRpg.slnx
dotnet run --project src/IslandRpg -c Release
```

The game checks common Steam installation locations automatically. For a custom
Steam library, pass the installation folder:

```powershell
dotnet run --project src/IslandRpg -c Release -- `
  --age2-path "D:\SteamLibrary\steamapps\common\Age2HD"
```

Alternatively, set `AGE2HD_PATH`:

```powershell
$env:AGE2HD_PATH = "D:\SteamLibrary\steamapps\common\Age2HD"
dotnet run --project src/IslandRpg -c Release
```

The path must contain `AoK HD.exe` and the expected
`resources\_common` data directories.

### NPC observe scenarios

Observe mode can run repeatable autonomous-NPC scenarios and stream JSON
events to standard output. The desert-surplus scenario starts two independent
NPCs together in desert terrain: one carries 20 cooked minnows and the other a
stone knife. Their personalities and simulation state decide whether the
imbalance produces cooperation, bargaining, separation or conflict.

```powershell
dotnet run --project src/IslandRpg -- --observe `
  --observe-scenario desert-surplus --observe-seconds 300 `
  --observe-hunger-rate 4 --observe-food-count 20 --seed 2187
```

Use `--observe-log-interval <seconds>` to adjust snapshot frequency. Scenario
events include the exact starting biome, positions and inventories.
`--observe-hunger-rate <multiplier>` accelerates NPC hunger loss for short
pressure tests without changing normal game balance.
`--observe-food-count <0-28>` varies the scenario's starting food supply for
controlled scarcity comparisons.

## Playing

Running the game opens the character and world menus. Create or select a
character, then create or load a world to begin playing.

| Input | Action |
|---|---|
| Left-click terrain | Walk to the selected location |
| Left-click an interactive target | Perform its default action, such as gathering, mining, opening storage or attacking |
| Right-click a world target | Open its available action menu |
| Left-click an inventory item | Select or use it when an interaction is active |
| Right-click an inventory item | Open its item actions, such as Eat, Drop, Excavate or placement |
| Drag an inventory item | Reorder it, drop it into the world, transfer it, or use it on compatible objects |
| Mouse wheel over the world | Zoom around the pointer |
| Mouse wheel over a scrollable panel | Scroll skills, guides, chat, storage or other lists |
| `Alt` | Highlight collectible ground items |
| `Enter` | Focus or submit chat |
| `Escape` | Cancel targeting or placement, close the active screen, or open the pause menu |

The three buttons at the lower right open **Combat**, **Skills** and **Bag**.
The Skills panel contains Adventure, combat and gathering skills; use the mouse
wheel to reach later rows. Select a skill and press **Guide** to open its level
guide.

World cursors indicate the default interaction available under the pointer.
For example, the attack cursor identifies a combat target. Open the Combat
panel to choose an unarmed stance: Accurate trains Attack,
Aggressive trains Strength, and Defensive trains Defence. Left-click a target
to approach and continue attacking on the global combat timer. Walking away
cancels the target without resetting attack readiness.

The icon-led player panel beside the minimap shows Adventure level, current
and maximum health, hunger, and remaining well-fed time. Right-click berries,
cooked fish, or stew in the Bag and select **Eat**. Better meals restore more
hunger and keep hunger draining slowly for longer.

Developer mode's item bank contains a placeable training dummy; left-click it
or choose **Attack** from its context menu to approach and fight on fixed
combat ticks.

### v0.3 progression loop

1. Right-click the stone shovel, select **Excavate**, and choose a clear,
   non-water tile. Left-click the dig site to continue excavating.
2. A completed dark hole has connected to cave terrain. Drag rope onto it to
   create a traversable entrance; use the climb cursor to travel between levels.
3. Mine underground deposits with a pickaxe. Larger and denser deposits take
   more strikes, and the strongest carried pickaxe is selected automatically.
4. Reach Crafting level 3, craft and place a workbench, then remain nearby to
   construct a clay bloomery and smithing anvil.
5. Stand beside the bloomery to alloy copper and tin or reduce iron ore into a
   bloom. Use the nearby anvil and a hammer to forge bars and metal tools.
6. Forage berry bushes for Farming XP and drag berries onto a lit campfire to
   cook them through the same interaction used for fish.
7. Forge a bronze axe for faster woodcutting and a bronze sickle for quicker,
   higher-yield berry harvesting.
8. Let a log burn down to charcoal as a renewable bloomery fuel. Forge and
   place a cooking pot beside a lit campfire to turn raw fish and berries into
   stew.
9. Build wooden chests and barrels at a nearby workbench. Open placed storage
   to deposit, withdraw and preserve supplies with the world.
10. Explore beaches, grasslands, deserts and caves to discover different slime
    populations. Surface slimes retaliate when attacked; cave slimes are
    naturally aggressive.
11. Loot defeated slimes for stackable gel, cores, salt crystals and medicinal
    herbs, then use those materials in preservation, medicine and advanced
    crafting.
12. Enable AI survivors from Advanced new-game setup to play alongside an
    autonomous group, or select Observe to follow their settlement decisions.

Unfinished excavations can be restored. Completed holes can be filled using
matching dirt or sand, and installed rope can be recovered from the context
menu. Changes remain synchronized between the overworld and underground.

Developer mode is intended for testing. Enter `/imahacker` in chat, then open
**Pause → Settings → Dev** to access XP controls, time advancement and the
teleportable world map. In that map, `T` toggles tree-density visualization.

## Saves and generated data

Player profiles, worlds and settings are stored outside the repository:

```text
%LOCALAPPDATA%\IslandRpg\
├── Players\
├── Worlds\
└── settings.json
```

World chunks are grouped into indexed region files and compressed
independently. Copy the entire `%LOCALAPPDATA%\IslandRpg` directory to back up
all local progress.

The asset validator writes its report to `AssetCache/`, which is ignored by
Git. Temporary graphic exports belong in `TestExport/`, which is also ignored.

## Command-line modes

Arguments after `--` are passed to Island RPG:

```text
--game
--world
--island
--catalog
--seed <number>
--age2-path <folder>
--graphic <SLP id>
--graphic-name <DAT graphic name>
--validate
```

Examples:

```powershell
# Play using a deterministic seed
dotnet run --project src/IslandRpg -c Release -- --game --seed 2187

# Open the standalone streamed-world preview
dotnet run --project src/IslandRpg -c Release -- --world --seed 2187

# Open the finite island preview
dotnet run --project src/IslandRpg -c Release -- --island

# Open the decoded asset catalogue
dotnet run --project src/IslandRpg -c Release -- --catalog

# Validate discoverable assets without opening a window
dotnet run --project src/IslandRpg -c Release -- --catalog --validate

# Preview one loose SLP graphic
dotnet run --project src/IslandRpg -c Release -- --graphic 495

# Resolve a graphic by its DAT name
dotnet run --project src/IslandRpg -c Release -- `
  --graphic-name TREEA_NN
```

Asset validation records unavailable DAT references in
`AssetCache/asset-report.json`. Age of Empires data can contain legacy,
superseded or unused references, so a missing loose SLP is not automatically
evidence of a damaged installation.

## Building and testing

Build the full solution:

```powershell
dotnet build IslandRpg.slnx -c Release
```

Run the deterministic world and gameplay checks:

```powershell
dotnet run --project tests/IslandRpg.WorldChecks -c Release
```

Create a self-contained Windows x64 build with the checked-in publish profile:

```powershell
dotnet publish src/IslandRpg/IslandRpg.csproj `
  -p:PublishProfile=FolderProfile
```

Published builds still require the user to provide their own Age of Empires II
HD installation. Do not redistribute Age of Empires assets with a release.

## Repository layout

```text
src/IslandRpg/                  Game, rendering, gameplay and world generation
tests/IslandRpg.WorldChecks/    Deterministic integration and regression checks
tools/IslandRpg.SpriteTool/     32×32 inventory sprite-sheet conversion
tools/IslandRpg.ObjectSpriteTool/ Isometric object conversion and sizing
tools/IslandRpg.GraphicExport/  Local AoE graphic search and export utility
artifacts/                      Source and preview material for authored assets
```

## Asset tools

### Inventory sprite sheets

Convert generated artwork to a chroma-keyed 32×32 grid:

```powershell
dotnet run --project tools/IslandRpg.SpriteTool -- `
  input.png output.png `
  --columns 4 --rows 2 --cell-size 32 `
  --chroma "#FF00FF" --tolerance 32
```

### Isometric objects

The object tool applies documented gameplay footprints, visual scale and ground
hotspots:

```powershell
dotnet run --project tools/IslandRpg.ObjectSpriteTool -- `
  --input artifacts/object-sprites/workbench/workbench-source.png `
  --output artifacts/object-sprites/workbench/workbench.png `
  --preset workbench `
  --preview artifacts/object-sprites/workbench/workbench-preview.png
```

See the [Object Sprite Tool guide](tools/IslandRpg.ObjectSpriteTool/README.md)
for the size standard, prompt template and animation-overlay workflow.

### Local Age of Empires graphic research

Search the local DAT catalogue and export matching decoded frames:

```powershell
dotnet run --project tools/IslandRpg.GraphicExport -- `
  --install "C:\Program Files (x86)\Steam\steamapps\common\Age2HD" `
  --query FISH `
  --output TestExport\Fish
```

See the [Graphic Export guide](tools/IslandRpg.GraphicExport/README.md) for
exact-name searches and additional arguments. Exported third-party graphics are
for local research and must not be committed or redistributed.

## Contributing

Issues and focused pull requests are welcome.

Before submitting a change:

1. Keep new feature logic out of `GameHostWindow.cs` wherever practical; use a
   dedicated service, controller or partial-class file.
2. Preserve compatibility with existing worlds and player profiles.
3. Add or update checks for progression, persistence and deterministic world
   behavior.
4. Run the Release build and `IslandRpg.WorldChecks`.
5. Do not commit extracted Age of Empires graphics, local saves, generated
   exports, IDE user settings or publish history.

## Status and limitations

This is an early playable prototype. Slimes are the first complete enemy family,
and autonomous-survivor planning remains experimental and dependent on the
selected local Ollama model. Farming covers tree planting, crops and berry
foraging; Cooking supports fish, berries, stew and preserved food; placeable
furniture and construction remain limited. Multiplayer is not implemented. See
the [v0.3.0 release notes](release-notes/RELEASE_NOTES_0.3.md) for more detail.

## License and acknowledgements

Island RPG's original software and documentation are licensed under the
[MIT License](LICENSE).

Third-party libraries and the bundled Arimo font retain their own licenses.
See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Age of Empires assets and trademarks are not covered by the Island RPG license
and are not distributed by this repository.
