using UnityEngine;

/// <summary>
/// Data component placed on a WorldStructure GameObject to control how terrain
/// height is modified inside (and around) that structure's footprint.
///
/// - Flatten: samples the base terrain height at the footprint edges, averages
///   them, and flattens to that height. The structure GO is repositioned to match.
/// - SetHeight: flattens terrain to the specified <see cref="targetHeight"/>.
///   The structure GO is repositioned to match.
/// - AddHeight: adds <see cref="heightDelta"/> to the base terrain height
///   inside the footprint. Does not reposition the structure.
///
/// <see cref="blendMargin"/> controls how far beyond the footprint the
/// modification blends back to natural terrain (smoothstep transition).
///
/// Structures WITHOUT this component are transparent to terrain height modification.
/// </summary>
public class TerrainModification : MonoBehaviour {

    public enum Mode { Flatten, SetHeight, AddHeight }

    public Mode mode = Mode.Flatten;

    [Tooltip("Target Y height for SetHeight mode.")]
    public float targetHeight = 0f;

    [Tooltip("Height delta for AddHeight mode (positive = raise, negative = lower).")]
    public float heightDelta = 0f;

    [Tooltip("Distance beyond the footprint edge where the modification blends back to natural terrain.")]
    public float blendMargin = 10f;
}
