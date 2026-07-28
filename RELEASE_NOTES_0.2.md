# Island RPG v0.2.0

**Caves and connected progression — 28 July 2026**

Island RPG `v0.2.0` expands the first playable prototype into a connected
overworld-and-cave survival loop. Dig for cave passages, secure entrances with
rope, explore persistent underground terrain, mine geological resources, and
bring those materials back into Crafting progression.

## Highlights

### Underground exploration

- Dig persistent holes with a stone shovel and discover matching cave space.
- Install, retrieve and render ropes consistently across both world levels.
- Traverse with dedicated climb cursors and retain exact entrance locations.
- Explore blended cave materials, water, geological formations and rare ruins.
- World-space darkness, player light, entrance light and merging local lights.

### Mining and tool progression

- Mine coal, tin, copper, iron and stone with a dedicated miner animation.
- Persistent node health, depletion, health bars and size-aware interaction.
- Destroy large geological formations for Mining XP without item rewards.
- Cast copper and tin into bronze bars before forging bronze tools.
- Smelt iron ore into a bloom, then reheat and hammer out the slag to make bars.
- Forge stronger bronze and iron pickaxes plus an iron woodcutting axe.
- Craft and place a clay bloomery; smelting recipes only work while nearby.
- Build a smithing anvil from early bronze and forge bars and tools beside it.
- Automatically select the strongest available pickaxe.

### Farming, foraging and cooking

- Gather wild berries from temperate forage bushes.
- Gather tropical berries from warm-climate forage bushes.
- Persistent berry regrowth using the shared vegetation cooldown system.
- Earn Farming XP from foraging.
- Roast berries over campfires through the existing Cooking pipeline.

### Lighting, interface and feedback

- Persistent underground campfires with level-scaled burn duration and radius.
- Local lights merge rather than overwrite one another.
- GPU-atlas-batched level-up fireworks with a short-lived light pulse.
- Level-aware minimap and developer map presentation.
- Dedicated interaction cursors for mining and cave traversal.
- Improved batched font rendering and readable text treatment.
- Version and project credits are identified directly on the main menu.
- All eight skills now expose a shared level guide with unlocks or level effects.

### Performance and persistence

- Active-level filtering prevents overworld and cave simulation contamination.
- Cached vegetation and mining hit tests avoid additional world scans.
- Fixed particle pools and reusable render buffers avoid frame allocations.
- Crafting category and availability data is cached and allocation-free while
  the recipe window renders.
- Shared scrolling lists iterate visible rows without per-frame range objects.
- Underground campfires, mining state, entrances and mutable objects persist.
- Unchanged deterministic cave chunks skip snapshot and save work.

## Compatibility

Existing v0.1 player and world data remains loadable. Chunk payloads are
versioned and older payloads receive safe defaults for newly persisted fields.

As before, a legally owned Age of Empires II HD installation is required at
runtime. No Age of Empires assets are included or distributed with Island RPG.

## Validation

The release is covered by the deterministic world-check suite, including
generation seams, cross-level caching, cave persistence, campfire state,
Mining progression, Farming forage rewards, Cooking profiles, cursor mappings,
particle performance foundations and region storage.
