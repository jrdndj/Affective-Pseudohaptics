using UnityEngine;

/// <summary>
/// Enforces a plane boundary on a decoy hand using collider positions.
/// Prevents hand from passing through a Z-aligned plane.
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

    [Header("Debug")]
    public bool drawGizmos = true;
    public float gizmoSize = 0.05f;
    public Color planeColor = new Color(0.2f, 0.8f, 1f, 0.7f);

    void Awake()
    {
        if (!decoyHandRoot)
            Debug.LogWarning("[HandPlaneColliderBoundary] Decoy hand root not assigned.");

        if (!planeTransform)
            Debug.LogWarning("[HandPlaneColliderBoundary] Plane transform not assigned.");

        if (autoFindDecoyColliders && decoyHandRoot)
            decoyColliders = decoyHandRoot.GetComponentsInChildren<Collider>(true);
    }

    void LateUpdate()
    {
        if (!decoyHandRoot || !planeTransform || decoyColliders == null || decoyColliders.Length == 0)
            return;

        float planeZ   = planeTransform.position.z;
        float dirSign  = flipDirection ? -1f : 1f;  // Sign of normal direction
        float minDist  = Mathf.Max(0f, planeSkin + planeThickness);

        bool  havePenetration = false;
        float maxPenetration  = 0f;

        for (int i = 0; i < decoyColliders.Length; i++)
        {
            Collider col = decoyColliders[i];
            if (!col || !col.enabled) continue;

            Vector3 p = col.transform.position;

            // Signed distance along plane normal
            float dist = (p.z - planeZ) * dirSign;

            // Penetration if collider crossed plane
            float penetration = minDist - dist;

            if (penetration > 0f && penetration > maxPenetration)
            {
                maxPenetration  = penetration;
                havePenetration = true;
            }
        }

        if (!havePenetration || maxPenetration <= 0f)
            return;

        // Push decoy root out of blocked region
        Vector3 planeNormal = Vector3.forward * dirSign;
        Vector3 correction  = planeNormal * maxPenetration;
        decoyHandRoot.position += correction;
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
