using UnityEngine;
using UnityEngine.Serialization;
using System;

[RequireComponent(typeof(AudioSource))]
[DefaultExecutionOrder(11030)]
public class HandTextureAudio : MonoBehaviour
{
    const float TextureOutputSafetyGain = 0.62f;
    const float ContactLoudnessFloor = 0.82f;
    const float SpeedLoudnessFloor = 0.72f;
    const float SpeedLoudnessExponent = 0.65f;
    const float SmoothSurfaceGain = 1.38f;
    const float SmoothBassCompensation = 0.22f;
    const float SmoothMidCompensation = 0.02f;
    const float SmoothMidBoostScale = 0.12f;
    const float SmoothSpeedFloor = 0.40f;
    const float SmoothSpeedExponent = 0.52f;
    const float RoughStaticDamping = 0.82f;
    const float GrainStaticDamping = 0.75f;
    const float ImpactDamping = 0.70f;

    [Header("References")]
    public HapticsGlobalData globals;

    [Header("Mode")]
    public bool useGlobalTelemetry;

    [Header("Telemetry")]
    [Tooltip("Which hand: channel from HapticsGlobalData (left/right on Global Manager).")]
    public HandTelemetrySide handTelemetrySide = HandTelemetrySide.Left;
    [SerializeField, FormerlySerializedAs("telemetry"), Tooltip("Optional override. Leave empty to use HapticsGlobalData.")]
    HandTelemetryChannel _telemetryChannelOverride;
    [Tooltip("When useGlobalTelemetry: merge these channels. If empty, uses Global Manager left + right.")]
    public HandTelemetryChannel[] aggregateTelemetry;

    [Header("Telemetry — legacy")]
    [FormerlySerializedAs("driver")]
    public HandTextureDriver textureHandDriver;
    [FormerlySerializedAs("aggregateDrivers"), Tooltip("When useGlobalTelemetry and no aggregateTelemetry: merge these drivers.")]
    public HandTextureDriver[] aggregateTextureHandDrivers;

    AudioSource _audioSource;
    int _outputSampleRate;
    HapticsGlobalData.AudioSettings _audioSettings;

    float _roughnessDiscrete01;
    float _tangentialSpeedCached;
    bool _isContactActive;
    bool _touchingNow;
    bool _touchingPreviousFrame;
    SurfaceType _surfaceTypeCached;
    bool _isTextureSurface;

    float _controlTimer;

    float _smoothNoiseLp;
    float _smoothNoiseLpAlpha;
    float _amplitudeModulationPhase;
    float _smoothCascade1;
    float _smoothCascade2;
    float _smoothSilkBodyLp;
    float _smoothSilkBodyLpAlpha;
    float _smoothWindPhase;

    float _roughNoiseLp;
    float _roughNoiseLpAlpha;
    float _jitterHold;
    float _jitterTimer;
    float _jitterInterval;

    const int MaxGrainVoices = 16;
    struct GrainVoice
    {
        public bool IsActive;
        public float TimeSeconds;
        public float DecayTau;
        public float Frequency;
        public float Bandwidth;
        public float FilterY1;
        public float FilterY2;
        public float Gain;
    }

    GrainVoice[] _grainVoices = new GrainVoice[MaxGrainVoices];
    float _nextGrainTimeSeconds;
    float _dspTimeSeconds;
    float _grainSpawnRateHz;

    float _contactEnvelopeSmoothed;
    float _impactEnvelope;
    float _impactOscHz;

    float _bassLowpass;
    float _midLowpass;
    float _midHighpass;
    float _bassLowpassAlpha;
    float _midLowpassAlpha;
    float _midHighpassAlpha;

    uint _randomState = 0x1234567u;

    float WhiteNoise()
    {
        _randomState = 1664525u * _randomState + 1013904223u;
        return ((_randomState >> 9) & 0x7FFFFF) / 8388607f * 2f - 1f;
    }

    float Random01()
    {
        _randomState = 1103515245u * _randomState + 12345u;
        return ((_randomState >> 8) & 0xFFFFFF) / 16777215f;
    }

    void Awake()
    {
        if (!globals)
            globals = HapticsGlobalData.Instance;
        if (!globals)
        {
            Debug.LogError("[HandTextureAudio] No HapticsGlobalData found.");
            enabled = false;
            return;
        }

        _audioSettings = globals.audio;
        _audioSource = GetComponent<AudioSource>();
        _audioSource.playOnAwake = true;
        _audioSource.loop = true;

        _outputSampleRate = AudioSettings.outputSampleRate;

        if (_audioSource.clip == null)
            _audioSource.clip = AudioClip.Create("ProcSilence_Texture", 4, 1, _outputSampleRate, false);
        if (!_audioSource.isPlaying)
            _audioSource.Play();

        var audioSettings = _audioSettings;
        _jitterInterval = 1f / Mathf.Max(1f, audioSettings.jitterHzByR.x);
        _jitterTimer = _jitterInterval;
        _jitterHold = 1f;

        RecomputeToneFilterCoefficients(in audioSettings);
    }

    void OnEnable()
    {
        if (!globals)
            globals = HapticsGlobalData.Instance;
        if (globals != null)
            globals.OnGlobalsChanged += OnHapticsGlobalsChanged;
    }

    void OnDisable()
    {
        if (globals != null)
            globals.OnGlobalsChanged -= OnHapticsGlobalsChanged;
    }

    void OnHapticsGlobalsChanged()
    {
        if (globals == null)
            return;
        _audioSettings = globals.audio;
        RecomputeToneFilterCoefficients(in _audioSettings);
    }

    void RecomputeToneFilterCoefficients(in HapticsGlobalData.AudioSettings audioSettings)
    {
        float dt = 1f / Mathf.Max(1, _outputSampleRate);
        _bassLowpassAlpha = 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(10f, audioSettings.bassFreq) * dt);
        _midLowpassAlpha = 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(10f, audioSettings.midFreq) * dt);
        _midHighpassAlpha = 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(10f, audioSettings.midFreq * 0.5f) * dt);
    }

    void LateUpdate()
    {
        var audioSettings = _audioSettings;

        HandTelemetrySnapshot snapshot;
        if (useGlobalTelemetry)
        {
            HandTelemetryChannel[] channelGroup = null;
            if (aggregateTelemetry != null && aggregateTelemetry.Length > 0)
                channelGroup = aggregateTelemetry;
            else if (HapticsGlobalData.Instance != null)
                channelGroup = HapticsGlobalData.Instance.GetBothHandTelemetryChannels();

            if (channelGroup != null && channelGroup.Length > 0)
                snapshot = HandTelemetrySnapshot.MergeTexturePriority(channelGroup);
            else if (aggregateTextureHandDrivers != null && aggregateTextureHandDrivers.Length > 0)
                snapshot = HandTelemetrySnapshot.MergeTexturePriority(aggregateTextureHandDrivers);
            else
                return;
        }
        else
        {
            var channel = _telemetryChannelOverride;
            if (!channel && HapticsGlobalData.Instance != null)
                channel = HapticsGlobalData.Instance.GetHandTelemetryChannel(handTelemetrySide);

            if (channel)
                snapshot = channel.Latest;
            else if (textureHandDriver)
                snapshot = HandTelemetrySnapshot.FromHandTextureDriver(textureHandDriver);
            else
                return;
        }

        bool isTouching = snapshot.IsTouching;
        float tangentialSpeed = snapshot.TangentialSpeed;
        SurfaceType surfaceType = snapshot.SurfaceType;

        _touchingPreviousFrame = _touchingNow;
        _touchingNow = isTouching;
        _surfaceTypeCached = surfaceType;
        _tangentialSpeedCached = tangentialSpeed;

        switch (surfaceType)
        {
            case SurfaceType.Rough:
                _roughnessDiscrete01 = 1f;
                break;
            case SurfaceType.Smooth:
                _roughnessDiscrete01 = 0f;
                break;
            default:
                _roughnessDiscrete01 = 0.5f;
                break;
        }

        _isTextureSurface = surfaceType == SurfaceType.Rough || surfaceType == SurfaceType.Smooth;
        _isContactActive = isTouching;

        // High-frequency impact clicks read as harsh on smooth fabric; keep them for rough only.
        if (audioSettings.impactClick && _touchingNow && !_touchingPreviousFrame && _isTextureSurface &&
            surfaceType != SurfaceType.Smooth)
        {
            _impactEnvelope = 1f;
            float speed01 = Mathf.Clamp01(_tangentialSpeedCached / Mathf.Max(0.001f, audioSettings.speedRef));
            _impactOscHz = Mathf.Lerp(2000f, audioSettings.impactMaxHz, speed01);
        }
    }

    void OnAudioFilterRead(float[] data, int channelCount)
    {
        var audioSettings = _audioSettings;
        int sampleCount = data.Length / channelCount;
        float dt = 1f / Mathf.Max(1, _outputSampleRate);

        for (int i = 0; i < sampleCount; i++)
        {
            _dspTimeSeconds += dt;
            _controlTimer += dt;

            if (_controlTimer >= 1f / Mathf.Max(10f, audioSettings.controlRateHz))
            {
                _controlTimer = 0f;

                float roughness = _roughnessDiscrete01;
                float speed01 = Mathf.Clamp01(_tangentialSpeedCached / Mathf.Max(0.001f, audioSettings.speedRef));

                float smoothCutHz =
                    Mathf.Lerp(audioSettings.lowpassHzByR.x, audioSettings.lowpassHzByR.y, roughness) +
                    Mathf.Lerp(audioSettings.lowpassHzBySpeed.x, audioSettings.lowpassHzBySpeed.y, speed01) * (1f - roughness);
                smoothCutHz = Mathf.Clamp(smoothCutHz, 80f, _outputSampleRate * 0.45f);
                _smoothNoiseLpAlpha = 1f - Mathf.Exp(-2f * Mathf.PI * smoothCutHz * dt);

                float silkCut = Mathf.Clamp(audioSettings.smoothLayerLowpassHz, 120f, 2200f);
                _smoothSilkBodyLpAlpha = 1f - Mathf.Exp(-2f * Mathf.PI * silkCut * dt);

                float roughCutHz =
                    Mathf.Lerp(audioSettings.roughLowpassHz.x, audioSettings.roughLowpassHz.y, Mathf.Max(roughness, speed01));
                roughCutHz = Mathf.Clamp(roughCutHz, 200f, _outputSampleRate * 0.45f);
                _roughNoiseLpAlpha = 1f - Mathf.Exp(-2f * Mathf.PI * roughCutHz * dt);

                float jitterHz = Mathf.Lerp(audioSettings.jitterHzByR.x, audioSettings.jitterHzByR.y, roughness) *
                                 Mathf.Lerp(0.2f, 1f, speed01);
                jitterHz = Mathf.Max(1f, jitterHz);
                _jitterInterval = 1f / jitterHz;
                _jitterTimer = Mathf.Min(_jitterTimer, _jitterInterval);

                RecomputeToneFilterCoefficients(in audioSettings);
            }

            float contactTau = _isContactActive ? audioSettings.attackSmoothSec : audioSettings.releaseSmoothSec;
            float contactBlend = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, contactTau));
            _contactEnvelopeSmoothed = Mathf.Lerp(_contactEnvelopeSmoothed, _isContactActive ? 1f : 0f, contactBlend);

            float textureMask = _isTextureSurface ? 1f : 0f;

            _smoothNoiseLp += _smoothNoiseLpAlpha * (WhiteNoise() - _smoothNoiseLp);
            _smoothCascade1 += _smoothNoiseLpAlpha * 0.7f * (_smoothNoiseLp - _smoothCascade1);
            _smoothCascade2 += _smoothNoiseLpAlpha * 0.5f * (_smoothCascade1 - _smoothCascade2);
            float smoothSignal = _smoothCascade2;

            float speed01Now = Mathf.Clamp01(_tangentialSpeedCached / Mathf.Max(0.001f, audioSettings.speedRef));
            bool smoothOnly = _roughnessDiscrete01 < 1e-3f && textureMask > 0.5f;
            float amDepthUse = audioSettings.glideAMDepth;
            float amHzLow = audioSettings.glideAMHz.x;
            float amHzHigh = audioSettings.glideAMHz.y;
            if (smoothOnly)
            {
                amDepthUse *= 0.28f;
                amHzLow = Mathf.Min(amHzLow, 0.10f);
                amHzHigh = Mathf.Min(amHzHigh, 0.32f);
            }

            float amHz = Mathf.Lerp(amHzLow, amHzHigh, speed01Now) * (1f - _roughnessDiscrete01);
            _amplitudeModulationPhase += amHz * dt * 2f * Mathf.PI;
            if (_amplitudeModulationPhase > Mathf.PI * 2f)
                _amplitudeModulationPhase -= Mathf.PI * 2f;
            float amplitudeMod = 1f + amDepthUse * Mathf.Sin(_amplitudeModulationPhase);

            float smoothVolume =
                Mathf.Lerp(audioSettings.smoothVolByR.x, audioSettings.smoothVolByR.y, _roughnessDiscrete01) *
                (1f - _roughnessDiscrete01);
            float smoothSpeedFactor = smoothOnly
                ? Mathf.Lerp(SmoothSpeedFloor, 1f, Mathf.Pow(speed01Now, SmoothSpeedExponent))
                : speed01Now;
            smoothVolume *= smoothSpeedFactor * textureMask;
            float smoothSampleRaw = smoothSignal * amplitudeMod * smoothVolume;

            if (smoothOnly)
            {
                _smoothWindPhase += dt * Mathf.Lerp(0.10f, 0.26f, speed01Now) * 2f * Mathf.PI;
                if (_smoothWindPhase > Mathf.PI * 2f)
                    _smoothWindPhase -= Mathf.PI * 2f;
                float windBreath = 0.78f + 0.22f * Mathf.Sin(_smoothWindPhase);
                _smoothSilkBodyLp += _smoothSilkBodyLpAlpha * (smoothSampleRaw * windBreath - _smoothSilkBodyLp);
                smoothSampleRaw = Mathf.Lerp(smoothSampleRaw, _smoothSilkBodyLp, 0.72f);
            }
            else
            {
                _smoothSilkBodyLp = Mathf.Lerp(_smoothSilkBodyLp, 0f, 1f - Mathf.Exp(-30f * dt));
            }

            float smoothSample = smoothSampleRaw;

            _roughNoiseLp += _roughNoiseLpAlpha * (WhiteNoise() - _roughNoiseLp);

            _jitterTimer += dt;
            if (_jitterTimer >= _jitterInterval)
            {
                _jitterTimer -= _jitterInterval;
                _jitterHold = 1f + (Random01() * 2f - 1f) * audioSettings.jitterDepth;
            }

            float roughDrive = 1f + _roughnessDiscrete01 * audioSettings.roughDriveAt1;
            float roughRaw = SoftClip(_roughNoiseLp * roughDrive);
            float roughVolume = Mathf.Lerp(audioSettings.roughVolByR.x, audioSettings.roughVolByR.y, _roughnessDiscrete01);
            roughVolume *= speed01Now * textureMask;
            float roughSample = roughRaw * _jitterHold * roughVolume * RoughStaticDamping;

            float grainMix = 0f;
            if (audioSettings.enableGrainAccents)
            {
                float roughGate = Mathf.SmoothStep(0f, 1f, _roughnessDiscrete01) * textureMask;
                _grainSpawnRateHz = Mathf.Lerp(audioSettings.grainRateHz.x, audioSettings.grainRateHz.y, speed01Now) * roughGate;

                if (_grainSpawnRateHz > 0.01f)
                {
                    while (_dspTimeSeconds >= _nextGrainTimeSeconds)
                    {
                        SpawnGrainVoice(in audioSettings, roughGate, speed01Now);
                        float u = Mathf.Max(1e-6f, Random01());
                        float gap = -Mathf.Log(u) / _grainSpawnRateHz;
                        _nextGrainTimeSeconds = (_nextGrainTimeSeconds <= 0f ? _dspTimeSeconds : _nextGrainTimeSeconds) + gap;
                    }
                }

                for (int g = 0; g < MaxGrainVoices; g++)
                {
                    if (!_grainVoices[g].IsActive)
                        continue;
                    var grain = _grainVoices[g];
                    grain.TimeSeconds += dt;

                    float envelope = Mathf.Exp(-grain.TimeSeconds / grain.DecayTau);
                    if (envelope < 1e-3f)
                    {
                        grain.IsActive = false;
                        _grainVoices[g] = grain;
                        continue;
                    }

                    float n0 = WhiteNoise();
                    grain.FilterY1 += 0.12f * (n0 - grain.FilterY1);
                    float highpass = n0 - grain.FilterY1;

                    float lpa = 2f * Mathf.PI *
                                Mathf.Clamp(grain.Frequency + grain.Bandwidth, 80f, _outputSampleRate * 0.45f) * dt;
                    lpa = Mathf.Clamp01(lpa);
                    grain.FilterY2 += lpa * (highpass - grain.FilterY2);
                    float bandpass = grain.FilterY2;

                    grainMix += bandpass * grain.Gain * envelope;
                    _grainVoices[g] = grain;
                }
            }

            float impactSample = 0f;
            if (_impactEnvelope > 1e-3f && audioSettings.impactClick)
            {
                _impactEnvelope *= Mathf.Exp(-dt / Mathf.Max(1e-4f, audioSettings.impactTauSec));
                if (_isTextureSurface && _surfaceTypeCached != SurfaceType.Smooth)
                {
                    float noise = WhiteNoise();
                    float osc = Mathf.Sin(2f * Mathf.PI * _impactOscHz * _dspTimeSeconds);
                    impactSample = noise * Mathf.Abs(osc) * _impactEnvelope * audioSettings.impactLevel * ImpactDamping;
                }
            }

            float textureSample =
                (smoothSample + roughSample + grainMix * audioSettings.grainLevel * GrainStaticDamping + impactSample) * _contactEnvelopeSmoothed;

            float sample = textureSample;
            _bassLowpass += _bassLowpassAlpha * (sample - _bassLowpass);
            sample += _bassLowpass * audioSettings.bassBoost;

            _midLowpass += _midLowpassAlpha * (sample - _midLowpass);
            _midHighpass += _midHighpassAlpha * (sample - _midHighpass);
            float midContent = _midLowpass - _midHighpass;
            bool smoothTextureActive = _isTextureSurface && _surfaceTypeCached == SurfaceType.Smooth;
            float midBoostScaled = smoothTextureActive
                ? audioSettings.midBoost * SmoothMidBoostScale
                : audioSettings.midBoost;
            sample += midContent * midBoostScaled;

            if (smoothTextureActive)
            {
                sample += _bassLowpass * SmoothBassCompensation;
                sample += midContent * SmoothMidCompensation;
            }

            float speedLoudness = Mathf.Lerp(
                SpeedLoudnessFloor,
                1f,
                Mathf.Pow(speed01Now, SpeedLoudnessExponent)
            );
            float contactLoudness = Mathf.Lerp(ContactLoudnessFloor, 1f, _contactEnvelopeSmoothed);
            float surfaceComp = smoothTextureActive ? SmoothSurfaceGain : 1f;
            sample *= audioSettings.masterGain * speedLoudness * contactLoudness * surfaceComp * TextureOutputSafetyGain;

            for (int ch = 0; ch < channelCount; ch++)
                data[i * channelCount + ch] += sample;
        }
    }

    static float SoftClip(float x)
    {
        x = Mathf.Clamp(x, -2.5f, 2.5f);
        return x - (x * x * x) / 3f;
    }

    void SpawnGrainVoice(in HapticsGlobalData.AudioSettings audioSettings, float roughGate, float speed01)
    {
        int index = -1;
        for (int i = 0; i < MaxGrainVoices; i++)
        {
            if (!_grainVoices[i].IsActive)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return;

        float tau = Mathf.Lerp(audioSettings.grainTauSec.x, audioSettings.grainTauSec.y, 1f - speed01);
        float centerHz = Mathf.Lerp(audioSettings.grainBandHz.x, audioSettings.grainBandHz.y, roughGate);
        float bandwidth = Mathf.Lerp(audioSettings.grainBandwidthHz.x, audioSettings.grainBandwidthHz.y, speed01);
        float gain = Mathf.Lerp(0.08f, 1.0f, roughGate) * Mathf.Lerp(0.25f, 1.0f, speed01);

        _grainVoices[index] = new GrainVoice
        {
            IsActive = true,
            TimeSeconds = 0f,
            DecayTau = tau,
            Frequency = centerHz,
            Bandwidth = bandwidth,
            Gain = gain,
            FilterY1 = 0f,
            FilterY2 = 0f
        };
    }
}
