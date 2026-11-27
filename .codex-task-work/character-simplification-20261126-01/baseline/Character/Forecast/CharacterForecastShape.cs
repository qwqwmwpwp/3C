using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal readonly struct CharacterForecastShape
    {
        internal readonly Vector3 Center, Axis;
        internal readonly float Radius, Segment, Skin, GroundDot, StepHeight, FootRadius, FootHeight, FootSkin;

        internal CharacterForecastShape(Vector3 center, Vector3 axis, float radius, float segment, float skin,
            float groundDot, float stepHeight, float footRadius, float footHeight, float footSkin)
        {
            Center = center; Axis = axis; Radius = radius; Segment = segment; Skin = skin; GroundDot = groundDot;
            StepHeight = stepHeight; FootRadius = footRadius; FootHeight = footHeight; FootSkin = footSkin;
        }
    }

    internal readonly struct ForecastQuery : IEquatable<ForecastQuery>
    {
        internal readonly Vector3 A, B, Direction;
        internal readonly float Radius, Distance;
        internal bool Ray => Radius == 0f;

        internal ForecastQuery(Vector3 a, Vector3 b, float radius, Vector3 direction, float distance)
        {
            A = a; B = b; Radius = radius; Direction = direction; Distance = distance;
        }

        public bool Equals(ForecastQuery other) => A.Equals(other.A) && B.Equals(other.B) &&
            Direction.Equals(other.Direction) && Radius.Equals(other.Radius) && Distance.Equals(other.Distance);
        public override bool Equals(object other) => other is ForecastQuery query && Equals(query);
        public override int GetHashCode() => unchecked(((A.GetHashCode() * 397 ^ B.GetHashCode()) * 397 ^
            Direction.GetHashCode()) * 397 ^ Radius.GetHashCode() ^ Distance.GetHashCode());
    }

    /// <summary>从已确认命中重放步进。推测命中仅用于批量预取可能的后续查询，所有查询确认后才能发布。</summary>
    internal sealed class CharacterForecastProbePass : ICharacterForecastGround
    {
        private readonly CharacterForecastShape shape;
        private readonly CharacterForecastWorld world;
        internal readonly Dictionary<ForecastQuery, ForecastHit> Answers = new Dictionary<ForecastQuery, ForecastHit>();
        internal readonly List<ForecastQuery> Missing = new List<ForecastQuery>();
        private readonly Dictionary<ForecastQuery, ForecastHit> guesses = new Dictionary<ForecastQuery, ForecastHit>();
        private List<ForecastQuery> previous = new List<ForecastQuery>(), trace = new List<ForecastQuery>();
        private readonly ForecastCast sweep;
        private ForecastHit floor;

        internal CharacterForecastProbePass(CharacterForecastShape shape, CharacterForecastWorld world)
        {
            this.shape = shape;
            this.world = world;
            sweep = Cast;
        }

        internal void BeginPass()
        {
            Missing.Clear();
            guesses.Clear();
            var buffer = previous;
            previous = trace;
            trace = buffer;
            trace.Clear();
        }

        private ForecastHit Query(in ForecastQuery query)
        {
            int index = trace.Count;
            trace.Add(query);
            if (Answers.TryGetValue(query, out var hit))
            {
                if (hit.Found && !hit.Dynamic && !query.Ray && hit.Normal.y >= shape.GroundDot) floor = hit;
                return hit;
            }
            if (world.TryQuery(query, out hit)) return hit;
            if (guesses.TryGetValue(query, out hit)) return hit;
            Missing.Add(query);
            if (index < previous.Count && Nearby(previous[index], query) && Answers.TryGetValue(previous[index], out var prior))
                hit = prior;
            else hit = GuessPlane(query);
            guesses.Add(query, hit);
            return hit;
        }

        private static bool Nearby(in ForecastQuery a, in ForecastQuery b) => a.Radius == b.Radius &&
            (a.A - b.A).sqrMagnitude < 0.00000001f && (a.B - b.B).sqrMagnitude < 0.00000001f &&
            (a.Direction - b.Direction).sqrMagnitude < 0.00000001f && Mathf.Abs(a.Distance - b.Distance) < 0.0001f;

        // 对本轮每一次未命中缓存的查询，都用 PhysX 在实际查询位置的回包校验；
        // 20 微米内的接触误差低于此求解器最小胶囊间隙（1 毫米）。
        internal bool Confirmed()
        {
            foreach (var pair in guesses)
            {
                var guess = pair.Value;
                var actual = Answers[pair.Key];
                if (guess.Found != actual.Found || guess.Dynamic != actual.Dynamic || guess.Kinematic != actual.Kinematic) return false;
                if (actual.Found && (Mathf.Abs(guess.Distance - actual.Distance) > 0.00002f ||
                    (guess.Point - actual.Point).sqrMagnitude > 4e-10f ||
                    (guess.Normal - actual.Normal).sqrMagnitude > 1e-8f)) return false;
            }
            return true;
        }

        private ForecastHit GuessPlane(in ForecastQuery query)
        {
            if (query.Ray || !floor.Found) return default;
            float approach = -Vector3.Dot(query.Direction, floor.Normal);
            if (approach <= 0.0001f) return default;
            var end = Vector3.Dot(query.A - query.B, floor.Normal) <= 0f ? query.A : query.B;
            float distance = (Vector3.Dot(end - floor.Point, floor.Normal) - query.Radius) / approach;
            if (distance < 0f || distance > query.Distance) return default;
            return new ForecastHit(floor.Normal, end + query.Direction * distance - floor.Normal * query.Radius, distance);
        }

        private bool Cast(Vector3 position, Quaternion rotation, Vector3 direction, float distance, out ForecastHit hit)
        {
            Vector3 center = position + rotation * shape.Center;
            Vector3 span = rotation * shape.Axis * shape.Segment;
            hit = Query(new ForecastQuery(center - span, center + span, Mathf.Max(0.0001f, shape.Radius - shape.Skin), direction, distance));
            return hit.Found;
        }

        private ForecastHit Ray(Vector3 origin, Vector3 direction, float distance) =>
            Query(new ForecastQuery(origin, default, 0f, direction, distance));

        public bool TryStep(ref Vector3 position, Quaternion rotation, Vector3 displacement, out bool uncertain)
        {
            uncertain = false;
            if (shape.StepHeight <= 0f || displacement == Vector3.zero) return false;
            Vector3 foot = position + rotation * shape.Center - Vector3.up * shape.FootHeight;
            var probe = new CharacterStepProbePlan(foot, displacement, shape.FootRadius, shape.FootSkin, shape.StepHeight);
            var face = Ray(probe.FaceOrigin, probe.Direction, probe.FaceDistance);
            if (!face.Found || face.Normal.y >= shape.GroundDot) return false;
            uncertain = face.Dynamic;
            if (uncertain && !face.Kinematic) return false;
            var top = Ray(probe.TopOrigin(face.Distance), Vector3.down, probe.TopDistance);
            if (!top.Found || top.Normal.y < shape.GroundDot) return false;
            uncertain |= top.Dynamic;
            if (uncertain || !probe.TryLift(top.Point.y, out float lift) ||
                Cast(position, rotation, Vector3.up, lift, out _)) return false;
            position += Vector3.up * lift;
            return true;
        }

        public bool Move(ref Vector3 position, Quaternion rotation, ref Vector3 velocity, float dt,
            out CollisionFlags contacts, out Vector3 ground, out CharacterSlopeContacts steep) =>
            CharacterForecastSlide.Move(sweep, shape.Skin, shape.GroundDot, ref position, rotation, ref velocity, dt,
                out contacts, out ground, out steep);
    }
}
