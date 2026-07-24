# Island RPG prototype

A minimal OpenTK prototype that reads Age of Empires II HD sprites from the
player's own installation. No Age of Empires assets are included.

Island RPG is an unofficial, non-commercial project. It is not affiliated with
or endorsed by Microsoft. Age of Empires is a trademark of Microsoft.

## Run

```powershell
dotnet run --project src/IslandRpg
```

The program checks common Steam locations and `AGE2HD_PATH`. By default a
single persistent game window moves through loading, GPU preparation, and a
pannable seeded infinite-world view. The world streams deterministic 32×32
chunks around the camera, saves them under the current user's local application
data directory, culls off-screen terrain batches, and unloads distant chunks to
keep memory and GPU use bounded. Chunk halos preserve Gaussian biome blending
and shoreline effects across chunk borders.
It uses a 2:1 isometric projection, corner-based elevation, coastal/inland biomes, and
biome-matched trees. Terrain is drawn first, then tree shadows and trees in
map-depth order. Deep and shallow water use the installed HD normal maps for
two-scale animated waves, refraction, and restrained specular highlights.
Buildings are intentionally excluded.

Startup first shows an asset-loading screen in that same window. Every discoverable DAT graphic is
classified, and every available loose SLP is validated and decoded into memory.
Legacy, superseded, unused, or otherwise unavailable DAT references are recorded
separately in `AssetCache/asset-report.json`; they are not automatically treated
as a damaged installation. Render
graphics remain separate from future gameplay entities, because one unit can
reference several graphics for movement, attacks, death, effects, and shadows.
Override either the install or graphic:

```powershell
dotnet run --project src/IslandRpg -- --age2-path "D:\SteamLibrary\steamapps\common\Age2HD" --graphic 495
```

Look up another graphic by its internal DAT name:

```powershell
dotnet run --project src/IslandRpg -- --graphic-name TREEA_NN
```

Open the complete world-asset preview explicitly:

```powershell
dotnet run --project src/IslandRpg -- --catalog
```

Open the generated island explicitly:

```powershell
dotnet run --project src/IslandRpg -- --island
```

Open or create an infinite world with a specific seed:

```powershell
dotnet run --project src/IslandRpg -- --world --seed 2187
```

The same seed produces the same island chains, elevation, ocean depth, biomes,
and trees. Use WASD, the arrow keys, or left-mouse dragging to move the camera.
The original finite island renderer remains available through `--island`.

Press Escape to close. The current prototype supports classic SLP frames,
palette colors, player-color pixels, shadows, hotspots, animation, and
nearest-neighbor scaling.

Hold the left mouse button and drag to pan the camera. Sprites render at native
1:1 pixels and keep the same physical pixel dimensions when the window is
resized or maximized.

Use the mouse wheel to zoom around the cursor position. Zoom is intentionally
limited to 0.65×–1.75×, with 1× as the native-pixel default.

The current DAT reader extracts graphic names, SLP IDs, frames per angle,
angle counts, frame timing, replay delay, mirroring mode, and graphic IDs.
Palette selection follows the SLP frame `properties` field. A value of `0x10`
selects Age2HD's standard game palette, `50500.bina`; the catalogue tree SLPs
all declare this value. Variant files such as `pal_2` are not assigned merely
from a graphic filename prefix.

Use `--validate` to test asset detection and decoding without opening a window.
