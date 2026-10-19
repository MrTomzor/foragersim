using UnityEngine;

/// <summary>
/// Data component placed on a WorldStructure GameObject to control how trees
/// are generated inside that structure's footprint.
///
/// - Remove: no trees spawn inside the footprint
/// - DecreaseDensity: trees spawn with probability equal to <see cref="value"/>
///   (0 = no trees, 1 = full density)
/// - IncreaseDensity: normal trees always spawn, plus <see cref="value"/> × base
///   density extra trees are added deterministically
///
/// Structures WITHOUT this component are transparent to tree generation.
/// </summary>
public class VegetationModification : MonoBehaviour {

    public enum Mode { Remove, DecreaseDensity, IncreaseDensity }

    public Mode mode = Mode.Remove;

    [Range(0f, 2f)]
    [Tooltip("DecreaseDensity: fraction of trees kept (0–1). IncreaseDensity: multiplier for extra trees. Unused for Remove.")]
    public float value = 1f;
}
