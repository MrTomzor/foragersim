using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns four wall WorldStructures that enclose the world in a box when
/// "world_bounds/boundary_type" = "visible_wall". Does nothing for any other value.
///
/// Each wall is an instance of the "world_boundary" prefab
/// (Resources/WorldGen/WorldStructurePrefabs/world_boundary), scaled to fit the
/// boundary dimensions. The prefab root transform scale is used to size the wall,
/// so the prefab should have its footprintCollider as a direct child with default
/// local scale (1,1,1).
///
/// Wall centers are placed at terrain height so non-flat height modes are handled.
///
/// World config params read each episode (walls are fully rebuilt on Clear + Initialize):
///   world_bounds/width            – world X extent (default 100)
///   world_bounds/height           – world Z extent (default 100)
///   world_bounds/boundary_height  – wall Y scale (default 10)
///   world_bounds/boundary_type    – "visible_wall" to spawn; anything else = no walls
/// </summary>
public class WorldBoundaryLoader : WorldDataProvider {

    public override WorldDataType[] Provides => new[] { WorldDataType.Boundaries };
    public override WorldDataType[] DependsOn => new[] { WorldDataType.Height, WorldDataType.Layout };

    [Header("Defaults")]
    public float defaultWallThickness = 1f;

    private readonly List<WorldStructure> _walls = new List<WorldStructure>();

    private const string PrefabPath = "WorldGen/WorldStructurePrefabs/world_boundary";

    // ─────────────────────────────────────────────
    //  WorldDataProvider
    // ─────────────────────────────────────────────

    public override void Generate() {
        string boundaryType = WorldLoadingController.GetParamString("world_bounds/boundary_type", "none");
        if (!boundaryType.Equals("visible_wall", System.StringComparison.OrdinalIgnoreCase)){
            Debug.Log($"WorldBoundaryLoader: boundary_type = '{boundaryType}' (not 'visible_wall'), skipping wall spawning");
            return;
        }

        WorldStructure prefab = Resources.Load<WorldStructure>(PrefabPath);
        if (prefab == null) {
            Debug.LogError($"WorldBoundaryLoader: prefab not found at Resources/{PrefabPath}");
            return;
        }

        float worldW    = WorldLoadingController.GetParamFloat("world_bounds/width",           100f);
        float worldH    = WorldLoadingController.GetParamFloat("world_bounds/height",          100f);
        float wallH     = WorldLoadingController.GetParamFloat("world_bounds/boundary_height",  10f);
        float thickness = defaultWallThickness;

        float halfW = worldW * 0.5f;
        float halfH = worldH * 0.5f;

        // North (+Z) and South (-Z): length runs along X axis
        SpawnWall(prefab, new Vector2(0f,    halfH), worldW,     wallH, thickness);
        SpawnWall(prefab, new Vector2(0f,   -halfH), worldW,     wallH, thickness);
        // East (+X) and West (-X): length runs along Z axis
        SpawnWall(prefab, new Vector2( halfW, 0f),   thickness,  wallH, worldH);
        SpawnWall(prefab, new Vector2(-halfW, 0f),   thickness,  wallH, worldH);
    }

    // Called every episode reset — rebuilds walls from scratch so world_bounds
    // param changes (including different dimensions) are always reflected.
    public override void Clear() {
        // WorldStructure.OnDestroy() unregisters from WorldData automatically.
        foreach (WorldStructure wall in _walls)
            if (wall != null) DestroyImmediate(wall.gameObject);
        _walls.Clear();
    }

    // ─────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────

    private void SpawnWall(
        WorldStructure prefab,
        Vector2        center2D,
        float          scaleX,
        float          scaleY,
        float          scaleZ)
    {
        // Sample terrain height at wall midpoint so walls sit flush on any terrain mode.
        float   terrainY = WorldServices.Get<IHeightProvider>().GetTerrainHeight(center2D.x, center2D.y);
        Vector3 pos      = new Vector3(center2D.x, terrainY + scaleY * 0.5f, center2D.y);

        WorldStructure wall = Instantiate(prefab, pos, Quaternion.identity, transform);
        wall.structureType  = "world_boundary";

        // Scale the root transform — both the visual mesh and the footprint collider
        // (expected to be a child with default local scale) inherit this scale,
        // so GetBoundingBox2D() returns the correct world-space footprint.
        wall.transform.localScale = new Vector3(scaleX, scaleY, scaleZ);

        // WorldStructure.Awake() auto-registered with the prefab's default scale.
        // Re-register now that the scale is correct so the chunk dict is accurate.
        WorldData.UnregisterStructure(wall);
        WorldData.RegisterStructure(wall);

        _walls.Add(wall);
        Debug.Log($"WorldBoundaryLoader: spawned wall at {pos}, scale ({scaleX:F1}, {scaleY:F1}, {scaleZ:F1})");
    }
}
