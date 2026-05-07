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
        FormationClass.General,
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

    private const string LogFile = "ambush_debug.log";

    private static void DbgClear()
    {
        try
        {
            System.IO.File.WriteAllText(LogFile, "");
        }
        catch { }
    }

    private static void Dbg(string m)
    {
        try
        {
            System.IO.File.AppendAllText(LogFile, m + System.Environment.NewLine);
        }
        catch { }
    }

    public static void ApplyMarchFormation(Mission mission)
    {
        DbgClear();
        Dbg("=== ApplyMarchFormation ===");

        var enemyTeam = mission.PlayerEnemyTeam;
        if (enemyTeam == null || !mission.IsBattleSpawnPathSelectorInitialized)
            return;

        var spawnPath = mission.GetInitialSpawnPathData(enemyTeam.Side);
        var currentOffset = mission.DeploymentPlan.GetSpawnPathOffset(enemyTeam);
        var endOffset = 1e9f;
        spawnPath.ClampPathOffset(ref endOffset);

        // Iterate front-to-rear: lowest march index (Bodyguard, then cavalry classes...) goes
        // first and is centered exactly at the slider's currentOffset; subsequent formations
        // stack behind it (toward lower path offsets).
        var formations = enemyTeam
            .FormationsIncludingSpecialAndEmpty.Where(f => f.CountOfUnits > 0)
            .OrderBy(f =>
            {
                var idx = Array.IndexOf(MarchOrder, f.FormationIndex);
                return idx < 0 ? int.MaxValue : idx;
            })
            .ToList();

        var wasTeleporting = mission.IsTeleportingAgents;
        mission.IsTeleportingAgents = true;

        // Path offset where the previous formation's rear edge sits. The next formation's
        // front edge should land MarchFormationGap behind this. Higher path offset = closer
        // to player (forward); lower offset = behind.
        var previousRearOffset = 0f;
        var isFirst = true;

        foreach (var formation in formations)
        {
            var width = CalculateColumnWidth(formation);

            // First pass: place at any sensible offset just to get agents teleported into the
            // formation arrangement so we can measure its actual extent.
            var initialOrderOffset = isFirst
                ? currentOffset
                : previousRearOffset - MarchFormationGap;
            PlaceFormationAt(mission, formation, spawnPath, initialOrderOffset, endOffset, width);

            // maxProj = how far ahead of OrderPosition the front-most agent is (≈ 0 for
            // TransposedLineFormation, since rank 0 sits AT OrderPosition).
            // minProj = how far behind OrderPosition the rear-most agent is (≈ -depth).
            MeasureFormationExtent(formation, out var maxProj, out var minProj, out var diameter);
            Dbg(
                $"[Extent] {formation.FormationIndex} maxProj={maxProj:F2} minProj={minProj:F2} diam={diameter:F2}"
            );

            // Compute corrected OrderPosition (path offset).
            float correctedOrderOffset;
            if (isFirst)
            {
                // Geometric center at currentOffset → OrderPos shifted forward by half the
                // formation's asymmetric extent: OrderPos = currentOffset - (max+min)/2.
                correctedOrderOffset = currentOffset - (maxProj + minProj) * 0.5f;
                isFirst = false;
            }
            else
            {
                // Front edge at (previousRear - gap) → OrderPos = (previousRear - gap) - frontExtent.
                // frontExtent = maxProj + diameter/2.
                correctedOrderOffset =
                    previousRearOffset - MarchFormationGap - (maxProj + diameter * 0.5f);
            }
            PlaceFormationAt(mission, formation, spawnPath, correctedOrderOffset, endOffset, width);

            // Re-measure (small drift possible after re-placement) and update previousRearOffset.
            MeasureFormationExtent(formation, out maxProj, out minProj, out diameter);
            previousRearOffset = correctedOrderOffset + (minProj - diameter * 0.5f);
            Dbg(
                $"[Final] {formation.FormationIndex} orderOff={correctedOrderOffset:F2} frontEdge={correctedOrderOffset + maxProj + diameter * 0.5f:F2} rearEdge={previousRearOffset:F2}"
            );
        }

        mission.IsTeleportingAgents = wasTeleporting;
    }

    private static void PlaceFormationAt(
        Mission mission,
        Formation formation,
        SpawnPathData spawnPath,
        float centerOffset,
        float endOffset,
        float width
    )
    {
        spawnPath.ClampPathOffset(ref centerOffset);
        spawnPath.GetSpawnPathFrameFacingTarget(
            centerOffset,
            endOffset,
            useTangentDirection: true,
            out var center,
            out var dir
        );

        // hasValidZ:false so GetNavMesh() lazily resolves the navmesh from (X,Y) — required
        // for Formation.BatchUnitPositions to populate _globalPositions.
        var worldPos = new WorldPosition(
            mission.Scene,
            UIntPtr.Zero,
            new Vec3(center, 0f),
            hasValidZ: false
        );

        formation.SetArrangementOrder(ArrangementOrder.ArrangementOrderColumn);
        formation.SetFormOrder(FormOrder.FormOrderCustom(width));
        formation.SetMovementOrder(MovementOrder.MovementOrderMove(worldPos));
        formation.SetFacingOrder(FacingOrder.FacingOrderLookAtDirection(dir));
        formation.SetPositioning(worldPos, dir, formation.ArrangementOrder.GetUnitSpacing());

        // Run the strongest-front swap. Each call is a single bubble-sort pass,
        // so iterate enough times to fully converge
        var passes = formation.Arrangement.RankCount + 2;
        for (var i = 0; i < passes; i++)
            formation.Arrangement.OnTickOccasionally();

        formation.ApplyActionOnEachUnit(agent =>
            agent.ForceUpdateCachedAndFormationValues(
                updateOnlyMovement: true,
                arrangementChangeAllowed: false
            )
        );
        formation.SetHasPendingUnitPositions(false);
        formation.SetMovementOrder(MovementOrder.MovementOrderStop);
    }

    // Measure the front-to-back extent of agents projected onto the formation's facing direction.
    // maxProj: how far AHEAD of OrderPosition the front-most agent center is.
    // minProj: how far BEHIND OrderPosition the rear-most agent center is (negative value).
    // For TransposedLineFormation: maxProj ≈ 0 (front rank sits at OrderPosition), minProj ≈ -depth.
    private static void MeasureFormationExtent(
        Formation formation,
        out float maxProj,
        out float minProj,
        out float diameter
    )
    {
        var dir = formation.Direction;
        var origin = formation.OrderPosition;
        var lo = float.MaxValue;
        var hi = float.MinValue;
        formation.ApplyActionOnEachUnit(agent =>
        {
            var p = Vec2.DotProduct(agent.Position.AsVec2 - origin, dir);
            if (p < lo)
                lo = p;
            if (p > hi)
                hi = p;
        });
        diameter = Formation.GetDefaultUnitDiameter(formation.PhysicalClass.IsMounted());
        if (lo == float.MaxValue)
        {
            maxProj = 0f;
            minProj = 0f;
            return;
        }
        maxProj = hi;
        minProj = lo;
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
}
