using AmbushStance.Deployment;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AmbushStance.Patches;

// For the player (ambusher) team, the entire map is a valid deployment zone except for
// the center exclusion square around the enemy spawn. This overrides the vanilla attacker
// boundary which would otherwise restrict placement to one end of the map.
[HarmonyPatch(typeof(DefaultTeamDeploymentPlan), "IsPositionInsideDeploymentBoundaries")]
internal class CenterExclusionDeploymentPatch
{
    static void Postfix(DefaultTeamDeploymentPlan __instance, Vec2 position, ref bool __result)
    {
        if (Mission.Current?.IsFieldBattle != true)
            return;
        if (__instance.Team != Mission.Current.PlayerTeam)
            return;

        AmbushDeploymentHelper.GetEnemySpawnCenter(Mission.Current, out var center, out var dir);
        __result = !AmbushDeploymentHelper.IsInsideCenterExclusion(position, center, dir);
    }
}

// MissionScreen.UpdateCamera snaps the camera to GetClosestDeploymentBoundaryPosition when
// IsPositionInsideDeploymentBoundaries returns false. Returning the original position for
// center-exclusion positions lets the camera move freely there while troop placement is
// still blocked (OrderTroopPlacer.UpdateFormationDrawing does an early return instead of snap).
[HarmonyPatch(typeof(DefaultTeamDeploymentPlan), "GetClosestDeploymentBoundaryPosition")]
internal class CenterExclusionCameraFreePatch
{
    static void Postfix(DefaultTeamDeploymentPlan __instance, Vec2 position, ref Vec2 __result)
    {
        if (Mission.Current?.IsFieldBattle != true)
            return;
        if (__instance.Team != Mission.Current.PlayerTeam)
            return;

        AmbushDeploymentHelper.GetEnemySpawnCenter(Mission.Current, out var center, out var dir);
        if (AmbushDeploymentHelper.IsInsideCenterExclusion(position, center, dir))
            __result = position;
    }
}
