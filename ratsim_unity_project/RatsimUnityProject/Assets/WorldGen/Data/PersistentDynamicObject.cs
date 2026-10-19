using UnityEngine;

/// <summary>
/// Attach to any dynamically-spawned object (rubble, physics props, etc.) that should
/// persist across chunk unload/reload cycles.
///
/// On Awake, the object:
///   1. Finds its parent WorldStructure and records the origin structure's deterministic ID.
///   2. Checks with DynamicObjectLoader whether this structure has already spawned
///      persistent objects in a previous step. If so, this is a duplicate from a
///      structure reload — the object destroys itself immediately.
///   3. Otherwise, registers with DynamicObjectLoader and reparents to the loader's
///      transform so it survives the structure's content container being destroyed.
///
/// DynamicObjectLoader handles chunk-based enable/disable based on the object's
/// current world position and requiredLod.
/// </summary>
public class PersistentDynamicObject : MonoBehaviour {

    [Tooltip("Minimum LOD level required for this object to be active (0 = most detailed)")]
    public int requiredLod = 0;

    /// <summary>Deterministic ID of the structure that originally spawned this object.</summary>
    [HideInInspector] public int originStructureId;

    private void Awake() {
        var loader = DynamicObjectLoader.Instance;
        if (loader == null) {
            Debug.LogError("PersistentDynamicObject: no DynamicObjectLoader in scene");
            return;
        }

        WorldStructure parentStructure = GetComponentInParent<WorldStructure>();
        if (parentStructure == null) {
            Debug.LogWarning($"PersistentDynamicObject: '{name}' has no parent WorldStructure, registering without origin");
            originStructureId = 0;
            loader.Register(this);
            transform.SetParent(loader.transform);
            return;
        }

        originStructureId = parentStructure.DeterministicId;
        uint currentStep = RoslikeTCPServer.GetInstance().stepIndex;

        if (loader.IsStructureRespawn(originStructureId, currentStep)) {
            // This structure already has living persistent objects from a previous load.
            // We are a duplicate from a structure reload — destroy self.
            DestroyImmediate(gameObject);
            return;
        }

        loader.RecordStructureSpawn(originStructureId, currentStep);
        loader.Register(this);
        transform.SetParent(loader.transform);
    }

    // Not in Awake: Awake runs while the spawner is activating the hierarchy, where SetActive
    // is refused. Start runs before the next simulation step (one step per frame), so the
    // object never simulates over an unloaded chunk.
    private void Start() {
        DynamicObjectLoader.Instance?.RefreshVisibility(this);
    }

    private void OnDestroy() {
        // Guard against the DestroyImmediate path above — don't unregister if we never registered.
        if (DynamicObjectLoader.Instance != null)
            DynamicObjectLoader.Instance.Unregister(this);
    }
}
