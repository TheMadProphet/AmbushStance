using HarmonyLib;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Missions.Handlers;

namespace AmbushStance.Patches;

// Forces the player to always be treated as the attacker (ambusher) in field battles.
// Both the deployment handler and its controller must agree on the player side.

[HarmonyPatch(typeof(BattleDeploymentHandler), MethodType.Constructor, typeof(bool))]
internal class ForcePlayerAttackerInHandlerPatch
{
    static void Prefix(ref bool isPlayerAttacker) => isPlayerAttacker = true;
}

[HarmonyPatch(typeof(BattleDeploymentMissionController), MethodType.Constructor, typeof(bool))]
internal class ForcePlayerAttackerInControllerPatch
{
    static void Prefix(ref bool isPlayerAttacker) => isPlayerAttacker = true;
}
