# Test Assets

This folder is the sparse original-asset source used when the developer
setting **Use Test Assets** is enabled. Missing entries use generated debug
placeholders and never fall back to Age of Empires assets.

The setting applies after restarting the game.

## Folder layout

```text
TestAssets/
|-- Graphics/
|   |-- VMBAS_FN.png
|   `-- VMBAS_WN/
|       |-- 000.png
|       `-- 001.png
|-- Terrain/
|   `-- g_grs_00_color.png
`-- Water/
    |-- normal0.png
    `-- normal3.png
```

Use a single named PNG for a static graphic. Use a directory of alphabetically
ordered PNG frames for animation. Graphic frames use a bottom-centre hotspot.
Directional animations contain five equal direction groups.

Terrain images must all use matching dimensions. Missing graphics, terrain,
and water normals are generated as visible debug placeholders.

`Terrain/terrain-atlas-01.png` through `terrain-atlas-05.png` are the
high-resolution source sheets for the included 512x512 terrain tiles. Runtime
filenames map to game biomes as follows:

```text
g_wt4_00_COLOR   Deep Water
g_wt3_00_color   Shallow Water
g_sha_00_color   River Water
g_sh3_00_color   Mangrove Shallows
g_bch_00_color   Beach
g_for_00_color   Forest
g_fo2_00_color   Jungle Floor
g_gr5_00_color   Dry Grass
g_gr4_00_color   Mud
g_gr3_00_color   Highland
g_rck_00_COLOR   Rock
g_sng_00_color   Tundra
g_sno_00_color   Snow
g_pal_00_color   Desert Sand
g_pal1_00_COLOR  Cracked Earth
g_grs_00_color   Grassland
```

## Water animation

`Water/water-height-atlas.png` is the grayscale source used to derive the four
512x512 tangent-space normal maps sampled by the animated water shader:

```text
normal0.png  Primary deep-water swells
normal1.png  Secondary deep-water undulations
normal2.png  Primary shallow-water ripples
normal3.png  Secondary shoreline ripples
```

The shader scrolls and combines these layers at different scales and speeds;
they are normal maps rather than sequential animation frames.
