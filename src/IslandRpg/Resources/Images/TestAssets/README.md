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

## Trees

Original tree source sheets are stored under `SourceSheets/Trees`. Runtime
frames use the installed game's frame counts and canvas dimensions:

```text
TREEA-TREEL  12 single-frame generic trees  102-176 x 127-182
FPAL_NN      13 palm variants               61-129 x 75-168
FPIN_NN       9 pine variants               72-106 x 118-157
FOAK_NN      14 oak variants                93-128 x 127-188
FJUN_NN      13 jungle variants             71-118 x 59-178
FSNO_NN       9 snow-pine variants          72-106 x 118-157
FBAM_NN       4 bamboo variants             53-71 x 69-91
FCAC_NN       6 cactus variants             14-21 x 14-61
STUMP_NN      3 stump variants              20-29 x 19-20
STUMB_NN      2 large stump variants        43 x 26
```

Multi-frame graphics use zero-padded files such as `FPAL_NN/000.png`.
Corresponding `_N0` directories contain original translucent ground shadows
derived for these sprites rather than copied assets.

## Bushes and plants

Original vegetation source sheets are stored under `SourceSheets/Bushes`.
Runtime frames match the installed game's variant counts and canvas sizes:

```text
PLANTS      5 ground-plant variants       18-39 x 15-34
BUSH_NN     2 large-shrub variants       108-118 x 68-74
BUSH2_NN   18 woodland-shrub variants     53-99 x 47-71
BUSH3_NN    9 alpine flowering shrubs     53-113 x 49-74
FORAG_NN    4 temperate berry bushes      69-88 x 50-64
FORAGM_NN   4 tropical berry bushes       72-82 x 61-66
```

`BUSH2_NN` frames `012` through `017` are the snow-covered variants selected
on snow terrain. The three bush families have matching `_N0` shadow folders;
plants and berry bushes do not request separate shadow graphics.
