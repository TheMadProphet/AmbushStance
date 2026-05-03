using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace AmbushStance.Patches;

// In an ambush the enemy is already in position — the player can see what they're
// about to hit. Skip the vanilla logic that hides defenders when the player is the attacker.
[HarmonyPatch(typeof(DeploymentMissionController), "HideAgentsOfSide")]
internal class ShowEnemyDuringDeploymentPatch
{
    static bool Prefix()
    {
        return Mission.Current?.IsFieldBattle != true;
    }
}
