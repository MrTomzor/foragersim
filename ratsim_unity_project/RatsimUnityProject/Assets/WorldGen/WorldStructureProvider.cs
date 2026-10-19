using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Abstract base for providers that react to individual WorldStructure load/unload events.
/// Extends WorldDataProvider so it participates in the dependency graph and episode Clear() loop.
///
/// Replaces WorldStructureLoader. StructureLoadingCoordinator calls
/// OnWorldStructureLoaded/Unloaded on all registered WorldStructureProviders
/// when structures enter/leave loaded chunks.
/// </summary>
public abstract class WorldStructureProvider : WorldDataProvider
{
    public static new List<WorldStructureProvider> registered = new List<WorldStructureProvider>();

    protected override void OnEnable()
    {
        base.OnEnable();
        registered.Add(this);
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        registered.Remove(this);
    }

    // Default no-ops for chunk callbacks — override only if needed.
    public override void GenerateChunk(int cx, int cz, int lod) { }
    public override void ClearChunk(int cx, int cz, int lod) { }

    /// <summary>Called when a structure becomes visible at the given LOD.</summary>
    public abstract void OnWorldStructureLoaded(WorldStructure s, int lod);

    /// <summary>Called when a structure is fully out of range.</summary>
    public abstract void OnWorldStructureUnloaded(WorldStructure s, int lod);
}
