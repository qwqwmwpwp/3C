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
        private readonly CharacterMovementForecast beforeChange = new CharacterMovementForecast();
        private readonly CharacterMovementForecast discarded = new CharacterMovementForecast();
        private CharacterMovementForecast source;
        private CharacterBehaviorDriver driver;
        private bool waitingForPath, marker;
        private double from, until, expires;
        private Vector3 markerPosition;
        private float teleportDistance;

        internal void Bind(CharacterBehaviorDriver next, float teleport)
        {
            teleportDistance = teleport;
            var forecast = next ? next.MovementForecast : null;
            if (ReferenceEquals(driver, next) && ReferenceEquals(source, forecast)) return;
            Dispose();
            driver = next;
            source = forecast;
            if (source == null) return;
            previous.CopyPublishedFrom(source);
            source.Revised += Changed;
            source.FrameSubmitted += ObserveBehaviorFrame;
        }

        // 以角色可暂停时钟同步预测变更与可见轨迹。
        private void Changed(CharacterForecastRevision revision)
        {
            double now = driver.CharacterTime;
            switch (revision)
            {
                case CharacterForecastRevision.Reset:
                    Reset();
                    return;
                case CharacterForecastRevision.Takeover:
                case CharacterForecastRevision.Withdraw:
                    if (previous.Available && Mark(previous, now, now))
                    {
                        waitingForPath = revision == CharacterForecastRevision.Takeover;
                        marker = !HasTravel(discarded, from, until);
                    }
                    beforeChange.Clear();
                    previous.Clear();
                    return;
                case CharacterForecastRevision.Published:
                    Published(now);
                    return;
                default:
                    // 输入和物理纠偏只更新青线。普通失效期间保留对照样本，等待真实行为裁决。
                    return;
            }
        }

        private void Published(double now)
        {
            double t = source.StartTime;
            if (previous.TrySample(t, 0f, out var old) && source.TrySample(t, 0f, out var next) &&
                Teleported(old.Position, next.Position)) Reset();

            if (waitingForPath)
            {
                if (now < expires) FindTail(source);
                waitingForPath = false;
            }
            else
            {
                // 新预测可能先于下一次动作边界发布。只暂存会改变行为的旧预测，不在发布时显示打断。
                if (beforeChange.Available && !FirstBehaviorChange(beforeChange, source, t, out _)) beforeChange.Clear();
                if (!beforeChange.Available && FirstBehaviorChange(previous, source, t, out _))
                    beforeChange.CopyPublishedFrom(previous);
            }
            previous.CopyPublishedFrom(source);
        }

        // 帧末依序核对本渲染帧的所有许可变更，包括没有物理步的帧和同帧的接续与打断。
        private void ObserveBehaviorFrame()
        {
            var updates = driver.BehaviorUpdates;
            double time = driver.CharacterTime;
            for (int i = 0; i < updates.Count; i++) time -= updates[i].DeltaTime;
            for (int i = 0; i < updates.Count; i++)
            {
                var frame = updates[i];
                time += frame.DeltaTime;
                if (frame.SimulationVersion == driver.SimulationVersion && frame.Behavior.HasTransitions)
                    ObserveTransition(frame.Behavior, time);
            }
        }

        // 只有已发生且偏离旧预测的真实转换才替换轨迹尾段。
        private void ObserveTransition(in CharacterBehaviorFrameResult behavior, double t)
        {
            var old = beforeChange.TrySample(t, 0f, out var expected) ? beforeChange : previous;
            if (!old.TrySample(t, 0f, out expected)) return;
            if (Teleported(expected.Position, driver.transform.position)) { Reset(); return; }
            var actual = new CharacterForecastSample(driver.transform.position, Vector3.zero, Quaternion.identity,
                behavior, driver.LatestFrame.CollisionFlags, driver.FrameInput.Gait);
            bool interrupted = behavior.InterruptedBehaviors != CharacterBehaviorId.None;
            if (!interrupted && SameBehavior(expected, actual)) return; // 已经预期的自然结束或恢复移动。
            if (!Mark(old, t, driver.CharacterTime)) return;
            beforeChange.Clear();
            waitingForPath = !source.TrySample(t, 0f, out var projected) || !SameBehavior(actual, projected);
            if (!waitingForPath) FindTail(source);
        }

        private bool Teleported(Vector3 a, Vector3 b) =>
            teleportDistance > 0f && (a - b).sqrMagnitude >= teleportDistance * teleportDistance;

        // 移动系统在待机时也持有 Move；普通移动以实际许可为准。攻击阶段的自然推进不切换行为。
        private static bool SameBehavior(in CharacterForecastSample a, in CharacterForecastSample b) =>
            (a.Behaviors & ~CharacterBehaviorId.Move) == (b.Behaviors & ~CharacterBehaviorId.Move) &&
            a.MoveApproved == b.MoveApproved && a.AttackStateId == b.AttackStateId && a.AttackInstance == b.AttackInstance &&
            a.ExternallyControlled == b.ExternallyControlled;

        private static bool FirstBehaviorChange(CharacterMovementForecast old, CharacterMovementForecast next,
            double now, out double time)
        {
            time = now;
            if (old == null || next == null || !old.Available || !next.Available) return false;
            double start = Math.Max(now, Math.Max(old.StartTime, next.StartTime));
            double end = Math.Min(End(old, now), End(next, now));
            double step = Math.Min(old.SampleInterval, next.SampleInterval);
            for (time = start; time <= end; time = Math.Min(end, time + step))
            {
                if (old.TrySample(time, 0f, out var a) && next.TrySample(time, 0f, out var b) && !SameBehavior(a, b))
                    return true;
                if (time == end) break;
            }
            return false;
        }

        internal static double End(CharacterMovementForecast path, double now) =>
            Math.Min(now + path.Horizon, path.StartTime + path.ValidSeconds);

        internal void CompareRehearsal(CharacterMovementForecast old, CharacterMovementForecast next, double now, double clock)
        {
            if (FirstBehaviorChange(old, next, now, out double t) && Mark(old, t, clock)) FindTail(next);
        }

        private bool Mark(CharacterMovementForecast old, double time, double clock)
        {
            if (!old.TrySample(time, 0f, out var pose)) return false;
            discarded.CopyPublishedFrom(old);
            from = time;
            until = End(old, time);
            markerPosition = pose.Position;
            marker = true; // 真实行为已切换，路线仍重合时保留断点。
            expires = clock + Lifetime;
            return true;
        }

        // 只有已经确认的切换能进入这里；几何差异只决定画断点还是虚线。
        private void FindTail(CharacterMovementForecast next)
        {
            marker = true;
            double start = Math.Max(from, next.StartTime);
            double end = Math.Min(until, End(next, start));
            double step = Math.Min(discarded.SampleInterval, next.SampleInterval);
            double last = start;
            for (double t = start; t <= end; t = Math.Min(end, t + step))
            {
                if (!discarded.TrySample(t, 0f, out var a) || !next.TrySample(t, 0f, out var b)) break;
                if ((a.Position - b.Position).sqrMagnitude > 0.03f * 0.03f)
                {
                    from = last;
                    marker = !HasTravel(discarded, from, until);
                    return;
                }
                if (t == end) break;
                last = t;
            }
            if (next.EnvironmentLimited && end >= start && until > end + 0.001)
            {
                from = end;
                marker = !HasTravel(discarded, from, until);
            }
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
            beforeChange.Clear();
            waitingForPath = false;
            expires = 0d;
        }

        public void Dispose()
        {
            if (source != null)
            {
                source.Revised -= Changed;
                source.FrameSubmitted -= ObserveBehaviorFrame;
            }
            driver = null;
            source = null;
            Reset();
        }
    }
}
