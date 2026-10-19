using UnityEngine;
using System.Collections.Generic;

public class SmokeLoader : WorldDataProvider, ISmokeProvider
{
    public override WorldDataType[] Provides => new[] { WorldDataType.Smoke };

    [Header("Prefab")]
    public GameObject smoke2dPrefab;

    [Header("Defaults (overridden by episode params)")]
    public int default2dModeEnabled = 1;
    public int default3dModeEnabled = 0;
    public float defaultRadius = 10f;
    public float defaultDensity = 0.1f;

    private bool _mode2d;
    private bool _mode3d;
    private float _radius;
    private float _density;
    private bool _paramsLoaded;

    public bool spawnGlobalSmoke = false;
    private GameObject _globalSmokeInstance;

    private readonly Dictionary<SmokeOrigin, GameObject> _spawnedByOrigin = new Dictionary<SmokeOrigin, GameObject>();

    protected override void OnEnable()
    {
        base.OnEnable();
        WorldServices.Register<ISmokeProvider>(this);
        SmokeOrigin.OnOriginEnabled += HandleOriginEnabled;
        SmokeOrigin.OnOriginDisabled += HandleOriginDisabled;
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        SmokeOrigin.OnOriginEnabled -= HandleOriginEnabled;
        SmokeOrigin.OnOriginDisabled -= HandleOriginDisabled;
    }

    public override void Generate()
    {
        LoadParams();
        if (spawnGlobalSmoke)
        {
            // instantiate the smoke prefab at origin and scale it up based on the world bounds size
            GameObject globalSmoke = Instantiate(smoke2dPrefab, Vector3.zero, Quaternion.identity);
            _globalSmokeInstance = globalSmoke;
            float worldW = WorldLoadingController.GetParamFloat("world_bounds/width");
            float worldH = WorldLoadingController.GetParamFloat("world_bounds/height");
            float worldSize = Mathf.Max(worldW, worldH);
            var smoke2d = globalSmoke.GetComponent<SmokeObject2D>();
            smoke2d.radius = worldSize;
            smoke2d.density = _density;
            smoke2d.corruptionMode = _corruptionMode;
            smoke2d.effectiveRange = _effectiveRange;
            smoke2d.effectiveRangeVariance = _effectiveRangeVariance;
            globalSmoke.name = "GlobalSmoke";
        }
    }

    private SmokeCorruptionMode _corruptionMode = SmokeCorruptionMode.RandomHits;
    private float _effectiveRange = 5f;
    private float _effectiveRangeVariance = 1f;

    private void LoadParams()
    {
        _mode2d  = WorldLoadingController.GetParamInt("smoke/2dmode_enabled", default2dModeEnabled) != 0;
        _mode3d  = WorldLoadingController.GetParamInt("smoke/3dmode_enabled", default3dModeEnabled) != 0;
        spawnGlobalSmoke  = WorldLoadingController.GetParamInt("smoke/global_smoke_enabled", 0) != 0;
        _radius  = WorldLoadingController.GetParamFloat("smoke/default_radius", defaultRadius);
        _density = WorldLoadingController.GetParamFloat("smoke/default_density", defaultDensity);

        string modeStr = WorldLoadingController.GetParamString("smoke/corruption_mode", "random_hits");
        _corruptionMode = modeStr == "effective_range"
            ? SmokeCorruptionMode.EffectiveRange
            : SmokeCorruptionMode.RandomHits;
        _effectiveRange = WorldLoadingController.GetParamFloat("smoke/effective_range", 5f);
        _effectiveRangeVariance = WorldLoadingController.GetParamFloat("smoke/effective_range_variance", 1f);

        _paramsLoaded = true;

        Debug.Log($"SmokeLoader: params loaded — 2d={_mode2d}, 3d={_mode3d}, radius={_radius}, density={_density}, corruptionMode={modeStr}, effectiveRange={_effectiveRange}, effectiveRangeVariance={_effectiveRangeVariance}");
    }

    private void HandleOriginEnabled(SmokeOrigin origin)
    {
        if (!_paramsLoaded) LoadParams();
        if (_spawnedByOrigin.ContainsKey(origin)) return;

        GameObject container = new GameObject($"_Smoke_{origin.name}");
        container.transform.SetParent(origin.transform);
        container.transform.localPosition = Vector3.zero;
        _spawnedByOrigin[origin] = container;

        if (_mode2d)
        {
            GameObject go;
            if (smoke2dPrefab != null)
            {
                go = Instantiate(smoke2dPrefab, origin.transform.position, Quaternion.identity, container.transform);
            }
            else
            {
                go = new GameObject("SmokeObject2D");
                go.transform.SetParent(container.transform);
                go.transform.position = origin.transform.position;
            }

            var smoke2d = go.GetComponent<SmokeObject2D>();
            if (smoke2d == null)
                smoke2d = go.AddComponent<SmokeObject2D>();
            smoke2d.radius = _radius;
            smoke2d.density = _density;
            smoke2d.corruptionMode = _corruptionMode;
            smoke2d.effectiveRange = _effectiveRange;
            smoke2d.effectiveRangeVariance = _effectiveRangeVariance;

            var nso = go.GetComponent<NamedSemanticObject>();
            if (nso == null)
                nso = go.AddComponent<NamedSemanticObject>();
            nso.semanticName = "smoke";
        }

        if (_mode3d)
        {
            // Stub for future particle-based smoke rendering
        }
    }

    private void HandleOriginDisabled(SmokeOrigin origin)
    {
        if (_spawnedByOrigin.TryGetValue(origin, out GameObject container))
        {
            if (container != null)
                DestroyImmediate(container);
            _spawnedByOrigin.Remove(origin);
        }
    }

    public override void Clear()
    {
        foreach (var kvp in _spawnedByOrigin)
            if (kvp.Value != null) DestroyImmediate(kvp.Value);
        _spawnedByOrigin.Clear();
        _paramsLoaded = false;

        if(spawnGlobalSmoke)
        {
            if (_globalSmokeInstance != null)
                DestroyImmediate(_globalSmokeInstance);
        }
    }

    public List<SmokeObject2D> GetActiveSmokeObjects()
    {
        return SmokeObject2D.allActive;
    }
}
