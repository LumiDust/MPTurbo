# Changelog

## 0.4.0 — Core Multi-Colonist Synchronization & Lockstep Determinism

- **Native Speed Synchronization (Default On)**: Directly hooked into RimWorld's bottom-right `TimeControls.DoTimeControlsGUI` and keyboard shortcuts (`Space`, `1`, `2`, `3`, `4`). Synchronizes pause and game speeds across all peers with server authority. Removed redundant HUD multiplier sliders.
- **Full Action Replication ("What single player can do, multiplayer can also do")**:
  - Replicated designations and designation cancellations (mining, cutting, harvest, hunt, tame, smooth, deconstruct).
  - Replicated blueprints, construction placement, and blueprint/frame cancellations.
  - Replicated pawn drafting and right-click ordered jobs (move, attack, interact, haul, rescue, prioritize).
  - Replicated item forbid/unforbid toggle (F key and buttons via `ForbidUtility`).
  - Replicated colonist work priorities in the Work Tab (`Pawn_WorkSettings`).
  - Replicated stockpile, dumping, and growing zone management (create, add cells, remove cells, delete zone).
  - Replicated workbench crafting bills (recipe add, repeat count, bill deletion).
  - Replicated colonist allowed area assignments in the Assign/Schedule tab.
- **Pawn Path State Persistence**: Implemented `TurboPawnPathState` MapComponent to serialize exact path nodes, total cost, and node indices on shared saves, avoiding vanilla load-time re-pathing stalls, movement cost reset, and filth RNG desync.
- **Cosmetic Effect Random Isolation**: Wrapped visual motes (`MoteMaker`), particles (`FleckMaker`), audio samples (`Sample`, `SubSustainer`), steam geysers (`IntermittentSteamSprayer`), and animal vocalizations (`Pawn_CallTracker`) in a seeded isolated RNG scope (`Rand.PushState(seed)`), preventing client rendering differences from altering the simulation RNG stream.
- **World Pawns & Wander Determinism**: Ensured canonical deterministic ordering of world pawns during `WorldPawnsTick`, and anchored wander destination selection using stable pawn ID seeds.
- **Dual-Client Lockstep Verification**: Successfully validated multi-cycle lockstep deterministic simulation and state hash matching (`PASS actual/logical tick invariant`) across independent RimWorld 1.7 processes.

## 0.3.0 — Development prototype

- Added the independent dedicated server and RimWorld 1.7 client connection interface.
- Added server-confirmed multiplier settings, revisions and controller permissions.
- Added exact game-version and mod/configuration fingerprint checks.
- Added content-addressed snapshots, verified local caching and resumable chunk downloads.
- Added completed-tick TPS diagnostics, reconnect isolation and integration tests.

Shared-colony loading and gameplay command replication remain in development. This version has not completed in-game two-client acceptance testing.
