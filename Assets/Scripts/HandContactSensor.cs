using UnityEngine;
using System.Collections.Generic;

// Overlap probes on decoy joints; optional broadphase + throttled detail pass.
public class HandContactSensor
{
    readonly Collider[] _hits = new Collider[32];
    readonly Dictionary<Collider, SurfaceData> _surfaceByCollider = new Dictionary<Collider, SurfaceData>();

    Vector3 _normal = Vector3.up;
    Vector3 _contactPointSmooth;
    float _contactEnv;
    bool _sliding;
    float _detailTimer;
    int _jointCursor;
    float _cachedCoverage01;
    SurfaceData _cachedSurface;
    SurfaceType _cachedSurfaceType = SurfaceType.Neutral;

    public void Initialize(Vector3 initialContactPoint)
    {
        _normal = Vector3.up;
        _contactPointSmooth = initialContactPoint;
        _contactEnv = 0f;
        _sliding = false;
        _detailTimer = 0f;
        _jointCursor = 0;
        _cachedCoverage01 = 0f;
        _cachedSurface = null;
        _cachedSurfaceType = SurfaceType.Neutral;
    }

    public HandContactState Sample(
        Transform xrArmatureRoot,
        Transform decoyArmatureRoot,
        Collider[] decoyJointColliders,
        LayerMask contactLayers,
        float jointProbeRadius,
        float maxJointContactDistance,
        float contactProbeRadius,
        float contactReleaseTime,
        bool useBroadphase,
        float broadphaseRadius,
        int jointsPerFrame,
        float detailUpdateHz,
        Vector3 sourcePosition,
        Vector3 sourceVelocity,
        in HapticsGlobalData.VisualSettings settings,
        float dt)
    {
        bool touchingRaw = false;
        bool runDetail = true;

        if (detailUpdateHz > 0f)
        {
            _detailTimer += dt;
            float interval = 1f / detailUpdateHz;
            if (_detailTimer < interval)
                runDetail = false;
            else
                _detailTimer = 0f;
        }

        if (useBroadphase)
        {
            int broadCount = Physics.OverlapSphereNonAlloc(
                sourcePosition,
                Mathf.Max(broadphaseRadius, 1e-4f),
                _hits,
                contactLayers,
                QueryTriggerInteraction.Ignore
            );
            touchingRaw = broadCount > 0;
            if (!touchingRaw)
                runDetail = false;
        }

        if (!runDetail)
        {
            // Throttled frame: skip joint probes, keep last surface/coverage.
            Vector3 tangentVelocity = Tangential(sourceVelocity, _normal);
            if (!touchingRaw)
            {
                _sliding = false;
                UpdateContactEnvelope(false, contactReleaseTime, dt);
                return BuildState(
                    touchingRaw,
                    tangentVelocity,
                    settings,
                    _cachedCoverage01 = 0f,
                    null,
                    _cachedSurfaceType = SurfaceType.Neutral
                );
            }

            UpdateContactEnvelope(touchingRaw, contactReleaseTime, dt);
            return BuildState(
                touchingRaw,
                tangentVelocity,
                settings,
                _cachedCoverage01,
                _cachedSurface,
                _cachedSurfaceType
            );
        }

        Collider bestSurfaceCollider = null;
        Vector3 bestSurfaceCp = sourcePosition;
        float bestSurfaceD2 = float.PositiveInfinity;

        Vector3 cpAccum = Vector3.zero;
        float weightAccum = 0f;
        int contactCount = 0;
        int sampleCount = 0;

        float maxD2 = Mathf.Max(1e-6f, maxJointContactDistance * maxJointContactDistance);
        Collider[] jointCols = decoyJointColliders;

        if (jointCols != null && jointCols.Length > 0)
        {
            int totalJoints = jointCols.Length;
            int sampleTarget = Mathf.Clamp(jointsPerFrame, 1, totalJoints);
            for (int j = 0; j < sampleTarget; j++)
            {
                int idx = (_jointCursor + j) % totalJoints;
                var handCollider = jointCols[idx];
                if (!handCollider || !handCollider.enabled) continue;

                Vector3 jointCenter = handCollider.bounds.center;
                sampleCount++;
                int count = Physics.OverlapSphereNonAlloc(
                    jointCenter,
                    jointProbeRadius,
                    _hits,
                    contactLayers,
                    QueryTriggerInteraction.Ignore
                );

                ProcessHits(
                    count,
                    jointCenter,
                    xrArmatureRoot,
                    decoyArmatureRoot,
                    maxD2,
                    ref touchingRaw,
                    ref contactCount,
                    ref cpAccum,
                    ref weightAccum,
                    ref bestSurfaceCollider,
                    ref bestSurfaceCp,
                    ref bestSurfaceD2
                );
            }

            _jointCursor = (_jointCursor + sampleTarget) % totalJoints;
        }
        else
        {
            int count = Physics.OverlapSphereNonAlloc(
                sourcePosition,
                contactProbeRadius,
                _hits,
                contactLayers,
                QueryTriggerInteraction.Ignore
            );

            ProcessHits(
                count,
                sourcePosition,
                xrArmatureRoot,
                decoyArmatureRoot,
                maxD2,
                ref touchingRaw,
                ref contactCount,
                ref cpAccum,
                ref weightAccum,
                ref bestSurfaceCollider,
                ref bestSurfaceCp,
                ref bestSurfaceD2
            );

            sampleCount = 1;
        }

        float coverage01 = 0f;
        if (touchingRaw && weightAccum > 0f)
        {
            Vector3 cp = cpAccum / weightAccum;
            float cpHz = 20f;
            float cpA = 1f - Mathf.Exp(-2f * Mathf.PI * cpHz * dt);
            _contactPointSmooth = Vector3.Lerp(_contactPointSmooth, cp, cpA);

            int denom = Mathf.Max(1, sampleCount);
            coverage01 = Mathf.Clamp01((float)contactCount / denom);
        }

        if (touchingRaw && bestSurfaceCollider != null)
        {
            Vector3 approx = sourcePosition - bestSurfaceCp;
            _normal = approx.sqrMagnitude > 1e-10f
                ? approx.normalized
                : bestSurfaceCollider.transform.up;
        }

        UpdateContactEnvelope(touchingRaw, contactReleaseTime, dt);

        Vector3 tangentialVelocity = Tangential(sourceVelocity, _normal);
        float tangentialSpeed = tangentialVelocity.magnitude;

        if (!_sliding && tangentialSpeed >= settings.slideSpeedThresholdEnter) _sliding = true;
        else if (_sliding && tangentialSpeed <= settings.slideSpeedThresholdExit) _sliding = false;

        SurfaceData surface = null;
        SurfaceType surfaceType = SurfaceType.Neutral;
        if (bestSurfaceCollider)
        {
            surface = GetSurfaceForCollider(bestSurfaceCollider);
            if (surface)
                surfaceType = surface.surfaceType;
        }

        _cachedCoverage01 = coverage01;
        _cachedSurface = surface;
        _cachedSurfaceType = surfaceType;

        return BuildState(
            touchingRaw,
            tangentialVelocity,
            tangentialSpeed,
            settings,
            coverage01,
            surface,
            surfaceType
        );
    }

    void ProcessHits(
        int hitCount,
        Vector3 probeCenter,
        Transform xrArmatureRoot,
        Transform decoyArmatureRoot,
        float maxD2,
        ref bool touchingRaw,
        ref int contactCount,
        ref Vector3 cpAccum,
        ref float weightAccum,
        ref Collider bestSurfaceCollider,
        ref Vector3 bestSurfaceCp,
        ref float bestSurfaceD2)
    {
        for (int i = 0; i < hitCount; i++)
        {
            var collider = _hits[i];
            if (!collider) continue;
            if (collider.transform.IsChildOf(xrArmatureRoot)) continue;
            if (decoyArmatureRoot && collider.transform.IsChildOf(decoyArmatureRoot)) continue;

            Vector3 cp = collider.ClosestPoint(probeCenter);
            float d2 = (cp - probeCenter).sqrMagnitude;
            if (d2 > maxD2) continue;

            touchingRaw = true;
            contactCount++;

            float w = 1f / (0.0005f + d2);
            cpAccum += cp * w;
            weightAccum += w;

            if (d2 < bestSurfaceD2)
            {
                bestSurfaceD2 = d2;
                bestSurfaceCollider = collider;
                bestSurfaceCp = cp;
            }
        }
    }

    void UpdateContactEnvelope(bool touchingRaw, float contactReleaseTime, float dt)
    {
        float target = touchingRaw ? 1f : 0f;
        float attackTau = Mathf.Max(0.01f, contactReleaseTime * 0.15f);
        float releaseTau = Mathf.Max(0.01f, contactReleaseTime);
        float tau = target > _contactEnv ? attackTau : releaseTau;
        float k = 1f - Mathf.Exp(-dt / tau);
        _contactEnv = Mathf.Lerp(_contactEnv, target, k);
    }

    SurfaceData GetSurfaceForCollider(Collider collider)
    {
        if (!collider)
            return null;
        if (_surfaceByCollider.TryGetValue(collider, out var cached))
            return cached;

        var surface = collider.GetComponentInParent<SurfaceData>();
        _surfaceByCollider[collider] = surface;
        return surface;
    }

    HandContactState BuildState(
        bool touchingRaw,
        Vector3 tangentialVelocity,
        in HapticsGlobalData.VisualSettings settings,
        float coverage01,
        SurfaceData surface,
        SurfaceType surfaceType)
    {
        float tangentialSpeed = tangentialVelocity.magnitude;
        return BuildState(
            touchingRaw,
            tangentialVelocity,
            tangentialSpeed,
            settings,
            coverage01,
            surface,
            surfaceType
        );
    }

    HandContactState BuildState(
        bool touchingRaw,
        Vector3 tangentialVelocity,
        float tangentialSpeed,
        in HapticsGlobalData.VisualSettings settings,
        float coverage01,
        SurfaceData surface,
        SurfaceType surfaceType)
    {
        return new HandContactState
        {
            TouchingRaw = touchingRaw,
            Touching = _contactEnv > 0.02f,
            Sliding = _sliding,
            ContactEnvelope = _contactEnv,
            ContactCoverage01 = coverage01,
            Roughness01 = SurfaceRoughness(surfaceType, settings.defaultRoughness),
            ContactPoint = _contactPointSmooth,
            ContactNormal = _normal,
            TangentialVelocity = tangentialVelocity,
            TangentialSpeed = tangentialSpeed,
            Surface = surface,
            SurfaceType = surfaceType
        };
    }

    static float SurfaceRoughness(SurfaceType surfaceType, float defaultRoughness)
    {
        switch (surfaceType)
        {
            case SurfaceType.Smooth:
                return 0f;
            case SurfaceType.Rough:
                return 1f;
            default:
                return Mathf.Clamp01(defaultRoughness);
        }
    }

    static Vector3 Tangential(Vector3 v, Vector3 n) => v - Vector3.Dot(v, n) * n;
}
