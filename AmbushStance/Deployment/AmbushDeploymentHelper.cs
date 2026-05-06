using System;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AmbushStance.Deployment;

public static class AmbushDeploymentHelper
{
    public const float CenterExclusionHalfSize = 30f;
    private const float MarchFormationGap = 5f;

    // Front of column = index 0 (closest to player). Highest index goes at the rear.
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
        spawnPath.ClampPathOffset(ref startOffset);
        var endOffset = 1e9f;
        spawnPath.ClampPathOffset(ref endOffset);
        var pathOffset = startOffset + (endOffset - startOffset) * sliderOffset;

        var deploymentPlan = mission.DeploymentPlan;
        deploymentPlan.ClearDeploymentPlan(enemyTeam);
        deploymentPlan.MakeDeploymentPlan(enemyTeam, pathOffset);

        // Skip AutoDeployTeamUsingDeploymentPlan — it stacks formations side-by-side, but we
        // want them stacked rear-to-front in a marching column.
        ApplyMarchFormation(mission);
    }

    public static void ApplyMarchFormation(Mission mission)
    {
        var enemyTeam = mission.PlayerEnemyTeam;
        if (enemyTeam == null || !mission.IsBattleSpawnPathSelectorInitialized)
            return;

        var spawnPath = mission.GetInitialSpawnPathData(enemyTeam.Side);
        var currentOffset = mission.DeploymentPlan.GetSpawnPathOffset(enemyTeam);
        var endOffset = 1e9f;
        spawnPath.ClampPathOffset(ref endOffset);

        // Stack from rear toward player: highest march index (Ranged) placed first at currentOffset,
        // Bodyguard placed last at the head of the column closest to the player.
        var formations = enemyTeam
            .FormationsIncludingEmpty.Where(f => f.CountOfUnits > 0)
            .OrderByDescending(f =>
            {
                var idx = Array.IndexOf(MarchOrder, f.FormationIndex);
                return idx < 0 ? int.MinValue : idx;
            })
            .ToList();

        var wasTeleporting = mission.IsTeleportingAgents;
        mission.IsTeleportingAgents = true;

        var accumulated = 0f;
        foreach (var formation in formations)
        {
            var (width, depth, files, distancePlusDiameter, intervalPlusDiameter, diameter) =
                ComputeColumnLayout(formation);

            // GetSpawnPathFrameFacingTarget returns the formation center; rear sits at
            // currentOffset + accumulated, so center = rear + depth/2.
            var centerOffset = currentOffset + accumulated + depth * 0.5f;
            spawnPath.ClampPathOffset(ref centerOffset);
            spawnPath.GetSpawnPathFrameFacingTarget(
                centerOffset,
                endOffset,
                useTangentDirection: true,
                out var center,
                out var dir
            );

            var worldPos = new WorldPosition(
                mission.Scene,
                UIntPtr.Zero,
                new Vec3(center, 0f),
                hasValidZ: false
            );

            // Configure formation state (arrangement, width, facing, anchor, hold) so when battle
            // begins the formation system already knows its layout — AI re-takes control with the
            // correct OrderPosition/Direction/ArrangementOrder.
            formation.SetArrangementOrder(ArrangementOrder.ArrangementOrderColumn);
            formation.SetFormOrder(FormOrder.FormOrderCustom(width));
            formation.SetFacingOrder(FacingOrder.FacingOrderLookAtDirection(dir));
            formation.SetPositioning(worldPos, dir, formation.ArrangementOrder.GetUnitSpacing());
            formation.SetMovementOrder(MovementOrder.MovementOrderStop);

            // Place agents directly. We can't use ForceUpdateCachedAndFormationValues here:
            //   - ColumnFormation.GetWorldPositionOfUnit returns null, so GetOrderPositionOfUnit
            //     falls through to _movementOrder.CreateNewOrderWorldPositionMT, which is the
            //     single formation-center point — every agent ends up at the same spot.
            //   - In Deployment mode, TrySetFormationFrame projects positions onto the team's
            //     deployment boundaries, clamping out-of-bounds path positions to one boundary
            //     intersection.
            // SetFormationFrameDisabled prevents Agent.Tick from re-projecting on subsequent ticks.
            var perp = new Vec2(-dir.Y, dir.X);
            var rearCenter = center - dir * (depth - diameter) * 0.5f;
            var agentIdx = 0;
            formation.ApplyActionOnEachUnit(agent =>
            {
                var file = agentIdx % files;
                var rank = agentIdx / files;
                var lateral = (file - (files - 1) / 2f) * intervalPlusDiameter;
                var forward = rank * distancePlusDiameter;
                var slot = rearCenter + perp * lateral + dir * forward;
                var z = 0f;
                mission.Scene.GetHeightAtPoint(
                    slot,
                    BodyFlags.CommonCollisionExcludeFlagsForCombat,
                    ref z
                );
                agent.TeleportToPosition(new Vec3(slot.X, slot.Y, z));
                agent.SetFormationFrameDisabled();
                agentIdx++;
            });
            formation.SetHasPendingUnitPositions(false);

            accumulated += depth + MarchFormationGap;
        }

        mission.IsTeleportingAgents = wasTeleporting;
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

    private static float CalculateColumnWidth(Formation formation)
    {
        var count = formation.CountOfUnits;
        if (formation.PhysicalClass.IsMounted())
            return count < 10 ? 6f : 9f + 4f * (count / 20);
        else
            return count < 10 ? 4f : 6f + 2f * (count / 30);
    }

    private static (
        float width,
        float depth,
        int files,
        float distancePlusDiameter,
        float intervalPlusDiameter,
        float diameter
    ) ComputeColumnLayout(Formation formation)
    {
        var width = CalculateColumnWidth(formation);
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

        var intervalPlusDiameter = interval + diameter;
        var distancePlusDiameter = distance + diameter;
        var files = Math.Max(1, (int)(width / intervalPlusDiameter));
        var ranks = (formation.CountOfUnits + files - 1) / files;
        var depth = Math.Max(0f, ranks - 1) * distancePlusDiameter + diameter;
        return (width, depth, files, distancePlusDiameter, intervalPlusDiameter, diameter);
    }
}
