using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal interface ICharacterForecastGround
    {
        bool TryStep(ref Vector3 position, Quaternion rotation, Vector3 displacement, out bool uncertain);
        bool Move(ref Vector3 position, Quaternion rotation, ref Vector3 velocity, float dt,
            out CollisionFlags contacts, out Vector3 ground, out CharacterSlopeContacts steep);
    }

    internal readonly struct ForecastHit
    {
        internal readonly bool Found, Dynamic, Kinematic;
        internal readonly Vector3 Normal, Point;
        internal readonly float Distance;
        internal ForecastHit(RaycastHit hit)
        {
            Found = true;
            Normal = hit.normal;
            Point = hit.point;
            Distance = hit.distance;
            var body = hit.rigidbody;
            Dynamic = body != null;
            Kinematic = Dynamic && body.isKinematic;
        }

        internal ForecastHit(Vector3 normal, Vector3 point, float distance)
        {
            Found = true;
            Normal = normal;
            Point = point;
            Distance = distance;
            Dynamic = Kinematic = false;
        }
    }

    internal delegate bool ForecastCast(Vector3 position, Quaternion rotation, Vector3 direction, float distance, out ForecastHit hit);

    internal static class CharacterForecastSlide
    {
        internal static bool Move(ForecastCast Cast, float skin, float groundDot, ref Vector3 position, Quaternion rotation,
            ref Vector3 velocity, float dt, out CollisionFlags contacts, out Vector3 ground, out CharacterSlopeContacts steep)
        {
            contacts = CollisionFlags.None;
            ground = Vector3.zero;
            steep = default;
            Vector3 remaining = velocity * dt;
            for (int i = 0; i < 3 && remaining.sqrMagnitude > 0.000000001f; i++)
            {
                Vector3 direction = remaining.normalized;
                if (!Cast(position, rotation, direction, remaining.magnitude + skin, out var hit))
                {
                    position += remaining;
                    break;
                }
                if (hit.Dynamic) return false;
                float travel = Mathf.Clamp(hit.Distance - skin, 0f, remaining.magnitude);
                position += direction * travel;
                remaining = Vector3.ProjectOnPlane(remaining - direction * travel, hit.Normal);
                velocity -= hit.Normal * Mathf.Min(0f, Vector3.Dot(velocity, hit.Normal));
                if (hit.Normal.y >= groundDot) { contacts |= CollisionFlags.Below; ground = hit.Normal; }
                else if (hit.Normal.y < -0.5f) contacts |= CollisionFlags.Above;
                else
                {
                    contacts |= CollisionFlags.Sides;
                    steep.Add(hit.Normal);
                }
            }
            if (Cast(position + Vector3.up * skin, rotation, Vector3.down, skin * 4f, out var floor)
                && floor.Normal.y >= groundDot && Vector3.Dot(velocity, floor.Normal) < 0.5f)
            {
                if (floor.Dynamic) return false;
                contacts |= CollisionFlags.Below;
                ground = floor.Normal;
                position += Vector3.up * (2f * skin - floor.Distance);
                velocity -= ground * Mathf.Min(0f, Vector3.Dot(velocity, ground));
            }
            return true;
        }
    }
}
