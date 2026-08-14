# Island RPG v0.4.0

**Dedicated Multiplayer — 14 August 2026**

Version 0.4 opens the island to other players. A dedicated server now owns the
world clock, movement, combat, gathering, crafting, construction and social
state. Clients present the same cursors and walk-to-range interactions as
single-player, then commit one authoritative command when they arrive.

Host a new world or continue a previous hosted save, join a friend by address,
or pick a LAN server from the same list used for saved hosts. The character
you select is the person who joins: name, colour and gender travel with you.

## Release highlights

### Host, join and stay connected

- The Multiplayer menu is a stepped flow: choose a character, then Host or
  Join. Long status and help text wrap instead of overflowing the panel.
- Host a fresh world from a seed, or resume a previously hosted world from
  the saved-world list.
- Join a friend by host and port, or save named servers the way Minecraft
  remembers multiplayer worlds.
- LAN games advertise themselves on the local network. Discovered servers
  appear in the join list without typing an address.
- The selected character's name, team colour and gender are the identity
  that joins. Reconnect resumes the same actor instead of minting a new one.
- The dedicated server listens on UDP/TCP port `38740` by default. LAN
  discovery uses a separate out-of-band beacon.

```powershell
dotnet run --project src/IslandRpg.Server -c Release
```

Clients and the dedicated server must share build `0.4.0` and content
`base`. Pass `--build-version` only when you intentionally pin a custom
build string.

### Gameplay that matches single-player

- World cursors, walk-to-range and one commit per action are the same as
  solo play: pick up, drop, open containers, cook, craft, place furniture,
  build, excavate, harvest and fight.
- Generated ground loot — sticks, rocks, crop seeds, seaweed and shells —
  uses stable IDs shared by solo generation and the dedicated server.
  Picking any of those items up no longer disconnects the client.
- Dropped items and other server-owned objects stay on the normal
  world-object revision path.
- Remote players and local Follow play the same walk cycle as a click to
  walk. Following stands beside the leader, not on their tile. Clicking
  away or walking yourself clears Follow so you can follow again.

### Social play

- Right-click another player for Trade or Follow.
- Trade publishes a shared offer window; both sides confirm before
  inventories change.
- Guilds, Friends and Ignore are server-owned. Each list is sent to the
  owning client and restored on reconnect.
- Ignore blocks trade, follow and chat from that player.

### Built for many players, not a full object flood

- The simulation ticks at 60 Hz. Movement snapshots go out at 20 Hz over
  UDP with interpolation on the client.
- Public snapshots carry actors, boats and enemies. World objects travel
  as bounded revisioned deltas so publish cost does not grow with every
  rock and stick in the seed.
- Join ingest is budgeted so a large baseline cannot stall players who
  are already in the world.

Authoritative systems already shared with solo play remain on the server:
caves, boats and fishing, combat, crops, quests, furniture and storage.

## Compatibility

Existing v0.1, v0.2 and v0.3 solo player and world saves remain loadable in
single-player. Hosted multiplayer worlds use dedicated-server checkpoints
under the hosted-worlds save root and are separate from a local solo world
of the same seed.

Clients and servers from earlier builds cannot join a v0.4 session. Backing
up `%LOCALAPPDATA%\IslandRpg` before upgrading is still recommended.

Island RPG requires Windows, .NET 10 and a legally owned Age of Empires II
HD installation at runtime. Compatible Age of Empires assets are loaded
locally and are not included or redistributed with Island RPG.

Optional AI survivor features still require
[Ollama](https://ollama.com/) and a locally installed chat model.

## Validation

Version 0.4 is covered by `IslandRpg.WorldChecks` and
`IslandRpg.NetworkingChecks`. At release preparation they pass
**68,602** world assertions and **272** networking checks, including:

- handshake, reconnect, build-version and content-version rejection;
- public world deltas without private inventory or container leaks;
- pickup and drop of every generated ground-loot kind on a real client;
- trade, follow, friends, ignore and guild list publication;
- LAN discovery beacons without a protocol-version change;
- remote walk presentation and follow retargeting;
- caves, boats, combat, resources, furniture and checkpoint restart.

## Known limitations

- Multiplayer currently targets a Windows dedicated server and Windows
  clients on the same build.
- Interactive OpenGL play cannot be driven by the automated suites;
  WorldChecks and NetworkingChecks are the release bar.
- Beach collectibles that respawn locally in solo play do not respawn in
  multiplayer; only the seed-generated coastal set is authoritative.
- AI survivors, NPC planning and Observe mode remain single-player
  systems and still depend on a local Ollama model.
- Slimes remain the only fully integrated enemy family.

## Thank you

Thank you for walking beside someone else on the same island and telling
us when the animation, the pickup or the join flow felt wrong. Version 0.4
is the first time Island RPG is a place you can share, not only a world
you can generate.
