# Dedicated-server / single-player parity

Living checklist for world actions and skills. Single-player is the
reference. Dedicated-server multiplayer must walk the same distance, play
the same clip, show that clip to remotes, and commit the same result on
the server.

| Mark | Meaning |
| --- | --- |
| ✓ | Matches single-player: walk range, clip, remotes, authority |
| ~ | Playable, but walk, clip, remotes, or authority still differ |
| ✗ | Missing, local-only, or does not match |
| — | Not a single-player action (multiplayer-only) |

Counts: **42 ✓ · 0 ~ · 2 ✗ · 2 —**

## Gathering and resources

| Action | Skill | Parity | Notes |
| --- | --- | --- | --- |
| Pick up ground item | — | ✓ | Walk `.46`. `PresentSkill Gather` at begin. Commit does not restart the clip. Item hides for other clients. Late joiners receive picked generated-loot IDs so already-taken sticks/rocks/seeds stay hidden. |
| Drop item | — | ✓ | Walk `StandOff(.46)` then Gather. Remotes see Gather. |
| Cut tree | Woodcutting | ✓ | Walk tree range. `PresentSkill Work` at begin. Cancel/deplete presents Idle so remotes leave Work. |
| Gather sticks | Woodcutting | ✓ | `PresentSkill Gather` at begin. Tree is not hidden when Remaining is only stick stock. |
| Gather fibres | Farming | ✓ | Walk `.72`. Right-click menu matches SP. `PresentSkill Gather`. |
| Gather berries | Farming | ✓ | Expiry uses `FarmingSkill.GatherSeconds`. Late commit does not start a second clip. |
| Mine (underground) | Mining | ✓ | Walk `.82`. `PresentSkill Mine` at begin. Cancel/deplete presents Idle. |
| Harvest crop | Farming | ✓ | Same Gather windup as pickup, then `HarvestCrop`. Commit does not restart Gather. |
| Plant crop | Farming | ✓ | Authoritative `PlantCrop`. No body clip on either path. |
| Plant tree | Farming | ✓ | Shared `PlantedTreeService`. Starts as a scaled shrub, grows, then compacts. Always shows a health bar and `Planted by X`. Living-tree cap is 12 per planter. Chopping is a world-object strike. The felled trunk fades then removes. |
| Fill / empty bucket | Crafting | ✓ | Craft a wooden bucket. Use then right-click water: sea → seawater, rivers/caves → water. Walk `.72`, Gather, then fill. Right-click a filled bucket to empty it. |

## Combat

| Action | Skill | Parity | Notes |
| --- | --- | --- | --- |
| Attack enemy | Attack / Strength / Defence | ✓ | Walk `AttackRange` `.82`. `PresentSkill Attack`. Remotes play then clear Attack. |
| Combat stance | Attack / Strength / Defence | ✓ | Server-owned. No clip either side. |
| Death / respawn | — | ✓ | Remotes see Die. Local overlay waits `DeathAnimationSeconds` before the respawn modal, same as SP. |
| Attack training dummy | Attack / Strength / Defence | ✓ | Walks close. `PresentSkill Attack` from the first swing. Hits, dummy HP, reset, and stance XP are server-owned. |
| Attack villager | Attack / Strength / Defence | ✗ | Villagers are cleared on dedicated join. No server villager combat. |

## Cooking and firemaking

| Action | Skill | Parity | Notes |
| --- | --- | --- | --- |
| Cook on campfire | Cooking | ✓ | Walk `.72`. `PresentSkill Gather` for `CookingSkill.PlacementAnimationSeconds`. Commit does not restart. |
| Take campfire fuel | — | ✓ | Walk `.72`. `PresentSkill Gather`. |
| Light campfire | Firemaking | ✓ | Walk `.72`. Instant after arrival on both paths (no looping clip). |
| Add campfire fuel | Firemaking | ✓ | Walk `.72`. Instant after arrival on both paths. |
| Cook stew (pot) | Cooking | ✓ | Walk `.82`. Gather for placement + cook seconds. `CookStew` is server-owned (pot, nearby lit fire, ingredients, XP). |

## Building and stations

| Action | Skill | Parity | Notes |
| --- | --- | --- | --- |
| Use crafting station / craft | Crafting | ✓ | Walk `1.15`. Craft is server-owned. No body clip either side. |
| Open storage / transfer | — | ✓ | Walk `.9`. Private container protocol. |
| Place furniture / placeable | — | ✓ | Walk the SP footprint stand-off (`max(w,d)*0.5+.55`), Gather, then `PlaceInventoryWorldObject`. |
| Place construction foundation | — | ✓ | Same footprint stand-off as SP placeable math. |
| Build construction | — | ✓ | Walk to `ClosestInteractionPoint` at `.24`. `PresentSkill Build` at begin. Hammer commits keep Build. Cancel presents Idle. |
| Demolish unfinished site | — | ✓ | Instant like SP. Owner/refund is server-owned. |

## Caves and digging

| Action | Skill | Parity | Notes |
| --- | --- | --- | --- |
| Continue excavation | Digging | ✓ | Walk `.82`. `PresentSkill Dig` at begin. Finish/cancel presents Idle. |
| Start excavation | Digging | ✓ | Walk `.82`. `PresentSkill Dig` when the first shovel starts, not after the commit. |
| Enter / climb cave | — | ✓ | Walk `.72`. Traverse is server-owned. |
| Restore excavation | Digging | ✓ | Walk `.82`. Instant. |
| Take cave rope | — | ✓ | Walk `.82`. Instant. |
| Install cave rope | — | ✓ | Walk `.46` with Gather, then install. |
| Fill excavation | Digging | ✓ | Walk `.46` with Gather, then fill. |

## Boats and fishing

| Action | Skill | Parity | Notes |
| --- | --- | --- | --- |
| Fish from shore | Fishing | ✓ | Walk `FishingNetReach()` (`1.1`–`2.4`). `PresentSkill Fish` at begin. Catch does not restart the loop. Cancel publishes Idle. |
| Fish from boat | Fishing | ✓ | Start at net reach + `.45` deck. Remotes see the boat fishing composite. Cancel aboard presents Idle. Stronger nets shorten server cadence. |
| Board / move / stop / disembark boat | — | ✓ | Board walk is `1.25`, same as SP. Move/stop/disembark are server-owned. |
| Follow another player | — | — | Multiplayer-only. Server stands off `1.6`. |

## Inventory and survival

| Action | Skill | Parity | Notes |
| --- | --- | --- | --- |
| Swap inventory slots | — | ✓ | |
| Combine / sharpen tool | — | ✓ | |
| Eat / apply medicine | — | ✓ | Instant. No clip either side. Timed healing is server-owned. |
| Hunger / starvation / regen | Adventure | ✓ | Server survival tick. |
| Quests / adventure XP | Adventure | ✓ | Server-owned progress. |
| Chat | — | ✓ | |
| Trade with a player | — | — | Multiplayer-only. |
| Give item to villager | — | ✗ | Villagers are not on the dedicated server. |

## Known remaining gaps

1. **Villagers** — no attack, gift, or other NPC actions in dedicated multiplayer. The server has no villager simulation.
2. **Plant tree** — still blocked on the network client. Crop planting is fine. Planted trees would need a resource-node overlay plus local tree injection to be choppable.

Walk ranges live in `WorldActionReach`. Remote clips live in `ActorSkillStance` (`PresentSkill` at begin; one-shot commits do not restart Gather/Fish; looping cancel presents Idle).
