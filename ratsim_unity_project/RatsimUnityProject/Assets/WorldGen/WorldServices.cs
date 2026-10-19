using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Static service locator for cross-provider queries.
/// Replaces static singleton patterns (e.g. WorldHeightLoader.instance).
///
/// Providers register their interfaces during initialization:
///   WorldServices.Register&lt;IHeightProvider&gt;(this);
///
/// Consumers query without knowing the concrete class:
///   float h = WorldServices.Get&lt;IHeightProvider&gt;().GetTerrainHeight(x, z);
/// </summary>
public static class WorldServices
{
    private static readonly Dictionary<Type, object> _services = new Dictionary<Type, object>();

    public static void Register<T>(T service) where T : class
    {
        _services[typeof(T)] = service;
    }

    public static T Get<T>() where T : class
    {
        if (_services.TryGetValue(typeof(T), out object service))
            return (T)service;
        Debug.LogError($"WorldServices: no {typeof(T).Name} registered");
        return null;
    }

    public static bool Has<T>() where T : class
    {
        return _services.ContainsKey(typeof(T));
    }

    public static void Clear()
    {
        _services.Clear();
    }
}

// ─────────────────────────────────────────────
//  Service interfaces
// ─────────────────────────────────────────────

/// <summary>
/// Provides terrain height queries. Implemented by WorldHeightLoader.
/// Used by: WorldLayoutLoader, WorldBoundaryLoader, AgentLoader,
///          TreeLoader, TerrainMeshLoader, TerrainTextureLoader, RewardObjectLoader, WorldData.
/// </summary>
public interface IHeightProvider
{
    float GetTerrainHeight(float x, float z);
    float GetBaseTerrainHeight(float x, float z);

    /// <summary>
    /// Scans all structures for TerrainModification components and registers
    /// height influence zones. Called by layout provider after structure placement.
    /// </summary>
    void ProcessTerrainModifications();
}

/// <summary>
/// Provides terrain mesh info. Implemented by TerrainMeshLoader.
/// Used by: TerrainTextureLoader (to align texture resolution to mesh grid),
///          AgentLoader (to filter terrain colliders during spawn).
/// </summary>
public interface ITerrainMeshProvider
{
    int GetQuadsPerSide(int lod);
    void SetChunkTexture(int cx, int cz, UnityEngine.Texture2D tex);

    /// <summary>
    /// Returns true if the given collider belongs to the terrain mesh.
    /// Used to exclude terrain from physics overlap checks.
    /// </summary>
    bool IsTerrainCollider(UnityEngine.Collider col);
}

/// <summary>
/// Provides world layout queries. Implemented by WorldLayoutLoader.
/// Used by: CityLoader (to connect entry point stubs to the road grid).
/// </summary>
public interface ILayoutProvider
{
    System.Collections.Generic.List<EntryPoint> GetEntryPoints(WorldStructure structure);
}

/// <summary>
/// Provides access to active 2D smoke objects. Implemented by SmokeLoader.
/// Used by: SemanticLidarSensor (to corrupt rays passing through smoke).
/// </summary>
public interface ISmokeProvider
{
    System.Collections.Generic.List<SmokeObject2D> GetActiveSmokeObjects();
}

/// <summary>
/// Provides room queries for layout modes that expose a distinct "room" concept
/// (e.g. maze rooms connected by corridors). Implemented by MazeLayoutLoader.
/// Used by spawners (e.g. RewardObjectLoader) that want to constrain placement
/// to the interior of rooms, excluding corridors and walls.
/// </summary>
public interface IRoomProvider
{
    bool IsInRoom(float x, float z);
    System.Collections.Generic.List<Bounds2D> GetRooms();
}
