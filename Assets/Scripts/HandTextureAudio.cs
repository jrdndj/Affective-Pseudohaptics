using UnityEngine;
using System;

[RequireComponent(typeof(AudioSource))]
[DefaultExecutionOrder(11020)]
public class HandTextureAudio : MonoBehaviour
{
    public HapticsGlobalData globals;

    public bool useGlobalTelemetry = false;
    public HandTextureDriver driver;              // Local hand (default)
    public HandTextureDriver[] aggregateDrivers;  // Multiple hands when useGlobalTelemetry = true

    // Internal state
    AudioSource _src;
    int _sr;
    HapticsGlobalData.AudioSettings _settings;
    float _r, _spd;
    bool  _active;
    bool  _touchNow, _touchPrev;
    SurfaceType _surfaceType;
    bool _textureSurface;  // Rough/Smooth only

    // Control timers
    float _ctlTimer;

    // Smooth layer: low-pass filtering + AM
    float _lpSmooth, _lpA_Smooth;
    float _amPhase;
    float _smoothLp1, _smoothLp2;

    // Rough layer: jitter modulation
    float _lpRough, _lpA_Rough;
    float _jitterHold, _jitterTimer, _jitterInterval;

    // Grain accents: impulse bursts
    const int MaxGrains = 16;
    struct Grain
    {
        public bool on;
        public float t, tau, freq, bw, y1, y2, g;
    }
    Grain[] _grains = new Grain[MaxGrains];
    float _nextGrainTime;
    float _time;
    float _grainRate;

    // Global contact envelope
    float _onEnv;

    // Impact transient on contact
    float _impactEnv;
    float _impactHz;

    // Bass/Mid enhancement filters
    float _bassLp, _midLp, _midHp;
    float _bassA, _midLpA, _midHpA;

    // Pseudo-random generator
    uint _rng = 0x1234567u;
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
            Debug.LogError("[HandTextureAudio] No HapticsGlobalData found.");
            enabled = false;
            return;
        }

        _settings = globals.audio;
        _src = GetComponent<AudioSource>();
        _src.playOnAwake = true;
        _src.loop = true;

        _sr = AudioSettings.outputSampleRate;

        if (_src.clip == null)
        {
            var silent = AudioClip.Create("ProcSilence_Texture", 4, 1, _sr, false);
            _src.clip = silent;
        }
        if (!_src.isPlaying) _src.Play();

        var a = _settings;
        _jitterInterval = 1f / Mathf.Max(1f, a.jitterHzByR.x);
        _jitterTimer    = _jitterInterval;
        _jitterHold     = 1f;

        CalculateToneCoefficients(a);
    }

    void OnEnable()
    {
        if (!globals) globals = HapticsGlobalData.Instance;
        if (globals != null)
        {
            globals.OnGlobalsChanged += HandleGlobalsChanged;
        }
    }

    void OnDisable()
    {
        if (globals != null)
        {
            globals.OnGlobalsChanged -= HandleGlobalsChanged;
        }
    }

    void HandleGlobalsChanged()
    {
        if (globals == null) return;
        _settings = globals.audio;
        CalculateToneCoefficients(_settings);
    }

    void CalculateToneCoefficients(in HapticsGlobalData.AudioSettings a)
    {
        float dt = 1f / Mathf.Max(1, _sr);

        _bassA   = 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(10f, a.bassFreq) * dt);
        _midLpA  = 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(10f, a.midFreq) * dt);
        _midHpA  = 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(10f, a.midFreq * 0.5f) * dt);
    }

    int SurfacePriority(SurfaceType t)
    {
        switch (t)
        {
            case SurfaceType.Rough:  return 2;
            case SurfaceType.Smooth: return 1;
            default:                 return 0;
        }
    }

    void Update()
    {
        var a = _settings;

        bool touching = false;
        bool sliding  = false;
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
                sliding  |= d.IsSliding;
                spd       = Mathf.Max(spd, d.TangentialSpeed);

                if (d.IsTouching && d.CurrentSurface != null)
                {
                    var t = d.CurrentSurface.surfaceType;
                    int p = SurfacePriority(t);
                    if (p > bestPriority)
                    {
                        bestPriority = p;
                        surfaceType  = t;
                    }
                }
            }
        }
        else
        {
            if (!driver) return;

            touching = driver.IsTouching;
            sliding  = driver.IsSliding;
            spd      = driver.TangentialSpeed;

            if (driver.IsTouching && driver.CurrentSurface != null)
                surfaceType = driver.CurrentSurface.surfaceType;
            else
                surfaceType = SurfaceType.Neutral;
        }

        _touchPrev   = _touchNow;
        _touchNow    = touching;
        _surfaceType = surfaceType;
        _spd         = spd;

        // Discrete roughness:
        // Rough → 1, Smooth → 0, Neutral → 0.5 (but Neutral is silent)
        float rDiscrete;
        switch (surfaceType)
        {
            case SurfaceType.Rough:  rDiscrete = 1f;  break;
            case SurfaceType.Smooth: rDiscrete = 0f;  break;
            default:                 rDiscrete = 0.5f; break;
        }
        _r = rDiscrete;

        // Only Rough / Smooth drive texture audio
        _textureSurface = (surfaceType == SurfaceType.Rough || surfaceType == SurfaceType.Smooth);
        _active         = touching;

        // Impact click only on texture surfaces
        if (a.impactClick && _touchNow && !_touchPrev && _textureSurface)
        {
            _impactEnv = 1f;
            float spd01 = Mathf.Clamp01(_spd / Mathf.Max(0.001f, a.speedRef));
            _impactHz   = Mathf.Lerp(2000f, a.impactMaxHz, spd01);
        }
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        var a = _settings;
        int n = data.Length / channels;
        float dt = 1f / Mathf.Max(1, _sr);

        for (int i = 0; i < n; i++)
        {
            _time     += dt;
            _ctlTimer += dt;

            // Control-rate update
            if (_ctlTimer >= (1f / Mathf.Max(10f, a.controlRateHz)))
            {
                _ctlTimer = 0f;

                float r     = _r;
                float spd01 = Mathf.Clamp01(_spd / Mathf.Max(0.001f, a.speedRef));

                // Smooth LPF coeff
                float cutSmooth =
                    Mathf.Lerp(a.lowpassHzByR.x, a.lowpassHzByR.y, r) +
                    Mathf.Lerp(a.lowpassHzBySpeed.x, a.lowpassHzBySpeed.y, spd01) * (1f - r);
                cutSmooth = Mathf.Clamp(cutSmooth, 80f, _sr * 0.45f);
                _lpA_Smooth = 1f - Mathf.Exp(-2f * Mathf.PI * cutSmooth * dt);

                // Rough LPF coeff
                float cutRough =
                    Mathf.Lerp(a.roughLowpassHz.x, a.roughLowpassHz.y, Mathf.Max(r, spd01));
                cutRough   = Mathf.Clamp(cutRough, 200f, _sr * 0.45f);
                _lpA_Rough = 1f - Mathf.Exp(-2f * Mathf.PI * cutRough * dt);

                // Jitter
                float jHz = Mathf.Lerp(a.jitterHzByR.x, a.jitterHzByR.y, r) *
                            Mathf.Lerp(0.2f, 1f, spd01);
                jHz = Mathf.Max(1f, jHz);
                _jitterInterval = 1f / jHz;
                _jitterTimer    = Mathf.Min(_jitterTimer, _jitterInterval);

                // Recompute EQ coefficients
                CalculateToneCoefficients(a);
            }

            // Global contact envelope
            float tauGlobal  = (_active ? a.attackSmoothSec : a.releaseSmoothSec);
            float aEnvGlobal = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, tauGlobal));
            _onEnv = Mathf.Lerp(_onEnv, _active ? 1f : 0f, aEnvGlobal);

            float textureMask = _textureSurface ? 1f : 0f;

            // Smooth layer
            _lpSmooth  += _lpA_Smooth * (White() - _lpSmooth);
            _smoothLp1 += _lpA_Smooth * 0.7f * (_lpSmooth - _smoothLp1);
            _smoothLp2 += _lpA_Smooth * 0.5f * (_smoothLp1 - _smoothLp2);
            float smoothSignal = _smoothLp2;

            float spd01_now = Mathf.Clamp01(_spd / Mathf.Max(0.001f, a.speedRef));
            float amHz      = Mathf.Lerp(a.glideAMHz.x, a.glideAMHz.y, spd01_now) * (1f - _r);
            _amPhase += amHz * dt * 2f * Mathf.PI;
            if (_amPhase > Mathf.PI * 2f) _amPhase -= Mathf.PI * 2f;
            float am = 1f + a.glideAMDepth * Mathf.Sin(_amPhase);

            // Stronger smooth thanks to boosted smoothVolByR in globals
            float smoothVol =
                Mathf.Lerp(a.smoothVolByR.x, a.smoothVolByR.y, _r) * (1f - _r);
            smoothVol *= spd01_now * textureMask;

            float smoothSample = smoothSignal * am * smoothVol;

            // Rough layer
            _lpRough += _lpA_Rough * (White() - _lpRough);

            _jitterTimer += dt;
            if (_jitterTimer >= _jitterInterval)
            {
                _jitterTimer -= _jitterInterval;
                _jitterHold   = 1f + (Rand01() * 2f - 1f) * a.jitterDepth;
            }

            float drive    = 1f + _r * a.roughDriveAt1;
            float roughRaw = SoftClip(_lpRough * drive);

            float roughVol = Mathf.Lerp(a.roughVolByR.x, a.roughVolByR.y, _r);
            roughVol *= spd01_now * textureMask;

            float roughSample = roughRaw * _jitterHold * roughVol;

            // Grain accents
            float grainMix = 0f;
            if (a.enableGrainAccents)
            {
                float rGate = Mathf.SmoothStep(0f, 1f, _r) * textureMask;
                _grainRate  = Mathf.Lerp(a.grainRateHz.x, a.grainRateHz.y, spd01_now) * rGate;

                if (_grainRate > 0.01f)
                {
                    while (_time >= _nextGrainTime)
                    {
                        SpawnGrain(a, rGate, spd01_now);
                        float u   = Mathf.Max(1e-6f, Rand01());
                        float gap = -Mathf.Log(u) / _grainRate;
                        _nextGrainTime = (_nextGrainTime <= 0f ? _time : _nextGrainTime) + gap;
                    }
                }

                for (int g = 0; g < MaxGrains; g++)
                {
                    if (!_grains[g].on) continue;
                    var gr = _grains[g];
                    gr.t += dt;

                    float env = Mathf.Exp(-gr.t / gr.tau);
                    if (env < 1e-3f)
                    {
                        gr.on = false;
                        _grains[g] = gr;
                        continue;
                    }

                    float n0 = White();
                    gr.y1 += 0.12f * (n0 - gr.y1);
                    float hp = n0 - gr.y1;

                    float lpa = 2f * Mathf.PI *
                                Mathf.Clamp(gr.freq + gr.bw, 80f, _sr * 0.45f) * dt;
                    lpa     = Mathf.Clamp01(lpa);
                    gr.y2  += lpa * (hp - gr.y2);
                    float bp = gr.y2;

                    grainMix += bp * gr.g * env;
                    _grains[g] = gr;
                }
            }

            // Impact click
            float impact = 0f;
            if (_impactEnv > 1e-3f && a.impactClick && _textureSurface)
            {
                _impactEnv *= Mathf.Exp(-dt / Mathf.Max(1e-4f, a.impactTauSec));
                float nX  = White();
                float osc = Mathf.Sin(2f * Mathf.PI * _impactHz * _time);
                impact    = nX * Mathf.Abs(osc) * _impactEnv * a.impactLevel;
            }

            // Texture sample with envelope
            float textureSample =
                (smoothSample + roughSample + grainMix * a.grainLevel + impact) * _onEnv;

            // Bass & mid enhancement
            float sample = textureSample;
            _bassLp += _bassA * (sample - _bassLp);
            sample  += _bassLp * a.bassBoost;

            _midLp += _midLpA * (sample - _midLp);
            _midHp += _midHpA * (sample - _midHp);
            float midContent = _midLp - _midHp;
            sample += midContent * a.midBoost;

            sample *= a.masterGain;

            // Mix into channels
            for (int ch = 0; ch < channels; ch++)
                data[i * channels + ch] += sample;
        }
    }

    // Soft clipping function
    static float SoftClip(float x)
    {
        x = Mathf.Clamp(x, -2.5f, 2.5f);
        return x - (x * x * x) / 3f;
    }

    void SpawnGrain(in HapticsGlobalData.AudioSettings a, float rGate, float spd01)
    {
        int idx = -1;
        for (int i = 0; i < MaxGrains; i++)
        {
            if (!_grains[i].on)
            {
                idx = i;
                break;
            }
        }
        if (idx < 0) return;

        float tau = Mathf.Lerp(a.grainTauSec.x, a.grainTauSec.y, 1f - spd01);
        float f0  = Mathf.Lerp(a.grainBandHz.x, a.grainBandHz.y, rGate);
        float bw  = Mathf.Lerp(a.grainBandwidthHz.x, a.grainBandwidthHz.y, spd01);
        float g   = Mathf.Lerp(0.08f, 1.0f, rGate) * Mathf.Lerp(0.25f, 1.0f, spd01);

        _grains[idx] = new Grain {
            on   = true,
            t    = 0f,
            tau  = tau,
            freq = f0,
            bw   = bw,
            g    = g,
            y1   = 0f,
            y2   = 0f
        };
    }
}
