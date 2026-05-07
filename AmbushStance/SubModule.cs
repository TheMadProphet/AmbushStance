using AmbushStance.Behaviors;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace AmbushStance;

public class SubModule : MBSubModuleBase
{
    public static Harmony HarmonyInstance { get; private set; }

    protected override void OnSubModuleLoad()
    {
        HarmonyInstance = new Harmony("mod.harmony.AmbushStance");
        HarmonyInstance.PatchAll();
    }

    public override void OnMissionBehaviorInitialize(Mission mission)
    {
        base.OnMissionBehaviorInitialize(mission);
        // Logic itself bails out for non-spawn-path missions, so registering unconditionally is safe.
        mission.AddMissionBehavior(new AmbushMarchMissionLogic());
    }
}
