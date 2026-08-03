---
name: island-rpg-live-playtest
description: Play Island RPG through its real named control pipe while running the .NET game under dotnet watch, diagnose observed gameplay failures, implement focused reusable fixes, and add small evidence-driven features that improve the active play session. Use when asked to play, live-test, speed-run, debug while playing, fix bugs on the fly, evolve the game during play, or autonomously find and add fun low-risk improvements in the Island RPG repository.
---

# Island RPG live playtest

Work from the repository root. Treat gameplay as the source of evidence and
`dotnet watch` as the edit loop.

## Start safely

1. Read `git status --short --branch`. Preserve unrelated changes.
2. Run the solution build and WorldChecks before long play unless the user
   explicitly wants immediate reproduction.
3. Choose a unique pipe name such as `codex-live-<timestamp>`.
4. Start the game in a persistent terminal session:

   ```powershell
   dotnet watch --project src/IslandRpg/IslandRpg.csproj run -- --control-pipe <pipe-name>
   ```

5. Wait for the pipe. Send commands without changing the machine execution
   policy:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File <skill-path>\scripts\send-pipe.ps1 -PipeName <pipe-name> -Command help
   ```
6. Start a fresh game unless the task requires an existing save. Never inject
   inventory or edit saves during a legitimate playtest.

Read [references/control-pipe.md](references/control-pipe.md) when controlling
the game or diagnosing pipe responses.

## Play and observe

- Play through queued actions, quests, combat, containers, dialogue, travel,
  crafting, survival, fishing and caves as available.
- Query `state` after actions. A success response proves only that work was
  accepted; confirm the resulting state or visible world change.
- Take screenshots at milestones and when visual behaviour looks wrong.
- Read non-debug chat history and relevant runtime logs.
- Record exact reproduction inputs, state before/after, elapsed time and the
  expected behaviour before editing.
- Prefer continuing the game naturally over issuing artificial diagnostic
  actions that a player would not use.

## Fix on the fly

1. Trace the failing path into the shared gameplay service/controller.
2. Add a focused regression test before or with the fix.
3. Reuse entity interaction, inventory, pathing, feedback, action and
   controller layers. Do not add a player-only or pipe-only duplicate.
4. Patch the smallest coherent scope with `apply_patch`.
5. Let `dotnet watch` apply supported method-body changes. Watch its output.
6. If Hot Reload reports a rude edit, shader initialization changed, or a
   constructor must rerun, stop the game cleanly and restart `dotnet watch`.
7. Reproduce the same action in the running game and verify the result.

Do not treat Hot Reload acknowledgement as verification. Existing instances,
GPU programs, generated chunks and initialized state may require recreation.

## Add a fun feature

Only add a feature after game-breaking failures in the current loop are fixed.
Choose one small addition that follows directly from play evidence and:

- creates a visible decision, reward, risk or feedback improvement;
- reuses existing systems and assets;
- can be completed and tested in the current session;
- does not introduce a new broad subsystem;
- preserves saves and deterministic world generation where relevant.

Examples include better action feedback, a useful loot integration, a compact
context action, a quest-flow improvement, enemy tuning, or a small interaction
that makes an existing item meaningful. Do not add speculative AI architecture,
large content sets or unrelated refactors.

## Verification loop

After each change:

1. Run the focused regression.
2. Replay the exact live scenario.
3. Inspect state, screenshot or logs for proof.
4. Continue playing long enough to detect the next-order regression.

Before reporting completion:

1. Stop the game through the pipe and end `dotnet watch` cleanly.
2. Run `dotnet build IslandRpg.slnx --no-restore`.
3. Run the complete `IslandRpg.WorldChecks` suite.
4. Run `git diff --check` and inspect `git status --short`.
5. Remove only temporary artifacts created by this session.

Report playtime, progression, bugs reproduced, fixes, feature added, live proof,
build/test totals, changed files and remaining risks. Do not commit or push
unless the user asks.
