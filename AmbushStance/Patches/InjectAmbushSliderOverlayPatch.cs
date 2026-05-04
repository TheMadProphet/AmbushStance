using AmbushStance.ViewModels;
using HarmonyLib;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI.Data;
using TaleWorlds.MountAndBlade.GauntletUI.Mission.Singleplayer;

namespace AmbushStance.Patches;

[HarmonyPatch(typeof(MissionGauntletOrderOfBattleUIHandler), "OnMissionScreenInitialize")]
internal class InjectAmbushSliderOverlayInitPatch
{
    private static AmbushOrderOfBattleOverlayVM _vm;

    static void Postfix(MissionGauntletOrderOfBattleUIHandler __instance)
    {
        var layer = (GauntletLayer)
            AccessTools
                .Field(typeof(MissionGauntletOrderOfBattleUIHandler), "_gauntletLayer")
                .GetValue(__instance);

        _vm = new AmbushOrderOfBattleOverlayVM();
        layer.LoadMovie("AmbushOrderOfBattleOverlay", _vm);
    }

    internal static void FinalizeVM()
    {
        _vm?.OnFinalize();
        _vm = null;
    }
}

[HarmonyPatch(typeof(MissionGauntletOrderOfBattleUIHandler), "OnMissionScreenFinalize")]
internal class InjectAmbushSliderOverlayFinalizePatch
{
    static void Postfix() => InjectAmbushSliderOverlayInitPatch.FinalizeVM();
}
