using System;
using UnityEngine;

public class WorldStructure : MonoBehaviour {

    public string structureType;

    [Header("Footprint")]
    public BoxCollider footprintCollider;

    /// <summary>
    /// The LOD currently loaded by WorldStructureProvider components (-1 = not loaded).
    /// Managed by StructureLoadingCoordinator.
    /// </summary>
    [HideInInspector] public int currentLod = -1;

    /// <summary>
    /// Deterministic ID derived from structure type, position, and dimensions.
    /// Stable across unload/reload cycles and across runs with the same seed.
    /// </summary>
    public int DeterministicId {
        get {
            if (_deterministicId == 0)
                _deterministicId = ComputeDeterministicId();
            return _deterministicId;
        }
    }
    private int _deterministicId;

    private int ComputeDeterministicId() {
        // SeedMix, not System.HashCode: HashCode is seeded per process / domain reload, which
        // made these ids (and everything ordered by them, e.g. the well roster) differ
        // between Unity runs at the same world seed.
        Vector2 size = GetSize();
        int h = SeedMix.StableStringHash(structureType);
        h = SeedMix.Combine(h, Mathf.RoundToInt(transform.position.x * 100));
        h = SeedMix.Combine(h, Mathf.RoundToInt(transform.position.z * 100));
        h = SeedMix.Combine(h, Mathf.RoundToInt(size.x * 100));
        h = SeedMix.Combine(h, Mathf.RoundToInt(size.y * 100));
        return h;
    }

    private bool _registered = false;

    private void Awake() {
        // auto-register if placed manually in scene
        // layout generator calls RegisterStructure() explicitly after
        // configuring size, so _registered guard prevents double registration
        if (!_registered)
            WorldData.RegisterStructure(this);
    }

    private void OnDestroy() {
        WorldData.UnregisterStructure(this);
    }

    // called by layout generator after instantiation to apply config-driven size
    public void SetFootprintSize(Vector2 size) {
        // adjust collider size, keeping lossyScale in mind
        /*Vector3 ls = footprintCollider.transform.lossyScale;
        footprintCollider.size = new Vector3(
            size.x / ls.x,
            footprintCollider.size.y,  // keep Y unchanged
            size.y / ls.z
        );*/

        // simply change the transform scale of the collider object (just XZ). The collider itself is 1,1,1. Keep the original transform Y scale.
        Vector3 localScale = footprintCollider.transform.localScale;
        footprintCollider.transform.localScale = new Vector3(
            size.x,
            localScale.y,
            size.y
        );
        
    }

    // called by layout generator — explicit registration after size is set
    public void Register() {
        _registered = true;
        WorldData.RegisterStructure(this);
    }

    public Vector2 GetSize() {
        Vector3 s  = footprintCollider.size;
        Vector3 ls = footprintCollider.transform.lossyScale;
        return new Vector2(s.x * ls.x, s.z * ls.z);
    }

    public Vector2 GetCenter2D() {
        Vector3 wc = footprintCollider.transform.TransformPoint(footprintCollider.center);
        return new Vector2(wc.x, wc.z);
    }

    public float GetRotationCCW() => -transform.eulerAngles.y;

    public Bounds2D GetBoundingBox2D() =>
        new Bounds2D(GetCenter2D(), GetSize(), GetRotationCCW());

    public void SetLOD(int lod) {
        Transform lod0 = transform.Find("LOD0");
        Transform lod1 = transform.Find("LOD1");
        if (lod0 != null) lod0.gameObject.SetActive(lod == 0);
        if (lod1 != null) lod1.gameObject.SetActive(lod == 1);
    }
}
