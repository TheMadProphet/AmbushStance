using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.ModuleManager;
using TaleWorlds.MountAndBlade;
using Path = System.IO.Path;

namespace AmbushStance.Behaviors;

public class AmbushMarchMissionLogic : MissionLogic
{
    private const float RetickInterval = 0.25f;
    private const float ArrivalThresholdSq = 16f; // 4 m

    // Fraction of vanilla MaxSpeedMultiplier (and MountSpeed) used while marching. 0.3 ≈
    // brisk walk for foot, slow trot for cavalry. Tune lower for slower march.
    private const float WalkMaxSpeedMultiplier = 0.3f;
    private const float WalkMountSpeedMultiplier = 0.4f;
    private const float SpacingBuffer = 5f;

    // Private push-to-native method on Agent. We override AgentDrivenProperties values
    // each retick and need to push them to the engine WITHOUT going through
    // UpdateAgentProperties (which recalculates and resets our overrides).
    private static readonly MethodInfo PushDrivenPropertiesMethod = typeof(Agent).GetMethod(
        "UpdateDrivenProperties",
        BindingFlags.NonPublic | BindingFlags.Instance,
        binder: null,
        types: new[] { typeof(float[]) },
        modifiers: null
    );

    // AgentDrivenProperties.Values is internal; reach it via reflection so we can pass the
    // raw float[] to the native push above.
    private static readonly PropertyInfo DrivenPropertiesValuesProperty = typeof(
        AgentDrivenProperties
    ).GetProperty("Values", BindingFlags.NonPublic | BindingFlags.Instance);

    private MatrixFrame[] _waypoints;
    private readonly Dictionary<Formation, int> _nextWaypoint = new();
    private List<Formation> _columnOrder = new();
    private bool _marching;
    private float _retickTimer;

    public override void OnDeploymentFinished()
    {
        ClearLog();
        Log("=== OnDeploymentFinished ===");

        var enemyTeam = Mission.PlayerEnemyTeam;
        if (enemyTeam == null)
        {
            Log("BAIL: no PlayerEnemyTeam");
            return;
        }
        if (!Mission.IsBattleSpawnPathSelectorInitialized)
        {
            Log("BAIL: spawn path not initialised");
            return;
        }

        var path = Mission.GetInitialSpawnPath();
        if (path == null || path.NumberOfPoints < 2)
        {
            Log($"BAIL: path null or too short (n={path?.NumberOfPoints ?? 0})");
            return;
        }

        var rawPoints = new MatrixFrame[path.NumberOfPoints];
        path.GetPoints(rawPoints);

        // FormationsIncludingSpecialAndEmpty (vs FormationsIncludingEmpty) — the latter omits
        // General and Bodyguard, so they'd be left under vanilla AI control and charge.
        var formations = enemyTeam
            .FormationsIncludingSpecialAndEmpty.Where(f => f.CountOfUnits > 0)
            .ToList();
        if (formations.Count == 0)
        {
            Log("BAIL: no populated enemy formations");
            return;
        }

        // March-forward direction comes from a deployed formation's facing — set during
        // ApplyMarchFormation from the spawn-path tangent toward the path's end offset.
        var marchDir = formations[0].Direction;
        if (marchDir.LengthSquared < 0.01f)
        {
            Log("BAIL: marchDir near zero");
            return;
        }
        marchDir = marchDir.Normalized();
        var refOrigin = formations[0].CachedAveragePosition;

        _waypoints = rawPoints
            .OrderBy(p => Vec2.DotProduct(p.origin.AsVec2 - refOrigin, marchDir))
            .ToArray();
        for (var i = 0; i < _waypoints.Length; i++)
        {
            var proj = Vec2.DotProduct(_waypoints[i].origin.AsVec2 - refOrigin, marchDir);
            Log(
                $"  wp[{i}] proj={proj:F2} pos=({_waypoints[i].origin.X:F1},{_waypoints[i].origin.Y:F1})"
            );
        }

        // Front-to-rear column order (highest projection first). Used by anti-overlap halt
        // logic to identify each formation's immediate leader.
        _columnOrder = formations
            .OrderByDescending(f => Vec2.DotProduct(f.CachedAveragePosition - refOrigin, marchDir))
            .ToList();
        Log("Column order (front to rear):");
        for (var i = 0; i < _columnOrder.Count; i++)
        {
            var f = _columnOrder[i];
            var proj = Vec2.DotProduct(f.CachedAveragePosition - refOrigin, marchDir);
            Log($"  col[{i}] {f.FormationIndex} proj={proj:F2}");
        }

        foreach (var formation in formations)
        {
            var formProj = Vec2.DotProduct(formation.CachedAveragePosition - refOrigin, marchDir);
            var startIdx = _waypoints.Length - 1;
            for (var i = 0; i < _waypoints.Length; i++)
            {
                var wpProj = Vec2.DotProduct(_waypoints[i].origin.AsVec2 - refOrigin, marchDir);
                if (wpProj > formProj)
                {
                    startIdx = i;
                    break;
                }
            }
            _nextWaypoint[formation] = startIdx;

            formation.SetControlledByAI(false);
            formation.SetFiringOrder(FiringOrder.FiringOrderHoldYourFire);
            SuppressEnemyAwareness(formation);
            IssueMarchOrder(formation, _waypoints[startIdx]);

            Log(
                $"  [{formation.FormationIndex}] units={formation.CountOfUnits} formProj={formProj:F2} startWp={startIdx}"
            );
        }

        _marching = _nextWaypoint.Count > 0;
        Log($"Marching={_marching} formations={_nextWaypoint.Count}");
    }

    public override void OnMissionTick(float dt)
    {
        if (!_marching)
            return;
        _retickTimer += dt;
        if (_retickTimer < RetickInterval)
            return;
        _retickTimer = 0f;

        foreach (var formation in _nextWaypoint.Keys.ToList())
        {
            if (formation.CountOfUnits == 0)
                continue;

            if (formation.IsAIControlled)
                formation.SetControlledByAI(false);
            formation.SetFiringOrder(FiringOrder.FiringOrderHoldYourFire);
            SuppressEnemyAwareness(formation);
            ApplyWalkSpeedClamp(formation);

            var idx = _nextWaypoint[formation];
            var target2d = _waypoints[idx].origin.AsVec2;

            var arrived = false;
            formation.ApplyActionOnEachUnit(agent =>
            {
                if (
                    !arrived
                    && agent.Position.AsVec2.DistanceSquared(target2d) < ArrivalThresholdSq
                )
                    arrived = true;
            });

            if (arrived)
            {
                idx++;
                if (idx >= _waypoints.Length)
                {
                    Log($"[{formation.FormationIndex}] reached final waypoint — handover");
                    Handover();
                    return;
                }
                _nextWaypoint[formation] = idx;
                Log($"[{formation.FormationIndex}] advancing to wp {idx}/{_waypoints.Length - 1}");
            }

            if (IsTooCloseToLead(formation, out var haltMsg))
            {
                formation.SetMovementOrder(MovementOrder.MovementOrderStop);
                Log(haltMsg);
                continue;
            }

            IssueMarchOrder(formation, _waypoints[idx]);
        }
    }

    public override void OnAgentHit(
        Agent affectedAgent,
        Agent affectorAgent,
        in MissionWeapon affectorWeapon,
        in Blow blow,
        in AttackCollisionData attackCollisionData
    )
    {
        if (!_marching)
            return;
        if (affectedAgent?.Team != Mission.PlayerEnemyTeam)
            return;
        Log("First hit on enemy team — handover");
        Handover();
    }

    private void IssueMarchOrder(Formation formation, MatrixFrame waypoint)
    {
        var dir2d = waypoint.origin.AsVec2 - formation.CachedAveragePosition;
        var faceDir = dir2d.LengthSquared > 0.01f ? dir2d.Normalized() : formation.Direction;

        var wp = new WorldPosition(Mission.Scene, UIntPtr.Zero, waypoint.origin, hasValidZ: false);
        formation.SetMovementOrder(MovementOrder.MovementOrderMove(wp));
        formation.SetFacingOrder(FacingOrder.FacingOrderLookAtDirection(faceDir));
    }

    // Keep agents oblivious while marching: Patrolling watch state stops them looking at /
    // turning toward enemies. Re-applied each retick because agent AI flips it back to
    // Alarmed whenever an enemy comes into perception range.
    private static void SuppressEnemyAwareness(Formation formation)
    {
        formation.ApplyActionOnEachUnit(agent =>
        {
            if (agent.CurrentWatchState != Agent.WatchState.Patrolling)
                agent.SetWatchState(Agent.WatchState.Patrolling);
        });
    }

    // Slow agents to a walking pace by overriding their AgentDrivenProperties directly.
    // Column arrangement has no run/walk restriction and SetMaximumSpeedLimit alone wasn't
    // enough — the engine clamps based on MaxSpeedMultiplier (and MountSpeed for cav) in
    // the native locomotion code. Re-applied every retick because vanilla's stat-calc model
    // (SandboxAgentStatCalculateModel.UpdateAgentStats) recomputes these on state changes.
    private static void ApplyWalkSpeedClamp(Formation formation)
    {
        formation.ApplyActionOnEachUnit(agent =>
        {
            ClampAgentToWalk(agent);
            if (agent.MountAgent != null)
                ClampAgentToWalk(agent.MountAgent);
        });
    }

    private static void ClampAgentToWalk(Agent agent)
    {
        var props = agent.AgentDrivenProperties;
        if (props == null)
            return;

        // MaxSpeedMultiplier scales locomotion top speed for both foot agents and mount
        // agents; MountSpeed (lives on the MOUNT's properties, not the rider's) is the
        // primary lever for mounted locomotion. Apply both for cavalry.
        props.MaxSpeedMultiplier = 0.25f;
        if (agent.IsMount)
        {
            props.MountSpeed = agent.RiderAgent.Monster.WalkingSpeedLimit;
        }

        // Push the modified _statValues array to the native engine. UpdateAgentProperties
        // would recalculate first and wipe our overrides; this private overload skips the
        // recalc and just syncs values to native.
        var values = (float[])DrivenPropertiesValuesProperty?.GetValue(props);
        if (values != null)
            PushDrivenPropertiesMethod?.Invoke(agent, new object[] { values });
    }

    // Halt this formation if the formation directly ahead in the column is too close.
    // Centroid distance vs (depthA+depthB)/2 + buffer is a stable estimator that doesn't
    // depend on which arrangement either formation happens to be in this tick.
    private bool IsTooCloseToLead(Formation formation, out string haltMsg)
    {
        haltMsg = null;
        var leadIdx = _columnOrder.IndexOf(formation) - 1;
        if (leadIdx < 0)
            return false;
        var lead = _columnOrder[leadIdx];
        if (lead.CountOfUnits == 0)
            return false;
        var gap = formation.CachedAveragePosition.Distance(lead.CachedAveragePosition);
        var minGap = (formation.Depth + lead.Depth) * 0.5f + SpacingBuffer;
        if (gap >= minGap)
            return false;
        haltMsg =
            $"[{formation.FormationIndex}] HALT gap={gap:F2} minGap={minGap:F2} leader=[{lead.FormationIndex}]";
        return true;
    }

    private void Handover()
    {
        if (!_marching)
            return;
        _marching = false;

        var enemyTeam = Mission.PlayerEnemyTeam;
        if (enemyTeam != null)
        {
            foreach (var formation in enemyTeam.FormationsIncludingSpecialAndEmpty)
            {
                if (formation.CountOfUnits == 0)
                    continue;
                formation.SetFiringOrder(FiringOrder.FiringOrderFireAtWill);
                formation.ApplyActionOnEachUnit(agent =>
                {
                    agent.SetWatchState(Agent.WatchState.Alarmed);
                    // Lift the walk-pace overrides. UpdateAgentStats triggers a clean
                    // recalc through the StatCalculateModel and pushes fresh values to
                    // native — restoring full sprint speed.
                    agent.UpdateAgentStats();
                    agent.MountAgent?.UpdateAgentStats();
                });
                formation.SetControlledByAI(true);
            }
            enemyTeam.ResetTactic();
        }
        _nextWaypoint.Clear();
        _columnOrder.Clear();
        Log("Handover complete");
    }

    private static readonly string LogPath = Path.Combine(
        ModuleHelper.GetModuleFullPath("AmbushStance"),
        "ambush_march.log"
    );

    private static void ClearLog()
    {
        try
        {
            File.WriteAllText(LogPath, "");
        }
        catch { }
    }

    private static void Log(string m)
    {
        try
        {
            File.AppendAllText(LogPath, m + System.Environment.NewLine);
        }
        catch { }
    }
}
