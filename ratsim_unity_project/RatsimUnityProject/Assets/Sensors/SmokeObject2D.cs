using System.Collections.Generic;
using UnityEngine;

public enum SmokeCorruptionMode
{
    RandomHits,
    EffectiveRange
}

public class SmokeObject2D : MonoBehaviour
{
    public static List<SmokeObject2D> allActive = new List<SmokeObject2D>();

    [SerializeField] private float _radius = 10f;
    public float density = 0.1f;
    public SmokeCorruptionMode corruptionMode = SmokeCorruptionMode.RandomHits;
    public float effectiveRange = 5f;
    public float effectiveRangeVariance = 1f;

    public float radius
    {
        get => _radius;
        set
        {
            _radius = value;
            ApplyScale();
        }
    }

    private void ApplyScale()
    {
        var s = transform.localScale;
        s.x = _radius * 2;
        s.z = _radius * 2;
        transform.localScale = s;
    }

    void Awake() { ApplyScale(); }

    public Vector2 Center2D => new Vector2(transform.position.x, transform.position.z);

    void OnEnable()  { allActive.Add(this); }
    void OnDisable() { allActive.Remove(this); }

    /// <summary>
    /// 2D ray-circle intersection on the XZ plane.
    /// rayDir must be normalized. Returns true if the ray intersects this smoke circle.
    /// tEnter/tExit are distances along the ray, clamped to [0, maxT].
    /// </summary>
    public bool RayIntersect2D(Vector2 rayOrigin, Vector2 rayDir, float maxT,
                               out float tEnter, out float tExit)
    {
        tEnter = 0f;
        tExit = 0f;

        Vector2 L = Center2D - rayOrigin;
        float tc = Vector2.Dot(L, rayDir);
        float d2 = Vector2.Dot(L, L) - tc * tc;
        float r2 = radius * radius;

        if (d2 > r2) return false;

        float thc = Mathf.Sqrt(r2 - d2);
        tEnter = Mathf.Max(0f, tc - thc);
        tExit  = Mathf.Min(maxT, tc + thc);

        if (tEnter >= tExit) return false;
        return true;
    }
}
