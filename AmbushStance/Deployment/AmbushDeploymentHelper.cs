using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Missions.Handlers;

namespace AmbushStance.Deployment;

public static class AmbushDeploymentHelper
{
    public const float CenterExclusionHalfSize = 30f;

    public static void GetEnemySpawnCenter(
        Mission mission,
        out Vec2 spawnPosition,
        out Vec2 spawnDirection
    )
    {
        var enemyTeam = mission.PlayerEnemyTeam;
        if (enemyTeam == null)
        {
            spawnPosition = Vec2.Zero;
            spawnDirection = Vec2.Zero;
            return;
        }

        // Use the same call the game uses to place formations — correctly handles both
        // spawn-path scenes and entity-based spawn scenes.
        mission.GetFormationSpawnFrame(
            enemyTeam,
            FormationClass.Infantry,
            isReinforcement: false,
            out var formationSpawnPosition,
            out var formationSpawnDirection
        );
        spawnPosition = formationSpawnPosition.AsVec2;
        spawnDirection = formationSpawnDirection.Normalized();
    }

    public static void RedeployEnemyWithOffset(Mission mission, float sliderOffset)
    {
        var enemyTeam = mission.PlayerEnemyTeam;
        if (enemyTeam == null)
            return;

        // spawnPathOffset is relative to PivotOffset, not a 0-1 fraction.
        // Probe the valid range by saturating ClampPathOffset at both extremes.
        var spawnPath = mission.GetInitialSpawnPathData(enemyTeam.Side);
        var startOffset = spawnPath.ClampPathOffset(-1e9f); // → -PivotOffset (path distance = 0)
        var endOffset = spawnPath.ClampPathOffset(1e9f); // → PathLength - PivotOffset (path distance = PathLength)
        var pathOffset = startOffset + (endOffset - startOffset) * sliderOffset;

        var deploymentPlan = mission.DeploymentPlan;
        deploymentPlan.ClearDeploymentPlan(enemyTeam);
        deploymentPlan.MakeDeploymentPlan(enemyTeam, pathOffset);

        mission
            .GetMissionBehavior<BattleDeploymentHandler>()
            ?.AutoDeployTeamUsingDeploymentPlan(enemyTeam);

        // AutoDeployTeamUsingDeploymentPlan issues AIControlOff for non-siege battles.
        // Re-enable it so enemy AI behaves normally when the battle starts.
        var orderController = enemyTeam.MasterOrderController;
        orderController.SelectAllFormations();
        orderController.SetOrder(OrderType.AIControlOn);
        orderController.ClearSelectedFormations();
    }

    public static bool IsInsideCenterExclusion(Vec2 position, Vec2 center, Vec2 dir)
    {
        var perp = new Vec2(-dir.Y, dir.X);
        var offset = position - center;

        var localX = Vec2.DotProduct(offset, perp);
        var localY = Vec2.DotProduct(offset, dir);

        return MathF.Abs(localX) <= CenterExclusionHalfSize
            && MathF.Abs(localY) <= CenterExclusionHalfSize;
    }
}
