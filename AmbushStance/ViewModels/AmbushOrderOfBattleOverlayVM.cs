using System;
using AmbushStance.Deployment;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AmbushStance.ViewModels;

public class AmbushOrderOfBattleOverlayVM : ViewModel
{
    private float _pathOffset;

    [DataSourceProperty]
    public float PathOffset
    {
        get => _pathOffset;
        set
        {
            if (Math.Abs(value - _pathOffset) > 0.025f)
            {
                _pathOffset = value;
                AmbushDeploymentHelper.RedeployEnemyWithOffset(Mission.Current, value);
                Mission.Current.GetMissionBehavior<AmbushCenterExclusionMarker>().ReplaceMarkers();
                OnPropertyChangedWithValue(value);
            }
        }
    }
}
