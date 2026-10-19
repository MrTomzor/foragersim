using UnityEngine;

/// <summary>
/// WorldDataProvider that applies lighting and fog settings each episode,
/// and optionally advances time-of-day each physics step via a continuous timer.
///
/// Time is expressed in hours (0–24):
///   0 / 24 = midnight,  6 = sunrise,  12 = noon,  18 = sunset
///
/// The timer is registered once in Start() so it is never double-registered
/// across episodes. The callback checks _advanceTime (set in Initialize /
/// cleared in Clear) so it is a no-op outside of an active episode.
///
/// Config params (all optional — inspector fields are the fallback defaults):
///   lighting/time_of_day          — initial hour (0–24), default 12
///   lighting/time_advance_rate    — in-game hours per simulated second, 0 = static
///   lighting/max_light_intensity  — directional light intensity at noon
///   lighting/max_ambient_intensity — ambient intensity at noon
///   lighting/sun_azimuth          — Y rotation of sun (compass direction it comes from)
///   fog/enabled                   — 0 or 1
///   fog/color_preset              — "gray" | "ocean" | "" (falls back to fog/color_r/g/b)
///   fog/color_r/g/b               — individual RGB fog colour components (0–1)
///   fog/density                   — fog density (exponential modes)
///   fog/mode                      — "linear" | "exponential" | "exponential_squared"
/// </summary>
public class LightingAndFogLoader : WorldDataProvider {

    public override WorldDataType[] Provides => new[] { WorldDataType.Lighting };

    // ─────────────────────────────────────────────
    //  Inspector references
    // ─────────────────────────────────────────────

    [Header("References")]
    [Tooltip("Assign the scene's main Directional Light here.")]
    public Light directionalLight;

    [Tooltip("Skybox material to use when fog is enabled (e.g. a flat-colour or unlit material). " +
             "Leave null to leave the skybox unchanged.")]
    public Material fogSkyboxMaterial;

    // ─────────────────────────────────────────────
    //  Lighting defaults
    // ─────────────────────────────────────────────

    [Header("Lighting Defaults")]
    [Tooltip("Initial time of day in hours (0–24). 6 = sunrise, 12 = noon, 18 = sunset.")]
    public float defaultTimeOfDay = 12f;

    [Tooltip("How many in-game hours pass per simulated second. 0 = static lighting.")]
    public float defaultTimeAdvanceRate = 0f;

    [Tooltip("Directional light intensity multiplier at noon.")]
    public float defaultMaxLightIntensity = 1.2f;

    [Tooltip("Ambient intensity multiplier at noon. Scales to 0 at midnight.")]
    public float defaultMaxAmbientIntensity = 1f;

    [Tooltip("Y rotation of the directional light — controls the sun's compass direction.")]
    public float defaultSunAzimuth = 45f;

    // ─────────────────────────────────────────────
    //  Fog defaults
    // ─────────────────────────────────────────────

    [Header("Fog Defaults")]
    public bool    defaultFogEnabled = false;
    public Color   defaultFogColor   = Color.gray;
    public float   defaultFogDensity = 0.02f;
    public FogMode defaultFogMode    = FogMode.ExponentialSquared;

    // ─────────────────────────────────────────────
    //  Runtime state (reset each episode)
    // ─────────────────────────────────────────────

    private float _currentTimeOfDay;     // hours, 0–24
    private float _timeAdvanceRate;      // in-game hours per simulated second
    private bool  _advanceTime;

    private float _maxLightIntensity;
    private float _maxAmbientIntensity;
    private float _sunAzimuth;

    // How many simulated seconds pass between timer ticks
    private const float TimerPeriod = 0.1f;

    private Material _originalSkybox;

    // ─────────────────────────────────────────────
    //  One-time setup
    // ─────────────────────────────────────────────

    private void Start() {
        _originalSkybox = RenderSettings.skybox;
        RoslikeTCPServer.GetInstance().RegisterTimerContinuous(OnTimerTick, TimerPeriod);
    }

    // ─────────────────────────────────────────────
    //  WorldDataProvider
    // ─────────────────────────────────────────────

    public override void Generate() {
        _currentTimeOfDay    = WorldLoadingController.GetParamFloat("lighting/time_of_day",           defaultTimeOfDay);
        _timeAdvanceRate     = WorldLoadingController.GetParamFloat("lighting/time_advance_rate",      defaultTimeAdvanceRate);
        _maxLightIntensity   = WorldLoadingController.GetParamFloat("lighting/max_light_intensity",    defaultMaxLightIntensity);
        _maxAmbientIntensity = WorldLoadingController.GetParamFloat("lighting/max_ambient_intensity",  defaultMaxAmbientIntensity);
        _sunAzimuth          = WorldLoadingController.GetParamFloat("lighting/sun_azimuth",            defaultSunAzimuth);
        _advanceTime         = _timeAdvanceRate > 0f;

        ApplyFog();
        ApplyLighting();
    }

    public override void Clear() {
        // Prevent the timer callback from mutating state between episodes.
        _advanceTime = false;
    }


    // ─────────────────────────────────────────────
    //  Timer callback
    // ─────────────────────────────────────────────

    private void OnTimerTick(TimerEvent ev) {
        if (!_advanceTime) return;
        _currentTimeOfDay = (_currentTimeOfDay + _timeAdvanceRate * TimerPeriod) % 24f;
        ApplyLighting();
    }

    // ─────────────────────────────────────────────
    //  Fog
    // ─────────────────────────────────────────────

    private void ApplyFog() {
        bool fogEnabled = WorldLoadingController.GetParamInt("fog/enabled", defaultFogEnabled ? 1 : 0) != 0;
        RenderSettings.fog = fogEnabled;

        if (!fogEnabled) {
            RenderSettings.skybox = _originalSkybox;
            return;
        }

        Color fogColor = ResolveColor(
            WorldLoadingController.GetParamString("fog/color_preset", ""),
            "fog/color_r", "fog/color_g", "fog/color_b",
            defaultFogColor
        );
        RenderSettings.fogColor   = fogColor;
        RenderSettings.fogDensity = WorldLoadingController.GetParamFloat("fog/density", defaultFogDensity);
        RenderSettings.fogMode    = ParseFogMode(
            WorldLoadingController.GetParamString("fog/mode", FogModeToString(defaultFogMode))
        );

        if (fogSkyboxMaterial != null)
            RenderSettings.skybox = fogSkyboxMaterial;
    }

    // ─────────────────────────────────────────────
    //  Lighting
    // ─────────────────────────────────────────────

    private void ApplyLighting() {
        // Normalise to 0–1 (0 = midnight, 0.25 = 6am, 0.5 = noon, 0.75 = 6pm).
        float t = _currentTimeOfDay / 24f;

        // elevation: -1 at midnight, 0 at dawn/dusk, +1 at noon.
        // Derivation: elevation = -cos(2π·t)
        //   t=0   → -cos(0)   = -1  (midnight)
        //   t=0.25 → -cos(π/2) = 0  (dawn)
        //   t=0.5  → -cos(π)   = 1  (noon)
        //   t=0.75 → -cos(3π/2)= 0  (dusk)
        float elevation = -Mathf.Cos(t * 2f * Mathf.PI);
        float sunHeight = Mathf.Clamp01(elevation);   // 0 at night, 1 at noon

        if (directionalLight != null) {
            // X angle: -90 at midnight (below horizon), 0 at dawn, 90 at noon, 180 at dusk.
            float xAngle = (t - 0.25f) * 360f;
            directionalLight.transform.rotation = Quaternion.Euler(xAngle, _sunAzimuth, 0f);

            // Warm orange near horizon, white at noon.
            directionalLight.color     = Color.Lerp(new Color(1f, 0.5f, 0.2f), Color.white, sunHeight);
            directionalLight.intensity = sunHeight * _maxLightIntensity;
        }

        RenderSettings.ambientIntensity = sunHeight * _maxAmbientIntensity;
    }

    // ─────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────

    private Color ResolveColor(string preset, string keyR, string keyG, string keyB, Color fallback) {
        if (preset == "gray")  return new Color(0.5f, 0.5f, 0.5f);
        if (preset == "ocean") return new Color(0.5f, 0.7f, 0.9f);
        float r = WorldLoadingController.GetParamFloat(keyR, fallback.r);
        float g = WorldLoadingController.GetParamFloat(keyG, fallback.g);
        float b = WorldLoadingController.GetParamFloat(keyB, fallback.b);
        return new Color(r, g, b);
    }

    private static FogMode ParseFogMode(string s) => s switch {
        "linear"                => FogMode.Linear,
        "exponential"           => FogMode.Exponential,
        _                       => FogMode.ExponentialSquared
    };

    private static string FogModeToString(FogMode m) => m switch {
        FogMode.Linear          => "linear",
        FogMode.Exponential     => "exponential",
        _                       => "exponential_squared"
    };
}
