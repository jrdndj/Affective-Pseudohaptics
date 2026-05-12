using UnityEngine;

public enum HandTelemetrySide
{
    Left = 0,
    Right = 1
}

[CreateAssetMenu(fileName = "HandTelemetryChannel", menuName = "Haptics/Hand Telemetry Channel", order = 0)]
public class HandTelemetryChannel : ScriptableObject
{
    [SerializeField, Tooltip("Publish counter (debug).")]
    long _sequence;

    HandTelemetrySnapshot _latest;

    public long Sequence => _sequence;
    public HandTelemetrySnapshot Latest => _latest;

    public void Publish(in HandTelemetrySnapshot snapshot)
    {
        _latest = snapshot;
        unchecked
        {
            _sequence++;
        }
    }
}
