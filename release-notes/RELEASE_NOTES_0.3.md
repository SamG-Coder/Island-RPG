# Island RPG v0.3.0

**Living Survivors and Slime Combat — 3 August 2026**

Version 0.3 brings other people and hostile wildlife into the survival loop.
AI survivors now arrive with personal histories, skills, memories, needs and
limited knowledge. They can organize, disagree, cooperate, make commitments,
choose leaders and work toward a shared camp. The player can observe these
systems directly or survive alongside them.

This release also introduces the first roaming enemies: biome-specific slimes
with reusable combat behavior, illuminated attacks and persistent loot bags.

## Release highlights

### Autonomous survivors

- Enable up to ten AI survivors from the Advanced new-game settings.
- Give each survivor a name, personality, backstory and starting inventory.
- Survivors remember people, promises, violence, skills and personally
  discovered resource or danger locations.
- Hunger, health, energy, danger and personality influence current priorities.
- Survivors gather, craft, forage, fish, cook, plant, mine, fight and rest
  through shared action and interaction services.
- Low-energy survivors interrupt optional work to rest, while hunger and danger
  remain urgent overrides.
- Survivors can request items, approve or refuse proposals, bargain, trade,
  make promises and execute accepted plans instead of remaining in dialogue.
- Dead survivors stop acting, release reservations and leave persistent remains.

NPC knowledge is deliberately local. Survivors do not begin knowing who the
player is, do not receive global map information and do not automatically share
private memories.

### Group formation and settlement planning

- Shipwreck survivors visibly react to opening incidents before organizing.
- Groups gather into a rough council circle, introduce themselves and discuss
  skills before selecting temporary leadership.
- Leadership is influenced by personality, demonstrated actions and social
  support rather than a fixed character index.
- Leaders can request reconnaissance and assign settlement work.
- Scouts explore different sectors and report food, wood, stone, water, danger
  and defensible terrain.
- Groups can select a camp, establish a shared ground cache and contribute
  materials to campfires, storage and building projects.
- Members may object, lose respect, challenge leadership or leave the group.
- Small populations remain all-rounders so one- and two-person games are not
  locked out of crafting or exploration.

### Observe mode and AI controls

- Observe mode keeps the world active around survivors while allowing free
  camera movement, zoom, chat, commands, minimap access and pause menus.
- A survivor dashboard shows status, needs, role, health, hunger, energy,
  current thought, inventory, skills and memories.
- Selecting a survivor snaps the camera to them and can follow their activity.
- Live observe logs and periodic summaries make long simulations diagnosable.
- AI model names can be overridden from Advanced setup; the default local model
  is `Gemma4:12B`.
- Local Ollama requests keep the selected model resident for up to 30 minutes.
- The expanded named-pipe interface can create or load games, walk, inspect the
  world, read chat, craft, use items, interact, fight and capture screenshots.

AI survivors require a running, responsive Ollama model. They remain optional;
normal player-only games do not require Ollama.

### Shipwreck opening

- New shoreline games begin with a skippable storm-at-sea cinematic.
- A reusable cinematic director controls camera pans, zoom, scene fades,
  letterboxing, lighting, sound and tracked scene objects.
- The ship sails through animated deep water at night, is struck by lightning,
  catches fire and sinks before the camera transitions to the beach.
- Opening survivor incidents are seed-driven and can include rescue, injury,
  wreck-supply disputes, shared loss and panic.
- Injured survivors remain down until a helper physically reaches them, while
  exposure at the shoreline makes aid time-sensitive.

The cinematic plays only when the selected world actually begins on a shore.
Press `Escape` to skip it.

### Slimes and reusable enemy combat

- Water slimes inhabit beaches, grass slimes inhabit vegetation, sand slimes
  inhabit deserts and cave slimes inhabit the underground.
- Surface slimes are passive until attacked; cave slimes can acquire nearby
  targets automatically.
- Enemy spawners activate around players, enforce local population limits and
  scale later waves after a recovery period.
- Enemies roam within a bounded home area, chase only within their leash and
  lose aggro when targets escape.
- Configurable reaction delays prevent passive enemies from retaliating on the
  exact frame of the first hit.
- Slimes use directional movement, shader shadows, transparency, local light,
  elemental attack particles, health feedback and a completed death animation.
- Defeated slimes drop withdraw-only loot bags which fade after their final item
  is removed.

New slime materials include slime gel, slime cores, salt crystals and medicinal
herbs. These four loot materials are the only stackable player items. They feed
new crafting alternatives, preserved food, medicine, advanced tools and NPC
planning.

### Shared combat and entity interaction

- Players, survivors, enemies, trees, mining nodes and other damageable targets
  now share combat timing, health feedback, hit splats and interaction results.
- Attacks have per-actor cooldowns and cannot be reset by movement.
- Zero or negative combat damage is rejected.
- Survivors can attack, retaliate, flee and remember skill or combat outcomes.
- A knife is classified as a weapon and is the current carried item that adds
  bonus melee damage.
- Living entities regenerate health at the hunger-rate baseline; human actors
  recover substantially faster beside a lit fire.

### Inventory, quests and controls

- The player inventory remains 28 slots and now uses the reusable inventory
  container implementation.
- Slime-loot stack quantities persist through saves, transfers, crafting,
  cooking, eating, dropping, gifts, promises and quest checks.
- Legacy inventories migrate without losing duplicated items.
- Right-clicking an entity offers contextual Walk here, Attack, Give and Examine
  actions where applicable.
- Chat size can be Small, Medium or Large with optional text wrapping.
- Active quest requirements are tracked on the right side of the game view.
- Quest objectives re-evaluate whenever inventory state changes, including
  recipe conversions and item use.
- New games use the normal playable spawn search; the opening cinematic is
  conditional on finding a shoreline rather than forcing every seed to one.

### Performance and reliability

- Long-distance A* searches cache terrain passability and elevation and use a
  diagonal-aware heuristic.
- Live testing reduced a representative long route from roughly 4–10 seconds
  to about half a second on the development machine.
- Replacement paths stop superseded movement, preventing the actor from walking
  back to a stale route origin.
- Movement spends its full frame-distance across dense navigation waypoints.
- NPC survival catch-up processes all elapsed time without discarding overflow.
- Starvation damage tracks exact starving time and preserves fractional damage.
- Dead-state guards cover decision, movement, work, social and conflict paths.
- Theft incidents are deduplicated, unreachable targets are temporarily
  blacklisted and abandoned reservations are released immediately.
- Promise delivery is atomic, broken-promise consequences apply once and
  expired conflicts clear through normal lifecycle maintenance.

## Compatibility

Existing v0.1 and v0.2 player and world saves remain loadable. Player inventory
quantities and newer NPC state use backward-compatible defaults. As this is an
early-access release, backing up `%LOCALAPPDATA%\IslandRpg` before upgrading is
still recommended.

Island RPG requires Windows, .NET 10 and a legally owned Age of Empires II HD
installation at runtime. Compatible Age of Empires assets are loaded locally
and are not included or redistributed with Island RPG.

Optional AI survivor features additionally require
[Ollama](https://ollama.com/) and a locally installed chat model. `Gemma4:12B`
is the default; another model name can be entered in Advanced new-game setup.

## Validation

Version 0.3 is covered by the complete `IslandRpg.WorldChecks` suite. At release
preparation it passes **68,335 assertions**, including:

- deterministic generation, persistence and save compatibility;
- NPC survival, fatigue, memory, leadership, promises and settlement work;
- combat cooldowns, enemy reactions, spawners, leashes and death lifecycles;
- slime loot generation, inventory conservation and container transfers;
- quests, crafting, cooking, caves, storage and entity interactions;
- pathfinding correctness and dense-waypoint movement;
- render, streaming, transition and simulation performance checks.

## Known limitations

- AI quality and response time depend on the chosen local model and available
  hardware. Large models can consume significant system RAM.
- NPC dialogue and high-level planning are experimental and may still produce
  repetitive or impractical choices during very long simulations.
- Navigation uses grid-based routing rather than full hierarchical pathfinding;
  extremely distant or unreachable clicks can still take longer than local
  movement requests.
- Slimes are the only fully integrated enemy family in this release.
- Multiplayer, sleep schedules, beds, morale, rumours and shared NPC maps are
  outside the v0.3 scope.

## Thank you

Thank you for testing the simulations, reporting what happened on screen and
pushing the systems beyond scripted checks. Version 0.3 is substantially shaped
by live play sessions: the game now measures success by what actors visibly do,
not only by what their internal state says happened.
