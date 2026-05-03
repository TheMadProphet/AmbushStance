using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

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
