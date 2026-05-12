using UnityEngine;
using System;

[DefaultExecutionOrder(-100)]
public class HapticsGlobalData : MonoBehaviour
{
    public static HapticsGlobalData Instance { get; private set; }

    [Header("Hand telemetry (optional)")]
    [Tooltip("Assign left/right channels once here. Hand components set HandTelemetrySide to Left or Right.")]
    public HandTelemetryChannel leftHandTelemetryChannel;
    public HandTelemetryChannel rightHandTelemetryChannel;

    readonly HandTelemetryChannel[] _bothHandTelemetryChannels = new HandTelemetryChannel[2];

    public enum ThermalTestKind { Hot, Cold }

    [Serializable]
    public struct VisualSettings
    {
        // Contact and slide detection
        public float slideSpeedThresholdEnter;
        public float slideSpeedThresholdExit;
        [Range(0,1)] public float defaultRoughness;
        public float roughnessSmoothingSeconds;

        // Rough regime: lag + tremor
        public float latencyMsAt1;
        public float lagGainAt1;
        public float maxLagAt1;
        public float tremorAmpAt1;
        public float tremorAmpPerSpeed;
        public float tremorAmpMax;
        public Vector2 tremorHzRange;

        // Glide regime: smooth sliding motion
        public float glideAccel;
        public float glideFrictionAt0;
        public float glideFrictionNear05;
        public float glideMaxOffsetAt0;
        public float glideDeadzoneSpeed;
        public float glideReturnRate;

        // Global visual parameters
        public float maxEffectOffset;
        public float velocityLowpassHz;
    }

    [Serializable]
    public struct AudioSettings
    {
        // Overall gain and envelope
        public float masterGain;
        public float speedRef;
        public float attackSmoothSec;
        public float releaseSmoothSec;

        // Smooth layer: volume varies by roughness
        public Vector2 smoothVolByR;
        public Vector2 lowpassHzByR;
        public Vector2 lowpassHzBySpeed;

        // Glide amplitude modulation
        public Vector2 glideAMHz;
        [Range(0,1)] public float glideAMDepth;
        public float smoothLayerLowpassHz;

        // Rough layer: grit and jitter
        public Vector2 roughVolByR;
        public Vector2 jitterHzByR;
        [Range(0,0.5f)] public float jitterDepth;
        public Vector2 roughLowpassHz;
        [Range(0,2)] public float roughDriveAt1;

        // Grain accent impulses
        public bool   enableGrainAccents;
        public Vector2 grainRateHz;
        public Vector2 grainTauSec;
        public Vector2 grainBandHz;
        public Vector2 grainBandwidthHz;
        [Range(0,1)] public float grainLevel;

        // EQ: bass and mid emphasis
        [Range(0,1)] public float bassBoost;
        [Range(0,1)] public float midBoost;
        public float bassFreq;
        public float midFreq;

        // Control gating and update rate
        public float playSpeedThreshold;
        public float controlRateHz;

        // Impact click on contact
        public bool  impactClick;
        public float impactMaxHz;
        public float impactTauSec;
        public float impactLevel;
    }

    [Serializable]
    public struct ThermalAudioSettings
    {
        // Envelope
        [Range(0,1)] public float masterGain;
        public float attackSec;
        public float releaseSec;

        // Noise filtering for hot/cold perception
        public float hotNoiseCutoffHz;
        public float coldNoiseCutoffHz;
        public float coldLfoHz;

        // Speed-based gain modulation
        public float speedRef;
        public float minSpeedGain;
        public float maxSpeedGain;

        // Update rate
        public float controlRateHz;
    }

[Serializable]
public struct ThermalVisualSettings
{
    // Base glow parameters
    public float  baseHeatRadius;
    public float  baseHeatIntensity;
    [Range(0f, 1f)] public float glowCoverage;
    public Vector2 radiusByCoverage;
    public Vector2 intensityByCoverage;

    // Hot and cold tuning
    public Color  hotColor;
    public Color  coldColor;
    public float  hotIntensityMultiplier;
    public float  coldIntensityMultiplier;
    public float  hotRadiusMultiplier;
    public float  coldRadiusMultiplier;
    [Range(0f, 1f)] public float hotGlowCoverage;
    [Range(0f, 1f)] public float coldGlowCoverage;
    [Range(0f, 3f)] public float hotTintStrength;
    [Range(0f, 3f)] public float coldTintStrength;

    // Smoothing and blending
    public float centerLerpSpeed;
    public float intensityFadeSpeed;
    public float tempLerpSpeed;
}



    [Header("Global Visual Settings")]
    public VisualSettings visual = new VisualSettings {
        slideSpeedThresholdEnter = 0.02f,
        slideSpeedThresholdExit  = 0.015f,
        defaultRoughness = 0.5f,
        roughnessSmoothingSeconds = 0.12f,

        latencyMsAt1 = 60f,
        lagGainAt1 = 0.08f,
        maxLagAt1 = 0.015f,
        tremorAmpAt1 = 0.0025f,
        tremorAmpPerSpeed = 0.0010f,
        tremorAmpMax = 0.0030f,
        tremorHzRange = new Vector2(14f, 24f),

        glideAccel = 12f,
        glideFrictionAt0 = 0.2f,
        glideFrictionNear05 = 2.0f,
        glideMaxOffsetAt0 = 0.08f,
        glideDeadzoneSpeed = 0.01f,
        glideReturnRate = 10f,

        maxEffectOffset = 0.06f,
        velocityLowpassHz = 7f
    };

    [Header("Global Texture Audio")]
    public AudioSettings audio = new AudioSettings {
        masterGain = 0.24f,
        speedRef = 0.70f,
        attackSmoothSec = 0.02f,
        releaseSmoothSec = 0.08f,

        // Smooth: dark fabric / wind body; rough: brighter band.
        smoothVolByR = new Vector2(0.48f, 0.58f),
        lowpassHzByR = new Vector2(520f, 7600f),

        lowpassHzBySpeed = new Vector2(0f, 260f),
        glideAMHz = new Vector2(0.06f, 0.30f),
        glideAMDepth = 0.03f,
        smoothLayerLowpassHz = 880f,

        roughVolByR = new Vector2(0.0f, 0.78f),
        jitterHzByR = new Vector2(10f, 30f),
        jitterDepth = 0.12f,
        roughLowpassHz = new Vector2(3500f, 9800f),
        roughDriveAt1 = 0.24f,

        enableGrainAccents = true,
        grainRateHz = new Vector2(32f, 130f),
        grainTauSec = new Vector2(0.004f, 0.009f),
        grainBandHz = new Vector2(2500f, 6000f),
        grainBandwidthHz = new Vector2(1200f, 3200f),
        grainLevel = 0.16f,

        bassBoost = 0.34f,
        midBoost = 0.24f,
        bassFreq = 250f,
        midFreq = 1200f,

        playSpeedThreshold = 0.008f,
        controlRateHz = 40f,

        impactClick = true,
        impactMaxHz = 6000f,
        impactTauSec = 0.012f,
        impactLevel = 0.12f
    };

    [Header("Global Thermal Audio")]
    public ThermalAudioSettings thermalAudio = new ThermalAudioSettings {
        masterGain       = 0.24f,
        attackSec        = 0.02f,
        releaseSec       = 0.10f,
        hotNoiseCutoffHz = 7600f,
        coldNoiseCutoffHz= 2500f,
        coldLfoHz        = 6f,
        speedRef         = 0.8f,
        minSpeedGain     = 0.75f,
        maxSpeedGain     = 1.0f,
        controlRateHz    = 40f
    };

    [Header("Global Thermal Visual")]
    public ThermalVisualSettings thermalVisual = new ThermalVisualSettings {
        baseHeatRadius      = 0.04f,
        baseHeatIntensity   = 1.0f,
        glowCoverage        = 0.7f,
        radiusByCoverage    = new Vector2(0.6f, 1.4f),
        intensityByCoverage = new Vector2(0.5f, 1.6f),

        // Hot and cold colors
        hotColor  = new Color(1.0f, 0.25f, 0.15f, 1f),   // warm red/orange
        coldColor = new Color(0.20f, 0.60f, 1.00f, 1f),  // cold blue

        // Make hot stronger by default
        hotIntensityMultiplier = 3.0f,
        coldIntensityMultiplier = 1.0f,

        hotRadiusMultiplier = 1.8f,
        coldRadiusMultiplier = 1.0f,

        hotGlowCoverage = 0.95f,
        coldGlowCoverage = 0.70f,

        // Skin tint strength toward hot/cold
        hotTintStrength  = 2.0f,
        coldTintStrength = 1.0f,

        centerLerpSpeed    = 20f,
        intensityFadeSpeed = 10f,
        tempLerpSpeed      = 10f
    };
    public event Action OnGlobalsChanged;

    /// <summary>Channel for the given hand; may be null if not assigned.</summary>
    public HandTelemetryChannel GetHandTelemetryChannel(HandTelemetrySide side)
    {
        return side == HandTelemetrySide.Left ? leftHandTelemetryChannel : rightHandTelemetryChannel;
    }

    /// <summary>Fixed [left, right] order for merge helpers; null entries allowed.</summary>
    public HandTelemetryChannel[] GetBothHandTelemetryChannels()
    {
        RefreshHandTelemetryBothCache();
        return _bothHandTelemetryChannels;
    }

    void RefreshHandTelemetryBothCache()
    {
        _bothHandTelemetryChannels[0] = leftHandTelemetryChannel;
        _bothHandTelemetryChannels[1] = rightHandTelemetryChannel;
    }

    void Awake()
    {
        if (Instance && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        RefreshHandTelemetryBothCache();
    }

    void OnValidate()
    {
        RefreshHandTelemetryBothCache();
        OnGlobalsChanged?.Invoke();
    }
}
