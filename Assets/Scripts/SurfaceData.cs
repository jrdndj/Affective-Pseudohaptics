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
    public SurfaceType surfaceType = SurfaceType.Neutral;
}
