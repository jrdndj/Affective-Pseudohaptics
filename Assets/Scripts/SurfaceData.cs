using UnityEngine;

public enum SurfaceType
{
    Hot,
    Cold,
    Rough,
    Smooth,
    Neutral
}

[DisallowMultipleComponent]
public class SurfaceData : MonoBehaviour
{
    // Tag geometry the hand probes against (Hot/Cold/Rough/Smooth/Neutral).
    public SurfaceType surfaceType = SurfaceType.Neutral;
}
