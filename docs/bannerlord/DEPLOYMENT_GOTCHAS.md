# Bannerlord deployment / formation gotchas

Discovered the hard way while building AmbushStance's marching-column deployment. Cite source paths via the `bannerlord-sage` MCP before relying on these — TaleWorlds rewrites internals between patch versions.

## Arrangement

- **`ArrangementOrder.ArrangementOrderColumn` does NOT instantiate `ColumnFormation` during deployment.** It runs `RearrangeAux(false)` which calls `TransposeLineFormation(formation)` — installing a `TransposedLineFormation` (a `LineFormation` derivative) and registering `OnTick += TickForColumnArrangementInitialPositioning`. That tick handler has an `if (!IsDeployment ...)` guard, so the actual `ColumnFormation` only appears after deployment finishes (and only after agents move enough). Patching `ColumnFormation` to fix deployment-time behaviour is a no-op.
- **`ColumnFormation.GetWorldPositionOfUnit(int, int)` returns `null` in vanilla.** When (eventually) used, this makes the formation system fall back to `_movementOrder.CreateNewOrderWorldPositionMT` — the single formation-center point — and every agent ends up at the same spot.
- **`LineFormation` (and `TransposedLineFormation`) treat `Formation.OrderPosition` as the FRONT (rank 0), not the center.** `LineFormation.GetLocalPositionOfUnit` returns `vec.Y = -rank * (Distance + UnitDiameter)` — rank 0 at Y=0, higher ranks behind. Agent positions extend backward from `OrderPosition`. Measure formation extent with min/max projection along `Direction`: `maxProj ≈ 0`, `minProj ≈ -depth`.

## Direction conventions

- **`Formation.Direction` is the forward (facing) direction**, set by `SetPositioning(pos, dir, spacing)`.
- **`Vec2.TransformToParentUnitF` has a non-standard convention: `(0, 1)` is identity.** The `this` vector represents the LOCAL +Y axis mapped to world. Concretely: `result.x = this.y * a.x + this.x * a.y`, `result.y = -this.x * a.x + this.y * a.y`. Treat `this` as forward; local Y → forward, local X → lateral right.

## WorldPosition / navmesh

- **`new WorldPosition(scene, navMesh, pos, hasValidZ: true)` skips lazy navmesh resolution.** With `hasValidZ: true`, internal `State = Valid` (3). `GetNavMesh()` calls `ValidateZ(ValidAccordingToNavMesh)` (= 2), but the guard `if (State < minimumValidityState)` short-circuits (3 < 2 is false). `_navMesh` stays as `UIntPtr.Zero`. **Use `hasValidZ: false`** so the lazy resolution runs.
- **`Formation.BatchUnitPositions` requires `_orderPosition.GetNavMesh() != UIntPtr.Zero`.** Otherwise it returns false → `_globalPositions` never gets populated → `GetWorldPositionOfUnitOrDefault` returns invalid → agents fall back to formation-center clump.

## Deployment-boundary checks (TWO of them)

For a non-player-team formation positioned outside the team's official deployment zone (e.g., spawn-path positions), TWO independent boundary checks must be bypassed:

1. **`DefaultMissionDeploymentPlan.SupportsNavmesh(team)`** — gates the projection block in `Agent.TrySetFormationFrame` (`ProjectPositionToDeploymentBoundaries` clamps the position to the deployment box).
2. **`DefaultMissionDeploymentPlan.HasDeploymentBoundaries(team)`** — gates `IsPositionInsideDeploymentBoundaries` inside `Mission.IsFormationUnitPositionAvailableMT`. If this rejects the position, `TrySetFormationFrame` calls `SetFormationFrameDisabled` and the agent never teleports.

Both need Harmony prefixes returning `false` for the team you're moving outside the box. Patching only one is not enough.

## Teleporting vs walking

- **`Mission.IsTeleportingAgents = true`** must be set for `ForceUpdateCachedAndFormationValues` (and `MovementOrderMove` via formation movement) to teleport agents instantly instead of walking them. Vanilla `BattleDeploymentHandler.AutoDeployTeamUsingDeploymentPlan` sets this around its placement loop.
- **Vanilla placement pattern** (mirrors `BattleDeploymentHandler`):
  ```
  SetMovementOrder(Move) → SetFacingOrder → SetPositioning →
  ApplyActionOnEachUnit(agent => ForceUpdateCachedAndFormationValues(updateOnlyMovement: true, arrangementChangeAllowed: false)) →
  SetHasPendingUnitPositions(false) → SetMovementOrder(Stop)
  ```
  Bracket the whole thing with `IsTeleportingAgents = true`.

## Formation frame state lifecycle

- **`Agent.SetFormationFrameDisabled()` does not persist into battle.** `DeploymentMissionController.FinishDeployment` re-enables formation frame tracking on all agents at battle start. If you need agents to stay decoupled past deployment, re-pin `SetFormationFrameDisabled` after `OnDeploymentFinished` (and every tick, since the tactic system can flip things back).

## In-rank sorting (shielded → front)

- **`LineFormation.SwitchFrontUnitTypesToFrontRows`** is the only "sort" vanilla applies post-deployment. It is binary, not tier-based: shielded vs unshielded (`PreferShieldedUnitsOnFront(agent) => agent.HasShieldCached`), or bracer-vs-non under `IsUnderCavalryChargeFromFront`.
- **It runs every 0.5s via `Formation.Tick → Arrangement.OnTickOccasionally`** — but `Formation.Tick` only ticks AI-controlled formations after `Mission.AllowAiTicking = true`, which `DeploymentMissionController.FinishDeployment` flips on at battle start. During deployment the swap effectively never runs.
- **Each call is ONE pass of bubble sort** (Loop A: per-file front-promote by 1 rank; Loop B: cross-file back-to-front, with a hard `if (!flag) return;` early-exit on the first impossible swap). To fully converge during deployment, call it `RankCount + 2` times in a tight loop.
- **Hard gate: `if (Interval <= 0f) return;`** at the top of `SwitchFrontUnitTypesToFrontRows`. `Interval = InfantryInterval(UnitSpacing) * Arrangement.IntervalMultiplier = 0.38 * UnitSpacing * multiplier`. If `Formation.UnitSpacing == 0`, the swap silently does nothing. `ArrangementOrder.GetUnitSpacingOf(Column)` returns 2 (default case), but Square/ShieldWall return 0 — and a freshly created formation can have `UnitSpacing == 0` until `SetPositioning(_, _, unitSpacing)` is called with a non-zero value. **Force `unitSpacing >= 2`** when calling `SetPositioning` if you want the swap to do anything.
- **The swap only updates `_units2D` slot indices and agent `FormationFileIndex/RankIndex`.** It does NOT teleport agents. Re-run `ApplyActionOnEachUnit(a => a.ForceUpdateCachedAndFormationValues(...))` after the swap so agents move into their new slots.

## Misc

- **`ApplyActionOnEachUnit` iterates `Arrangement.GetAllUnits()` plus `_detachedUnits`** — includes unpositioned units (those in `_unpositionedUnits` when `BatchUnitPositions` failed). When measuring formation extent from agent positions, the projection of unpositioned agents will land at `OrderPosition` (since they haven't been moved), which can mask actual depth.
- **`SpawnPathData.GetSpawnPathFrameFacingTarget`** returns `Vec2` position, not `WorldPosition`. To build a `WorldPosition` from it for `SetPositioning`, use `hasValidZ: false` (per above) so the navmesh resolves lazily.
