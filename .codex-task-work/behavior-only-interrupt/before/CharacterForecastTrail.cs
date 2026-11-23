using System;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>仅保留所选角色上一版的有效样本和最近一次被替换的尾段。比较采用同一模拟时钟。</summary>
    internal sealed class CharacterForecastTrail : IDisposable
    {
        internal static readonly Color Normal = new Color(0.08f, 0.87f, 0.87f);
        private static readonly Color Interrupted = new Color(1f, 0.48f, 0.13f);
        private const double Lifetime = 0.6;
        private readonly CharacterMovementForecast previous = new CharacterMovementForecast();
        private readonly CharacterMovementForecast discarded = new CharacterMovementForecast();
        private CharacterMovementForecast source;
        private bool pending, marker;
        private double from, until, expires;
        private Vector3 markerPosition;
        private float teleportDistance;

        internal void Bind(CharacterMovementForecast next, float teleport)
        {
            teleportDistance = teleport;
            if (ReferenceEquals(source, next)) return;
            Dispose();
            source = next;
            if (next == null) return;
            previous.CopyPublishedFrom(next);
            next.Revised += Changed;
        }

        private void Changed(CharacterForecastRevision revision)
        {
            double now = Time.timeAsDouble;
            if (revision == CharacterForecastRevision.Reset) { Reset(); return; }
            if (!source.Available)
            {
                if (revision == CharacterForecastRevision.Withdraw)
                {
                    if (previous.Available) Withdraw(now);
                    previous.Clear();
                    pending = false;
                }
                else pending |= revision == CharacterForecastRevision.Replace;
                return;
            }
            if (pending || revision == CharacterForecastRevision.Replace ||
                source.EnvironmentLimited && End(source, now) + 0.001 < End(previous, now))
                Compare(previous, source, now, now);
            previous.CopyPublishedFrom(source);
            pending = false;
        }

        private void Withdraw(double now)
        {
            if (!previous.TrySample(now, 0f, out var pose)) return;
            discarded.CopyPublishedFrom(previous);
            from = now;
            until = End(previous, now);
            markerPosition = pose.Position;
            marker = !HasTravel(previous, from, until);
            expires = now + Lifetime;
        }

        internal static double End(CharacterMovementForecast path, double now) =>
            Math.Min(now + path.Horizon, path.StartTime + path.ValidSeconds);

        internal void Compare(CharacterMovementForecast old, CharacterMovementForecast next, double now, double displayClock)
        {
            if (old == null || !old.Available || next == null || !next.Available) return;
            double start = Math.Max(now, Math.Max(old.StartTime, next.StartTime));
            double oldEnd = End(old, now), newEnd = End(next, now);
            if (oldEnd < start || !old.TrySample(start, 0f, out var a) || !next.TrySample(start, 0f, out var b)) return;
            if (teleportDistance > 0 && Vector3.Distance(a.Position, b.Position) >= teleportDistance) { Reset(); return; }
            double overlap = Math.Min(oldEnd, newEnd);
            float dt = Mathf.Max(0.001f, Mathf.Min(old.SampleInterval, next.SampleInterval));
            double poseChange = -1d, pathChange = -1d, last = start;
            for (int i = 0; ; i++)
            {
                double t = Math.Min(overlap, start + i * (double)dt);
                if (!old.TrySample(t, 0f, out a) || !next.TrySample(t, 0f, out b)) break;
                if ((a.Position - b.Position).sqrMagnitude > 0.03f * 0.03f) { pathChange = last; break; }
                if (poseChange < 0 && (a.Behaviors != b.Behaviors || a.Attack != b.Attack ||
                    a.AttackInstance != b.AttackInstance || a.AttackPhase != b.AttackPhase ||
                    a.ExternallyControlled != b.ExternallyControlled || a.MoveApproved != b.MoveApproved ||
                    a.MoveApproved && a.Gait != b.Gait || Quaternion.Angle(a.Rotation, b.Rotation) > 8f))
                    poseChange = t;
                if (t >= overlap) break;
                last = t;
            }
            if (pathChange < 0 && next.EnvironmentLimited && oldEnd > newEnd + 0.001) pathChange = newEnd;
            if (pathChange < 0 && poseChange < 0) return;
            discarded.CopyPublishedFrom(old);
            from = pathChange >= 0 ? pathChange : poseChange;
            until = oldEnd;
            if (!old.TrySample(from, 0f, out a)) return;
            markerPosition = a.Position;
            marker = pathChange < 0 || !HasTravel(old, from, until);
            expires = displayClock + Lifetime;
        }

        private static bool HasTravel(CharacterMovementForecast path, double start, double end)
        {
            if (!path.TrySample(start, 0f, out var first)) return false;
            for (double t = start; t < end;)
            {
                t = Math.Min(end, t + path.SampleInterval);
                if (path.TrySample(t, 0f, out var next) && (first.Position - next.Position).sqrMagnitude > 0.0001f) return true;
            }
            return false;
        }

        internal void Reveal(double clock)
        {
            if (discarded.Available) expires = clock + Lifetime;
        }

        internal void Draw(CharacterMovementForecast path, double now, double end, double displayClock,
            double displayLimit, Vector3? currentPosition)
        {
            using (new Handles.DrawingScope(Normal))
            {
                if (displayClock < expires && discarded.Available && from <= displayLimit)
                {
                    Color color = Interrupted;
                    color.a = Mathf.Clamp01((float)((expires - displayClock) / Lifetime));
                    Handles.color = color;
                    if (marker) Handles.DrawWireDisc(markerPosition, Vector3.up,
                        Mathf.Clamp(HandleUtility.GetHandleSize(markerPosition) * 0.055f, 0.045f, 0.15f));
                    else DrawLine(discarded, from, Math.Min(until, displayLimit), true, null);
                }
                Handles.color = Normal;
                if (path != null && path.Available) DrawLine(path, now, end, false, currentPosition);
            }
        }

        private static void DrawLine(CharacterMovementForecast path, double start, double end, bool dashed, Vector3? currentPosition)
        {
            if (!path.TrySample(start, 0f, out var first)) return;
            Vector3 last = currentPosition ?? first.Position;
            float dashPhase = 0f;
            for (double t = start; t < end;)
            {
                t = Math.Min(end, t + path.SampleInterval);
                if (!path.TrySample(t, 0f, out var sample)) break;
                Vector3 next = sample.Position;
                if (dashed) DrawDashes(last, next, ref dashPhase);
                else if (next != last) Handles.DrawLine(last, next, 2f);
                last = next;
            }
        }

        // 跨采样段累计虚线相位，低速或高采样率不会把橙色虚线误画成实线。
        private static void DrawDashes(Vector3 a, Vector3 b, ref float phase)
        {
            float length = Vector3.Distance(a, b);
            if (length < 0.00001f) return;
            const float dash = 0.09f, period = 0.15f;
            Vector3 axis = (b - a) / length;
            for (float offset = -phase; offset < length; offset += period)
            {
                float left = Mathf.Max(0f, offset), right = Mathf.Min(length, offset + dash);
                if (right > left) Handles.DrawLine(a + axis * left, a + axis * right, 2f);
            }
            phase = Mathf.Repeat(phase + length, period);
        }

        internal void Reset()
        {
            previous.Clear();
            discarded.Clear();
            pending = false;
            expires = 0d;
        }

        public void Dispose()
        {
            if (source != null) source.Revised -= Changed;
            source = null;
            Reset();
        }
    }
}
