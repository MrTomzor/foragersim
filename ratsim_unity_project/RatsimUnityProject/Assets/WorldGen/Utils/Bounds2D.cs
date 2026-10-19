using UnityEngine;
using System.Linq;

public struct Bounds2D {
    public Vector2 center;
    public Vector2 size;
    public float   rotation; // degrees, CCW

    public Bounds2D(Vector2 center, Vector2 size, float rotation) {
        this.center   = center;
        this.size     = size;
        this.rotation = rotation;
    }

    public Vector2[] GetVertices() {
        float hw  = size.x * 0.5f, hh = size.y * 0.5f;
        Vector2[] local = {
            new Vector2(-hw, -hh), new Vector2( hw, -hh),
            new Vector2( hw,  hh), new Vector2(-hw,  hh)
        };
        Vector2[] world = new Vector2[4];
        for (int i = 0; i < 4; i++)
            world[i] = center + Rotate(local[i], rotation);
        return world;
    }

    public bool Overlaps(Bounds2D other) {
        Vector2[] axesA = { GetAxis(0), GetAxis(1) };
        Vector2[] axesB = { other.GetAxis(0), other.GetAxis(1) };
        Vector2[] vertsA = GetVertices();
        Vector2[] vertsB = other.GetVertices();

        foreach (var axis in axesA.Concat(axesB)) {
            Project(vertsA, axis, out float minA, out float maxA);
            Project(vertsB, axis, out float minB, out float maxB);
            if (maxA < minB || maxB < minA) return false;
        }
        return true;
    }

    public bool Contains(Vector2 point) {
        // transform point into local space and do AABB check
        Vector2 local = InverseRotate(point - center, rotation);
        return Mathf.Abs(local.x) <= size.x * 0.5f &&
               Mathf.Abs(local.y) <= size.y * 0.5f;
    }

    private Vector2 GetAxis(int index) {
        float rad = rotation * Mathf.Deg2Rad;
        return index == 0
            ? new Vector2( Mathf.Cos(rad), Mathf.Sin(rad))
            : new Vector2(-Mathf.Sin(rad), Mathf.Cos(rad));
    }

    private static void Project(Vector2[] verts, Vector2 axis, out float min, out float max) {
        min = max = Vector2.Dot(verts[0], axis);
        for (int i = 1; i < verts.Length; i++) {
            float p = Vector2.Dot(verts[i], axis);
            if (p < min) min = p; if (p > max) max = p;
        }
    }

    private static Vector2 Rotate(Vector2 v, float deg) {
        float rad = deg * Mathf.Deg2Rad;
        return new Vector2(
            v.x * Mathf.Cos(rad) - v.y * Mathf.Sin(rad),
            v.x * Mathf.Sin(rad) + v.y * Mathf.Cos(rad));
    }

    private static Vector2 InverseRotate(Vector2 v, float deg) => Rotate(v, -deg);
}