# Bannerlord Battle Deployment System

Notes on how vanilla field battle deployment works, as a reference for the AmbushStance mod.

## Pipeline Overview

### 1. BattleDeploymentHandler

`TaleWorlds.MountAndBlade.Missions.Handlers.BattleDeploymentHandler(bool isPlayerAttacker)`

Added as a mission behavior in `BannerlordMissions.cs` and `SandBoxMissions.cs`. The `isPlayerAttacker` bool determines which side the player is on. The enemy side is spawned and AI-controlled immediately; the player side gets the interactive deployment screen.

Extends `DeploymentHandler` → `MissionLogic`. Sets `MissionMode.Deployment` on start and restores the previous mode on removal.

### 2. DeploymentMissionController

`BattleDeploymentMissionController` (extends `DeploymentMissionController`) drives the setup flow:

1. Both sides are spawned via `DefaultBattleMissionAgentSpawnLogic`.
2. Enemy side AI is initialized but paused.
3. If `BattleInitializationModel.CanPlayerSideDeployWithOrderOfBattle()` returns false, `FinishDeployment()` is called immediately — no player UI shown.
4. Otherwise, `MissionMode.Deployment` stays active until the player clicks "Done".

### 3. Deployment Plan

`DefaultMissionDeploymentPlan` owns a `DefaultTeamDeploymentPlan` per team.

For field battles, `ComputeDeploymentZoneFromFormations()` builds the boundary polygon:
- Reads scene entities tagged `attacker_infantry`, `attacker_ranged`, `defender_infantry`, etc.
- Finds the furthest formation position from center to determine zone depth and width.
  - Width = `max(2 * maxHalfWidth + 1.5 * troopCount, 100)` units
  - Depth = furthest formation Y offset + `DeployZoneForwardMargin` (10 units)
- Clips the result against `Mission.Boundaries` (the full walkable map polygon).
- Result: a list of `(string id, MBList<Vec2> points)` convex boundary polygons stored on `DefaultTeamDeploymentPlan._deploymentBoundaries`.

Key constants in `DefaultTeamDeploymentPlan`:
```
DeployZoneMinimumWidth      = 100f
DeployZoneForwardMargin     = 10f
DeployZoneExtraWidthPerTroop = 1.5f
```

### 4. Boundary Enforcement (OrderTroopPlacer)

`OrderTroopPlacer` has `_restrictOrdersToDeploymentBoundaries = true` during deployment.

In `UpdateFormationDrawing()`, before allowing placement:
```csharp
if (_restrictOrdersToDeploymentBoundaries && plan.HasDeploymentBoundaries(playerTeam))
    if (!plan.IsPositionInsideDeploymentBoundaries(playerTeam, position))
        return; // placement blocked
```

`ProjectPositionToDeploymentBoundaries` is also used by `OrderController` to snap dragged formations back inside the zone.

### 5. Visual Boundary Flags (MissionDeploymentBoundaryMarker)

`MissionDeploymentBoundaryMarker("swallowtail_banner", markerInterval: 2f)`

Registered as a `MissionView` in `SandBoxMissionViews.cs`. On `OnDeploymentPlanMade`, it walks the boundary polygon perimeter and places a `swallowtail_banner` prefab entity every 2 meters. Each entity gets the team's banner texture applied to its mesh.

These are the small flag indicators visible during deployment.

### 6. Deployment Gate (BattleInitializationModel)

`SandboxBattleInitializationModel.CanPlayerSideDeployWithOrderOfBattleAux()` returns true only if:
- Not a sally-out battle
- Player is the army leader (or settlement owner)
- `IMissionAgentSpawnLogic.GetNumberOfPlayerControllableTroops() >= 20`

If false, `BypassPlayerDeployment` is set and deployment finishes instantly.

---

## Relevant Types

| Type | Assembly | Role |
|---|---|---|
| `BattleDeploymentHandler` | TaleWorlds.MountAndBlade | Sets deployment mode, drives player side |
| `BattleDeploymentMissionController` | TaleWorlds.MountAndBlade | Spawns troops, calls FinishDeployment |
| `DefaultMissionDeploymentPlan` | TaleWorlds.MountAndBlade | Owns per-team boundary polygons |
| `DefaultTeamDeploymentPlan` | TaleWorlds.MountAndBlade | Computes & stores one team's boundary |
| `OrderTroopPlacer` | TaleWorlds.MountAndBlade.View | Enforces boundary during player drag |
| `MissionDeploymentBoundaryMarker` | TaleWorlds.MountAndBlade.View | Spawns visual flag entities on boundary |
| `SandboxBattleInitializationModel` | Sandbox | Gate: decides if player gets deployment UI |

---

## Hooks Needed for Ambush Deployment

| Goal | Target | Patch Type |
|---|---|---|
| Player is always the ambusher (attacker side) | `BattleDeploymentHandler` constructor call site in `BannerlordMissions` | Harmony prefix, force `isPlayerAttacker = true` |
| Player always gets the deployment screen | `SandboxBattleInitializationModel.CanPlayerSideDeployWithOrderOfBattleAux` | Harmony prefix, return `true` |
| Block player deployment in center square | `DefaultTeamDeploymentPlan.IsPositionInsideDeploymentBoundaries` | Harmony postfix, also return `false` when inside center exclusion rect |
| Enemy spawns in center (no deployment) | No patch needed — enemy is always auto-deployed to its spawn frame entities in the scene |
