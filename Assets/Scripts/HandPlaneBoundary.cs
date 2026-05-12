using UnityEngine;

/// <summary>
/// Enforces a plane boundary on a decoy hand using collider positions.
/// Prevents hand from passing through a Z-aligned plane, and optionally pins
/// the decoy hand to the plane when it hovers nearby so tracking jitter does
/// not show up as visible wobble against the surface.
/// </summary>
[DefaultExecutionOrder(12010)]
public class HandPlaneColliderBoundary : MonoBehaviour
{
    [Header("Hand Roots")]
    public Transform xrHandRoot;      // Reference hand (unused)
    public Transform decoyHandRoot;   // Clamped hand root

    [Header("Plane")]
    public Transform planeTransform;  // XY plane at position.z defines boundary
    public bool flipDirection = false; // Z+ blocked if true, Z- blocked if false
    public float planeSkin = 0.002f;  // Minimum distance from plane
    public float planeThickness = 0.0f; // Extra blocked region thickness

    [Header("Decoy Colliders")]
    public bool autoFindDecoyColliders = true;  // Auto-grab colliders under decoyHandRoot
    public Collider[] decoyColliders;

    [Header("Surface Stick")]
    [Tooltip("Pin the decoy hand to the plane when it's close, so XR tracking jitter does not visibly wobble the hand against the surface. Releases when the user clearly pulls away.")]
    public bool enableSurfaceStick = true;
    [Tooltip("Distance from the plane (m) at which the decoy hand begins sticking.")]
    public float stickEnterDistance = 0.018f;
    [Tooltip("Distance from the plane (m) the decoy must clear before the stick releases. Provides hysteresis vs. stickEnterDistance.")]
    public float stickReleaseDistance = 0.060f;
    [Tooltip("Outward speed along the plane normal (m/s) that immediately releases the stick, so an intentional pull-away is not held back.")]
    public float stickReleaseSpeed = 0.35f;
    [Tooltip("How quickly the decoy eases into the pinned position when first entering the stick band (Hz). Higher = snappier engage.")]
    public float stickEnterFollowHz = 18f;
    [Tooltip("How tightly the decoy is held against the plane while stuck (Hz). Higher = stiffer pin against jitter.")]
    public float stickHoldFollowHz = 45f;
    [Tooltip("Outward-velocity low-pass cutoff (Hz) used for release detection. Low values ignore fast tracking jitter.")]
    public float outwardVelocityFilterHz = 8f;

    [Header("Debug")]
    public bool drawGizmos = true;
    public float gizmoSize = 0.05f;
    public Color planeColor = new Color(0.2f, 0.8f, 1f, 0.7f);

    bool _isStuck;
    float _prevSignedDist;
    bool _hasPrevSignedDist;
    float _outwardVelocitySmoothed;

    void Awake()
    {
        if (!decoyHandRoot)
            Debug.LogWarning("[HandPlaneColliderBoundary] Decoy hand root not assigned.");

        if (!planeTransform)
            Debug.LogWarning("[HandPlaneColliderBoundary] Plane transform not assigned.");

        if (autoFindDecoyColliders && decoyHandRoot)
            decoyColliders = decoyHandRoot.GetComponentsInChildren<Collider>(true);
    }

    void OnDisable()
    {
        _isStuck = false;
        _hasPrevSignedDist = false;
        _outwardVelocitySmoothed = 0f;
    }

    void LateUpdate()
    {
        if (!decoyHandRoot || !planeTransform || decoyColliders == null || decoyColliders.Length == 0)
            return;

        float planeZ   = planeTransform.position.z;
        float dirSign  = flipDirection ? -1f : 1f;
        float minDist  = Mathf.Max(0f, planeSkin + planeThickness);
        Vector3 planeNormal = Vector3.forward * dirSign;

        bool havePenetration = false;
        float maxPenetration = 0f;
        float minSignedDist = float.PositiveInfinity;

        for (int i = 0; i < decoyColliders.Length; i++)
        {
            Collider col = decoyColliders[i];
            if (!col || !col.enabled) continue;

            Vector3 p = col.transform.position;

            float dist = (p.z - planeZ) * dirSign;
            if (dist < minSignedDist)
                minSignedDist = dist;

            float penetration = minDist - dist;
            if (penetration > 0f && penetration > maxPenetration)
            {
                maxPenetration = penetration;
                havePenetration = true;
            }
        }

        if (havePenetration && maxPenetration > 0f)
        {
            decoyHandRoot.position += planeNormal * maxPenetration;
            minSignedDist += maxPenetration;
        }

        if (!enableSurfaceStick)
        {
            _isStuck = false;
            _hasPrevSignedDist = false;
            _outwardVelocitySmoothed = 0f;
            return;
        }

        float dt = Mathf.Max(Time.deltaTime, 1e-5f);

        // Smooth outward velocity so high-frequency tracking jitter does not falsely trigger release.
        float rawOutwardVel = _hasPrevSignedDist ? (minSignedDist - _prevSignedDist) / dt : 0f;
        float velBlend = 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(0.1f, outwardVelocityFilterHz) * dt);
        _outwardVelocitySmoothed = Mathf.Lerp(_outwardVelocitySmoothed, rawOutwardVel, velBlend);
        _prevSignedDist = minSignedDist;
        _hasPrevSignedDist = true;

        // Enforce hysteresis: enter close, release further.
        float safeReleaseDistance = Mathf.Max(stickReleaseDistance, stickEnterDistance + 0.005f);

        if (_isStuck)
        {
            bool clearedReleaseBand = minSignedDist > safeReleaseDistance;
            bool pullingAway = _outwardVelocitySmoothed > stickReleaseSpeed;
            if (clearedReleaseBand || pullingAway)
                _isStuck = false;
        }
        else if (minSignedDist <= stickEnterDistance)
        {
            _isStuck = true;
        }

        if (!_isStuck)
            return;

        // While stuck, the closest collider should sit at exactly minDist above the plane.
        // We translate the decoy root along the plane normal so XR tracking jitter does not show.
        float targetOffsetAlongNormal = minDist - minSignedDist;
        if (Mathf.Abs(targetOffsetAlongNormal) < 1e-5f)
            return;

        // Use a faster follow when far from the target pin (initial engage),
        // and a tighter follow when already near it (steady-state hold).
        float blendHz = (Mathf.Abs(targetOffsetAlongNormal) > 0.01f)
            ? stickEnterFollowHz
            : stickHoldFollowHz;
        float blend = 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(1f, blendHz) * dt);
        float appliedOffsetAlongNormal = targetOffsetAlongNormal * blend;

        decoyHandRoot.position += planeNormal * appliedOffsetAlongNormal;

        // Keep the stored signed distance consistent with the position we just applied so
        // the next-frame outward-velocity estimate reflects XR motion, not our correction.
        _prevSignedDist += appliedOffsetAlongNormal;
    }

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos || !planeTransform)
            return;

        Gizmos.color = planeColor;
        Vector3 origin = planeTransform.position;
        Vector3 n = (flipDirection ? Vector3.back : Vector3.forward);

        Gizmos.DrawLine(origin, origin + n * gizmoSize * 3f);
        Gizmos.DrawSphere(origin, gizmoSize * 0.15f);
        Vector3 right = Vector3.right * gizmoSize;
        Vector3 up    = Vector3.up    * gizmoSize;
        Gizmos.DrawLine(origin - right, origin + right);
        Gizmos.DrawLine(origin - up,    origin + up);
    }
}
