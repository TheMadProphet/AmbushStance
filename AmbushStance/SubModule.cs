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
}
