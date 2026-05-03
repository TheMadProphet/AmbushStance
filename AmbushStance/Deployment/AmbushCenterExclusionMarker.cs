using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.MountAndBlade.View.Tableaus.Thumbnails;

namespace AmbushStance.Deployment;

// Draws swallowtail banner flags around the center exclusion zone during deployment,
// so the player can see where they are NOT allowed to place troops.
public class AmbushCenterExclusionMarker : MissionView
{
    private const string PrefabName = "deploy_marker_red";
    private const float MarkerInterval = 1.75f;

    private readonly List<GameEntity> _markers = [];
    private GameEntity _cachedPrefab;

    public override void OnDeploymentPlanMade(Team team, bool isFirstPlan)
    {
        if (!isFirstPlan || !team.IsPlayerTeam)
            return;

        AmbushDeploymentHelper.GetEnemySpawnCenter(Mission, out var center, out var dir);
        var h = AmbushDeploymentHelper.CenterExclusionHalfSize;
        var perp = new Vec2(-dir.Y, dir.X);

        Vec2[] corners =
        [
            center + (-dir - perp) * h,
            center + (dir - perp) * h,
            center + (dir + perp) * h,
            center + (-dir + perp) * h,
        ];

        var banner = Mission.AttackerTeam?.Banner;
        for (var i = 0; i < corners.Length; i++)
        {
            var c0 = corners[i];
            var c1 = corners[(i + 1) % corners.Length];
            var start = new Vec3(c0.X, c0.Y, 0f);
            var end = new Vec3(c1.X, c1.Y, 0f);
            PlaceMarkersAlongSegment(start, end, banner);
        }
    }

    public override void OnAfterDeploymentFinished() => RemoveMarkers();

    public override void OnRemoveBehavior() => RemoveMarkers();

    private void PlaceMarkersAlongSegment(Vec3 start, Vec3 end, Banner banner)
    {
        var delta = end - start;
        var length = delta.Length;
        var step = delta.NormalizedCopy() * MarkerInterval;

        var pos = start;
        for (var dist = 0f; dist < length; dist += MarkerInterval)
        {
            var frame = MatrixFrame.Identity;

            // Position
            frame.origin = pos;
            if (
                !Mission.Scene.GetHeightAtPoint(
                    frame.origin.AsVec2,
                    BodyFlags.CommonCollisionExcludeFlagsForCombat,
                    ref frame.origin.z
                )
            )
                frame.origin.z = 0f;
            frame.origin.z += 0.1f;

            // Rotation
            var direction = delta.NormalizedCopy();
            var normal = Mission.Scene.GetNormalAt(frame.origin.AsVec2);
            frame.rotation.u = normal;
            frame.rotation.s = new Vec3(direction.x, direction.y, 0f);
            frame.rotation.f = Vec3.CrossProduct(frame.rotation.s, frame.rotation.u);
            frame.rotation.Orthonormalize();

            // Scale
            // var scale = Vec3.One * 0.4f;
            // frame.Scale(in scale);

            var entity = SpawnEntity(banner);
            entity.SetFrame(ref frame);
            _markers.Add(entity);

            pos += step;
        }
    }

    private GameEntity SpawnEntity(Banner banner)
    {
        if (_cachedPrefab == null)
            _cachedPrefab = GameEntity.Instantiate(null, PrefabName, callScriptCallbacks: false);

        var entity = GameEntity.CopyFrom(Mission.Scene, _cachedPrefab);
        entity.SetMobility(GameEntity.Mobility.Dynamic);

        if (banner == null)
            return entity;

        // var mesh = entity.GetFirstMesh();
        // var mat = mesh.GetMaterial().CreateCopy();
        // var debugInfo = BannerDebugInfo.CreateManual(nameof(AmbushCenterExclusionMarker));
        // banner.GetTableauTextureSmall(
        //     in debugInfo,
        //     tex =>
        //     {
        //         mat.SetTexture(Material.MBTextureType.DiffuseMap2, tex);
        //     }
        // );
        // mesh.SetMaterial(mat);

        return entity;
    }

    private void RemoveMarkers()
    {
        foreach (var e in _markers)
            e.Remove(103);
        _markers.Clear();
    }
}
