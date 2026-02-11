using UnityEngine;

[DefaultExecutionOrder(11010)]
public class HandThermalDriver : MonoBehaviour
{

    public HandTextureDriver handDriver;
    public SkinnedMeshRenderer handRenderer;

    public Material thermalMaterial;

    Material[] _materials;
    Material   _thermalInstance;

    Vector3 _smoothedCenter;    // World-space center of heat
    bool    _hasSmoothedCenter; // Tracking initialized
    float   _currentIntensity;  // Current heat intensity
    float   _smoothedTemp = 0.5f; // Temperature 0=cold, 0.5=neutral, 1=hot

    static readonly int ID_HeatCenterWS   = Shader.PropertyToID("_HeatCenterWS");
    static readonly int ID_HeatRadius     = Shader.PropertyToID("_HeatRadius");
    static readonly int ID_HeatIntensity  = Shader.PropertyToID("_HeatIntensity");
    static readonly int ID_Temp01         = Shader.PropertyToID("_Temp01");
    static readonly int ID_GlowCoverage   = Shader.PropertyToID("_GlowCoverage");
    static readonly int ID_ColdColor      = Shader.PropertyToID("_ColdColor");
    static readonly int ID_HotColor       = Shader.PropertyToID("_HotColor");
    static readonly int ID_HotTintStrength  = Shader.PropertyToID("_HotTintStrength");
    static readonly int ID_ColdTintStrength = Shader.PropertyToID("_ColdTintStrength");

    HapticsGlobalData.ThermalVisualSettings Cfg
    {
        get { return HapticsGlobalData.Instance.thermalVisual; }
    }

    void Awake()
    {
        if (!HapticsGlobalData.Instance)
        {
            Debug.LogError("[HandThermalDriver] No HapticsGlobalData.Instance in scene.");
            enabled = false;
            return;
        }

        if (!handRenderer) handRenderer = GetComponent<SkinnedMeshRenderer>();
        if (!handRenderer)
        {
            Debug.LogError("[HandThermalDriver] No SkinnedMeshRenderer assigned.");
            enabled = false;
            return;
        }

        _materials = handRenderer.materials;
        if (_materials == null || _materials.Length == 0)
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

        _thermalInstance = Instantiate(thermalMaterial);
        _materials[0] = _thermalInstance;
        handRenderer.materials = _materials;
    }

    void Update()
    {
        if (!handDriver || _thermalInstance == null || HapticsGlobalData.Instance == null)
            return;

        var cfg = Cfg;
        float dt = Mathf.Max(Time.deltaTime, 1e-5f);

        bool hasSurface = handDriver.IsTouching && handDriver.CurrentSurface != null;
        SurfaceType surfaceType = hasSurface
            ? handDriver.CurrentSurface.surfaceType
            : SurfaceType.Neutral;

        bool isHot      = (surfaceType == SurfaceType.Hot);
        bool isCold     = (surfaceType == SurfaceType.Cold);
        bool hasThermal = isHot || isCold;

        // Temperature mapping: 0=cold, 0.5=neutral, 1=hot
        float targetTemp;
        if (!hasThermal)
            targetTemp = 0.5f;
        else
            targetTemp = isHot ? 1.0f : 0.0f;

        float tempLerp = 1f - Mathf.Exp(-cfg.tempLerpSpeed * dt);
        _smoothedTemp = Mathf.Lerp(_smoothedTemp, targetTemp, tempLerp);

        // Contact coverage scales thermal effects
        float coverage = Mathf.Clamp01(handDriver.ContactCoverage01);

        // Thermal effect scales from configuration
        float radiusScale   = Mathf.Lerp(cfg.radiusByCoverage.x,    cfg.radiusByCoverage.y,    coverage);
        float intensScale   = Mathf.Lerp(cfg.intensityByCoverage.x, cfg.intensityByCoverage.y, coverage);
        float glowCoverage  = cfg.glowCoverage;
        float hotTintStrength  = cfg.hotTintStrength;
        float coldTintStrength = cfg.coldTintStrength;

        if (hasThermal)
        {
            if (isHot)
            {
                intensScale   *= cfg.hotIntensityMultiplier;
                radiusScale   *= cfg.hotRadiusMultiplier;
                glowCoverage   = cfg.hotGlowCoverage;
            }
            else // cold
            {
                intensScale   *= cfg.coldIntensityMultiplier;
                radiusScale   *= cfg.coldRadiusMultiplier;
                glowCoverage   = cfg.coldGlowCoverage;
            }
        }

        float targetIntensity = hasThermal
            ? cfg.baseHeatIntensity * intensScale
            : 0f;

        float intenLerp = 1f - Mathf.Exp(-cfg.intensityFadeSpeed * dt);
        _currentIntensity = Mathf.Lerp(_currentIntensity, targetIntensity, intenLerp);

        // Contact center position
        Vector3 centerWS = handDriver.ContactPoint;

        if ((centerWS == Vector3.zero && !hasSurface))
        {
            // Fallback if no contact data available
            centerWS = handRenderer.bounds.center;
        }

        if (!_hasSmoothedCenter)
        {
            _smoothedCenter = centerWS;
            _hasSmoothedCenter = true;
        }
        else
        {
            float cLerp = 1f - Mathf.Exp(-cfg.centerLerpSpeed * dt);
            _smoothedCenter = Vector3.Lerp(_smoothedCenter, centerWS, cLerp);
        }

        // Push parameters to shader
        float dynamicRadius = cfg.baseHeatRadius * radiusScale;

        _thermalInstance.SetVector(ID_HeatCenterWS, _smoothedCenter);
        _thermalInstance.SetFloat(ID_HeatRadius, dynamicRadius);
        _thermalInstance.SetFloat(ID_HeatIntensity, _currentIntensity);
        _thermalInstance.SetFloat(ID_Temp01, _smoothedTemp);
        _thermalInstance.SetFloat(ID_GlowCoverage, glowCoverage);

        // Apply colors and tint from configuration
        _thermalInstance.SetColor(ID_HotColor,  cfg.hotColor);
        _thermalInstance.SetColor(ID_ColdColor, cfg.coldColor);
        _thermalInstance.SetFloat(ID_HotTintStrength,  hotTintStrength);
        _thermalInstance.SetFloat(ID_ColdTintStrength, coldTintStrength);
    }
}
