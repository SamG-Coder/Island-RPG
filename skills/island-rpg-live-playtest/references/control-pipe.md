# Island RPG control pipe

Use the in-game named pipe rather than Observe mode when the task is to play.

## Send commands

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File <skill-path>\scripts\send-pipe.ps1 `
  -PipeName <pipe-name> `
  -Command state
```

For requests with arguments, UTF-8 encode the complete JSON as Base64 and pass
it with `-JsonBase64`. This avoids nested PowerShell quoting damage.

The helper prints the single JSON response and returns a failing exit code when
the connection or request fails.

## Core commands

- `help`: discover the current protocol. Prefer this over a stale command list.
- `new_game`: requires `character`, `world`; accepts `gender`, `seed`, `npcCount`.
- `load_latest`, `skip_cinematic`, `state`, `world`, `nearby`, `inventory`.
- `walk`, `act`, `craft`, `use`, `drop`, `combat_style`.
- `container`, `withdraw`, `withdraw_all` for world containers and loot bags.
- `chat`, `chat_history`, `events`, `screenshot`, `continue`, `stop`.

## Evidence rules

- Poll `state.actionQueue.readyForAction` before issuing the next physical action.
- Inspect `ui.blockers` and dismiss only the legitimate active modal.
- Treat `action_queued` as acceptance, not completion.
- Confirm inventory quantities, quest progress, entity health, target state,
  container state or position after completion.
- Use explicit entity IDs or stable keys when testing target selection.
- Preserve screenshots returned by the game when reporting visual failures.

## dotnet watch

Run:

```powershell
dotnet watch --project src/IslandRpg/IslandRpg.csproj run -- --control-pipe <pipe-name>
```

Keep its terminal session open and inspect every Hot Reload response. Restart
the watched game after rude edits, startup-path changes, embedded shader edits,
asset loading changes or world-generation changes that require fresh chunks.
