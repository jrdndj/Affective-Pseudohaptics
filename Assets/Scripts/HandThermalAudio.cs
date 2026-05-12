using UnityEngine;
using UnityEngine.Serialization;

[RequireComponent(typeof(AudioSource))]
[DefaultExecutionOrder(11031)]
public class HandThermalAudio : MonoBehaviour
{
    const float ThermalOutputSafetyGain = 0.68f;
    const float ContactLoudnessFloor = 0.84f;
    const float SpeedLoudnessFloor = 0.78f;
    const float SpeedLoudnessExponent = 0.70f;
    const float HotSurfaceGain = 0.95f;
    const float ColdSurfaceGain = 0.90f;
    const float CrackleStaticDamping = 0.60f;

    [Header("References")]
    public HapticsGlobalData globals;

    [Header("Mode")]
    public bool useGlobalTelemetry;

    [Header("Telemetry")]
    public HandTelemetrySide handTelemetrySide = HandTelemetrySide.Left;
    [SerializeField, FormerlySerializedAs("telemetry"), Tooltip("Optional override. Leave empty to use HapticsGlobalData.")]
    HandTelemetryChannel _telemetryChannelOverride;
    [Tooltip("When useGlobalTelemetry: merge these. If empty, uses Global Manager left + right.")]
    public HandTelemetryChannel[] aggregateTelemetry;

    [Header("Telemetry — legacy")]
    [FormerlySerializedAs("driver")]
    public HandTextureDriver textureHandDriver;
    [FormerlySerializedAs("aggregateDrivers")]
    public HandTextureDriver[] aggregateTextureHandDrivers;

    AudioSource _audioSource;
    int _outputSampleRate;
    HapticsGlobalData.ThermalAudioSettings _thermalAudioSettings;

    float _tangentialSpeedCached;
    bool _touchingNow;
    bool _touchingPreviousFrame;
    SurfaceType _surfaceTypeCached;

    bool _thermalHotActive;
    bool _thermalColdActive;

    float _controlTimer;
    float _dspTimeSeconds;

    float _contactEnvelopeSmoothed;

    float _thermalEnvelopeHot;
    float _thermalEnvelopeCold;

    float _hotNoiseLowpass;
    float _hotNoiseLowpassAlpha;

    float _coldNoiseLowpassLow;
    float _coldNoiseLowpassHigh;
    float _coldLowpassAlphaLow;
    float _coldLowpassAlphaHigh;

    float _crackHighpass;
    float _crackHighpassAlpha;

    const int MaxSubCracks = 3;
    struct SubCrackEvent
    {
        public bool IsActive;
        public float TimeSeconds;
        public float StartDelaySeconds;
        public float DecayTau;
        public float Amplitude;
        public float Brightness01;
    }

    SubCrackEvent[] _subCrackEvents = new SubCrackEvent[MaxSubCracks];

    float _nextCrackIntervalSeconds = 999f;
    bool _coldThermalWasActive;

    float _coldSlowLfoPhase;

    uint _randomState = 0xC0FFEEu;

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
            Debug.LogError("[HandThermalAudio] No HapticsGlobalData found.");
            enabled = false;
            return;
        }

        _thermalAudioSettings = globals.thermalAudio;
        _audioSource = GetComponent<AudioSource>();
        _audioSource.playOnAwake = true;
        _audioSource.loop = true;
        _outputSampleRate = AudioSettings.outputSampleRate;

        if (_audioSource.clip == null)
            _audioSource.clip = AudioClip.Create("ProcSilent_Thermal", 4, 1, _outputSampleRate, false);
        if (!_audioSource.isPlaying)
            _audioSource.Play();

        RecomputeThermalFilterCoefficients(in _thermalAudioSettings);
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
        _thermalAudioSettings = globals.thermalAudio;
        RecomputeThermalFilterCoefficients(in _thermalAudioSettings);
    }

    void RecomputeThermalFilterCoefficients(in HapticsGlobalData.ThermalAudioSettings thermalAudio)
    {
        float dt = 1f / Mathf.Max(1, _outputSampleRate);

        _hotNoiseLowpassAlpha = 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(50f, thermalAudio.hotNoiseCutoffHz) * dt);

        const float coldRumbleLowCutHz = 140f;
        float coldHighCutHz = Mathf.Max(1200f, thermalAudio.coldNoiseCutoffHz);
        _coldLowpassAlphaLow = 1f - Mathf.Exp(-2f * Mathf.PI * coldRumbleLowCutHz * dt);
        _coldLowpassAlphaHigh = 1f - Mathf.Exp(-2f * Mathf.PI * coldHighCutHz * dt);

        float crackCutHz = Mathf.Clamp(thermalAudio.coldNoiseCutoffHz * 1.8f, 3000f, 9500f);
        _crackHighpassAlpha = 1f - Mathf.Exp(-2f * Mathf.PI * crackCutHz * dt);
    }

    void LateUpdate()
    {
        HandTelemetrySnapshot snapshot;
        if (useGlobalTelemetry)
        {
            HandTelemetryChannel[] channelGroup = null;
            if (aggregateTelemetry != null && aggregateTelemetry.Length > 0)
                channelGroup = aggregateTelemetry;
            else if (HapticsGlobalData.Instance != null)
                channelGroup = HapticsGlobalData.Instance.GetBothHandTelemetryChannels();

            if (channelGroup != null && channelGroup.Length > 0)
                snapshot = HandTelemetrySnapshot.MergeThermalPriority(channelGroup);
            else if (aggregateTextureHandDrivers != null && aggregateTextureHandDrivers.Length > 0)
                snapshot = HandTelemetrySnapshot.MergeThermalPriority(aggregateTextureHandDrivers);
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
        _thermalHotActive = isTouching && surfaceType == SurfaceType.Hot;
        _thermalColdActive = isTouching && surfaceType == SurfaceType.Cold;
    }

    void OnAudioFilterRead(float[] data, int channelCount)
    {
        var thermalAudio = _thermalAudioSettings;
        int sampleCount = data.Length / channelCount;
        float dt = 1f / Mathf.Max(1, _outputSampleRate);

        for (int i = 0; i < sampleCount; i++)
        {
            _dspTimeSeconds += dt;
            _controlTimer += dt;

            if (_controlTimer >= 1f / Mathf.Max(10f, thermalAudio.controlRateHz))
            {
                _controlTimer = 0f;
                RecomputeThermalFilterCoefficients(in thermalAudio);
            }

            bool activeThermal = _thermalHotActive || _thermalColdActive;

            float contactTau = activeThermal ? thermalAudio.attackSec : thermalAudio.releaseSec;
            float contactBlend = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, contactTau));
            _contactEnvelopeSmoothed = Mathf.Lerp(_contactEnvelopeSmoothed, activeThermal ? 1f : 0f, contactBlend);

            float targetHot = _thermalHotActive ? 1f : 0f;
            float tauHot = targetHot > _thermalEnvelopeHot ? thermalAudio.attackSec : thermalAudio.releaseSec;
            float blendHot = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, tauHot));
            _thermalEnvelopeHot = Mathf.Lerp(_thermalEnvelopeHot, targetHot, blendHot);

            float targetCold = _thermalColdActive ? 1f : 0f;
            float tauCold = targetCold > _thermalEnvelopeCold ? thermalAudio.attackSec : thermalAudio.releaseSec;
            float blendCold = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, tauCold));
            _thermalEnvelopeCold = Mathf.Lerp(_thermalEnvelopeCold, targetCold, blendCold);

            bool coldNow = _thermalColdActive;
            if (coldNow && !_coldThermalWasActive)
                _nextCrackIntervalSeconds = Mathf.Lerp(0.5f, 1.2f, Random01());
            if (!coldNow)
                _nextCrackIntervalSeconds = 999f;
            _coldThermalWasActive = coldNow;

            float outputSample = 0f;

            if (_contactEnvelopeSmoothed > 1e-3f)
            {
                float noise = WhiteNoise();

                _hotNoiseLowpass += _hotNoiseLowpassAlpha * (noise - _hotNoiseLowpass);
                float hotHighpass = noise - _hotNoiseLowpass;
                float hotSample = hotHighpass * _thermalEnvelopeHot;

                _coldNoiseLowpassLow += _coldLowpassAlphaLow * (noise - _coldNoiseLowpassLow);
                _coldNoiseLowpassHigh += _coldLowpassAlphaHigh * (noise - _coldNoiseLowpassHigh);
                float coldLow = _coldNoiseLowpassLow;
                float coldHigh = noise - _coldNoiseLowpassHigh;
                float coldContactEnvelope = _thermalEnvelopeCold;

                if (coldNow && coldContactEnvelope > 1e-3f)
                {
                    _nextCrackIntervalSeconds -= dt;
                    if (_nextCrackIntervalSeconds <= 0f)
                    {
                        int subCount = 2 + (int)(Random01() * 3f);
                        if (subCount > MaxSubCracks)
                            subCount = MaxSubCracks;

                        for (int scIdx = 0; scIdx < MaxSubCracks; scIdx++)
                            _subCrackEvents[scIdx].IsActive = false;

                        float baseSpacing = Mathf.Lerp(0.008f, 0.025f, Random01());

                        for (int j = 0; j < subCount; j++)
                        {
                            float rank = subCount <= 1 ? 0f : j / (float)(subCount - 1);
                            float ampBase = Mathf.Lerp(1.0f, 0.45f, rank);
                            float ampJit = Mathf.Lerp(0.85f, 1.3f, Random01());
                            float amp = ampBase * ampJit * Mathf.Lerp(0.70f, 1.2f, coldContactEnvelope);

                            float tauMin = j == 0 ? 0.002f : 0.006f;
                            float tauMax = j == 0 ? 0.010f : 0.028f;
                            float tau = Mathf.Lerp(tauMin, tauMax, Random01());

                            float delayJitter = Mathf.Lerp(0.7f, 1.3f, Random01());
                            float startDelay = baseSpacing * j * delayJitter;
                            float brightness = Mathf.Lerp(0.6f, 1.0f, Random01());

                            _subCrackEvents[j] = new SubCrackEvent
                            {
                                IsActive = true,
                                TimeSeconds = 0f,
                                StartDelaySeconds = startDelay,
                                DecayTau = tau,
                                Amplitude = amp,
                                Brightness01 = brightness
                            };
                        }

                        float u = Random01();
                        float minInt = Mathf.Lerp(0.8f, 0.5f, coldContactEnvelope);
                        float maxInt = Mathf.Lerp(1.5f, 0.9f, coldContactEnvelope);
                        _nextCrackIntervalSeconds = Mathf.Lerp(minInt, maxInt, u);
                    }
                }

                float lfoFreq = Mathf.Clamp(thermalAudio.coldLfoHz, 0.10f, 0.80f);
                _coldSlowLfoPhase += 2f * Mathf.PI * lfoFreq * dt;
                if (_coldSlowLfoPhase > 2f * Mathf.PI)
                    _coldSlowLfoPhase -= 2f * Mathf.PI;
                float coldLfo = 0.92f + 0.08f * Mathf.Sin(_coldSlowLfoPhase);

                float coldBase = (coldLow * 0.9f + coldHigh * 0.1f) * coldLfo;

                float crackMix = 0f;
                if (coldContactEnvelope > 1e-3f)
                {
                    float nCrack = WhiteNoise();
                    _crackHighpass += _crackHighpassAlpha * (nCrack - _crackHighpass);
                    float crackNoiseBase = nCrack - _crackHighpass;
                    float crackNoise = crackNoiseBase * 0.8f + coldHigh * 0.2f;

                    for (int c = 0; c < MaxSubCracks; c++)
                    {
                        if (!_subCrackEvents[c].IsActive)
                            continue;
                        var sc = _subCrackEvents[c];
                        sc.TimeSeconds += dt;

                        if (sc.TimeSeconds < sc.StartDelaySeconds)
                        {
                            _subCrackEvents[c] = sc;
                            continue;
                        }

                        float localT = sc.TimeSeconds - sc.StartDelaySeconds;
                        float env = Mathf.Exp(-localT / Mathf.Max(0.001f, sc.DecayTau));
                        if (env < 1e-3f)
                        {
                            sc.IsActive = false;
                            _subCrackEvents[c] = sc;
                            continue;
                        }

                        float bright = Mathf.Lerp(0.7f, 1.0f, sc.Brightness01);
                        float dark = 1.0f - 0.5f * sc.Brightness01;
                        float shaped = crackNoise * bright + coldLow * dark * 0.2f;
                        crackMix += shaped * env * sc.Amplitude;
                        _subCrackEvents[c] = sc;
                    }
                }

                float coldSample = (coldBase * 0.72f + crackMix * 0.55f * CrackleStaticDamping) * coldContactEnvelope * 0.82f;

                float speed01 = Mathf.Clamp01(_tangentialSpeedCached / Mathf.Max(0.001f, thermalAudio.speedRef));
                float speedGainConfigured = Mathf.Lerp(thermalAudio.minSpeedGain, thermalAudio.maxSpeedGain, speed01);
                float speedGainShaped = Mathf.Lerp(
                    SpeedLoudnessFloor,
                    1f,
                    Mathf.Pow(speed01, SpeedLoudnessExponent)
                );
                float contactGainShaped = Mathf.Lerp(ContactLoudnessFloor, 1f, _contactEnvelopeSmoothed);
                float surfaceComp = _thermalHotActive ? HotSurfaceGain : (_thermalColdActive ? ColdSurfaceGain : 1f);
                outputSample =
                    (hotSample + coldSample) *
                    thermalAudio.masterGain *
                    speedGainConfigured *
                    speedGainShaped *
                    contactGainShaped *
                    surfaceComp *
                    ThermalOutputSafetyGain;
            }

            for (int ch = 0; ch < channelCount; ch++)
                data[i * channelCount + ch] += outputSample;
        }
    }
}
