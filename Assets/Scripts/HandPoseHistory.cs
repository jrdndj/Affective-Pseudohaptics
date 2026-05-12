using UnityEngine;

public class HandPoseHistory
{
    struct PoseSample
    {
        public Vector3 pos;
        public Quaternion rot;
        public float time;
    }

    const int PoseCap = 128;

    readonly PoseSample[] _poseBuf = new PoseSample[PoseCap];
    int _poseCount;
    Vector3 _lastPosition;
    Vector3 _velocity;

    public Vector3 Position { get; private set; }
    public Quaternion Rotation { get; private set; }
    public Vector3 Velocity => _velocity;

    public void Initialize(Transform source)
    {
        Position = source.position;
        Rotation = source.rotation;
        _lastPosition = Position;
        _velocity = Vector3.zero;
        _poseCount = 0;
    }

    public void Tick(Transform source, float velocityLowpassHz, float dt, float now)
    {
        Position = source.position;
        Rotation = source.rotation;

        Vector3 instVel = (Position - _lastPosition) / dt;
        _lastPosition = Position;

        if (velocityLowpassHz > 0f)
        {
            float a = 1f - Mathf.Exp(-2f * Mathf.PI * velocityLowpassHz * dt);
            _velocity = Vector3.Lerp(_velocity, instVel, a);
        }
        else
        {
            _velocity = instVel;
        }

        PushPose(new PoseSample { pos = Position, rot = Rotation, time = now });
    }

    public void SampleDelayedPose(float tTarget, out Vector3 pos, out Quaternion rot)
    {
        if (_poseCount == 0)
        {
            pos = Position;
            rot = Rotation;
            return;
        }

        PoseSample newest = _poseBuf[_poseCount - 1];
        if (tTarget >= newest.time)
        {
            pos = newest.pos;
            rot = newest.rot;
            return;
        }

        PoseSample a = newest, b = newest;
        for (int i = _poseCount - 2; i >= 0; i--)
        {
            b = _poseBuf[i];
            if (b.time <= tTarget)
            {
                a = _poseBuf[i + 1];
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

    void PushPose(PoseSample sample)
    {
        if (_poseCount < PoseCap)
        {
            _poseBuf[_poseCount++] = sample;
            return;
        }

        for (int i = 1; i < PoseCap; i++)
            _poseBuf[i - 1] = _poseBuf[i];
        _poseBuf[PoseCap - 1] = sample;
    }
}
