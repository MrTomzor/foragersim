using UnityEngine;

/// <summary>
/// Generic WorldStructureProvider that manages LOD visibility on WorldStructure instances.
///
/// WorldStructure prefabs contain children named LOD0, LOD1, etc.  When a structure is
/// loaded at a given LOD, this loader enables the matching child and disables all other
/// LOD children.  On unload, all LOD children are disabled.
///
/// Enabled LOD content is set to the Default layer so it renders in agent cameras and
/// is excluded from WorldGen physics queries.
/// </summary>
public class SimpleStructureLoader : WorldStructureProvider {

    public override WorldDataType[] Provides => new[] { WorldDataType.StructureContent };
    public override WorldDataType[] DependsOn => new[] { WorldDataType.StructureEvents };

    // ─────────────────────────────────────────────
    //  WorldStructureProvider
    // ─────────────────────────────────────────────

    public override void OnWorldStructureLoaded(WorldStructure s, int lod) {
        string lodName = $"LOD{lod}";

        foreach (Transform child in s.transform) {
            if (!IsLodChild(child.name)) continue;

            if (child.name == lodName) {
                child.gameObject.SetActive(true);
                SetLayerRecursive(child.gameObject, 0);
            } else {
                child.gameObject.SetActive(false);
            }
        }
    }

    public override void OnWorldStructureUnloaded(WorldStructure s, int lod) {
        foreach (Transform child in s.transform) {
            if (IsLodChild(child.name))
                child.gameObject.SetActive(false);
        }
    }

    public override void Clear() { }

    // ─────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────

    private static bool IsLodChild(string name) {
        // Match "LOD" followed by one or more digits
        if (name.Length < 4 || !name.StartsWith("LOD")) return false;
        for (int i = 3; i < name.Length; i++)
            if (!char.IsDigit(name[i])) return false;
        return true;
    }

    private static void SetLayerRecursive(GameObject go, int layer) {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursive(child.gameObject, layer);
    }
}
