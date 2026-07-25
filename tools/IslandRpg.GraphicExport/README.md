# IslandRpg.GraphicExport

Searches the Age of Empires II HD DAT graphic catalogue and exports every
decoded SLP frame as a transparent PNG. Results are grouped by DAT graphic
name and accompanied by `manifest.csv`.

```powershell
dotnet run --project tools/IslandRpg.GraphicExport -- `
  --install "C:\Program Files (x86)\Steam\steamapps\common\Age2HD" `
  --query FISH `
  --output TestExport\Fish
```

Use `--query` for case-insensitive partial matches against DAT names and
filenames. Use `--exact` when known DAT graphic names should be exported
without metadata false positives. Both arguments may be repeated:

```powershell
dotnet run --project tools/IslandRpg.GraphicExport -- `
  --install "<Age2HD folder>" `
  --exact FISH1_NN --exact FISH2_NN `
  --output TestExport\Fish
```

`TestExport/` is intentionally ignored by Git so it can be reused for any
future asset audit.
