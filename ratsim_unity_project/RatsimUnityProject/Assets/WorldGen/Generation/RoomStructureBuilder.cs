using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Shared builder for footprint-only "room" <see cref="WorldStructure"/>s that
/// expose a <c>LOD0/rewardSpawnPositions/&lt;slot&gt;</c> hierarchy of spawn slots.
///
/// Both <see cref="MazeLayoutLoader"/> (one slot per interior cell) and
/// <see cref="WidloskiMazeLayoutLoader"/> (one center slot per grid room) build
/// their rooms through this single code path, so any structure-aware loader
/// (RewardObjectLoader, WellLoader) treats rooms from either layout identically.
/// In particular the "drop a well in a room's center" rule lives in exactly one
/// place: WellLoader anchors to <c>rewardSpawnPositions</c>, and every layout
/// produces them the same way here.
///
/// The footprint is a disabled <see cref="BoxCollider"/> on the WorldGen layer —
/// no physics query ever hits it, so rooms don't block agents or spawners; they
/// remain queryable only via <see cref="WorldStructure.GetBoundingBox2D"/> /
/// <see cref="IRoomProvider"/>. Slots must be in place BEFORE the structure is
/// activated (activation fires WorldStructure.Awake + registration).
/// </summary>
public static class RoomStructureBuilder {

    public struct Slot {
        public string  name;      // e.g. "cell_i_j" (parsed into a grid coord by WellLoader)
        public Vector3 position;  // world-space
    }

    /// <summary>
    /// Builds a room structure of the given type at <paramref name="centerWorld"/> with
    /// footprint (XZ) <paramref name="size"/> and the given spawn slots, parents it to
    /// <paramref name="parent"/>, and activates it. Returns the WorldStructure.
    /// </summary>
    public static WorldStructure Build(
            Transform parent,
            string structureType,
            Vector3 centerWorld,
            Vector2 size,
            IReadOnlyList<Slot> slots) {

        GameObject root = new GameObject(structureType);
        root.SetActive(false);

        // Footprint-only child: disabled BoxCollider on WorldGen layer. Disabled means
        // no physics queries (CheckBox, Raycast) ever hit it, so rooms don't block
        // agents or other spawners by accident.
        GameObject footprint = new GameObject("Footprint");
        footprint.transform.SetParent(root.transform, false);
        int wgLayer = LayerMask.NameToLayer("WorldGen");
        if (wgLayer >= 0) footprint.layer = wgLayer;

        BoxCollider col = footprint.AddComponent<BoxCollider>();
        col.size = Vector3.one;
        col.isTrigger = true;
        col.enabled = false;

        WorldStructure ws = root.AddComponent<WorldStructure>();
        ws.structureType = structureType;
        ws.footprintCollider = col;

        root.transform.SetParent(parent);
        root.transform.position = centerWorld;
        // Scale only the footprint child so WorldStructure.GetSize() (size × lossyScale) returns `size`.
        footprint.transform.localScale = new Vector3(size.x, 1f, size.y);

        // LOD0/rewardSpawnPositions/<slot> — one Transform per spawn slot. Built for every
        // room regardless of label so any structure-aware loader can use them.
        GameObject lod0 = new GameObject("LOD0");
        lod0.transform.SetParent(root.transform, false);
        GameObject group = new GameObject("rewardSpawnPositions");
        group.transform.SetParent(lod0.transform, false);
        if (slots != null) {
            foreach (Slot s in slots) {
                GameObject sp = new GameObject(s.name);
                sp.transform.SetParent(group.transform, false);
                sp.transform.position = s.position;
            }
        }

        root.SetActive(true);
        return ws;
    }
}
