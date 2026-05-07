using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace AmbushStance.Patches;

// Bypass deployment-boundary clamping/checks for the enemy team during deployment so column
// formations placed along the spawn path are accepted as-is.
//
// Two independent gates need bypassing:
//   1. SupportsNavmesh — gates the position projection inside Agent.TrySetFormationFrame.
//      With it true, the position would be clamped to the deployment boundary.
//   2. HasDeploymentBoundaries — gates IsPositionInsideDeploymentBoundaries inside
//      Mission.IsFormationUnitPositionAvailableMT. If the position fails that check,
//      TrySetFormationFrame disables the formation frame and the agent never teleports.
internal static class DeploymentBoundaryBypassPatch
{
    [HarmonyPatch(
        typeof(DefaultMissionDeploymentPlan),
        nameof(DefaultMissionDeploymentPlan.SupportsNavmesh)
    )]
    internal static class SupportsNavmesh
    {
        static bool Prefix(Team team, ref bool __result)
        {
            if (Mission.Current != null && team == Mission.Current.PlayerEnemyTeam)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(
        typeof(DefaultMissionDeploymentPlan),
        nameof(DefaultMissionDeploymentPlan.HasDeploymentBoundaries)
    )]
    internal static class HasDeploymentBoundaries
    {
        static bool Prefix(Team team, ref bool __result)
        {
            if (Mission.Current != null && team == Mission.Current.PlayerEnemyTeam)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
