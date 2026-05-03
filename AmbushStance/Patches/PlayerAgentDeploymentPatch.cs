using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AmbushStance.Patches;

// During deployment, the base game sets the player agent's Controller to None so they
// can't act. The non-AI path in ForceUpdateCachedAndFormationValues calls TrySetFormationFrame,
// which has an IsFormationUnitPositionAvailable check that blocks the player agent from
// being teleported with their formation. AI agents use ParallelUpdateCachedAndFormationValues-
// ForAIAgent which bypasses that check and teleports correctly.
//
// Fix: after the base sets Controller = None, flip it to AI. The second call to this
// method at deployment end is a no-op in the original (flag already set), so the agent's
// controller will be Player at that point and the condition below won't fire.
[HarmonyPatch(typeof(DeploymentMissionController), "OnAgentControllerSetToPlayer")]
internal class PlayerAgentDeploymentPatch
{
    static void Postfix(Agent agent)
    {
        if (Mission.Current?.IsFieldBattle != true)
            return;
        if (agent.Controller == AgentControllerType.None)
            agent.Controller = AgentControllerType.AI;
    }
}
