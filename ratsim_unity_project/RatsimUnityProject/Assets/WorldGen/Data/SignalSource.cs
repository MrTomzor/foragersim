using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Omnidirectional signal emitter. Attach to any GameObject that should broadcast a
/// scalar signal on a named channel (e.g. "food", "predator"). Self-registers into
/// a static list while the component is enabled; the <see cref="SectorSignalSensor"/>
/// iterates this list each tick to compute per-sensor observations.
///
/// No occlusion. Falloff with distance is either linear (value drops linearly to 0 at
/// <see cref="range"/>) or exponential (value = strength * exp(-3 * d / range) —
/// chosen so the value at d = range is ~0.05, near the linear cutoff).
///
/// Loaders may add this component to prefabs conditionally and tweak fields per-spawn
/// (see RewardObjectLoader's signal_source/* params). The default reward prefab does
/// NOT have this component — loaders add it when needed.
/// </summary>
public class SignalSource : MonoBehaviour
{
    public enum FalloffMode { Linear, Exponential }

    [Tooltip("Channel name this source broadcasts on (e.g. 'food', 'predator').")]
    public string channel = "default";

    [Tooltip("Peak signal strength at distance 0. Sensor output is clamped to [0,1].")]
    public float strength = 1f;

    [Tooltip("Distance at which the signal decays to ~0. Beyond this the source contributes 0.")]
    public float range = 10f;

    [Tooltip("Falloff shape across distance.")]
    public FalloffMode falloff = FalloffMode.Linear;

    private static readonly List<SignalSource> _active = new List<SignalSource>();
    /// <summary>Read-only view of all currently enabled signal sources.</summary>
    public static IReadOnlyList<SignalSource> Active => _active;

    void OnEnable()  { _active.Add(this); }
    void OnDisable() { _active.Remove(this); }

    /// <summary>Scalar value received at distance d. Returns 0 if d >= range.</summary>
    public float ValueAt(float distance)
    {
        if (range <= 0f || distance >= range) return 0f;
        if (distance <= 0f) return strength;
        switch (falloff)
        {
            case FalloffMode.Exponential:
                // exp(-3 * d/R) drops from 1 at d=0 to ~0.05 at d=R.
                return strength * Mathf.Exp(-3f * distance / range);
            case FalloffMode.Linear:
            default:
                return strength * (1f - distance / range);
        }
    }
}
