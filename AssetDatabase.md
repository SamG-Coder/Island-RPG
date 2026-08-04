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
