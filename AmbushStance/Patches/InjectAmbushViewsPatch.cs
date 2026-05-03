using System.Collections.Generic;
using System.Linq;
using AmbushStance.Deployment;
using HarmonyLib;
using SandBox.View.Missions;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace AmbushStance.Patches;

// Adds AmbushCenterExclusionMarker to the field battle mission view list
// so the player sees flags around the forbidden center zone during deployment.
[HarmonyPatch(typeof(SandBoxMissionViews), "OpenBattleMission")]
internal class InjectAmbushViewsPatch
{
    static void Postfix(ref MissionView[] __result)
    {
        var views = __result.ToList();
        views.Add(new AmbushCenterExclusionMarker());
        __result = views.ToArray();
    }
}
