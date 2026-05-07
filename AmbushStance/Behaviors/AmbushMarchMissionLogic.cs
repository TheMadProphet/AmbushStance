using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private MatrixFrame[] _waypoints;
    private readonly Dictionary<Formation, int> _nextWaypoint = new();
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
                    agent.SetWatchState(Agent.WatchState.Alarmed)
                );
                formation.SetControlledByAI(true);
            }
            enemyTeam.ResetTactic();
        }
        _nextWaypoint.Clear();
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
