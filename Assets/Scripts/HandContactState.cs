using UnityEngine;

public struct HandContactState
{
    public bool TouchingRaw;
    public bool Touching;
    public bool Sliding;

    public float ContactEnvelope;
    public float ContactCoverage01;
    public float Roughness01;

    public Vector3 ContactPoint;
    public Vector3 ContactNormal;
    public Vector3 TangentialVelocity;
    public float TangentialSpeed;

    public SurfaceData Surface;
    public SurfaceType SurfaceType;
}
