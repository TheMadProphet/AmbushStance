using System.Collections.Generic;
using System.Linq;
using AmbushStance.Deployment;
using HarmonyLib;
using SandBox.View.Missions;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.MountAndBlade.View.MissionViews.Singleplayer;

namespace AmbushStance.Patches;

// Do not place vanilla deployment boundary markers
// TODO: Do this conditionally - only on ambush
[HarmonyPatch(typeof(MissionDeploymentBoundaryMarker), "AddBoundaryMarkerForSide")]
internal class HideMissionDeploymentBoundaryMarker
{
    static bool Prefix()
    {
        return false;
    }
}
