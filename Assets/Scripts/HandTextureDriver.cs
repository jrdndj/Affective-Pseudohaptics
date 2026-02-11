using UnityEngine;
using System.Collections.Generic;

[DefaultExecutionOrder(11000)]
public class HandTextureDriver : MonoBehaviour
{
    public HapticsGlobalData globals;

    // Current global visual settings snapshot
    HapticsGlobalData.VisualSettings _settings;
    HapticsGlobalData.VisualSettings V => _settings;

    [Header("XR Hand (actual tracked)")]
    public SkinnedMeshRenderer xrRenderer;
    public Transform xrArmatureRoot;

    [Header("Decoy Hand (visibly rendered)")]
    public SkinnedMeshRenderer decoyRenderer;
    public Transform decoyArmatureRoot;

    [Header("Decoy hand colliders")]
    public bool autoFindDecoyColliders = true;
    public Collider[] decoyJointColliders;

    [Header("Joint contact settings")]
    public float jointProbeRadius = 0.01f;

    [Tooltip("Max squared distance for valid joint contact")]
    public float maxJointContactDistance = 0.025f;

    [Header("Debug Indicator")]
    public GameObject indicator;

    [Header("Contact query (effects)")]
    public LayerMask contactLayers = ~0;
    public float contactProbeRadius = 0.06f; // Fallback sphere radius

    public bool useMotionEffects = true;

    public float contactReleaseTime = 1.0f;   // Smooth release after leaving touch

    public bool drawGizmos = false;

    // Telemetry properties
    public float   CurrentRoughness01 { get; private set; }
    public bool    IsTouching         { get; private set; } // Smoothed envelope
    public bool    IsTouchingRaw      { get; private set; } // Instant raw contact
    public bool    IsSliding          { get; private set; }
    public float   TangentialSpeed    { get; private set; }
    public Vector3 TangentialVelocity { get; private set; }
    public Vector3 ContactNormal      { get; private set; } = Vector3.up;

    public SurfaceData CurrentSurface { get; private set; }

    // World-space contact point (or last touch point)
    public Vector3 ContactPoint { get; private set; }

    // Contact coverage: fraction of joints in contact
    public float ContactCoverage01 { get; private set; }

    public Vector3    BasePos      { get; private set; }
    public Quaternion BaseRot      { get; private set; }
    public Vector3    EffectOffset { get; private set; }

    // Internal state
    readonly Dictionary<string, Transform> srcByName = new Dictionary<string, Transform>();
    readonly List<(Transform src, Transform dst)> bonePairs = new List<(Transform src, Transform dst)>();
    readonly Collider[] hits = new Collider[32];

    struct PoseSample { public Vector3 pos; public Quaternion rot; public float time; }
    const int PoseCap = 128;
    readonly PoseSample[] poseBuf = new PoseSample[PoseCap];
    int poseCount;

    Vector3 lastSrcPos;
    Vector3 velLP;
    bool    sliding;

    // Glide effect state
    Vector3 glideVel;
    Vector3 glideOffset;

    // Frame coordinate system
    Vector3 nrm = Vector3.up, t1 = Vector3.right, t2 = Vector3.forward;

    // Roughness state (0=smooth, 1=rough, 0.5=neutral)
    float currentRoughness;

    // Tremor oscillation state
    float tremorPhase;
    float tremorHz;

    // Smoothed contact and effect envelopes
    float contactEnv;              // Smooth contact envelope (0..1)
    Vector3 contactPointSmooth;    // Smoothed contact point

    // Smoothed base pose
    Vector3    basePosSmooth;
    Quaternion baseRotSmooth;

    // Smoothed effect offset
    Vector3 effectOffsetSmooth;

    // Legacy physics fields (no longer used for motion)
    Vector3    desiredPos;
    Quaternion desiredRot;
    bool       hasDesiredPose;

    // Current surface type (debug and telemetry)
    SurfaceType _surfaceType = SurfaceType.Neutral;

    void Awake()
    {
        if (!globals) globals = HapticsGlobalData.Instance;
        if (!globals)
        {
            Debug.LogError("[HandTextureDriver] No HapticsGlobalData found in scene.");
            enabled = false;
            return;
        }

        _settings = globals.visual;

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

        lastSrcPos = xrArmatureRoot.position;
        velLP      = Vector3.zero;
        poseCount  = 0;

        // Hide XR hand mesh, show only decoy
        foreach (var r in xrRenderer.GetComponentsInChildren<Renderer>(true))
            r.enabled = false;

        // Initialize decoy root pose to match XR pose
        decoyArmatureRoot.SetPositionAndRotation(xrArmatureRoot.position, xrArmatureRoot.rotation);

        SetIndicator(false);

        var cfg = V;
        currentRoughness = Mathf.Clamp01(cfg.defaultRoughness);
        glideVel         = Vector3.zero;
        glideOffset      = Vector3.zero;

        tremorPhase = Random.value * Mathf.PI * 2f;
        tremorHz    = Random.Range(cfg.tremorHzRange.x, cfg.tremorHzRange.y);

        // Initialize smoothed pose and effect state
        basePosSmooth      = xrArmatureRoot.position;
        baseRotSmooth      = xrArmatureRoot.rotation;
        effectOffsetSmooth = Vector3.zero;
        contactEnv         = 0f;
        contactPointSmooth = xrArmatureRoot.position;
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
        _settings = globals.visual;
    }

    void BuildBonePairs()
    {
        srcByName.Clear();
        foreach (var b in xrRenderer.bones)
            if (b && !srcByName.ContainsKey(b.name))
                srcByName.Add(b.name, b);

        bonePairs.Clear();
        foreach (var d in decoyRenderer.bones)
        {
            if (!d) continue;
            if (d == decoyArmatureRoot) continue; // root driven separately

            string key = d.name.EndsWith("_Ghost")
                ? d.name.Substring(0, d.name.Length - 6)
                : d.name;

            if (srcByName.TryGetValue(key, out var s))
                bonePairs.Add((s, d));
        }

        if (bonePairs.Count == 0)
            Debug.LogWarning("[HandTextureDriver] No bone name matches (children).");
    }

    void LateUpdate()
    {
        var cfg = V;

        float dt  = Mathf.Max(Time.deltaTime, 1e-5f);
        float now = Time.time;

        // Mirror bones from source to decoy (local transforms)
        for (int i = 0; i < bonePairs.Count; i++)
        {
            var pair = bonePairs[i];
            if (!pair.src || !pair.dst) continue;
            pair.dst.localPosition = pair.src.localPosition;
            pair.dst.localRotation = pair.src.localRotation;
            pair.dst.localScale    = pair.src.localScale;
        }

        // Track source motion and history
        Vector3 srcPos  = xrArmatureRoot.position;
        Vector3 instVel = (srcPos - lastSrcPos) / dt;
        lastSrcPos = srcPos;

        if (cfg.velocityLowpassHz > 0f)
        {
            float a = 1f - Mathf.Exp(-2f * Mathf.PI * cfg.velocityLowpassHz * dt);
            velLP = Vector3.Lerp(velLP, instVel, a);
        }
        else
        {
            velLP = instVel;
        }

        PushPose(new PoseSample { pos = srcPos, rot = xrArmatureRoot.rotation, time = now });

        // Sample contact from decoy joint colliders
        bool     touchingRaw        = false;
        Collider bestSurfaceCollider = null;
        Vector3  bestSurfaceCp       = srcPos;
        float    bestSurfaceD2       = float.PositiveInfinity;

        Vector3 cpAccum     = Vector3.zero;
        float   weightAccum = 0f;
        int     contactCount = 0;

        float maxD2 = Mathf.Max(1e-6f, maxJointContactDistance * maxJointContactDistance);

        Collider[] jointCols = decoyJointColliders;
        if (jointCols != null && jointCols.Length > 0)
        {
            for (int j = 0; j < jointCols.Length; j++)
            {
                var hc = jointCols[j];
                if (!hc || !hc.enabled) continue;

                Vector3 jointCenter = hc.bounds.center;

                int n = Physics.OverlapSphereNonAlloc(
                    jointCenter,
                    jointProbeRadius,
                    hits,
                    contactLayers,
                    QueryTriggerInteraction.Ignore
                );

                for (int i = 0; i < n; i++)
                {
                    var c = hits[i];
                    if (!c) continue;
                    if (c.transform.IsChildOf(xrArmatureRoot)) continue;
                    if (decoyArmatureRoot && c.transform.IsChildOf(decoyArmatureRoot)) continue;

                    Vector3 cp = c.ClosestPoint(jointCenter);
                    float d2 = (cp - jointCenter).sqrMagnitude;
                    if (d2 > maxD2) continue;

                    touchingRaw = true;
                    contactCount++;

                    float w = 1f / (0.0005f + d2);
                    cpAccum     += cp * w;
                    weightAccum += w;

                    if (d2 < bestSurfaceD2)
                    {
                        bestSurfaceD2      = d2;
                        bestSurfaceCollider = c;
                        bestSurfaceCp       = cp;
                    }
                }
            }
        }
        else
        {
            // Fallback: single sphere near XR root
            int n = Physics.OverlapSphereNonAlloc(
                srcPos, contactProbeRadius, hits, contactLayers,
                QueryTriggerInteraction.Ignore
            );
            for (int i = 0; i < n; i++)
            {
                var c = hits[i];
                if (!c) continue;
                if (c.transform.IsChildOf(xrArmatureRoot)) continue;
                if (decoyArmatureRoot && c.transform.IsChildOf(decoyArmatureRoot)) continue;

                Vector3 cp = c.ClosestPoint(srcPos);
                float d2 = (cp - srcPos).sqrMagnitude;
                if (d2 > maxD2) continue;

                touchingRaw = true;
                contactCount++;

                float w = 1f / (0.0005f + d2);
                cpAccum     += cp * w;
                weightAccum += w;

                if (d2 < bestSurfaceD2)
                {
                    bestSurfaceD2      = d2;
                    bestSurfaceCollider = c;
                    bestSurfaceCp       = cp;
                }
            }
        }

        if (touchingRaw && weightAccum > 0f)
        {
            Vector3 cp = cpAccum / weightAccum;

            // Smooth contact point (avoid jittery highlight)
            float cpHz = 20f;
            float cpA  = 1f - Mathf.Exp(-2f * Mathf.PI * cpHz * dt);
            contactPointSmooth = Vector3.Lerp(contactPointSmooth, cp, cpA);
            ContactPoint       = contactPointSmooth;

            int denom = Mathf.Max(1, jointCols != null ? jointCols.Length : 1);
            ContactCoverage01 = Mathf.Clamp01((float)contactCount / denom);
        }
        else
        {
            ContactCoverage01 = 0f;
        }

        if (touchingRaw && bestSurfaceCollider != null)
        {
            Vector3 approx = (srcPos - bestSurfaceCp);
            nrm = approx.sqrMagnitude > 1e-10f ? approx.normalized : bestSurfaceCollider.transform.up;
        }

        IsTouchingRaw = touchingRaw;
        UpdateContactEnvelope(touchingRaw, dt);
        bool touching = IsTouching;

        // 4) tangential velocity
        Vector3 vTan = Tangential(velLP, nrm);
        float   speed = vTan.magnitude;

        if (!sliding && speed >= cfg.slideSpeedThresholdEnter) sliding = true;
        else if (sliding && speed <= cfg.slideSpeedThresholdExit) sliding = false;

        // 5) surface type  -> discrete tactile behavior
        CurrentSurface = null;
        SurfaceType surfaceType = SurfaceType.Neutral;

        if (bestSurfaceCollider)
        {
            var surface = bestSurfaceCollider.GetComponentInParent<SurfaceData>();
            if (surface)
            {
                CurrentSurface = surface;
                surfaceType    = surface.surfaceType;
            }
        }

        switch (surfaceType)
        {
            case SurfaceType.Smooth:
                currentRoughness = 0f;
                break;
            case SurfaceType.Rough:
                currentRoughness = 1f;
                break;
            default:
                currentRoughness = 0.5f;
                break;
        }
        CurrentRoughness01 = currentRoughness;
        _surfaceType       = surfaceType;

        bool isEffectSurface = (surfaceType == SurfaceType.Smooth || surfaceType == SurfaceType.Rough);

        // envelope used to fade pseudo-haptic effects
        float effectEnv   = 0f;
        float iceFactor   = 0f;
        float roughFactor = 0f;

        if (isEffectSurface)
        {
            effectEnv = contactEnv;
            if (effectEnv > 1e-3f)
            {
                if (surfaceType == SurfaceType.Smooth)
                    iceFactor = effectEnv;
                else if (surfaceType == SurfaceType.Rough)
                    roughFactor = effectEnv;
            }
        }

        // 6) base pose (with extra lag only on effect surfaces)
        Vector3    basePos = srcPos;
        Quaternion baseRot = xrArmatureRoot.rotation;

        bool roughContactNow  = (effectEnv > 1e-3f && surfaceType == SurfaceType.Rough);
        bool smoothContactNow = (effectEnv > 1e-3f && surfaceType == SurfaceType.Smooth);

        if (roughContactNow && touching)
        {
            float latencyMs = cfg.latencyMsAt1;
            if (latencyMs > 0f)
            {
                float tTarget = now - 0.001f * latencyMs;
                SampleDelayedPose(tTarget, out basePos, out baseRot);
            }
        }

        if (speed > 1e-6f)
        {
            t1 = vTan / Mathf.Max(speed, 1e-6f);
        }
        else
        {
            t1 = Vector3.Cross(nrm, Vector3.up);
            if (t1.sqrMagnitude < 1e-6f)
                t1 = Vector3.Cross(nrm, Vector3.right);
            t1.Normalize();
        }
        t2 = Vector3.Normalize(Vector3.Cross(nrm, t1));

        // smooth base pose:
        // - free space / neutral: track hard (almost no lag)
        // - Smooth/Rough surfaces: slower follow so offsets show up
        {
            float freeHz   = 40f;  // snappy when no pseudo-haptics
            float smoothHz = 12f;
            float roughHz  = 7f;

            float contactHz    = Mathf.Lerp(smoothHz, roughHz, roughFactor);
            float baseFollowHz = isEffectSurface
                ? Mathf.Lerp(freeHz, contactHz, Mathf.Clamp01(effectEnv))
                : freeHz;

            float aBase = 1f - Mathf.Exp(-2f * Mathf.PI * baseFollowHz * dt);
            basePosSmooth = Vector3.Lerp(basePosSmooth, basePos, aBase);
            baseRotSmooth = Quaternion.Slerp(baseRotSmooth, baseRot, aBase);
        }

        BasePos = basePosSmooth;
        BaseRot = baseRotSmooth;

        // Motion effects (pseudo-haptic glide and tremor on texture surfaces)
        Vector3 effectTarget = Vector3.zero;

        if (useMotionEffects && isEffectSurface && effectEnv > 1e-3f)
        {
            // Smooth surface: glide effect
            glideOffset -= Vector3.Dot(glideOffset, nrm) * nrm;
            glideVel    -= Vector3.Dot(glideVel,    nrm) * nrm;

            if (smoothContactNow)
            {
                if (speed > cfg.glideDeadzoneSpeed)
                {
                    // Predictive offset for visible glide
                    float predictTime = 0.12f;
                    Vector3 targetOffset = vTan * predictTime;

                    float accel = cfg.glideAccel;
                    float a     = 1f - Mathf.Exp(-accel * dt);
                    glideOffset = Vector3.Lerp(glideOffset, targetOffset, a);
                }
            }
            else
            {
                // Decay glide offset when not on smooth
                float returnRate = cfg.glideReturnRate;
                float r          = 1f - Mathf.Exp(-returnRate * dt);
                glideOffset      = Vector3.Lerp(glideOffset, Vector3.zero, r);
            }

            // Clamp glide globally
            float maxGlide = cfg.glideMaxOffsetAt0 * 1.5f;
            if (glideOffset.magnitude > maxGlide)
                glideOffset = glideOffset.normalized * maxGlide;

            // Rough surface: lag and tremor
            Vector3 roughEffect = Vector3.zero;
            if (roughContactNow)
            {
                float slideGate = sliding ? 1f : 0.5f;

                float baseLag   = cfg.maxLagAt1 * 0.4f;
                float speedLag  = speed * cfg.lagGainAt1 * 1.5f;
                float lag       = Mathf.Min(cfg.maxLagAt1 * 1.8f, baseLag + speedLag);
                roughEffect -= t1 * lag * slideGate;

                float tremGate = Mathf.Clamp01(effectEnv);
                float spd01    = Mathf.Clamp01(speed / Mathf.Max(0.05f, cfg.slideSpeedThresholdEnter + 0.15f));

                // Tremor oscillation
                float tremHzMin    = cfg.tremorHzRange.x;
                float tremHzMax    = cfg.tremorHzRange.y;
                float tremHzTarget = Mathf.Lerp(tremHzMin, tremHzMax, 0.7f);
                tremorHz = Mathf.Lerp(tremorHz, tremHzTarget, 1f - Mathf.Exp(-6f * dt));
                tremorPhase += 2f * Mathf.PI * tremorHz * dt;

                float baseAmp = cfg.tremorAmpAt1 + cfg.tremorAmpPerSpeed * speed;
                baseAmp = Mathf.Clamp(baseAmp, 0f, cfg.tremorAmpMax * 2.0f);

                float amp = baseAmp * (0.7f + 0.6f * spd01) * tremGate * slideGate;

                float s1 = Mathf.Sin(tremorPhase);
                float s2 = Mathf.Sin(tremorPhase * 1.37f + 0.25f);

                roughEffect += (t1 * s1 + t2 * 0.4f * s2) * amp;
            }

            Vector3 smoothPart = glideOffset * iceFactor;
            Vector3 roughPart  = roughEffect;

            effectTarget = smoothPart + roughPart;

            if (effectTarget.sqrMagnitude > cfg.maxEffectOffset * cfg.maxEffectOffset)
                effectTarget = effectTarget.normalized * cfg.maxEffectOffset;
        }
        else
        {
            // No pseudo-haptics on neutral; decay glide
            float returnRate = cfg.glideReturnRate;
            float r          = 1f - Mathf.Exp(-returnRate * dt);
            glideOffset      = Vector3.Lerp(glideOffset, Vector3.zero, r);
        }

        // Final smoothing for controlled offsets
        {
            float freeHz   = 24f;
            float smoothHz = 12f;
            float roughHz  = 7f;

            float contactHz = Mathf.Lerp(smoothHz, roughHz, roughFactor);
            float followHz  = isEffectSurface
                ? Mathf.Lerp(freeHz, contactHz, Mathf.Clamp01(effectEnv))
                : freeHz;

            float aEff = 1f - Mathf.Exp(-2f * Mathf.PI * followHz * dt);
            effectOffsetSmooth = Vector3.Lerp(effectOffsetSmooth, effectTarget, aEff);
        }

        EffectOffset = effectOffsetSmooth;

        // Final pose for decoy hand (direct transform, no rigidbody)
        Vector3 finalPos = BasePos + EffectOffset;
        Quaternion finalRot = BaseRot;

        decoyArmatureRoot.SetPositionAndRotation(finalPos, finalRot);

        // Keep desired pose for debugging or future reintroduction
        desiredPos    = finalPos;
        desiredRot    = finalRot;
        hasDesiredPose = true;

        // Telemetry output
        IsSliding          = sliding;
        TangentialVelocity = vTan;
        TangentialSpeed    = speed;
        ContactNormal      = nrm;

        SetIndicator(contactEnv > 0.01f);
    }

    // Rigidbody no longer used for motion; kept as placeholder
    void FixedUpdate()
    {
        // Intentionally empty – decoy driven by transform in LateUpdate
    }

    void UpdateContactEnvelope(bool touchingRaw, float dt)
    {
        // Smooth contact envelope for fade transitions
        float target = touchingRaw ? 1f : 0f;

        float attackTau  = Mathf.Max(0.01f, contactReleaseTime * 0.15f);
        float releaseTau = Mathf.Max(0.01f, contactReleaseTime);

        float tau = (target > contactEnv) ? attackTau : releaseTau;
        float k   = 1f - Mathf.Exp(-dt / tau);

        contactEnv = Mathf.Lerp(contactEnv, target, k);
        IsTouching = contactEnv > 0.02f;
    }

    static Vector3 Tangential(Vector3 v, Vector3 n) => v - Vector3.Dot(v, n) * n;

    void PushPose(PoseSample s)
    {
        if (poseCount < PoseCap)
        {
            poseBuf[poseCount++] = s;
        }
        else
        {
            for (int i = 1; i < PoseCap; i++)
                poseBuf[i - 1] = poseBuf[i];
            poseBuf[PoseCap - 1] = s;
        }
    }

    void SampleDelayedPose(float tTarget, out Vector3 pos, out Quaternion rot)
    {
        if (poseCount == 0)
        {
            pos = xrArmatureRoot.position;
            rot = xrArmatureRoot.rotation;
            return;
        }

        PoseSample newest = poseBuf[poseCount - 1];
        if (tTarget >= newest.time)
        {
            pos = newest.pos;
            rot = newest.rot;
            return;
        }

        PoseSample a = newest, b = newest;
        for (int i = poseCount - 2; i >= 0; i--)
        {
            b = poseBuf[i];
            if (b.time <= tTarget)
            {
                a = poseBuf[i + 1];
                break;
            }
            a = b;
        }

        float t = Mathf.Approximately(a.time, b.time)
            ? 0f
            : Mathf.InverseLerp(b.time, a.time, tTarget);

        pos = Vector3.Lerp(b.pos, a.pos, t);
        rot = Quaternion.Slerp(b.rot, a.rot, t);
    }

    void SetIndicator(bool show)
    {
        if (indicator && indicator.activeSelf != show)
            indicator.SetActive(show);
    }

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos || !xrArmatureRoot) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(xrArmatureRoot.position, contactProbeRadius);
    }
}
