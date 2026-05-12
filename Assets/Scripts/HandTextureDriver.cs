using UnityEngine;
using UnityEngine.Serialization;
using System.Collections.Generic;

[DefaultExecutionOrder(11000)]
public class HandTextureDriver : MonoBehaviour
{
    const float Epsilon = 1e-5f;
    const float EffectBlendThreshold = 1e-3f;
    const float SmoothGlidePredictSeconds = 0.042f;
    const float SmoothGlideMaxFactor = 0.48f;
    const float SmoothFollowHz = 14f;
    const float SmoothEffectFollowHz = 12f;

    [Header("References")]
    public HapticsGlobalData globals;

    [Header("Tracked XR hand")]
    public SkinnedMeshRenderer xrRenderer;
    public Transform xrArmatureRoot;

    [Header("Visible decoy hand")]
    public SkinnedMeshRenderer decoyRenderer;
    public Transform decoyArmatureRoot;

    [Header("Decoy collision")]
    public bool autoFindDecoyColliders = true;
    public Collider[] decoyJointColliders;
    [Tooltip("Physics probe radius at each decoy joint.")]
    public float jointProbeRadius = 0.01f;
    [Tooltip("Max distance (m) from joint to counted contact.")]
    public float maxJointContactDistance = 0.025f;

    [Header("World contact query")]
    public LayerMask contactLayers = ~0;
    public float contactProbeRadius = 0.06f;

    [Header("Contact performance")]
    [Tooltip("Broadphase check near palm before per-joint probes.")]
    public bool useBroadphase = true;
    [Tooltip("Broadphase radius (m) around the hand root).")]
    public float broadphaseRadius = 0.12f;
    [Tooltip("How many decoy joints to probe per frame (round-robin). 0 = all joints.")]
    public int jointsPerFrame = 6;
    [Tooltip("Hz for detailed contact point/surface updates. 0 = every frame.")]
    public float contactDetailHz = 30f;

    [Header("Telemetry")]
    [Tooltip("Which hand: channel comes from HapticsGlobalData (left/right assets on Global Manager).")]
    public HandTelemetrySide handTelemetrySide = HandTelemetrySide.Left;
    [SerializeField, FormerlySerializedAs("telemetryChannel"), Tooltip("Optional per-hand override. Leave empty to use HapticsGlobalData.")]
    HandTelemetryChannel _telemetryChannelOverride;

    [Header("Motion pseudo-haptics")]
    public bool useMotionEffects = true;
    public float contactReleaseTime = 1.0f;

    [Header("Debug")]
    public GameObject indicator;
    public bool drawGizmos;

    // --- Outputs (read by audio / thermal / trials) -------------------------------------------

    public float CurrentRoughness01 { get; private set; }
    public bool IsTouching { get; private set; }
    public bool IsTouchingRaw { get; private set; }
    public bool IsSliding { get; private set; }
    public float TangentialSpeed { get; private set; }
    public Vector3 TangentialVelocity { get; private set; }
    public Vector3 ContactNormal { get; private set; } = Vector3.up;
    public SurfaceData CurrentSurface { get; private set; }
    public Vector3 ContactPoint { get; private set; }
    public float ContactCoverage01 { get; private set; }
    public float ContactEnvelope01 { get; private set; }
    public Vector3 BasePos { get; private set; }
    public Quaternion BaseRot { get; private set; }
    public Vector3 EffectOffset { get; private set; }

    // --- Runtime --------------------------------------------------------------------------------

    HapticsGlobalData.VisualSettings _visualSettings;

    readonly Dictionary<string, Transform> _xrBoneTransformByName = new Dictionary<string, Transform>();
    readonly List<(Transform src, Transform dst)> _xrToDecoyBonePairs = new List<(Transform src, Transform dst)>();

    readonly HandPoseHistory _poseHistory = new HandPoseHistory();
    readonly HandContactSensor _contactSensor = new HandContactSensor();

    Vector3 _glideOffsetWorld;
    Vector3 _tangentAxisPrimary;
    Vector3 _tangentAxisSecondary;
    float _tremorPhase;
    float _tremorFrequencyHz;
    Vector3 _smoothBasePosition;
    Quaternion _smoothBaseRotation;
    Vector3 _smoothEffectOffsetWorld;

    void Awake()
    {
        if (!globals)
            globals = HapticsGlobalData.Instance;
        if (!globals)
        {
            Debug.LogError("[HandTextureDriver] No HapticsGlobalData found in scene.");
            enabled = false;
            return;
        }

        _visualSettings = globals.visual;

        if (!xrRenderer || !decoyRenderer)
        {
            Debug.LogError("[HandTextureDriver] Assign xrRenderer + decoyRenderer.");
            enabled = false;
            return;
        }

        if (!xrArmatureRoot)
            xrArmatureRoot = xrRenderer.rootBone ? xrRenderer.rootBone : xrRenderer.transform;
        if (!decoyArmatureRoot)
            decoyArmatureRoot = decoyRenderer.rootBone ? decoyRenderer.rootBone : decoyRenderer.transform;

        if (autoFindDecoyColliders || decoyJointColliders == null || decoyJointColliders.Length == 0)
            decoyJointColliders = decoyArmatureRoot.GetComponentsInChildren<Collider>(true);

        BuildBonePairs();

        _poseHistory.Initialize(xrArmatureRoot);
        _contactSensor.Initialize(xrArmatureRoot.position);

        foreach (var renderer in xrRenderer.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = false;

        decoyArmatureRoot.SetPositionAndRotation(xrArmatureRoot.position, xrArmatureRoot.rotation);

        SetIndicator(false);

        var visualSettings = _visualSettings;
        CurrentRoughness01 = Mathf.Clamp01(visualSettings.defaultRoughness);
        ContactPoint = xrArmatureRoot.position;
        ContactNormal = Vector3.up;
        _glideOffsetWorld = Vector3.zero;

        _tremorPhase = Random.value * Mathf.PI * 2f;
        _tremorFrequencyHz = Random.Range(visualSettings.tremorHzRange.x, visualSettings.tremorHzRange.y);

        _smoothBasePosition = xrArmatureRoot.position;
        _smoothBaseRotation = xrArmatureRoot.rotation;
        _smoothEffectOffsetWorld = Vector3.zero;
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
        _visualSettings = globals.visual;
    }

    void BuildBonePairs()
    {
        _xrBoneTransformByName.Clear();
        foreach (var bone in xrRenderer.bones)
            if (bone && !_xrBoneTransformByName.ContainsKey(bone.name))
                _xrBoneTransformByName.Add(bone.name, bone);

        _xrToDecoyBonePairs.Clear();
        foreach (var decoyBone in decoyRenderer.bones)
        {
            if (!decoyBone)
                continue;
            if (decoyBone == decoyArmatureRoot)
                continue;

            string key = decoyBone.name.EndsWith("_Ghost")
                ? decoyBone.name.Substring(0, decoyBone.name.Length - 6)
                : decoyBone.name;

            if (_xrBoneTransformByName.TryGetValue(key, out var xrBone))
                _xrToDecoyBonePairs.Add((xrBone, decoyBone));
        }

        if (_xrToDecoyBonePairs.Count == 0)
            Debug.LogWarning("[HandTextureDriver] No bone name matches (children).");
    }

    void LateUpdate()
    {
        var visualSettings = _visualSettings;
        float deltaTime = Mathf.Max(Time.deltaTime, Epsilon);
        float timeNow = Time.time;

        for (int i = 0; i < _xrToDecoyBonePairs.Count; i++)
        {
            var pair = _xrToDecoyBonePairs[i];
            if (!pair.src || !pair.dst)
                continue;
            pair.dst.localPosition = pair.src.localPosition;
            pair.dst.localRotation = pair.src.localRotation;
            pair.dst.localScale = pair.src.localScale;
        }

        _poseHistory.Tick(xrArmatureRoot, visualSettings.velocityLowpassHz, deltaTime, timeNow);
        Vector3 trackedRootPosition = _poseHistory.Position;

        HandContactState contactState = _contactSensor.Sample(
            xrArmatureRoot,
            decoyArmatureRoot,
            decoyJointColliders,
            contactLayers,
            jointProbeRadius,
            maxJointContactDistance,
            contactProbeRadius,
            contactReleaseTime,
            useBroadphase,
            broadphaseRadius,
            jointsPerFrame <= 0 ? int.MaxValue : jointsPerFrame,
            contactDetailHz,
            trackedRootPosition,
            _poseHistory.Velocity,
            visualSettings,
            deltaTime
        );

        IsTouchingRaw = contactState.TouchingRaw;
        IsTouching = contactState.Touching;
        IsSliding = contactState.Sliding;
        TangentialVelocity = contactState.TangentialVelocity;
        TangentialSpeed = contactState.TangentialSpeed;
        ContactNormal = contactState.ContactNormal;
        ContactPoint = contactState.ContactPoint;
        ContactCoverage01 = contactState.ContactCoverage01;
        ContactEnvelope01 = contactState.ContactEnvelope;
        CurrentSurface = contactState.Surface;
        CurrentRoughness01 = contactState.Roughness01;

        bool isTouching = contactState.Touching;
        Vector3 planeNormal = contactState.ContactNormal;
        Vector3 tangentialVelocity = contactState.TangentialVelocity;
        float tangentialSpeed = contactState.TangentialSpeed;
        SurfaceType surfaceType = contactState.SurfaceType;

        bool isTextureSurface = surfaceType == SurfaceType.Smooth || surfaceType == SurfaceType.Rough;

        float effectEnvelope = 0f;
        float smoothIceBlend = 0f;
        float roughBlend = 0f;

        if (isTextureSurface)
        {
            effectEnvelope = contactState.ContactEnvelope;
            if (effectEnvelope > EffectBlendThreshold)
            {
                if (surfaceType == SurfaceType.Smooth)
                    smoothIceBlend = effectEnvelope;
                else if (surfaceType == SurfaceType.Rough)
                    roughBlend = effectEnvelope;
            }
        }

        Vector3 basePosition = trackedRootPosition;
        Quaternion baseRotation = xrArmatureRoot.rotation;

        bool isRoughContact = effectEnvelope > EffectBlendThreshold && surfaceType == SurfaceType.Rough;
        bool isSmoothContact = effectEnvelope > EffectBlendThreshold && surfaceType == SurfaceType.Smooth;

        if (isRoughContact && isTouching)
        {
            float latencyMs = visualSettings.latencyMsAt1;
            if (latencyMs > 0f)
            {
                float targetTime = timeNow - 0.001f * latencyMs;
                _poseHistory.SampleDelayedPose(targetTime, out basePosition, out baseRotation);
            }
        }

        if (tangentialSpeed > Epsilon)
            _tangentAxisPrimary = tangentialVelocity / Mathf.Max(tangentialSpeed, Epsilon);
        else
        {
            _tangentAxisPrimary = Vector3.Cross(planeNormal, Vector3.up);
            if (_tangentAxisPrimary.sqrMagnitude < Epsilon)
                _tangentAxisPrimary = Vector3.Cross(planeNormal, Vector3.right);
            _tangentAxisPrimary.Normalize();
        }
        _tangentAxisSecondary = Vector3.Normalize(Vector3.Cross(planeNormal, _tangentAxisPrimary));

        {
            const float followHzFree = 40f;
            const float followHzSmooth = SmoothFollowHz;
            const float followHzRough = 7f;

            float contactFollowHz = Mathf.Lerp(followHzSmooth, followHzRough, roughBlend);
            float baseFollowHz = isTextureSurface
                ? Mathf.Lerp(followHzFree, contactFollowHz, Mathf.Clamp01(effectEnvelope))
                : followHzFree;

            float baseBlend = 1f - Mathf.Exp(-2f * Mathf.PI * baseFollowHz * deltaTime);
            _smoothBasePosition = Vector3.Lerp(_smoothBasePosition, basePosition, baseBlend);
            _smoothBaseRotation = Quaternion.Slerp(_smoothBaseRotation, baseRotation, baseBlend);
        }

        BasePos = _smoothBasePosition;
        BaseRot = _smoothBaseRotation;

        Vector3 effectTargetWorld = Vector3.zero;

        if (useMotionEffects && isTextureSurface && effectEnvelope > EffectBlendThreshold)
        {
            _glideOffsetWorld -= Vector3.Dot(_glideOffsetWorld, planeNormal) * planeNormal;

            if (isSmoothContact)
            {
                if (tangentialSpeed > visualSettings.glideDeadzoneSpeed)
                {
                    Vector3 targetOffset = tangentialVelocity * SmoothGlidePredictSeconds;
                    float smoothMaxGlide = visualSettings.glideMaxOffsetAt0 * SmoothGlideMaxFactor;
                    if (targetOffset.sqrMagnitude > smoothMaxGlide * smoothMaxGlide)
                        targetOffset = targetOffset.normalized * smoothMaxGlide;

                    // Silky glide: slightly slower follow so motion stays stable but still drifts subtly.
                    float glideBlend = 1f - Mathf.Exp(-(visualSettings.glideAccel * 1.05f + 5f) * deltaTime);
                    _glideOffsetWorld = Vector3.Lerp(_glideOffsetWorld, targetOffset, glideBlend);
                }
                else
                {
                    float glideReturnBlend = 1f - Mathf.Exp(-(visualSettings.glideReturnRate * 1.0f) * deltaTime);
                    _glideOffsetWorld = Vector3.Lerp(_glideOffsetWorld, Vector3.zero, glideReturnBlend);
                }
            }
            else
            {
                float glideReturnBlend = 1f - Mathf.Exp(-visualSettings.glideReturnRate * deltaTime);
                _glideOffsetWorld = Vector3.Lerp(_glideOffsetWorld, Vector3.zero, glideReturnBlend);
            }

            float maxGlide = visualSettings.glideMaxOffsetAt0 * 1.5f;
            if (_glideOffsetWorld.magnitude > maxGlide)
                _glideOffsetWorld = _glideOffsetWorld.normalized * maxGlide;

            Vector3 roughOffsetWorld = Vector3.zero;
            if (isRoughContact)
            {
                float slideGate = IsSliding ? 1f : 0.5f;
                float baseLag = visualSettings.maxLagAt1 * 0.4f;
                float speedLag = tangentialSpeed * visualSettings.lagGainAt1 * 1.5f;
                float lagAmount = Mathf.Min(visualSettings.maxLagAt1 * 1.8f, baseLag + speedLag);
                roughOffsetWorld -= _tangentAxisPrimary * lagAmount * slideGate;

                float tremorGate = Mathf.Clamp01(effectEnvelope);
                float speedNormalized = Mathf.Clamp01(
                    tangentialSpeed / Mathf.Max(0.05f, visualSettings.slideSpeedThresholdEnter + 0.15f));

                float tremorHzMin = visualSettings.tremorHzRange.x;
                float tremorHzMax = visualSettings.tremorHzRange.y;
                float tremorHzTarget = Mathf.Lerp(tremorHzMin, tremorHzMax, 0.7f);
                _tremorFrequencyHz = Mathf.Lerp(_tremorFrequencyHz, tremorHzTarget, 1f - Mathf.Exp(-6f * deltaTime));
                _tremorPhase += 2f * Mathf.PI * _tremorFrequencyHz * deltaTime;

                float tremorAmplitude = visualSettings.tremorAmpAt1 + visualSettings.tremorAmpPerSpeed * tangentialSpeed;
                tremorAmplitude = Mathf.Clamp(tremorAmplitude, 0f, visualSettings.tremorAmpMax * 2.0f);
                float tremorScale = tremorAmplitude * (0.7f + 0.6f * speedNormalized) * tremorGate * slideGate;

                float s1 = Mathf.Sin(_tremorPhase);
                float s2 = Mathf.Sin(_tremorPhase * 1.37f + 0.25f);
                roughOffsetWorld += (_tangentAxisPrimary * s1 + _tangentAxisSecondary * 0.4f * s2) * tremorScale;
            }

            effectTargetWorld = _glideOffsetWorld * smoothIceBlend + roughOffsetWorld;

            float maxEffect = visualSettings.maxEffectOffset;
            if (effectTargetWorld.sqrMagnitude > maxEffect * maxEffect)
                effectTargetWorld = effectTargetWorld.normalized * maxEffect;
        }
        else
        {
            float glideReturnBlend = 1f - Mathf.Exp(-visualSettings.glideReturnRate * deltaTime);
            _glideOffsetWorld = Vector3.Lerp(_glideOffsetWorld, Vector3.zero, glideReturnBlend);
        }

        {
            const float effectFollowHzFree = 18f;
            const float effectFollowHzSmooth = SmoothEffectFollowHz;
            const float effectFollowHzRough = 7f;

            float effectContactHz = Mathf.Lerp(effectFollowHzSmooth, effectFollowHzRough, roughBlend);
            float effectFollowHz = isTextureSurface
                ? Mathf.Lerp(effectFollowHzFree, effectContactHz, Mathf.Clamp01(effectEnvelope))
                : effectFollowHzFree;

            float effectBlend = 1f - Mathf.Exp(-2f * Mathf.PI * effectFollowHz * deltaTime);
            _smoothEffectOffsetWorld = Vector3.Lerp(_smoothEffectOffsetWorld, effectTargetWorld, effectBlend);
        }

        EffectOffset = _smoothEffectOffsetWorld;

        Vector3 finalPosition = BasePos + EffectOffset;
        Quaternion finalRotation = BaseRot;
        decoyArmatureRoot.SetPositionAndRotation(finalPosition, finalRotation);

        SetIndicator(effectEnvelope > 0.01f || IsTouching);

        var publishChannel = ResolveTelemetryPublishChannel();
        if (publishChannel)
            publishChannel.Publish(HandTelemetrySnapshot.FromContact(in contactState));
    }

    HandTelemetryChannel ResolveTelemetryPublishChannel()
    {
        if (_telemetryChannelOverride)
            return _telemetryChannelOverride;
        var globalData = globals ? globals : HapticsGlobalData.Instance;
        return globalData ? globalData.GetHandTelemetryChannel(handTelemetrySide) : null;
    }

    void FixedUpdate() { }

    void SetIndicator(bool show)
    {
        if (indicator && indicator.activeSelf != show)
            indicator.SetActive(show);
    }

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos || !xrArmatureRoot)
            return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(xrArmatureRoot.position, contactProbeRadius);
    }
}
