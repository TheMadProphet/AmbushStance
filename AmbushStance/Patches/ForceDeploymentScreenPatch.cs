using HarmonyLib;
using SandBox.GameComponents;
using TaleWorlds.MountAndBlade;

namespace AmbushStance.Patches;

// Ensures the player always receives the deployment screen in field battles,
// bypassing the vanilla "army leader with 20+ troops" requirement.
[HarmonyPatch(typeof(SandboxBattleInitializationModel), "CanPlayerSideDeployWithOrderOfBattleAux")]
internal class ForceDeploymentScreenPatch
{
    static bool Prefix(ref bool __result)
    {
        if (Mission.Current?.IsFieldBattle != true)
            return true;

        __result = true;
        return false;
    }
}
