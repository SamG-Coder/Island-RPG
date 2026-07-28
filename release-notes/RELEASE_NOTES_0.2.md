# Island RPG v0.2.0

**Caves and Connected Progression — 29 July 2026**

Version 0.2 transforms Island RPG from an overworld prototype into a connected
survival and progression experience. Players can now excavate cave entrances,
explore a persistent underground world, gather new resources, process them at
crafting stations, cook food and store their supplies.

## Release highlights

### Discover and explore caves

- Use a stone shovel to excavate clear, non-water terrain.
- Digging creates a persistent work site with material-dependent health.
- Completed excavations reveal whether a natural cave exists below.
- Attach a rope to a successful shaft to travel between world levels.
- Retrieve the rope, refill unfinished excavations or permanently close a shaft.
- Entrance position, rope state and light are synchronized above and below.
- Dedicated cursor artwork clearly identifies digging and climbing actions.

The underground is procedurally generated with blended rock materials, natural
boundaries, small waterways, ore deposits, vegetation, ruins and geological
formations. Darkness is treated as a gameplay environment: the player, cave
entrances, campfires and level-up effects provide local light, with nearby
lights blending together naturally.

### Mining and metalworking

- Mine stone, coal, tin, copper and iron deposits with a pickaxe.
- Mining nodes have persistent health, accurate selection bounds and depletion.
- Large rock formations can be destroyed for Mining XP.
- The strongest available pickaxe is selected automatically.
- Convert copper and tin into bronze bars.
- Smelt iron ore into blooms, then reheat and hammer away the slag.
- Forge bronze and iron pickaxes, axes and sickles.
- Burn logs into renewable charcoal for smelting and smithing.

Metalworking now has a connected station-based workflow. Reach Crafting level 3
and work near a workbench to construct a clay bloomery and smithing anvil.
Bloomery recipes are only shown at the bloomery, while forging recipes are
filtered to the anvil.

### Farming, foraging and cooking

- Harvest wild and tropical berries from climate-appropriate bushes.
- Berry bushes use persistent regrowth timers shared with world vegetation.
- Foraging awards Farming XP and benefits from improved sickles.
- Roast berries and fish over a lit campfire.
- Craft a bronze cooking pot and place it beside a campfire.
- Combine fish and berries into stew through the Cooking skill.

### Persistent storage

- Craft and place wooden chests and storage barrels.
- Wooden chests provide 48 slots; barrels provide 40 slots.
- Container contents persist independently in the world and underground.
- Stack items and withdraw or deposit 1, 5, 10, 25, 100 or all.
- Quickly deposit the entire player inventory when space permits.
- Scroll large inventories and inspect items from the context menu.
- Storage uses a dedicated interaction cursor and the reusable container UI.

The developer item bank uses the same container system and can display every
registered item, grouped by category, for testing.

### Interface and presentation

- Added a level-aware minimap and improved developer world map.
- Reworked settings pages and long lists around reusable scrolling controls.
- Added batched font rendering and a readability shader for UI text.
- Added optimized fireworks and a light pulse when a skill level is gained.
- Added persistent campfire lighting with larger skill-scaled radius and burn
  duration.
- Added skill guides describing unlocks and level effects for all eight skills.
- Added version information and credits to the main menu.

### Performance and reliability

- Isolated overworld and underground simulation to the active world level.
- Prevented inactive world interactions and UI state from leaking across level
  transitions.
- Added cached vegetation, mining and interaction hit tests.
- Added reusable render buffers, fixed particle pools and GPU-atlas batching.
- Cached crafting categories and recipe availability.
- Limited scrolling controls to visible rows.
- Avoided rewriting unchanged deterministic cave chunks.
- Added versioned persistence for excavations, ropes, mining nodes, campfires,
  storage containers and other mutable world objects.

## Compatibility

Existing v0.1 player and world saves remain loadable. Older chunk payloads
receive safe defaults for fields introduced in this release.

Island RPG requires a legally owned Age of Empires II HD installation at
runtime. Age of Empires assets are not included or distributed with the game.

## Validation

Version 0.2 is covered by the automated world-check suite, including:

- deterministic overworld and cave generation;
- terrain seams and cross-level isolation;
- cave entrance, campfire, mining and storage persistence;
- crafting, smithing, foraging and cooking progression;
- interaction cursor mappings;
- save compatibility and malformed-data safeguards;
- render-buffer, particle and interaction-probe performance checks.

## Thank you

Thank you for playing and helping shape Island RPG. Version 0.2 establishes the
core gathering, exploration, crafting and storage loop that future releases can
build upon.
