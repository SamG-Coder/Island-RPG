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
