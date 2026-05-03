using HarmonyLib;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace AmbushStance;

public class SubModule : MBSubModuleBase
{
    public static Harmony HarmonyInstance { get; private set; }

    protected override void OnSubModuleLoad()
    {
        HarmonyInstance = new Harmony("mod.harmony.AmbushStance");
        HarmonyInstance.PatchAll();
        UIConfig.DoNotUseGeneratedPrefabs = true;

        Module.CurrentModule.AddInitialStateOption(
            new InitialStateOption(
                "Message",
                new TextObject("Message"),
                9990,
                () =>
                {
                    InformationManager.DisplayMessage(new InformationMessage("Hello World!"));
                },
                () => (false, null)
            )
        );
    }
}
