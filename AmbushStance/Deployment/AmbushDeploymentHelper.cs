using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Missions.Handlers;

namespace AmbushStance.Deployment;

public static class AmbushDeploymentHelper
{
    public const float CenterExclusionHalfSize = 30f;
    private const float MarchFormationGap = 5f;

    // Front of column = index 0 (closest to player)
    private static readonly FormationClass[] MarchOrder =
    [
        FormationClass.Bodyguard,
        FormationClass.HeavyCavalry,
        FormationClass.Cavalry,
        FormationClass.HorseArcher,
        FormationClass.HeavyInfantry,
        FormationClass.Infantry,
        FormationClass.LightCavalry,
        FormationClass.Ranged,
    ];

    public static void GetEnemySpawnCenter(
        Mission mission,
        out Vec2 spawnPosition,
        out Vec2 spawnDirection
    )
    {
        var enemyTeam = mission.PlayerEnemyTeam;
        if (enemyTeam == null)
        {
            spawnPosition = Vec2.Zero;
            spawnDirection = Vec2.Zero;
            return;
        }

        // For spawn-path scenes use the path data directly — GetFormationSpawnFrame calls
        // CreateNewDeploymentWorldPosition → GetGroundZ which crashes for large parties when
        // the plan has been made but not yet applied (no valid navmesh face in WorldPosition).
        if (mission.IsBattleSpawnPathSelectorInitialized)
        {
            var spawnPath = mission.GetInitialSpawnPathData(enemyTeam.Side);
            var currentOffset = mission.DeploymentPlan.GetSpawnPathOffset(enemyTeam);
            var endOffset = 1e9f;
            spawnPath.ClampPathOffset(ref endOffset);
            spawnPath.GetSpawnPathFrameFacingTarget(
                currentOffset,
                endOffset,
                useTangentDirection: true,
                out spawnPosition,
                out spawnDirection
            );
            return;
        }

        // Entity-based spawn scenes (siege etc.) — safe to call after plan is applied.
        mission.GetFormationSpawnFrame(
            enemyTeam,
            FormationClass.Infantry,
            isReinforcement: false,
            out var formationSpawnPosition,
            out var formationSpawnDirection
        );
        spawnPosition = formationSpawnPosition.AsVec2;
        spawnDirection = formationSpawnDirection.Normalized();
    }

    public static void RedeployEnemyWithOffset(Mission mission, float sliderOffset)
    {
        var enemyTeam = mission.PlayerEnemyTeam;
        if (enemyTeam == null)
            return;

        // spawnPathOffset is relative to PivotOffset, not a 0-1 fraction.
        // Probe the valid range by saturating ClampPathOffset at both extremes.
        var spawnPath = mission.GetInitialSpawnPathData(enemyTeam.Side);
        var startOffset = -1e9f;
        spawnPath.ClampPathOffset(ref startOffset); // → -PivotOffset (path distance = 0)
        var endOffset = 1e9f;
        spawnPath.ClampPathOffset(ref endOffset); // → PathLength - PivotOffset (path distance = PathLength)
        var pathOffset = startOffset + (endOffset - startOffset) * sliderOffset;

        var deploymentPlan = mission.DeploymentPlan;
        deploymentPlan.ClearDeploymentPlan(enemyTeam);
        deploymentPlan.MakeDeploymentPlan(enemyTeam, pathOffset);

        mission
            .GetMissionBehavior<BattleDeploymentHandler>()
            ?.AutoDeployTeamUsingDeploymentPlan(enemyTeam);

        ApplyMarchFormation(mission);

        // AutoDeployTeamUsingDeploymentPlan issues AIControlOff for non-siege battles.
        // Re-enable it so enemy AI behaves normally when the battle starts.
        var orderController = enemyTeam.MasterOrderController;
        orderController.SelectAllFormations();
        orderController.SetOrder(OrderType.AIControlOn);
        orderController.ClearSelectedFormations();
    }

    public static void ApplyMarchFormation(Mission mission)
    {
        var enemyTeam = mission.PlayerEnemyTeam;
        if (enemyTeam == null)
            return;

        if (!mission.IsBattleSpawnPathSelectorInitialized)
            return;

        var spawnPath = mission.GetInitialSpawnPathData(enemyTeam.Side);
        var currentOffset = mission.DeploymentPlan.GetSpawnPathOffset(enemyTeam);
        var endOffset = 1e9f;
        spawnPath.ClampPathOffset(ref endOffset);

        // Set column arrangement via the order controller so SetFormationUpdateEnabledAfterSetOrder
        // batches the change — same pattern AutoDeployTeamUsingDeploymentPlan uses for ArrangementLine.
        var orderController = enemyTeam.MasterOrderController;
        orderController.SelectAllFormations();
        orderController.SetFormationUpdateEnabledAfterSetOrder(false);
        orderController.SetOrder(OrderType.ArrangementColumn);
        orderController.SetFormationUpdateEnabledAfterSetOrder(true);
        orderController.ClearSelectedFormations();

        // Sort front-to-back: Bodyguard (idx 0) first, Ranged (idx 7) last.
        var formations = enemyTeam
            .FormationsIncludingEmpty.Where(f => f.CountOfUnits > 0)
            .OrderBy(f =>
            {
                var idx = Array.IndexOf(MarchOrder, f.FormationIndex);
                return idx < 0 ? int.MaxValue : idx;
            })
            .ToList();

        // SetMovementOrder(Move) + IsTeleportingAgents teleports agents into their column slots.
        // No Stop at the end — AI overrides the move order when battle starts.
        var wasTeleporting = mission.IsTeleportingAgents;
        mission.IsTeleportingAgents = true;

        // Walk backward along the path (decreasing offset = away from player).
        // GetSpawnPathFrameFacingTarget handles all path curve math — no manual polyline walking needed.
        ClearDebugMarkers();
        spawnPath.GetSpawnPathFrameFacingTarget(
            currentOffset,
            endOffset,
            useTangentDirection: true,
            out var spawnPos,
            out var spawnDir
        );
        PlaceDebugMarker(mission, new Vec3(spawnPos, 0f), new Vec3(spawnDir, 0f));

        var accumulated = 0f;
        foreach (var formation in formations)
        {
            var width = CalculateColumnWidth(formation);
            var depth = CalculateColumnDepth(formation);
            // if (formation.LogicalClass == FormationClass.Cavalry)
            //     depth = 50f;
            var sampleOffset = currentOffset - accumulated;
            spawnPath.ClampPathOffset(ref sampleOffset);

            spawnPath.GetSpawnPathFrameFacingTarget(
                sampleOffset,
                endOffset,
                useTangentDirection: true,
                out var pos,
                out var dir
            );

            PlaceDebugMarker(mission, new Vec3(pos, 0f), new Vec3(dir, 0f));

            var worldPos = new WorldPosition(
                mission.Scene,
                UIntPtr.Zero,
                new Vec3(pos, 0f),
                hasValidZ: false
            );
            formation.SetFormOrder(FormOrder.FormOrderCustom(width));
            formation.SetMovementOrder(MovementOrder.MovementOrderMove(worldPos));
            formation.SetFacingOrder(FacingOrder.FacingOrderLookAtDirection(dir));
            formation.ApplyActionOnEachUnitViaBackupList(a =>
                a.ForceUpdateCachedAndFormationValues(false, true)
            );
            formation.SetHasPendingUnitPositions(false);

            // Measure actual depth from agent positions after teleport.
            // Theoretical depth kept for comparison: depth
            var minProj = float.MaxValue;
            var maxProj = float.MinValue;
            formation.ApplyActionOnEachUnit(agent =>
            {
                var proj = Vec2.DotProduct(agent.Position.AsVec2 - pos, dir);
                if (proj < minProj) minProj = proj;
                if (proj > maxProj) maxProj = proj;
            });
            var realDepth = (minProj < maxProj) ? (maxProj - minProj) : depth;

            accumulated += realDepth + MarchFormationGap;
        }

        mission.IsTeleportingAgents = wasTeleporting;
    }

    private static float CalculateColumnWidth(Formation formation)
    {
        var count = formation.CountOfUnits;
        if (formation.PhysicalClass.IsMounted())
            return count < 10 ? 6f : 9f + 4f * (count / 20);
        else
            return count < 10 ? 4f : 6f + 2f * (count / 25);
    }

    private static float CalculateColumnDepth(Formation formation)
    {
        var isMounted = formation.PhysicalClass.IsMounted();
        var diameter = Formation.GetDefaultUnitDiameter(isMounted);
        var spacing = ArrangementOrder.GetUnitSpacingOf(
            ArrangementOrder.ArrangementOrderEnum.Column
        );
        var interval = isMounted
            ? Formation.CavalryInterval(spacing)
            : Formation.InfantryInterval(spacing);
        var distance = isMounted
            ? Formation.CavalryDistance(spacing)
            : Formation.InfantryDistance(spacing);

        // var width = CalculateColumnWidth(formation);
        var width = formation.Width;
        var files = Math.Max(1, (int)(width / (interval + diameter)));
        // var ranks = (formation.CountOfUnits + files - 1) / files;
        var ranks = formation.Arrangement.RankCount;
        return Math.Max(0f, ranks - 1) * (distance + diameter) + diameter;
    }

    public static bool IsInsideCenterExclusion(Vec2 position, Vec2 center, Vec2 dir)
    {
        var perp = new Vec2(-dir.Y, dir.X);
        var offset = position - center;

        var localX = Vec2.DotProduct(offset, perp);
        var localY = Vec2.DotProduct(offset, dir);

        return MathF.Abs(localX) <= CenterExclusionHalfSize
            && MathF.Abs(localY) <= CenterExclusionHalfSize;
    }

    private static readonly List<GameEntity> _debugMarkers = [];

    private static void PlaceDebugMarker(Mission mission, Vec3 pos, Vec3 dir)
    {
        var frame = MatrixFrame.Identity;
        frame.origin = pos;
        if (
            !mission.Scene.GetHeightAtPoint(
                frame.origin.AsVec2,
                BodyFlags.CommonCollisionExcludeFlagsForCombat,
                ref frame.origin.z
            )
        )
            frame.origin.z = 0f;
        frame.origin.z += 1f;

        var direction = dir.NormalizedCopy();
        var normal = mission.Scene.GetNormalAt(frame.origin.AsVec2);
        frame.rotation.u = normal;
        frame.rotation.s = new Vec3(direction.x, direction.y, 0f);
        frame.rotation.f = Vec3.CrossProduct(frame.rotation.s, frame.rotation.u);
        frame.rotation.Orthonormalize();

        var entity = mission.GetMissionBehavior<AmbushCenterExclusionMarker>().SpawnDebugMarker();
        entity.SetFrame(ref frame);
        _debugMarkers.Add(entity);
    }

    private static void ClearDebugMarkers()
    {
        foreach (var e in _debugMarkers)
            e.Remove(103);
        _debugMarkers.Clear();
    }
}
