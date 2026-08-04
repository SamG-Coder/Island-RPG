# Asset Database

This file records classified Age of Empires II assets that Island RPG loads
from the player's local installation. DAT names and IDs are authoritative;
the footprint classification is Island RPG metadata used to select a suitable
construction visual for a building.

## Construction graphics

Every construction graphic contains three authored frames. The game loads all
three frames, even where a building deliberately presents only two phases.

| DAT graphic | Graphic ID | SLP | Native frame 0 | Footprint | Classification | Current use |
|---|---:|---:|---:|---:|---|---|
| `CNST1_NN` | 118 | 236 | 96 x 64 | 1 x 1 | Single tile | Registered for small buildings |
| `CNST2_NN` | 119 | 237 | 174 x 103 | 2 x 2 | House | All house variants |
| `CNST3_NN` | 120 | 238 | 269 x 147 | 3 x 3 | Medium building | Registered for future medium buildings |
| `CNST4_NN` | 121 | 239 | 354 x 189 | 4 x 4 | Large building | Registered for future large buildings |
| `CNST8_NN` | 123 | 241 | 440 x 231 | 5 x 5 | Very large building | Registered for future very large buildings |
| `CNST12_NN` | 124 | 243 | 722 x 373 | 8 x 8 | Monument | Registered for monuments/wonders |
| `CNSTD_NN` | 4248 | 4397 | 220 x 133 | 3 x 3 | Waterfront | Registered for docks/waterfront buildings |

The `CNST2_NN` house presentation uses frame 0 for the marked foundation and
frame 2 for the raised worksite. It then changes to the selected completed
house sprite. Frame 1 remains loaded and available for buildings that require
three construction phases.

Exports for visual auditing are produced by the reusable C# tool:

```powershell
dotnet run --project tools/IslandRpg.GraphicExport -- `
  --install "<Age2HD folder>" `
  --output "TestExport/HouseConstructionStages" `
  --construction-stages
```

## Standalone defensive buildings

Standalone defences use point placement, occupy a complete tile footprint and
are built through the same health-on-hammer-strike controller as houses. Every
listed architecture is exposed in the Defences browser.

| Family | DAT graphics | Variants | Footprint | Construction | Classification |
|---|---|---:|---:|---|---|
| Outpost | `WCTWX1NNG@3223` | 1 | 1 x 1 | `CNST1_NN` | Early wooden observation post |
| Watch tower | `WCTW1NNG{E,F,I,M,W,X}` | 10 | 1 x 1 | `CNST1_NN` | Basic tower |
| Guard tower | `WCTW2NNG{E,F,I,M,W,X}` | 10 | 1 x 1 | `CNST1_NN` | Reinforced tower |
| Keep | `WCTW3NNG{E,F,I,M,W,X}` | 10 | 1 x 1 | `CNST1_NN` | Heavy tower |
| Bombard tower | `WCTW4NNG{E,F,I,M,W,X}` | 10 | 1 x 1 | `CNST1_NN` | Late heavy tower |
| Castle | `CSTL3NN{E,F,I,M,W,X}` | 11 | 4 x 4 | `CNST8_NN` | Fortress |

The `X` DAT name is reused by five expansion graphic IDs, so atlas keys always
include the graphic ID. Castles also include the separately authored African
`CSTL3NNW@7633` variant. This produces 52 unique, save-stable standalone
defence definitions.

## Wall and gate variants

| Family | Completed DAT graphics | Buildable variants | Placement |
|---|---|---:|---|
| Fence | `FENCENNG` | 1 | Routed wall drag |
| Palisade | `FENCEN1G` | 1 | Routed wall drag |
| Fortified palisade | `WALL1N1G` | 1 | Routed wall drag |
| Stone wall | `WALL2NN{E,F,M,W,X}` | 10 | Routed wall drag |
| Fortified wall | `WALL3NN{E,F,M,W,X}` | 10 | Routed wall drag |
| Stone gate | `GTAA2NN*` + `GTAC2NN*`; construction `GTAX2CN*` frames 0–2 | 11 | One three-tile gate entity; authored scaffold progression |
| Fortified gate | `GTAA3NN*` + `GTAC3NN*`; construction `GTAX3CN*` frames 0–2 | 10 | One three-tile gate entity; authored scaffold progression |

The six `X` wall IDs and expansion gate IDs remain ID-qualified in the atlas.
Each gate is one three-cell asset and one saved world object. Its authored gate
span is layered with one matching wall section on either side at the offsets
recorded by the AoE composite layout. Those side sections are visual and
collision parts of the gate: they are not separately selectable walls, do not
create extra construction sites and do not have independent health. The
special palisade expansion gate is already self-contained.

The remaining `GTA*`, `GTB*`, `GTC*` and `GTD*` records are directional,
open/damaged and component layers of those 21 player-facing gate variants;
they are not duplicated as fake entries in the build browser.
