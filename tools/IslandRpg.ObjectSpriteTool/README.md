# Island RPG Object Sprite Tool

Converts generated isometric object artwork into consistently sized,
transparent in-game sprites. It removes a chroma-key background, trims the
subject, sizes it from a named gameplay preset, applies a restrained
pixel-art colour finish, anchors it at ground contact, and writes metadata.

## Size standard

Object dimensions are gameplay units, not arbitrary image dimensions.

- One `1 x 1` ground unit projects to a `96 x 48` pixel diamond at 1x zoom.
- `footprintWidth` and `footprintDepth` control collision, placement, and the
  maximum projected width of the art.
- `height` is visual height in world units. One height unit is currently 48 px.
- `visualScale` adjusts only the rendered artwork inside that footprint. It
  does not shrink collision or placement. Use this to match character scale.
- The hotspot is the bottom-centre ground contact. Rendering should place this
  hotspot at the object's world position.
- Keep the generated object at the correct isometric angle. The converter
  normalizes scale and presentation; it does not invent a missing camera angle.

Initial definitions:

| Object | Footprint | Height | Intended scale |
|---|---:|---:|---|
| Workbench | 2 x 1 | 1 | 0.62; waist-high and about two character spaces long |
| Campfire | 1 x 1 | 0.3 | 0.78; low stone ring with separate fuel and flame overlays |
| Chair | 0.5 x 0.5 | 1 | 0.70; one seated character |
| Door | 0.5 x 1 | 2 | 0.75; full character clearance |

Edit `object-definitions.json` to add presets without changing the program.

## Prompt command

Copy this and replace the bracketed values:

```text
Create one [OBJECT], comparable in scale to an adult game character.
Gameplay footprint: [WIDTH] x [DEPTH] world units; visual height:
[HEIGHT] world units. Classic late-1990s/early-2000s isometric RTS sprite,
Age-of-Empires-II-inspired visual language, hand-painted pixel-art appearance,
earthy restrained palette, crisp readable silhouette, pre-rendered sprite
look. Orthographic isometric three-quarter view, camera looking down about
30 degrees. Entire object visible with generous padding. Bottom-centre ground
contact must be clear. Warm neutral light from upper-left. Exactly one object
on a perfectly flat solid #FF00FF background. Background must have no shadow,
gradient, texture, floor, or lighting variation. Do not use #FF00FF in the
object. No character, scenery, text, watermark, cast shadow, or contact shadow.
```

## Convert

```powershell
dotnet run --project tools/IslandRpg.ObjectSpriteTool -- `
  --input artifacts/object-sprites/workbench/workbench-source.png `
  --output artifacts/object-sprites/workbench/workbench.png `
  --preset workbench `
  --preview artifacts/object-sprites/workbench/workbench-preview.png
```

The command creates:

- `workbench.png`: transparent, game-sized sprite.
- `workbench.object.json`: footprint, pixel size, and hotspot.
- `workbench-preview.png`: 4x checkerboard inspection image; red cross is the
  ground hotspot.

## Animation overlays

Generated animation grids can be chroma-keyed, normalized to one consistent
scale, anchored, and composited over a finished object for inspection:

```powershell
dotnet run --project tools/IslandRpg.ObjectSpriteTool -- `
  --animation-sheet `
  --input <4x4-source.png> `
  --base <finished-object.png> `
  --output <runtime-horizontal-sheet.png> `
  --preview <composite-4x4-preview.png> `
  --columns 4 --rows 4 `
  --canvas-width 58 --canvas-height 58 `
  --target-width 24 --target-height 30 `
  --anchor-x 29 --anchor-y 38
```

The runtime output is a transparent horizontal strip. Every frame uses the
same scale and bottom-centre anchor, preventing animation jitter.

For layout checks, `--fuel <sheet.png>` can insert an existing item beneath
the fire. Select its source cell with `--fuel-x`, `--fuel-y`, `--fuel-width`,
and `--fuel-height`; size and position it with `--fuel-target-width`,
`--fuel-target-height`, `--fuel-anchor-x`, and `--fuel-anchor-y`.
Pass `--hide-animation true` to preview the base and fuel without the
animation overlay.
Pass `--hide-base true` to export a transparent animation-only strip for
runtime composition.
