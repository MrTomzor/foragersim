using UnityEngine;

/// <summary>
/// Object-lifetime helpers for world generation.
/// </summary>
public static class WorldGenObjects {
    /// <summary>
    /// Deferred <c>Destroy</c> that takes effect for physics NOW. <c>Object.Destroy</c> only
    /// marks the object; it stays in the physics scene until the end of the frame, so a
    /// collision-checked spawn in the same frame (e.g. uniform rewards in the chunks the agent's
    /// requestor loads during the reset step) still collides with last episode's trees, rewards,
    /// walkers and wells — and places differently on the second reset than on the first.
    /// Deactivating first removes the colliders immediately; the memory is still freed at the
    /// end of the frame as usual. Use this in every provider's Clear() instead of Destroy.
    /// (DestroyImmediate would also work but is discouraged outside editor code and is slower
    /// for large hierarchies.)
    /// </summary>
    public static void DestroyHidden(GameObject go) {
        if (go == null) return;
        go.SetActive(false);
        Object.Destroy(go);
    }
}
