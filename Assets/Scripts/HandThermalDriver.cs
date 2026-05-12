using UnityEngine;
using UnityEngine.Serialization;

[DefaultExecutionOrder(11015)]
public class HandThermalDriver : MonoBehaviour
{
    const float Epsilon = 1e-5f;

    [Header("References")]
    public SkinnedMeshRenderer handRenderer;
    public Material thermalMaterial;

    [Header("Telemetry")]
    [Tooltip("Left or right")]
    public HandTelemetrySide handTelemetrySide = HandTelemetrySide.Left;
    [SerializeField, FormerlySerializedAs("telemetry"), Tooltip("Override; empty = globals.")]
    HandTelemetryChannel _telemetryChannelOverride;

    [Header("Telemetry — legacy")]
    [FormerlySerializedAs("handDriver"), Tooltip("If no channel")]
    public HandTextureDriver textureHandDriver;

    Material[] _rendererMaterials;
    Material _thermalMaterialInstance;

    Vector3 _smoothedHeatCenterWorld;
    bool _hasSmoothedHeatCenter;
    float _currentHeatIntensity;
    float _smoothedHeatRadius;
    float _smoothedTemperature01 = 0.5f;

    static readonly int ShaderIdHeatCenterWorld = Shader.PropertyToID("_HeatCenterWS");
    static readonly int ShaderIdHeatRadius = Shader.PropertyToID("_HeatRadius");
    static readonly int ShaderIdHeatIntensity = Shader.PropertyToID("_HeatIntensity");
    static readonly int ShaderIdTemperature01 = Shader.PropertyToID("_Temp01");
    static readonly int ShaderIdGlowCoverage = Shader.PropertyToID("_GlowCoverage");
    static readonly int ShaderIdColdColor = Shader.PropertyToID("_ColdColor");
    static readonly int ShaderIdHotColor = Shader.PropertyToID("_HotColor");
    static readonly int ShaderIdHotTintStrength = Shader.PropertyToID("_HotTintStrength");
    static readonly int ShaderIdColdTintStrength = Shader.PropertyToID("_ColdTintStrength");

    void Awake()
    {
        if (!HapticsGlobalData.Instance)
        {
            Debug.LogError("[HandThermalDriver] No HapticsGlobalData.Instance in scene.");
            enabled = false;
            return;
        }

        if (!handRenderer)
            handRenderer = GetComponent<SkinnedMeshRenderer>();
        if (!handRenderer)
        {
            Debug.LogError("[HandThermalDriver] No SkinnedMeshRenderer assigned.");
            enabled = false;
            return;
        }

        _rendererMaterials = handRenderer.materials;
        if (_rendererMaterials == null || _rendererMaterials.Length == 0)
        {
            Debug.LogError("[HandThermalDriver] Renderer has no materials.");
            enabled = false;
            return;
        }

        if (!thermalMaterial)
        {
            Debug.LogError("[HandThermalDriver] Thermal material not assigned.");
            enabled = false;
            return;
        }

        _thermalMaterialInstance = Instantiate(thermalMaterial);
        _rendererMaterials[0] = _thermalMaterialInstance;
        handRenderer.materials = _rendererMaterials;
    }

    void LateUpdate()
    {
        if (_thermalMaterialInstance == null || HapticsGlobalData.Instance == null)
            return;

        HandTelemetrySnapshot telemetrySnapshot;
        var channel = _telemetryChannelOverride;
        if (!channel && HapticsGlobalData.Instance != null)
            channel = HapticsGlobalData.Instance.GetHandTelemetryChannel(handTelemetrySide);

        if (channel)
            telemetrySnapshot = channel.Latest;
        else if (textureHandDriver)
            telemetrySnapshot = HandTelemetrySnapshot.FromHandTextureDriver(textureHandDriver);
        else
            return;

        var visualSettings = HapticsGlobalData.Instance.thermalVisual;
        float deltaTime = Mathf.Max(Time.deltaTime, Epsilon);

        float contactEnv01 = Mathf.Clamp01(telemetrySnapshot.ContactEnvelope01);
        bool hasSurface = contactEnv01 > 1e-3f && telemetrySnapshot.Surface;
        SurfaceType surfaceType = hasSurface ? telemetrySnapshot.SurfaceType : SurfaceType.Neutral;

        bool isHot = surfaceType == SurfaceType.Hot;
        bool isCold = surfaceType == SurfaceType.Cold;
        bool hasThermal = isHot || isCold;

        float targetTemperature01 = hasThermal ? (isHot ? 1f : 0f) : 0.5f;
        float temperatureBlend = 1f - Mathf.Exp(-visualSettings.tempLerpSpeed * deltaTime);
        _smoothedTemperature01 = Mathf.Lerp(_smoothedTemperature01, targetTemperature01, temperatureBlend);

        float coverage01 = Mathf.Clamp01(telemetrySnapshot.ContactCoverage01);

        float radiusScale = Mathf.Lerp(visualSettings.radiusByCoverage.x, visualSettings.radiusByCoverage.y, coverage01);
        float intensityScale = Mathf.Lerp(visualSettings.intensityByCoverage.x, visualSettings.intensityByCoverage.y, coverage01);

        float glowCoverage = Mathf.Lerp(0.25f, visualSettings.glowCoverage, coverage01);
        float hotTintStrength = visualSettings.hotTintStrength;
        float coldTintStrength = visualSettings.coldTintStrength;

        if (hasThermal)
        {
            if (isHot)
            {
                intensityScale *= visualSettings.hotIntensityMultiplier;
                radiusScale *= visualSettings.hotRadiusMultiplier;
                glowCoverage = visualSettings.hotGlowCoverage;
            }
            else
            {
                intensityScale *= visualSettings.coldIntensityMultiplier;
                radiusScale *= visualSettings.coldRadiusMultiplier;
                glowCoverage = visualSettings.coldGlowCoverage;
            }
        }

        float targetIntensity = hasThermal ? visualSettings.baseHeatIntensity * intensityScale * contactEnv01 : 0f;
        float intensityBlend = 1f - Mathf.Exp(-visualSettings.intensityFadeSpeed * deltaTime);
        _currentHeatIntensity = Mathf.Lerp(_currentHeatIntensity, targetIntensity, intensityBlend);

        Vector3 heatCenterWorld = telemetrySnapshot.ContactPoint;
        Vector3 boundsCenterWorld = handRenderer.bounds.center;
        if (heatCenterWorld == Vector3.zero && !hasSurface)
            heatCenterWorld = boundsCenterWorld;
        else
            heatCenterWorld = Vector3.Lerp(heatCenterWorld, boundsCenterWorld, Mathf.Clamp01(coverage01 * 0.65f));

        if (!_hasSmoothedHeatCenter)
        {
            _smoothedHeatCenterWorld = heatCenterWorld;
            _hasSmoothedHeatCenter = true;
        }
        else
        {
            float centerBlend = 1f - Mathf.Exp(-visualSettings.centerLerpSpeed * deltaTime);
            _smoothedHeatCenterWorld = Vector3.Lerp(_smoothedHeatCenterWorld, heatCenterWorld, centerBlend);
        }

        float patchBoost = Mathf.Lerp(1.0f, 2.2f, Mathf.SmoothStep(0.25f, 1.0f, coverage01));

        float grow01 = Mathf.SmoothStep(0.0f, 1.0f, contactEnv01);
        float dynamicRadiusTarget = visualSettings.baseHeatRadius * radiusScale * patchBoost * Mathf.Lerp(0.35f, 1.0f, grow01);
        _smoothedHeatRadius = Mathf.Lerp(_smoothedHeatRadius, dynamicRadiusTarget, intensityBlend);

        _thermalMaterialInstance.SetVector(ShaderIdHeatCenterWorld, _smoothedHeatCenterWorld);
        _thermalMaterialInstance.SetFloat(ShaderIdHeatRadius, _smoothedHeatRadius);
        _thermalMaterialInstance.SetFloat(ShaderIdHeatIntensity, _currentHeatIntensity);
        _thermalMaterialInstance.SetFloat(ShaderIdTemperature01, _smoothedTemperature01);
        _thermalMaterialInstance.SetFloat(ShaderIdGlowCoverage, glowCoverage);
        _thermalMaterialInstance.SetColor(ShaderIdHotColor, visualSettings.hotColor);
        _thermalMaterialInstance.SetColor(ShaderIdColdColor, visualSettings.coldColor);
        _thermalMaterialInstance.SetFloat(ShaderIdHotTintStrength, hotTintStrength);
        _thermalMaterialInstance.SetFloat(ShaderIdColdTintStrength, coldTintStrength);
    }
}
