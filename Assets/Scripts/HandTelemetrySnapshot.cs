using System;
using UnityEngine;

public readonly struct HandTelemetrySnapshot : IEquatable<HandTelemetrySnapshot>
{
    public readonly bool IsTouching;
    public readonly bool IsTouchingRaw;
    public readonly bool IsSliding;
    public readonly float ContactEnvelope01;
    public readonly float TangentialSpeed;
    public readonly Vector3 TangentialVelocity;
    public readonly Vector3 ContactNormal;
    public readonly Vector3 ContactPoint;
    public readonly float ContactCoverage01;
    public readonly float CurrentRoughness01;
    public readonly SurfaceType SurfaceType;
    public readonly SurfaceData Surface;

    public HandTelemetrySnapshot(
        bool isTouching,
        bool isTouchingRaw,
        bool isSliding,
        float contactEnvelope01,
        float tangentialSpeed,
        in Vector3 tangentialVelocity,
        in Vector3 contactNormal,
        in Vector3 contactPoint,
        float contactCoverage01,
        float currentRoughness01,
        SurfaceType surfaceType,
        SurfaceData surface)
    {
        IsTouching = isTouching;
        IsTouchingRaw = isTouchingRaw;
        IsSliding = isSliding;
        ContactEnvelope01 = contactEnvelope01;
        TangentialSpeed = tangentialSpeed;
        TangentialVelocity = tangentialVelocity;
        ContactNormal = contactNormal;
        ContactPoint = contactPoint;
        ContactCoverage01 = contactCoverage01;
        CurrentRoughness01 = currentRoughness01;
        SurfaceType = surfaceType;
        Surface = surface;
    }

    public static HandTelemetrySnapshot FromContact(in HandContactState contact)
    {
        return new HandTelemetrySnapshot(
            contact.Touching,
            contact.TouchingRaw,
            contact.Sliding,
            Mathf.Clamp01(contact.ContactEnvelope),
            contact.TangentialSpeed,
            contact.TangentialVelocity,
            contact.ContactNormal,
            contact.ContactPoint,
            contact.ContactCoverage01,
            contact.Roughness01,
            contact.SurfaceType,
            contact.Surface
        );
    }

    public static HandTelemetrySnapshot FromHandTextureDriver(HandTextureDriver d)
    {
        if (!d)
            return default;

        SurfaceType st = SurfaceType.Neutral;
        SurfaceData surf = d.CurrentSurface;
        if (surf)
            st = surf.surfaceType;

        return new HandTelemetrySnapshot(
            d.IsTouching,
            d.IsTouchingRaw,
            d.IsSliding,
            Mathf.Clamp01(d.ContactEnvelope01),
            d.TangentialSpeed,
            d.TangentialVelocity,
            d.ContactNormal,
            d.ContactPoint,
            d.ContactCoverage01,
            d.CurrentRoughness01,
            st,
            surf
        );
    }

    static int TextureSurfacePriority(SurfaceType t)
    {
        switch (t)
        {
            case SurfaceType.Rough: return 2;
            case SurfaceType.Smooth: return 1;
            default: return 0;
        }
    }

    static int ThermalSurfacePriority(SurfaceType t)
    {
        switch (t)
        {
            case SurfaceType.Hot: return 2;
            case SurfaceType.Cold: return 1;
            default: return 0;
        }
    }

    // Pick dominant rough/smooth hand when both touch; speed from faster hand.
    public static HandTelemetrySnapshot MergeTexturePriority(HandTelemetryChannel[] channels)
    {
        if (channels == null || channels.Length == 0)
            return default;

        bool anyTouch = false;
        bool anyTouchRaw = false;
        bool anySlide = false;
        float maxSpd = 0f;
        Vector3 maxVel = Vector3.zero;
        int bestPriority = -1;
        HandTelemetrySnapshot pick = default;

        for (int i = 0; i < channels.Length; i++)
        {
            var ch = channels[i];
            if (!ch) continue;
            HandTelemetrySnapshot s = ch.Latest;
            anyTouch |= s.IsTouching;
            anyTouchRaw |= s.IsTouchingRaw;
            anySlide |= s.IsSliding;
            if (s.TangentialSpeed >= maxSpd)
            {
                maxSpd = s.TangentialSpeed;
                maxVel = s.TangentialVelocity;
            }

            if (s.IsTouching && s.Surface)
            {
                int p = TextureSurfacePriority(s.SurfaceType);
                if (p > bestPriority)
                {
                    bestPriority = p;
                    pick = s;
                }
            }
        }

        if (bestPriority < 0)
        {
            return new HandTelemetrySnapshot(
                anyTouch,
                anyTouchRaw,
                anySlide,
                0f,
                maxSpd,
                maxVel,
                Vector3.up,
                Vector3.zero,
                0f,
                0.5f,
                SurfaceType.Neutral,
                null
            );
        }

        return new HandTelemetrySnapshot(
            anyTouch,
            anyTouchRaw,
            anySlide,
            pick.ContactEnvelope01,
            maxSpd,
            maxVel,
            pick.ContactNormal,
            pick.ContactPoint,
            pick.ContactCoverage01,
            pick.CurrentRoughness01,
            pick.SurfaceType,
            pick.Surface
        );
    }

    // Pick dominant hot/cold hand when both touch; speed from faster hand.
    public static HandTelemetrySnapshot MergeThermalPriority(HandTelemetryChannel[] channels)
    {
        if (channels == null || channels.Length == 0)
            return default;

        bool anyTouch = false;
        bool anyTouchRaw = false;
        bool anySlide = false;
        float maxSpd = 0f;
        Vector3 maxVel = Vector3.zero;
        int bestPriority = -1;
        HandTelemetrySnapshot pick = default;

        for (int i = 0; i < channels.Length; i++)
        {
            var ch = channels[i];
            if (!ch) continue;
            HandTelemetrySnapshot s = ch.Latest;
            anyTouch |= s.IsTouching;
            anyTouchRaw |= s.IsTouchingRaw;
            anySlide |= s.IsSliding;
            if (s.TangentialSpeed >= maxSpd)
            {
                maxSpd = s.TangentialSpeed;
                maxVel = s.TangentialVelocity;
            }

            if (s.IsTouching && s.Surface)
            {
                int p = ThermalSurfacePriority(s.SurfaceType);
                if (p > bestPriority)
                {
                    bestPriority = p;
                    pick = s;
                }
            }
        }

        if (bestPriority < 0)
        {
            return new HandTelemetrySnapshot(
                anyTouch,
                anyTouchRaw,
                anySlide,
                0f,
                maxSpd,
                maxVel,
                Vector3.up,
                Vector3.zero,
                0f,
                0.5f,
                SurfaceType.Neutral,
                null
            );
        }

        return new HandTelemetrySnapshot(
            anyTouch,
            anyTouchRaw,
            anySlide,
            pick.ContactEnvelope01,
            maxSpd,
            maxVel,
            pick.ContactNormal,
            pick.ContactPoint,
            pick.ContactCoverage01,
            pick.CurrentRoughness01,
            pick.SurfaceType,
            pick.Surface
        );
    }

    public static HandTelemetrySnapshot MergeTexturePriority(HandTextureDriver[] drivers)
    {
        if (drivers == null || drivers.Length == 0)
            return default;

        bool anyTouch = false;
        bool anyTouchRaw = false;
        bool anySlide = false;
        float maxSpd = 0f;
        Vector3 maxVel = Vector3.zero;
        int bestPriority = -1;
        HandTextureDriver pickDriver = null;

        for (int i = 0; i < drivers.Length; i++)
        {
            var d = drivers[i];
            if (!d) continue;
            anyTouch |= d.IsTouching;
            anyTouchRaw |= d.IsTouchingRaw;
            anySlide |= d.IsSliding;
            if (d.TangentialSpeed >= maxSpd)
            {
                maxSpd = d.TangentialSpeed;
                maxVel = d.TangentialVelocity;
            }

            if (d.IsTouching && d.CurrentSurface)
            {
                int p = TextureSurfacePriority(d.CurrentSurface.surfaceType);
                if (p > bestPriority)
                {
                    bestPriority = p;
                    pickDriver = d;
                }
            }
        }

        if (!pickDriver)
        {
            return new HandTelemetrySnapshot(
                anyTouch,
                anyTouchRaw,
                anySlide,
                0f,
                maxSpd,
                maxVel,
                Vector3.up,
                Vector3.zero,
                0f,
                0.5f,
                SurfaceType.Neutral,
                null
            );
        }

        SurfaceType st = pickDriver.CurrentSurface ? pickDriver.CurrentSurface.surfaceType : SurfaceType.Neutral;
        return new HandTelemetrySnapshot(
            anyTouch,
            anyTouchRaw,
            anySlide,
            Mathf.Clamp01(pickDriver.ContactEnvelope01),
            maxSpd,
            maxVel,
            pickDriver.ContactNormal,
            pickDriver.ContactPoint,
            pickDriver.ContactCoverage01,
            pickDriver.CurrentRoughness01,
            st,
            pickDriver.CurrentSurface
        );
    }

    public static HandTelemetrySnapshot MergeThermalPriority(HandTextureDriver[] drivers)
    {
        if (drivers == null || drivers.Length == 0)
            return default;

        bool anyTouch = false;
        bool anyTouchRaw = false;
        bool anySlide = false;
        float maxSpd = 0f;
        Vector3 maxVel = Vector3.zero;
        int bestPriority = -1;
        HandTextureDriver pickDriver = null;

        for (int i = 0; i < drivers.Length; i++)
        {
            var d = drivers[i];
            if (!d) continue;
            anyTouch |= d.IsTouching;
            anyTouchRaw |= d.IsTouchingRaw;
            anySlide |= d.IsSliding;
            if (d.TangentialSpeed >= maxSpd)
            {
                maxSpd = d.TangentialSpeed;
                maxVel = d.TangentialVelocity;
            }

            if (d.IsTouching && d.CurrentSurface)
            {
                int p = ThermalSurfacePriority(d.CurrentSurface.surfaceType);
                if (p > bestPriority)
                {
                    bestPriority = p;
                    pickDriver = d;
                }
            }
        }

        if (!pickDriver)
        {
            return new HandTelemetrySnapshot(
                anyTouch,
                anyTouchRaw,
                anySlide,
                0f,
                maxSpd,
                maxVel,
                Vector3.up,
                Vector3.zero,
                0f,
                0.5f,
                SurfaceType.Neutral,
                null
            );
        }

        SurfaceType st = pickDriver.CurrentSurface ? pickDriver.CurrentSurface.surfaceType : SurfaceType.Neutral;
        return new HandTelemetrySnapshot(
            anyTouch,
            anyTouchRaw,
            anySlide,
            Mathf.Clamp01(pickDriver.ContactEnvelope01),
            maxSpd,
            maxVel,
            pickDriver.ContactNormal,
            pickDriver.ContactPoint,
            pickDriver.ContactCoverage01,
            pickDriver.CurrentRoughness01,
            st,
            pickDriver.CurrentSurface
        );
    }

    public bool Equals(HandTelemetrySnapshot other)
    {
        return IsTouching == other.IsTouching
               && IsTouchingRaw == other.IsTouchingRaw
               && IsSliding == other.IsSliding
               && TangentialSpeed.Equals(other.TangentialSpeed)
               && TangentialVelocity.Equals(other.TangentialVelocity)
               && ContactNormal.Equals(other.ContactNormal)
               && ContactPoint.Equals(other.ContactPoint)
               && ContactCoverage01.Equals(other.ContactCoverage01)
               && CurrentRoughness01.Equals(other.CurrentRoughness01)
               && SurfaceType == other.SurfaceType
               && Surface == other.Surface;
    }

    public override bool Equals(object obj) => obj is HandTelemetrySnapshot other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hashCode = IsTouching.GetHashCode();
            hashCode = (hashCode * 397) ^ IsTouchingRaw.GetHashCode();
            hashCode = (hashCode * 397) ^ IsSliding.GetHashCode();
            hashCode = (hashCode * 397) ^ TangentialSpeed.GetHashCode();
            hashCode = (hashCode * 397) ^ TangentialVelocity.GetHashCode();
            hashCode = (hashCode * 397) ^ ContactNormal.GetHashCode();
            hashCode = (hashCode * 397) ^ ContactPoint.GetHashCode();
            hashCode = (hashCode * 397) ^ ContactCoverage01.GetHashCode();
            hashCode = (hashCode * 397) ^ CurrentRoughness01.GetHashCode();
            hashCode = (hashCode * 397) ^ (int)SurfaceType;
            hashCode = (hashCode * 397) ^ (Surface != null ? Surface.GetHashCode() : 0);
            return hashCode;
        }
    }
}
