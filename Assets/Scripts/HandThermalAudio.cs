using UnityEngine;

[RequireComponent(typeof(AudioSource))]
[DefaultExecutionOrder(11021)]
public class HandThermalAudio : MonoBehaviour
{
    public HapticsGlobalData globals;

    public bool useGlobalTelemetry = false;
    public HandTextureDriver driver;              // Local hand (default)
    public HandTextureDriver[] aggregateDrivers;  // Multiple hands when useGlobalTelemetry = true

    // Internal state
    AudioSource _src;
    int _sr;

    HapticsGlobalData.ThermalAudioSettings _settings;

    float _spd;
    bool  _touchNow, _touchPrev;
    SurfaceType _surfaceType;

    bool _thermalHotActive;
    bool _thermalColdActive;

    float _ctlTimer;
    float _time;

    // Global thermal contact envelope
    float _onEnv;

    // Per-mode envelopes
    float _thermalEnvHot, _thermalEnvCold;

    // Hot filter for sizzling high-pass
    float _hotLp;
    float _hotA;

    // Cold bands: glacier rumble + ice detail
    float _coldLpLow;
    float _coldLpHigh;
    float _coldALow;
    float _coldAHigh;

    // Crack noise high-pass filter
    float _crackHp;
    float _crackHpA;

    // Cold crack events: multi-part ice-break sounds
    const int MaxSubCracks = 3;
    struct SubCrack
    {
        public bool on;
        public float t;
        public float startDelay;
        public float tau;
        public float amp;
        public float color;  // Brightness 0..1
    }
    SubCrack[] _subCracks = new SubCrack[MaxSubCracks];

    float _crackTimer = 999f;  // Seconds until next crack event
    bool  _coldWasActive = false;  // Detect cold contact rising edge

    // Cold slow LFO for subtle modulation
    float _coldLfoPhase;

    // Pseudo-random generator
    uint _rng = 0xC0FFEEu;
    float White()
    {
        _rng = 1664525u * _rng + 1013904223u;
        return ((_rng >> 9) & 0x7FFFFF) / 8388607f * 2f - 1f;
    }
    float Rand01()
    {
        _rng = 1103515245u * _rng + 12345u;
        return ((_rng >> 8) & 0xFFFFFF) / 16777215f;
    }

    void Awake()
    {
        if (!globals) globals = HapticsGlobalData.Instance;
        if (!globals)
        {
            Debug.LogError("[HandThermalAudio] No HapticsGlobalData found.");
            enabled = false;
            return;
        }

        _settings = globals.thermalAudio;
        _src = GetComponent<AudioSource>();
        _src.playOnAwake = true;
        _src.loop = true;
        _sr = AudioSettings.outputSampleRate;

        if (_src.clip == null)
        {
            var silent = AudioClip.Create("ProcSilent_Thermal", 4, 1, _sr, false);
            _src.clip = silent;
        }
        if (!_src.isPlaying) _src.Play();
        RecomputeFilterCoeffs(_settings);
    }

    void OnEnable()
    {
        if (!globals) globals = HapticsGlobalData.Instance;
        if (globals != null)
            globals.OnGlobalsChanged += HandleGlobalsChanged;
    }

    void OnDisable()
    {
        if (globals != null)
            globals.OnGlobalsChanged -= HandleGlobalsChanged;
    }

    void HandleGlobalsChanged()
    {
        if (globals == null) return;
        _settings = globals.thermalAudio;
        RecomputeFilterCoeffs(_settings);
    }

    void RecomputeFilterCoeffs(in HapticsGlobalData.ThermalAudioSettings t)
    {
        float dt = 1f / Mathf.Max(1, _sr);

        // Hot LP for high-pass derivation (sizzle)
        _hotA = 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(50f, t.hotNoiseCutoffHz) * dt);

        // Cold: deep rumble + mid/high band for icy detail
        float lowCut = 140f;  // Glacier hum
        float hiCut  = Mathf.Max(1200f, t.coldNoiseCutoffHz);

        _coldALow  = 1f - Mathf.Exp(-2f * Mathf.PI * lowCut * dt);
        _coldAHigh = 1f - Mathf.Exp(-2f * Mathf.PI * hiCut  * dt);

        // Crack HP filter for sharp, broadband impulses
        float crackCut = Mathf.Clamp(t.coldNoiseCutoffHz * 1.8f, 3000f, 9500f);
        _crackHpA = 1f - Mathf.Exp(-2f * Mathf.PI * crackCut * dt);
    }

    int SurfacePriority(SurfaceType st)
    {
        switch (st)
        {
            case SurfaceType.Hot:  return 2;
            case SurfaceType.Cold: return 1;
            default:               return 0;
        }
    }

    void Update()
    {
        bool touching = false;
        float spd     = 0f;
        SurfaceType surfaceType = SurfaceType.Neutral;

        if (useGlobalTelemetry && aggregateDrivers != null && aggregateDrivers.Length > 0)
        {
            int bestPriority = -1;

            for (int i = 0; i < aggregateDrivers.Length; i++)
            {
                var d = aggregateDrivers[i];
                if (!d) continue;

                touching |= d.IsTouching;
                spd = Mathf.Max(spd, d.TangentialSpeed);

                if (d.IsTouching && d.CurrentSurface != null)
                {
                    var ttype = d.CurrentSurface.surfaceType;
                    int p = SurfacePriority(ttype);
                    if (p > bestPriority)
                    {
                        bestPriority = p;
                        surfaceType = ttype;
                    }
                }
            }
        }
        else
        {
            if (!driver) return;
            touching = driver.IsTouching;
            spd = driver.TangentialSpeed;
            if (driver.IsTouching && driver.CurrentSurface != null)
                surfaceType = driver.CurrentSurface.surfaceType;
            else
                surfaceType = SurfaceType.Neutral;
        }

        _touchPrev    = _touchNow;
        _touchNow     = touching;
        _surfaceType  = surfaceType;
        _spd          = spd;
        _thermalHotActive  = touching && surfaceType == SurfaceType.Hot;
        _thermalColdActive = touching && surfaceType == SurfaceType.Cold;
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        var t = _settings;
        int n  = data.Length / channels;
        float dt = 1f / Mathf.Max(1, _sr);

        for (int i = 0; i < n; i++)
        {
            _time     += dt;
            _ctlTimer += dt;

            if (_ctlTimer >= (1f / Mathf.Max(10f, t.controlRateHz)))
            {
                _ctlTimer = 0f;
                RecomputeFilterCoeffs(t);
            }

            bool activeThermal = _thermalHotActive || _thermalColdActive;

            // Thermal contact envelope (amplitude gate)
            float tauGlobal   = activeThermal ? t.attackSec : t.releaseSec;
            float aEnvGlobal  = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, tauGlobal));
            _onEnv = Mathf.Lerp(_onEnv, activeThermal ? 1f : 0f, aEnvGlobal);

            // Per-thermal envelopes
            float targetHot = _thermalHotActive ? 1f : 0f;
            float tauHot    = (targetHot > _thermalEnvHot) ? t.attackSec : t.releaseSec;
            float aHot      = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, tauHot));
            _thermalEnvHot  = Mathf.Lerp(_thermalEnvHot, targetHot, aHot);

            float targetCold = _thermalColdActive ? 1f : 0f;
            float tauCold    = (targetCold > _thermalEnvCold) ? t.attackSec : t.releaseSec;
            float aCold      = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, tauCold));
            _thermalEnvCold  = Mathf.Lerp(_thermalEnvCold, targetCold, aCold);

            // Crack event timing: trigger on cold contact edge
            bool coldNow = _thermalColdActive;
            if (coldNow && !_coldWasActive)
            {
                float u0 = Rand01();
                _crackTimer = Mathf.Lerp(0.5f, 1.2f, u0);
            }
            if (!coldNow)
                _crackTimer = 999f;
            _coldWasActive = coldNow;

            float sample = 0f;

            if (_onEnv > 1e-3f)
            {
                float noise = White();

                // Hot: bright sizzling high-frequency hiss
                _hotLp += _hotA * (noise - _hotLp);
                float hotHp     = noise - _hotLp;
                float hotSample = hotHp * _thermalEnvHot;

                // Cold: glacier rumble + ice cracks
                _coldLpLow  += _coldALow  * (noise - _coldLpLow);
                _coldLpHigh += _coldAHigh * (noise - _coldLpHigh);
                float coldLow = _coldLpLow;
                float coldHi  = noise - _coldLpHigh;
                float coldContact = _thermalEnvCold;

                // Crack event generation
                if (coldNow && coldContact > 1e-3f)
                {
                    _crackTimer -= dt;
                    if (_crackTimer <= 0f)
                    {
                        int subCount = 2 + (int)(Rand01() * 3f);  // 2–4 sub-cracks
                        if (subCount > MaxSubCracks) subCount = MaxSubCracks;

                        for (int scIdx = 0; scIdx < MaxSubCracks; scIdx++)
                            _subCracks[scIdx].on = false;

                        float baseSpacing = Mathf.Lerp(0.008f, 0.025f, Rand01());

                        for (int j = 0; j < subCount; j++)
                        {
                            float rank   = (subCount <= 1) ? 0f : (j / (float)(subCount - 1));
                            float ampBase= Mathf.Lerp(1.2f, 0.55f, rank);
                            float ampJit = Mathf.Lerp(0.85f, 1.5f, Rand01());
                            float amp    = ampBase * ampJit * Mathf.Lerp(0.70f, 1.2f, coldContact);

                            float tauMin = (j == 0) ? 0.002f : 0.006f;
                            float tauMax = (j == 0) ? 0.010f : 0.028f;
                            float tau    = Mathf.Lerp(tauMin, tauMax, Rand01());

                            float delayJitter = Mathf.Lerp(0.7f, 1.3f, Rand01());
                            float startDelay  = baseSpacing * j * delayJitter;

                            float color = Mathf.Lerp(0.6f, 1.0f, Rand01());

                            _subCracks[j] = new SubCrack {
                                on         = true,
                                t          = 0f,
                                startDelay = startDelay,
                                tau        = tau,
                                amp        = amp,
                                color      = color
                            };
                        }

                        float u = Rand01();
                        float minInt = Mathf.Lerp(0.8f, 0.5f, coldContact);
                        float maxInt = Mathf.Lerp(1.5f, 0.9f, coldContact);
                        _crackTimer = Mathf.Lerp(minInt, maxInt, u);
                    }
                }

                // Very slow LFO for subtle temperature modulation
                float lfoFreq = Mathf.Clamp(t.coldLfoHz, 0.10f, 0.80f);
                _coldLfoPhase += 2f * Mathf.PI * lfoFreq * dt;
                if (_coldLfoPhase > 2f * Mathf.PI) _coldLfoPhase -= 2f * Mathf.PI;
                float coldLfo = 0.92f + 0.08f * Mathf.Sin(_coldLfoPhase);

                float coldBase = (coldLow * 0.9f + coldHi * 0.1f) * coldLfo;

                float crackMix = 0f;
                if (coldContact > 1e-3f)
                {
                    float nCrack = White();
                    _crackHp += _crackHpA * (nCrack - _crackHp);
                    float crackNoiseBase = nCrack - _crackHp;
                    float crackNoise     = crackNoiseBase * 0.8f + coldHi * 0.2f;

                    for (int c = 0; c < MaxSubCracks; c++)
                    {
                        if (!_subCracks[c].on) continue;
                        var sc = _subCracks[c];
                        sc.t += dt;

                        if (sc.t < sc.startDelay)
                        {
                            _subCracks[c] = sc;
                            continue;
                        }

                        float localT = sc.t - sc.startDelay;
                        float env = Mathf.Exp(-localT / Mathf.Max(0.001f, sc.tau));
                        if (env < 1e-3f)
                        {
                            sc.on = false;
                            _subCracks[c] = sc;
                            continue;
                        }

                        float color = sc.color;
                        float bright = Mathf.Lerp(0.7f, 1.0f, color);
                        float dark   = 1.0f - 0.5f * color;
                        float shaped = crackNoise * bright + coldLow * dark * 0.2f;
                        crackMix += shaped * env * sc.amp;
                        _subCracks[c] = sc;
                    }
                }

                float coldSample = (coldBase * 0.70f + crackMix * 0.85f) * coldContact * 0.80f;

                float spd01   = Mathf.Clamp01(_spd / Mathf.Max(0.001f, t.speedRef));
                float spdGain = Mathf.Lerp(t.minSpeedGain, t.maxSpeedGain, spd01);
                sample = (hotSample + coldSample) * t.masterGain * _onEnv * spdGain;
            }

            for (int ch = 0; ch < channels; ch++)
                data[i * channels + ch] += sample;
        }
    }
}
