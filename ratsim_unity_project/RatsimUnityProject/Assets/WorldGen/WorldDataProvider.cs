using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Abstract base for all world generation data providers.
/// Replaces WorldLoadingModule with dependency-aware ordering.
///
/// Each provider declares:
///   - Provides: what WorldDataType(s) it produces
///   - DependsOn: what WorldDataType(s) must exist before it runs
///
/// The system topologically sorts providers so dependencies are
/// always satisfied before a provider is asked to generate data.
///
/// Providers implement whichever generation granularity matches their data:
///   - Generate(): global, once per episode (layout, agents, lighting)
///   - GenerateChunk(): per-chunk spatial data (terrain mesh, vegetation)
///   - ClearChunk(): per-chunk cleanup on unload
///   - Clear(): full episode cleanup
/// </summary>
public abstract class WorldDataProvider : MonoBehaviour
{
    public static List<WorldDataProvider> registered = new List<WorldDataProvider>();

    public abstract WorldDataType[] Provides { get; }
    public virtual WorldDataType[] DependsOn => new WorldDataType[0];

    protected virtual void OnEnable()
    {
        registered.Add(this);
    }

    protected virtual void OnDisable()
    {
        registered.Remove(this);
    }

    /// <summary>Called once per episode for global work (layout, agent spawning, lighting).
    /// Runs in dependency order before any chunk loading begins.</summary>
    public virtual void Generate() { }

    /// <summary>Called when a chunk needs this provider's data at the given LOD.</summary>
    public virtual void GenerateChunk(int cx, int cz, int lod) { }

    /// <summary>Called when a chunk no longer needs this provider's data.</summary>
    public virtual void ClearChunk(int cx, int cz, int lod) { }

    /// <summary>Called on episode reset to destroy all generated data.</summary>
    public abstract void Clear();
}
