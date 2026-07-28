# Island RPG

Island RPG is an experimental isometric survival and skilling RPG built with
C#, .NET and OpenTK. It combines a persistent procedurally generated world with
classic point-and-click interactions, gathering, crafting, fishing, cooking and
placeable objects.

The project is currently at **v0.2.0**. It is playable, but it remains an early
prototype and its systems, balance and save format may continue to evolve.

Visit the [Island RPG website](https://samuelgrahame.github.io/AoeRpg/) for a
visual overview, release history and installation links.

See [the v0.2.0 release notes](release-notes/RELEASE_NOTES_0.2.md) for the complete feature
summary.

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
git clone https://github.com/samuelGrahame/AoeRpg.git
cd AoeRpg
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

## Playing

The default command opens the playable game and its character/world menus.

| Input | Action |
|---|---|
| Left click | Move or perform the default interaction |
| Right click | Open an object's context menu |
| Mouse wheel | Zoom around the pointer |
| Drag an inventory item | Reorder, drop, fuel, cook or place where supported |
| `Alt` | Highlight collectible ground items |
| `Enter` | Focus or submit chat |
| `Escape` | Close the active screen or open the pause menu |

Use the on-screen **Skills** and **Bag** buttons for progression and inventory.
Click any skill name inside its detail panel to open its level guide.

### v0.2 progression loop

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
   to deposit, stack, withdraw, and preserve supplies with the world.

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

This is an early playable prototype. Combat, health, hunger, NPC simulation,
building construction and audio are not implemented. Farming covers tree
planting and berry foraging, Cooking supports fish and berries, and placeable
furniture remains limited. See the
[v0.2.0 release notes](release-notes/RELEASE_NOTES_0.2.md) for more detail.

## License and acknowledgements

Island RPG's original software and documentation are licensed under the
[MIT License](LICENSE).

Third-party libraries and the bundled Arimo font retain their own licenses.
See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Age of Empires assets and trademarks are not covered by the Island RPG license
and are not distributed by this repository.
